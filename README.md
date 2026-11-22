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
- [daemon/ResourceMonitorDaemon.Tests](daemon/ResourceMonitorDaemon.Tests) — zero-dependency tests for payload formatting, settings, backoff, MQTT connection handling and the optional Loki sink
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
3. `%LocalAppData%\ResourceMonitorDaemon\settings.json` (per-user; written by the tray **⚙ 设置 (Settings)…** dialog);
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

### Optional: Grafana Loki

The daemon can push its own log, a metrics line and the ESP32 status (`resmon/status`) to **your own** Grafana Loki. This is off by default. The repository holds no URL, tenant or token: you enter them yourself.

1. Get a Loki push endpoint:
   - **Grafana Cloud**: in your stack's Loki details, use the URL (for example `https://logs-prod-xxx.grafana.net`), the numeric **user/instance ID** as Username, and an access-policy token with the `logs:write` scope. Use AuthMode `Basic`.
   - **Self-hosted Loki**: for example `http://loki:3100`, usually AuthMode `None`. Set TenantId only if multi-tenancy is enabled (sent as `X-Scope-OrgID`). If you use credentials, use `https`; the dialog warns before it saves credentials for a plain-`http` URL.
2. Open the tray menu **可观测性 (Observability)…**, tick **Enabled**, and fill in Url, Auth mode, Username and Token. Then click **测试连接 (Test)**. It pushes one test line and shows the HTTP result, for example `HTTP 204 No Content`.
3. Click **保存 (Save)**. The change takes effect immediately, without a restart. Disabling the sink stops collection and discards anything still queued.

`/loki/api/v1/push` is appended to the Url only when it has no path, so a full push URL or a reverse-proxy path also works.

The token is saved only as Windows DPAPI ciphertext (`TokenProtected`, current Windows user only) in `%LocalAppData%\ResourceMonitorDaemon\settings.json`. Leaving the token box empty keeps the saved token, and **清除 (Clear)** removes it. For automation, set `RESMON_Observability__Loki__Token`. That variable takes precedence and is never written to disk. A plain `Token` key in a JSON file is ignored with a warning. The daemon never logs the token, the `Authorization` header or the URL path. Its startup line shows only whether Loki is enabled, the URL host, the auth mode and whether a token is configured.

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
  - per-user folders become `%LOCALAPPDATA%`, `%APPDATA%` or `%USERPROFILE%`;
  - the Windows user name becomes `<user>`;
  - the PC name becomes `<host>`, unless you enabled `IncludeHostLabel`.

  Apart from this, the lines are the same text as `daemon.log` and can contain the broker host or IP address you configured.
- CPU and memory percentages, memory used/total in GB, and the MQTT state.
- the device status, with only the documented fields and at most one message every 5 s. It contains no SSID, password, IP or MAC address.

Messages from the sink itself, for example failed pushes, stay in the local log only. The sink never blocks or slows sampling or MQTT publishing. Failed pushes are retried with backoff up to 60 s, and on Exit the daemon spends at most 2 s flushing.
A batch is at most 1 MiB and is given up after 8 attempts or 10 minutes. The given-up entries are dropped and counted in the local log.

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
- the daemon has a settings dialog but no first-run pairing wizard yet;
- hardware wiring and enclosure decisions are still tracked in the design docs rather than being treated as final production assembly;
- the project is still a prototype and should be considered “functional lab build” rather than a finished product.

## Documentation

For the exact contract, hardware assumptions, and roadmap, see:

- [COMMUNICATION.md](COMMUNICATION.md)
- [HARDWARE.md](HARDWARE.md)
- [DEVELOPMENT_PLAN.md](DEVELOPMENT_PLAN.md)
- [daemon/IMPLEMENTATION_SUMMARY.md](daemon/IMPLEMENTATION_SUMMARY.md)
