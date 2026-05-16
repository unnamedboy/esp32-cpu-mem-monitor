# Hardware Design

## Current Prototype

This project is an ESP32 edge display for Windows CPU and memory usage. The
Windows daemon publishes metric percentages over MQTT; the ESP32 receives those
values and drives two analogue gauge outputs through an external I2C DAC.

## Confirmed Hardware

| Item | Status | Notes |
| --- | --- | --- |
| Controller | Confirmed | ESP32-WROOM-32 development board |
| Carrier board | Confirmed | MOC9686 ESP32 expansion shield with GPIO, 3.3 V, 5 V, GND, and labelled I2C headers |
| Prototype area | Confirmed | Separate full-size solderless breadboard |
| Controller power | Confirmed | USB-C during development |
| Backlight boost converter | Selected | Amazon ASIN `B0C2H6PFVH`; used to boost a 5 V supply to 12 V for voltmeter backlight power only |
| External DAC | Firmware target | Firmware currently uses a GP8413-compatible DFRobot GP8XXX DAC at I2C address `0x59`; physical module, connector labels, and wiring still need verification |
| Front-panel voltmeters | Planned | Gauge signal requirements are not documented yet; backlight is planned for 12 V power |
| M5Stack Unit DAC2 | Optional future candidate | Do not treat as selected until its exact part, pinout, address, and power requirements are documented |

## Electrical Boundaries

- The MOC9686 carrier `DC IN` label is `6-9 V`. Do not connect 12 V directly to this input.
- ESP32 GPIO is 3.3 V logic. Do not feed 5 V signals into GPIO pins without documented level shifting or conditioning.
- The 12 V backlight rail is generated from 5 V through the selected boost converter. It is only for voltmeter backlight power.
- Do not connect the 12 V backlight rail to the MOC9686 `DC IN`, ESP32 GPIO, I2C, or DAC signal outputs.
- The 12 V backlight rail still needs a measured current budget, polarity check, switch/fuse decision, and grounding plan before permanent wiring.
- The external DAC output range is configured in firmware as `0-5V`. That is a DAC output domain, not an ESP32 GPIO input domain.
- Missing DAC hardware is allowed: firmware logs that the DAC was not found and continues running without writing outputs.

## Power Subsystems

| Rail | Source | Loads | Status | Notes |
| --- | --- | --- | --- | --- |
| ESP32 5 V / USB power | USB-C during development | ESP32 development board and carrier | Confirmed development power | Keep USB-C reachable for flashing and serial diagnostics |
| Logic 3.3 V | ESP32/carrier regulator | ESP32 GPIO and I2C logic | Confirmed logic domain | Do not expose GPIO to 5 V or 12 V signals |
| DAC output domain | External DAC module | CPU and memory gauge signal inputs | Firmware-configured 0-5 V | Verify actual gauge signal input requirements before permanent wiring |
| Backlight 12 V | 5 V to 12 V boost converter, Amazon ASIN `B0C2H6PFVH` | Voltmeter backlight lamps only | Selected, needs bench validation | Verify output voltage under load, input current from 5 V source, polarity, heat, and available headroom |

The boost converter's exact continuous current rating, wiring colors, protection
features, and thermal limits must be checked against the purchased module label
or product page before enclosure installation. Do not size wiring, fusing, or
thermal clearance from a generic listing alone.

## Firmware Pin and Bus Assignment

| Function | Firmware setting | Direction | Voltage domain | Notes |
| --- | --- | --- | --- | --- |
| I2C SDA | GPIO21 | Bidirectional | 3.3 V logic | Reserved project default |
| I2C SCL | GPIO22 | Output from ESP32, bidirectional bus behavior | 3.3 V logic | Reserved project default |
| DAC I2C address | `0x59` | I2C device address | 3.3 V logic bus | Must be verified against the actual DAC module |
| CPU gauge output | DAC channel `0` | Analogue output from DAC | Firmware-configured 0-5 V DAC range | Maps MQTT `resmon/cpu` from `0..100%` to DAC code `0..32767` |
| Memory gauge output | DAC channel `1` | Analogue output from DAC | Firmware-configured 0-5 V DAC range | Maps MQTT `resmon/mem` from `0..100%` to DAC code `0..32767` |

## Intended Wiring

This table documents the intended connection model. Treat rows marked
`Unverified` as design intent, not build-proof wiring.

| Component | ESP32 connection | Direction | Voltage domain | Power | Protection/conditioning | Firmware configuration | Validation |
| --- | --- | --- | --- | --- | --- | --- | --- |
| GP8413-compatible external DAC | I2C SDA GPIO21, I2C SCL GPIO22 | Bidirectional I2C plus DAC outputs | 3.3 V I2C logic; DAC output configured 0-5 V | TODO: verify module supply pin and allowed voltage | TODO: verify pull-ups, output loading, and gauge interface | `DAC_I2C_ADDRESS = 0x59`, `CPU_OUTPUT_CHANNEL = 0`, `MEM_OUTPUT_CHANNEL = 1` | Unverified physical wiring |
| CPU analogue gauge | DAC channel 0 | Output | TODO: verify gauge input requirements | TODO | TODO: current limiting, buffering, scaling, or driver if required | MQTT `resmon/cpu`; clamp to `0..100%` | Unverified gauge |
| Memory analogue gauge | DAC channel 1 | Output | TODO: verify gauge input requirements | TODO | TODO: current limiting, buffering, scaling, or driver if required | MQTT `resmon/mem`; clamp to `0..100%` | Unverified gauge |
| Voltmeter backlights | No ESP32 GPIO connection | Power only | 12 V backlight rail | 5 V to 12 V boost converter output | TODO: verify polarity, current draw, switch/fuse, and shared ground plan | None | Unverified load test |

## Mechanical Layout

- Mount the ESP32 carrier separately from the solderless breadboard during prototyping when practical.
- Keep the ESP32 antenna area clear of wire bundles, metal gauge bodies, other PCBs, and conductive enclosure parts.
- Keep USB-C accessible for power, flashing, and serial diagnostics.
- Keep reset/boot controls accessible until the flashing and recovery workflow is final.
- Keep the breadboard removable while circuit values and wiring are still changing.
- Do not treat solderless breadboard wiring as permanent internal enclosure construction.

## Measurements Needed Before Enclosure CAD

Record physical measurements in millimetres before creating final CAD:

- ESP32 carrier width, length, height, hole diameter, hole spacing, and hole-to-edge offsets.
- USB-C opening location, plug clearance, and cable bend space.
- Header heights and reset/boot button clearances.
- ESP32 antenna keep-out area.
- Breadboard dimensions and mounting method.
- DAC module dimensions, connector clearance, and mounting method.
- Boost converter dimensions, wire exit direction, heat clearance, and mounting method.
- Gauge bezel diameter, body depth, terminal layout, and cable clearance.
- Planned wall thickness, lid overlap, and fastening method.

## Open Hardware Decisions

- Exact external DAC module and whether the GP8413-compatible firmware target matches the module on the bench.
- Whether analogue gauges accept 0-5 V directly or require current drive, buffering, attenuation, or calibration.
- Whether future disk/network gauges require additional DAC channels or a different DAC.
- Exact current draw for all 12 V backlights and whether the boost converter has enough continuous current and thermal margin.
- Whether the backlight rail needs a physical switch, inline fuse, or connectorized harness.
- Final enclosure material and antenna clearance.
