# ESP32 CPU/MEM Monitor

ESP32-based edge display for Windows CPU and memory usage.

The ESP32 hosts a small MQTT broker and drives two DAC outputs for CPU and
memory gauges. The Windows daemon samples local resource usage and publishes
percentage payloads to the ESP32.

Firmware source and tests live under `firmware/`. `platformio.ini` stays at the
repository root so PlatformIO tools can detect the project. The Windows daemon
lives under `daemon/`.

## Current Runtime Contract

- Broker: `resmon.local:1883`
- CPU topic: `resmon/cpu`
- Memory topic: `resmon/mem`
- Payload: ASCII decimal percentage, for example `42.5`
- Transport: MQTT only

See [COMMUNICATION.md](COMMUNICATION.md) for the full message contract.

## Hardware

See [HARDWARE.md](HARDWARE.md) for the current prototype hardware design,
electrical boundaries, I2C/DAC assignments, the selected 5 V to 12 V backlight
boost converter, and unresolved wiring decisions.

## Local Firmware Secrets

Tracked source files must not contain real Wi-Fi credentials.

For local development, copy the example file:

```powershell
Copy-Item firmware\src\secrets.example.h firmware\src\secrets.h
```

Then edit `firmware\src\secrets.h` locally:

```cpp
const char* WIFI_SSID = "your-wifi-ssid";
const char* WIFI_PASSWORD = "your-wifi-password";
```

`firmware/src/secrets.h` is ignored by git.

## Tests

C# daemon logic:

```powershell
dotnet build daemon\ResourceMonitorDaemon.sln
dotnet run --project daemon\ResourceMonitorDaemon.Tests\ResourceMonitorDaemon.Tests.csproj
```

C++ firmware logic:

```powershell
platformio test
```

## Planning

See [DEVELOPMENT_PLAN.md](DEVELOPMENT_PLAN.md) for the current roadmap,
including the planned Windows client setup wizard and secure ESP32 Wi-Fi
provisioning flow.
