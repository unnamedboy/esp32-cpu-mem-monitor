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
3. user settings `%LocalAppData%\ResourceMonitorDaemon\settings.json` (written by the tray dialog and the pairing wizard; contains **only the fields explicitly set there**, so values not set keep coming from `appsettings.json`/defaults);
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
  to 1 (supported version). It re-serializes only the contract fields, in contract order, with
  type checks; `build` is capped at 64 characters and `wifi`/`prov` at 32. Unknown fields are
  accepted but never forwarded, so nothing extra can be smuggled into Loki.
- `DeviceStatusForwarder` logs valid messages at Debug and logs one Information line per kind of
  rejection. It forwards valid messages to Loki only when the sink is enabled, through a token
  bucket: a burst of 3, then 1 per 5 s; the contract interval is 10 s. The broker is
  unauthenticated, so a flood cannot evict queued daemon logs or burn Loki ingestion. Excess
  messages are counted and reported locally at most once per minute.
- Older firmware never publishes status, and that is not an error.

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
  - `{app="resmon-device",kind="status"}`: the normalized, rate-limited status JSON.
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
  - Network errors, timeouts and 5xx are retried with no attempt or age limit, so an outage of
    any length loses only what the bounded drop-oldest queue drops. Only one batch, plus its split
    halves, is held outside the queue. Loki rejects entries older than its
    `reject_old_samples_max_age` with a 400, and that drops the batch.
  - After 3 throttled attempts (any 429, with or without Retry-After), a batch is split in half;
    splitting is lossless. A single entry throttled 3 times without Retry-After is given up: it is
    counted in `DroppedByServer` and logged as a Warning once until a push succeeds. With
    Retry-After it is retried for as long as the server asks.
  - Batches are capped at 1 MiB of uncompressed body. The body is written as raw UTF-8
    (`JavaScriptEncoder.UnsafeRelaxedJsonEscaping`): quotes, backslashes and control characters
    are still escaped, and the size estimate counts those escapes, so CJK text is not doubled by
    `\uXXXX`. Single lines are truncated at 16 KB with a marker.
  - 413 splits the batch in half and retries at once; a single entry that is still too large is
    dropped.
  - Other 4xx and 3xx responses drop the batch. This is logged as a Warning once per status code
    until a push succeeds; redirects are not followed.
  - When the response body says `total ignored: N out of M` (Loki stores the valid entries of a
    partly rejected batch), only N count as dropped and the log says "rejected N of M entries".
    Otherwise the whole batch counts, which is an upper bound, and the log says Loki may have
    accepted some entries.
  - 401/403/404 and request errors also back off, so a wrong URL or credentials are not retried
    every flush interval.
  - When the queue is full, the oldest entries are dropped, and the number of drops is logged at
    most once per minute.
- **Lifecycle**: `ObservabilityService` (an `IHostedLifecycleService`) is registered before
  `MonitorService`, so it starts first and stops last; the shutdown messages of the other services
  are still flushed. On stop it flushes within a 2 s budget, which is also bounded by the host
  stop token, then disposes the sink. `Program.Main` is the single shutdown owner and stops the host
  once (3 s budget), so `MonitorService` stops first and the sink flushes in the remaining time. A sink failure never affects sampling or MQTT.
- **Diagnostics**: one startup line, logged after all services have started:
  `Observability: Loki enabled|disabled; url host=<host[:port]>, auth=<mode>, token configured=yes|no.`
  Settings changes are logged with the same host-only description.
- **UI**: the tray item "可观测性 (Observability)…" opens `ObservabilityForm`; see Tray Controls.
  All user-visible texts of the dialog and its tray entry are in `ObservabilityText`, ready to move
  to resources.

Privacy notes:

- Nothing is sent unless the user enables the sink and enters a URL.
- Data is sent only to the URL the user configured.
- Forwarded log lines pass through `LokiLogRedactor` first:
  - `%LOCALAPPDATA%`, `%APPDATA%` and `%USERPROFILE%` prefixes replace the real folders;
  - the 8.3 short forms of those folders are replaced too;
  - the profile folder in any other absolute path (`X:\Users\<name>` or `\\server\Users\<name>`,
    either slash, any case, spaces allowed up to the next separator) becomes `<user>`. `Public`,
    `Default`, `Default User` and `All Users` are kept, and relative paths and URLs such as
    `https://host/Users/123` are not touched;
  - cheap substring checks run before each regex, so a typical line costs about 0.3 µs;
  - the Windows user name becomes `<user>` (whole word, case-insensitive, names of at least
    3 characters). If the account name is a common word such as `admin`, that word is also
    replaced in ordinary text. This is accepted in favour of privacy.
  - the PC name becomes `<host>`, unless `IncludeHostLabel` is on.

  Otherwise they are the same text as `daemon.log`, so they can contain the broker host name or
  IP address.
