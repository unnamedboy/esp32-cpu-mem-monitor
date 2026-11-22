using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;
using ResourceMonitorDaemon.Observability;
using ResourceMonitorDaemon.Provisioning;

namespace ResourceMonitorDaemon.Tests
{
    /// <summary>
    /// MonitorService end to end (real MqttConnection + in-process loopback MQTTnet broker),
    /// the shutdown budget, and the serial-port probe filter. Timing assertions use counts over
    /// windows with generous tolerances so they stay deterministic on a busy machine.
    /// </summary>
    internal static class MonitorServiceTests
    {
        private const string CpuTopic = DeviceSettings.DefaultCpuTopic;
        private const string MemTopic = DeviceSettings.DefaultMemTopic;
        private static readonly Regex PayloadFormat = new(@"^\d{1,3}\.\d$", RegexOptions.CultureInvariant);

        public static readonly (string Name, Action Run)[] All =
        {
            ("MonitorService publishes one cpu and one mem message per interval, wire format unchanged", () => RunAsync(PublishesPerIntervalAsync)),
            ("MonitorService publishes nothing while paused and resumes after Resume", () => RunAsync(PauseAndResumeAsync)),
            ("MonitorService applies an interval change promptly", () => RunAsync(IntervalChangeAsync)),

            ("Shutdown budget covers MQTT dispose plus the Loki flush", () =>
            {
                AssertTrue(MqttConnection.MaxShutdownDuration <= TimeSpan.FromSeconds(1), $"MQTT shutdown bound {MqttConnection.MaxShutdownDuration}");
                AssertTrue(
                    ResourceMonitorDaemon.Program.ShutdownTimeout >= MqttConnection.MaxShutdownDuration + ObservabilityService.ShutdownFlushBudget + TimeSpan.FromMilliseconds(500),
                    $"host budget {ResourceMonitorDaemon.Program.ShutdownTimeout} leaves no margin");
                AssertTrue(ResourceMonitorDaemon.Program.ShutdownTimeout <= TimeSpan.FromSeconds(5), "host budget should stay modest");
            }),

            ("SerialPortFilter probes only known ESP32 USB-UART bridges when the vendor is known", () =>
            {
                AssertTrue(Probe("COM3", @"\Device\Silabser0", 0x10C4), "CP210x");
                AssertTrue(Probe("COM4", @"\Device\USBSER000", 0x1A86), "CH340/CH9102");
                AssertTrue(Probe("COM5", @"\Device\VCP0", 0x0403), "FTDI");
                AssertTrue(Probe("COM6", @"\Device\USBSER001", 0x303A), "Espressif native USB");
                AssertTrue(!Probe("COM7", @"\Device\USBSER002", 0x2341), "unrelated USB serial device (Arduino) must be skipped");
                AssertTrue(!Probe("COM8", @"\Device\USBSER003", 0x8087), "unrelated USB serial device must be skipped");
            }),
            ("SerialPortFilter skips legacy UARTs and Bluetooth ports when the vendor is unknown", () =>
            {
                AssertTrue(!Probe("COM1", @"\Device\Serial0", null), "legacy UART");
                AssertTrue(!Probe("COM2", @"\Device\Serial12", null), "legacy UART, two digits");
                AssertTrue(!Probe("COM9", @"\Device\BthModem0", null), "Bluetooth modem");
                AssertTrue(!Probe("COM10", @"\Device\BthEnum5", null), "Bluetooth enum");
                AssertTrue(Probe("COM11", @"\Device\USBSER000", null), "USB CDC without vendor info is probed");
                AssertTrue(Probe("COM12", @"\Device\SerialUsb0", null), "only exact \\Device\\Serial<n> counts as legacy");
                AssertTrue(Probe("COM13", null, null), "nothing known: probe as before");
            }),
            ("SerialPortFilter.ParseVendorId reads USB and FTDI enumeration key names", () =>
            {
                AssertEqual<ushort?>(0x10C4, SerialPortFilter.ParseVendorId("VID_10C4&PID_EA60"));
                AssertEqual<ushort?>(0x303A, SerialPortFilter.ParseVendorId("VID_303A&PID_1001&MI_00"));
                AssertEqual<ushort?>(0x0403, SerialPortFilter.ParseVendorId("VID_0403+PID_6001+A50285BIA"));
                AssertEqual<ushort?>(0x1A86, SerialPortFilter.ParseVendorId("vid_1a86&pid_7523"));
                AssertEqual<ushort?>(null, SerialPortFilter.ParseVendorId("ROOT_HUB30"));
                AssertEqual<ushort?>(null, SerialPortFilter.ParseVendorId("VID_12&PID_34"));
                AssertEqual<ushort?>(null, SerialPortFilter.ParseVendorId("XVID_10C4&PID_EA60"));
                AssertEqual<ushort?>(null, SerialPortFilter.ParseVendorId(null));
            }),
            ("SerialPortInventory reads this machine's registry without throwing", () =>
            {
                var described = SerialPortInventory.Describe(new[] { "COM1", "COM250" });
                AssertEqual(2, described.Count);
                AssertEqual("COM250", described[1].PortName);
            }),
        };

