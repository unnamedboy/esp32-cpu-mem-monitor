using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ResourceMonitorDaemon
{
    /// <summary>
    /// Reads and writes the per-user settings file
    /// (<c>%LocalAppData%\ResourceMonitorDaemon\settings.json</c>).
    /// </summary>
    /// <remarks>
    /// Usage for the settings UI / pairing wizard:
    /// <code>
    /// var settings = optionsMonitor.CurrentValue with { FallbackAddress = "192.168.1.50" };
    /// store.Save(settings);   // validated, written atomically, then the running daemon reloads
    /// </code>
    /// The daemon picks up the change through the configuration file watcher, and
    /// <c>Program</c> additionally forces a configuration reload on <see cref="Saved"/>,
    /// so no restart is needed. Environment variables (<c>RESMON_*</c>) still take precedence
    /// over values saved here.
    /// </remarks>
    public interface ISettingsStore
    {
        /// <summary>Absolute path of the user settings file.</summary>
        string FilePath { get; }

        /// <summary>
        /// Returns the values persisted in the user settings file, with built-in defaults for
        /// missing fields. Does not include <c>appsettings.json</c> or environment overrides.
        /// A missing or unreadable file yields defaults (never throws).
        /// </summary>
        DeviceSettings Load();

        /// <summary>
        /// Validates and atomically writes every <see cref="DeviceSettings"/> value to the user
        /// settings file. Other top-level sections already in the file are preserved.
        /// </summary>
        /// <exception cref="ArgumentException">The settings fail <see cref="DeviceSettingsValidator.Validate"/>.</exception>
        /// <exception cref="IOException">The file could not be written.</exception>
        void Save(DeviceSettings settings);

        /// <summary>Raised after a successful <see cref="Save"/>.</summary>
        event EventHandler? Saved;
    }

    /// <inheritdoc cref="ISettingsStore"/>
    public sealed class UserSettingsStore : ISettingsStore
    {
        private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
        private readonly ILogger? _logger;
        private readonly object _gate = new();

        public UserSettingsStore(string filePath, ILogger? logger = null)
        {
            FilePath = Path.GetFullPath(filePath);
            _logger = logger;
        }

        public string FilePath { get; }

        public event EventHandler? Saved;

        public DeviceSettings Load()
        {
            var warnings = new List<string>();
            DeviceSettings settings;
            lock (_gate)
            {
                if (!File.Exists(FilePath))
                {
                    return new DeviceSettings();
                }

                try
                {
                    using var stream = new MemoryStream(File.ReadAllBytes(FilePath));
                    var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
                    settings = DeviceSettingsLoader.Load(configuration, warnings);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning("Unable to read user settings '{Path}': {Error}. Using defaults.", FilePath, ex.GetBaseException().Message);
                    return new DeviceSettings();
                }
            }

            foreach (var warning in warnings)
            {
                _logger?.LogWarning("User settings: {Warning}", warning);
            }

            return settings;
        }

        public void Save(DeviceSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            var normalized = settings with
            {
                Host = settings.Host?.Trim() ?? string.Empty,
                FallbackAddress = settings.FallbackAddress?.Trim() ?? string.Empty,
            };

            var errors = DeviceSettingsValidator.Validate(normalized);
            if (errors.Count > 0)
            {
                throw new ArgumentException("Invalid settings: " + string.Join(" ", errors), nameof(settings));
            }

            lock (_gate)
            {
                var root = ReadExistingRoot();
                root[DeviceSettings.SectionName] = JsonSerializer.SerializeToNode(normalized, WriteOptions);

                var directory = Path.GetDirectoryName(FilePath)!;
                Directory.CreateDirectory(directory);
                var tempPath = FilePath + ".tmp";
                try
                {
                    File.WriteAllText(tempPath, root.ToJsonString(WriteOptions));
                    ReplaceWithRetry(tempPath, FilePath);
                }
                catch
                {
                    TryDelete(tempPath);
                    throw;
                }
            }

            _logger?.LogInformation("User settings saved to {Path}: {Settings}", FilePath, normalized.Describe());
            Saved?.Invoke(this, EventArgs.Empty);
        }

        private JsonObject ReadExistingRoot()
        {
            try
            {
                if (File.Exists(FilePath) && JsonNode.Parse(File.ReadAllText(FilePath)) is JsonObject existing)
                {
                    return existing;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                _logger?.LogWarning("Existing user settings '{Path}' are unreadable and will be replaced: {Error}", FilePath, ex.Message);
            }

            return new JsonObject();
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception)
            {
                // Best effort; a stale .tmp file is harmless and overwritten by the next save.
            }
        }

        private static void ReplaceWithRetry(string source, string destination)
        {
            // The configuration file watcher may briefly hold the destination open while reloading.
            const int attempts = 5;
            for (var i = 1; ; i++)
            {
                try
                {
                    File.Move(source, destination, overwrite: true);
                    return;
                }
                catch (IOException) when (i < attempts)
                {
                    Thread.Sleep(50);
                }
                catch (UnauthorizedAccessException) when (i < attempts)
                {
                    Thread.Sleep(50);
                }
            }
        }
    }
}
