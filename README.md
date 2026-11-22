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
- [daemon/ResourceMonitorDaemon.Tests](daemon/ResourceMonitorDaemon.Tests) — zero-dependency tests for payload formatting, settings, backoff, MQTT connection handling, the provisioning client and the pairing wizard's decisions
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
3. `%LocalAppData%\ResourceMonitorDaemon\settings.json` (per-user; written by the tray **⚙ 设置 (Settings)…** dialog and, for `FallbackAddress`, the pairing wizard);
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

### Pairing a device (Wi-Fi setup over Bluetooth)

The tray's **配对设备 (Pair device)…** wizard sends your Wi-Fi name and password to the ESP32 over an encrypted Bluetooth LE session. The session is protected by the device's 8-character pairing code. You need a PC with Bluetooth LE and firmware that implements the pairing contract (`PROVISIONING.md` on the firmware branch).

> Status: the wizard and the Bluetooth client are implemented and unit-tested, but they have **not yet been tested with a physical ESP32**.

1. **Put the device in pairing mode.** A device without Wi-Fi settings enters pairing mode on its own at start-up. To pair a device that is already configured, hold its **BOOT** button for 5–15 seconds, then release it. Holds longer than 15 s are ignored.
2. **Open the wizard.** Right-click the tray icon and choose **配对设备 (Pair device)…**. On the first start of a PC that has never connected to a device, the daemon shows its first "MQTT connection failed" notification once as **无法连接设备。未找到设备，点击进行配对 (Cannot reach the device. No device found — click to pair)**. Click it while it is on screen. The wizard never opens by itself.
3. **Select the device.** The wizard scans for 5 seconds and lists devices named `PROV_RESMON_XXXXXX` with their signal strength. If exactly one device is found, it is selected for you. Use **重新扫描 (Rescan)** if the device was not in pairing mode yet. If Bluetooth is off, the wizard says so; turn it on in Windows Settings and rescan.
4. **Pairing code.** If the device is connected to this PC by USB, the wizard reads the code automatically and shows **已通过 USB 自动读取配对码 (read automatically over USB)**, with the code masked. Close any serial monitor first, because a COM port can only be open in one program. Otherwise, type the code. It is printed in the device's serial log at 115200 baud, in a line like `Provisioning: name=PROV_RESMON_XXXXXX pop=ABCDEFGH`. Codes use letters and the digits 2–9 (no 0, O, 1 or I), and case, spaces and dashes are ignored.
5. **Wi-Fi.** Enter the network name (SSID) and password. To copy the network this PC is connected to, click **使用本机当前 Wi-Fi (Use this PC's Wi-Fi)**. The first time, Windows 11 may ask whether apps may use your location, because Windows treats Wi-Fi details as location data. If you decline, type the name instead. The ESP32 supports **2.4 GHz only**. If your PC is connected on 5 GHz, the wizard warns you; make sure the router also offers the network on 2.4 GHz.
6. **Click 开始配对 (Pair).** The wizard shows each step: connect, secure session, send Wi-Fi settings, wait for Wi-Fi. **取消 (Cancel)** stops the attempt, and closing the wizard cancels everything.
7. **Done.** When the device reports its IP address, the wizard saves it as `FallbackAddress` in `%LocalAppData%\ResourceMonitorDaemon\settings.json`; no other setting is changed. The daemon then reconnects without a restart. The wizard finishes with **已连接，正在发送数据 (connected, publishing)** once metrics are being sent.

The Wi-Fi password is used only for that attempt. The password box is cleared afterwards, and the password is never written to disk or to the log. If an attempt fails, the wizard tells you what to fix:

| Message | What to do |
| --- | --- |
| Wi-Fi 密码错误 (wrong Wi-Fi password) | Re-enter the password and try again. |
| 找不到该 Wi-Fi（ESP32 仅支持 2.4 GHz） (network not found) | Check the SSID and that the router offers 2.4 GHz. |
| 配对码错误 (pairing code rejected) | Check the code in the serial log and try again. |
| No result within 30 s | Check the SSID, password and 2.4 GHz, then try again. A retry also detects a device that joined Wi-Fi late. |
| Lost the Bluetooth link | The wizard checks `resmon.local` for 15 s first. If the device is unreachable, rescan, or power-cycle the device or hold BOOT for 5–15 s, then pair again. |
| The device did not accept another attempt | Power-cycle the device or hold BOOT for 5–15 s and release, then rescan and pair. |

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
