# Windows daemon internals

`ResourceMonitorDaemon` is a Windows tray app. It samples CPU and memory usage, publishes
them to the ESP32's MQTT broker, pairs new devices over Bluetooth LE, and can optionally
forward logs and metrics to Grafana Loki. This page explains how it is put together.
For using it, see the [README](../README.md); for the wire formats, see
[protocol.md](protocol.md) and [provisioning.md](provisioning.md).

## Platform and dependencies

The project targets `net9.0-windows10.0.19041.0` (Windows 10 version 2004 or later),
because the Bluetooth client uses the WinRT `Windows.Devices.Bluetooth` APIs. The UI is
Windows Forms.

| Package | Used for |
| --- | --- |
| `Microsoft.Extensions.Hosting` | Generic host: configuration, dependency injection, logging, hosted services |
| `MQTTnet` 4.3 | MQTT client |
| `BouncyCastle.Cryptography` | X25519 for the Security1 handshake (.NET has no portable X25519 API; AES-CTR uses the built-in AES) |
| `System.IO.Ports` | USB serial pairing-code query |

The test project ([`daemon/ResourceMonitorDaemon.Tests`](../daemon/ResourceMonitorDaemon.Tests))
is a plain console runner with no test framework.

## Components

| Path | Role |
| --- | --- |
| `Program.cs` | Builds the host (configuration sources, services, logging), creates the tray, runs the message loop, owns shutdown |
| `MonitorService.cs` | Sampling loop, live settings, pause/resume, startup diagnostics |
| `MqttConnection.cs`, `ReconnectBackoff.cs` | Non-blocking connection management, reconnect schedule, publishing, status subscription |
| `MetricPayloadFormatter.cs`, `NativeMemory.cs` | Payload formatting and clamping; Windows memory status |
| `DeviceSettings.cs`, `DeviceSettingsValidator.cs` | `Device` settings, defaults, validation and sanitizing |
| `DaemonConfiguration.cs` | File locations (`DaemonPaths`), configuration layers, options registration |
| `UserSettingsStore.cs`, `UserDeviceSettingsPatch.cs` | Reading and writing the per-user settings file |
| `TrayContext.cs`, `SettingsForm.cs` | Tray menu, notifications, settings dialog |
| `DaemonLogger.cs` | Rolling file logger |
| `Localization/`, `Resources/` | UI strings and language selection |
| `Observability/` | Optional Loki sink and its dialog |
| `Provisioning/` | BLE provisioning client library |
| `Pairing/` | Pairing wizard and first-run prompt |

## Threads and lifetime

- **UI thread.** `Program.Main` runs on an STA thread and owns the Windows Forms message
  loop: tray icon, notifications, dialogs and the pairing wizard. The wizard's Bluetooth
  and serial work is async on this thread, so the UI never blocks.
- **Sampling loop.** `MonitorService` is a hosted `BackgroundService`. It samples on a
  fixed-rate schedule (missed ticks are skipped, not bunched up) and also wakes when
  settings change.
- **Connection attempts** run on the thread pool. A sampling tick only checks state and
  starts an attempt when one is due; it never waits for a connect.
- **Loki sink** (if enabled) runs its own send loop and never blocks the loggers or the
  sampling loop.

Startup order: the tray is created before the host starts, so the "connection failed"
notification handler is attached before the first attempt can fail. The UI language is
applied before any UI text exists.

Shutdown: **Exit** hides the icon and ends the message loop. `Program.Main` then stops
the host once. Services stop in reverse registration order: `MonitorService` first,
which cancels any in-flight connect and closes the MQTT client within
`MqttConnection.MaxShutdownDuration` (1 s), then the Loki sink, which gets up to
`ObservabilityService.ShutdownFlushBudget` (2 s) for a final flush. The host stop budget,
`Program.ShutdownTimeout`, is the sum of the two plus 1 s, so 4 s at most; a healthy
Exit takes milliseconds.

If the host fails to start, the tray icon is removed and the error is logged and shown
in a message box.

## Sampling and publishing

- CPU: `PerformanceCounter("Processor", "% Processor Time", "_Total")`.
- Memory: `GlobalMemoryStatusEx` memory load, plus used/total GB for the log and Loki.
- Each sample is logged at Debug level, so the default log does not grow every second.
- Payloads are formatted with one decimal in the invariant culture and clamped to
  `0.0..100.0`. They are sent with QoS 0, not retained.
