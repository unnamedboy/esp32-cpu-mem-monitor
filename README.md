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
  protected by an 8-character pairing code. No Wi-Fi password in the firmware by default
  (an optional development fallback can compile one in).
- Gauges fall to zero when data stops; out-of-range or malformed values are ignored.
- Settings dialog, live configuration reload, automatic reconnects.
- Optional forwarding of logs, metrics and device health to your own Grafana Loki.
- English and Simplified Chinese UI.

## What you need

- An ESP32-WROOM-32 dev board (the prototype uses a MOC9686 expansion shield), powered
  over USB with a data cable (not a charge-only one).
- For the display: a GP8413-compatible I2C DAC and two analogue panel gauges. The
  firmware also runs without them. See [docs/hardware.md](docs/hardware.md#parts) for
  gauge requirements (not yet verified), wiring status and **safety notes**: the shield's `DC IN` is labelled
  6–9 V (never connect 12 V), and GPIO is 3.3 V.
- A Wi-Fi network that offers 2.4 GHz for the ESP32, on the same LAN as the PC.
- A Windows 10 (2004 or later) or Windows 11 PC with Bluetooth LE for pairing.
- [PlatformIO](https://platformio.org/) to build the firmware.
- The [.NET](https://dotnet.microsoft.com/) 9 SDK to build and run the daemon, or a newer
  SDK plus the .NET 9 Desktop Runtime: the daemon and its tests target .NET 9 and need
  that runtime.

## Quick start

### 0. Wiring (optional)

There is no verified wiring guide for the DAC and gauges yet. The firmware runs fine
with nothing attached, so you can start with just the board on USB.

What the firmware expects: a GP8413-compatible DAC (DFRobot GP8XXX library) at I2C
address `0x59`, on the I2C bus at GPIO21 (SDA) and GPIO22 (SCL); the actual module is
not yet verified. The address is `DAC_I2C_ADDRESS` in
[`firmware/src/config.h`](firmware/src/config.h). Check whether your module has address
jumpers or switches, and that they match.

Before wiring anything, read the [connection table](docs/hardware.md#connections) and
the [safety notes](docs/hardware.md#electrical-safety), in particular the check of the
DAC's I2C pull-up voltage: the ESP32's I2C pins are 3.3 V only.

### 1. Flash the firmware

Get the source if you haven't yet:

```sh
git clone https://github.com/unnamedboy/esp32-cpu-mem-monitor.git
cd esp32-cpu-mem-monitor
```

Run the commands below from the repository root, with the board on USB (`pio` is the
PlatformIO CLI; in VS Code, use a PlatformIO terminal). If no COM port appears for the
board, install the driver for its USB-serial chip (usually CP210x or CH340). The first
`pio run` downloads the ESP32 toolchain and libraries, several hundred MB.

If the board has run other firmware before, erase the flash once first with
`pio run -e esp32dev -t erase`. That removes any saved Wi-Fi network, which would
otherwise make the board skip pairing; the device then generates a new pairing code
([details](docs/provisioning.md#boards-that-already-have-wi-fi-settings)). Then upload
and open the serial monitor:

```sh
pio run -e esp32dev -t upload
pio device monitor -e esp32dev
```

The serial monitor (115200 baud) shows the startup diagnostics. They are printed once at
boot, so press EN/RESET to see them again after opening the monitor. A board without
Wi-Fi settings goes straight into pairing mode and prints its pairing code (repeated
every 30 s):

```text
Provisioning: name=PROV_RESMON_A1B2C3 pop=ABCDEFGH
```

Close the serial monitor before pairing, so the wizard can read the code over USB.
If instead the board connects to a Wi-Fi network it already knew, see
[boards that already have Wi-Fi settings](docs/provisioning.md#boards-that-already-have-wi-fi-settings).

### 2. Build and start the daemon

```powershell
dotnet build daemon\ResourceMonitorDaemon.sln -c Release
daemon\ResourceMonitorDaemon\bin\Release\net9.0-windows10.0.19041.0\ResourceMonitorDaemon.exe
```

The app lives in the notification area; on Windows 11 its icon may be in the hidden-icons
overflow. Until a device is paired, an "MQTT connection failed" notification (on the
[first run](docs/daemon.md#first-run-pairing-prompt): *No device found — click to
pair*) is expected. There is no installer yet;
to start the daemon with Windows, put a shortcut to the exe in your Startup folder.

### 3. Pair the device

Right-click the tray icon, choose **Pair device…**, pick the
`PROV_RESMON_…` device, confirm the pairing code was read over USB (or type it from the
serial log), enter your Wi-Fi name and password, and click **Pair**. When the device has joined Wi-Fi, the wizard saves its IP address and
the daemon connects. The gauges start moving if they are wired; otherwise the serial
log's `DAC values: cpu=… mem=…` line starts changing.

Bluetooth pairing has not yet been tested on real hardware. If it fails for you, the
[`secrets.h` development fallback](docs/provisioning.md#development-fallback-secretsh)
puts the Wi-Fi credentials into the firmware build instead.

To re-pair later or switch networks, hold the board's **BOOT** button for 5–15 s while it
is running, then release it. Details and troubleshooting:
[docs/provisioning.md](docs/provisioning.md#pairing-a-device).

## Configuration

Most people only need the tray's **⚙ Settings…** dialog. It saves only the values you
change, to `%LocalAppData%\ResourceMonitorDaemon\settings.json`. Those override the
shipped `appsettings.json` next to the exe, and `RESMON_` environment variables (`__`
separates sections) override both ([how the layers work](docs/daemon.md#layers)).

| Setting | Default | Meaning |
| --- | --- | --- |
| `Device:Host` | `resmon.local` | Broker host name or IP |
| `Device:FallbackAddress` | empty | IPv4 tried when `Host` fails; set by the pairing wizard |
| `Device:Port` | `1883` | Broker port |
| `Device:CpuTopic`, `Device:MemTopic` | `resmon/cpu`, `resmon/mem` | Must match the firmware |
| `Device:SampleIntervalMs` | `1000` | 250–60000 |
| `Ui:Language` | `auto` | `auto`, `en` or `zh-Hans` |

Changes to the JSON files apply while the daemon runs; a host or port change reconnects
at once (or on resume, if sending is paused). Invalid values are reset to the default
or clamped into range, with a warning in the log.

Environment variables are read only when the daemon starts. Set them and start the exe
from the same PowerShell window, or set them as user environment variables and restart
the daemon. Values set this way are shown read-only in the dialog and are never saved:

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
Chinese) gets English. Change it with **Language** in the settings dialog, or force it
with `RESMON_Ui__Language`. The menu switches as soon as the setting is applied; dialogs
that are already open keep their language. The log is always English.

The documentation uses the English labels; the Chinese UI has the same layout and menu
order.

## Troubleshooting

| Symptom | Things to check |
| --- | --- |
| "MQTT connection failed" notification | Is the ESP32 powered and on the same network? Does `resmon.local` resolve on this PC? Pairing saves the device's IP as a fallback; you can also set **Host** to the IP. The daemon keeps retrying in the background. |
| Gauges sink to zero | No valid data for 5 s: sending is paused (tray menu), the daemon is not running, or the connection dropped. |
| Device doesn't show up in the wizard | It is not in pairing mode: use the BOOT gesture (hold 5–15 s, release) and **Rescan**. Check that Bluetooth is on. |
| Pairing code not read automatically | Close any serial monitor; the COM port can only be open in one program. Auto-read is only tried on ports that could be an ESP32 USB-serial bridge ([rules](docs/provisioning.md#serial-pairing-code-query)); to retry the USB read, click **Rescan** in the wizard and select the device again; otherwise type the code from the serial log. |
| Pairing fails | See the [message table](docs/provisioning.md#if-pairing-fails). Remember the ESP32 needs 2.4 GHz. |
| Serial log says `DAC not found at 0x59` | Check the DAC wiring and address; the firmware runs on without outputs. |
| Upload fails with `Failed to connect to ESP32: Timed out waiting for packet header` | Use a USB data cable (not a charge-only one) and check the COM port driver. If the board needs it, hold BOOT while the upload starts connecting, then release it. |
| Firmware build stops with `CORE_DEBUG_LEVEL >= 3 is refused` | Use a level from 0 to 2 (`platformio.ini` sets 1). The build refuses 3 and higher on purpose; see [why](docs/firmware.md#toolchain). |

For anything else, open the log from the tray menu. For more detail, set
`RESMON_Logging__LogLevel__Default=Debug` and restart the daemon.

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

The daemon and its tests need the .NET runtime listed under [What you need](#what-you-need).

```powershell
# Daemon: build and run the tests (prints "N/N passed")
dotnet build daemon\ResourceMonitorDaemon.sln
dotnet run --project daemon\ResourceMonitorDaemon.Tests\ResourceMonitorDaemon.Tests.csproj

# Firmware: build, then compile the tests without a board
# (each suite is built and then reported as SKIPPED with "0 test cases"; that is expected)
pio run -e esp32dev
pio test -e esp32dev --without-uploading --without-testing

# Firmware tests on a connected board (they replace the firmware; re-flash afterwards)
pio test -e esp32dev
pio run -e esp32dev -t upload
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

- Metrics over MQTT and the daemon work, and the firmware's DAC output logic is
  unit-tested. **Bluetooth pairing, the
  BOOT re-pairing gesture and the status topic are implemented and unit-tested but have
  not yet been tested on a physical ESP32.**
- The DAC module, gauges and backlight wiring have not been verified on the bench.
- Wi-Fi credentials are stored on the ESP32 unencrypted
  ([security model](docs/provisioning.md#security-model)).
- The MQTT broker has no authentication; use it on a trusted network.
- One device per network is the expected setup. With several, `resmon.local` may
  resolve to another monitor; set **Host** to your device's IP address
  ([details](docs/provisioning.md#limitations)).
- No installer or autostart yet.
