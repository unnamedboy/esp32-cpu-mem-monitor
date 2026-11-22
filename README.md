# ESP32 CPU/MEM Monitor

Analogue panel gauges that show your Windows PC's CPU and memory usage. A small Windows
tray app samples both values every second and sends them over Wi-Fi (MQTT) to an ESP32,
which drives two needle gauges through an I2C DAC. When the PC stops sending, the needles
fall back to zero on their own.

```text
 Windows PC                          Wi-Fi / MQTT                 ESP32
+---------------------------+   resmon/cpu, resmon/mem   +-----------------------+
| ResourceMonitorDaemon     | -------------------------> | MQTT broker           |
| (tray app)                |                            |   -> I2C DAC          |
|                           | <------------------------- |   -> CPU gauge        |
|                           |   resmon/status (health)   |   -> MEM gauge        |
+---------------------------+                            +-----------------------+
```

This is a working prototype, not a finished product; see [Status](#status-and-limitations).

## Features

- CPU and memory usage on two analogue gauges, updated every second.
- The ESP32 runs its own MQTT broker and is found as `resmon.local`; no server needed.
- Wi-Fi setup over Bluetooth LE from a pairing wizard in the tray app, encrypted and
  protected by an 8-character pairing code. No Wi-Fi password in the firmware.
- Gauges fall to zero when data stops; out-of-range or malformed values are ignored.
- Settings dialog, live configuration reload, automatic reconnects.
- Optional forwarding of logs, metrics and device health to your own Grafana Loki.
- English and Simplified Chinese UI.

## What you need

- An ESP32-WROOM-32 dev board (the prototype uses a MOC9686 expansion shield), powered
  over USB. A GP8413-compatible I2C DAC and two analogue panel gauges for the display;
  the firmware also runs without them. See [docs/hardware.md](docs/hardware.md) for
  parts, wiring and **safety notes** (the shield's `DC IN` takes 6–9 V only, GPIO is
  3.3 V).
- A 2.4 GHz Wi-Fi network shared by the PC and the ESP32.
- A Windows 10 (2004 or later) or Windows 11 PC with Bluetooth LE for pairing.
- [PlatformIO](https://platformio.org/) to build the firmware and the
  [.NET SDK](https://dotnet.microsoft.com/) 9 or later to build the daemon.

## Quick start

### 0. Wiring (optional)

There is no verified wiring guide for the DAC and gauges yet. What is fixed: the DAC is
a GP8413-compatible module (DFRobot GP8XXX library) at I2C address `0x59`, on the I2C
bus at GPIO21 (SDA) and GPIO22 (SCL). The firmware runs fine with nothing attached, so
you can start with just the board on USB. Before wiring anything, read the
[connection table](docs/hardware.md#connections) and the
[safety notes](docs/hardware.md#electrical-safety).

### 1. Flash the firmware

From the repository root, with the board on USB (`pio` is the PlatformIO CLI; in VS Code,
use a PlatformIO terminal). If no COM port appears for the board, install the driver
for its USB-serial chip (usually CP210x or CH340):

```sh
pio run -e esp32dev -t upload
pio device monitor -e esp32dev
```

The serial monitor (115200 baud) shows the startup diagnostics. A board without Wi-Fi
settings goes straight into pairing mode and prints its pairing code:

```text
Provisioning: name=PROV_RESMON_A1B2C3 pop=ABCDEFGH
```

Close the serial monitor before pairing, so the wizard can read the code over USB.
If the board previously ran older firmware of this project, it will reuse that Wi-Fi
network instead; see [upgrading](docs/provisioning.md#upgrading-a-board-that-ran-older-firmware).

### 2. Build and start the daemon

```powershell
dotnet build daemon\ResourceMonitorDaemon.sln -c Release
daemon\ResourceMonitorDaemon\bin\Release\net9.0-windows10.0.19041.0\ResourceMonitorDaemon.exe
```

The app lives in the notification area. There is no installer yet; to start it with
Windows, put a shortcut to the exe in your Startup folder.

### 3. Pair the device

Right-click the tray icon, choose **Pair device…**, pick the
`PROV_RESMON_…` device, check the pairing code, enter your Wi-Fi name and password, and
click **Pair**. When the device has joined Wi-Fi, the wizard saves its IP address and
the daemon connects; the gauges start moving.

To re-pair later or switch networks, hold the board's **BOOT** button for 5–15 s while it
is running, then release it. Details and troubleshooting:
[docs/provisioning.md](docs/provisioning.md#pairing-a-device).

## Configuration

Most people only need the tray's **⚙ Settings…** dialog. Settings come from
these layers, lowest to highest priority:

1. built-in defaults;
2. `appsettings.json` next to the exe;
3. `%LocalAppData%\ResourceMonitorDaemon\settings.json`, written by the dialogs and the
   pairing wizard and holding only the values you changed;
4. environment variables starting with `RESMON_` (`__` separates sections).

| Setting | Default | |
| --- | --- | --- |
| `Device:Host` | `resmon.local` | Broker host name or IP |
| `Device:FallbackAddress` | empty | IPv4 tried when `Host` fails; set by the pairing wizard |
| `Device:Port` | `1883` | Broker port |
| `Device:CpuTopic`, `Device:MemTopic` | `resmon/cpu`, `resmon/mem` | Must match the firmware |
| `Device:SampleIntervalMs` | `1000` | 250–60000 |
| `Ui:Language` | `auto` | `auto`, `en` or `zh-Hans` |

Changes apply while the daemon runs; a host or port change reconnects automatically.
Invalid values fall back to defaults with a warning in the log. Values set through
environment variables are shown read-only in the dialog and are never saved:

```powershell
$env:RESMON_Device__Host = "192.168.1.50"
$env:RESMON_Logging__LogLevel__Default = "Debug"   # log every sample
```

All settings, including reconnect timing: [docs/daemon.md](docs/daemon.md#settings).
The firmware's pins, topics and timings are compile-time settings in
[`firmware/src/config.h`](firmware/src/config.h).

The log is `%LocalAppData%\ResourceMonitorDaemon\daemon.log` (rolled at 5 MB); open it
with **View log** in the tray menu.

### Optional: Grafana Loki

The daemon can push its log, a metrics line every 10 s and the device's health messages
to your own Loki (Grafana Cloud or self-hosted). It is off by default; enable it under
**Observability…** in the tray menu. Setup, privacy and example queries:
[docs/loki.md](docs/loki.md).

### Language

The tray menu, notifications and dialogs are available in English and Simplified
Chinese. With `auto` the daemon follows the Windows display language: Simplified
Chinese display languages get the Chinese UI, everything else (including Traditional
Chinese) gets English. Change it with **Language** in the settings dialog, or force it with
`RESMON_Ui__Language`. The menu switches immediately; dialogs that are already open keep
their language. The log is always English.

The documentation uses the English labels; the Chinese UI has the same layout and menu
order.

## Troubleshooting

| Symptom | Things to check |
| --- | --- |
| "MQTT connection failed" notification | Is the ESP32 powered and on the same network? Does `resmon.local` resolve on this PC? Pairing saves the device's IP as a fallback; you can also set **Host** to the IP. The daemon keeps retrying in the background. |
| Gauges sink to zero | No valid data for 5 s: sending is paused (tray menu), the daemon is not running, or the connection dropped. |
| Device doesn't show up in the wizard | It is not in pairing mode: use the BOOT gesture (hold 5–15 s, release) and **Rescan**. Check that Bluetooth is on. |
| Pairing code not read automatically | Close any serial monitor; the COM port can only be open in one program. Auto-read only works with common ESP32 USB-serial chips (CP210x, CH340/CH343/CH9102, FTDI, Espressif native USB). Otherwise type the code from the serial log. |
| Pairing fails | See the [message table](docs/provisioning.md#if-pairing-fails). Remember the ESP32 needs 2.4 GHz. |
| Serial log says `DAC not found at 0x59` | Check the DAC wiring and address; the firmware runs on without outputs. |
| Firmware build stops with `CORE_DEBUG_LEVEL >= 3 is refused` | Use level 1 (the default in `platformio.ini`) or 2. The build refuses 3 and higher on purpose; see [why](docs/firmware.md#toolchain). |

For anything else, open the log from the tray menu, and set
`RESMON_Logging__LogLevel__Default=Debug` for more detail.

## Project layout

| Path | Contents |
| --- | --- |
| [`platformio.ini`](platformio.ini) | PlatformIO project for the firmware |
| [`firmware/src`](firmware/src) | Firmware (`main.cpp`) and its configuration (`config.h`) |
| [`firmware/include`](firmware/include) | Pure, unit-tested firmware logic |
| [`firmware/test`](firmware/test) | Unity tests for `firmware/include` |
| [`daemon/ResourceMonitorDaemon`](daemon/ResourceMonitorDaemon) | Windows tray app |
| [`daemon/ResourceMonitorDaemon.Tests`](daemon/ResourceMonitorDaemon.Tests) | Daemon tests (console runner) |
| [`assets/SQ52_CPU_MEM_1to1.svg`](assets/SQ52_CPU_MEM_1to1.svg) | CPU/MEM gauge face to print for a square panel voltmeter |
| [`docs`](docs) | Documentation |

## Building and testing

```powershell
# Daemon: build and run the tests (prints "N/N passed")
dotnet build daemon\ResourceMonitorDaemon.sln
dotnet run --project daemon\ResourceMonitorDaemon.Tests\ResourceMonitorDaemon.Tests.csproj

# Firmware: build, then compile the tests without a board
pio run -e esp32dev
pio test -e esp32dev --without-uploading --without-testing

# Firmware tests on a connected board
pio test -e esp32dev
```

The firmware is pinned to `espressif32 @ 7.0.1` (Arduino-ESP32 2.0.17). See
[docs/contributing.md](docs/contributing.md) for conventions and the checks to run
before a pull request.

## Documentation

- [Architecture](docs/architecture.md): how the pieces fit together and why
- [MQTT protocol](docs/protocol.md): topics, payloads, device status
- [Provisioning and pairing](docs/provisioning.md): pairing, security model, BLE protocol
- [Hardware](docs/hardware.md): parts, wiring, safety, enclosure
- [Firmware internals](docs/firmware.md)
- [Daemon internals](docs/daemon.md)
- [Grafana Loki](docs/loki.md)
- [Roadmap](docs/roadmap.md) and [contributing](docs/contributing.md)

## Status and limitations

- Metrics over MQTT, the DAC output logic and the daemon work. **Bluetooth pairing, the
  BOOT re-pairing gesture and the status topic are implemented and unit-tested but have
  not yet been tested on a physical ESP32.**
- The DAC module, gauges and backlight wiring have not been verified on the bench.
- Wi-Fi credentials are stored on the ESP32 without flash encryption.
- The MQTT broker has no authentication; use it on a trusted network.
- One device per network is the expected setup: with several, `resmon.local` is
  ambiguous and the saved IP address is what identifies yours.
- No installer or autostart yet.
