# MQTT Communication Contract

The ESP32 is an edge display device and hosts the MQTT broker for this prototype.

| Metric | Topic | Payload | QoS | Retained |
| --- | --- | --- | --- | --- |
| CPU utilization | `resmon/cpu` | ASCII decimal percentage in the range `0..100`, for example `42.5` | 0 | No |
| Memory utilization | `resmon/mem` | ASCII decimal percentage in the range `0..100`, for example `68.0` | 0 | No |

The topic identifies the metric. Payloads contain no JSON envelope, timestamp, or version field.

The ESP32 validates each payload and ignores malformed or out-of-range values. CPU and memory values expire independently after five seconds without a valid update, then decay smoothly to zero for display purposes.
