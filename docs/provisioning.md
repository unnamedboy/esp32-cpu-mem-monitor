# Wi-Fi provisioning and pairing

The firmware contains no Wi-Fi credentials. A new device starts in pairing mode and
receives your network name and password from the Windows app over an encrypted
Bluetooth LE session. This page covers pairing from the user's side, the security
model, and the protocol details that the firmware and the Windows client share.

> **Not yet tested on hardware.** The firmware side and the Windows client are
> implemented and unit-tested, but pairing has not yet been run against a physical
> ESP32. Please report what you find.

## How the device chooses its Wi-Fi

At every boot the firmware takes the first matching option
(`decideBootAction()` in [`provisioning_logic.h`](../firmware/include/provisioning_logic.h)):

1. **Re-pairing was requested** with the BOOT button: clear the flag, erase the stored
   credentials, start pairing mode.
2. **Stored credentials exist** (from an earlier pairing): connect with them.
3. **A development fallback is compiled in** (`firmware/src/secrets.h` with a non-empty
   `WIFI_SSID`): connect with it, without Bluetooth.
4. Otherwise: start pairing mode.

In pairing mode the device advertises over BLE as `PROV_RESMON_XXXXXX` (the last three
bytes of its Wi-Fi MAC address) and prints its pairing code to the USB serial log
(115200 baud) once on entry and then every 30 s:

```text
Provisioning: name=PROV_RESMON_A1B2C3 pop=ABCDEFGH
```

## Pairing a device

You need a Windows 10 (2004 or later) or Windows 11 PC with Bluetooth LE, and the
daemon running.

