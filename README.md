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
- clamps values to the valid `0.0..100.0` range before publishing.

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
- payload: plain ASCII percentage, for example `42.5`
- transport: MQTT only
- QoS: 0 (at-most-once)

The ESP32 treats each metric independently and expires stale values after a five-second timeout before decaying toward zero.

## Key repository layout

- [platformio.ini](platformio.ini) — PlatformIO project configuration for the ESP32 firmware
- [firmware/src](firmware/src) — firmware source, Wi-Fi config, MQTT settings, and DAC logic
- [firmware/include](firmware/include) — shared firmware helpers such as metric conversion
- [daemon/ResourceMonitorDaemon](daemon/ResourceMonitorDaemon) — Windows daemon app
- [daemon/ResourceMonitorDaemon.Tests](daemon/ResourceMonitorDaemon.Tests) — zero-dependency tests for payload formatting, settings, backoff and MQTT connection handling
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
