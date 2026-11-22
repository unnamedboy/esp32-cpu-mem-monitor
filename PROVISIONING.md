# Wi-Fi Provisioning and Pairing Contract

This document is the contract between the ESP32 firmware and the Windows
(.NET) pairing client, plus a description of what the firmware actually
implements. Both sides must follow it exactly.

> **Validation status:** the firmware side builds and its pure logic is
> unit-tested, but BLE provisioning has **not yet been tested on physical
> hardware** or against the Windows client. Treat every behaviour below as
> implemented-but-unverified until a bench test is recorded here.

## Transport and security

- Transport: BLE, using the Arduino-ESP32 `WiFiProv` library (ESP-IDF
  `wifi_provisioning` manager) BLE scheme.
- Security: protocomm **Security1** (X25519 key exchange + AES-256-CTR)
  **with Proof of Possession (PoP)**. No custom crypto protocol. The .NET
  client reimplements the client side of the official protocol (reference:
  ESP-IDF `tools/esp_prov/security/security1.py`, `esp_prov/prov/wifi_prov.py`,
  `components/protocomm/proto/*.proto`, `components/wifi_provisioning/proto/*.proto`).
- BLE service UUID: `021a9004-0382-4aea-bff4-6b3f1c5adfb4` (Arduino WiFiProv
  default; the firmware passes it explicitly from `PROV_BLE_SERVICE_UUID` in
  [firmware/src/config.h](firmware/src/config.h)).
- BLE advertised device name / service name: `PROV_RESMON_XXXXXX`, where
  `XXXXXX` is the last 3 bytes of the Wi-Fi STA MAC in uppercase hex. The
  client discovers devices whose name starts with `PROV_RESMON_`.
  - Depending on the ESP-IDF BLE transport, the name may be carried in the
    scan response rather than the primary advertisement, so the client
    should scan actively. (Unverified on hardware.)
- GATT endpoints are identified by their Characteristic User Description
  descriptor (0x2901) text: `prov-session`, `prov-config`, `proto-ver`,
  `prov-scan` (optional for us). The client must look them up by descriptor
  text, not by hard-coded characteristic UUIDs.
- Security1 session (a single AES-CTR keystream is shared by every encrypt
  **and** decrypt after the session is set up, exactly as esp_prov
  `security1.py` does):
  1. client → `SessionData{sec_ver=SecScheme1, sec1=Sec1Payload{msg=Session_Command0, sc0=SessionCmd0{client_pubkey}}}`
  2. device → `SessionResp0{status, device_pubkey, device_random(16 bytes)}`
  3. shared = X25519(client_priv, device_pubkey); key = shared XOR SHA256(pop); iv = device_random
  4. client → `Session_Command1 { client_verify_data = AES-CTR(device_pubkey) }`
  5. device → `SessionResp1 { status, device_verify_data }`; client checks
     AES-CTR-decrypt(device_verify_data) == client_pubkey. Mismatch → wrong
     PoP; abort with a clear "pairing code rejected" error.
- Wi-Fi config (endpoint `prov-config`, payloads encrypted with the session keystream):
  `WiFiConfigPayload{msg=TypeCmdSetConfig, cmd_set_config=CmdSetConfig{ssid, passphrase}}` →
  `TypeCmdApplyConfig` → poll `TypeCmdGetStatus` (≤ 1 poll/s, ≤ 30 s total) until
  `sta_state == Connected` (read `connected.ip4_addr`) or `ConnectionFailed`
  (read `fail_reason`: AuthError / NetworkNotFound).
  - Firmware behaviour on `ConnectionFailed`: the firmware calls
    `wifi_prov_mgr_reset_sm_state_on_failure()`, which clears the rejected
    credentials and lets the client send a new `SetConfig` + `ApplyConfig` in
    the **same** session. The status poll keeps reporting the failure until
    new credentials are applied. A client that instead closes the session and
    reconnects is also fine.

### Clarifications (ESP-IDF v4.4.7 behaviour, agreed with the client)

