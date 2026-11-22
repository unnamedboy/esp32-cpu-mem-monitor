using System.Collections;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ResourceMonitorDaemon
{
    /// <summary>Well-known file locations used by the daemon.</summary>
    public static class DaemonPaths
    {
        public const string AppFolderName = "ResourceMonitorDaemon";

        /// <summary><c>%LocalAppData%\ResourceMonitorDaemon</c> (writable per-user data).</summary>
        public static string AppDataDirectory { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppFolderName);

        /// <summary>User settings file written by the tray settings dialog and the pairing wizard.</summary>
        public static string UserSettingsPath => Path.Combine(AppDataDirectory, "settings.json");

        /// <summary>Rolling daemon log opened by the tray "View log" entry.</summary>
        public static string LogFilePath => Path.Combine(AppDataDirectory, "daemon.log");

        /// <summary>Read-only defaults shipped next to the executable.</summary>
        public static string AppSettingsPath => Path.Combine(AppContext.BaseDirectory, DaemonConfiguration.AppSettingsFileName);
    }

    /// <summary>
    /// Builds the daemon configuration. Sources, lowest to highest priority:
    /// built-in defaults (<see cref="DeviceSettings"/>) → <c>appsettings.json</c> next to the exe
    /// → user settings file → environment variables prefixed with <c>RESMON_</c>.
    /// </summary>
    public static class DaemonConfiguration
    {
        public const string AppSettingsFileName = "appsettings.json";
        public const string EnvironmentPrefix = "RESMON_";

        /// <summary>
        /// Adds the daemon configuration sources to <paramref name="builder"/>.
        /// Broken JSON files never throw: the error is reported through
        /// <paramref name="onLoadError"/> and that file's values are ignored.
        /// </summary>
        public static IConfigurationBuilder AddDaemonSources(
            this IConfigurationBuilder builder,
            string appSettingsDirectory,
            string userSettingsPath,
            Action<string>? onLoadError = null,
            bool reloadOnChange = true)
        {
            builder.AddJsonFile(source =>
            {
                source.Path = Path.Combine(appSettingsDirectory, AppSettingsFileName);
                ConfigureJsonSource(source, reloadOnChange, onLoadError);
            });

            builder.AddJsonFile(source =>
            {
                source.Path = Path.GetFullPath(userSettingsPath);
                ConfigureJsonSource(source, reloadOnChange, onLoadError);
            });

            builder.AddEnvironmentVariables(EnvironmentPrefix);
            return builder;
        }

        private static void ConfigureJsonSource(JsonConfigurationSource source, bool reloadOnChange, Action<string>? onLoadError)
        {
            source.Optional = true;
            source.ReloadOnChange = reloadOnChange;
            source.OnLoadException = context =>
            {
                context.Ignore = true;
                onLoadError?.Invoke(
                    $"Ignoring configuration file '{source.Path}': {context.Exception.GetBaseException().Message}");
            };

            // Absolute path → PhysicalFileProvider rooted at the nearest existing directory,
            // so the file is watched even if its folder is created later.
            source.ResolveFileProvider();
        }

        /// <summary>Names (not values) of the <c>RESMON_</c> environment variables that are set.</summary>
        public static IReadOnlyList<string> GetEnvironmentOverrideNames()
        {
            var names = new List<string>();
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                if (entry.Key is string name && name.StartsWith(EnvironmentPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    names.Add(name);
                }
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        /// <summary>
        /// Registers <see cref="DeviceSettings"/> for <c>IOptionsMonitor&lt;DeviceSettings&gt;</c>.
        /// Values are parsed leniently and sanitized, so invalid configuration logs a warning
        /// instead of throwing; the options are recomputed whenever configuration reloads.
        /// </summary>
        public static IServiceCollection AddDeviceSettings(this IServiceCollection services)
        {
            services.AddOptions<DeviceSettings>()
                .Configure<IConfiguration, ILoggerFactory>((target, configuration, loggerFactory) =>
                {
                    var warnings = new List<string>();
                    DeviceSettingsLoader.Load(configuration, warnings).CopyTo(target);
                    var logger = loggerFactory.CreateLogger("ResourceMonitorDaemon.Settings");
                    foreach (var warning in warnings)
                    {
                        logger.LogWarning("Invalid setting: {Warning}", warning);
                    }
                });

            services.AddSingleton<IOptionsChangeTokenSource<DeviceSettings>>(provider =>
                new ConfigurationChangeTokenSource<DeviceSettings>(provider.GetRequiredService<IConfiguration>()));
            return services;
        }
    }

    /// <summary>Reads the <c>Device</c> section without ever throwing on malformed values.</summary>
    public static class DeviceSettingsLoader
    {
        public static DeviceSettings Load(IConfiguration configuration, ICollection<string> warnings)
        {
            var section = configuration.GetSection(DeviceSettings.SectionName);
            var defaults = new DeviceSettings();
            var raw = new DeviceSettings
            {
                Host = section[nameof(DeviceSettings.Host)] ?? defaults.Host,
                FallbackAddress = section[nameof(DeviceSettings.FallbackAddress)] ?? defaults.FallbackAddress,
                Port = ReadInt(section, nameof(DeviceSettings.Port), defaults.Port, warnings),
                CpuTopic = section[nameof(DeviceSettings.CpuTopic)] ?? defaults.CpuTopic,
                MemTopic = section[nameof(DeviceSettings.MemTopic)] ?? defaults.MemTopic,
                SampleIntervalMs = ReadInt(section, nameof(DeviceSettings.SampleIntervalMs), defaults.SampleIntervalMs, warnings),
                ReconnectInitialBackoffMs = ReadInt(section, nameof(DeviceSettings.ReconnectInitialBackoffMs), defaults.ReconnectInitialBackoffMs, warnings),
                ReconnectMaxBackoffMs = ReadInt(section, nameof(DeviceSettings.ReconnectMaxBackoffMs), defaults.ReconnectMaxBackoffMs, warnings),
                ConnectTimeoutMs = ReadInt(section, nameof(DeviceSettings.ConnectTimeoutMs), defaults.ConnectTimeoutMs, warnings),
            };

            return DeviceSettingsValidator.Sanitize(raw, warnings);
        }

        private static int ReadInt(IConfiguration section, string key, int defaultValue, ICollection<string> warnings)
        {
            var text = section[key];
            if (text == null)
            {
                return defaultValue;
            }

            if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }

            warnings.Add($"{key} '{text}' is not an integer. Using default {defaultValue}.");
            return defaultValue;
        }
    }

    /// <summary>
    /// Collects messages produced before logging is available (e.g. configuration load errors)
    /// and forwards them once a logger is attached.
    /// </summary>
    public sealed class DeferredWarningLog
    {
        private readonly object _gate = new();
        private readonly List<string> _pending = new();
        private ILogger? _logger;

        public void Report(string message)
        {
            lock (_gate)
            {
                if (_logger == null)
                {
                    _pending.Add(message);
                    return;
                }
            }

            _logger.LogWarning("{Message}", message);
        }

        public void Attach(ILogger logger)
        {
            string[] pending;
            lock (_gate)
            {
                _logger = logger;
                pending = _pending.ToArray();
                _pending.Clear();
            }

            foreach (var message in pending)
            {
                logger.LogWarning("{Message}", message);
            }
        }
    }
}
