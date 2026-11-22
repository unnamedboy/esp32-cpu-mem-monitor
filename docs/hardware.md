# Hardware

The ESP32 receives CPU and memory percentages over MQTT and drives two analogue gauges
through an external two-channel I2C DAC. The hardware is a prototype: the controller
and carrier are confirmed, while the DAC module, gauges and backlight wiring have not
been verified on the bench yet.

A CPU/MEM gauge face for a generic square panel voltmeter is in
[`assets/SQ52_CPU_MEM_1to1.svg`](../assets/SQ52_CPU_MEM_1to1.svg) (printed at 1:1).

## Parts

| Part | Status | Notes |
| --- | --- | --- |
| ESP32-WROOM-32 development board | Confirmed | Controller; USB-C power and flashing during development |
| MOC9686 ESP32 expansion shield | Confirmed | Carrier with GPIO, 3.3 V, 5 V, GND and labelled I2C headers |
| Full-size solderless breadboard | Confirmed | Separate prototype area |
| GP8413-compatible I2C DAC (DFRobot GP8XXX driver) | Firmware target | I2C address `0x59`, output range set to 0–5 V. Exact module, connector labels and wiring not yet verified. |
| Two analogue panel voltmeters | Planned | Signal input requirements not yet documented; backlights need 12 V |
| 5 V → 12 V boost converter (Amazon ASIN `B0C2H6PFVH`) | Selected | Powers the voltmeter backlights only; needs bench validation |
| M5Stack Unit DAC2 | Optional candidate | Not selected; part, pinout, address and power requirements not documented |

## Electrical safety

- **The MOC9686 `DC IN` accepts 6–9 V.** Never connect 12 V to it.
- **ESP32 GPIO is 3.3 V logic.** Don't connect 5 V (or 12 V) signals to a GPIO without a
  level shifter, divider or other documented conditioning.
- **The 12 V backlight rail is separate.** It comes from the boost converter and feeds
  only the voltmeter backlights. Keep it away from `DC IN`, GPIO, I2C and the DAC
  outputs. Before permanent wiring it still needs a measured current budget, a polarity
  check, a switch/fuse decision and a grounding plan.
- **The DAC's 0–5 V range is an output domain** (DAC → gauges), not an ESP32 input.
- **Missing hardware is fine.** If no DAC answers at startup, the firmware logs
  `DAC not found at 0x59; running without external DAC` and keeps running without
  writing outputs.

## Power

| Rail | Source | Loads | Status |
| --- | --- | --- | --- |
| 5 V / USB | USB-C | ESP32 board and carrier | Confirmed for development; keep USB-C reachable for flashing and serial |
| 3.3 V logic | Board/carrier regulator | ESP32 GPIO and I2C | Confirmed |
| DAC output | DAC module | CPU and memory gauge inputs | Configured 0–5 V; gauge requirements unverified |
| 12 V backlight | Boost converter from 5 V | Voltmeter backlights only | Selected; verify voltage under load, input current, polarity and heat |

Check the boost converter's continuous current rating, wire colours, protection and
thermal limits against the purchased module, not a generic listing, before sizing
wiring, fusing or enclosure clearance.

## Pins and bus

All assignments live in [`firmware/src/config.h`](../firmware/src/config.h).

| Function | Setting | Direction | Voltage domain | Notes |
| --- | --- | --- | --- | --- |
| I2C SDA | GPIO21 (`I2C_SDA_PIN`) | Bidirectional | 3.3 V | Project default I2C pins |
| I2C SCL | GPIO22 (`I2C_SCL_PIN`) | Output (bus clock) | 3.3 V | |
| DAC | address `0x59` (`DAC_I2C_ADDRESS`) | I2C device | 3.3 V bus | Verify against the real module |
| CPU gauge | DAC channel 0 (`CPU_OUTPUT_CHANNEL`) | DAC output | 0–5 V | `resmon/cpu` |
| Memory gauge | DAC channel 1 (`MEM_OUTPUT_CHANNEL`) | DAC output | 0–5 V | `resmon/mem` |
| BOOT button | GPIO0 (`BOOT_BUTTON_PIN`) | Input | 3.3 V | On-board button, no extra wiring |

The firmware quantises each percentage to 256 steps (0–255) and scales that to the
GP8413's 15-bit code (0–32767), so 100 % is the top of the configured 0–5 V range. The
outputs are refreshed every second and on every valid message.

Pins that are deliberately not used for peripherals: GPIO1/GPIO3 (USB serial), the
boot-strapping pins GPIO0, GPIO2, GPIO12 and GPIO15 (except the existing BOOT button,
below), and GPIO34–39 as outputs (they are input-only). The ESP32's internal DAC
(GPIO25/26) is unused.

### BOOT button (GPIO0)

