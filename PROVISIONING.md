# Wi-Fi Provisioning and Pairing Contract

This document is the contract between the ESP32 firmware and the Windows
(.NET) pairing client, plus a description of what the firmware actually
implements. Both sides must follow it exactly.

> **Validation status:** the firmware side builds, its pure logic is
> unit-tested, and the link-time BT fix was checked in the built ELF, but BLE
> provisioning has **not yet been tested on physical hardware** or against the
> Windows client. Treat every behaviour below as implemented-but-unverified
> until a bench test is recorded here.

## Transport and security

- Transport: BLE, using the ESP-IDF `wifi_provisioning` manager BLE scheme
  (the same manager Arduino-ESP32's `WiFiProv` wraps; the firmware calls the
  manager directly, see below).
- Security: protocomm **Security1** (X25519 key exchange + AES-256-CTR)
  **with Proof of Possession (PoP)**. No custom crypto protocol. The .NET
  client reimplements the client side of the official protocol (reference:
  ESP-IDF `tools/esp_prov/security/security1.py`, `esp_prov/prov/wifi_prov.py`,
  `components/protocomm/proto/*.proto`, `components/wifi_provisioning/proto/*.proto`).
- BLE service UUID: `021a9004-0382-4aea-bff4-6b3f1c5adfb4` (Arduino WiFiProv
  default; the firmware sets it explicitly from `PROV_BLE_SERVICE_UUID` in
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
    the same or a new session (without it, ApplyConfig would return
    InternalError). The status poll keeps reporting the failure until new
    credentials are applied.

### Clarifications (ESP-IDF v4.4.7 behaviour, agreed with the client)

- **Wrong PoP:** the device closes the GATT connection on `Session_Command1`
  instead of replying. The client maps a link failure during Command1 to
  "pairing code rejected" (device closed connection). A device that fails
  Command1 may also leave the stale `SessionResp0` in the attribute; the client
  treats a Response0-typed reply to Command1 as a rejection too. BLE
  advertising resumes after the disconnect, so the user can retry.
- **BLE lifetime after success:** in IDF 4.4.7 the GetStatus handler calls
  `wifi_prov_mgr_done()` as soon as it reports Connected; with the default
  auto-stop that tears BLE down after ~1 s. The firmware therefore calls
  `wifi_prov_mgr_disable_auto_stop(PROV_BLE_CLEANUP_DELAY_MS = 5000)` between
  `wifi_prov_mgr_init()` and `wifi_prov_mgr_start_provisioning()`, and itself
  calls `wifi_prov_mgr_stop_provisioning()` `PROV_STOP_AFTER_SUCCESS_MS`
  (30 s) after `WIFI_PROV_CRED_SUCCESS`. The manager keeps BLE up for the
  5 s cleanup delay after that, then emits `WIFI_PROV_END`; the core calls
  `wifi_prov_mgr_deinit()` and the `FREE_BTDM` handler frees BT memory. So
  BLE stays up for about 35 s after credentials succeed. Nothing is stopped or
  deinitialised on `CRED_SUCCESS` itself. mDNS and the MQTT broker start as
  soon as the IP is obtained, while BLE is still up.
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
  wizard.
- **IP address:** `connected.ip4_addr` may be missing or invalid; the client
  must not save `FallbackAddress` in that case.
- **Serial reply:** exactly `RESMON-POP name=PROV_RESMON_XXXXXX pop=ABCDEFGH`
  with single spaces, terminated by `\r\n` (`Serial.println`).
- **BOOT re-provisioning trigger:** hold BOOT for 5–15 seconds, then
  release. Holds longer than 15 s are ignored (protects against a serial tool
  holding DTR asserted, which pulls GPIO0 low via the auto-program circuit).

### Firmware implementation (Arduino-ESP32 core 2.0.17)

The firmware is pinned to PlatformIO `espressif32 @ 7.0.1`
(`framework-arduinoespressif32` 3.20017 = Arduino-ESP32 2.0.17, ESP-IDF 4.4.7)
and uses the IDF 4.4 `wifi_provisioning` API directly instead of
`WiFiProv.beginProvision()`, because `beginProvision()` does init and start in
one call and auto-stop can only be disabled in between. Sequence
(`startProvisioning()` in [firmware/src/main.cpp](firmware/src/main.cpp)):

```cpp
wifiLowLevelInit(true);                        // Wi-Fi driver + Arduino event task
wifi_prov_mgr_init({ .scheme = wifi_prov_scheme_ble,
                     .scheme_event_handler = WIFI_PROV_SCHEME_BLE_EVENT_HANDLER_FREE_BTDM });
wifi_prov_mgr_reset_provisioning();            // only after a BOOT request
wifi_prov_mgr_disable_auto_stop(5000);         // PROV_BLE_CLEANUP_DELAY_MS
wifi_prov_scheme_ble_set_service_uuid(PROV_BLE_SERVICE_UUID);
wifi_prov_mgr_start_provisioning(WIFI_PROV_SECURITY_1, pop, "PROV_RESMON_XXXXXX", NULL);
```

Other calls and events:

- The Arduino core registers its handler for `WIFI_PROV_EVENT` when the Wi-Fi
  driver is initialised, so `ARDUINO_EVENT_PROV_START`, `_CRED_RECV`,
  `_CRED_FAIL`, `_CRED_SUCCESS` and `_END` arrive through `WiFi.onEvent()`
  exactly as with `WiFiProv`. On `PROV_END` the core calls
  `wifi_prov_mgr_deinit()`.
- `wifi_prov_mgr_reset_sm_state_on_failure()` after `PROV_CRED_FAIL`, but
  only **before** provisioning has succeeded (`decideCredentialFailureAction()`
  in `provisioning_logic.h`). The manager's Wi-Fi handler stays registered
  until provisioning stops and reports a post-success drop (for example the AP
  rebooting during the BLE grace period) as `CRED_FAIL` too. Resetting then
  would write an empty STA config to flash and unpair the device, so after
  success the firmware keeps the credentials, logs `Wi-Fi lost during BLE
  grace period ...; credentials kept`, and calls
  `wifi_prov_mgr_stop_provisioning()` immediately so only the firmware's
  reconnect policy runs.
- `wifi_prov_mgr_stop_provisioning()` 30 s after `PROV_CRED_SUCCESS`.
- `wifi_prov_mgr_reset_provisioning()` (= `esp_wifi_restore()`) when the BOOT
  gesture requests re-provisioning.
- The "already provisioned" check reads the STA config with
  `esp_wifi_get_config(WIFI_IF_STA)` and tests for a non-empty SSID, the same
  check `wifi_prov_mgr_is_provisioned()` performs.
- **BT memory at startup:** Arduino's `initArduino()` releases the BT
  controller memory before `setup()` unless `btInUse()` returns true. The
  strong `btInUse()` (returning true) lives in `esp32-hal-bt.c` and is only
  linked if something from that file is referenced; the firmware references
  `btStarted()` in its startup diagnostics for that reason. Without it BLE
  could never start (verified in the linker map and disassembly of the build).
- On boots that do not provision, the firmware releases BT controller/host
  memory with `esp_bt_mem_release(ESP_BT_MODE_BTDM)`. BLE cannot be restarted
  without a reboot (the BOOT gesture reboots).
- **Provisioning start failure:** if `wifi_prov_mgr_init()` or
  `wifi_prov_mgr_start_provisioning()` fails, the PoP is unavailable, or no
  `PROV_START` event arrives within 5 s, the firmware logs an error and
  restarts into provisioning after 3 s (the `prov_req` flag is set again). It
  does this at most `PROV_START_MAX_ATTEMPTS` (3) consecutive times; the count
  is kept in RTC memory across software resets and cleared on power-on, on a
  successful start, and on the BOOT gesture. After that it connects with the
  `secrets.h` dev fallback if one exists, otherwise it stays up and logs a
  rate-limited error ("power-cycle to retry").

## Proof of Possession (pairing code)

- Generated once by the firmware on first boot with the hardware RNG
  (`esp_random()`, with `bootloader_random_enable()` providing entropy because
  the radio is not running yet), stored in NVS namespace `resmon`, key `pop`.
  8 characters from alphabet `23456789ABCDEFGHJKLMNPQRSTUVWXYZ` (no 0/O/1/I).
- Survives provisioning reset (`esp_wifi_restore()` only touches the Wi-Fi NVS
  data). Only a full NVS erase regenerates it. The firmware also regenerates it
  if the stored value is missing or not a valid 8-character code.
- Never sent over Wi-Fi/MQTT. Never logged by the .NET daemon.
- The firmware prints it on serial only as described below.
  `platformio.ini` pins `-DCORE_DEBUG_LEVEL=1` (errors only), and
  `firmware/src/config.h` fails the build at level ≥ 3: info logging prints
  the PoP, and verbose logging (5) prints received Wi-Fi passwords.

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
  | `error` | Provisioning could not start (retry restart pending, or given up) |
  | `starting` | Setup not finished (not normally observable) |

  Clients must treat any `unavailable` answer as "not pairable now".
- The firmware reads serial non-blockingly (at most 64 bytes per `loop()`)
  with a bounded line buffer: lines longer than 32 characters are discarded
  up to the next `\n`. Unknown lines are ignored silently.
- While in provisioning mode, the firmware also prints a rate-limited
  (once at entry, then every 30 s) human-readable hint line:
  `Provisioning: name=PROV_RESMON_XXXXXX pop=ABCDEFGH` (physical USB access is
  treated as trusted).
- Other diagnostic lines are interleaved on the same port (for example
  `DAC values: ...` when the outputs change, at most every 10 s, and a status
  line every 60 s); the client must ignore lines that do not start with
  `RESMON-POP `.
- The .NET client must open the COM port with `DtrEnable=false,
  RtsEnable=false` to avoid resetting the board (and to avoid pulling GPIO0
  low through the auto-program circuit, see [HARDWARE.md](HARDWARE.md)), try
  each available COM port with a short timeout (≤ 1.5 s per port), and match
  the returned `name=` against the BLE device it is pairing with. If no port
  answers, the wizard asks the user to type the code.

## Device lifecycle

- Boot (pure decision function `decideBootAction()` in
  [firmware/include/provisioning_logic.h](firmware/include/provisioning_logic.h)):
  1. if the `prov_req` flag is set in NVS (`resmon/prov_req`, from the BOOT
     gesture or a provisioning-start retry) → clear it, reset stored Wi-Fi
     credentials, enter provisioning mode;
  2. else if stored Wi-Fi credentials exist (Wi-Fi NVS store has an SSID) →
     connect as STA;
  3. else if the compiled dev fallback `WIFI_SSID` (from ignored `secrets.h`)
     is non-empty → connect with it (no BLE);
  4. else → provisioning mode.
- Dev fallback credentials are applied with `WIFI_STORAGE_RAM`, so they are
  never written to the Wi-Fi NVS store and never mistaken for provisioned
  credentials on a later boot.
- Provisioning mode: BLE advertising as above. When the client's credentials
  work, the device joins Wi-Fi and immediately starts mDNS `resmon.local` and
  the MQTT broker; BLE stays up for about 35 s more (see Clarifications) and
  is then stopped and its memory freed.
- Wi-Fi connection is non-blocking and the firmware owns the reconnect
  policy: the core's auto-reconnect is disabled, and `loop()` calls
  `esp_wifi_connect()` only when no attempt is in flight (after a disconnect
  event, or 30 s without a result), with bounded exponential backoff
  (5 s → 60 s, reset on connect). DAC stale-decay and the BOOT button keep
  running while Wi-Fi is down; "connecting" is logged at most every 10 s with
  the last real disconnect reason. mDNS and the MQTT broker are started once,
  on the first successful connection, and keep running across reconnects.
- IP, SSID and RSSI for diagnostics come from the `GOT_IP` event and
  `esp_wifi_sta_get_ap_info()`, because the Arduino `WiFi` accessors report
  nothing after the provisioning manager has started Wi-Fi itself. The DHCP
  hostname is set to `resmon` on the STA interface on every path.
- Re-open provisioning on a configured device: while the firmware is running,
  hold the on-board BOOT button (GPIO0) for 5–15 s, then release → the
  firmware sets `prov_req`, resets the stored Wi-Fi credentials, and restarts.
  Releasing earlier does nothing; holding longer than 15 s is logged
  (`BOOT held too long ..., ignored`) and does nothing.
- MQTT contract (`resmon/cpu`, `resmon/mem`, port 1883) is unchanged; see
  [COMMUNICATION.md](COMMUNICATION.md).

## Daemon-side result

On success the client receives `ip4_addr`. The daemon saves:

- `Host` = `resmon.local` (unchanged default; mDNS name)
- `FallbackAddress` = the received IPv4 address (used when `Host` fails to
  resolve/connect; not saved if `ip4_addr` is missing or invalid)

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
4. Enter the Wi-Fi SSID and password in the app. The device joins Wi-Fi and
   the broker becomes reachable at `resmon.local:1883` (fallback: the reported
   IP address) right away; the app reads the result over BLE, and BLE shuts
   down about 35 s later.

### Re-pair / change Wi-Fi network

1. While the firmware is running (not during reset), press and hold the
   **BOOT** button. The serial log shows `BOOT pressed: hold 5-15 s, then
   release ...`, and after 5 s `BOOT held 5 s: release now ...`.
2. Release it before 15 s have passed. The board clears its Wi-Fi
   credentials and restarts directly into provisioning mode; pair again as
   above.
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
  back to the fallback, use the BOOT gesture and then power-cycle without
  pairing.

## Bench-test checklist (not yet run)

Record the date, board, and result of each item here once tested:

- [ ] First boot without credentials: advertising name, hint line, `RESMON?POP` reply.
- [ ] Pairing from the Windows client; `ip4_addr` received; MQTT reachable via
      `resmon.local` and the IP.
- [ ] BLE stays up ~35 s after success, then `Provisioning: ended`; free heap
      rises after the BT memory release.
- [ ] Wrong PoP: client reports "pairing code rejected"; a retry with the right
      code works.
- [ ] Wrong password / unknown SSID: `ConnectionFailed` with AuthError /
      NetworkNotFound; retry with correct credentials in the same session and in
      a new session.
- [ ] **AP lost during the BLE grace period:** pair successfully, then power-cycle
      the AP within 30 s. Expect `Wi-Fi lost during BLE grace period ...;
      credentials kept`, BLE stopping, reconnection once the AP is back, and the
      credentials still present after a device power cycle.
- [ ] Reconnect after AP loss in normal operation (backoff 5 s → 60 s, last
      reason logged).
- [ ] BOOT gesture: release after < 5 s (nothing), 5–15 s (re-provisioning),
      > 15 s (ignored); with a serial monitor attached.
- [ ] Dev fallback via `secrets.h`; stored credentials take priority.
- [ ] Provisioning start failure path (if it can be provoked): 3 restarts, then
      fallback or a rate-limited error.

## Limitations

- Not yet tested on physical hardware or against the Windows client.
- Credentials are stored in the standard ESP-IDF Wi-Fi NVS store without NVS
  or flash encryption (development storage policy; see
  [DEVELOPMENT_PLAN.md](DEVELOPMENT_PLAN.md) Phase 4).
- Security1 (not Security2/SRP6a) is used; the IDF 4.4 manager in core 2.0.17
  supports Security0/1 only.
- Only SSID and passphrase are provisioned. Hostname, MQTT port and topics
  remain compile-time defaults in `config.h`.
- The Arduino core still retries once by itself after the very first
  disconnect of each boot (independent of the auto-reconnect setting). That
  single extra attempt can overlap with the firmware's own policy; it is
  bounded and not repeated.
- The BLE + Wi-Fi image needs the `min_spiffs.csv` partition table (two
  1.875 MB OTA app slots, 128 KB SPIFFS). NVS offset and size are unchanged
  from the default table.
