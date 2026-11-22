using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ResourceMonitorDaemon
{
    /// <summary>
    /// Reads and writes the per-user settings file
    /// (<c>%LocalAppData%\ResourceMonitorDaemon\settings.json</c>).
    /// </summary>
    /// <remarks>
    /// The file holds only the <c>Device</c> fields the user explicitly set
    /// (<see cref="UserDeviceSettingsPatch"/>); everything else keeps coming from
    /// <c>appsettings.json</c> or the built-in defaults, and <c>RESMON_*</c> environment
    /// variables still win over it. The one pattern to change a value is
    /// <see cref="Update"/>, e.g. for the pairing wizard:
    /// <code>
    /// store.Update(patch => patch.FallbackAddress = "192.168.1.50");
    /// </code>
    /// Set a property to <c>null</c> to remove it from the file. The running daemon picks the
    /// change up through the configuration file watcher (no restart).
    /// </remarks>
    public interface ISettingsStore
    {
        /// <summary>Absolute path of the user settings file.</summary>
        string FilePath { get; }

        /// <summary>
        /// Returns the fields set in the user settings file. A missing or unreadable file, or
        /// an unparsable value, yields an empty/partial patch (never throws).
        /// </summary>
        UserDeviceSettingsPatch Load();

        /// <summary>
        /// Loads the current patch, lets <paramref name="edit"/> change it, validates it and
        /// writes it atomically, all under one lock. Only non-null fields are written; other
        /// top-level sections and unknown <c>Device</c> keys in the file are preserved.
        /// </summary>
        /// <exception cref="ArgumentException">The edited patch fails <see cref="UserDeviceSettingsPatch.Validate"/>.</exception>
        /// <exception cref="IOException">The file could not be written.</exception>
        void Update(Action<UserDeviceSettingsPatch> edit);

        /// <summary>Raised after a successful <see cref="Update"/>.</summary>
        event EventHandler? Saved;
    }

    /// <inheritdoc cref="ISettingsStore"/>
    public sealed class UserSettingsStore : ISettingsStore
    {
        private static readonly JsonSerializerOptions WriteOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private static readonly JsonDocumentOptions ReadOptions = new()
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        private readonly ILogger? _logger;
        private readonly object _gate = new();

        public UserSettingsStore(string filePath, ILogger? logger = null)
        {
            FilePath = Path.GetFullPath(filePath);
            _logger = logger;
        }

        public string FilePath { get; }

        /// <summary>
        /// Path of the most recent backup made by this instance, or <c>null</c>. An unparsable
        /// settings file is copied to <c>settings.json.&lt;yyyyMMdd-HHmmss&gt;.bak</c> before it is
        /// replaced; existing backups are never overwritten.
        /// </summary>
        public string? LastBackupPath { get; private set; }

        public event EventHandler? Saved;

        public UserDeviceSettingsPatch Load()
        {
            lock (_gate)
            {
                return LoadLocked();
            }
        }

        public void Update(Action<UserDeviceSettingsPatch> edit)
        {
            ArgumentNullException.ThrowIfNull(edit);
            UserDeviceSettingsPatch patch;
            lock (_gate)
            {
                patch = LoadLocked();
                edit(patch);
                patch.Host = patch.Host?.Trim();
                patch.FallbackAddress = patch.FallbackAddress?.Trim();

                var errors = patch.Validate();
                if (errors.Count > 0)
                {
                    throw new ArgumentException("Invalid settings: " + string.Join(" ", errors), nameof(edit));
                }

                WriteLocked(patch);
            }

            _logger?.LogInformation("User settings saved to {Path}: {Patch}", FilePath, patch.Describe());
            Saved?.Invoke(this, EventArgs.Empty);
        }

        private UserDeviceSettingsPatch LoadLocked()
        {
            var patch = new UserDeviceSettingsPatch();
            if (!File.Exists(FilePath))
            {
                return patch;
            }

            IConfigurationSection section;
            try
            {
                // Same parser as the live configuration: tolerates comments and trailing commas,
                // keys are case-insensitive.
                using var stream = new MemoryStream(File.ReadAllBytes(FilePath));
                section = new ConfigurationBuilder().AddJsonStream(stream).Build().GetSection(DeviceSettings.SectionName);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("Unable to read user settings '{Path}': {Error}", FilePath, ex.GetBaseException().Message);
                return patch;
            }

            patch.Host = section[nameof(DeviceSettings.Host)];
            patch.FallbackAddress = section[nameof(DeviceSettings.FallbackAddress)];
            patch.CpuTopic = section[nameof(DeviceSettings.CpuTopic)];
            patch.MemTopic = section[nameof(DeviceSettings.MemTopic)];
            patch.Port = ReadInt(section, nameof(DeviceSettings.Port));
            patch.SampleIntervalMs = ReadInt(section, nameof(DeviceSettings.SampleIntervalMs));
            patch.ReconnectInitialBackoffMs = ReadInt(section, nameof(DeviceSettings.ReconnectInitialBackoffMs));
            patch.ReconnectMaxBackoffMs = ReadInt(section, nameof(DeviceSettings.ReconnectMaxBackoffMs));
            patch.ConnectTimeoutMs = ReadInt(section, nameof(DeviceSettings.ConnectTimeoutMs));
            return patch;
        }

        private int? ReadInt(IConfigurationSection section, string key)
        {
            var text = section[key];
            if (text == null)
            {
                return null;
            }

            if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }

            _logger?.LogWarning("User settings: {Key} '{Value}' is not an integer and is ignored.", key, text);
            return null;
        }

        private void WriteLocked(UserDeviceSettingsPatch patch)
        {
            var root = ReadExistingRootLocked();

            // Merge into the existing Device object: drop every known field (any casing), then
            // add the set ones. Unknown keys are kept for forward compatibility.
            var device = root[DeviceSettings.SectionName] as JsonObject ?? new JsonObject();
            foreach (var key in device.Select(property => property.Key).ToList())
            {
                if (UserDeviceSettingsPatch.FieldNames.Contains(key, StringComparer.OrdinalIgnoreCase))
                {
                    device.Remove(key);
                }
            }

            foreach (var property in JsonSerializer.SerializeToNode(patch, WriteOptions)!.AsObject().ToList())
            {
                if (property.Value != null && UserDeviceSettingsPatch.FieldNames.Contains(property.Key))
                {
                    device[property.Key] = property.Value.DeepClone();
                }
            }

            if (device.Count == 0)
            {
                root.Remove(DeviceSettings.SectionName);
            }
            else
            {
                root[DeviceSettings.SectionName] = device;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
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

        private JsonObject ReadExistingRootLocked()
        {
            if (!File.Exists(FilePath))
            {
                return new JsonObject();
            }

            var text = File.ReadAllText(FilePath);
            if (string.IsNullOrWhiteSpace(text))
            {
                return new JsonObject();   // nothing to preserve, no backup needed
            }

            try
            {
                if (JsonNode.Parse(text, nodeOptions: null, documentOptions: ReadOptions) is JsonObject existing)
                {
                    // Detach from the parsed document so it can be re-serialized freely.
                    return existing.DeepClone().AsObject();
                }
            }
            catch (JsonException)
            {
                // Handled below.
            }

            // Never silently discard content we cannot parse: keep a copy next to the file.
            var backup = CreateUniqueBackupPath();
            File.Copy(FilePath, backup, overwrite: false);
            LastBackupPath = backup;
            _logger?.LogWarning("User settings '{Path}' are not a valid JSON object; previous content backed up to {Backup}.", FilePath, backup);
            return new JsonObject();
        }

        private string CreateUniqueBackupPath()
        {
            var stem = $"{FilePath}.{DateTime.Now:yyyyMMdd-HHmmss}";
            var candidate = stem + ".bak";
            for (var i = 1; File.Exists(candidate); i++)
            {
                candidate = $"{stem}-{i}.bak";
            }

            return candidate;
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