- Device status is forwarded as contract fields only.
- Credentials configured for a plain `http://` Url produce a settings warning, and the dialog asks
  before it saves them or uses them for a Test push.
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
- "配对设备 (Pair device)…" opens the pairing wizard (one instance at a time; see
  "Pairing wizard"). On the first connection failure of an unconfigured PC, the
  failure balloon is shown once as a "click to pair" balloon instead. It keeps
  the "MQTT connection failed" title, says the device cannot be reached and "未找到设备，点击进行配对
  (No device found — click to pair)", and clicking it opens the wizard.
- Pause/resume confirmations use the same balloon helper, so clicking them never
  opens the wizard.
- "查看日志 (Log)" opens `daemon.log` from `%LocalAppData%\ResourceMonitorDaemon`.
- "可观测性 (Observability)…" opens `ObservabilityForm` for the optional Loki sink.
  - Fields: Enabled, Url, AuthMode, Username, Token, TenantId, IncludeHostLabel and
    MetricsIntervalSeconds.
  - It uses the same only-changed-fields and env read-only rules as the settings dialog.
  - The token box is masked and never shows a stored token. An empty box keeps the saved token,
    with the placeholder "已保存 (saved)". Typing replaces the token, which is stored
    DPAPI-encrypted. "清除 (Clear)" removes it.
  - "测试连接 (Test)" pushes one line using the values in the dialog and shows the HTTP result.
  - Before saving or testing credentials for a plain-`http` URL, the dialog asks for confirmation.
  - The dialog is modeless; Cancel closes it without saving, and Exit closes it.
- Exit hides the icon and disables the menu immediately (re-entry guarded) and ends
  the message loop; `Program.Main` is the single owner of shutdown and stops the
  host once (3 s budget) before disposing it. `MonitorService.StopAsync` is also
  idempotent.
- The settings dialog is modeless; Cancel and Esc close it without saving.
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
- `Provisioning/`: BLE Wi-Fi provisioning client library (see "Provisioning client" below).
- `Pairing/`: pairing wizard form, `PairingOutcomePresenter` (pure decisions and messages),
  `PairingText` (fixed UI texts), `BrokerProbe`, `CurrentWifiNetwork` (SSID prefill and
  2.4 GHz hint) and `FirstRunPairingPrompt` (see "Pairing wizard").
- `ResourceMonitorDaemon.Tests`: zero-dependency test runner.

The daemon targets `net9.0-windows10.0.19041.0` (Windows 10 2004 or later)
because the BLE client uses the WinRT `Windows.Devices.Bluetooth` APIs.

## Provisioning client

