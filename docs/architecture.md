# Architecture

```text
 Windows PC                                   ESP32
+-----------------------------+              +------------------------------+
| ResourceMonitorDaemon       |  resmon/cpu  | MQTT broker (port 1883)      |
|   CPU/RAM sampler ----------+------------->|   validate, clamp, map       |
|                             |  resmon/mem  |          |                   |
|                             +------------->|          v                   |
|                             |              |   I2C DAC ---> CPU gauge     |
|                             |              |           ---> MEM gauge     |
|   status log <--------------+--------------+-- resmon/status every 10 s   |
|                             |              |                              |
|   pairing wizard -----------+---- BLE ---->|   Wi-Fi provisioning         |
|   pairing-code reader ------+---- USB ---->|   RESMON?POP                 |
+--------------+--------------+              +------------------------------+
               |
               v  optional: logs, metrics and status to your own Grafana Loki
```

## Responsibilities

**The daemon** knows how busy the PC is. It samples, formats and publishes percentages,
manages the connection, and helps the user pair a device. It knows nothing about DAC
channels, output scaling or gauge behaviour.

**The firmware** owns everything physical: it maps percentages to DAC codes, clamps
them, and decides what the gauges show when data stops arriving. It does not depend on
the daemon to reach a safe state: a stale metric falls to zero on its own after 5 s.

## Design decisions

- **The ESP32 hosts the broker.** One PC and one device on a home or lab network need no
  separate MQTT server. The device is reachable as `resmon.local` via mDNS; the pairing
  wizard also saves its IP address as a fallback for networks where mDNS is unreliable.
- **Plain MQTT on a trusted LAN.** QoS 0, nothing retained, no authentication or TLS.
  The latest value is all that matters, and losing a sample is harmless. Anything on
  the LAN can publish to the gauges or subscribe to the status, which is why the status
  payload carries no network identifiers and the daemon rate-limits what it forwards.
- **Minimal payloads.** A metric is a bare decimal string, and the topic says which
  metric it is. This keeps the firmware parser trivial and easy to test.
- **Credentials belong to the device.** Wi-Fi credentials go from the wizard to the
  ESP32 over an encrypted BLE session (ESP-IDF's official provisioning, no custom
  crypto) and are stored only there. The PC never stores the Wi-Fi password.
- **Non-blocking everywhere.** The firmware's `loop()` and the daemon's sampling loop
  never wait for the network; connecting, reconnecting and pushing to Loki happen in the
  background with bounded backoff.
- **Optional hardware fails safe.** The firmware compiles and runs without a DAC and
  reports it in its diagnostics. The daemon runs without a device and keeps retrying.
- **Testable cores.** Firmware decisions live in pure headers under `firmware/include`
  with Unity tests; the daemon's protocol, settings and wizard logic are tested without
  hardware against fakes and loopback servers.

## Where to read next

| Topic | Document |
| --- | --- |
| MQTT topics and payloads | [protocol.md](protocol.md) |
| Pairing, security model, BLE protocol | [provisioning.md](provisioning.md) |
| Firmware structure and behaviour | [firmware.md](firmware.md) |
| Daemon structure and behaviour | [daemon.md](daemon.md) |
| Optional Loki forwarding | [loki.md](loki.md) |
| Parts, wiring, safety | [hardware.md](hardware.md) |
| What's next | [roadmap.md](roadmap.md) |
