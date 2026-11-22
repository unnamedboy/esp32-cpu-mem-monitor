using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ResourceMonitorDaemon.Observability;

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

        /// <summary>Raised after a successful <see cref="Update"/> (or Loki section update).</summary>
        event EventHandler? Saved;
    }

    /// <summary>
    /// The <c>Observability:Loki</c> section of the same user settings file, kept separate from
    /// <see cref="ISettingsStore"/> so that interface stays unchanged. Same rules: only set fields
    /// are written, other sections and unknown keys are preserved, writes are atomic, and the
    /// running daemon picks changes up through the configuration file watcher.
    /// <code>
    /// store.UpdateLoki(patch => { patch.Enabled = true; patch.SetToken(token, protector); });
    /// </code>
    /// </summary>
    public interface IObservabilitySettingsStore
    {
        /// <summary>Absolute path of the user settings file.</summary>
        string FilePath { get; }

        /// <summary>The Loki fields set in the user file only; never throws.</summary>
        UserLokiSettingsPatch LoadLoki();

        /// <summary>Loads, edits, validates and atomically writes the Loki section under the store lock.</summary>
        /// <exception cref="ArgumentException">The edited patch fails <see cref="UserLokiSettingsPatch.Validate"/>.</exception>
        /// <exception cref="IOException">The file could not be written.</exception>
        void UpdateLoki(Action<UserLokiSettingsPatch> edit);
    }

    /// <inheritdoc cref="ISettingsStore"/>
    public sealed class UserSettingsStore : ISettingsStore, IObservabilitySettingsStore
    {
        private static readonly JsonSerializerOptions WriteOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private static readonly JsonSerializerOptions LokiWriteOptions = new(WriteOptions)
        {
            Converters = { new JsonStringEnumConverter() },
        };

        private static readonly string[] DeviceSectionPath = { DeviceSettings.SectionName };
        private static readonly string[] LokiSectionPath = LokiSettings.SectionName.Split(ConfigurationPath.KeyDelimiter);

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

        public UserLokiSettingsPatch LoadLoki()
        {
            lock (_gate)
            {
                return LoadLokiLocked();
            }
        }

        public void UpdateLoki(Action<UserLokiSettingsPatch> edit)
        {
            ArgumentNullException.ThrowIfNull(edit);
            UserLokiSettingsPatch patch;
            lock (_gate)
            {
                patch = LoadLokiLocked();
                edit(patch);
                patch.Url = patch.Url?.Trim();
                patch.Username = patch.Username?.Trim();
                patch.TenantId = patch.TenantId?.Trim();

                var errors = patch.Validate();
                if (errors.Count > 0)
                {
                    throw new ArgumentException("Invalid observability settings: " + string.Join(" ", errors), nameof(edit));
                }

                WriteSectionLocked(LokiSectionPath, UserLokiSettingsPatch.FieldNames, JsonSerializer.SerializeToNode(patch, LokiWriteOptions)!.AsObject());
            }

            // Describe() shows the URL host only and never the token ciphertext.
            _logger?.LogInformation("Observability settings saved to {Path}: {Patch}", FilePath, patch.Describe());
            Saved?.Invoke(this, EventArgs.Empty);
        }

        private UserLokiSettingsPatch LoadLokiLocked()
        {
            var patch = new UserLokiSettingsPatch();
            if (ReadSectionLocked(LokiSettings.SectionName) is not { } section)
            {
                return patch;
            }

            var ignored = new List<string>();
            patch.Enabled = ReadBool(section, nameof(LokiSettings.Enabled), ignored);
            patch.Url = section[nameof(LokiSettings.Url)];
            patch.AuthMode = LokiSettingsLoader.TryParseName(section[nameof(LokiSettings.AuthMode)], out LokiAuthMode mode) ? mode : null;
            patch.Username = section[nameof(LokiSettings.Username)];
            patch.TokenProtected = section[LokiSettings.TokenProtectedKey];
            patch.TenantId = section[nameof(LokiSettings.TenantId)];
            patch.IncludeHostLabel = ReadBool(section, nameof(LokiSettings.IncludeHostLabel), ignored);
            patch.MetricsIntervalSeconds = ReadInt(section, nameof(LokiSettings.MetricsIntervalSeconds));
            patch.MinLevel = LokiSettingsLoader.TryParseName(section[nameof(LokiSettings.MinLevel)], out LogLevel level) ? level : null;
            patch.BatchMaxEntries = ReadInt(section, nameof(LokiSettings.BatchMaxEntries));
            patch.QueueCapacity = ReadInt(section, nameof(LokiSettings.QueueCapacity));
            patch.FlushIntervalMs = ReadInt(section, nameof(LokiSettings.FlushIntervalMs));
            patch.Gzip = ReadBool(section, nameof(LokiSettings.Gzip), ignored);
            foreach (var warning in ignored)
            {
                _logger?.LogWarning("User settings: {Warning}", warning);
            }

            return patch;
        }

        private static bool? ReadBool(IConfigurationSection section, string key, ICollection<string> warnings)
        {
            var text = section[key];
            if (text == null)
            {
                return null;
            }

            if (bool.TryParse(text.Trim(), out var value))
            {
                return value;
            }

            warnings.Add($"Observability:Loki:{key} '{text}' is not true/false and is ignored.");
            return null;
        }

        /// <summary>The configuration section at <paramref name="path"/> of the user file, or null if missing or unreadable.</summary>
        private IConfigurationSection? ReadSectionLocked(string path)
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            try
            {
                // Same parser as the live configuration: tolerates comments and trailing commas,
                // keys are case-insensitive.
                using var stream = new MemoryStream(File.ReadAllBytes(FilePath));
                return new ConfigurationBuilder().AddJsonStream(stream).Build().GetSection(path);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("Unable to read user settings '{Path}': {Error}", FilePath, ex.GetBaseException().Message);
                return null;
            }
        }

        private UserDeviceSettingsPatch LoadLocked()
        {
            var patch = new UserDeviceSettingsPatch();
            if (ReadSectionLocked(DeviceSettings.SectionName) is not { } section)
            {
                return patch;
            }

            patch.Host = section[nameof(DeviceSettings.Host)];
            patch.FallbackAddress = section[nameof(DeviceSettings.FallbackAddress)];
            patch.CpuTopic = section[nameof(DeviceSettings.CpuTopic)];
            patch.MemTopic = section[nameof(DeviceSettings.MemTopic)];
            patch.StatusTopic = section[nameof(DeviceSettings.StatusTopic)];
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

        private void WriteLocked(UserDeviceSettingsPatch patch) =>
            WriteSectionLocked(DeviceSectionPath, UserDeviceSettingsPatch.FieldNames, JsonSerializer.SerializeToNode(patch, WriteOptions)!.AsObject());

        private void WriteSectionLocked(IReadOnlyList<string> sectionPath, IReadOnlyList<string> fieldNames, JsonObject values)
        {
            var root = ReadExistingRootLocked();

            // Walk (or create) the nested section objects, reusing an existing key in any casing.
            var chain = new List<(JsonObject Owner, string Key, JsonObject Node)>();
            var current = root;
            foreach (var segment in sectionPath)
            {
                var key = current.Select(property => property.Key).FirstOrDefault(k => string.Equals(k, segment, StringComparison.OrdinalIgnoreCase)) ?? segment;
                var child = current[key] as JsonObject ?? new JsonObject();
                chain.Add((current, key, child));
                current = child;
            }

            // Merge into the existing section object: drop every known field (any casing), then
            // add the set ones. Unknown keys are kept for forward compatibility.
            var section = current;
            foreach (var key in section.Select(property => property.Key).ToList())
            {
                if (fieldNames.Contains(key, StringComparer.OrdinalIgnoreCase))
                {
                    section.Remove(key);
                }
            }

            foreach (var property in values.ToList())
            {
                if (property.Value != null && fieldNames.Contains(property.Key))
                {
                    section[property.Key] = property.Value.DeepClone();
                }
            }

            // Attach non-empty section objects and remove empty ones, innermost first.
            for (var i = chain.Count - 1; i >= 0; i--)
            {
                var (owner, key, node) = chain[i];
                if (node.Count == 0)
                {
                    owner.Remove(key);
                }
                else if (!ReferenceEquals(owner[key], node))
                {
                    owner[key] = node;
                }
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
