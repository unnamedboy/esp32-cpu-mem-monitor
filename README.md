# ESP32 CPU/MEM Monitor

This repository implements a working prototype for a Windows-to-ESP32 resource monitor.
The Windows daemon samples CPU and memory usage, publishes MQTT payloads, and the ESP32 hosts the broker and drives two DAC outputs for a live analogue display.

## What is implemented today

### Windows daemon

The .NET app in [daemon/ResourceMonitorDaemon](daemon/ResourceMonitorDaemon) currently does the following:

- samples local CPU usage with Windows Performance Counters;
- reads total/used RAM via the Windows memory API;
- formats percentages in a stable one-decimal invariant format;
- publishes values to the ESP32 MQTT broker (default `resmon.local:1883`);
- sends metrics on `resmon/cpu` and `resmon/mem` by default;
- reads host, port, topics and sampling interval from layered configuration and applies changes without restart;
- reconnects in the background with bounded exponential backoff, without stalling sampling, and can try a fallback IPv4 address;
- includes a tray UI with pause/resume, a settings dialog, a log viewer and once-per-outage MQTT notifications;
- includes a pairing wizard that sets up the ESP32's Wi-Fi over Bluetooth LE and remembers the device's IP address (not yet tested on real hardware);
- clamps values to the valid `0.0..100.0` range before publishing;
- subscribes to the optional device status topic `resmon/status` (published by newer firmware);
- can optionally forward its log, a metrics line and the device status to your own Grafana Loki (off by default; see [Optional: Grafana Loki](#optional-grafana-loki)).

### ESP32 firmware

The firmware in [firmware/src/main.cpp](firmware/src/main.cpp) currently does the following:

- connects to Wi-Fi as a station;
- advertises the device via mDNS as `resmon.local`;
- hosts a small MQTT broker with PicoMQTT on port 1883;
- subscribes to `resmon/cpu` and `resmon/mem`;
- validates incoming payloads and ignores malformed or out-of-range values;
- updates two DAC channels for CPU and memory output;
- fades stale metrics back toward zero after the configured timeout;
- logs startup diagnostics including build id, I2C/DAC settings, free heap, and connection state.

### Shared configuration and contract

- MQTT contract: [COMMUNICATION.md](COMMUNICATION.md)
- Hardware design/status: [HARDWARE.md](HARDWARE.md)
- roadmap and future phases: [DEVELOPMENT_PLAN.md](DEVELOPMENT_PLAN.md)
- firmware configuration: [firmware/src/config.h](firmware/src/config.h)

## Runtime contract

- broker host: `resmon.local` (default, configurable)
- broker port: `1883` (default, configurable)
- CPU topic: `resmon/cpu` (default, configurable)
- memory topic: `resmon/mem` (default, configurable)
- optional device status topic: `resmon/status` (ESP32 → daemon JSON, see [COMMUNICATION.md](COMMUNICATION.md))
- payload: plain ASCII percentage, for example `42.5`
- transport: MQTT only
- QoS: 0 (at-most-once)

The ESP32 treats each metric independently and expires stale values after a five-second timeout before decaying toward zero.

## Key repository layout

- [platformio.ini](platformio.ini) — PlatformIO project configuration for the ESP32 firmware
- [firmware/src](firmware/src) — firmware source, Wi-Fi config, MQTT settings, and DAC logic
- [firmware/include](firmware/include) — shared firmware helpers such as metric conversion
- [daemon/ResourceMonitorDaemon](daemon/ResourceMonitorDaemon) — Windows daemon app
- [daemon/ResourceMonitorDaemon.Tests](daemon/ResourceMonitorDaemon.Tests) — zero-dependency tests for payload formatting, settings, backoff, MQTT connection handling, the provisioning client, the pairing wizard's decisions and the optional Loki sink
- [assets/SQ52_CPU_MEM_1to1.svg](assets/SQ52_CPU_MEM_1to1.svg) — front-panel sticker asset for a CPU/MEM gauge face, intended as a replacement for a generic voltmeter face

## Local setup

### Firmware secrets

Real Wi-Fi credentials must not be committed to git.

Create a local secrets file from the example:

```powershell
Copy-Item firmware\src\secrets.example.h firmware\src\secrets.h
```

Then edit [firmware/src/secrets.h](firmware/src/secrets.h) locally with your SSID and password:

```cpp
const char* WIFI_SSID = "your-wifi-ssid";
const char* WIFI_PASSWORD = "your-wifi-password";
```

### Daemon configuration

The daemon reads non-secret settings from these sources, lowest to highest priority:

1. built-in defaults;
2. `appsettings.json` next to `ResourceMonitorDaemon.exe` (shipped defaults);
3. `%LocalAppData%\ResourceMonitorDaemon\settings.json` (per-user; written by the tray **⚙ Settings… (设置…)** dialog and, for `FallbackAddress`, the pairing wizard);
4. environment variables with the `RESMON_` prefix (`__` separates sections).

The user settings file contains only the values you set there; anything not listed keeps coming from `appsettings.json` or the defaults. Example:

```json
{
  "Device": {
    "Host": "resmon.local",
    "FallbackAddress": "192.168.1.50",
    "Port": 1883,
    "CpuTopic": "resmon/cpu",
    "MemTopic": "resmon/mem",
    "SampleIntervalMs": 1000
  }
}
```

Environment variable examples (PowerShell, then start the daemon from the same shell):

```powershell
$env:RESMON_Device__Host = "192.168.1.50"
$env:RESMON_Device__Port = "1883"
$env:RESMON_Device__SampleIntervalMs = "500"
$env:RESMON_Logging__LogLevel__Default = "Debug"   # log every sample
```

Edits to either JSON file, or a Save from the tray dialog, are applied while the daemon runs; a host or port change reconnects automatically.
Fields set by `RESMON_` environment variables are shown read-only in the dialog and are never written to the user settings file.
Invalid values are replaced by defaults (or clamped) with a warning in the log rather than stopping the daemon.
The daemon never stores Wi-Fi credentials.

The log is written to `%LocalAppData%\ResourceMonitorDaemon\daemon.log` (rolled to `daemon.log.1` at 5 MB) and can be opened from the tray menu.
See [daemon/IMPLEMENTATION_SUMMARY.md](daemon/IMPLEMENTATION_SUMMARY.md) for all settings, validation rules and the settings API.

### Language

The tray menu, notifications and dialogs are available in English and Simplified Chinese (简体中文). The setting `Ui:Language` chooses the language:

| Value | Language |
| --- | --- |
| `auto` (default) | Follows the Windows display language: Simplified Chinese for `zh-Hans`, `zh-CN` and `zh-SG`, English for everything else (Traditional Chinese has no translation yet and also uses English). |
| `en` | English |
| `zh-Hans` | Simplified Chinese |

Change it with the **Language** drop-down in the tray **⚙ Settings…** dialog. It saves `"Ui": { "Language": "zh-Hans" }` in the user settings file; choosing the value that `appsettings.json` already sets (normally `auto`) removes the entry again. To force a language without touching the file, set `RESMON_Ui__Language` (for example `$env:RESMON_Ui__Language = "en"`); the drop-down is then read-only. An invalid value falls back to `auto` with a warning in the log.

The tray menu switches as soon as the setting changes; notifications and windows opened afterwards use the new language. A dialog that is already open keeps its labels. The log (`daemon.log` and the optional Loki sink) is always in English, so searches and LogQL queries do not depend on the UI language. Windows itself draws the Yes/No/OK buttons of message boxes in the Windows display language.

### Pairing a device (Wi-Fi setup over Bluetooth)

The tray's **Pair device… (配对设备…)** wizard sends your Wi-Fi name and password to the ESP32 over an encrypted Bluetooth LE session. The session is protected by the device's 8-character pairing code. You need a PC with Bluetooth LE and firmware that implements the pairing contract (`PROVISIONING.md` on the firmware branch).

> Status: the wizard and the Bluetooth client are implemented and unit-tested, but they have **not yet been tested with a physical ESP32**.

1. **Put the device in pairing mode.** A device without Wi-Fi settings enters pairing mode on its own at start-up. To pair a device that is already configured, hold its **BOOT** button for 5–15 seconds, then release it. Holds longer than 15 s are ignored.
2. **Open the wizard.** Right-click the tray icon and choose **Pair device… (配对设备…)**. On the first start of a PC that has never connected to a device, the daemon shows its first "MQTT connection failed" notification once as **Cannot reach the device. No device found — click to pair.** (无法连接设备。未找到设备，点击进行配对。). Click it while it is on screen. The wizard never opens by itself.
3. **Select the device.** The wizard scans for 5 seconds and lists devices named `PROV_RESMON_XXXXXX` with their signal strength. If exactly one device is found, it is selected for you. Use **Rescan (重新扫描)** if the device was not in pairing mode yet. If Bluetooth is off, the wizard says so; turn it on in Windows Settings and rescan.
4. **Pairing code.** If the device is connected to this PC by USB, the wizard reads the code automatically and shows **Read automatically over USB.** (已通过 USB 自动读取配对码。), with the code masked. Close any serial monitor first, because a COM port can only be open in one program. Otherwise, type the code. It is printed in the device's serial log at 115200 baud, in a line like `Provisioning: name=PROV_RESMON_XXXXXX pop=ABCDEFGH`. Codes use letters and the digits 2–9 (no 0, O, 1 or I), and case, spaces and dashes are ignored.
5. **Wi-Fi.** Enter the network name (SSID) and password. To copy the network this PC is connected to, click **Use this PC's Wi-Fi (使用本机当前 Wi-Fi)**. The first time, Windows 11 may ask whether apps may use your location, because Windows treats Wi-Fi details as location data. If you decline, type the name instead. The ESP32 supports **2.4 GHz only**. If your PC is connected on 5 GHz, the wizard warns you; make sure the router also offers the network on 2.4 GHz.
6. **Click Pair (开始配对).** The wizard shows each step: connect, secure session, send Wi-Fi settings, wait for Wi-Fi. **Cancel (取消)** stops the attempt, and closing the wizard cancels everything.
7. **Done.** When the device reports its IP address, the wizard saves it as `FallbackAddress` in `%LocalAppData%\ResourceMonitorDaemon\settings.json`; no other setting is changed. The daemon then reconnects without a restart. The wizard finishes with **Connected, publishing** (已连接，正在发送数据) once metrics are being sent.

The Wi-Fi password is used only for that attempt. The password box is cleared afterwards, and the password is never written to disk or to the log. If an attempt fails, the wizard tells you what to fix:

| Message | What to do |
| --- | --- |
| Wrong Wi-Fi password (Wi-Fi 密码错误) | Re-enter the password and try again. |
| Wi-Fi network not found (找不到该 Wi-Fi（ESP32 仅支持 2.4 GHz）) | Check the SSID and that the router offers 2.4 GHz. |
| Wrong pairing code (配对码错误) | Check the code in the serial log and try again. |
| No result within 30 s | Check the SSID, password and 2.4 GHz, then try again. A retry also detects a device that joined Wi-Fi late. |
| Lost the Bluetooth link | The wizard checks `resmon.local` for 15 s first. If the device is unreachable, rescan, or power-cycle the device or hold BOOT for 5–15 s, then pair again. |
| The device did not accept another attempt | Power-cycle the device or hold BOOT for 5–15 s and release, then rescan and pair. |

### Optional: Grafana Loki

The daemon can push its own log, a metrics line and the ESP32 status (`resmon/status`) to **your own** Grafana Loki. This is off by default. The repository holds no URL, tenant or token: you enter them yourself.

1. Get a Loki push endpoint:
   - **Grafana Cloud**: in your stack's Loki details, use the URL (for example `https://logs-prod-xxx.grafana.net`), the numeric **user/instance ID** as Username, and an access-policy token with the `logs:write` scope. Use AuthMode `Basic`.
   - **Self-hosted Loki**: for example `http://loki:3100`, usually AuthMode `None`. Set TenantId only if multi-tenancy is enabled (sent as `X-Scope-OrgID`). If you use credentials, use `https`; the dialog warns before it saves credentials for a plain-`http` URL.
2. Open the tray menu **Observability… (可观测性…)**, tick **Enabled**, and fill in Url, Auth mode, Username and Token. Then click **Test (测试连接)**. It pushes one test line and shows the HTTP result, for example `HTTP 204 No Content`.
3. Click **Save (保存)**. The change takes effect immediately, without a restart. Disabling the sink stops collection and discards anything still queued.

`/loki/api/v1/push` is appended to the Url only when it has no path, so a full push URL or a reverse-proxy path also works.

The token is saved only as Windows DPAPI ciphertext (`TokenProtected`, current Windows user only) in `%LocalAppData%\ResourceMonitorDaemon\settings.json`. Leaving the token box empty keeps the saved token, and **Clear (清除)** removes it. For automation, set `RESMON_Observability__Loki__Token`. That variable takes precedence and is never written to disk. A plain `Token` key in a JSON file is ignored with a warning. The daemon never logs the token, the `Authorization` header or the URL path. Its startup line shows only whether Loki is enabled, the URL host, the auth mode and whether a token is configured.

Settings (section `Observability:Loki`; environment form `RESMON_Observability__Loki__<Name>`):

| Setting | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `false` | Master switch |
| `Url` | empty | Loki base URL or full push URL (no user info) |
| `AuthMode` | `None` | `None`, `Basic` (Username + token) or `Bearer` (token) |
| `Username` | empty | Basic-auth user, for example the Grafana Cloud instance ID |
| `TenantId` | empty | Optional `X-Scope-OrgID` |
| `IncludeHostLabel` | `false` | Adds `host="<PC name>"` to every stream |
| `MetricsIntervalSeconds` | `10` | One metrics line per interval; `0` turns it off |
| `MinLevel` | `Information` | Minimum daemon log level forwarded (the normal `Logging` filter applies first) |
| `BatchMaxEntries` / `QueueCapacity` / `FlushIntervalMs` | `500` / `5000` / `2000` | Batching. The queue is bounded: when it is full, the oldest entries are dropped and the drops are reported in the local log. |
| `Gzip` | `true` | Sends the request body with `Content-Encoding: gzip` |

Streams and labels are listed in [COMMUNICATION.md](COMMUNICATION.md#optional-daemon--grafana-loki-streams). What is sent:

- daemon log lines at or above `MinLevel`, redacted before they are sent:
  - per-user folders become `%LOCALAPPDATA%`, `%APPDATA%` or `%USERPROFILE%`, including their short 8.3 forms such as `C:\Users\ABCDEF~1`;
  - the profile folder in any other absolute path (`C:\Users\<name>\…` or `\\server\Users\<name>\…`, either slash, spaces allowed) becomes `<user>`. The shared folders `Public`, `Default`, `Default User` and `All Users` are kept, and relative paths and URLs are not touched;
  - the Windows user name becomes `<user>`. This is a whole-word match, so if your account name is a common word (for example `admin`), that word is replaced in ordinary text too, e.g. "Run as <user>". Privacy wins over readability here;
  - the PC name becomes `<host>`, unless you enabled `IncludeHostLabel`.

  Apart from this, the lines are the same text as `daemon.log` and can contain the broker host or IP address you configured.
- CPU and memory percentages, memory used/total in GB, and the MQTT state.
- the device status, with only the documented fields and at most one message every 5 s. It contains no SSID, password, IP or MAC address.

Messages from the sink itself, for example failed pushes, stay in the local log only. The sink never blocks or slows sampling or MQTT publishing. Failed pushes are retried with backoff up to 60 s, and on Exit the daemon spends at most 2 s flushing.
During an outage (network errors, timeouts, 5xx) the daemon keeps retrying indefinitely. Nothing is given up; entries are lost only if the bounded queue (`QueueCapacity`) overflows, which drops the oldest entries and reports it in the local log. Entries older than Loki's own limit (`reject_old_samples_max_age`, about a week by default) are rejected by Loki with a 400, which drops that batch. For a rejected batch, Loki usually stores the valid entries and reports `total ignored: N out of M`; the daemon then counts only those N as dropped. If Loki does not say, the local log says the batch was rejected and that Loki may have accepted some entries.

A batch is at most 1 MiB, measured as raw UTF-8 JSON. A batch that is too large (413), or that is throttled (429) three times, is split in half; splitting never loses data. A single entry that still gets 413, or still gets 429 without Retry-After three times, is dropped and counted in the local log.

Example LogQL queries (Grafana Explore):

```logql
# Average CPU % over 5 minutes
avg_over_time({app="resmon-daemon",kind="metrics"} | json | unwrap cpu [5m])

# Peak memory %
max_over_time({app="resmon-daemon",kind="metrics"} | json | unwrap mem [5m])

# Fraction of metrics samples (one per MetricsIntervalSeconds) in the last hour with the ESP32 broker unreachable
sum(count_over_time({app="resmon-daemon",kind="metrics"} | json | mqtt="disconnected" [1h]))
  / sum(count_over_time({app="resmon-daemon",kind="metrics"} [1h]))

# Daemon warnings and errors
{app="resmon-daemon",kind="log",level=~"warn|error|critical"}

# Device free heap (lowest in 15 minutes) and Wi-Fi signal
min_over_time({app="resmon-device",kind="status"} | json | unwrap heap_free [15m])
avg_over_time({app="resmon-device",kind="status"} | json | unwrap rssi [5m])

# Device restarts (uptime reset) with firmware build
{app="resmon-device",kind="status"} | json | uptime_s < 60 | line_format "{{.build}} heap={{.heap_free}}"
```

### Build and test

C# daemon tests:

```powershell
dotnet run --project daemon\ResourceMonitorDaemon.Tests\ResourceMonitorDaemon.Tests.csproj
```

PlatformIO firmware build/tests:

```powershell
platformio test
```

## Current status and caveats

This project is no longer in the “early concept” stage. The current prototype already has:

- a working Windows-side metrics publisher;
- an ESP32-side MQTT broker and DAC controller;
- topic-based communication and payload validation;
- stale metric decay and safe DAC clamping.

Known limitations still present:

- firmware Wi-Fi setup is still static/local rather than a first-run provisioning flow;
- the daemon's pairing wizard and Bluetooth provisioning client have not yet been tested against a physical ESP32;
- hardware wiring and enclosure decisions are still tracked in the design docs rather than being treated as final production assembly;
- the project is still a prototype and should be considered “functional lab build” rather than a finished product.

## Documentation

For the exact contract, hardware assumptions, and roadmap, see:

- [COMMUNICATION.md](COMMUNICATION.md)
- [HARDWARE.md](HARDWARE.md)
- [DEVELOPMENT_PLAN.md](DEVELOPMENT_PLAN.md)
- [daemon/IMPLEMENTATION_SUMMARY.md](daemon/IMPLEMENTATION_SUMMARY.md)
