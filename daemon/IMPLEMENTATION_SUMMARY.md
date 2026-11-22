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
- `SettingsForm.cs`, `TrayContext.cs`: Windows tray menu and settings dialog.
- `Provisioning/`: BLE Wi-Fi provisioning client library (see "Provisioning client" below).
- `ResourceMonitorDaemon.Tests`: zero-dependency test runner.

The daemon targets `net9.0-windows10.0.19041.0` (Windows 10 2004 or later)
because the BLE client uses the WinRT `Windows.Devices.Bluetooth` APIs.

## Provisioning client

Namespace `ResourceMonitorDaemon.Provisioning`. It is the Windows side of the
pairing contract (`PROVISIONING.md` on the firmware branch): BLE provisioning
with the ESP-IDF 4.4 `wifi_provisioning` manager (which the firmware calls
directly), using protocomm **Security1** and a proof of possession (the
8-character pairing code). It is a library only; there is no
wizard UI yet and nothing in `Program.cs` uses it.

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
| `WifiConfigClient` | `ConfigureAsync`: GetStatus check -> SetConfig -> ApplyConfig -> GetStatus polling (at most 1 per second, at most 30 s including an in-flight exchange). Result: `Connected(ip4)` (with `JoinedSsid`, `AlreadyConnected`, `JoinedDifferentNetwork`), `Failed(AuthError or NetworkNotFound)`, `Timeout`, or `ConnectionLost` (lost contact with the device after the settings were applied, through a disconnect, GATT error or timeout; the device may have joined Wi-Fi). `Ip4Address` is null unless it is a usable unicast IPv4 address (0.0.0.0/8, loopback, multicast/reserved and broadcast are rejected). |
| `ProvisioningService.ProvisionAsync` | Uses the same `ConfigureAsync` path, so the already-connected check exists only once. |
| `SerialPopReader`, `PopLineParser`, `PairingCode` | USB serial pairing-code query (`RESMON?POP`), reply parsing (the line must start with `RESMON-POP `; only leading whitespace is tolerated) and pairing-code validation. |
| `ProtoWriter` / `ProtoReader`, `Sec1Messages`, `WifiConfigMessages` | Minimal hand-written protobuf codec for just these messages. |

Errors: `PairingCodeRejectedException` (wrong pairing code),
`ProvisioningTransportException` with an `Error` kind (Bluetooth off or
unavailable, device not found or disconnected, timeout, service or endpoint
missing, GATT error), and `ProvisioningProtocolException` (unexpected device
answers), including `ProvisioningRetryRejectedException` when the device
refuses new settings because an earlier attempt is still in progress or
failed. Messages can be shown to the user.

Secrets: the Wi-Fi password arrives as a .NET `string`, which cannot be wiped,
so the caller (the wizard) should drop its references as soon as
`ProvisionAsync` returns. Inside the library, the byte copies made from it (the
UTF-8 encoding and the unencrypted SetConfig message) are zeroed after
encryption. It is never logged, stored, or put in exception messages. The
pairing code follows the same rules: its hash and the session key are zeroed,
it is never logged, and it is redacted from `ToString()`. The SSID is not
logged either, and `Connected` and `WifiStatus` redact it from `ToString()`.
.NET's AES object keeps its own internal copy of the session
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
  (`manager.c`). The firmware therefore calls `wifi_prov_mgr_disable_auto_stop`
  and stops provisioning itself about 30 s after success, so BLE stays up for
  roughly 35 s after the device reports Connected. The client does not depend
  on that window:
  - a disconnect right after a Connected read still returns `Connected`;
  - losing contact while still waiting (disconnect, GATT error or timeout)
    returns `ConnectionLost`, and the wizard should then try the normal MQTT
    connection to `resmon.local` before reporting failure.
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
    sends no credentials, because a new ApplyConfig would fail and the user
    would be told to restart a device that is working. It returns `Connected`
    with `AlreadyConnected = true` and `JoinedSsid` (from
    `WifiConnectedState.ssid`).
    - `JoinedDifferentNetwork` is true when that SSID differs from the one the
      user just entered. The wizard should then say "the device is already on
      network X" instead of reporting success for the requested network.
    - If the device does not report its SSID (field absent, empty or only NUL
      padding), the flag stays false.
    - The SSID is never logged; the log only says whether it matched.
    - On a real device, answering Connected also starts the firmware's
      end-of-provisioning countdown.
  - In v4.4.7, ApplyConfig returns InternalError when
    `wifi_prov_mgr_configure_sta` fails, which happens once `prov_state` has
    reached CRED_RECV, or when no config was set. CRED_RECV covers two cases:
    an earlier attempt is still connecting (for example after a client
    `Timeout`), or it failed without a reset. The client always turns this
    into `ProvisioningRetryRejectedException`. `PreviousAttemptFailed` on the
    exception says whether an earlier failure was seen.
  - The message tells the user to hold the BOOT button for 5–15 seconds and
    then release it (holds longer than 15 s are ignored by the firmware). It
    deliberately does not suggest power-cycling. `configure_sta` has already
    stored the earlier credentials in NVS, so after a power-cycle the device
    would start as a plain Wi-Fi station with those credentials and no BLE.
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
  a link lost while waiting, retry after a failure, an already-connected
  device (same, different or unreported SSID), an empty GetStatus,
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
- the real BLE teardown timing after `Connected` (the firmware's window of
  about 35 s). The fake device only simulates a link drop right after the
  Connected read, or while still connecting.

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
- `MqttConnection` against an in-process loopback MQTTnet broker: non-blocking ticks,
  one notification per outage, reconnect notice, fallback address, and unchanged
  topic/payload/QoS/retain on the wire; dispose with unreachable host + fallback
  finishing in under a second; a handler attached after the first failure still
  gets the outage notification.
- the provisioning client (`ProvisioningTests.cs` and
  `FakeProvisioningDevice.cs`; see "Provisioning client").

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
  through `ISettingsStore.Update`. The BLE provisioning client library exists, but
  no UI uses it yet and it has not been tested against real hardware.
- Comments in a hand-edited `settings.json` are not preserved when the file is
  saved from the dialog or wizard.
- Wi-Fi credentials must not be stored in daemon settings, source code or logs.
- If later password recall is needed, it should be opt-in and protected with Windows DPAPI.
- Backoff and timeout values are editable only through the JSON files or
  environment variables, not the settings dialog.
- Retry granularity is one sample tick, so the effective minimum retry delay is
  `SampleIntervalMs`.