- **Wrong PoP:** the device closes the GATT connection on `Session_Command1`
  instead of replying. The client maps a link failure during Command1 to
  "pairing code rejected" (device closed connection). BLE advertising resumes
  after the disconnect, so the user can retry with the correct code.
- **BLE lifetime after success:** the firmware never stops or deinitialises
  provisioning on `WIFI_PROV_CRED_SUCCESS`. BLE stays up until the ESP-IDF
  manager stops itself (auto-stop; the IDF default allows the client to read
  `sta_state=Connected` and `ip4_addr`, roughly up to 30 s after the IP is
  obtained) and the core handles `WIFI_PROV_END` (`wifi_prov_mgr_deinit()`,
  then the `FREE_BTDM` handler frees BT memory). The client can rely on BLE
  staying up for at least ~5 s after the device has its IP. mDNS and the MQTT
  broker are started as soon as the IP is obtained, while BLE is still up.
- **Strict formats:** the name suffix is exactly 6 uppercase hex digits
  (`PROV_RESMON_A1B2C3`, `%02X` formatting). The PoP is exactly 8 uppercase
  characters from `23456789ABCDEFGHJKLMNPQRSTUVWXYZ`; Security1 hashes it
  case-sensitively.
- **Credential limits:** SSID ≤ 32 bytes, passphrase ≤ 63 bytes (WPA
  passphrase; a 64-hex-digit raw PSK is not supported by this contract).
- **Failure reporting:** only `AuthError` and `NetworkNotFound` surface as
  `ConnectionFailed`. Any other Wi-Fi failure makes ESP-IDF 4.4 keep retrying,
  so the client sees its 30 s timeout. The firmware never reboots or leaves
  provisioning mode because of a failed attempt; the user can retry from the
  wizard (same session after `ConnectionFailed`, or a new session).
- **IP address:** `connected.ip4_addr` may be missing or invalid; the client
  must not save `FallbackAddress` in that case.
- **Serial reply:** exactly `RESMON-POP name=PROV_RESMON_XXXXXX pop=ABCDEFGH`
  with single spaces, terminated by `\r\n` (`Serial.println`).

### Firmware implementation (Arduino-ESP32 core 2.0.17)

The firmware is pinned to PlatformIO `espressif32 @ 7.0.1`
(`framework-arduinoespressif32` 3.20017 = Arduino-ESP32 2.0.17, ESP-IDF 4.4.7).
On this core the WiFiProv API uses the 2.x names:

```cpp
WiFiProv.beginProvision(WIFI_PROV_SCHEME_BLE,
                        WIFI_PROV_SCHEME_HANDLER_FREE_BTDM,
                        WIFI_PROV_SECURITY_1,
                        provPop,               // 8-char PoP from NVS
                        provServiceName,       // "PROV_RESMON_XXXXXX"
                        NULL,                  // service key (SoftAP only)
                        PROV_BLE_SERVICE_UUID, // 021a9004-...
                        resetStoredCredentials);
```

Other provisioning calls used:

- `ARDUINO_EVENT_PROV_START`, `_CRED_RECV`, `_CRED_FAIL`, `_CRED_SUCCESS`,
  `_END` events via `WiFi.onEvent()` (the core calls `wifi_prov_mgr_deinit()`
  on `PROV_END`; the `FREE_BTDM` handler then releases BT memory).
- `wifi_prov_mgr_reset_sm_state_on_failure()` after `PROV_CRED_FAIL`.
- `wifi_prov_mgr_reset_provisioning()` (= `esp_wifi_restore()`) on a BOOT
  long-press.
- The "already provisioned" check reads the STA config with
  `esp_wifi_get_config(WIFI_IF_STA)` and tests for a non-empty SSID, which is
  the same check `wifi_prov_mgr_is_provisioned()` performs. It is done
  directly because the boot order needs the dev fallback between
  "provisioned" and "provisioning", which `beginProvision()` cannot express.

