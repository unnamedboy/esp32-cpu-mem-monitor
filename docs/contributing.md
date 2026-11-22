# Contributing

Thanks for helping. This page covers the conventions and the checks to run; build and
test commands are in the [README](../README.md#building-and-testing).

## Before you start

- Read [architecture.md](architecture.md) for how the pieces fit together.
- Keep changes small and focused. The project is a prototype, so prefer configurable,
  incremental changes over assumptions about hardware that hasn't been measured.
- Don't change the MQTT payloads, topics or the pairing protocol unless that is the
  point of the change; the daemon and firmware have to agree, and older firmware may
  still be on existing boards.

## Firmware conventions

- All pins, I2C addresses, topics, timings, feature flags and calibration values live in
  [`firmware/src/config.h`](../firmware/src/config.h). Don't scatter raw GPIO numbers or
  constants through `main.cpp`.
- `loop()` must stay non-blocking: no long `delay()`, no waiting for the network, and
  rate-limited serial output (the serial port also carries the pairing-code query).
- Optional hardware must fail safe: the firmware has to boot and run without the DAC
  or any other I2C device, and say so in its diagnostics.
- Clamp every value that drives an output.
- Put decision logic in a pure header under `firmware/include` and add Unity tests in a
  `firmware/test/test_<name>/` folder, one folder per suite.
- Keep the startup diagnostics current: build ID, enabled features, pins, detected I2C
  devices, free heap, Wi-Fi and MQTT state.
- Respect the ESP32 pin constraints listed in [hardware.md](hardware.md#pins-and-bus).

## Daemon conventions

- No UI text in C# sources: add it to both `.resx` files and the typed accessors (see
  [daemon.md](daemon.md#localization)). The localization tests fail otherwise.
- Log messages are English and must never contain passwords, tokens, the pairing code or the SSID.
- Settings go through `DeviceSettings`/`UserSettingsStore`; invalid values are sanitized
  with a warning, never fatal.
- Nothing may block the sampling loop.

## Hardware changes

When you wire something, update the [connection table](hardware.md#connections) in the
same change: part, ESP32 connection and header label, direction, voltage domain, power,
protection, firmware setting and how you tested it. Mark anything you have not verified
as unverified. Keep the safety rules in [hardware.md](hardware.md#electrical-safety) in
mind: the carrier's `DC IN` is labelled 6–9 V, GPIO is 3.3 V, and the 12 V backlight rail stays
separate. Don't turn photo estimates into enclosure CAD; measure.

## Checks before you open a pull request

Firmware changes:

1. `pio run -e esp32dev` builds, and the tests compile with
   `pio test -e esp32dev --without-uploading --without-testing` (or run on a board with
   `pio test -e esp32dev`, then re-flash with `pio run -e esp32dev -t upload`). The
   compile-only run reports each suite as SKIPPED with "0 test cases"; that is expected.
2. With a board: the startup diagnostics are complete and contain no secrets; valid,
   invalid and stale metrics behave as expected; the firmware runs without the DAC;
   `resmon/status` still arrives every 10 s without SSID, IP, MAC, pairing code or
   password.

Daemon changes:

1. The solution builds and all tests pass.
2. Reconnects don't block sampling, settings changes apply without a rebuild, and logs
   and notifications contain no credentials.

The tests need the .NET runtime listed under
[What you need](../README.md#what-you-need).

If a running daemon locks the build output, build to a separate folder (ignored by
git). Run this from the repository root and keep the folder inside `daemon\`: the
localization test finds the sources by walking up from the test assembly.

```powershell
dotnet build daemon\ResourceMonitorDaemon.sln -p:OutDir="$PWD\daemon\.tmpbuild\"
dotnet daemon\.tmpbuild\ResourceMonitorDaemon.Tests.dll
```

Update the docs in the same pull request when behaviour, settings or wiring change.
