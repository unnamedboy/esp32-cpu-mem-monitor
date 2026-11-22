# MQTT Communication Contract

The ESP32 is an edge display device and hosts the MQTT broker for this prototype.

| Metric | Topic | Payload | QoS | Retained |
| --- | --- | --- | --- | --- |
| CPU utilization | `resmon/cpu` | ASCII decimal percentage in the range `0..100`, for example `42.5` | 0 | No |
| Memory utilization | `resmon/mem` | ASCII decimal percentage in the range `0..100`, for example `68.0` | 0 | No |

The topic identifies the metric. Payloads contain no JSON envelope, timestamp, or version field.

The ESP32 validates each payload and ignores malformed or out-of-range values. CPU and memory values expire independently after five seconds without a valid update, then decay smoothly to zero for display purposes.

## Device status topic (optional)

Published by firmware ≥ the status-topic branch; **optional**. Older firmware does not publish it,
and the daemon treats a missing status topic as normal (not an error). The `resmon/cpu` and
`resmon/mem` contract above is unchanged.

| Data | Topic | Direction | Payload | QoS | Retained |
| --- | --- | --- | --- | --- | --- |
| Device health | `resmon/status` | ESP32 → daemon (published into the ESP32's own broker) | compact UTF-8 JSON object, ≤ 256 bytes | 0 | No |

- Topic name: firmware `STATUS_TOPIC` in `config.h`; daemon setting `Device:StatusTopic`
  (default `resmon/status`, no wildcards, must differ from the metric topics).
- Interval: every 10 s (`STATUS_INTERVAL_MS`), plus once immediately after the broker starts.
- The daemon subscribes (QoS 0) after every (re)connect, because it uses a clean session.

Example:

```json
{"v":1,"build":"phase4-ble-prov","uptime_s":1234,"heap_free":182344,"heap_min":150000,
 "rssi":-61,"wifi":"connected","prov":"provisioned","dac":true}
```

| Field | Type | Meaning |
| --- | --- | --- |
| `v` | int | Schema version, currently `1`. Consumers ignore unknown fields and reject `v` greater than they support. |
| `build` | string | `FIRMWARE_BUILD_ID` |
| `uptime_s` | int | `millis()/1000` (wraps after about 49 days; acceptable) |
| `heap_free` | int | `ESP.getFreeHeap()` |
| `heap_min` | int | `ESP.getMinFreeHeap()` |
| `rssi` | int | `WiFi.RSSI()` when connected; omitted otherwise |
| `wifi` | string | `connected` / `connecting` / `disconnected` |
| `prov` | string | `provisioning` / `provisioned` / `dev-fallback` / `unconfigured` |
| `dac` | bool | External DAC detected |

Never included: SSID, passwords, proof of possession (PoP), IP addresses, MAC addresses.

Daemon validation: the payload must be valid UTF-8, at most 1 KB, a JSON object, and contain an
integer `v` between 1 and the supported version (1). Invalid messages are ignored, with one local
log line per kind of problem. Valid messages are logged locally at Debug level and, only if the
optional Loki sink is enabled, forwarded unchanged to the stream `{app="resmon-device", kind="status"}`.

## Optional daemon → Grafana Loki streams

Disabled by default (`Observability:Loki:Enabled = false`); see the README. When enabled, the daemon
pushes to the user's own Loki with only these low-cardinality label sets (plus `host="<PC name>"`
when `IncludeHostLabel` is set):

| Stream labels | Line content |
| --- | --- |
| `{app="resmon-daemon", kind="log", level="<trace\|debug\|info\|warn\|error\|critical>"}` | Daemon log line `Category: message` (local-only categories excluded) |
| `{app="resmon-daemon", kind="metrics"}` | One JSON line per `MetricsIntervalSeconds`: `{"cpu":12.3,"mem":45.6,"mem_used_gb":7.21,"mem_total_gb":15.86,"mqtt":"connected"}` (`mqtt` is `connected`, `disconnected` or `paused`) |
| `{app="resmon-device", kind="status"}` | The raw validated `resmon/status` JSON as received |
