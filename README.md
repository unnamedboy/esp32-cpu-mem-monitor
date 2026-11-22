# ESP32 CPU/MEM Monitor

This repository implements a working prototype for a Windows-to-ESP32 resource monitor.
The Windows daemon samples CPU and memory usage, publishes MQTT payloads, and the ESP32 hosts the broker and drives two DAC outputs for a live analogue display.

## What is implemented today

### Windows daemon

The .NET app in [daemon/ResourceMonitorDaemon](daemon/ResourceMonitorDaemon) currently does the following:

- samples local CPU usage with Windows Performance Counters;
- reads total/used RAM via the Windows memory API;
- formats percentages in a stable one-decimal invariant format;
- publishes values to the ESP32 MQTT broker on `resmon.local:1883`;
- sends metrics on `resmon/cpu` and `resmon/mem`;
- includes a tray UI with pause/resume controls and MQTT failure notifications;
- clamps values to the valid `0.0..100.0` range before publishing.

### ESP32 firmware

The firmware in [firmware/src/main.cpp](firmware/src/main.cpp) currently does the following:

- gets Wi-Fi credentials through BLE provisioning (ESP-IDF `wifi_provisioning`
  called directly, Security1 + 8-character pairing code), with an
  optional compiled `secrets.h` development fallback — see
  [PROVISIONING.md](PROVISIONING.md) (implemented, **not yet tested on hardware**);
- re-opens provisioning when the on-board BOOT button is held for 5–15 seconds and then released (longer holds are ignored);
- answers `RESMON?POP` on USB serial with the pairing code while in provisioning mode;
- connects to Wi-Fi as a station without blocking the main loop; the firmware owns the reconnect policy (bounded 5–60 s backoff);
- advertises the device via mDNS as `resmon.local`;
- hosts a small MQTT broker with PicoMQTT on port 1883;
- subscribes to `resmon/cpu` and `resmon/mem`;
- validates incoming payloads and ignores malformed or out-of-range values;
- updates two DAC channels for CPU and memory output;
- fades stale metrics back toward zero after the configured timeout;
- publishes a small JSON health snapshot (build, uptime, heap, RSSI, Wi-Fi/provisioning state, DAC presence) on `resmon/status` every 10 s into its own broker (feature flag `ENABLE_STATUS_TOPIC`; no SSID, IP, MAC or secrets);
- logs startup diagnostics including build id, I2C/DAC settings, free heap, and connection state.

### Shared configuration and contract

- MQTT contract: [COMMUNICATION.md](COMMUNICATION.md)
- BLE provisioning / pairing contract: [PROVISIONING.md](PROVISIONING.md)
- Hardware design/status: [HARDWARE.md](HARDWARE.md)
- roadmap and future phases: [DEVELOPMENT_PLAN.md](DEVELOPMENT_PLAN.md)
- firmware configuration: [firmware/src/config.h](firmware/src/config.h)

## Runtime contract

- broker host: `resmon.local`
- broker port: `1883`
- CPU topic: `resmon/cpu`
- memory topic: `resmon/mem`
- payload: plain ASCII percentage, for example `42.5`
- device status topic (ESP32 → subscribers): `resmon/status`, compact JSON, see [COMMUNICATION.md](COMMUNICATION.md)
- transport: MQTT only
- QoS: 0 (at-most-once)

The ESP32 treats each metric independently and expires stale values after a five-second timeout before decaying toward zero.

## Key repository layout

- [platformio.ini](platformio.ini) — PlatformIO project configuration for the ESP32 firmware
- [firmware/src](firmware/src) — firmware source, Wi-Fi config, MQTT settings, and DAC logic
- [firmware/include](firmware/include) — pure, unit-tested firmware helpers (metric conversion, provisioning boot decision, pairing code, serial command parsing, BOOT button hold-and-release window, `resmon/status` payload formatting)
- [firmware/test](firmware/test) — Unity tests for the helpers in `firmware/include`
- [daemon/ResourceMonitorDaemon](daemon/ResourceMonitorDaemon) — Windows daemon app
- [daemon/ResourceMonitorDaemon.Tests](daemon/ResourceMonitorDaemon.Tests) — small smoke tests for formatting and clamping logic
- [assets/SQ52_CPU_MEM_1to1.svg](assets/SQ52_CPU_MEM_1to1.svg) — front-panel sticker asset for a CPU/MEM gauge face, intended as a replacement for a generic voltmeter face

