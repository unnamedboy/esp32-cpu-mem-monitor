# Development Plan

## Product Direction

Build a self-contained ESP32 edge display for Windows CPU and memory usage.
The Windows daemon samples host metrics and publishes the latest values. The
ESP32 owns display behavior, including DAC mapping and stale-data fade-out.

## Current State

Status as of 2026-11-22:

- Phase 1: Complete
- Phase 2: Complete (including the former remaining work)
- Phase 3: Complete (settings module and pairing wizard)
- Phase 4: Implemented on both sides (firmware BLE provisioning and the Windows
  client/wizard); not yet validated on hardware
- Phase 5: Planned

Completed implementation summary:

- MQTT is the only supported runtime transport.
- The ESP32 hosts the MQTT broker for this single-device, trusted-LAN setup.
- CPU and memory use `resmon/cpu` and `resmon/mem`.
- Each payload is an ASCII decimal percentage in the range `0..100`.
- The ESP32 rejects invalid values and fades each stale metric independently.
- Optional observability: the firmware publishes the device status topic
  `resmon/status`. The daemon subscribes to it and can push its log, a metrics
  line and the device status to the user's own Grafana Loki. Loki is disabled
  by default, and the token is stored DPAPI-encrypted. Contract and streams:
  COMMUNICATION.md; see Observability below.
- Firmware Wi-Fi credentials are no longer tracked. Local development uses
  ignored `firmware/src/secrets.h`, copied from
  `firmware/src/secrets.example.h`.
- A top-level README documents the real project state and local setup flow.
- C++ PlatformIO tests cover metric payload parsing and DAC value mapping,
  plus the provisioning boot decision, pairing code, serial PoP query
  parsing, reconnect backoff, provisioning-start retry policy, the BOOT
  button hold-and-release window, and the `resmon/status` payload.
- Firmware Wi-Fi startup is non-blocking and BLE provisioning is implemented
  per [PROVISIONING.md](PROVISIONING.md) (hardware validation pending).
