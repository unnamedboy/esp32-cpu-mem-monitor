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
  are not kept). An unparsable file is copied to `settings.json.bak` before it is
  replaced.
- `ISettingsStore.Load()` — the fields set in the user file only (not
  `appsettings.json`, defaults or environment); never throws.
- `ISettingsStore.Saved` — raised after a successful update. No reload call is
  needed: the configuration file watcher applies the change.
- `ISettingsStore.FilePath` — absolute path of the user settings file.
- Never copy effective settings (`IOptionsMonitor<DeviceSettings>.CurrentValue`)
  wholesale into the patch: that would persist environment overrides and freeze
  `appsettings.json` values. The tray dialog uses
  `UserDeviceSettingsPatch.ApplyDialogEdit(lowerLayers, edited, envFields)`, which
  skips env-overridden fields and clears values equal to
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
  up for at least `ReconnectMaxBackoffMs` resets it when it drops; a shorter-lived
  connection counts as a failure, so a flapping broker is still backed off. An
  endpoint change resets it and reconnects immediately.
- A failed publish marks the connection lost; the reconnect follows the backoff.
- Shutdown cancels any in-flight connect attempt first (a shutdown token is linked
  into every connect/close/publish timeout), so exit does not wait for connect
  timeouts; `MonitorService.StopAsync` honours the host stop token.
- One tray notification per outage ("MQTT connection failed") and one
  "MQTT reconnected" notice when it recovers. The outage only counts as notified
  once a handler actually ran (the tray attaches it before the host starts). The first failure of an outage
  is logged as a Warning, later failures at Debug with an Information summary
  every 20 attempts.

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
  Save writes only values that differ from defaults + `appsettings.json` and
  leaves other user-file fields (e.g. FallbackAddress) untouched; the change is
  applied live, and the confirmation balloon names any field still overridden by
  an environment variable.
- "查看日志 (Log)" opens `daemon.log` from `%LocalAppData%\ResourceMonitorDaemon`.
- Exit hides the icon and disables the menu first (re-entry guarded), then stops
  the hosted service within 3 s and disposes the tray icon.

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
- `SettingsForm.cs`, `TrayContext.cs`: Windows tray menu and settings dialog.
- `ResourceMonitorDaemon.Tests`: zero-dependency test runner.

## Tests

The C# test runner covers:

- one-decimal invariant percentage formatting and clamping;
- settings validation, sanitizing to defaults and non-numeric values;
- configuration precedence (defaults < appsettings.json < user file < `RESMON_` env) and broken JSON files;
- `UserSettingsStore`: only set fields written, `appsettings.json` still applying
  after saving `FallbackAddress`, env values not persisted by the dialog edit,
  comments/trailing commas and unknown keys preserved, `.bak` backup of an
  unparsable file, invalid input, live reload through the file watcher;
- sanitize-warning de-duplication across reloads and absolute paths in load errors;
- log rotation under concurrent writers;
- `ReconnectBackoff` doubling, cap, reset, flapping broker and stable-connection reset;
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