        private static bool Probe(string port, string? device, ushort? vendor) =>
            SerialPortFilter.ShouldProbe(new SerialPortCandidate(port, device, vendor), out _);

        // ---- MonitorService scenarios ----------------------------------------------------------

        private static async Task PublishesPerIntervalAsync()
        {
            await using var harness = await Harness.StartAsync(sampleIntervalMs: 250);
            await harness.WaitForFirstMessageAsync();

            var window = await harness.CollectAsync(TimeSpan.FromSeconds(2));
            var cpu = window.Where(m => m.Topic == CpuTopic).ToList();
            var mem = window.Where(m => m.Topic == MemTopic).ToList();

            // ~8 ticks expected in 2 s at 250 ms; generous bounds for a loaded machine.
            AssertTrue(cpu.Count is >= 4 and <= 10, $"cpu messages in 2 s: {cpu.Count}");
            AssertTrue(Math.Abs(cpu.Count - mem.Count) <= 1, $"cpu {cpu.Count} vs mem {mem.Count}");
            AssertEqual(0, window.Count(m => m.Topic is not CpuTopic and not MemTopic));

            // Exactly one cpu and one mem per tick: the stream strictly alternates cpu, mem.
            var topics = window.Select(m => m.Topic).SkipWhile(t => t == MemTopic).ToList();
            for (var i = 0; i < topics.Count; i++)
            {
                AssertEqual(i % 2 == 0 ? CpuTopic : MemTopic, topics[i]);
            }

            foreach (var message in window)
            {
                AssertTrue(PayloadFormat.IsMatch(message.Payload), $"payload '{message.Payload}' is not an ASCII one-decimal percentage");
                var value = double.Parse(message.Payload, CultureInfo.InvariantCulture);
                AssertTrue(value is >= 0 and <= 100, $"payload {value} out of range");
                AssertEqual(MqttQualityOfServiceLevel.AtMostOnce, message.Qos);
                AssertTrue(!message.Retain, "message is retained");
            }
        }

        private static async Task PauseAndResumeAsync()
        {
            await using var harness = await Harness.StartAsync(sampleIntervalMs: 250);
            await harness.WaitForFirstMessageAsync();

            harness.Service.Pause();
            AssertTrue(harness.Service.IsPaused, "not paused");
            await Task.Delay(400);                      // let a tick that was already publishing finish
            var paused = await harness.CollectAsync(TimeSpan.FromSeconds(1.2));
            AssertEqual(0, paused.Count);

            harness.Service.Resume();
            var resumedAfter = await harness.WaitForFirstMessageAsync();
            AssertTrue(resumedAfter < TimeSpan.FromSeconds(2), $"publishing resumed after {resumedAfter.TotalMilliseconds:F0} ms");
        }

        private static async Task IntervalChangeAsync()
        {
            // Start with a long interval: connected, but nothing is published for a minute.
            await using var harness = await Harness.StartAsync(sampleIntervalMs: 60000);
            await harness.WaitUntilAsync(() => harness.Service.IsConnected, TimeSpan.FromSeconds(5), "MonitorService never connected");
            AssertEqual(0, (await harness.CollectAsync(TimeSpan.FromMilliseconds(300))).Count);

            harness.Options.Set(harness.Options.CurrentValue with { SampleIntervalMs = 250 });
            var firstAfter = await harness.WaitForFirstMessageAsync();
            AssertTrue(firstAfter < TimeSpan.FromSeconds(1.5), $"250 ms interval applied after {firstAfter.TotalMilliseconds:F0} ms");
            var fast = (await harness.CollectAsync(TimeSpan.FromSeconds(2))).Count(m => m.Topic == CpuTopic);
            AssertTrue(fast is >= 4 and <= 10, $"cpu messages in 2 s at 250 ms: {fast}");

            harness.Options.Set(harness.Options.CurrentValue with { SampleIntervalMs = 1000 });
            await Task.Delay(300);
            var slow = (await harness.CollectAsync(TimeSpan.FromSeconds(3))).Count(m => m.Topic == CpuTopic);
            AssertTrue(slow is >= 2 and <= 4, $"cpu messages in 3 s at 1000 ms: {slow}");
        }

        private static void RunAsync(Func<Task> test) => test().GetAwaiter().GetResult();

        private static void AssertTrue(bool condition, string message) => ProvisioningTests.AssertTrue(condition, message);