- The firmware publishes a `resmon/status` device-health JSON snapshot every
  10 s into its own broker (see [Observability](#observability); hardware
  validation pending).
- C# tests cover MQTT percentage payload formatting and clamping.
- The daemon reads `DeviceSettings` from defaults < `appsettings.json` <
  `%LocalAppData%\ResourceMonitorDaemon\settings.json` < `RESMON_*`
  environment variables, applies changes live, and reconnects in the
  background without blocking sampling.
- The tray's "配对设备 (Pair device)…" wizard provisions Wi-Fi over BLE
  (Security1 with an 8-character pairing code) and saves the reported IPv4
  address as `FallbackAddress`. It does not store the Wi-Fi password. It has
  not been tested against a physical ESP32 yet.

See [COMMUNICATION.md](COMMUNICATION.md) for the active message contract.
See [PROVISIONING.md](PROVISIONING.md) for the BLE pairing contract.
See [HARDWARE.md](HARDWARE.md) for the current hardware design and open
electrical/mechanical decisions.

## Target Architecture

```text
Windows daemon
  MetricsSampler -> MetricPublisher -> MQTT
                                      |
                                      v
ESP32 edge device
  MQTT broker -> EdgeDisplayController -> DAC outputs

Windows setup wizard -> ProvisioningTransport -> ESP32 Wi-Fi configuration
```

The Windows daemon must not know about DAC channels, output scaling, or
fade-out behavior. The ESP32 must not depend on the daemon to enter a safe,
visually consistent stale-data state.

## Phase 2: Configuration and Credential Safety

Status: Complete

### Goal

Remove Wi-Fi credentials from tracked source files and establish a secure,
reproducible configuration path for development.

### Completed

- `firmware/src/config.h` loads ignored `firmware/src/secrets.h` when present.
- `firmware/src/secrets.example.h` provides placeholder values only.
- `.gitignore` excludes `firmware/src/secrets.h`, `bin/`, and `obj/` output folders.
- Startup diagnostics print Wi-Fi state but not Wi-Fi passwords.
- (2026-11-22) The blocking `while (WiFi.status() != WL_CONNECTED)` wait is
  gone. `loop()` runs a non-blocking connection state machine with bounded
  reconnect backoff; DAC stale-decay keeps running while Wi-Fi is down; mDNS
  and the MQTT broker start once on the first connection.
- (2026-11-22) A build with no credentials at all no longer waits forever: it
  logs `no stored credentials and no secrets.h fallback` and enters BLE
  provisioning (Phase 4). If provisioning cannot start it logs a rate-limited
  error instead.
- (2026-11-22) The SSID is printed once per successful connection only;
  passwords are never printed. Wi-Fi diagnostics are rate-limited.
- (2026-11-22) The PlatformIO platform (`espressif32 @ 7.0.1`) and the
  DFRobot_GP8XXX library commit are pinned so builds are reproducible.

### Remaining Work

None.

## Phase 3: Windows Configuration Module

Status: Complete. Steps 1–6 are implemented. The pairing wizard (step 4) has
been tested with unit tests and a start-up smoke test only; it has not run
against a physical ESP32 yet (see Phase 4).

### Goal

Move daemon connection settings out of source code and prepare a first-run
setup wizard without making the real-time sampling path more complex.

### Module Interfaces

- `MetricsSampler`: returns current CPU and memory percentages.
- `MetricPublisher`: publishes a percentage to the configured MQTT topic.
- `MqttConnection`: owns connection state and bounded reconnect backoff
  (implemented; non-blocking, Host then FallbackAddress).
- `DeviceSettings`: owns non-secret values such as device host, port, topics,
  and sampling interval (implemented, with `UserSettingsStore` for the
  per-user settings file).
- `ProvisioningTransport`: accepts Wi-Fi credentials for one provisioning
  attempt and does not retain them by default.

### Implementation Steps

1. Done: Add `appsettings.json` for non-secret daemon settings.
2. Done: Support environment-variable overrides for automation and diagnostics
   (`RESMON_` prefix, e.g. `RESMON_Device__Host`).
3. Done: Replace hard-coded daemon constants with `DeviceSettings`
   (validated, sanitized to defaults, applied live via `IOptionsMonitor`).
4. Done: tray-menu entry "⚙ 设置 (Settings)…" edits and saves settings
   through `UserSettingsStore`. Done: the "配对设备 (Pair device)…" wizard
   (`Pairing/PairingWizardForm`), opened from the tray or once from a
   first-run "click to pair" balloon. After a successful pairing it writes only
   `FallbackAddress` with `ISettingsStore.Update(p => p.FallbackAddress = ip)`.
5. Done: the Wi-Fi password exists only in the wizard's password box and the
   `ProvisionAsync` call. The box is cleared after every attempt, and the
   password is never logged.
6. Done: Wi-Fi passwords are not stored on the Windows PC (no DPAPI recall).

### Optional Windows Password Recall

If a later workflow genuinely needs the PC to resend a saved password, protect
the encrypted value with Windows DPAPI using the current-user scope and store
only the ciphertext under the application's LocalAppData directory. This is an
opt-in feature, not a prerequisite for the wizard.

## Phase 4: ESP32 Provisioning and Secure Storage

Status: Implemented on both sides; **not yet validated on hardware** or end to
end. Firmware side: see [PROVISIONING.md](PROVISIONING.md). Windows side: the BLE
provisioning client (`daemon/ResourceMonitorDaemon/Provisioning`) and the pairing
wizard implement steps 4–6 and 8 of the desired user flow; steps 1–3 and 7 belong
to the firmware. The optional fields in step 5 (device name, MQTT port and
topics) are not offered: the BLE provisioning contract carries only the SSID and
password.

### Goal

Allow Wi-Fi configuration without firmware recompilation while protecting
credentials at rest and in transit.

### Implemented (firmware)

The contract is [PROVISIONING.md](PROVISIONING.md). Summary:

- BLE provisioning through the ESP-IDF `wifi_provisioning` manager (called
  directly so auto-stop can be disabled; BLE stays up ~35 s after success), Security1 with an 8-character Proof of Possession, advertised as
  `PROV_RESMON_XXXXXX`; BT memory is released after provisioning.
- The PoP is generated once with the hardware RNG and stored in NVS
  (`resmon/pop`). It is shown on USB serial only in provisioning mode (a
  rate-limited hint line and the `RESMON?POP` query).
- Boot order: BOOT re-provisioning request → stored credentials → compiled
  `secrets.h` dev fallback (RAM-only) → provisioning.
- Holding the on-board BOOT button (GPIO0) for 5–15 s and releasing it resets the stored
  credentials and restarts into provisioning.
- Pure decision/format logic lives in `firmware/include` with Unity tests.

### Remaining Work

1. Bench-test on the ESP32-WROOM-32 + MOC9686 board: first-boot pairing,
   wrong PoP, wrong password / unknown SSID, re-pair via the BOOT hold-and-release gesture,
   dev fallback, reconnect after AP loss, BT memory release (heap before
   and after), BLE staying up ~35 s after success, the provisioning-start
   retry path, the BOOT gesture with a serial tool attached, and an AP loss
   during the BLE grace period (credentials must survive). Checklist and
   results in PROVISIONING.md.
2. End-to-end test with the Windows pairing client.
3. Decide on NVS/flash encryption before treating stored credentials as
   product-grade (see Storage Policy).
4. Optional device/MQTT settings from the payload below are **not**
   provisioned: the provisioning manager carries only SSID and passphrase, and hostname,
   port, and topics stay compile-time defaults. Adding them would need a
   custom protocomm endpoint and is deferred.

### Desired User Flow

1. User flashes a generic firmware image with no Wi-Fi password in source code.
2. On first boot, the ESP32 detects that Wi-Fi is not configured.
3. The ESP32 enters an explicit provisioning mode and advertises itself as a
   setup target.
4. The Windows client discovers the setup target and opens a setup wizard.
   (Client: done. Tray menu or one-time first-run balloon; BLE scan for
   `PROV_RESMON_*`.)
5. The wizard asks for:
   - Wi-Fi SSID;
   - Wi-Fi password;
   - optional device name, defaulting to `resmon`;
   - optional MQTT port and topics, using current defaults.
   (Client: SSID and password done, plus the pairing code, read over USB or
   typed. The optional fields are not part of the BLE contract.)
6. The client sends the configuration to the ESP32 over an authenticated and
   encrypted provisioning channel. (Client: done; Security1 + pairing code.)
7. The ESP32 stores the credential, exits provisioning mode, joins Wi-Fi, starts
   mDNS, and starts the MQTT broker.
8. The client forgets the Wi-Fi password by default and switches into normal
   metric publishing mode. (Client: done. It saves the reported IPv4 as
   `FallbackAddress`, reconnects live and shows "已连接，正在发送数据".)

### Client-to-ESP32 Configuration Payload

The provisioning payload should be versioned from the start:

```json
{
  "version": 1,
  "wifi": {
    "ssid": "example",
    "password": "secret"
  },
  "device": {
    "hostname": "resmon"
  },
  "mqtt": {
    "port": 1883,
    "cpuTopic": "resmon/cpu",
    "memTopic": "resmon/mem"
  }
}
```

Rules:

- The password is accepted only during provisioning and is never logged.
- The client does not persist the password by default.
- The ESP32 validates hostname, port, and topic strings before saving.
- Bad configuration must leave the existing working configuration intact.
- A factory reset or explicit provisioning button is required to replace saved
  Wi-Fi credentials on a configured device.

### Provisioning Policy

1. Prefer BLE provisioning when the Windows system supports it.
2. If SoftAP fallback is required, treat it as a temporary provisioning
   transport only; do not add a runtime configuration API.
3. Use an authenticated and encrypted provisioning session; do not invent a
   plaintext BLE credential protocol.
4. Disable BLE or SoftAP provisioning after successful connection.
5. Require an explicit user action, such as a physical reset control, to reopen
   provisioning on an already configured device.

### Storage Policy

- The ESP32 is the long-term owner of the Wi-Fi credential.
- Development storage may use the platform's standard Wi-Fi credential store.
- Product-grade storage requires NVS encryption backed by flash encryption.
- Enabling device encryption is a deliberate production decision: document key
  handling, recovery, firmware-update, and factory-reset behavior before it is
  enabled on physical devices.

### Technology Decision

The current firmware uses Arduino through PlatformIO. Before implementing BLE
provisioning, evaluate whether to:

- use a maintained Arduino-compatible provisioning implementation; or
- move the provisioning layer, or the firmware, to ESP-IDF and use its official
  provisioning manager.

Do not implement custom BLE credential encryption unless there is a documented
reason the supported provisioning paths cannot be used.

Decision (2026-11-22): stay on Arduino through PlatformIO and use the
official ESP-IDF provisioning manager that ships with the core (the same one
`WiFiProv` wraps, called directly so auto-stop can be disabled). No custom
crypto. Consequences: Security1 only (core 2.0.17 has no Security2),
and the larger BLE image needs the `min_spiffs.csv` partition table (OTA
layout kept, SPIFFS reduced to 128 KB).

## Phase 5: Additional Metrics and Hardware Expansion

Status: Planned

### Scope

- Keep physical outputs limited to CPU and memory while using the two-channel
  GP8413 DAC.
- Validate the actual DAC module, gauge input requirements, output loading, and
  any required signal conditioning before making enclosure or permanent wiring
  decisions.
- Validate the selected 5 V to 12 V boost converter under the actual voltmeter
  backlight load before committing to permanent wiring or enclosure placement.
- Add system-level disk activity monitoring in the Windows daemon first.
- Record disk read/write throughput locally for diagnostics.
- Do not publish disk I/O or allocate a DAC channel until a physical display
  requirement is confirmed.

### Future Hardware Decision

The GP8413 supports two analogue output channels. A physical disk-I/O or
network gauge will require another DAC or a different multi-channel DAC.
Choose that hardware only when the number and voltage requirements of future
gauges are known and documented.

## Observability

Status: Firmware and daemon sides implemented (2026-11-22); not yet validated on hardware

- Firmware: `resmon/status` (QoS 0, not retained, every 10 s while Wi-Fi is
  connected, the first one 10 s after the broker starts) carries schema version,
  build id, uptime, free/minimum heap, RSSI, Wi-Fi and provisioning state and
  DAC presence. Contract: [COMMUNICATION.md](COMMUNICATION.md). Formatting is
  a pure, unit-tested header (`firmware/include/status_payload.h`); feature
  flag `ENABLE_STATUS_TOPIC` in `firmware/src/config.h`.
- Daemon: subscribes to `Device:StatusTopic` (default `resmon/status`) after
  every reconnect and treats a missing topic as normal (older firmware). It
  can optionally forward its logs, a metrics line and the validated,
  rate-limited device status to the user's own Grafana Loki (README
  "Optional: Grafana Loki"; disabled by default).
