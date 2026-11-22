using Microsoft.Extensions.Logging;

namespace ResourceMonitorDaemon.Observability
{
    /// <summary>
    /// The <c>Observability:Loki</c> values explicitly set in the user settings file. A <c>null</c>
    /// property means "not set here" (the value comes from <c>appsettings.json</c> or the default),
    /// exactly like <see cref="UserDeviceSettingsPatch"/>. There is deliberately no plain token
    /// property: only the DPAPI ciphertext (<see cref="TokenProtected"/>) can be persisted.
    /// </summary>
    public sealed record UserLokiSettingsPatch
    {
        /// <summary>Every key a patch can carry (also the keys the store owns in the file).</summary>
        public static IReadOnlyList<string> FieldNames { get; } = new[]
        {
            nameof(Enabled),
            nameof(Url),
            nameof(AuthMode),
            nameof(Username),
            nameof(TokenProtected),
            nameof(TenantId),
            nameof(IncludeHostLabel),
            nameof(MetricsIntervalSeconds),
            nameof(MinLevel),
            nameof(BatchMaxEntries),
            nameof(QueueCapacity),
            nameof(FlushIntervalMs),
            nameof(Gzip),
        };

        /// <summary>Fields the Observability dialog edits (the token is handled separately).</summary>
        public static IReadOnlyList<string> DialogFieldNames { get; } = new[]
        {
            nameof(Enabled),
            nameof(Url),
            nameof(AuthMode),
            nameof(Username),
            nameof(TenantId),
            nameof(IncludeHostLabel),
            nameof(MetricsIntervalSeconds),
        };

        public bool? Enabled { get; set; }
        public string? Url { get; set; }
        public LokiAuthMode? AuthMode { get; set; }
        public string? Username { get; set; }

        /// <summary>Base64 DPAPI (CurrentUser) ciphertext of the token; see <see cref="ISecretProtector"/>.</summary>
        public string? TokenProtected { get; set; }

        public string? TenantId { get; set; }
        public bool? IncludeHostLabel { get; set; }
        public int? MetricsIntervalSeconds { get; set; }
        public LogLevel? MinLevel { get; set; }
        public int? BatchMaxEntries { get; set; }
        public int? QueueCapacity { get; set; }
        public int? FlushIntervalMs { get; set; }
        public bool? Gzip { get; set; }

        public bool IsEmpty => this == new UserLokiSettingsPatch();

        /// <summary>Encrypts <paramref name="token"/> and stores the ciphertext; the plain value is not kept.</summary>
        public void SetToken(string token, ISecretProtector protector)
        {
            ArgumentNullException.ThrowIfNull(protector);
            var trimmed = token?.Trim() ?? string.Empty;
            if (LokiSettingsValidator.ValidateToken(trimmed) is { } error)
            {
                throw new ArgumentException(error, nameof(token));
            }

            TokenProtected = trimmed.Length == 0 ? null : protector.Protect(trimmed);
        }

        /// <summary>
        /// Applies the dialog's values with the same rules as
        /// <see cref="UserDeviceSettingsPatch.ApplyDialogEdit"/>: only <see cref="DialogFieldNames"/>
        /// fields that the user changed (edited ≠ displayed) and that are not overridden by an
        /// environment variable are touched; such a field is stored if it differs from the lower
        /// layers (defaults + appsettings.json), otherwise cleared.
        /// </summary>
        public void ApplyDialogEdit(LokiSettings lowerLayers, LokiSettings displayed, LokiSettings edited, IReadOnlySet<string> environmentOverriddenFields)
        {
            bool Changed(string name, bool equalToDisplayed) => !equalToDisplayed && !environmentOverriddenFields.Contains(name);

            if (Changed(nameof(Enabled), edited.Enabled == displayed.Enabled))
            {
                Enabled = edited.Enabled == lowerLayers.Enabled ? null : edited.Enabled;
            }

            if (Changed(nameof(Url), edited.Url == displayed.Url))
            {
                Url = edited.Url == lowerLayers.Url ? null : edited.Url;
            }

            if (Changed(nameof(AuthMode), edited.AuthMode == displayed.AuthMode))
            {
                AuthMode = edited.AuthMode == lowerLayers.AuthMode ? null : edited.AuthMode;
            }

            if (Changed(nameof(Username), edited.Username == displayed.Username))
            {
                Username = edited.Username == lowerLayers.Username ? null : edited.Username;
            }

            if (Changed(nameof(TenantId), edited.TenantId == displayed.TenantId))
            {
                TenantId = edited.TenantId == lowerLayers.TenantId ? null : edited.TenantId;
            }

            if (Changed(nameof(IncludeHostLabel), edited.IncludeHostLabel == displayed.IncludeHostLabel))
            {
                IncludeHostLabel = edited.IncludeHostLabel == lowerLayers.IncludeHostLabel ? null : edited.IncludeHostLabel;
            }

            if (Changed(nameof(MetricsIntervalSeconds), edited.MetricsIntervalSeconds == displayed.MetricsIntervalSeconds))
            {
                MetricsIntervalSeconds = edited.MetricsIntervalSeconds == lowerLayers.MetricsIntervalSeconds ? null : edited.MetricsIntervalSeconds;
            }
        }

