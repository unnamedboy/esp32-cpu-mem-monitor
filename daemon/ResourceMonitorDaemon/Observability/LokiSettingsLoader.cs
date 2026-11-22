using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ResourceMonitorDaemon.Localization;

namespace ResourceMonitorDaemon.Observability
{
    /// <summary>
    /// Validation rules for <see cref="LokiSettings"/>. <see cref="Validate"/> is strict (dialog
    /// and <see cref="UserLokiSettingsPatch.Validate"/>); <see cref="Sanitize"/> never fails and
    /// replaces bad values with a warning, so a bad file can never crash the daemon. No message
    /// repeats the URL or the token.
    /// </summary>
    public static class LokiSettingsValidator
    {
        public const int MinMetricsIntervalSeconds = 0;
        public const int MaxMetricsIntervalSeconds = 3600;
        public const int MinBatchMaxEntries = 1;
        public const int MaxBatchMaxEntries = 5000;
        public const int MinQueueCapacity = 100;
        public const int MaxQueueCapacity = 100000;
        public const int MinFlushIntervalMs = 100;
        public const int MaxFlushIntervalMs = 60000;
        public const int MaxUsernameLength = 256;
        public const int MaxTokenLength = 4096;

        /// <summary>Loki/Mimir tenant ids: at most 150 bytes of <c>[A-Za-z0-9!-_.*'()]</c>, not "." or "..".</summary>
        public const int MaxTenantIdLength = 150;

        public static string? ValidateUsername(string? username) => UsernameProblem(username)?.English;

        public static UiMessage? UsernameProblem(string? username) =>
            username == null || username.Length == 0
                ? null
                : username.Length > MaxUsernameLength || username.Any(char.IsControl) || username.Contains(':') || username != username.Trim()
                    ? UiMessage.Observability_ErrorUsername(LokiSettingsFields.Username, MaxUsernameLength)
                    : null;

        /// <summary>The token goes into an HTTP header: printable ASCII without spaces only.</summary>
        public static string? ValidateToken(string? token) => TokenProblem(token)?.English;

        public static UiMessage? TokenProblem(string? token) =>
            token == null || token.Length == 0
                ? null
                : token.Length > MaxTokenLength || token.Any(c => c < '!' || c > '~')
                    ? UiMessage.Observability_ErrorToken(LokiSettingsFields.Token, MaxTokenLength)
                    : null;

        public static string? ValidateTenantId(string? tenantId) => TenantIdProblem(tenantId)?.English;

        public static UiMessage? TenantIdProblem(string? tenantId)
        {
            if (string.IsNullOrEmpty(tenantId))
            {
                return null;
            }

            if (tenantId.Length > MaxTenantIdLength
                || tenantId is "." or ".."
                || tenantId.Any(c => !(char.IsAsciiLetterOrDigit(c) || "!-_.*'()".Contains(c))))
            {
                return UiMessage.Observability_ErrorTenantId(LokiSettingsFields.TenantId, MaxTenantIdLength);
            }

            return null;
        }

        /// <summary>Returns every problem with <paramref name="settings"/> in English; empty when valid.</summary>
        public static IReadOnlyList<string> Validate(LokiSettings settings) =>
            Problems(settings).Select(problem => problem.English).ToList();

