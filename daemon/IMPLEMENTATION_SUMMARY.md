# C# Resource Monitor Daemon Summary

## Current Role

The daemon is the Windows-side metrics client for the ESP32 CPU/MEM monitor.
It samples local CPU and memory usage (once per second by default) and
publishes values to the MQTT broker hosted by the ESP32.

The active communication contract is documented in `../COMMUNICATION.md`.

## Implemented Behavior

### Metrics

- CPU usage is sampled with `PerformanceCounter("Processor", "% Processor Time", "_Total")`.
- Memory usage is sampled through `NativeMemory.GetMemoryStatus()`.
- Each sample is logged at **Debug** level (CPU percentage plus RAM used/total),
  so the default Information-level log does not grow by one line per second.
- When sending is paused, sampling continues but connecting and MQTT publishing are skipped.

### MQTT Publishing (contract unchanged)

- Broker host/port, topics and sampling interval come from `DeviceSettings`
  (defaults: `resmon.local:1883`, `resmon/cpu`, `resmon/mem`, 1000 ms).
- Payload format: invariant-culture ASCII decimal percentage with one decimal place, for example `42.3`.
- Payload bounds: values are clamped to `0.0..100.0` before publishing.
- QoS: 0 (at most once), not retained.

`MetricPayloadFormatter` owns the payload formatting rule so it can be tested
without a live broker.

### Settings

`DeviceSettings` (configuration section `Device`) holds non-secret values only;
it has no Wi-Fi credential fields.

| Setting | Default | Validation / fallback |
| --- | --- | --- |
| `Host` | `resmon.local` | host name or IP; invalid → default |
| `FallbackAddress` | empty | dotted IPv4 or empty; invalid → empty (disabled) |
| `Port` | `1883` | 1..65535; invalid → default |
| `CpuTopic` | `resmon/cpu` | non-empty, ≤128 chars, no `#`/`+`; invalid → default |
| `MemTopic` | `resmon/mem` | as above, must differ from `CpuTopic` |
| `StatusTopic` | `resmon/status` | as above, must differ from both metric topics (subscribed, see below) |
| `SampleIntervalMs` | `1000` | clamped to 250..60000 |
| `ReconnectInitialBackoffMs` | `1000` | clamped to 250..600000 |
| `ReconnectMaxBackoffMs` | `30000` | clamped to 250..600000, ≥ initial |
| `ConnectTimeoutMs` | `3000` | clamped to 500..30000 |

Sources, lowest to highest priority:

1. built-in defaults (`DeviceSettings`);
2. `appsettings.json` next to the executable (copied to the build output, non-secret);
3. user settings `%LocalAppData%\ResourceMonitorDaemon\settings.json` (written by the tray dialog and the future pairing wizard; contains **only the fields explicitly set there**, so values not set keep coming from `appsettings.json`/defaults);
4. environment variables prefixed `RESMON_`, e.g. `RESMON_Device__Host=192.168.1.50`.

Invalid values never crash the daemon: `DeviceSettingsLoader` parses
leniently and `DeviceSettingsValidator.Sanitize` replaces bad values with a
logged warning. A JSON file that cannot be parsed is ignored (its layer
contributes nothing) and reported in the log.

Changes apply without restart: both JSON files are watched
(`reloadOnChange`, typically picked up within half a second) and
`MonitorService` consumes `IOptionsMonitor<DeviceSettings>`. Topic and interval
changes take effect on the next tick; a host, fallback or port change drops the
current connection and reconnects. A single save can raise several watcher
reloads; identical settings are ignored and identical sanitize warnings are
logged only once.

### Settings API for other features (pairing wizard)

The user file is a patch (`UserDeviceSettingsPatch`, every property nullable;
`null` = not set here). There is one way to change it:

```csharp
// Resolve ISettingsStore (UserSettingsStore) from DI.
settingsStore.Update(patch => patch.FallbackAddress = "192.168.1.50");
// Set a property to null to remove it from the file again.
```