        private static void AssertEqual<T>(T expected, T actual) => ProvisioningTests.AssertEqual(expected, actual);

        private sealed record Received(string Topic, string Payload, MqttQualityOfServiceLevel Qos, bool Retain);

        /// <summary>Loopback broker + MonitorService with a settable options monitor.</summary>
        private sealed class Harness : IAsyncDisposable
        {
            private readonly ConcurrentQueue<MqttApplicationMessage> _received;
            private readonly MqttServer _server;
            private readonly string _tempDirectory;

            private Harness(ConcurrentQueue<MqttApplicationMessage> received, MqttServer server, TestOptionsMonitor options, MonitorService service, string tempDirectory)
            {
                _received = received;
                _server = server;
                Options = options;
                Service = service;
                _tempDirectory = tempDirectory;
            }

            public TestOptionsMonitor Options { get; }

            public MonitorService Service { get; }

            public static async Task<Harness> StartAsync(int sampleIntervalMs)
            {
                var port = MqttConnectionScenario.FreeLoopbackPort();
                var received = new ConcurrentQueue<MqttApplicationMessage>();
                var server = await MqttConnectionScenario.StartBrokerAsync(port, received);
                var options = new TestOptionsMonitor(new DeviceSettings
                {
                    Host = "127.0.0.1",
                    Port = port,
                    SampleIntervalMs = sampleIntervalMs,
                    ConnectTimeoutMs = 1000,
                });
                var tempDirectory = Path.Combine(Path.GetTempPath(), "resmon-monitor-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDirectory);
                var service = new MonitorService(
                    NullLogger<MonitorService>.Instance,
                    options,
                    new MqttConnection(NullLogger<MqttConnection>.Instance),
                    new UserSettingsStore(Path.Combine(tempDirectory, "settings.json")));
                await service.StartAsync(CancellationToken.None);
                return new Harness(received, server, options, service, tempDirectory);
            }

            /// <summary>Discards what arrived so far, then waits for the next metric message.</summary>
            public async Task<TimeSpan> WaitForFirstMessageAsync()
            {
                _received.Clear();
                var stopwatch = Stopwatch.StartNew();
                await WaitUntilAsync(() => _received.Any(m => m.Topic is CpuTopic or MemTopic), TimeSpan.FromSeconds(5), "no metric message arrived");
                return stopwatch.Elapsed;
            }

            /// <summary>Discards what arrived so far and returns everything received during <paramref name="window"/>.</summary>
            public async Task<IReadOnlyList<Received>> CollectAsync(TimeSpan window)
            {
                _received.Clear();
                await Task.Delay(window);
                return _received
                    .Where(m => m.Topic is CpuTopic or MemTopic)
                    .Select(m => new Received(m.Topic, Encoding.ASCII.GetString(m.PayloadSegment), m.QualityOfServiceLevel, m.Retain))
                    .ToList();
            }

            public async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string failure)
            {
                var stopwatch = Stopwatch.StartNew();
                while (!condition())
                {
                    AssertTrue(stopwatch.Elapsed < timeout, failure);
                    await Task.Delay(20);
                }
            }

            public async ValueTask DisposeAsync()
            {
                await Service.StopAsync(CancellationToken.None);
                await _server.StopAsync();
                _server.Dispose();
                try
                {
                    Directory.Delete(_tempDirectory, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }

        /// <summary>IOptionsMonitor whose value the test sets; listeners run synchronously like a config reload.</summary>
        private sealed class TestOptionsMonitor : IOptionsMonitor<DeviceSettings>
        {
            private readonly object _gate = new();
            private readonly List<Action<DeviceSettings, string?>> _listeners = new();
            private DeviceSettings _current;

            public TestOptionsMonitor(DeviceSettings initial) => _current = initial;

            public DeviceSettings CurrentValue
            {
                get
                {
                    lock (_gate)
                    {
                        return _current;
                    }
                }
            }

            public DeviceSettings Get(string? name) => CurrentValue;

            public void Set(DeviceSettings value)
            {
                Action<DeviceSettings, string?>[] listeners;
                lock (_gate)
                {
                    _current = value;
                    listeners = _listeners.ToArray();
                }

                foreach (var listener in listeners)
                {
                    listener(value, Microsoft.Extensions.Options.Options.DefaultName);
                }
            }

            public IDisposable? OnChange(Action<DeviceSettings, string?> listener)
            {
                lock (_gate)
                {
                    _listeners.Add(listener);
                }

                return new Subscription(() =>
                {
                    lock (_gate)
                    {
                        _listeners.Remove(listener);
                    }
                });
            }

            private sealed class Subscription(Action dispose) : IDisposable
            {
                public void Dispose() => dispose();
            }
        }
    }
}
