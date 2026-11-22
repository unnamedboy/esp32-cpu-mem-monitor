using Microsoft.Extensions.Configuration;
using ResourceMonitorDaemon;

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
    ("UserSettingsStore round-trips settings atomically", () =>
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "nested", "settings.json");
        var store = new UserSettingsStore(path);
        AssertTrue(store.Load() == new DeviceSettings(), "missing file should yield defaults");

        var saved = 0;
        store.Saved += (_, _) => saved++;
        var settings = new DeviceSettings
        {
            Host = "esp32.lan",
            FallbackAddress = "192.168.1.77",
            Port = 1884,
            CpuTopic = "lab/cpu",
            MemTopic = "lab/mem",
            SampleIntervalMs = 500,
        };
        store.Save(settings);
        AssertEqual(1, saved);
        AssertTrue(store.Load() == settings, "loaded settings differ from saved settings");
        AssertTrue(!File.Exists(path + ".tmp"), "temporary file left behind");
        AssertTrue(!File.ReadAllText(path).Contains("password", StringComparison.OrdinalIgnoreCase), "file mentions a password");

        // Pairing-wizard style partial update keeps the other values.
        store.Save(store.Load() with { FallbackAddress = "192.168.1.78" });
        var reloaded = store.Load();
        AssertEqual("192.168.1.78", reloaded.FallbackAddress);
        AssertEqual("esp32.lan", reloaded.Host);
    }),
    ("UserSettingsStore preserves other sections and rejects invalid settings", () =>
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path, """{ "Logging": { "LogLevel": { "Default": "Debug" } }, "Device": { "Port": 1 } }""");
        var store = new UserSettingsStore(path);
        store.Save(new DeviceSettings { Port = 1885 });
        var text = File.ReadAllText(path);
        AssertTrue(text.Contains("\"Logging\""), "Logging section was dropped");
        AssertEqual(1885, store.Load().Port);

        AssertThrows<ArgumentException>(() => store.Save(new DeviceSettings { CpuTopic = "resmon/#" }));
        AssertEqual(1885, store.Load().Port);
    }),
    ("UserSettingsStore.Load returns defaults for a corrupt file", () =>
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path, "not json at all");
        AssertTrue(new UserSettingsStore(path).Load() == new DeviceSettings(), "expected defaults");
    }),
};

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
