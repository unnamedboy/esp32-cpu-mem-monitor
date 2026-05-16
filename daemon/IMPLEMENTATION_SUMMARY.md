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
- `ResourceMonitorDaemon.Tests`: small zero-dependency test runner for pure daemon logic.

## Tests

The current C# test runner covers:

- one-decimal invariant percentage formatting;
- clamping out-of-range percentage payloads before publish.

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
- There is no first-run setup wizard yet.
- The daemon does not provision ESP32 Wi-Fi credentials yet.
- Wi-Fi credentials must not be stored in daemon source code or logs.
- If later password recall is needed, it should be opt-in and protected with Windows DPAPI.