        /// <summary>Every problem with <paramref name="settings"/>; the dialog shows <see cref="UiMessage.Localized"/>.</summary>
        public static IReadOnlyList<UiMessage> Problems(LokiSettings settings)
        {
            var errors = new List<UiMessage>();
            void Add(UiMessage? error)
            {
                if (error != null)
                {
                    errors.Add(error);
                }
            }

            if (settings.Enabled || settings.Url.Length > 0)
            {
                Add(LokiUrl.Problem(settings.Url));
            }

            if (!Enum.IsDefined(settings.AuthMode))
            {
                errors.Add(UiMessage.Observability_ErrorAuthMode(LokiSettingsFields.AuthMode));
            }

            Add(UsernameProblem(settings.Username));
            if (settings.AuthMode == LokiAuthMode.Basic && settings.Enabled && settings.Username.Length == 0)
            {
                errors.Add(UiMessage.Observability_ErrorBasicNeedsUsername(LokiSettingsFields.AuthMode, LokiSettingsFields.Username));
            }

            Add(TokenProblem(settings.Token));
            Add(TenantIdProblem(settings.TenantId));
            Add(DeviceSettingsValidator.RangeProblem(settings.MetricsIntervalSeconds, MinMetricsIntervalSeconds, MaxMetricsIntervalSeconds, LokiSettingsFields.MetricsIntervalSeconds));
            Add(DeviceSettingsValidator.RangeProblem(settings.BatchMaxEntries, MinBatchMaxEntries, MaxBatchMaxEntries, LokiSettingsFields.BatchMaxEntries));
            Add(DeviceSettingsValidator.RangeProblem(settings.QueueCapacity, MinQueueCapacity, MaxQueueCapacity, LokiSettingsFields.QueueCapacity));
            Add(DeviceSettingsValidator.RangeProblem(settings.FlushIntervalMs, MinFlushIntervalMs, MaxFlushIntervalMs, LokiSettingsFields.FlushIntervalMs));
            return errors;
        }

        /// <summary>
        /// Returns a copy in which every invalid value is replaced (strings cleared or defaulted,
        /// numbers clamped); one warning per replaced value.
        /// </summary>
        public static LokiSettings Sanitize(LokiSettings input, ICollection<string> warnings)
        {
            var result = input with
            {
                Url = input.Url?.Trim() ?? string.Empty,
                Username = input.Username?.Trim() ?? string.Empty,
                TenantId = input.TenantId?.Trim() ?? string.Empty,
                Token = input.Token?.Trim() ?? string.Empty,
            };

            if (result.Url.Length > 0 && LokiUrl.HasUserInfo(result.Url))
            {
                warnings.Add("Observability:Loki:Url contains a user name or password; it was removed. Use AuthMode/Username and the token instead.");
                result.Url = LokiUrl.StripUserInfo(result.Url);
            }

            if (result.Url.Length > 0 && LokiUrl.Validate(result.Url) is { } urlError)
            {
                warnings.Add($"Observability:Loki: {urlError} Loki stays off.");
                result.Url = string.Empty;
            }

            if (result.Enabled && result.Url.Length == 0)
            {
                warnings.Add("Observability:Loki is enabled but has no valid Url; Loki stays off.");
            }

            if (!Enum.IsDefined(result.AuthMode))
            {
                warnings.Add("Observability:Loki:AuthMode is not None, Basic or Bearer. Using None.");
                result.AuthMode = LokiAuthMode.None;
            }

            if (ValidateUsername(result.Username) is { } usernameError)
            {
                warnings.Add($"Observability:Loki: {usernameError} Username ignored.");
                result.Username = string.Empty;
            }

            if (ValidateToken(result.Token) is { } tokenError)
            {
                warnings.Add($"Observability:Loki: {tokenError} Token ignored.");
                result.Token = string.Empty;
            }

            if (result.Token.Length == 0)
            {
                result.TokenSource = LokiTokenSource.None;
            }

            if (result.Enabled && result.AuthMode != LokiAuthMode.None && result.Token.Length == 0)
            {
                warnings.Add($"Observability:Loki:AuthMode is {result.AuthMode} but no token is configured; requests are sent without credentials.");
            }

            if (result.Enabled && LokiUrl.SendsCredentialsInsecurely(result))
            {
                warnings.Add("Observability:Loki sends credentials over unencrypted http://; use an https:// Url.");
            }

            if (result.Enabled && result.AuthMode == LokiAuthMode.Basic && result.Username.Length == 0)
            {
                warnings.Add("Observability:Loki:AuthMode is Basic but Username is empty.");
            }

            if (ValidateTenantId(result.TenantId) is { } tenantError)
            {
                warnings.Add($"Observability:Loki: {tenantError} No X-Scope-OrgID header is sent.");
                result.TenantId = string.Empty;
            }

            if (!Enum.IsDefined(result.MinLevel))
            {
                warnings.Add("Observability:Loki:MinLevel is not a log level. Using Information.");
                result.MinLevel = LogLevel.Information;
            }

            result.MetricsIntervalSeconds = Clamp(result.MetricsIntervalSeconds, MinMetricsIntervalSeconds, MaxMetricsIntervalSeconds, nameof(LokiSettings.MetricsIntervalSeconds), warnings);
            result.BatchMaxEntries = Clamp(result.BatchMaxEntries, MinBatchMaxEntries, MaxBatchMaxEntries, nameof(LokiSettings.BatchMaxEntries), warnings);
            result.QueueCapacity = Clamp(result.QueueCapacity, MinQueueCapacity, MaxQueueCapacity, nameof(LokiSettings.QueueCapacity), warnings);
            result.FlushIntervalMs = Clamp(result.FlushIntervalMs, MinFlushIntervalMs, MaxFlushIntervalMs, nameof(LokiSettings.FlushIntervalMs), warnings);
            return result;
        }

