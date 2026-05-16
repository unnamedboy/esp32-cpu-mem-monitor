# ESP32 CPU/MEM Monitor — Project Instructions

## Purpose and scope

This repository controls an ESP32-based CPU/memory monitor. Read this file before changing firmware, hardware documentation, pin assignments, configuration, or enclosure-related files.

The system is still a prototype. Prefer safe, incremental, configurable changes over assumptions about hardware that has not yet been connected or measured.

## Confirmed hardware

- Controller: ESP32-WROOM-32 development board.
- Carrier/expansion board: MOC9686 ESP32 expansion shield, with GPIO breakout headers, 3.3 V, 5 V, GND, and labelled I2C breakout headers.
- Prototype area: a separate full-size solderless breadboard. The ESP32 carrier is not required to be inserted into this breadboard; it already exposes GPIO headers.
- Current controller power: USB-C.
- Future peripheral: M5Stack Unit DAC2; it must be treated as optional until its exact wiring and power requirements are documented in the project.
- Future components may include analogue signal-conditioning parts, sensors, and front-panel analogue gauges.

Reference photos are stored under `project_sources/` in this workspace. Treat visible labels as useful evidence, but do not infer unlabelled electrical connections or exact mechanical dimensions from a photograph.

## Electrical safety rules — non-negotiable

1. The MOC9686 carrier is labelled **DC IN 6–9 V**. Never propose, document, or configure a direct 12 V connection to this input.
2. ESP32 GPIO and ADC pins are 3.3 V logic. Do not connect a 5 V analogue or digital signal to an ESP32 GPIO unless a verified level-shifting, attenuation, or conditioning circuit is explicitly documented.
3. A later 12 V rail for gauges or lighting is a separate power subsystem. Do not merge it with the ESP32 input without a documented converter, grounding plan, current budget, and polarity verification.
4. Do not claim that an unverified module, signal conditioner, DAC, or gauge has a particular voltage range, pinout, or I2C address. Add a TODO and request the module documentation or a close-up photograph.
5. Do not make permanent wiring or enclosure decisions based only on approximate measurements from photos.

## Pin and bus constraints

- Reserve I2C defaults for **SDA = GPIO21** and **SCL = GPIO22**, unless a documented hardware change intentionally overrides them.
- Do not use GPIO1 or GPIO3 for peripherals while serial flashing/logging is required.
- Avoid boot-strapping pins GPIO0, GPIO2, GPIO12, and GPIO15 for new peripherals unless the boot-state implications have been checked and documented.
- GPIO34–GPIO39 are input-only. Never configure them as outputs.
- If the ESP32's internal DAC is used, only GPIO25 and GPIO26 are valid DAC outputs. Do not confuse this with the external M5Stack DAC2.
- Treat every new GPIO assignment as a project-level decision: centralise it in configuration and document its purpose, voltage domain, direction, and physical connection.

## Firmware and configuration rules

1. Preserve existing communication behaviour and public message formats unless the requested task explicitly changes them.
2. Keep hardware configuration centralised. Use the existing configuration approach, or add a clearly named configuration header/module for:
   - GPIO assignments;
   - I2C pins and device addresses;
   - optional peripheral feature flags;
   - Wi-Fi/MQTT configuration placeholders; and
   - calibration values and safe output limits.
3. Do not scatter raw GPIO numbers, I2C addresses, or voltage-conversion constants throughout application logic.
4. Optional hardware must fail safely: firmware must compile and boot when a DAC, sensor, or I2C device is absent. Missing devices must produce a useful diagnostic instead of a crash, blocking loop, or repeated reset.
5. Use a non-blocking main loop. Avoid long `delay()` calls and avoid excessive serial logging, display refreshes, I2C probes, or MQTT reconnect attempts.
6. Use bounded output values and explicit clamping whenever code produces an analogue, PWM, DAC, or gauge-control value.
7. Do not add dependencies merely to solve a small configuration or control-flow problem; follow the existing project stack and style.

## Startup diagnostics

When relevant code is changed, retain or add concise startup diagnostics that report:

- firmware version/build identifier where available;
- enabled optional features;
- allocated GPIO and I2C bus pins;
- detected I2C device addresses;
- free heap / relevant runtime health information; and
- Wi-Fi and MQTT connection state.

Diagnostics must remain rate-limited after startup.

## Hardware documentation requirements

When changing a hardware-related feature, update the project documentation in the same change. For every actual connection, document:

| Field | Required information |
| --- | --- |
| Component | Exact module or part name/version |
| ESP32 connection | GPIO/I2C bus and physical header label |
| Direction | Input, output, or bidirectional |
| Voltage domain | 3.3 V, 5 V, or other verified level |
| Power | Supply and ground connection |
| Protection/conditioning | Divider, buffer, level shifter, fuse, etc., if applicable |
| Firmware configuration | Setting/feature flag/calibration source |
| Validation | How the connection was tested |

If any of these are unknown, document the connection as unverified rather than filling in a plausible value.

## Physical layout and enclosure guidance

- During prototyping, mount the ESP32 carrier separately from the large breadboard when practical. The carrier already provides breakout headers and its four corner mounting holes make it suitable for standoffs.
- Keep the ESP32 PCB antenna area clear of dense wire bundles, nearby PCBs, metal gauge bodies, and conductive enclosure parts. A plastic enclosure wall is normally preferable near the antenna, but its required clearance must be verified before final CAD.
- Keep the USB-C port reachable from an exterior wall for power and firmware flashing. Do not block access to reset/boot controls if they are required by the board.
- Use the carrier's corner holes with measured M3-compatible standoffs/screws only after confirming hole diameter and spacing. Leave clearance below the board so solder joints and headers cannot contact the enclosure floor.
- Keep the breadboard removable while circuit values and wiring are still changing. For the final build, migrate proven circuits to perfboard, a custom PCB, or another mechanically secure solution; do not treat a loose solderless breadboard as permanent internal construction.
- Separate low-level analogue signal wiring from USB/power and any future higher-current wiring. Keep analogue wiring short where practical, leave a small service loop, use detachable labelled connectors, and never route cables across the antenna zone.

## Mechanical measurements before CAD

Before creating or editing an enclosure model, request or record measured values in millimetres for:

- carrier board width, length, height, hole diameter, and hole-to-hole spacing;
- hole-to-edge offsets;
- USB-C opening location, plug clearance, and cable bend space;
- header heights and the reset/boot-button clearances;
- ESP32 antenna keep-out area;
- breadboard dimensions and mounting method;
- DAC2 dimensions, connector clearance, and mounting requirements;
- gauge bezel diameter, body depth, terminals, and cable clearance; and
- planned wall thickness, lid overlap, and fastening method.

Never convert estimated dimensions from a photo into final CAD geometry without calling them estimates and asking for physical measurements.

## Work process

1. Inspect the relevant existing code and documentation before editing.
2. State any hardware assumption that materially affects the design.
3. Implement the smallest safe change that meets the request.
4. Build/compile or otherwise validate the change when the project tooling is available.
5. Summarise changed files, required physical wiring, known limitations, and tests run.

If a task requires a component datasheet, pinout, exact measurement, or electrical test that is not present in the repository, stop before committing to a hardware-specific design and list the missing information needed.