- `ISettingsStore.Update(Action<UserDeviceSettingsPatch>)` — loads the current
  patch, applies the edit, validates the set fields
  (`UserDeviceSettingsPatch.Validate`, throws `ArgumentException`), and writes
  atomically (`settings.json.tmp` then replace; `IOException` on failure). Only
  non-null fields are written. Other top-level sections and unknown `Device` keys
  are preserved; comments and trailing commas are accepted when reading (comments
  are not kept). An empty file is treated as empty; an unparsable file is copied to
  `settings.json.<yyyyMMdd-HHmmss>.bak` (never overwriting an earlier backup) before
  it is replaced.
- `ISettingsStore.Load()` — the fields set in the user file only (not
  `appsettings.json`, defaults or environment); never throws.
- `ISettingsStore.Saved` — raised after a successful update. No reload call is
  needed: the configuration file watcher applies the change.
- `ISettingsStore.FilePath` — absolute path of the user settings file.
- Never copy effective settings (`IOptionsMonitor<DeviceSettings>.CurrentValue`)
  wholesale into the patch: that would persist environment overrides and freeze
  `appsettings.json` values. The tray dialog opens with
  `IOptionsMonitor<DeviceSettings>.CurrentValue` and saves with
  `UserDeviceSettingsPatch.ApplyDialogEdit(lowerLayers, displayed, edited, envFields)`,
  which touches only fields the user changed (edited ≠ displayed), skips
  env-overridden fields, and clears values equal to
  `DaemonConfiguration.LoadLowerLayers(...)` (defaults + `appsettings.json`).

### Connection Handling

`MqttConnection` owns the MQTT client, connection state and the reconnect
schedule, and never blocks the sampling loop:

- Each tick, `MaintainConnection()` returns immediately. If disconnected and the
  backoff deadline has passed, it starts one background connect attempt:
  `Host` first, then `FallbackAddress` if set, each bounded by `ConnectTimeoutMs`.
- While disconnected or an attempt is in flight, publishing is skipped for that tick.
- Backoff (`ReconnectBackoff`, pure and clock-injectable) starts at
  `ReconnectInitialBackoffMs` and doubles per failure up to `ReconnectMaxBackoffMs`.
  A successful connect does not reset it by itself: only a connection that stays
  up for at least min(`ReconnectMaxBackoffMs`, 30 s) resets it when it drops; a
  shorter-lived connection counts as a failure, so a flapping broker is still backed
  off. Changing the backoff settings keeps this connection state. An endpoint change
  resets the schedule and reconnects immediately.
- A failed publish marks the connection lost; the reconnect follows the backoff.
- Shutdown cancels any in-flight connect attempt first (a shutdown token is linked
  into every connect/close/publish timeout), so exit does not wait for connect
  timeouts; `MonitorService.StopAsync` honours the host stop token.
- One tray notification per outage ("MQTT connection failed") and one
  "MQTT reconnected" notice when it recovers. The outage only counts as notified
  once a handler actually ran (the tray attaches it before the host starts). The first failure of an outage
  is logged as a Warning, later failures at Debug with an Information summary
  every 20 attempts.

### Device status topic

- `MqttConnection` subscribes to `DeviceSettings.StatusTopic` (default `resmon/status`, QoS 0)
  inside every connect attempt. The client uses a clean session, so every reconnect, including
  one after a broker restart, subscribes again. If only the topic changes, the client unsubscribes
  and subscribes in the background without reconnecting. If the subscription is refused or fails,
  the daemon logs a warning and keeps publishing cpu/mem.
- Messages on the current status topic are raised through `MqttConnection.StatusMessageReceived`.
  The cpu/mem wire format, QoS 0 and non-retained publishing are unchanged.
- `DeviceStatusValidator` accepts a UTF-8 JSON object of at most 1 KB with an integer `v` from 1
  to 1 (supported version). `DeviceStatusForwarder` logs valid messages at Debug, logs one
  Information line per kind of rejection, and forwards valid messages to Loki only when the sink
  is enabled. Older firmware never publishes status, and that is not an error.

### Optional Grafana Loki observability

Disabled by default. Everything user-specific (URL, user, tenant, token) comes from the user's
settings or environment; the repository and `appsettings.json` hold none of it.

```
 sources (never block)                         queue                                sender
 LokiLoggerProvider ───┐
 LokiMetricsForwarder ─┼─► LokiEntryQueue (bounded, drop-oldest, ─► LokiSink loop ─► LokiPushClient ─► POST {Url}/loki/api/v1/push
 DeviceStatusForwarder ┘   strictly increasing ns timestamps)      (batch, backoff)   (HttpClient, gzip, auth, 10 s timeout)
```

