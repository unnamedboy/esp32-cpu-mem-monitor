# C# Resource Monitor Daemon Summary

## Current Role

The daemon is the Windows-side metrics client for the ESP32 CPU/MEM monitor.
It samples local CPU and memory usage once per second and publishes values to
the MQTT broker hosted by the ESP32.

The active communication contract is documented in `../COMMUNICATION.md`.

## Implemented Behavior

### Metrics

- CPU usage is sampled with `PerformanceCounter("Processor", "% Processor Time", "_Total")`.
- Memory usage is sampled through `NativeMemory.GetMemoryStatus()`.
- Each sample is logged as CPU percentage plus RAM used/total.
- When sending is paused, sampling and logging continue but MQTT publishing is skipped.

### MQTT Publishing

- Broker host: `resmon.local`
- Broker port: `1883`
- CPU topic: `resmon/cpu`
- Memory topic: `resmon/mem`
- Payload format: invariant-culture ASCII decimal percentage with one decimal place, for example `42.3`
- Payload bounds: values are clamped to `0.0..100.0` before publishing.
- QoS: at most once.

`MetricPayloadFormatter` owns the payload formatting rule so it can be tested
without a live broker.

### Connection Handling

- The daemon attempts to connect to the ESP32 MQTT broker at startup.
- If disconnected during sampling, it attempts to reconnect.
- Reconnect uses bounded exponential backoff:
  - maximum retries: 10
  - initial backoff: 100 ms
  - maximum backoff: 10 seconds
- After repeated failure, the daemon records an error and raises a tray notification.

### Tray Controls

- The tray menu exposes pause/resume controls.
- The menu label updates once per second to reflect the current send state.
- The tray menu can open `daemon.log` from the executable directory.
- Exit stops the hosted service and disposes the tray icon.

## Project Structure

- `Program.cs`: configures the host, registers `MonitorService`, and starts the tray UI.
- `MonitorService.cs`: owns sampling, MQTT connection, publish loop, pause/resume state, and notifications.
- `MetricPayloadFormatter.cs`: formats and clamps percentage payloads for MQTT.
- `NativeMemory.cs`: wraps Windows memory status.
- `TrayContext.cs`: owns the Windows tray menu and user interaction.
- `Provisioning/`: BLE Wi-Fi provisioning client library (see "Provisioning client" below).
- `ResourceMonitorDaemon.Tests`: small zero-dependency test runner for pure daemon logic.

The daemon targets `net9.0-windows10.0.19041.0` (Windows 10 2004 or later)
because the BLE client uses the WinRT `Windows.Devices.Bluetooth` APIs.

## Provisioning client

Namespace `ResourceMonitorDaemon.Provisioning`. It is the Windows side of the
pairing contract (`PROVISIONING.md` on the firmware branch): Arduino-ESP32
`WiFiProv` BLE provisioning with protocomm **Security1** and a proof of
possession (the 8-character pairing code). It is a library only; there is no
wizard UI yet and nothing in `Program.cs` uses it.

Wire formats and behaviour were taken from ESP-IDF **v4.4.7**, the IDF bundled
with Arduino-ESP32 2.0.17 (`framework-arduinoespressif32` 3.20017):
`components/protocomm/proto/{constants,session,sec1}.proto`,
`components/wifi_provisioning/proto/{wifi_constants,wifi_config}.proto`, and the
reference client `tools/esp_prov/{security/security1.py, prov/wifi_prov.py,
transport/ble_cli.py}` at
<https://github.com/espressif/esp-idf/tree/v4.4.7>.

### Components

| Type | Role |
| --- | --- |
| `ProvisioningService` | Facade for the wizard: `ScanAsync`, `TryReadPopFromUsbAsync`, `ProvisionAsync` with step progress. |
| `BleDeviceScanner` | Active BLE scan; reports `PROV_RESMON_*` devices once each, strongest first. |
| `BleProvisioningTransport` | WinRT GATT client. Finds service `021a9004-0382-4aea-bff4-6b3f1c5adfb4`, maps endpoints by their 0x2901 user description, sends each request as write-with-response then reads the response from the same characteristic. |
| `IProvisioningTransport` | `SendAsync(endpoint, data, ct)` seam; the tests use an in-memory fake device behind it. |
| `Security1Session` | X25519 key exchange, key = shared secret XOR SHA-256(pairing code), AES-256-CTR with IV = device random and one keystream for all traffic, Command0/1 handshake and verifier check. |
| `WifiConfigClient` | SetConfig -> ApplyConfig -> GetStatus polling (at most 1 per second, at most 30 s). Result: `Connected(ip4)`, `Failed(AuthError or NetworkNotFound)` or `Timeout`. |
| `SerialPopReader`, `PopLineParser`, `PairingCode` | USB serial pairing-code query (`RESMON?POP`), reply parsing and pairing-code validation. |
| `ProtoWriter` / `ProtoReader`, `Sec1Messages`, `WifiConfigMessages` | Minimal hand-written protobuf codec for just these messages. |