        /// <summary>Validates each set field.</summary>
        public IReadOnlyList<string> Validate()
        {
            var errors = new List<string>();
            void Add(string? error)
            {
                if (error != null)
                {
                    errors.Add(error);
                }
            }

            if (Url != null && Url.Length > 0)
            {
                Add(LokiUrl.Validate(Url));
            }

            if (AuthMode is { } mode && !Enum.IsDefined(mode))
            {
                errors.Add("AuthMode must be None, Basic or Bearer.");
            }

            Add(LokiSettingsValidator.ValidateUsername(Username));
            Add(LokiSettingsValidator.ValidateTenantId(TenantId));
            if (TokenProtected != null && !IsBase64(TokenProtected))
            {
                errors.Add("TokenProtected must be base64 ciphertext.");
            }

            if (MinLevel is { } level && !Enum.IsDefined(level))
            {
                errors.Add("MinLevel must be a log level.");
            }

            void Range(int? value, int min, int max, string name)
            {
                if (value is { } v)
                {
                    Add(DeviceSettingsValidator.ValidateRange(v, min, max, name));
                }
            }

            Range(MetricsIntervalSeconds, LokiSettingsValidator.MinMetricsIntervalSeconds, LokiSettingsValidator.MaxMetricsIntervalSeconds, nameof(MetricsIntervalSeconds));
            Range(BatchMaxEntries, LokiSettingsValidator.MinBatchMaxEntries, LokiSettingsValidator.MaxBatchMaxEntries, nameof(BatchMaxEntries));
            Range(QueueCapacity, LokiSettingsValidator.MinQueueCapacity, LokiSettingsValidator.MaxQueueCapacity, nameof(QueueCapacity));
            Range(FlushIntervalMs, LokiSettingsValidator.MinFlushIntervalMs, LokiSettingsValidator.MaxFlushIntervalMs, nameof(FlushIntervalMs));
            return errors;
        }

        /// <summary>Single-line description of the set fields for logs: URL host only, token as "(set)".</summary>
        public string Describe()
        {
            var parts = new List<string>();
            void Add(string name, object? value)
            {
                if (value != null)
                {
                    parts.Add($"{name}={value}");
                }
            }

            Add(nameof(Enabled), Enabled);
            Add("UrlHost", Url == null ? null : LokiUrl.DescribeHost(Url));
            Add(nameof(AuthMode), AuthMode);
            Add(nameof(Username), Username == null ? null : "(set)");
            Add(nameof(TokenProtected), TokenProtected == null ? null : "(set)");
            Add(nameof(TenantId), TenantId == null ? null : "(set)");
            Add(nameof(IncludeHostLabel), IncludeHostLabel);
            Add(nameof(MetricsIntervalSeconds), MetricsIntervalSeconds);
            Add(nameof(MinLevel), MinLevel);
            Add(nameof(BatchMaxEntries), BatchMaxEntries);
            Add(nameof(QueueCapacity), QueueCapacity);
            Add(nameof(FlushIntervalMs), FlushIntervalMs);
            Add(nameof(Gzip), Gzip);
            return parts.Count == 0 ? "(nothing set)" : string.Join(", ", parts);
        }

        /// <summary>Never prints the ciphertext or the URL path (overrides the generated member dump).</summary>
        public override string ToString() => $"UserLokiSettingsPatch {{ {Describe()} }}";

        private static bool IsBase64(string value)
        {
            var buffer = new byte[value.Length];
            return value.Length > 0 && Convert.TryFromBase64String(value, buffer, out _);
        }
    }
}