- While sending is paused, sampling continues but connecting and publishing stop.

## Settings

### Layers

Settings come from these sources, lowest to highest priority:

1. built-in defaults;
2. `appsettings.json` next to the executable (shipped defaults);
3. `%LocalAppData%\ResourceMonitorDaemon\settings.json` (per-user; written by the tray
   dialogs and the pairing wizard);
4. environment variables prefixed `RESMON_`, with `__` between sections, e.g.
   `RESMON_Device__Host`. They are read only at startup ([how to set them](../README.md#configuration)).

The user file holds **only the values set there**, so anything not listed keeps coming
from `appsettings.json` or the defaults. Both JSON files are watched and changes apply
within about a second, without a restart. A new interval reschedules the next sample;
a host, fallback or port change drops the connection and reconnects immediately.

Invalid values never stop the daemon: they are replaced by the default or clamped, with
a warning in the log. A JSON file that cannot be parsed contributes nothing and is
reported in the log.

### Device settings

Section `Device`. No setting holds a Wi-Fi credential.

| Setting | Default | Rules |
| --- | --- | --- |
| `Host` | `resmon.local` | Host name or IP address |
| `FallbackAddress` | empty | Dotted IPv4 tried after `Host` fails; empty disables it. Written by the pairing wizard. |
| `Port` | `1883` | 1–65535 |
| `CpuTopic` | `resmon/cpu` | Non-empty, at most 128 characters, no `#` or `+`, no control characters or surrounding spaces |
| `MemTopic` | `resmon/mem` | Same rules; must differ from `CpuTopic` |
| `StatusTopic` | `resmon/status` | Same rules; must differ from both metric topics |
| `SampleIntervalMs` | `1000` | Clamped to 250–60000 |
| `ReconnectInitialBackoffMs` | `1000` | Clamped to 250–600000 |
| `ReconnectMaxBackoffMs` | `30000` | Clamped to 250–600000, at least the initial value |
| `ConnectTimeoutMs` | `3000` | Clamped to 500–30000 |

`Ui:Language` (`auto`, `en`, `zh-Hans`) and the `Observability:Loki` section are
described under [Localization](#localization) and in [loki.md](loki.md). Logging levels
use the standard `Logging` section.

### Writing the user file

`UserSettingsStore` is the only writer. Callers change it through a patch in which every
field is nullable (`null` means "not set here"):

```csharp
settingsStore.Update(patch => patch.FallbackAddress = "192.168.1.50");
```

- The set fields are validated before anything is written; writes are atomic
  (`settings.json.tmp`, then replace).
- Other sections and unknown keys are kept. Comments and trailing commas are accepted
  when reading, but comments are not preserved on save.
- A file that cannot be parsed is first copied to
  `settings.json.<yyyyMMdd-HHmmss>.bak` (never overwriting an earlier backup).
- No reload call is needed: the file watcher applies the change.

The settings and Observability dialogs save only the fields the user actually changed.
A changed value equal to defaults + `appsettings.json` is removed from the file instead
of copied. Fields set by `RESMON_` environment variables are shown read-only with
`(env)` and never written. Don't copy effective settings wholesale into the patch: that
would persist environment overrides and freeze `appsettings.json` values.

The settings dialog edits `Host`, `Port`, `CpuTopic`, `MemTopic`, `SampleIntervalMs` and
the language, and shows `FallbackAddress` read-only. The backoff and timeout values can
only be set in JSON or the environment.

## MQTT connection

`MqttConnection` owns the client, the connection state and the reconnect schedule.

- When disconnected and an attempt is due, it starts one background attempt: `Host`
  first, then `FallbackAddress` if set, each limited by `ConnectTimeoutMs`. While
  disconnected, publishing is skipped for that tick, so the effective minimum retry
  delay is one sample interval.
- The delay starts at `ReconnectInitialBackoffMs` and doubles per failure up to
  `ReconnectMaxBackoffMs`. A connect does not reset it by itself: only a connection that
  stays up for min(`ReconnectMaxBackoffMs`, 30 s) does. A broker that accepts and
  immediately drops connections therefore stays backed off.
- A failed publish marks the connection lost.
- Each outage shows one "MQTT connection failed" notification, and one "MQTT
  reconnected" notice when it recovers. The first failure is logged as a warning; later
  ones at Debug, with a summary every 20 attempts.
- The status topic is subscribed in every connect attempt (clean session). A topic
  change re-subscribes in the background without reconnecting. A failed subscription
  is logged and never affects publishing.
- `ConnectionGeneration` increases with every new connection; the pairing wizard uses
  it to tell a fresh connection from an old one.

Received status messages are validated and, if Loki is enabled, forwarded; see
[protocol.md](protocol.md#how-the-daemon-uses-it).

## Logging and diagnostics

- `%LocalAppData%\ResourceMonitorDaemon\daemon.log`, rolled to `daemon.log.1` at 5 MB.
  Write failures never affect the daemon. **View log** in the tray opens it.
- The level comes from the `Logging` section (Information by default);
  `RESMON_Logging__LogLevel__Default=Debug` (set before starting the daemon) logs every
  sample.
- At startup the daemon logs its version, the .NET runtime, the settings sources with
  file paths and the names of any `RESMON_` overrides, the effective settings, the log
  path, memory use, the UI language and the Loki configuration (host, auth mode and
  whether a token is set; never the token). Connection changes are logged as they
  happen.
- Pairing logs name the device and the result at Information level. The device's
  Bluetooth address and Wi-Fi IP address are logged only at Debug level; a saved IP
  address appears later as the configured `FallbackAddress` in the settings and
  connection lines.
- Log messages are always English, whatever the UI language.

## Optional Loki sink

User setup, settings and privacy are in [loki.md](loki.md). Internally:

```text
LokiLoggerProvider    ─┐
LokiMetricsForwarder  ─┼─► LokiEntryQueue ─► LokiSink loop ─► LokiPushClient ─► POST /loki/api/v1/push
DeviceStatusForwarder ─┘   (bounded,         (batching,       (HttpClient, gzip,
                            drop-oldest)      retry)            auth, 10 s timeout)
```

- **Sources never block.** They enqueue and return. The queue assigns strictly
  increasing nanosecond timestamps across the process. Lines over 16,384 characters are truncated
  with a marker.
- **No feedback loop.** The logger provider ignores the
  `ResourceMonitorDaemon.Observability.*` and `System.Net.Http.*` categories, so the
  sink's own messages and HTTP errors stay in the local log and can never re-enter the
  queue.
- **Live settings.** `ObservabilityService` (a hosted service registered before
  `MonitorService`, so it stops last) reconfigures the sink on every settings change.
- **Token storage.** The token is resolved from `RESMON_Observability__Loki__Token`
  first, then from `TokenProtected` in the user file: base64 DPAPI ciphertext, scope
  CurrentUser. Its fixed entropy only namespaces the value; since the code is public,
  the protection boundary is the Windows user account. The user-file patch
  (`UserLokiSettingsPatch`) has no plain-token property, so only the ciphertext can be
  written. The decrypted token exists only in memory (`LokiSettings.Token`);
  `ToString()` and `Describe()` never print it, and HTTP error text is scrubbed of the
  token, the Basic credential and the URL path before it is logged or shown.
- **Redaction.** `LokiLogRedactor` rewrites forwarded log lines (user folders, user name,
  PC name).

## Localization

The UI is in English (the neutral language) and Simplified Chinese.

- **Resources.** `Resources/Strings.resx` (English) and `Resources/Strings.zh-Hans.resx`.
  The SDK builds the translation into `zh-Hans/ResourceMonitorDaemon.resources.dll`.
- **Access.** `Localization/Strings.cs` has one member per key: a property for plain
  text, a method for a format string (`Strings.Pairing_StepSaved(address)`).
  `StringSet` has the same members bound to one culture. Keys are grouped by prefix:
  `Common_`, `Tray_`, `Settings_`, `Observability_`, `Pairing_`, `Provisioning_`.
- **Language choice.** `UiLanguage` resolves `Ui:Language`. `auto` maps any Simplified
  Chinese Windows display language (`zh-Hans`, `zh-CN`, `zh-SG`, bare `zh`) to
  `zh-Hans` and everything else, including Traditional Chinese, to English. Only the
  resource culture changes; thread cultures are left alone, so number formatting and
  the MQTT payloads are unaffected.
- **Live switching.** `UiLanguageMonitor` re-applies the setting on every configuration
  reload. The tray menu re-reads its texts at once. Each dialog captures a `StringSet`
  when it opens and keeps that language for everything it shows later, so a dialog never
  mixes languages.
- **Logs stay English.** Where a message is both logged and shown, the code carries a
  `UiMessage` (a key plus arguments, created only through typed factories in
  `UiMessage.Factories.cs`). The log gets the English text with configuration keys
  (`CpuTopic and MemTopic must be different.`); the dialog gets the localized text with
  field labels. Provisioning exceptions keep English messages for the log;
  `PairingOutcomePresenter` maps them to localized texts by type and code.
- **Not translated.** The product name, auth mode values (`None`/`Basic`/`Bearer`), the
  `(env)` marker, environment variable names, server responses and .NET exception
  details. Message box buttons are drawn by Windows in the Windows display language.

`LocalizationTests` keep this honest: both resx files must have the same keys and
placeholders, English values must not contain CJK text, translations must not use
bilingual labels, and a source scan fails on CJK text or English string literals passed
to UI sinks (`MessageBox.Show`, notifications, label helpers, `Text =` assignments and
similar) anywhere in `daemon/ResourceMonitorDaemon/**/*.cs` outside `Resources/`.
Comments are not scanned; a line marked `i18n-allow` is exempt.

To add a UI text, add the key to every resx file (with a placeholder comment in the
English one), then the matching member to `Strings.cs` and `StringSet.cs`, and, for a
text that is also logged, a factory in `UiMessage.Factories.cs`. To add a language:

1. Copy `Strings.zh-Hans.resx` to `Strings.<culture>.resx` and translate every value,
   keeping placeholders and technical tokens (`RESMON_…`, `(env)`, serial log lines).
2. Add the language to `UiLanguage` (constant, `Values`, culture, `Resolve`, and the
   `auto` mapping) and to the drop-down in `SettingsForm` (a `Settings_Language…` key
   with the language's own name in every file).
3. Extend `LocalizationTests` and check the dialogs for clipped text.

## Provisioning client

Namespace `ResourceMonitorDaemon.Provisioning`: the Windows side of
[provisioning.md](provisioning.md). The wire formats follow ESP-IDF v4.4.7 (the version
bundled with Arduino-ESP32 2.0.17) and its reference client, `esp_prov`.

| Type | Role |
| --- | --- |
| `ProvisioningService` | Facade for the wizard: `ScanAsync`, `TryReadPopFromUsbAsync`, `ProvisionAsync` with step progress |
| `BleDeviceScanner` | Active BLE scan; reports each `PROV_RESMON_*` device once, strongest first |
| `BleProvisioningTransport` | WinRT GATT client: finds the service, maps endpoints by their 0x2901 descriptor, writes each request and reads the response from the same characteristic |
| `IProvisioningTransport` | `SendAsync(endpoint, data, ct)` seam; tests use an in-memory fake device |
| `Security1Session` | X25519 key exchange, key derivation with the pairing code, AES-256-CTR, Command0/1 handshake |
| `WifiConfigClient` | GetStatus check → SetConfig → ApplyConfig → status polling; returns `Connected` (with IP, if usable), `Failed`, `Timeout` or `ConnectionLost` |
| `SerialPopReader`, `SerialPortFilter`, `PairingCode` | USB serial query (only on likely ESP32 ports, see [provisioning.md](provisioning.md#serial-pairing-code-query)) and pairing-code validation |
| `ProtoWriter`/`ProtoReader`, `Sec1Messages`, `WifiConfigMessages` | Minimal hand-written protobuf codec for these messages |

Errors are typed: `PairingCodeRejectedException`, `ProvisioningTransportException` (with
an `Error` kind such as Bluetooth off, device not found, timeout or GATT error),
`ProvisioningProtocolException`, and `ProvisioningRetryRejectedException` when the device
refuses new settings because an earlier attempt is still pending or failed.

Robustness and secrets:

- The protobuf reader checks tag ranges, wire types and lengths, so malformed input from
  any nearby device can only raise `ProvisioningProtocolException`. This is covered by
  crafted inputs and a seeded fuzz loop.
- An empty `GetStatus` reply is not treated as *Connected*, even though that is the
  protobuf default; the `connected` sub-message must be present.
- The Wi-Fi password arrives as a .NET string, which cannot be wiped, so the wizard
  drops its references right after `ProvisionAsync`. Byte copies made inside the
  library are zeroed after encryption, as are the pairing-code hash and the session
  key. The password, pairing code and SSID are never logged and are redacted from
  `ToString()`.

## Pairing wizard

Namespace `ResourceMonitorDaemon.Pairing`. `PairingWizardForm` is a two-column dialog
(it fits a 1080p screen at 150 % scaling) on top of `ProvisioningService`; only one
instance is open at a time. `PairingOutcomePresenter` holds the decisions and messages
as pure, unit-tested code.

- **Saving the address.** Only a usable unicast IPv4 address is saved, as
  `FallbackAddress`, and nothing else changes. Without one, the daemon keeps using
  `Host`.
- **Waiting for the daemon.** After saving, the wizard polls
  `MonitorService.IsConnected` and `ConnectionGeneration` for up to 45 s and counts only
  a connection made after the attempt started, so an older session that is still up
  doesn't count. The exception: if the device was already on Wi-Fi before the attempt
  and the effective `FallbackAddress` did not change, the daemon has no reason to
  reconnect, so an existing connection counts.
- **Lost Bluetooth link.** `BrokerProbe` checks for up to 15 s, every 1–3 s (each probe
  takes up to 2 s, followed by a 1 s pause), for a new daemon connection or a TCP connection to `Host:Port`.
- **Wi-Fi helpers.** "Use this PC's Wi-Fi" reads the connected SSID through WinRT only
  when clicked (it can trigger Windows 11's location prompt). The 2.4 GHz warning parses
  `netsh wlan show interfaces` (decoded with the OEM code page) and recognizes English
  and Chinese labels; otherwise no warning is shown.
- **Ambiguity.** `resmon.local` can resolve to a different monitor when several share a
  network; the wizard cannot tell them apart when no IP address was reported.

### First-run pairing prompt

On the daemon's very first run for a Windows user, the first "MQTT connection failed"
notification is replaced once by *No device found — click to pair*, which opens the
wizard. It is offered only if all of these hold:

- the marker file `%LocalAppData%\ResourceMonitorDaemon\pairing-prompted` does not exist
  (it is written when the prompt is shown and the first time the daemon connects to a
  broker);
- `Host` is the default `resmon.local` and `FallbackAddress` is empty;
- the daemon has not connected since it started;
- the daemon has not run before: no log from an earlier run (including older builds
  that wrote their log next to the executable).

The click only works while the notification is on screen; Windows does not report
clicks on notifications in the Action Center. If Windows suppresses the notification
(Focus Assist), the prompt is not repeated. To see it again, delete the marker and the
daemon logs. The wizard never opens by itself.

## Tests

Run them with:

```powershell
dotnet run --project daemon\ResourceMonitorDaemon.Tests\ResourceMonitorDaemon.Tests.csproj
```

The runner prints one line per test and a `passed` total. It sets the UI language to
English first. What is covered:

- **`Program.cs`** (the test runner's entry point): payload formatting, settings
  validation and layering, the user settings store, log rotation, reconnect backoff.
- **`MqttConnectionScenario.cs`**: `MqttConnection` against an in-process MQTTnet broker
  (non-blocking ticks, notifications, fallback address, wire format,
  connection generation).
- **`ObservabilityTests.cs`**: the Loki pipeline, including an end-to-end run against a
  loopback fake Loki, and the status subscription over an in-process broker.
- **`ProvisioningTests.cs`** with **`FakeProvisioningDevice.cs`**: protobuf encodings,
  X25519 (RFC 7748) and AES-CTR (NIST SP 800-38A) vectors, and full sessions against an
  in-memory device whose crypto uses different code paths from the client.
- **`PairingTests.cs`**: the wizard's decisions, the first-run rule, the `netsh` parser,
  `BrokerProbe`.
- **`MonitorServiceTests.cs`**: `MonitorService` end to end against an in-process broker
  (one message per topic per interval, pause and resume, interval changes), the
  shutdown budget, and the serial port filter.
- **`LocalizationTests.cs`**: see [Localization](#localization).

The wizard and dialog layouts are not unit-tested. If you change a layout, check the
dialog by eye in English and Simplified Chinese, at 100 % and 150 % display scaling.

## Limitations

- The Bluetooth client and the wizard are [not yet tested on hardware](provisioning.md):
  WinRT scanning and GATT, long writes, the real error codes on a wrong pairing code,
  the USB query on this board's USB-UART bridge, and BLE timing are unverified.
- Loki entries live only in memory: they are lost if the process is killed, and
  whatever is still queued after the shutdown flush budget is dropped.
- Only English and Simplified Chinese exist; Traditional Chinese Windows uses English.
