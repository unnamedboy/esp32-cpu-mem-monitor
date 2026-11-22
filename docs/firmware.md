# Firmware internals

The firmware is an Arduino sketch built with PlatformIO. It joins Wi-Fi, runs the MQTT
broker, turns the received percentages into DAC outputs, and handles Bluetooth pairing.
Wire formats are in [protocol.md](protocol.md) and [provisioning.md](provisioning.md);
pins and wiring in [hardware.md](hardware.md).

## Toolchain

[`platformio.ini`](../platformio.ini) pins everything for reproducible builds:

- `espressif32 @ 7.0.1`, which is Arduino-ESP32 core 2.0.17 on ESP-IDF 4.4.7. The
  provisioning code uses the IDF 4.4 `wifi_provisioning` API, so a core 3.x upgrade
  needs code changes.
- `mlesniew/PicoMQTT @ 1.3.0` for the broker.
- `DFRobot_GP8XXX` at a fixed 2023 commit; later commits need core 3.x.
- Partition table `min_spiffs.csv`: the Bluetooth + Wi-Fi image (about 1.6 MB) does not
  fit the default 1.25 MB app slot. `min_spiffs.csv` has two 1.875 MB app slots and
  128 KB SPIFFS, and keeps NVS where the default table has it, so stored Wi-Fi
  credentials and the pairing code survive a switch between the two tables.
- `-DCORE_DEBUG_LEVEL=1` (errors only). Levels 0 to 2 are accepted; `config.h` stops
  the build at level 3 or higher. At level 5 the Arduino core logs the Wi-Fi password
  received during pairing; levels 3 and 4 leak no secrets but flood the serial port
  that also carries the pairing-code query.
- `-Wl,-u,btStarted` links in the Arduino Bluetooth support code. Without it, Arduino
  releases the Bluetooth memory before `setup()` and BLE pairing can never start.

## Source layout