- **Settings** (`LokiSettings`, section `Observability:Loki`): `Enabled`, `Url`, `AuthMode`
  (None/Basic/Bearer), `Username`, `TenantId` (`X-Scope-OrgID`), `IncludeHostLabel`,
  `MetricsIntervalSeconds` (10, 0 = off), `MinLevel` (Information), `BatchMaxEntries` (500),
  `QueueCapacity` (5000), `FlushIntervalMs` (2000), `Gzip` (true). `LokiSettingsLoader` reads
  these leniently and `LokiSettingsValidator.Sanitize` replaces bad values with a warning, the
  same pattern as `Device`. `IOptionsMonitor<LokiSettings>` reloads them live: the hosted
  `ObservabilityService` calls `LokiSink.Configure` on every change, so enabling, disabling or
  changing the URL needs no restart.
- **Token**: the effective token is resolved in this order:
  1. `RESMON_Observability__Loki__Token`. It is plain text, never persisted and shown read-only
     in the dialog.
  2. `TokenProtected` in the user settings file. This is base64 DPAPI ciphertext, scope
     CurrentUser, with app-specific entropy (`DpapiSecretProtector`). The
     `System.Security.Cryptography.ProtectedData` assembly ships with the WindowsForms shared
     framework, so no package was added.

  A plain `Token` key in a JSON file is ignored with a warning. `LokiSettings.ToString()` and
  `Describe()` never print the token. `LokiRedactor` removes the token, the Basic credential and
  the URL path from any HTTP error text before it is logged or shown. Credentials embedded in
  the Url are stripped with a warning.
- **Persistence**: `UserSettingsStore` also implements `IObservabilitySettingsStore`
  (`LoadLoki` / `UpdateLoki(Action<UserLokiSettingsPatch>)`). `ISettingsStore` is unchanged. The
  rules match the device patch: the file stores only fields that were explicitly set, other
  sections and unknown keys are preserved, writes are atomic, and empty sections are removed.
  `UserLokiSettingsPatch` has no plain-token property, only `TokenProtected` and
  `SetToken(token, protector)`.
- **Streams** (constant, low-cardinality labels; `host="<PC name>"` only when
  `IncludeHostLabel` is set):
  - `{app="resmon-daemon",kind="log",level=...}`: `LokiLoggerProvider`, entries at or above
    `MinLevel`, formatted as `Category: message` with the exception appended.
  - `{app="resmon-daemon",kind="metrics"}`: `LokiMetricsForwarder`. `MonitorService` passes each
    existing sample through the small `IMetricsObserver` hook, so no extra counter is added.
  - `{app="resmon-device",kind="status"}`: the raw validated status JSON.
- **No recursion**: the logger provider returns a no-op logger for the
  `ResourceMonitorDaemon.Observability.*` and `System.Net.Http.*` categories. All messages from
  the sink, settings warnings, the dialog and status handling therefore go to the local log only,
  and an HTTP failure can never feed back into the queue.
- **Sender** (`LokiSink`): a batch is sent when it reaches `BatchMaxEntries` or after
  `FlushIntervalMs`. Entries are grouped per stream and sorted by timestamp, and the queue assigns
  process-wide strictly increasing nanosecond timestamps.
  - 429, 5xx, timeouts and network errors keep the batch and retry the same batch with
    exponential backoff (`ReconnectBackoff`, 1 s doubling to 60 s). `Retry-After` is honoured up
    to 60 s.
  - Other 4xx and 3xx responses drop the batch. This is logged as a Warning once per status code
    until a push succeeds; redirects are not followed.
  - When the queue is full, the oldest entries are dropped, and the number of drops is logged at
    most once per minute.
- **Lifecycle**: `ObservabilityService` (an `IHostedLifecycleService`) is registered before
  `MonitorService`, so it starts first and stops last; the shutdown messages of the other services
  are still flushed. On stop it flushes within a 2 s budget, which is also bounded by the host
  stop token, then disposes the sink. A sink failure never affects sampling or MQTT.
- **Diagnostics**: one startup line, logged after all services have started:
  `Observability: Loki enabled|disabled; url host=<host[:port]>, auth=<mode>, token configured=yes|no.`
  Settings changes are logged with the same host-only description.