- Remaining: confirm on hardware that a subscribed client (e.g.
  `mosquitto_sub -h resmon.local -t resmon/status -v`) receives one message
  every 10 s and that the CPU/MEM display is unaffected. Also check that a
  subscriber which stops reading (e.g. a suspended `mosquitto_sub`) is dropped
  by keep-alive before it can stall `loop()`.

## Verification Checklist

For every firmware change:

1. Run `platformio run -e esp32dev` from the repository root, and compile the
   tests with `platformio test -e esp32dev --without-uploading --without-testing`.
2. Verify startup diagnostics show build, feature, I2C, DAC, heap, Wi-Fi, and
   MQTT state without exposing secrets.
3. Test valid, invalid, stale, and reconnecting metric messages.
4. Verify absent optional hardware fails safely.
5. Verify `resmon/status` still arrives every 10 s and contains no SSID, IP,
   MAC, PoP or password.

For every daemon change:

1. Run `dotnet build` in an isolated output directory if the live daemon holds
   its installed executable open.
2. Verify reconnects do not block sampling.
3. Verify settings updates do not require recompilation.
4. Verify logs and notifications do not include credentials.

## Open Decisions

- Disk gauge semantics: disk active-time percentage or normalized throughput.
- Future physical I/O/network gauge count and DAC hardware choice.
- BLE support target for the Windows wizard and its SoftAP fallback UX.
- Production key-management and factory-reset policy before enabling flash/NVS
  encryption on deployed devices.