| File | Contents |
| --- | --- |
| [`firmware/src/config.h`](../firmware/src/config.h) | All pins, addresses, topics, timings and feature flags |
| [`firmware/src/main.cpp`](../firmware/src/main.cpp) | `setup()`, `loop()` and everything that touches hardware or the network |
| `firmware/src/secrets.h` | Optional development Wi-Fi fallback, ignored by git ([details](provisioning.md#development-fallback-secretsh)) |
| [`firmware/include/`](../firmware/include) | Pure logic with no Arduino dependencies, unit-tested |
| [`firmware/test/`](../firmware/test) | Unity tests for the headers in `firmware/include` |

The headers in `firmware/include`:

| Header | Logic |
| --- | --- |
| `metric_conversion.h` | Payload parsing and percent → 0–255 output value |
| `provisioning_logic.h` | Boot decision, network states, reconnect backoff, provisioning failure handling |
| `pop_code.h` | Pairing-code alphabet, generation and validation; BLE device name |
| `serial_command.h` | Bounded serial line buffer and the `RESMON?POP` reply |
| `button_long_press.h` | BOOT hold-and-release detector |
| `status_payload.h` | `resmon/status` JSON formatting |

Keep new hardware settings in `config.h` and new decision logic in a testable header,
rather than scattering constants or branching through `main.cpp`.

## Startup

`setup()` runs once:

1. Serial at 115200 baud and the build ID.
2. BOOT button pin as input with pull-up.
3. I2C on GPIO21/22 and the DAC at `0x59`. If found: 0–5 V range, both outputs at zero.
   If not, the firmware logs it and continues without DAC writes.
4. Pairing identity: the BLE name from the Wi-Fi MAC address, and the pairing code from
   NVS (generated with the hardware RNG if missing or invalid).
5. MQTT subscriptions to the CPU and memory topics.
6. Wi-Fi: the [boot decision](provisioning.md#how-the-device-chooses-its-wi-fi) picks
   stored credentials, the `secrets.h` fallback, or pairing mode. On boots that don't
   pair, the Bluetooth memory is released.
7. Startup diagnostics: build, enabled features, serial command, pins, detected I2C
   devices, Bluetooth controller state, Wi-Fi boot reason and state, MQTT state, status
   topic and free heap.

## Main loop

`loop()` never blocks. Each pass:

- reads the BOOT button (50 ms debounce);
- reads at most 64 bytes of serial input;
- runs the network state machine (provisioning events, connect and reconnect);
- services the MQTT broker, once it has started;
- publishes `resmon/status` when due;
- after the 5 s timeout, lowers a stale metric by one step every 50 ms;
- refreshes the DAC outputs once a second;
- prints a status line every 60 s.

Events from the Arduino network task only set flags; all decisions happen in `loop()`.

### Wi-Fi and reconnects

The firmware owns the reconnect policy; the Arduino core's auto-reconnect is turned off,
and only one connection attempt runs at a time:

- When a disconnect event ends an attempt, the next one waits the current backoff
  delay. The delay starts at 5 s and doubles with every attempt, up to 60 s.
- An attempt that produces no result within 30 s is abandoned, and the next one starts
  immediately.
- Once connected, the delay goes back to 5 s.

While connecting, the firmware logs a line at most every 10 s with the last disconnect
reason and the time to the next attempt. The DAC decay and the
BOOT button keep working while Wi-Fi is down.

On the first successful connection the firmware starts mDNS (`resmon.local`, service
`_mqtt._tcp`) and the broker on port 1883. Both keep running across later reconnects.
The DHCP host name is `resmon`.

The core still makes one automatic retry after the first disconnect of each boot,
independently of the auto-reconnect setting. That single extra attempt can overlap with
the firmware's own policy; it is bounded and not repeated.

IP address, SSID and RSSI for diagnostics are read from the `GOT_IP` event and directly
from the Wi-Fi driver, because the Arduino `WiFi` accessors report nothing after the
provisioning manager has started Wi-Fi itself.

### Outputs

Both gauges stay at 0 V from boot until the first valid message for them arrives.
A valid payload (`0..100`; anything else is ignored) is quantised to 0–255 and
written to the DAC immediately, scaled to the 15-bit code 0–32767. After 5 s without an
update a metric falls by one step every 50 ms until it reaches zero. Re-pairing with the
BOOT button sets both outputs to zero before the restart.

## Serial output

The serial port (UART0, 115200 baud) carries human-readable diagnostics and the
pairing-code query. Output is rate-limited so the query stays usable:

- `DAC values: cpu=… mem=…` only when the values change, at most every 10 s;
- a `Status:` line every 60 s. While connected it reads
  `Status: wifi=connected ip=… rssi=… source=… mqtt=… dac=… cpu=… mem=… heap=… min_heap=… status_last=…`;
  otherwise `Status: wifi=<state> source=… last_reason=… mqtt=… dac=… cpu=… mem=… heap=… min_heap=… status_last=…`.
  `cpu` and `mem` are the current 0–255 output values; `status_last` is the time since
  the last status publish attempt, which does not confirm delivery;
- the pairing hint line every 30 s while in pairing mode.

The SSID is printed once per connection; passwords are never printed.

## Storage

| Data | Where | Cleared by |
| --- | --- | --- |
| Wi-Fi credentials | ESP-IDF Wi-Fi storage in NVS (not encrypted) | BOOT gesture, flash erase |
| Pairing code | NVS namespace `resmon`, key `pop` | Flash erase only |
| Re-pairing request | NVS namespace `resmon`, key `prov_req` | Next boot |
| Failed provisioning starts | RTC memory | Any reset other than a software restart (power-on, EN/reset button, brownout, watchdog), a successful start, giving up, BOOT gesture |

The `secrets.h` fallback is applied with RAM-only Wi-Fi storage and is never written to
NVS.

## Configuration reference

Selected settings from [`config.h`](../firmware/src/config.h):

| Setting | Value | Purpose |
| --- | --- | --- |
| `FIRMWARE_BUILD_ID` | `"phase4-ble-prov-status"` | Reported in diagnostics and `resmon/status` |
| `I2C_SDA_PIN`, `I2C_SCL_PIN` | 21, 22 | I2C bus |
| `DAC_I2C_ADDRESS` | `0x59` | GP8413 DAC |
| `CPU_OUTPUT_CHANNEL`, `MEM_OUTPUT_CHANNEL` | 0, 1 | DAC channels |
| `METRIC_TIMEOUT_MS` | 5000 | Time without updates before a metric decays |
| `BOOT_BUTTON_MIN_HOLD_MS`, `BOOT_BUTTON_MAX_HOLD_MS` | 5000, 15000 | Re-pairing hold window |
| `MDNS_HOSTNAME` | `"resmon"` | mDNS and DHCP host name |
| `WIFI_RECONNECT_MIN_MS`, `WIFI_RECONNECT_MAX_MS` | 5000, 60000 | Reconnect backoff |
| `PROV_STOP_AFTER_SUCCESS_MS`, `PROV_BLE_CLEANUP_DELAY_MS` | 30000, 5000 | How long BLE stays up after a successful pairing |
| `PROV_START_MAX_ATTEMPTS` | 3 | Failed provisioning starts before giving up |
| `MQTT_BROKER_PORT` | 1883 | Broker port |
| `CPU_TOPIC`, `MEM_TOPIC`, `STATUS_TOPIC` | `resmon/cpu`, `resmon/mem`, `resmon/status` | MQTT topics |
| `ENABLE_STATUS_TOPIC` | 1 | Set to 0 (e.g. `-DENABLE_STATUS_TOPIC=0`) to disable the status topic |
| `STATUS_INTERVAL_MS` | 10000 | Status publish interval |