        private static int Clamp(int value, int min, int max, string name, ICollection<string> warnings)
        {
            var clamped = Math.Clamp(value, min, max);
            if (clamped != value)
            {
                warnings.Add($"Observability:Loki:{name} {value} is outside {min}..{max}; clamped to {clamped}.");
            }

            return clamped;
        }
    }

    /// <summary>
    /// The <see cref="LokiSettings"/> fields as named in messages: configuration key in logs, the
    /// Observability dialog label (<c>Observability_*</c> resource) in the UI.
    /// </summary>
    public static class LokiSettingsFields
    {
        public static readonly UiField Url = new(nameof(LokiSettings.Url), nameof(Strings.Observability_Url));
        public static readonly UiField AuthMode = new(nameof(LokiSettings.AuthMode), nameof(Strings.Observability_AuthMode));
        public static readonly UiField Username = new(nameof(LokiSettings.Username), nameof(Strings.Observability_Username));
        public static readonly UiField Token = new(LokiSettings.TokenKey, nameof(Strings.Observability_Token));
        public static readonly UiField TenantId = new(nameof(LokiSettings.TenantId), nameof(Strings.Observability_Tenant));
        public static readonly UiField MetricsIntervalSeconds = new(nameof(LokiSettings.MetricsIntervalSeconds), nameof(Strings.Observability_MetricsInterval));
        public static readonly UiField BatchMaxEntries = new(nameof(LokiSettings.BatchMaxEntries), nameof(Strings.Observability_BatchMaxEntries));
        public static readonly UiField QueueCapacity = new(nameof(LokiSettings.QueueCapacity), nameof(Strings.Observability_QueueCapacity));
        public static readonly UiField FlushIntervalMs = new(nameof(LokiSettings.FlushIntervalMs), nameof(Strings.Observability_FlushInterval));
    }

    /// <summary>Reads the <c>Observability:Loki</c> section without ever throwing on malformed values.</summary>
    public static class LokiSettingsLoader
    {
        public static LokiSettings Load(IConfiguration configuration, ICollection<string> warnings, ISecretProtector? protector)
        {
            var section = configuration.GetSection(LokiSettings.SectionName);
            var defaults = new LokiSettings();
            var raw = new LokiSettings
            {
                Enabled = ReadBool(section, nameof(LokiSettings.Enabled), defaults.Enabled, warnings),
                Url = section[nameof(LokiSettings.Url)] ?? defaults.Url,
                AuthMode = ReadEnum(section, nameof(LokiSettings.AuthMode), defaults.AuthMode, warnings),
                Username = section[nameof(LokiSettings.Username)] ?? defaults.Username,
                TenantId = section[nameof(LokiSettings.TenantId)] ?? defaults.TenantId,
                IncludeHostLabel = ReadBool(section, nameof(LokiSettings.IncludeHostLabel), defaults.IncludeHostLabel, warnings),
                MetricsIntervalSeconds = ReadInt(section, nameof(LokiSettings.MetricsIntervalSeconds), defaults.MetricsIntervalSeconds, warnings),
                MinLevel = ReadEnum(section, nameof(LokiSettings.MinLevel), defaults.MinLevel, warnings),
                BatchMaxEntries = ReadInt(section, nameof(LokiSettings.BatchMaxEntries), defaults.BatchMaxEntries, warnings),
                QueueCapacity = ReadInt(section, nameof(LokiSettings.QueueCapacity), defaults.QueueCapacity, warnings),
                FlushIntervalMs = ReadInt(section, nameof(LokiSettings.FlushIntervalMs), defaults.FlushIntervalMs, warnings),
                Gzip = ReadBool(section, nameof(LokiSettings.Gzip), defaults.Gzip, warnings),
            };

            (raw.Token, raw.TokenSource) = ResolveToken(configuration, section, warnings, protector);
            return LokiSettingsValidator.Sanitize(raw, warnings);
        }