## Local setup

### Wi-Fi setup (BLE provisioning)

A firmware image built without `secrets.h` contains no Wi-Fi credentials. On
first boot it enters BLE provisioning mode and is paired from the Windows app;
the serial log shows the BLE name and the 8-character pairing code. Hold the
board's BOOT button for 5–15 seconds while it is running, then release it, to
forget the Wi-Fi network and pair again. Details: [PROVISIONING.md](PROVISIONING.md).

> **Upgrading a board that ran earlier firmware:** the old firmware called
> `WiFi.begin(SSID, PASS)` with the Arduino default `persistent=true`, so the
> `secrets.h` network is already in the ESP32's Wi-Fi NVS store. The new
> firmware treats that as provisioned credentials: it boots with
> `source=stored`, skips BLE, ignores later `secrets.h` changes, and answers
> `RESMON-POP unavailable state=provisioned`. To get first-boot behaviour, use
> the BOOT gesture once (hold 5–15 s, then release), or erase the whole flash
> and re-upload (`pio run -e esp32dev -t erase` then `pio run -e esp32dev -t upload`;
> this also generates a new pairing code).

### Optional development fallback (`secrets.h`)

Real Wi-Fi credentials must not be committed to git. For development without
the pairing app you can compile in fallback credentials; they are used only
when no BLE-provisioned credentials are stored.

Create a local secrets file from the example:

```powershell
Copy-Item firmware\src\secrets.example.h firmware\src\secrets.h
```

Then edit [firmware/src/secrets.h](firmware/src/secrets.h) locally with your SSID and password:

```cpp
const char* WIFI_SSID = "your-wifi-ssid";
const char* WIFI_PASSWORD = "your-wifi-password";
```

### Build and test

C# daemon tests:

```powershell
dotnet run --project daemon\ResourceMonitorDaemon.Tests\ResourceMonitorDaemon.Tests.csproj
```

PlatformIO firmware build (pinned to `espressif32 @ 7.0.1` = Arduino-ESP32
core 2.0.17; uses the `min_spiffs.csv` partition table because the BLE
provisioning image is ~1.63 MB):

```powershell
platformio run -e esp32dev
```

PlatformIO firmware tests (they run on the board; to only compile them use
`--without-uploading --without-testing`):

```powershell
platformio test -e esp32dev
```

The tests in `firmware/test` only include the pure headers from
`firmware/include`, so they can also be compiled and run with any host C++17
compiler and the Unity sources.

## Current status and caveats

This project is no longer in the “early concept” stage. The current prototype already has:

- a working Windows-side metrics publisher;
- an ESP32-side MQTT broker and DAC controller;
- topic-based communication and payload validation;
- stale metric decay and safe DAC clamping.

Known limitations still present:

- firmware BLE provisioning is implemented but has not yet been tested on physical hardware or end-to-end with the Windows pairing client;
- provisioned Wi-Fi credentials are stored in the standard ESP32 Wi-Fi NVS store without flash/NVS encryption;
- the daemon still uses hard-coded MQTT host and topic defaults;
- hardware wiring and enclosure decisions are still tracked in the design docs rather than being treated as final production assembly;
- the project is still a prototype and should be considered “functional lab build” rather than a finished product.

## Documentation

For the exact contract, hardware assumptions, and roadmap, see:

- [COMMUNICATION.md](COMMUNICATION.md)
- [PROVISIONING.md](PROVISIONING.md)
- [HARDWARE.md](HARDWARE.md)
- [DEVELOPMENT_PLAN.md](DEVELOPMENT_PLAN.md)
- [daemon/IMPLEMENTATION_SUMMARY.md](daemon/IMPLEMENTATION_SUMMARY.md)