Errors: `PairingCodeRejectedException` (wrong pairing code),
`ProvisioningTransportException` with an `Error` kind (Bluetooth off or
unavailable, device not found or disconnected, timeout, service or endpoint
missing, GATT error), and `ProvisioningProtocolException` (unexpected device
answers). Messages can be shown to the user.

Secrets: the Wi-Fi password is only held in buffers that are zeroed after
encryption. It is never logged, stored, or put in exception messages. The
pairing code is never logged and is redacted from `ToString()`. The SSID is not
logged either.

### Dependencies

- `BouncyCastle.Cryptography`: X25519. .NET has no portable X25519 API, and a
  security protocol should not depend on hand-rolled curve arithmetic. AES-CTR
  uses the built-in .NET AES (ECB mode) plus a 128-bit big-endian counter.
- `System.IO.Ports`: serial port access for the USB pairing-code query. It is
  not part of the base library.
- Windows SDK projection (from the `windows10.0.19041.0` target framework):
  Bluetooth LE scanning and GATT.

### Protocol notes

- On a wrong pairing code, a real ESP-IDF v4.4 device does not send a
  mismatching verifier. `security1.c` fails Command1 and `protocomm_ble.c`
  closes the GATT connection without a response. The client therefore reports
  `PairingCodeRejectedException` (reason `DeviceClosedConnection`) when the link
  fails during Command1. It also reports it (reason `VerificationMismatch`) if
  the device's verifier does not match.
- The device ends the protocomm session when BLE disconnects. The transport
  keeps the link open with `GattSession.MaintainConnection` and fails all later
  requests after any disconnect.
- If an exchange fails half-way, the session is marked unusable because the
  client and device keystreams can no longer be aligned. Reconnect and start a
  new session.
- `proto-ver` is queried in plain text (`---`, as `esp_prov` does) and logged.
  If it is missing, provisioning still continues.

### Verification status

Covered by unit tests (all in-memory):

- protobuf encodings, checked against byte sequences derived by hand from the
  `.proto` field numbers;
- X25519 against the RFC 7748 section 6.1 vectors;
- AES-256-CTR against NIST SP 800-38A F.5.5/F.5.6, plus keystream continuity
  across calls of uneven lengths;
- a full round trip against a fake device that implements the device side
  through separate code paths (BouncyCastle `X25519Agreement` and
  `SicBlockCipher`): Connected, wrong pairing code, forged verifier,
  AuthError, NetworkNotFound, timeout, and the session becoming unusable;
- serial reply parsing, scan de-duplication, and `ProvisioningService` progress
  and log redaction.

**Unverified on real hardware.** Nothing in this library has been run against
a physical ESP32 yet. In particular:

- WinRT BLE scanning, connecting, 0x2901 endpoint discovery, long
  (prepared) writes for the SetConfig payload, and the real Windows error codes
  when the device drops the link on a wrong pairing code;
- interoperability of the Security1 handshake with the real device: the fake
  device follows the IDF sources but is not the IDF implementation;
- the USB serial query: whether opening the port with DTR/RTS deasserted
  avoids resetting this board's USB-UART bridge, and how Bluetooth virtual COM
  ports behave (probes are abandoned after 1.5 s);
- how the firmware behaves after `Connected`. The client expects BLE to stay up
  long enough to read the status; the IDF default auto-stop is 30 s after the
  device gets an IP address.

## Tests

The current C# test runner covers:

- one-decimal invariant percentage formatting;
- clamping out-of-range percentage payloads before publish;
- the provisioning client (`ProvisioningTests.cs` and
  `FakeProvisioningDevice.cs`; see "Provisioning client").

Run it with:

```powershell
dotnet build daemon\ResourceMonitorDaemon.sln
dotnet run --project daemon\ResourceMonitorDaemon.Tests\ResourceMonitorDaemon.Tests.csproj
```

If the live daemon is running and locks the normal output files, build to a
temporary output directory:

```powershell
dotnet build daemon\ResourceMonitorDaemon.sln /p:OutDir=D:\source\esp32-cpu-mem-monitor\.tmpbuild\
dotnet .tmpbuild\ResourceMonitorDaemon.Tests.dll
```

## Current Limitations

- Broker host, port, topics, retry settings, and sampling interval are still hard-coded.
- There is no first-run setup wizard yet. The BLE provisioning client library
  exists, but no UI uses it and it has not been tested against real hardware.
- Wi-Fi credentials must not be stored in daemon source code or logs.
- If later password recall is needed, it should be opt-in and protected with Windows DPAPI.