On boots that do not provision, the firmware releases BT controller/host
memory with `esp_bt_mem_release(ESP_BT_MODE_BTDM)`; BLE cannot be restarted
without a reboot (the BOOT long-press reboots).

## Proof of Possession (pairing code)

- Generated once by the firmware on first boot with the hardware RNG
  (`esp_random()`, with `bootloader_random_enable()` providing entropy because
  the radio is not running yet), stored in NVS namespace `resmon`, key `pop`.
  8 characters from alphabet `23456789ABCDEFGHJKLMNPQRSTUVWXYZ` (no 0/O/1/I).
- Survives provisioning reset (`esp_wifi_restore()` only touches the Wi-Fi NVS
  data). Only a full NVS erase regenerates it. The firmware also regenerates it
  if the stored value is missing or not a valid 8-character code.
- Never sent over Wi-Fi/MQTT. Never logged by the .NET daemon.
- The firmware prints it on serial only as described below. Do **not** build
  the firmware with `CORE_DEBUG_LEVEL` ≥ 3 (info): WiFiProv's own `log_i`
  prints the PoP, and at verbose level (5) the core logs received Wi-Fi
  passwords. The default PlatformIO build uses level 0.

### USB serial PoP query (auto-read)

- Serial: UART0, 115200 8N1, line terminated by `\n` (`\r\n` accepted).
- Host sends exactly: `RESMON?POP\n`
- Device answers **only while in provisioning mode**, with one line:
  `RESMON-POP name=PROV_RESMON_XXXXXX pop=ABCDEFGH`
- Outside provisioning mode the device answers:
  `RESMON-POP unavailable state=<state>` where `<state>` is one of:

  | `state` | Meaning |
  | --- | --- |
  | `connecting` | STA credentials present, Wi-Fi not (yet / any more) connected |
  | `provisioned` | Connected with stored (BLE-provisioned) credentials |
  | `dev-fallback` | Connected with the compiled `secrets.h` development credentials |
  | `error` | Provisioning could not start and no other credentials exist |
  | `provisioning` | Provisioning mode, but the PoP could not be read from NVS |
  | `starting` | Setup not finished (not normally observable) |

  Clients must treat any `unavailable` answer as "not pairable now".
- The firmware reads serial non-blockingly (at most 64 bytes per `loop()`)
  with a bounded line buffer: lines longer than 32 characters are discarded
  up to the next `\n`. Unknown lines are ignored silently.
- While in provisioning mode, the firmware also prints a rate-limited
  (once at entry, then every 30 s) human-readable hint line:
  `Provisioning: name=PROV_RESMON_XXXXXX pop=ABCDEFGH` (physical USB access is
  treated as trusted).
- Other diagnostic lines (for example `DAC values: ...` once per second) are
  interleaved on the same port; the client must ignore lines that do not
  start with `RESMON-POP `.
- The .NET client must open the COM port with `DtrEnable=false,
  RtsEnable=false` to avoid resetting the board (and to avoid pulling GPIO0
  low through the auto-program circuit, see [HARDWARE.md](HARDWARE.md)), try
  each available COM port with a short timeout (≤ 1.5 s per port), and match
  the returned `name=` against the BLE device it is pairing with. If no port
  answers, the wizard asks the user to type the code.

## Device lifecycle

- Boot (pure decision function `decideBootAction()` in
  [firmware/include/provisioning_logic.h](firmware/include/provisioning_logic.h)):
  1. if the `prov_req` flag is set in NVS (`resmon/prov_req`, from a BOOT
     long-press) → clear it, reset stored Wi-Fi credentials, enter
     provisioning mode;
  2. else if stored Wi-Fi credentials exist (Wi-Fi NVS store has an SSID) →
     connect as STA;
  3. else if the compiled dev fallback `WIFI_SSID` (from ignored `secrets.h`)
     is non-empty → connect with it (no BLE);
  4. else → provisioning mode.
