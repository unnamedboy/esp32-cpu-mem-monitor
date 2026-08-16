# Development Plan

## Product Direction

Build a self-contained ESP32 edge display for Windows CPU and memory usage.
The Windows daemon samples host metrics and publishes the latest values. The
ESP32 owns display behavior, including DAC mapping and stale-data fade-out.

## Current State

Status as of 2026-08-16:

- Phase 1: Complete
- Phase 2: Complete
- Phase 3: In progress / not complete
- Phase 4: Planned
- Phase 5: Planned

Completed implementation summary:

- MQTT is the only supported runtime transport.
- The ESP32 hosts the MQTT broker for this single-device, trusted-LAN setup.
- CPU and memory use `resmon/cpu` and `resmon/mem`.
- Each payload is an ASCII decimal percentage in the range `0..100`.
- The ESP32 rejects invalid values and fades each stale metric independently.
- Firmware Wi-Fi credentials are no longer tracked. Local development uses
  ignored `firmware/src/secrets.h`, copied from
  `firmware/src/secrets.example.h`.
- A top-level README documents the real project state and local setup flow.
- C++ PlatformIO tests cover metric payload parsing and DAC value mapping.
- C# tests cover MQTT percentage payload formatting and clamping.

See [COMMUNICATION.md](COMMUNICATION.md) for the active message contract.
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

### Remaining Work

1. Add a firmware diagnostic for missing `WIFI_SSID` so an unconfigured build
   fails visibly instead of waiting forever for Wi-Fi.
2. Avoid printing SSID values unless useful for diagnosis; never print passwords.

## Phase 3: Windows Configuration Module

Status: In progress / not complete

### Goal

Move daemon connection settings out of source code and prepare a first-run
setup wizard without making the real-time sampling path more complex.

### Module Interfaces

- `MetricsSampler`: returns current CPU and memory percentages.
- `MetricPublisher`: publishes a percentage to the configured MQTT topic.
- `MqttConnection`: owns connection state and bounded reconnect backoff.
- `DeviceSettings`: owns non-secret values such as device host, port, topics,
  and sampling interval.
- `ProvisioningTransport`: accepts Wi-Fi credentials for one provisioning
  attempt and does not retain them by default.

### Implementation Steps

1. Add `appsettings.json` for non-secret daemon settings.
2. Support environment-variable overrides for automation and diagnostics.
3. Replace hard-coded daemon constants with `DeviceSettings`.
4. Add a first-run wizard and a tray-menu entry for changing settings.
5. Keep Wi-Fi passwords in memory only while a provisioning operation is in
   progress.
6. Do not persist Wi-Fi passwords on the Windows PC by default.

### Optional Windows Password Recall

If a later workflow genuinely needs the PC to resend a saved password, protect
the encrypted value with Windows DPAPI using the current-user scope and store
only the ciphertext under the application's LocalAppData directory. This is an
opt-in feature, not a prerequisite for the wizard.

## Phase 4: ESP32 Provisioning and Secure Storage

Status: Planned

### Goal

Allow Wi-Fi configuration without firmware recompilation while protecting
credentials at rest and in transit.

### Desired User Flow

1. User flashes a generic firmware image with no Wi-Fi password in source code.
2. On first boot, the ESP32 detects that Wi-Fi is not configured.
3. The ESP32 enters an explicit provisioning mode and advertises itself as a
   setup target.
4. The Windows client discovers the setup target and opens a setup wizard.
5. The wizard asks for:
   - Wi-Fi SSID;
   - Wi-Fi password;
   - optional device name, defaulting to `resmon`;
   - optional MQTT port and topics, using current defaults.
6. The client sends the configuration to the ESP32 over an authenticated and
   encrypted provisioning channel.
7. The ESP32 stores the credential, exits provisioning mode, joins Wi-Fi, starts
   mDNS, and starts the MQTT broker.
8. The client forgets the Wi-Fi password by default and switches into normal
   metric publishing mode.

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

## Verification Checklist

For every firmware change:

1. Run `platformio run` from the repository root.
2. Verify startup diagnostics show build, feature, I2C, DAC, heap, Wi-Fi, and
   MQTT state without exposing secrets.
3. Test valid, invalid, stale, and reconnecting metric messages.
4. Verify absent optional hardware fails safely.

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