        /// <summary>
        /// Environment token first (plain, never persisted); then the DPAPI-protected value from
        /// the settings files. A plain <c>Token</c> key in a JSON file is ignored with a warning.
        /// </summary>
        private static (string Token, LokiTokenSource Source) ResolveToken(
            IConfiguration configuration, IConfiguration section, ICollection<string> warnings, ISecretProtector? protector)
        {
            var plainKey = LokiSettings.SectionName + ConfigurationPath.KeyDelimiter + LokiSettings.TokenKey;
            if (!string.IsNullOrWhiteSpace(configuration[plainKey]))
            {
                // Find the provider that supplies the winning value (last provider wins).
                if (configuration is IConfigurationRoot root
                    && root.Providers.Reverse().FirstOrDefault(p => p.TryGet(plainKey, out var v) && !string.IsNullOrWhiteSpace(v)) is FileConfigurationProvider)
                {
                    warnings.Add(
                        $"Observability:Loki:Token in a settings file is ignored: tokens are never read from files in plain text. " +
                        $"Use the Observability dialog (stored DPAPI-encrypted) or the {LokiSettings.TokenEnvironmentVariable} environment variable.");
                }
                else
                {
                    return (configuration[plainKey]!.Trim(), LokiTokenSource.Environment);
                }
            }

            var protectedValue = section[LokiSettings.TokenProtectedKey];
            if (string.IsNullOrWhiteSpace(protectedValue))
            {
                return (string.Empty, LokiTokenSource.None);
            }

            if (protector == null)
            {
                warnings.Add("Observability:Loki:TokenProtected is set but no secret protector is available; token ignored.");
                return (string.Empty, LokiTokenSource.None);
            }

            if (protector.TryUnprotect(protectedValue.Trim(), out var token))
            {
                return (token, LokiTokenSource.Protected);
            }

            warnings.Add("Observability:Loki:TokenProtected could not be decrypted (saved by another Windows user or PC?); re-enter the token in the Observability dialog.");
            return (string.Empty, LokiTokenSource.None);
        }

        internal static bool ReadBool(IConfiguration section, string key, bool defaultValue, ICollection<string> warnings)
        {
            var text = section[key];
            if (text == null)
            {
                return defaultValue;
            }

            if (bool.TryParse(text.Trim(), out var value))
            {
                return value;
            }

            warnings.Add($"Observability:Loki:{key} '{text}' is not true/false. Using default {defaultValue}.");
            return defaultValue;
        }

        internal static int ReadInt(IConfiguration section, string key, int defaultValue, ICollection<string> warnings)
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

            warnings.Add($"Observability:Loki:{key} '{text}' is not an integer. Using default {defaultValue}.");
            return defaultValue;
        }

        internal static TEnum ReadEnum<TEnum>(IConfiguration section, string key, TEnum defaultValue, ICollection<string> warnings)
            where TEnum : struct, Enum
        {
            var text = section[key];
            if (text == null)
            {
                return defaultValue;
            }

            if (TryParseName(text, out TEnum value))
            {
                return value;
            }

            warnings.Add($"Observability:Loki:{key} '{text}' is not one of {string.Join(", ", Enum.GetNames<TEnum>())}. Using default {defaultValue}.");
            return defaultValue;
        }

        /// <summary>Case-insensitive enum name parse; numeric strings and undefined values are rejected.</summary>
        internal static bool TryParseName<TEnum>(string? text, out TEnum value) where TEnum : struct, Enum
        {
            var trimmed = text?.Trim() ?? string.Empty;
            if (trimmed.Length > 0 && !char.IsAsciiDigit(trimmed[0]) && trimmed[0] != '-'
                && Enum.TryParse(trimmed, ignoreCase: true, out value) && Enum.IsDefined(value))
            {
                return true;
            }

            value = default;
            return false;
        }
    }
}
