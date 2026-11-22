# Roadmap

## Done

- MQTT metrics from the Windows daemon to a broker on the ESP32, with payload validation,
  clamped DAC output and stale-value decay on the device.
- Non-blocking Wi-Fi on the firmware with bounded reconnect backoff; no credentials in
  source control (optional ignored `secrets.h` for development).
- Layered daemon settings (defaults, `appsettings.json`, per-user file, `RESMON_`
  environment variables) applied live, with a tray settings dialog.
- Non-blocking daemon reconnects with backoff and a fallback IPv4 address.
- BLE Wi-Fi provisioning (Security1 with a pairing code) in the firmware, and a pairing
  wizard in the daemon that reads the code over USB and saves the device's IP address.
- `resmon/status` device health topic, and optional forwarding of logs, metrics and
  status to the user's own Grafana Loki.
- English and Simplified Chinese UI.
- Pinned, reproducible firmware builds; unit tests for the firmware's pure logic and for
  the daemon.

## Next

1. **Validate on hardware.** Pairing, the BOOT gesture and the status topic have only
   been tested in software. Run the [hardware checklist](provisioning.md#testing-on-hardware),
   check that a status subscriber receives one message every 10 s without disturbing the
   gauges, and that a subscriber which stops reading is dropped by keep-alive.
2. **Pair end to end** from the Windows wizard against a real device, including the USB
   pairing-code read on this board's USB-UART bridge.
3. **Bench the analogue chain**: confirm the DAC module, the gauges' input requirements
   and any conditioning they need, then fill in the [connection table](hardware.md#connections).
4. **Validate the backlight supply**: the boost converter under the real backlight load.
5. **Disk activity in the daemon**: sample disk throughput locally for diagnostics. It
   will not be published or given a gauge until there is hardware for it.

## Open hardware decisions

- Which DAC module is on the bench, and whether it matches the GP8413 firmware target.
- Whether the gauges accept 0–5 V directly or need current drive, buffering,
  attenuation or calibration.
- More gauges (disk, network) need more DAC channels: the GP8413 has two. Pick the
  hardware once the number of gauges and their voltage requirements are known.
- Total 12 V backlight current, whether the boost converter has enough continuous
  current and thermal margin, and whether the rail needs a switch, fuse or connector
  harness.
- Enclosure material, antenna clearance, and how users reach the BOOT button.

## Open software decisions

- **Encrypting stored credentials.** Wi-Fi credentials are currently stored without NVS
  or flash encryption. Enabling encryption on real devices is a deliberate production
  step: key handling, recovery, firmware updates and factory reset have to be designed
  first.
- **Disk gauge semantics**: active-time percentage or normalised throughput.
- **Provisioning more than Wi-Fi.** The device name, MQTT port and topics are compile-time
  settings. Provisioning them would need a custom provisioning endpoint with versioned,
  validated settings that leave a working configuration intact on error.
- **PCs without Bluetooth LE.** There is no fallback yet. If one is added (for example
  SoftAP), it should only exist during provisioning and must be authenticated and
  encrypted, never a plaintext or permanent configuration API.
- **Password recall on the PC.** The daemon never stores the Wi-Fi password. If a later
  workflow needs to resend it, it should be opt-in and protected with Windows DPAPI for
  the current user.