GPIO0 is a boot-strapping pin, so its use is limited:

- The firmware only reads the board's existing BOOT button; nothing external is
  connected. The pin is configured as `INPUT_PULLUP` and never driven.
- It is read only after boot, from the main loop, with a 50 ms debounce. Holding it for
  5–15 s and releasing it re-opens Wi-Fi pairing
  ([details](provisioning.md#re-pairing-and-changing-networks)). A button already held
  when the firmware starts is ignored until released.
- Holding BOOT while the board resets or powers up enters the ESP32's ROM download mode
  instead of running the firmware. That is the normal flashing behaviour.
- On typical dev boards the USB-UART bridge's DTR/RTS lines can pull GPIO0 low (that is
  how auto-flashing works). A serial tool that opens the port after boot with **only
  DTR asserted** and closes it 5–15 s later looks exactly like the BOOT gesture and
  triggers re-pairing. Use tools that set both DTR and RTS, or neither; the Windows
  pairing client sets neither. Not yet verified on this board.

## Connections

Rows marked unverified are design intent, not tested wiring.

| Component | ESP32 connection | Direction | Voltage domain | Power | Protection/conditioning | Firmware configuration | Validation |
| --- | --- | --- | --- | --- | --- | --- | --- |
| GP8413-compatible I2C DAC (exact module unverified) | I2C: SDA GPIO21, SCL GPIO22 (carrier I2C header; exact label unverified) | Bidirectional I2C; analogue outputs | 3.3 V I2C logic; outputs configured 0–5 V | Unverified: module supply pin and allowed voltage | Unverified: pull-ups, output loading | `DAC_I2C_ADDRESS = 0x59`, `CPU_OUTPUT_CHANNEL = 0`, `MEM_OUTPUT_CHANNEL = 1` | Unverified |
| CPU gauge (panel voltmeter) | DAC channel 0 | Output | Unverified: gauge input requirements | Unverified | Unverified: current limiting, buffering, scaling or driver if needed | `resmon/cpu`, 0–100 % (out-of-range values are ignored) | Unverified |
| Memory gauge (panel voltmeter) | DAC channel 1 | Output | Unverified: gauge input requirements | Unverified | Unverified: current limiting, buffering, scaling or driver if needed | `resmon/mem`, 0–100 % (out-of-range values are ignored) | Unverified |
| On-board BOOT push-button (ESP32-WROOM-32 dev board; board revision unverified) | GPIO0, on the board; no header wiring | Input | 3.3 V (button pulls GPIO0 to GND) | Board 3.3 V and GND | Board pull-up (value unverified) plus internal pull-up; 50 ms firmware debounce | `BOOT_BUTTON_PIN = 0`, `BOOT_BUTTON_ACTIVE_LOW = true`, `BOOT_BUTTON_DEBOUNCE_MS = 50`, `BOOT_BUTTON_MIN_HOLD_MS = 5000`, `BOOT_BUTTON_MAX_HOLD_MS = 15000` | Unverified: hold-and-release and the auto-program interaction not bench-tested |
| Voltmeter backlights | None | Power only | 12 V backlight rail | Boost converter output | Unverified: polarity, current draw, switch/fuse, shared ground | None | Unverified load test |

When you wire or change a connection, update this table with the same fields and say
how you tested it.

## Mechanical layout

- Mount the ESP32 carrier on standoffs separately from the breadboard; it already
  exposes all headers.
- Keep the PCB antenna area clear of wire bundles, metal gauge bodies, other PCBs and
  conductive enclosure parts. Bluetooth pairing uses the same antenna as Wi-Fi.
- Keep USB-C reachable from outside for power, flashing and serial diagnostics.
- Keep the BOOT and EN/RESET buttons reachable. BOOT is also how users re-pair the
  device, so a closed enclosure needs an actuator or another documented way to press
  it.
- Keep the breadboard removable while circuit values change; move proven circuits to
  perfboard or a PCB for the final build.
- Route low-level analogue wiring away from USB and power wiring, keep it short, and
  use labelled detachable connectors.

## Measurements needed before enclosure CAD

Record these in millimetres, measured on the real parts; photo estimates are not good
enough for final geometry:

- Carrier width, length, height, hole diameter, hole spacing and hole-to-edge offsets
  (confirm M3 fits).
- USB-C opening position, plug clearance and cable bend space.
- Header heights and BOOT/EN button clearances.
- ESP32 antenna keep-out area.
- Breadboard dimensions and mounting method.
- DAC module dimensions, connector clearance and mounting.
- Boost converter dimensions, wire exit direction, heat clearance and mounting.
- Gauge bezel diameter, body depth, terminal layout and cable clearance.
- Wall thickness, lid overlap and fastening method.

Open hardware decisions are listed in the [roadmap](roadmap.md#open-hardware-decisions).