1. **Put the device in pairing mode.** A device without Wi-Fi settings does this by
   itself at startup. For a device that is already configured, or one built with
   `secrets.h`, use the [BOOT gesture](#re-pairing-and-changing-networks).
2. **Open the wizard.** Right-click the tray icon and choose **Pair device…**. On the
   very first run of the daemon, the first "MQTT connection failed"
   notification instead says *No device found — click to pair*; clicking it opens the
   wizard (see [when the prompt appears](daemon.md#first-run-pairing-prompt)).
3. **Select the device.** The wizard scans for 5 s and lists `PROV_RESMON_…` devices
   with their signal strength. A single device is selected automatically. Click
   **Rescan** if the device was not in pairing mode yet. If Bluetooth is off, turn it
   on in Windows Settings and rescan.
4. **Pairing code.** If the device is connected to this PC by USB through a common
   ESP32 USB-serial bridge (CP210x, CH340/CH343/CH9102, FTDI or Espressif native USB),
   the wizard reads the code over the serial port and shows *Read automatically over
   USB*. Close any serial monitor first: a COM port can only be open in one program.
   Otherwise type the 8-character code from the serial log. Codes use the letters A–Z without I and
   O and the digits 2–9; case, spaces and dashes are ignored.
5. **Wi-Fi.** Enter the network name (SSID) and password, or click **Use this PC's
   Wi-Fi** to copy the network this PC is on. Windows 11 may ask once whether apps may
   use your location, because it treats Wi-Fi details as location data; if you
   decline, type the name. **The ESP32 supports 2.4 GHz only.** The wizard warns you if
   this PC is connected on 5 or 6 GHz; make sure the router also offers the network on
   2.4 GHz.
6. **Click Pair.** Progress shows each step: connect, secure session, send
   Wi-Fi settings, wait for Wi-Fi. **Cancel** stops the attempt; closing the wizard
   cancels everything.
7. **Done.** When the device reports its IP address, the wizard saves it as
   `FallbackAddress` in the user settings file and changes nothing else. The daemon
   reconnects without a restart, and the wizard waits up to 45 s for that connection.
   It ends with *Connected, publishing.*, *Sending is paused* (resume it from the tray
   menu), or *The daemon keeps retrying … in the background*.

The Wi-Fi password is used only for that attempt. The password box is cleared
afterwards, and the password is never written to disk or to the log.

### If pairing fails

| Message | What to do |
| --- | --- |
| Wrong Wi-Fi password | Re-enter the password and try again. |
| Wi-Fi network not found | Check the SSID and that the router offers 2.4 GHz. |
| Wrong pairing code | Check the code in the serial log and try again. |
| No Wi-Fi result within 30 s | Check the SSID, password and 2.4 GHz, then try again. A retry also detects a device that joined Wi-Fi late. |
| Lost the Bluetooth link | The wizard first tries the configured `Host:Port` (default `resmon.local:1883`) for up to 15 s. If the device is still unreachable, rescan; if it no longer appears, use the BOOT gesture and pair again. |
| The device did not accept another attempt | Use the BOOT gesture, then rescan and pair. Don't power-cycle instead: the device has already stored the earlier settings and would restart with them and without Bluetooth. |
| The device is already connected to network "…" | It joined a different network in an earlier attempt. To switch, use the BOOT gesture and pair again. |
| No device in pairing mode found | Check power and pairing mode, then **Rescan**. |
| Bluetooth is off / no usable BLE adapter | Turn Bluetooth on, or use a PC with a BLE adapter. |

Reopening the wizard right after closing it can miss the USB pairing code, because an
abandoned serial probe may still hold the COM port for a moment. Wait briefly and
select the device again, or type the code.

## Re-pairing and changing networks

While the firmware is running, **hold the BOOT button for 5–15 s, then release it**.
The serial log shows `BOOT pressed: hold 5-15 s, then release …` and, after 5 s,
`BOOT held 5 s: release now …`. On release the device erases its stored Wi-Fi
credentials and restarts straight into pairing mode.

- Releasing before 5 s does nothing.
- Holding longer than 15 s is ignored, as protection against serial tools that hold
  GPIO0 low ([why](hardware.md#boot-button-gpio0)).
- Do not hold BOOT while pressing EN/RESET or plugging in power: that starts the ESP32's
  ROM download mode instead of the firmware.
- If the device is power-cycled before pairing completes, the request has already been
  used up: it boots with the `secrets.h` fallback if there is one, otherwise into
  pairing mode again (the credentials are already erased).

### Upgrading a board that ran older firmware

Firmware from before BLE provisioning connected with `WiFi.begin(SSID, PASSWORD)`,
which also saves the network in the ESP32's Wi-Fi storage. The new firmware treats that
as a paired network: it connects with it, skips pairing mode, ignores later `secrets.h`
changes, and answers the serial query with `state=provisioned`. To start fresh, use the
BOOT gesture once, or erase the flash and upload again (this also generates a new
pairing code):

```sh
pio run -e esp32dev -t erase
pio run -e esp32dev -t upload
```

### Development fallback (`secrets.h`)

For development without the pairing app, copy
[`firmware/src/secrets.example.h`](../firmware/src/secrets.example.h) to
`firmware/src/secrets.h` (ignored by git) and set `WIFI_SSID` and `WIFI_PASSWORD`. The
fallback is used only when no paired credentials are stored, and it is kept in RAM
only, so it is never mistaken for paired credentials later. Stored credentials always
win; to go back to the fallback, use the BOOT gesture and then power-cycle without
pairing.

## Security model

- **Transport.** BLE using the ESP-IDF `wifi_provisioning` manager and protocomm
  **Security1**: an X25519 key exchange, AES-256-CTR encryption, and a proof of
  possession (the pairing code). The protocol is ESP-IDF's, unchanged. The Windows
  client implements its client side with standard primitives (BouncyCastle X25519,
  .NET AES) and is tested against the RFC 7748 and NIST SP 800-38A vectors and an
  in-memory device; ESP-IDF's `esp_prov` tool is the reference client.
- **What Security1 does and does not protect.** The Wi-Fi password is sent only after
  the device has proved it knows the pairing code, so a fake device cannot simply
  collect it. But a device that impersonates `PROV_RESMON_…` gets one handshake from
  the client, and from that it can try to guess the 40-bit code offline. Security2
  (SRP6a) would rule out that offline guess; it is not available in this ESP-IDF
  version (see [Limitations](#limitations)).
- **Pairing code.** Generated once with the hardware random number generator, stored
  in NVS (namespace `resmon`, key `pop`), and kept across re-pairing; only a full flash
  erase, or a missing or invalid stored value, produces a new one. 8 characters from a
  32-symbol alphabet give 40 bits. It is never sent over Wi-Fi or MQTT.
- **USB is trusted.** Anyone with physical USB access can read the code from the serial
  log or with the [serial query](#serial-pairing-code-query) while the device is in
  pairing mode. The code is not printed outside pairing mode.
- **Pairing window.** Pairing mode only runs on a device without credentials or after
  the BOOT gesture. Bluetooth is turned off about 35 s after a successful pairing, and
  on boots that don't pair the Bluetooth memory is released entirely.
- **Credentials on the device** are stored in the ESP32's standard Wi-Fi NVS storage,
  **without** flash or NVS encryption. Someone with the board can read them out. See the
  [roadmap](roadmap.md) for the open decision on encryption.
- **Credentials on the PC.** The daemon never stores the Wi-Fi password. Only the
  device's IP address is saved.
- **Logs.** The firmware build is limited to a log level that cannot print the Wi-Fi
  password ([details](firmware.md#toolchain)). The daemon never logs the password, the
  pairing code or the SSID. The paired device's Bluetooth address and Wi-Fi IP address
  are logged only at Debug level; a saved IP address later appears in the daemon's
  settings and connection diagnostics as the configured `FallbackAddress`.

## Protocol reference

### BLE

| Item | Value |
| --- | --- |
| Device name | `PROV_RESMON_` + 6 uppercase hex digits, e.g. `PROV_RESMON_A1B2C3`. The name may be in the scan response, so scan actively. |
| GATT service UUID | `021a9004-0382-4aea-bff4-6b3f1c5adfb4` |
| Endpoints | Identified by the text of their Characteristic User Description (0x2901): `prov-session`, `prov-config`, `proto-ver`, `prov-scan`. Look them up by that text, not by characteristic UUID. |
| Security | Security1 with the pairing code as proof of possession |
| Credential limits | SSID up to 32 bytes, WPA passphrase up to 63 bytes (a raw 64-hex-digit PSK is not supported) |

### Session (Security1)

1. Client → `SessionCmd0 { client_pubkey }`.
2. Device → `SessionResp0 { device_pubkey, device_random (16 bytes) }`.
3. Both sides compute `key = X25519(shared) XOR SHA-256(pairing code)`; the IV is
   `device_random`. One AES-256-CTR keystream covers every message in both directions
   for the rest of the session.
4. Client → `SessionCmd1 { client_verify_data = encrypt(device_pubkey) }`.
5. Device → `SessionResp1 { device_verify_data }`; the client checks that it decrypts to
   its own public key.

With a wrong pairing code, ESP-IDF 4.4 does not reply to step 4: it closes the BLE
connection. The client treats a dropped link during step 4 (or a stale step-2 reply
read back from the characteristic) as "wrong pairing code". The device keeps
advertising, so the user can retry.

If an exchange fails half-way, the two keystreams can no longer be aligned; the client
discards the session and reconnects.

### Sending Wi-Fi settings

On `prov-config`, encrypted with the session keystream:

1. `GetStatus` first. If the device already reports *Connected* (it joined Wi-Fi after
   an earlier attempt that timed out or lost the link), the client sends nothing and
   reports success, or "already on a different network" if the joined SSID differs.
2. `SetConfig { ssid, passphrase }`, then `ApplyConfig`.
3. Poll `GetStatus` at most once per second, for up to 30 s, until the state is
   *Connected* (read `connected.ip4_addr`) or *ConnectionFailed* (`AuthError` or
   `NetworkNotFound`).

The outcome rules follow ESP-IDF 4.4.7 behaviour:

- **ConnectionFailed** (`AuthError`, `NetworkNotFound`): the firmware resets the
  provisioning state, so the client can send new settings in the same or a new session.
- **Any other Wi-Fi failure** makes ESP-IDF keep retrying, so the client sees its 30 s
  timeout. The device has then already stored the credentials and rejects another
  `ApplyConfig` (`InternalError`); a power cycle would boot with the stored (bad)
  credentials and no Bluetooth. Recovery is the BOOT gesture.
- **IP address.** `ip4_addr` may be missing or unusable. The client saves
  `FallbackAddress` only for a usable unicast IPv4 address.
- **Bluetooth after success.** The firmware keeps BLE up for 30 s after success
  (`PROV_STOP_AFTER_SUCCESS_MS`) plus a 5 s cleanup delay (`PROV_BLE_CLEANUP_DELAY_MS`),
  so the client can read the result. The MQTT broker starts as soon as the device has
  an IP address, while BLE is still up. The client does not rely on this window: a link
  lost after a *Connected* reply still counts as success, and a link lost while waiting
  makes the wizard check whether the broker is reachable.

### Serial pairing-code query

The Windows client reads the pairing code over USB so the user doesn't have to type
it.

- UART0, 115200 8N1. Lines end with `\n` (`\r\n` is accepted). Lines longer than
  32 characters and unknown lines are ignored.
- Send `RESMON?POP`.
- In pairing mode the device answers `RESMON-POP name=PROV_RESMON_XXXXXX pop=ABCDEFGH`.
- Otherwise it answers `RESMON-POP unavailable state=<state>`:

  | `state` | Meaning |
  | --- | --- |
  | `connecting` | Has credentials, not connected (yet) |
  | `provisioned` | Connected with paired credentials |
  | `dev-fallback` | Connected with `secrets.h` credentials |
  | `provisioning` | In pairing mode, but the code could not be loaded from NVS |
  | `error` | Pairing mode could not start |
  | `starting` | Setup not finished (not normally seen) |

- Other log lines are interleaved on the same port; ignore lines that don't start with
  `RESMON-POP `.
- Open the port with DTR and RTS both off, so the board is not reset and GPIO0 is not
  pulled low ([why](hardware.md#boot-button-gpio0)).

The Windows client (`SerialPopReader`, `SerialPortFilter`) only sends the query to
ports that are likely to be an ESP32:

- Ports whose USB vendor is known are probed only for the common ESP32 USB-serial
  bridges: Silicon Labs CP210x (`10C4`), WCH CH340/CH343/CH9102 (`1A86`), FTDI
  (`0403`) and Espressif native USB (`303A`).
- Ports of unknown origin are probed, unless they are legacy `\Device\Serial<n>` UARTs
  or Bluetooth serial ports (`BthModem`, `BthEnum`).

Each port gets at most 1.5 s, and only a reply whose `name=` matches the selected BLE
device is accepted. With any other USB-serial bridge, type the code instead.

## Firmware behaviour worth knowing

The firmware drives the ESP-IDF provisioning manager directly rather than through
Arduino's `WiFiProv.beginProvision()`, because auto-stop has to be disabled between
initialising and starting the manager (otherwise BLE goes down about 1 s after success,
before the client can read the result). Implementation notes:

- **Provisioning fails to start** (manager error, no pairing code, or no start event
  within 5 s): the firmware restarts into pairing mode after 3 s and tries again. After
  3 failed starts in a row it connects with the `secrets.h` fallback if there is one,
  or stays up and logs `Power-cycle to retry` every 30 s. The count survives software
  restarts and is cleared at power-on, on a successful start and by the BOOT gesture.
- **A late start.** If BLE reports that it started after the 5 s timeout was already
  handled (a retry restart pending, or given up), Bluetooth is in fact advertising:
  the firmware cancels the restart, clears the re-pairing request, returns to pairing
  mode and prints the pairing hint again. After a switch to the `secrets.h` fallback a
  late start is ignored, because the provisioning manager was already shut down
  (`decideProvisioningStarted()`).
- **Wi-Fi drops during the 35 s after success** (for example the router reboots): the
  provisioning manager reports this as a credential failure, but the firmware keeps the
  credentials, stops BLE early, and lets its normal reconnect policy take over.
- **An IP address before the credentials are accepted.** On every pairing the device
  gets its IP address a moment before the manager confirms success, so the firmware
  waits for that confirmation. It logs a warning only if the wait lasts 5 s.
- **Build settings.** BLE pairing depends on a linker flag and a larger app partition;
  see [firmware.md](firmware.md#toolchain).

## Limitations

- Not yet tested on hardware or end to end.
- Security1 rather than Security2 (SRP6a): ESP-IDF 4.4 in Arduino-ESP32 2.0.17 only
  offers Security0 and Security1.
- Only the SSID and password are provisioned. The host name, MQTT port and topics are
  compile-time settings in `config.h`, and the wizard does not offer them.
- Stored credentials are not encrypted on the device.
- `resmon.local` is ambiguous when several monitors share a network; the saved IP
  address (`FallbackAddress`) is then the reliable way to reach the paired one.

## Testing on hardware

When you have a board, these are the cases worth checking (on a board that ran older
firmware, clear the old credentials first, see above):

- First boot: advertised name, serial hint line, `RESMON?POP` reply.
- Pairing from the wizard: IP address received; broker reachable via `resmon.local`
  and via the IP; BLE stops about 35 s later and free heap rises.
- Wrong pairing code, then the right one.
- Wrong password and unknown SSID, then a retry in the same and in a new session.
- A failure that is neither (for example an AP that refuses association): 30 s timeout,
  rejected retry, BOOT gesture recovers.
- Router power-cycled during the 35 s after success: credentials kept, reconnects.
- Reconnect after losing the router in normal operation (5 s → 60 s backoff).
- BOOT gesture released after under 5 s, 5–15 s and over 15 s, with a serial monitor
  attached.
- `secrets.h` fallback, and stored credentials taking priority over it.