- **UI**: the tray item "可观测性 (Observability)…" opens `ObservabilityForm`; see Tray Controls.

Privacy notes:

- Nothing is sent unless the user enables the sink and enters a URL.
- Data is sent only to the URL the user configured.
- Forwarded log lines are the same text as `daemon.log`, so they can contain the broker host
  name or IP address.
- The device status contract excludes SSID, passwords, PoP, IP and MAC addresses.
- The host label is opt-in.
- Tokens are never stored in plain text and never logged.

### Logging and Diagnostics

- Log file: `%LocalAppData%\ResourceMonitorDaemon\daemon.log`, rolled to
  `daemon.log.1` at 5 MB. Writes are serialized (thread-safe) and failures to
  write never affect the daemon.
- Minimum level comes from the `Logging` section (Information by default);
  e.g. `RESMON_Logging__LogLevel__Default=Debug` enables per-sample lines.
- Startup diagnostics (logged once): informational version/build id, .NET
  runtime, settings sources with file paths and the names of `RESMON_*`
  overrides, effective settings, log path, working set/managed heap, and MQTT state.
  Connection state changes are logged as they happen. No secrets are logged.

### Tray Controls

- Pause/resume sending (label refreshes once per second).
- "⚙ 设置 (Settings)…" opens a dialog for Host, Port, CpuTopic, MemTopic and
  SampleIntervalMs with validation. Fields overridden by `RESMON_Device__*`
  environment variables are shown read-only with "(env)" and are never saved.
  Save writes only fields the user changed in the dialog, removes a changed value
  that equals defaults + `appsettings.json`, and leaves all other user-file fields
  (e.g. FallbackAddress) untouched; the change is
  applied live, and the confirmation balloon names any field still overridden by
  an environment variable.
- "查看日志 (Log)" opens `daemon.log` from `%LocalAppData%\ResourceMonitorDaemon`.
- "可观测性 (Observability)…" opens `ObservabilityForm` for the optional Loki sink.
  - Fields: Enabled, Url, AuthMode, Username, Token, TenantId, IncludeHostLabel and
    MetricsIntervalSeconds.
  - It uses the same only-changed-fields and env read-only rules as the settings dialog.
  - The token box is masked and never shows a stored token. An empty box keeps the saved token,
    with the placeholder "已保存 (saved)". Typing replaces the token, which is stored
    DPAPI-encrypted. "清除 (Clear)" removes it.
  - "测试连接 (Test)" pushes one line using the values in the dialog and shows the HTTP result.
  - Before saving credentials for a plain-`http` URL, the dialog asks for confirmation.
- Exit hides the icon and disables the menu first (re-entry guarded), then stops
  the hosted service within 3 s and disposes the tray icon.
- If the host fails to start, the tray icon is removed, the error is logged and
  shown in a message box, and the process exits.

## Project Structure

- `Program.cs`: builds configuration and logging, registers services, starts the tray UI.
- `DeviceSettings.cs`: settings record and defaults.
- `DeviceSettingsValidator.cs`: strict validation and lenient sanitizing.
- `DaemonConfiguration.cs`: file paths (`DaemonPaths`), configuration sources, options registration, lenient loader.
- `UserDeviceSettingsPatch.cs`: the explicitly-set user fields, validation and dialog-edit rules.
- `UserSettingsStore.cs`: `ISettingsStore` for the user settings file.
- `ReconnectBackoff.cs`: pure reconnect schedule.
- `MqttConnection.cs`: non-blocking connection management and publishing.
- `MonitorService.cs`: sampling loop, live settings, pause/resume, startup diagnostics.
- `MetricPayloadFormatter.cs`: formats and clamps percentage payloads for MQTT.
- `NativeMemory.cs`: wraps Windows memory status.
- `DaemonLogger.cs`: rotating file logger provider.
- `IMetricsObserver.cs`: per-sample hook used by the metrics stream.
- `Observability/`: optional Loki sink.
  - `LokiSettings`, `LokiSettingsLoader` (with the validator), `LokiUrl`: settings and URL rules.
  - `UserLokiSettingsPatch`, `SecretProtector` (DPAPI): persistence and token protection.
  - `LokiEntry` (labels, monotonic clock), `LokiEntryQueue`, `LokiPushClient` (payload, HTTP,
    redaction), `LokiSink`: the push pipeline.
  - `LokiLoggerProvider`, `LokiForwarders` (metrics and device status): the sources.
  - `ObservabilityService`: hosted service and DI registration.
  - `ObservabilityForm`: the dialog.
