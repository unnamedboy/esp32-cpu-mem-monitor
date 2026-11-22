# MQTT protocol

The ESP32 runs the MQTT broker ([PicoMQTT](https://github.com/mlesniew/PicoMQTT)) on
TCP port 1883 and advertises itself over mDNS as `resmon.local` (service `_mqtt._tcp`).
The Windows daemon connects as a client, publishes the two metrics and subscribes to
the device status. There is no TLS and no authentication: the broker is meant for a
single device on a trusted home or lab LAN.

| Topic | Direction | Payload | QoS | Retained |
| --- | --- | --- | --- | --- |
| `resmon/cpu` | daemon → ESP32 | CPU usage, ASCII decimal percentage `0..100`, e.g. `42.5` | 0 | no |
| `resmon/mem` | daemon → ESP32 | Memory usage, same format, e.g. `68.0` | 0 | no |
| `resmon/status` | ESP32 → subscribers | Device health, compact JSON, at most 256 bytes ([below](#device-status)) | 0 | no |

The topic identifies the metric; payloads have no envelope, timestamp or version.

The daemon's topic names are configurable (`Device:CpuTopic`, `Device:MemTopic`,
`Device:StatusTopic`); the firmware's are compile-time constants in
[`firmware/src/config.h`](../firmware/src/config.h) (`CPU_TOPIC`, `MEM_TOPIC`,
`STATUS_TOPIC`). If you change one side, change the other.

## Metric payloads

The daemon formats each value with one decimal place in the invariant culture
(always `.` as the decimal separator) and clamps it to `0.0..100.0` before publishing.

The firmware parses the payload as a float, allows trailing whitespace, and ignores
anything that is not a finite number in `0..100`. CPU and memory are handled
independently. A metric that receives no valid update for 5 s (`METRIC_TIMEOUT_MS`)
starts falling towards zero by one 8-bit output step every 50 ms, so a full-scale
needle reaches zero in about 13 s. This is how the gauges show that the PC has stopped
sending, for example when it sleeps or the daemon is paused.

To exercise the device without the daemon, any MQTT client works, for example:

```sh
mosquitto_pub -h resmon.local -t resmon/cpu -m 42.5
mosquitto_sub -h resmon.local -t resmon/status -v
```

## Device status

The firmware publishes a JSON health snapshot to `resmon/status` on its own broker.
Only clients subscribed to the topic receive it.

- Every 10 s (`STATUS_INTERVAL_MS`) while Wi-Fi is connected. The first message is sent
  10 s after the broker starts.
- QoS 0, not retained, at most 256 bytes (`STATUS_PAYLOAD_MAX`).
- Enabled by default; build with `-DENABLE_STATUS_TOPIC=0` to turn it off.

```json
{"v":1,"build":"example-build","uptime_s":1234,"heap_free":182344,"heap_min":150000,"rssi":-61,"wifi":"connected","prov":"provisioned","dac":true}
```

| Field | Type | Meaning |
| --- | --- | --- |
| `v` | int | Schema version, currently `1` |
| `build` | string | `FIRMWARE_BUILD_ID`, JSON-escaped, at most 64 bytes |
| `uptime_s` | int | Seconds since boot (`millis() / 1000`, wraps after about 49 days) |
| `heap_free` | int | Free heap in bytes |
| `heap_min` | int | Lowest free heap since boot, in bytes |
| `rssi` | int | Wi-Fi signal in dBm; omitted when it cannot be read |
| `wifi` | string | `connected`, `connecting` or `disconnected` |
| `prov` | string | `provisioned` (BLE-provisioned credentials), `dev-fallback` (compiled `secrets.h`), `provisioning` or `unconfigured` |
| `dac` | bool | Whether the external DAC was detected at startup |

Because the status is only published while Wi-Fi is connected, current firmware
always sends `"wifi":"connected"` and `prov` = `provisioned` or `dev-fallback`. The
other values are reserved in the schema.

The payload never contains the SSID, passwords, the pairing code, IP addresses or MAC
addresses.

Rules for consumers:

- Ignore unknown fields.
- Reject a message whose `v` is higher than the version you support.
- Treat a missing status topic as normal: older firmware does not publish it.

### How the daemon uses it

The daemon subscribes to `Device:StatusTopic` after every connect (it uses a clean
session, so each reconnect needs a new subscription). It accepts a message only if it
is valid UTF-8, at most 1 KB, a JSON object, and has an integer `v` of 1. Valid
messages are rate-limited to a burst of 3 followed by one message per 5 s; messages
over the limit are only counted, with a warning at most once a minute. A message
within the limit is logged at Debug level and, if the optional [Loki sink](loki.md)
is enabled, forwarded with only the fields listed above, type-checked and
re-serialized in that order. The broker is unauthenticated, so the rate limit stops
another client on the LAN from flooding the log or your Loki.

### Known limitation

The firmware writes status messages to each subscriber's TCP connection from its main
loop. A client that keeps its connection open but stops reading (for example a
suspended `mosquitto_sub`) fills the ESP32's send buffer after about 20 messages. After
that, each publish can stall the loop for 10 s or more. MQTT keep-alive normally drops
such a client first; this has not yet been checked on hardware.
