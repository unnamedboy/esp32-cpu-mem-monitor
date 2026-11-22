using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResourceMonitorDaemon;
using ResourceMonitorDaemon.Tests;

var tests = new (string Name, Action Run)[]
{
    ("FormatPercentPayload uses invariant one-decimal formatting", () =>
    {
        AssertEqual("0.0", MetricPayloadFormatter.FormatPercentPayload(0));
        AssertEqual("42.3", MetricPayloadFormatter.FormatPercentPayload(42.26));
        AssertEqual("100.0", MetricPayloadFormatter.FormatPercentPayload(100));
    }),
    ("FormatPercentPayload clamps out-of-range values", () =>
    {
        AssertEqual("0.0", MetricPayloadFormatter.FormatPercentPayload(-0.1));
        AssertEqual("100.0", MetricPayloadFormatter.FormatPercentPayload(100.1));
    }),
    // ---- DeviceSettings validation -------------------------------------------------------
    ("DeviceSettings defaults match the MQTT contract and are valid", () =>
    {
        var defaults = new DeviceSettings();
        AssertEqual("resmon.local", defaults.Host);
        AssertEqual(1883, defaults.Port);
        AssertEqual("resmon/cpu", defaults.CpuTopic);
        AssertEqual("resmon/mem", defaults.MemTopic);
        AssertEqual(1000, defaults.SampleIntervalMs);
        AssertEqual("", defaults.FallbackAddress);
        AssertEqual(0, DeviceSettingsValidator.Validate(defaults).Count);
    }),
    ("Validate rejects bad host, port, fallback and wildcard topics", () =>
    {
        AssertTrue(DeviceSettingsValidator.ValidateHost("") != null, "empty host");
        AssertTrue(DeviceSettingsValidator.ValidateHost("bad host!") != null, "host with space");
        AssertTrue(DeviceSettingsValidator.ValidateHost("192.168.1.20") == null, "ipv4 host");
        AssertTrue(DeviceSettingsValidator.ValidatePort(0) != null, "port 0");
        AssertTrue(DeviceSettingsValidator.ValidatePort(65536) != null, "port 65536");
        AssertTrue(DeviceSettingsValidator.ValidatePort(65535) == null, "port 65535");
        AssertTrue(DeviceSettingsValidator.ValidateFallbackAddress("") == null, "empty fallback ok");
        AssertTrue(DeviceSettingsValidator.ValidateFallbackAddress("10.0.0.7") == null, "ipv4 fallback");
        AssertTrue(DeviceSettingsValidator.ValidateFallbackAddress("resmon.local") != null, "hostname fallback");
        AssertTrue(DeviceSettingsValidator.ValidateFallbackAddress("1") != null, "short ipv4 form");
        AssertTrue(DeviceSettingsValidator.ValidateFallbackAddress("::1") != null, "ipv6 fallback");
        AssertTrue(DeviceSettingsValidator.ValidateTopic("resmon/#", "t") != null, "# wildcard");
        AssertTrue(DeviceSettingsValidator.ValidateTopic("resmon/+/cpu", "t") != null, "+ wildcard");
        AssertTrue(DeviceSettingsValidator.ValidateTopic(" ", "t") != null, "blank topic");
        var same = new DeviceSettings { MemTopic = "resmon/cpu" };
        AssertTrue(DeviceSettingsValidator.Validate(same).Count > 0, "identical topics");
    }),
    ("Sanitize falls back to defaults and clamps ranges with warnings", () =>
    {
        var warnings = new List<string>();
        var input = new DeviceSettings
        {
            Host = "  ",
            FallbackAddress = "not-an-ip",
            Port = 70000,
            CpuTopic = "resmon/#",
            MemTopic = "a/+/b",
            SampleIntervalMs = 10,
            ReconnectInitialBackoffMs = 5000,
            ReconnectMaxBackoffMs = 2000,
            ConnectTimeoutMs = 999999,
        };
        var result = DeviceSettingsValidator.Sanitize(input, warnings);
        AssertEqual(DeviceSettings.DefaultHost, result.Host);
        AssertEqual("", result.FallbackAddress);
        AssertEqual(DeviceSettings.DefaultPort, result.Port);
        AssertEqual(DeviceSettings.DefaultCpuTopic, result.CpuTopic);
        AssertEqual(DeviceSettings.DefaultMemTopic, result.MemTopic);
        AssertEqual(DeviceSettingsValidator.MinSampleIntervalMs, result.SampleIntervalMs);
        AssertEqual(5000, result.ReconnectMaxBackoffMs);
        AssertEqual(DeviceSettingsValidator.MaxConnectTimeoutMs, result.ConnectTimeoutMs);
        AssertEqual(0, DeviceSettingsValidator.Validate(result).Count);
        AssertTrue(warnings.Count >= 8, $"expected one warning per bad value, got {warnings.Count}");

        var high = DeviceSettingsValidator.Sanitize(new DeviceSettings { SampleIntervalMs = 999999 }, new List<string>());
        AssertEqual(DeviceSettingsValidator.MaxSampleIntervalMs, high.SampleIntervalMs);
    }),
    ("Sanitize leaves valid settings untouched", () =>
    {
        var warnings = new List<string>();
        var input = new DeviceSettings { Host = "192.168.4.1", FallbackAddress = "192.168.4.2", Port = 1884, SampleIntervalMs = 500 };
        AssertTrue(DeviceSettingsValidator.Sanitize(input, warnings) == input, "settings changed");
        AssertEqual(0, warnings.Count);
    }),
    ("Loader tolerates non-numeric values without throwing", () =>
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Device:Port"] = "abc",
            ["Device:SampleIntervalMs"] = "fast",
            ["Device:Host"] = "esp.lan",
        }).Build();
        var warnings = new List<string>();
        var settings = DeviceSettingsLoader.Load(configuration, warnings);
        AssertEqual(DeviceSettings.DefaultPort, settings.Port);
        AssertEqual(DeviceSettings.DefaultSampleIntervalMs, settings.SampleIntervalMs);
        AssertEqual("esp.lan", settings.Host);
        AssertEqual(2, warnings.Count);
    }),

    // ---- Configuration precedence --------------------------------------------------------
    ("Precedence: defaults < appsettings.json < user file < RESMON_ env", () =>
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "appsettings.json"),
            """{ "Device": { "Host": "from-appsettings", "Port": 1000, "CpuTopic": "app/cpu", "SampleIntervalMs": 2000 } }""");
        var userPath = Path.Combine(temp.Path, "user", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(userPath)!);
        File.WriteAllText(userPath, """{ "Device": { "Host": "from-user", "Port": 2000 } }""");

        const string envName = "RESMON_Device__Host";
        var previous = Environment.GetEnvironmentVariable(envName);
        try
        {
            Environment.SetEnvironmentVariable(envName, null);
            var configuration = new ConfigurationBuilder()
                .AddDaemonSources(temp.Path, userPath, reloadOnChange: false)
                .Build();
            var withoutEnv = DeviceSettingsLoader.Load(configuration, new List<string>());
            AssertEqual("from-user", withoutEnv.Host);       // user file beats appsettings

            Environment.SetEnvironmentVariable(envName, "from-env");
            configuration = new ConfigurationBuilder()
                .AddDaemonSources(temp.Path, userPath, reloadOnChange: false)
                .Build();
            var settings = DeviceSettingsLoader.Load(configuration, new List<string>());
            AssertEqual("from-env", settings.Host);          // env wins
            AssertEqual(2000, settings.Port);                // user file beats appsettings
            AssertEqual("app/cpu", settings.CpuTopic);       // appsettings beats default
            AssertEqual(2000, settings.SampleIntervalMs);
            AssertEqual(DeviceSettings.DefaultMemTopic, settings.MemTopic); // built-in default
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previous);
        }
    }),
    ("Broken JSON file is ignored and reported instead of throwing", () =>
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "appsettings.json"), """{ "Device": { "Port": 1999 } }""");
        var userPath = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(userPath, "{ this is not json");
        var errors = new List<string>();
        var configuration = new ConfigurationBuilder()
            .AddDaemonSources(temp.Path, userPath, errors.Add, reloadOnChange: false)
            .Build();
        var settings = DeviceSettingsLoader.Load(configuration, new List<string>());
        AssertEqual(1999, settings.Port);
        AssertEqual(1, errors.Count);
    }),

    // ---- UserSettingsStore ---------------------------------------------------------------
    ("Update writes only the fields that were set; appsettings.json values keep applying", () =>
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "appsettings.json"),
            """{ "Device": { "Host": "app.host", "Port": 1999 } }""");
        var userPath = Path.Combine(temp.Path, "user", "settings.json");
        var store = new UserSettingsStore(userPath);
        AssertTrue(store.Load().IsEmpty, "missing file should yield an empty patch");

        var saved = 0;
        store.Saved += (_, _) => saved++;
        store.Update(patch => patch.FallbackAddress = "192.168.1.77");   // pairing-wizard pattern
        AssertEqual(1, saved);
        AssertTrue(!File.Exists(userPath + ".tmp"), "temporary file left behind");

        var device = JsonNode.Parse(File.ReadAllText(userPath))!["Device"]!.AsObject();
        AssertEqual("FallbackAddress", string.Join(",", device.Select(p => p.Key)));
        AssertTrue(store.Load() == new UserDeviceSettingsPatch { FallbackAddress = "192.168.1.77" }, "patch round-trip");

        var effective = LoadEffective(temp.Path, userPath);
        AssertEqual("app.host", effective.Host);
        AssertEqual(1999, effective.Port);
        AssertEqual("192.168.1.77", effective.FallbackAddress);

        // Clearing a field removes it from the file again.
        store.Update(patch => patch.FallbackAddress = null);
        AssertTrue(store.Load().IsEmpty, "field not removed");
        AssertEqual("", LoadEffective(temp.Path, userPath).FallbackAddress);
    }),
    ("Dialog save does not persist env-overridden values or copies of lower layers", () =>
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "appsettings.json"),
            """{ "Device": { "Host": "app.host", "Port": 1999, "CpuTopic": "app/cpu" } }""");
        var userPath = Path.Combine(temp.Path, "settings.json");
        var store = new UserSettingsStore(userPath);
        store.Update(patch => { patch.Host = "user.host"; patch.FallbackAddress = "10.0.0.9"; });

        const string envName = "RESMON_Device__Host";
        var previous = Environment.GetEnvironmentVariable(envName);
        try
        {
            Environment.SetEnvironmentVariable(envName, "env.host");
            var envFields = DaemonConfiguration.GetEnvironmentOverriddenDeviceFields();
            AssertTrue(envFields.Contains("Host"), "env field not detected");

            // The dialog edits the effective values: Host shows env.host (read-only), the user
            // changes only Port and leaves CpuTopic at the appsettings value.
            var lowerLayers = DaemonConfiguration.LoadLowerLayers(temp.Path);
            AssertEqual("app.host", lowerLayers.Host);
            var displayed = LoadEffective(temp.Path, userPath);
            AssertEqual("env.host", displayed.Host);
            var edited = displayed with { Port = 1885 };
            store.Update(patch => patch.ApplyDialogEdit(lowerLayers, displayed, edited, envFields));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previous);
        }

        var saved = store.Load();
        AssertEqual("user.host", saved.Host);            // env value not persisted; user value kept
        AssertEqual(1885, saved.Port);
        AssertEqual(null, saved.CpuTopic);               // equal to appsettings → not written
        AssertEqual("10.0.0.9", saved.FallbackAddress);  // not shown in dialog → untouched
        AssertTrue(!File.ReadAllText(userPath).Contains("env.host"), "env value written to file");

        var effective = LoadEffective(temp.Path, userPath);
        AssertEqual("user.host", effective.Host);

        // Editing a value back to the lower-layer value removes it from the file.
        store.Update(p => p.ApplyDialogEdit(DaemonConfiguration.LoadLowerLayers(temp.Path), effective, effective with { Port = 1999 }, new HashSet<string>()));
        AssertEqual(null, store.Load().Port);
        AssertEqual("user.host", store.Load().Host);
    }),
    ("Dialog save touches only fields the user changed, even if it showed stale values", () =>
    {
        using var temp = new TempDir();
        var store = new UserSettingsStore(Path.Combine(temp.Path, "settings.json"));
        store.Update(patch => patch.Host = "new.host");     // saved elsewhere after the dialog opened

        var lowerLayers = new DeviceSettings();
        var displayed = lowerLayers with { Host = "old.host" };   // stale value shown in the dialog
        var edited = displayed with { SampleIntervalMs = 2000 };  // user changes only the interval
        store.Update(patch => patch.ApplyDialogEdit(lowerLayers, displayed, edited, new HashSet<string>()));

        AssertTrue(store.Load() == new UserDeviceSettingsPatch { Host = "new.host", SampleIntervalMs = 2000 }, "patch: " + store.Load().Describe());
    }),
    ("Update preserves other sections, unknown keys, and tolerates comments/trailing commas", () =>
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path, """
            {
              // hand-edited
              "Logging": { "LogLevel": { "Default": "Debug", }, },
              "Device": { "port": 1884, "FutureKey": "keep", },
            }
            """);
        var store = new UserSettingsStore(path);
        AssertEqual(1884, store.Load().Port);
        store.Update(patch => patch.CpuTopic = "lab/cpu");

        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        AssertEqual("Debug", root["Logging"]!["LogLevel"]!["Default"]!.GetValue<string>());
        AssertEqual("keep", root["Device"]!["FutureKey"]!.GetValue<string>());
        AssertEqual("Port,CpuTopic", string.Join(",", root["Device"]!.AsObject().Select(p => p.Key).Where(k => k != "FutureKey")));
        AssertEqual(1884, root["Device"]!["Port"]!.GetValue<int>());
        AssertEqual(null, store.LastBackupPath);

        AssertThrows<ArgumentException>(() => store.Update(patch => patch.MemTopic = "resmon/#"));
        AssertEqual(null, store.Load().MemTopic);
    }),
    ("Unparsable settings file: Load returns empty patch, Update backs it up first", () =>
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "settings.json");
        const string broken = "{ \"Logging\": { not json";
        File.WriteAllText(path, broken);
        var store = new UserSettingsStore(path);
        AssertTrue(store.Load().IsEmpty, "expected empty patch");

        store.Update(patch => patch.Port = 1886);
        var firstBackup = store.LastBackupPath!;
        AssertEqual(broken, File.ReadAllText(firstBackup));
        AssertTrue(Path.GetFileName(firstBackup).StartsWith("settings.json.") && firstBackup.EndsWith(".bak"), "backup name " + firstBackup);
        AssertEqual(1886, store.Load().Port);

        // A second broken file gets its own backup; the first one is never overwritten.
        File.WriteAllText(path, "also broken");
        store.Update(patch => patch.Port = 1887);
        AssertTrue(store.LastBackupPath != firstBackup, "backup reused");
        AssertEqual("also broken", File.ReadAllText(store.LastBackupPath!));
        AssertEqual(broken, File.ReadAllText(firstBackup));
    }),
    ("Empty or whitespace settings file is treated as empty without a backup", () =>
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path, "  " + Environment.NewLine + " ");
        var store = new UserSettingsStore(path);
        store.Update(patch => patch.Port = 1888);
        AssertEqual(null, store.LastBackupPath);
        AssertEqual(1, Directory.GetFiles(temp.Path).Length);
        AssertEqual(1888, store.Load().Port);
    }),
    ("Saved settings reach a live configuration through the file watcher", () =>
    {
        using var temp = new TempDir();
        var userPath = Path.Combine(temp.Path, "settings.json");
        var configuration = new ConfigurationBuilder().AddDaemonSources(temp.Path, userPath).Build();
        try
        {
            new UserSettingsStore(userPath).Update(patch => patch.Port = 1887);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            while (DeviceSettingsLoader.Load(configuration, new List<string>()).Port != 1887 && stopwatch.ElapsedMilliseconds < 5000)
            {
                Thread.Sleep(50);
            }

            AssertEqual(1887, DeviceSettingsLoader.Load(configuration, new List<string>()).Port);
        }
        finally
        {
            (configuration as IDisposable)?.Dispose();
        }
    }),
    ("Repeated reloads log the same invalid-setting warnings only once", () =>
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Device:Port"] = "0" })
            .Build();
        var captured = new CapturingLoggerProvider();
        using var services = new ServiceCollection()
            .AddLogging(logging => logging.AddProvider(captured))
            .AddSingleton<IConfiguration>(configuration)
            .AddDeviceSettings()
            .BuildServiceProvider();
        var monitor = services.GetRequiredService<IOptionsMonitor<DeviceSettings>>();
        AssertEqual(DeviceSettings.DefaultPort, monitor.CurrentValue.Port);
        for (var i = 0; i < 3; i++)
        {
            configuration.Reload();
            AssertEqual(DeviceSettings.DefaultPort, monitor.CurrentValue.Port);
        }

        AssertEqual(1, captured.Messages.Count(m => m.Contains("Port 0")));
    }),
    ("Configuration load errors report the absolute file path", () =>
    {
        using var temp = new TempDir();
        var userPath = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(userPath, "{ broken");
        var errors = new List<string>();
        new ConfigurationBuilder().AddDaemonSources(temp.Path, userPath, errors.Add, reloadOnChange: false).Build();
        AssertTrue(errors.Count == 1 && errors[0].Contains(Path.GetFullPath(userPath)), "message: " + string.Join(" | ", errors));
    }),

    // ---- DaemonLoggerProvider ------------------------------------------------------------
    ("DaemonLoggerProvider rolls daemon.log to daemon.log.1 at the size limit", () =>
    {
        using var temp = new TempDir();
        var provider = new DaemonLoggerProvider(Path.Combine(temp.Path, "logs", "daemon.log"), maxFileSizeBytes: 2048);
        var logger = provider.CreateLogger("ResourceMonitorDaemon.Tests");
        var tasks = Enumerable.Range(0, 4).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 25; i++)
            {
                Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(logger, "thread {Thread} line {Line} " + new string('x', 40), t, i);
            }
        })).ToArray();
        Task.WaitAll(tasks);

        AssertTrue(File.Exists(provider.RolledFilePath), "rolled file missing");
        AssertTrue(new FileInfo(provider.FilePath).Length <= 2048, "active log exceeds limit");
        var lines = File.ReadAllLines(provider.FilePath).Concat(File.ReadAllLines(provider.RolledFilePath)).ToArray();
        AssertTrue(lines.All(l => l.StartsWith("[") && l.Contains("[Information] [Tests] thread")), "interleaved or malformed line");
    }),

    // ---- ReconnectBackoff ----------------------------------------------------------------
    ("ReconnectBackoff is due immediately, doubles per failure and caps at max", () =>
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var backoff = new ReconnectBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8), () => now);
        AssertTrue(backoff.IsAttemptDue, "first attempt should be due immediately");

        var delays = new List<double>();
        for (var i = 0; i < 6; i++)
        {
            var delay = backoff.RecordFailure();
            delays.Add(delay.TotalSeconds);
            AssertTrue(!backoff.IsAttemptDue, $"attempt due right after failure {i + 1}");
            now += delay - TimeSpan.FromMilliseconds(1);
            AssertTrue(!backoff.IsAttemptDue, $"attempt due before deadline after failure {i + 1}");
            now += TimeSpan.FromMilliseconds(1);
            AssertTrue(backoff.IsAttemptDue, $"attempt not due at deadline after failure {i + 1}");
        }

        AssertEqual("1,2,4,8,8,8", string.Join(",", delays));
        AssertEqual(6, backoff.ConsecutiveFailures);
    }),
    ("ReconnectBackoff.Reset restores the initial delay and makes the attempt due", () =>
    {
        var now = DateTimeOffset.UnixEpoch;
        var backoff = new ReconnectBackoff(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(30), () => now);
        backoff.RecordFailure();
        backoff.RecordFailure();
        backoff.RecordFailure();
        AssertEqual(TimeSpan.FromSeconds(4), backoff.NextDelay);
        backoff.Reset();
        AssertTrue(backoff.IsAttemptDue, "not due after reset");
        AssertEqual(0, backoff.ConsecutiveFailures);
        AssertEqual(TimeSpan.FromMilliseconds(500), backoff.RecordFailure());
    }),
    ("ReconnectBackoff treats max < initial as max = initial", () =>
    {
        var backoff = new ReconnectBackoff(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1));
        AssertEqual(TimeSpan.FromSeconds(5), backoff.RecordFailure());
        AssertEqual(TimeSpan.FromSeconds(5), backoff.RecordFailure());
    }),

    ("ReconnectBackoff keeps backing off a broker that drops connections immediately", () =>
    {
        var now = DateTimeOffset.UnixEpoch;
        var backoff = new ReconnectBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8), () => now);
        var delays = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            AssertTrue(backoff.IsAttemptDue, $"attempt {i} not due");
            backoff.RecordConnected();
            now += TimeSpan.FromMilliseconds(200);            // broker drops after 200 ms
            var delay = backoff.RecordDisconnected();
            AssertTrue(delay != null, "short-lived connection reset the backoff");
            delays.Add(delay!.Value.TotalSeconds);
            AssertTrue(!backoff.IsAttemptDue, "flapping broker retried immediately");
            now += delay.Value;
        }

        AssertEqual("1,2,4,8,8", string.Join(",", delays));
    }),
    ("ReconnectBackoff resets only after a connection stays up for the max delay", () =>
    {
        var now = DateTimeOffset.UnixEpoch;
        var backoff = new ReconnectBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8), () => now);
        backoff.RecordFailure();
        backoff.RecordFailure();
        now += TimeSpan.FromSeconds(10);
        backoff.RecordConnected();
        AssertEqual(2, backoff.ConsecutiveFailures);          // success alone does not reset
        now += backoff.StableConnectionThreshold;
        AssertTrue(backoff.RecordDisconnected() == null, "stable connection should reset");
        AssertEqual(0, backoff.ConsecutiveFailures);
        AssertTrue(backoff.IsAttemptDue, "reconnect after a stable connection should be immediate");
        AssertEqual(TimeSpan.FromSeconds(1), backoff.NextDelay);
    }),

    ("ReconnectBackoff.Reconfigure keeps the connection state and clamps the next delay", () =>
    {
        var now = DateTimeOffset.UnixEpoch;
        var backoff = new ReconnectBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8), () => now);
        backoff.RecordFailure();
        backoff.RecordFailure();
        backoff.RecordFailure();                              // next delay 8 s
        backoff.RecordConnected();
        backoff.Reconfigure(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4));
        AssertEqual(TimeSpan.FromSeconds(4), backoff.NextDelay);
        AssertEqual(3, backoff.ConsecutiveFailures);
        now += TimeSpan.FromSeconds(4);
        AssertTrue(backoff.RecordDisconnected() == null, "stability timer lost across Reconfigure");
        AssertEqual(TimeSpan.FromSeconds(2), backoff.NextDelay);
    }),
    ("ReconnectBackoff caps the stability threshold at 30 s", () =>
    {
        var now = DateTimeOffset.UnixEpoch;
        var backoff = new ReconnectBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(10), () => now);
        AssertEqual(TimeSpan.FromSeconds(30), backoff.StableConnectionThreshold);
        backoff.RecordFailure();
        backoff.RecordConnected();
        now += TimeSpan.FromSeconds(30);
        AssertTrue(backoff.RecordDisconnected() == null, "30 s connection should count as stable");
    }),

    ("MonitorService.StopAsync is idempotent (a duplicate host stop does nothing)", () =>
    {
        using var temp = new TempDir();
        var captured = new CapturingLoggerProvider();
        var services = new ServiceCollection()
            .AddLogging(logging => logging.AddProvider(captured))
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddDeviceSettings()
            .AddSingleton<ISettingsStore>(new UserSettingsStore(Path.Combine(temp.Path, "settings.json")))
            .AddSingleton(provider => new MqttConnection(provider.GetRequiredService<ILogger<MqttConnection>>()))
            .AddSingleton<MonitorService>()
            .BuildServiceProvider();
        var service = services.GetRequiredService<MonitorService>();

        service.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        service.StopAsync(CancellationToken.None).GetAwaiter().GetResult();

        AssertEqual(1, captured.Messages.Count(m => m.Contains("ResourceMonitorDaemon stopping")));
        services.DisposeAsync().AsTask().GetAwaiter().GetResult();   // MqttConnection is IAsyncDisposable only
    }),

    // ---- MqttConnection (in-process loopback broker) --------------------------------------
    ("MqttConnection is non-blocking, uses the fallback, and notifies once per outage", () =>
    {
        MqttConnectionScenario.Run().GetAwaiter().GetResult();
    }),
    ("MqttConnection.DisposeAsync cancels an in-flight connect to unreachable addresses quickly", () =>
    {
        MqttConnectionScenario.DisposeWhileConnecting().GetAwaiter().GetResult();
    }),
    ("MqttConnection notifies an outage even if the handler is attached after the first failure", () =>
    {
        MqttConnectionScenario.LateNotificationHandler().GetAwaiter().GetResult();
    }),
}.Concat(ProvisioningTests.All).Concat(PairingTests.All).Concat(ObservabilityTests.All).ToArray();

var failed = 0;

foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}

Console.WriteLine($"{tests.Length - failed}/{tests.Length} passed");
return failed == 0 ? 0 : 1;

static void AssertEqual<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"expected '{expected}', got '{actual}'");
    }
}

static void AssertTrue(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertThrows<TException>(Action action) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"expected {typeof(TException).Name}");
}

static DeviceSettings LoadEffective(string appSettingsDirectory, string userSettingsPath)
{
    var configuration = new ConfigurationBuilder()
        .AddDaemonSources(appSettingsDirectory, userSettingsPath, reloadOnChange: false)
        .Build();
    return DeviceSettingsLoader.Load(configuration, new List<string>());
}

sealed class CapturingLoggerProvider : ILoggerProvider
{
    public System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(System.Collections.Concurrent.ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            messages.Enqueue(formatter(state, exception));
    }
}

sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "resmon-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