- `SettingsForm.cs`, `TrayContext.cs`: Windows tray menu and settings dialog.
- `ResourceMonitorDaemon.Tests`: zero-dependency test runner.

## Tests

The C# test runner covers:

- one-decimal invariant percentage formatting and clamping;
- settings validation, sanitizing to defaults and non-numeric values;
- configuration precedence (defaults < appsettings.json < user file < `RESMON_` env) and broken JSON files;
- `UserSettingsStore`: only set fields written, `appsettings.json` still applying
  after saving `FallbackAddress`, env values not persisted by the dialog edit,
  comments/trailing commas and unknown keys preserved, timestamped `.bak` backups
  that never overwrite each other, empty files without backup, dialog saves that
  touch only changed fields (even from stale values), invalid input, live reload
  through the file watcher;
- sanitize-warning de-duplication across reloads and absolute paths in load errors;
- log rotation under concurrent writers;
- `ReconnectBackoff` doubling, cap, reset, flapping broker, stable-connection reset,
  30 s stability cap and state preserved across `Reconfigure`;
- `StatusTopic` default, validation and collision handling.
- Loki sink:
  - push payload shape, labels and ns timestamps strictly increasing per stream, also across
    threads;
  - URL path joining and user-info stripping;
  - auth headers per mode, tenant header and gzip;
  - the token never appearing in `ToString`, results or logs (capturing logger);
  - drop-oldest queue and its counter, batching by size and interval with a fake clock;
  - 429/5xx retry with backoff and Retry-After, and 4xx drop logged once;
  - the recursion guard;
  - settings parse/sanitize, file tokens ignored, env token precedence, DPAPI round-trip and
    no env or plain token persisted;
  - live reload through the file watcher;
  - status payload validation;
  - an end-to-end run against a loopback `HttpListener` fake Loki (gzip-decoded logs, metrics,
    status, the Test push and the shutdown flush), the 2 s budget against a Loki that never
    answers, and `resmon/status` over the in-process broker, including resubscribing after a
    broker restart and following a topic change.
- `MqttConnection` against an in-process loopback MQTTnet broker: non-blocking ticks,
  one notification per outage, reconnect notice, fallback address, and unchanged
  topic/payload/QoS/retain on the wire; dispose with unreachable host + fallback
  finishing in under a second; a handler attached after the first failure still
  gets the outage notification.

Run it with:

```powershell
dotnet build daemon\ResourceMonitorDaemon.sln
dotnet run --project daemon\ResourceMonitorDaemon.Tests\ResourceMonitorDaemon.Tests.csproj
```

If the live daemon is running and locks the normal output files, build to a
temporary output directory (ignored by git):

```powershell
# from the repository root
dotnet build daemon\ResourceMonitorDaemon.sln /p:OutDir="$PWD\.tmpbuild\"
dotnet .tmpbuild\ResourceMonitorDaemon.Tests.dll
```

## Current Limitations

- There is no first-run setup / pairing wizard yet; it will write `FallbackAddress`
  through `ISettingsStore.Update`.
- Comments in a hand-edited `settings.json` are not preserved when the file is
  saved from the dialog or wizard.
- The daemon does not provision ESP32 Wi-Fi credentials yet.
- Wi-Fi credentials must not be stored in daemon settings, source code or logs.
- If later password recall is needed, it should be opt-in and protected with Windows DPAPI.
- Backoff and timeout values are editable only through the JSON files or
  environment variables, not the settings dialog.
- Retry granularity is one sample tick, so the effective minimum retry delay is
  `SampleIntervalMs`.
- Loki: entries queued in memory are lost if the process is killed, and entries still queued
  after the 2 s shutdown budget are dropped; there is no disk buffer. The status stream depends
  on firmware that publishes `resmon/status`. `MinLevel` below the `Logging` filter level has no
  effect until that filter is lowered too. Batching, queue and gzip settings are editable only
  in JSON or environment variables, not in the dialog.
