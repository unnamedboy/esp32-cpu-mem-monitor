# MQTT Communication Contract

The ESP32 is an edge display device and hosts the MQTT broker for this prototype.

| Metric | Topic | Payload | QoS | Retained |
| --- | --- | --- | --- | --- |
| CPU utilization | `resmon/cpu` | ASCII decimal percentage in the range `0..100`, for example `42.5` | 0 | No |
| Memory utilization | `resmon/mem` | ASCII decimal percentage in the range `0..100`, for example `68.0` | 0 | No |
| Device status (ESP32 → daemon) | `resmon/status` | Compact JSON object, at most 256 bytes; see [Device status](#device-status-resmonstatus) | 0 | No |

The topic identifies the metric. The `resmon/cpu` and `resmon/mem` payloads contain no JSON envelope, timestamp, or version field.

The ESP32 validates each payload and ignores malformed or out-of-range values. CPU and memory values expire independently after five seconds without a valid update, then decay smoothly to zero for display purposes.

## Device status (`resmon/status`)

- Publisher: the ESP32 firmware, publishing into its own hosted broker (local
  publish). Only clients subscribed to `resmon/status` receive it.
- Topic: `resmon/status` (`STATUS_TOPIC` in
  [firmware/src/config.h](firmware/src/config.h); daemon setting
  `StatusTopic`, default the same).
- QoS 0, not retained. Published every 10 s (`STATUS_INTERVAL_MS`), plus once
  immediately after the broker starts, and only while Wi-Fi is connected.
  Firmware feature flag `ENABLE_STATUS_TOPIC` (default `1`).
- Payload: compact UTF-8 JSON object, at most 256 bytes
  (`STATUS_PAYLOAD_MAX`), built with `snprintf` into a fixed buffer.

```json
{"v":1,"build":"phase4-ble-prov","uptime_s":1234,"heap_free":182344,"heap_min":150000,"rssi":-61,"wifi":"connected","prov":"provisioned","dac":true}
```

| Field | Type | Meaning |
| --- | --- | --- |
| `v` | int | Schema version, currently `1`. Consumers ignore unknown fields and reject unknown `v` greater than the version they support. |
| `build` | string | `FIRMWARE_BUILD_ID` (JSON-escaped), ≤ 64 characters |
| `uptime_s` | int | `millis() / 1000` (wraps after about 49 days; acceptable) |
| `heap_free` | int | `ESP.getFreeHeap()` |
| `heap_min` | int | `ESP.getMinFreeHeap()` |
| `rssi` | int | Wi-Fi RSSI in dBm when connected; omitted otherwise |
| `wifi` | string | `connected` / `connecting` / `disconnected` |
| `prov` | string | `provisioning` / `provisioned` / `dev-fallback` / `unconfigured` |
| `dac` | bool | External DAC detected at startup |

Never included: SSID, passwords, PoP, IP addresses, MAC.

Firmware mapping (`firmware/include/status_payload.h`):

| Firmware state | `wifi` | `prov` |
| --- | --- | --- |
| `Connected`, credentials from the Wi-Fi NVS store (BLE-provisioned) | `connected` | `provisioned` |
| `Connected`, credentials from the compiled `secrets.h` fallback | `connected` | `dev-fallback` |
| `Connecting` (stored / dev fallback credentials) | `connecting` | `provisioned` / `dev-fallback` |
| `Provisioning` (BLE provisioning active) | `disconnected` | `provisioning` |
| `Starting` or `Error` (no credential source) | `disconnected` | `unconfigured` |

Because the status is only published while Wi-Fi is connected, current
firmware sends `wifi` = `connected` and `prov` = `provisioned` or
`dev-fallback`; the other values are part of the schema for completeness.
During the BLE grace period after a successful provisioning the device is
already connected and reports `provisioned`.

`rssi` is read with `esp_wifi_sta_get_ap_info()` rather than `WiFi.RSSI()`,
because the Arduino accessor reports nothing after in-boot BLE provisioning.
The firmware's `status_last` diagnostic counts publish attempts, including
those with no subscriber; it does not confirm delivery.

Consumers ignore unknown fields. A missing status topic is not an error: older
firmware does not publish it.

Known limitation: publishing writes to each subscriber's TCP connection from
`loop()`. A subscriber that keeps its connection alive but stops reading could,
once the ESP32 send buffer fills (about 20 status messages), stall `loop()` for
up to about 10 s per publish (`WiFiClient::write` retries). Keep-alive normally
drops such clients first. The `resmon/cpu` and `resmon/mem` contract above
is unchanged.