- Dev fallback credentials are applied with `WIFI_STORAGE_RAM`, so they are
  never written to the Wi-Fi NVS store and never mistaken for provisioned
  credentials on a later boot.
- Provisioning mode: BLE advertising as above; after successful provisioning,
  the BLE stack is stopped and BT memory freed, the device joins Wi-Fi, starts
  mDNS `resmon.local`, then the MQTT broker.
- Wi-Fi connection is non-blocking. `loop()` keeps the DAC stale-decay and
  BOOT button running while Wi-Fi is down, retries with bounded exponential
  backoff (5 s → 60 s), and logs "connecting" at most every 10 s. mDNS and the
  MQTT broker are started once, on the first successful connection, and keep
  running across reconnects.
- Re-open provisioning on a configured device: hold the on-board BOOT button
  (GPIO0) for ≥ 5 s while the firmware is running → the firmware sets
  `prov_req`, resets the stored Wi-Fi credentials, and restarts.
- MQTT contract (`resmon/cpu`, `resmon/mem`, port 1883) is unchanged; see
  [COMMUNICATION.md](COMMUNICATION.md).

## Daemon-side result

On success the client receives `ip4_addr`. The daemon saves:

- `Host` = `resmon.local` (unchanged default; mDNS name)
- `FallbackAddress` = the received IPv4 address (used when `Host` fails to resolve/connect)

The Wi-Fi password is held in memory only for the provisioning attempt and
never persisted or logged.

## User flows

### First boot (no credentials)

1. Flash the firmware without `firmware/src/secrets.h` (or with an empty
   `WIFI_SSID`).
2. The serial log shows
   `Wi-Fi: no stored credentials and no secrets.h fallback; entering BLE provisioning`
   and `Provisioning: name=PROV_RESMON_XXXXXX pop=ABCDEFGH`.
3. With the USB cable connected, start pairing in the Windows app. It finds
   `PROV_RESMON_XXXXXX` over BLE, reads the pairing code over USB serial
   (`RESMON?POP`), or asks you to type the 8-character code from the serial log.
4. Enter the Wi-Fi SSID and password in the app. The device connects, BLE
   shuts down, and the broker becomes reachable at `resmon.local:1883`
   (fallback: the reported IP address).

### Re-pair / change Wi-Fi network

1. While the firmware is running (not during reset), press and hold the
   **BOOT** button for at least 5 seconds. The serial log shows
   `BOOT button pressed: hold 5 s ...` and then the reset message.
2. The board restarts directly into provisioning mode; pair again as above.
3. If the board is power-cycled before pairing completes, the request flag
   has already been consumed: the device boots with the dev fallback if one
   is compiled in, otherwise straight back into provisioning.

Do **not** hold BOOT while pressing EN/RESET or plugging in power: that
enters the ROM serial download mode instead of running the firmware.

### Development fallback (`secrets.h`)

- Copy `firmware/src/secrets.example.h` to `firmware/src/secrets.h` (ignored by
  git) and set `WIFI_SSID` / `WIFI_PASSWORD`.
- The fallback is used only when no BLE-provisioned credentials are stored
  and no provisioning was requested. Stored credentials always win; to go
  back to the fallback, BOOT long-press and then power-cycle without pairing.

## Limitations

- Not yet tested on physical hardware or against the Windows client.
- Credentials are stored in the standard ESP-IDF Wi-Fi NVS store without NVS
  or flash encryption (development storage policy; see
  [DEVELOPMENT_PLAN.md](DEVELOPMENT_PLAN.md) Phase 4).
- Security1 (not Security2/SRP6a) is used, as the core 2.0.17 WiFiProv API
  supports Security0/1 only.
- Only SSID and passphrase are provisioned. Hostname, MQTT port and topics
  remain compile-time defaults in `config.h`.
- The BLE + Wi-Fi image needs the `min_spiffs.csv` partition table (two
  1.875 MB OTA app slots, 128 KB SPIFFS). NVS offset and size are unchanged
  from the default table.