Namespace `ResourceMonitorDaemon.Provisioning`. It is the Windows side of the
pairing contract (`PROVISIONING.md` on the firmware branch): Arduino-ESP32
`WiFiProv` BLE provisioning with protocomm **Security1** and a proof of
possession (the 8-character pairing code). The pairing wizard (see "Pairing
wizard") is its only user.

Wire formats and behaviour were taken from ESP-IDF **v4.4.7**, the IDF bundled
with Arduino-ESP32 2.0.17 (`framework-arduinoespressif32` 3.20017):
`components/protocomm/proto/{constants,session,sec1}.proto`,
`components/wifi_provisioning/proto/{wifi_constants,wifi_config}.proto`, and the
reference client `tools/esp_prov/{security/security1.py, prov/wifi_prov.py,
transport/ble_cli.py}` at
<https://github.com/espressif/esp-idf/tree/v4.4.7>.

### Components

| Type | Role |
| --- | --- |
| `ProvisioningService` | Facade for the wizard: `ScanAsync`, `TryReadPopFromUsbAsync`, `ProvisionAsync` with step progress. |
| `BleDeviceScanner` | Active BLE scan; reports `PROV_RESMON_*` devices once each, strongest first. |
| `BleProvisioningTransport` | WinRT GATT client. Finds service `021a9004-0382-4aea-bff4-6b3f1c5adfb4`, maps endpoints by their 0x2901 user description, sends each request as write-with-response then reads the response from the same characteristic. |
| `IProvisioningTransport` | `SendAsync(endpoint, data, ct)` seam; the tests use an in-memory fake device behind it. |
| `Security1Session` | X25519 key exchange, key = shared secret XOR SHA-256(pairing code), AES-256-CTR with IV = device random and one keystream for all traffic, Command0/1 handshake and verifier check. |
| `WifiConfigClient` | GetStatus (earlier-failure check) -> SetConfig -> ApplyConfig -> GetStatus polling (at most 1 per second, at most 30 s including an in-flight exchange). Result: `Connected(ip4)`, `Failed(AuthError or NetworkNotFound)`, `Timeout`, or `ConnectionLost` (lost contact with the device after the settings were applied, through a disconnect, GATT error or timeout; the device may have joined Wi-Fi). |
| `SerialPopReader`, `PopLineParser`, `PairingCode` | USB serial pairing-code query (`RESMON?POP`), reply parsing and pairing-code validation. |
| `ProtoWriter` / `ProtoReader`, `Sec1Messages`, `WifiConfigMessages` | Minimal hand-written protobuf codec for just these messages. |

Errors: `PairingCodeRejectedException` (wrong pairing code),
`ProvisioningTransportException` with an `Error` kind (Bluetooth off or
unavailable, device not found or disconnected, timeout, service or endpoint
missing, GATT error), and `ProvisioningProtocolException` (unexpected device
answers), including `ProvisioningRetryRejectedException` when the device
refuses a new attempt after a failed one. Messages can be shown to the user.

Secrets: the Wi-Fi password arrives as a .NET `string`, which cannot be wiped,
so the caller (the wizard) should drop its references as soon as
`ProvisionAsync` returns. Inside the library, the byte copies made from it (the
UTF-8 encoding and the unencrypted SetConfig message) are zeroed after
encryption. It is never logged, stored, or put in exception messages. The
pairing code follows the same rules: its hash and the session key are zeroed,
it is never logged, and it is redacted from `ToString()`. The SSID is not
logged either. .NET's AES object keeps its own internal copy of the session
key until it is disposed.

### Dependencies

- `BouncyCastle.Cryptography`: X25519. .NET has no portable X25519 API, and a
  security protocol should not depend on hand-rolled curve arithmetic. AES-CTR
  uses the built-in .NET AES (ECB mode) plus a 128-bit big-endian counter.
- `System.IO.Ports`: serial port access for the USB pairing-code query. It is
  not part of the base library.
- Windows SDK projection (from the `windows10.0.19041.0` target framework):
  Bluetooth LE scanning and GATT.

### Protocol notes

- On a wrong pairing code, a real ESP-IDF v4.4 device does not send a
  mismatching verifier. `security1.c` fails Command1 and `protocomm_ble.c`
  closes the GATT connection without a response. The client therefore reports
  `PairingCodeRejectedException` (reason `DeviceClosedConnection`) when the link
  fails during Command1. It also reports it (reason `VerificationMismatch`) if
  the device's verifier does not match.
- If the write for Command1 is acknowledged anyway (the prepared-write path in
  `protocomm_ble.c` acknowledges even on failure), the read returns the stale
  Response0. The client treats that as a rejected pairing code too.
- The device ends the protocomm session when BLE disconnects. The transport
  keeps the link open with `GattSession.MaintainConnection`. If a disconnect
  has been seen, the next request fails before it is written. A read that
  already completed is still returned, even if the link drops right after.
- **BLE teardown after success.** In IDF v4.4.7, the GetStatus handler calls
  `wifi_prov_mgr_done()` as soon as it reports Connected (`handlers.c`). With
  the default auto-stop, BLE then goes down after `max(cleanup_delay, 100 ms)`
  (`manager.c`). The 30 s auto-stop timer is only a fallback. The contract
  therefore requires the firmware to call `wifi_prov_mgr_disable_auto_stop(5000)`
  or longer, and the client relies on that. The client also copes when the
  firmware does not: a disconnect right after a Connected read still returns
  `Connected`. Losing contact while still waiting (disconnect, GATT error or
  timeout) returns `ConnectionLost`, and
  the wizard should then try the normal MQTT connection to `resmon.local`
  before reporting failure.
- **Retry after a failure.** After a failed attempt, `wifi_prov_mgr` rejects
  new credentials (ApplyConfig returns InternalError) unless the firmware calls
  `wifi_prov_mgr_reset_sm_state_on_failure()`, which the contract requires.
  - A retry usually means a new `ProvisionAsync` call, with a new connection
    and session. So before sending credentials, the client sends one GetStatus
    to learn whether the device still reports ConnectionFailed. The reset does
    not clear the device's `wifi_state` (`manager.c`), so that answer survives
    across sessions. Before any ApplyConfig, an unconfigured device reports
    Connecting (`wifi_state` 0).
  - If this check returns Connected, the device already joined Wi-Fi after an
    earlier attempt ended in `Timeout` or `ConnectionLost`. The client then
    sends no credentials and returns `Connected(ip4) { AlreadyConnected = true }`
    directly, because a new
    ApplyConfig would fail and the user would be told to restart a device that
    is working. On a real device, answering Connected also makes the firmware
    finish provisioning and stop BLE.
  - In v4.4.7, ApplyConfig only returns InternalError when
    `wifi_prov_mgr_configure_sta` fails or no config was set. The client
    therefore always turns it into `ProvisioningRetryRejectedException`.
    `PreviousAttemptFailed` on the exception says whether an earlier failure
    was seen.
  - The message tells the user to power-cycle the device, or hold its BOOT
    button for 5–15 seconds and then release it (holds longer than 15 s are
    ignored by the firmware).
- An empty GetStatus response is not treated as Connected, even though
  Connected is the protobuf default value. The client only reports Connected
  when the `connected` sub-message is present.
- The protobuf reader checks tag ranges, wire types and lengths, so malformed
  input from any nearby device advertising `PROV_RESMON_*` can only raise
  `ProvisioningProtocolException`. This is tested with crafted inputs and a
  seeded fuzz loop.
- If an exchange fails half-way, the session is marked unusable because the
  client and device keystreams can no longer be aligned. Reconnect and start a
  new session.
- `proto-ver` is queried in plain text (`---`, as `esp_prov` does) and logged.
  If it is missing, provisioning still continues.

### Verification status

Covered by unit tests (all in-memory):

- protobuf encodings, checked against byte sequences derived by hand from the
  `.proto` field numbers;
- X25519 against the RFC 7748 section 6.1 vectors;
- AES-256-CTR against NIST SP 800-38A F.5.5/F.5.6, plus keystream continuity
  across calls of uneven lengths;
- a full round trip against an in-memory fake device. Its crypto uses
  different code paths from the client (BouncyCastle `X25519Agreement` and
  `SicBlockCipher`). Its message layouts are written with literal field
  numbers, but its low-level protobuf framing reuses the client's
  `ProtoReader`/`ProtoWriter`, so it does not check varint or tag encoding
  independently; the byte-level ProtoWire tests cover that. Cases covered:
  Connected, wrong pairing code (dropped link or stale Response0), forged
  verifier, AuthError, NetworkNotFound, timeout (including a hung exchange),
  a link lost while waiting, retry after a failure, an empty GetStatus,
  disposal during an exchange, and the session becoming unusable;
- no golden transcript from the reference Python client: neither the system
  Python nor PlatformIO's Python has the `cryptography` package installed;
- serial reply parsing, scan de-duplication, and `ProvisioningService` progress
  and log redaction.

**Unverified on real hardware.** Nothing in this library has been run against
a physical ESP32 yet. In particular:

- WinRT BLE scanning, connecting, 0x2901 endpoint discovery, long
  (prepared) writes for the SetConfig payload, and the real Windows error codes
  when the device drops the link on a wrong pairing code;
- interoperability of the Security1 handshake with the real device: the fake
  device follows the IDF sources but is not the IDF implementation;
- the USB serial query: whether opening the port with DTR/RTS deasserted
  avoids resetting this board's USB-UART bridge, and how Bluetooth virtual COM
  ports behave (probes are abandoned after 1.5 s);
- the real BLE teardown timing after `Connected` and after
  `wifi_prov_mgr_disable_auto_stop`. The fake device only simulates a link
  drop right after the Connected read, or while still connecting.

## Pairing wizard

Namespace `ResourceMonitorDaemon.Pairing`. A WinForms dialog
(`PairingWizardForm`), "配对设备 (Pair device)", on top of `ProvisioningService`.
It has two columns so it still fits a 1080p screen at 150 % scaling. All work
runs on the UI thread with async/await, so the UI never blocks.

| Section | Behaviour |
| --- | --- |
| 1. Device | Starts a 5 s BLE scan when the wizard opens and lists `PROV_RESMON_*` devices live, with signal strength. A Rescan button repeats it. If exactly one device is found, it is selected. Bluetooth off or missing gives a clear message (`ProvisioningTransportException.Error`). |
| 2. Pairing code | Selecting a device starts `TryReadPopFromUsbAsync(name)` in the background. If found, the code is filled in masked: "已通过 USB 自动读取配对码 (read automatically over USB)". The user can switch to "手动输入 (Enter manually)" and back. Typed codes are normalized with `PairingCode.TryNormalize`. A hint names the serial log line `Provisioning: name=... pop=...` at 115200 baud. |
| 3. Wi-Fi | SSID and password, with a masked password box and a show/hide checkbox. A note says the ESP32 supports 2.4 GHz only. Nothing is read when the wizard opens. The link "使用本机当前 Wi-Fi (Use this PC's Wi-Fi)" next to the SSID box runs the lookup on click, off the UI thread. Reading the connected SSID can make Windows 11 show its one-time "allow location access" prompt (Microsoft Learn: "Changes to API behavior for Wi-Fi access and location"), so it is never done automatically. The SSID is filled in when WinRT (`NetworkInformation` → `WlanConnectionProfileDetails.GetConnectedSsid()`) reports exactly one connected Wi-Fi network; there is no code-page conversion and no process to start. If nothing can be read, the note asks the user to type the name (not connected, or location access not granted). The optional 2.4 GHz warning is computed on the same click from `netsh wlan show interfaces`. Its output is decoded with the system OEM code page (`GetOEMCP`, e.g. 936 on Chinese Windows) and read from the "Band" line, or from the channel number (> 14 means 5 GHz) when there is no band line. English and Chinese labels are recognized; otherwise the band is unknown and no warning is shown. Everything here is best effort: nothing is installed, no admin is needed, and any failure just leaves the field empty. Input is validated with `PairingCode` and `WifiConfigClient.ValidateCredentials` before any Bluetooth traffic. |
| 4. Pair | `ProvisionAsync` with step progress (connect, secure session, send settings, wait for Wi-Fi), followed by "save address" and "daemon connects". Cancel cancels the attempt. Closing the wizard cancels the scan, USB query, attempt and checks, and `ProvisionAsync` then disposes the BLE connection. |

Results (`PairingOutcomePresenter`, pure and unit-tested):

| Result | Message / action |
| --- | --- |
| `Connected(ip)` with a usable unicast IPv4 | Success. `ISettingsStore.Update(p => p.FallbackAddress = ip)` is called, and nothing else is changed. |
| `Connected(null)`, or an unusable address (0.0.0.0, broadcast, loopback, multicast, 169.254/16) | Success, but nothing is saved; the daemon keeps using `Host` (resmon.local). |
| `Failed(AuthError)` | "Wi-Fi 密码错误"; focus moves to the password box. |
| `Failed(NetworkNotFound)` | "找不到该 Wi-Fi（ESP32 仅支持 2.4 GHz）"; focus moves to the SSID box. |
| `Timeout` | No result within 30 s: check the SSID, password and 2.4 GHz support, then retry. A retry detects a device that joined Wi-Fi late. |
| `ConnectionLost` | `BrokerProbe` tries for up to 15 s, about once per second: a daemon MQTT connection made *after the attempt started*, or a TCP connect to the configured `Host:Port`. If that works, it is a success, with no address saved. Otherwise a failure with "rescan, or power-cycle / hold BOOT 5–15 s". |
| `PairingCodeRejectedException` | "配对码错误"; focus moves to the code box. |
| `ProvisioningRetryRejectedException` | Chinese restart/BOOT instruction plus the library message, which already contains `ReprovisionInstruction`. |
| `ProvisioningTransportException` | Specific text per `Error` kind (Bluetooth off or unavailable, device gone, disconnected, timeout, not a provisioning device). |
| `ArgumentException` | Field-specific invalid-input message; the parameter suffix is removed and values are never echoed. |
| Anything else | Generic "see the log" message. The exception is logged as an error. |

After a save, the configuration file watcher applies the new
`FallbackAddress`, and `MqttConnection` reconnects (an endpoint change skips
the backoff). The wizard polls the read-only `MonitorService.IsConnected` and
`ConnectionGeneration` for up to 45 s. It waits until the new address has been
applied and a connection exists that was established after the attempt started,
so an older MQTT session that is still up does not count. There is one
exception. If the device was already on Wi-Fi before the attempt sent anything
(`WifiProvisioningResult.Connected.AlreadyConnected`, the retry short-circuit),
its broker did not restart. If in addition the effective `FallbackAddress` did
not change (same address, or no IP reported), the daemon has no reason to
reconnect. In that case an existing connection counts
(`PairingOutcomePresenter.RequiresNewDaemonConnection`). It then shows
**"已连接，正在发送数据 (connected, publishing)"**. Otherwise it reports that sending
is paused or that the daemon keeps retrying. If
`RESMON_Device__FallbackAddress` overrides the saved value, the wizard says so.

Ambiguity: `Host` (resmon.local) is an mDNS name. With several monitors on
the same network it can resolve to a different device than the one just
paired. The wizard cannot tell them apart in the `ConnectionLost` check or in the
final status when no address was reported.

Password hygiene: the password is read from the box only for the
`ProvisionAsync` call. The local reference is dropped and the box cleared after
every attempt, on success, failure or cancel. On `AuthError` the focus returns
to the empty box. The box is also cleared when the wizard closes. The password
is never logged or persisted. The SSID and pairing code are not logged either:
log lines name only the device, the result and the saved IPv4 address.

First-run prompt (`FirstRunPairingPrompt`): the prompt is offered only when all
of these hold: the notification is the MQTT connection-failure one, the
prompt was never shown, the effective `FallbackAddress` is empty, and the
daemon has not connected since it started. The tray then shows the failure as
the "click to pair" balloon, which keeps the failure title and states the failure,
and writes the marker file `%LocalAppData%\ResourceMonitorDaemon\pairing-prompted`.
The prompt therefore appears at most once per user. The marker is written as soon as the balloon is
requested, so if Windows suppresses it (Focus Assist, notifications off), the prompt
is not repeated; the tray menu entry always works. Delete that file to see the
prompt again. No window opens on its own at login.

User-visible texts: fixed wizard and tray-prompt texts are in `PairingText`;
outcome-dependent messages are built in `PairingOutcomePresenter`. Both are
ready to move to resources for localization.

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
- the provisioning client (`ProvisioningTests.cs` and
  `FakeProvisioningDevice.cs`; see "Provisioning client").
- the pairing wizard's pure parts (`PairingTests.cs`): result and exception
  messages, which addresses may be saved, input validation without echoing
  values, scan texts, the first-run rule and marker file, SSID selection, the
  `netsh` band parser (English labels, and Chinese output encoded as CP936 bytes),
  and `BrokerProbe` against a loopback listener and a closed port. The
  `MqttConnection` scenario also checks that `ConnectionGeneration` advances on
  every (re)connect. The form itself
  is not unit-tested. Its layout was checked by rendering it with `DrawToBitmap`
  at 100 % and at a simulated 150 %.

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

- The pairing wizard and BLE provisioning client have **not been tested against
  real hardware**. That includes the BLE scan and GATT, the USB code query, and the
  timing of the `ConnectionLost` path.
- The first-run balloon opens the wizard only while it is on screen. Windows does
  not report clicks on notifications that have moved to the Action Center, so
  use the tray menu then.
- "Use this PC's Wi-Fi" and the 2.4 GHz hint are best effort. Windows 11 may ask
  once for location permission when the link is clicked. If it is denied, or
  location services are off, the SSID cannot be read and must be typed.
  The band hint only recognizes English and Chinese `netsh` labels.
- Reopening the wizard right after closing it can miss the USB pairing code. A
  serial probe abandoned after its 1.5 s budget may still hold the COM port
  (for example, blocked in `Open()`) until it finishes. Wait a moment and select
  the device again, or type the code.
- `resmon.local` may name a different monitor when several are on the network
  (see "Pairing wizard").
- The wizard does not set a device name or the MQTT port and topics. The BLE
  contract only carries the SSID and password.
- Comments in a hand-edited `settings.json` are not preserved when the file is
  saved from the dialog or wizard.
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
