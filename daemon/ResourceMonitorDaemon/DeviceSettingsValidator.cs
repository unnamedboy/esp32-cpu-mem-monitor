using System.Net;
using System.Net.Sockets;
using ResourceMonitorDaemon.Localization;

namespace ResourceMonitorDaemon
{
    /// <summary>
    /// Validation rules for <see cref="DeviceSettings"/>.
    /// <see cref="Validate"/> / <see cref="Problems"/> are strict (used by the settings UI; <see cref="UserDeviceSettingsPatch.Validate"/>
    /// applies the same per-field rules before <see cref="UserSettingsStore.Update"/> writes);
    /// <see cref="Sanitize"/> never fails and replaces bad values with defaults or clamped values
    /// (used when loading configuration so a bad file can never crash the daemon). Messages come from
    /// <see cref="Strings"/> as <see cref="UiMessage"/>s: English in logs and exceptions, the UI
    /// language in dialogs.
    /// </summary>
    public static class DeviceSettingsValidator
    {
        public const int MinSampleIntervalMs = 250;
        public const int MaxSampleIntervalMs = 60000;
        public const int MinBackoffMs = 250;
        public const int MaxBackoffMs = 600000;
        public const int MinConnectTimeoutMs = 500;
        public const int MaxConnectTimeoutMs = 30000;
        public const int MaxTopicLength = 128;
        public const int MaxHostLength = 253;

        // Each rule exists twice: a *Problem method returning the message as a UiMessage (English with
        // configuration keys for logs, localized with dialog labels for dialogs) and the original
        // string API returning the English text.

        public static string? ValidateHost(string? host) => HostProblem(host)?.English;

        public static UiMessage? HostProblem(string? host)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                return UiMessage.Settings_ErrorEmpty(DeviceSettingsFields.Host);
            }

            var trimmed = host.Trim();
            if (trimmed.Length > MaxHostLength || Uri.CheckHostName(trimmed) == UriHostNameType.Unknown)
            {
                return UiMessage.Settings_ErrorHostInvalid(DeviceSettingsFields.Host, trimmed);
            }

            return null;
        }

        public static string? ValidateFallbackAddress(string? address) => FallbackAddressProblem(address)?.English;

        public static UiMessage? FallbackAddressProblem(string? address)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                return null;
            }

            var trimmed = address.Trim();
            if (!IPAddress.TryParse(trimmed, out var ip)
                || ip.AddressFamily != AddressFamily.InterNetwork
                || trimmed.Count(c => c == '.') != 3)
            {
                return UiMessage.Settings_ErrorFallbackInvalid(DeviceSettingsFields.FallbackAddress, trimmed);
            }

            return null;
        }

        public static string? ValidatePort(int port) => PortProblem(port)?.English;

        public static UiMessage? PortProblem(int port) => RangeProblem(port, 1, 65535, DeviceSettingsFields.Port);

        public static string? ValidateTopic(string? topic, string name) => TopicProblem(topic, DeviceSettingsFields.ForName(name))?.English;

        public static UiMessage? TopicProblem(string? topic, UiField field)
        {
            if (string.IsNullOrWhiteSpace(topic))
            {
                return UiMessage.Settings_ErrorEmpty(field);
            }

            if (topic.Length > MaxTopicLength)
            {
                return UiMessage.Settings_ErrorTopicTooLong(field, MaxTopicLength);
            }

            if (topic.IndexOfAny(new[] { '#', '+' }) >= 0)
            {
                return UiMessage.Settings_ErrorTopicWildcard(field, topic);
            }

            if (topic.Any(char.IsControl) || topic != topic.Trim())
            {
                return UiMessage.Settings_ErrorTopicWhitespace(field);
            }

            return null;
        }

        public static string? ValidateRange(int value, int min, int max, string name) =>
            RangeProblem(value, min, max, DeviceSettingsFields.ForName(name))?.English;

        public static UiMessage? RangeProblem(int value, int min, int max, UiField field) =>
            value >= min && value <= max ? null : UiMessage.Settings_ErrorRange(field, value, min, max);

        /// <summary>Returns every problem with <paramref name="settings"/> in English; empty when valid.</summary>
        public static IReadOnlyList<string> Validate(DeviceSettings settings) =>
            Problems(settings).Select(problem => problem.English).ToList();

        /// <summary>Every problem with <paramref name="settings"/>; the settings dialog shows <see cref="UiMessage.Localized"/>.</summary>
        public static IReadOnlyList<UiMessage> Problems(DeviceSettings settings)
        {
            var errors = new List<UiMessage>();
            void Add(UiMessage? error)
            {
                if (error != null)
                {
                    errors.Add(error);
                }
            }

            Add(HostProblem(settings.Host));
            Add(FallbackAddressProblem(settings.FallbackAddress));
            Add(PortProblem(settings.Port));
            Add(TopicProblem(settings.CpuTopic, DeviceSettingsFields.CpuTopic));
            Add(TopicProblem(settings.MemTopic, DeviceSettingsFields.MemTopic));
            if (settings.CpuTopic == settings.MemTopic)
            {
                errors.Add(UiMessage.Settings_ErrorTopicsEqual(DeviceSettingsFields.CpuTopic, DeviceSettingsFields.MemTopic));
            }

            Add(TopicProblem(settings.StatusTopic, DeviceSettingsFields.StatusTopic));
            if (settings.StatusTopic == settings.CpuTopic || settings.StatusTopic == settings.MemTopic)
            {
                errors.Add(DeviceSettingsFields.StatusTopicCollides);
            }

            Add(RangeProblem(settings.SampleIntervalMs, MinSampleIntervalMs, MaxSampleIntervalMs, DeviceSettingsFields.SampleIntervalMs));
            Add(RangeProblem(settings.ReconnectInitialBackoffMs, MinBackoffMs, MaxBackoffMs, DeviceSettingsFields.ReconnectInitialBackoffMs));
            Add(RangeProblem(settings.ReconnectMaxBackoffMs, MinBackoffMs, MaxBackoffMs, DeviceSettingsFields.ReconnectMaxBackoffMs));
            if (settings.ReconnectMaxBackoffMs < settings.ReconnectInitialBackoffMs)
            {
                errors.Add(DeviceSettingsFields.BackoffOrder);
            }

            Add(RangeProblem(settings.ConnectTimeoutMs, MinConnectTimeoutMs, MaxConnectTimeoutMs, DeviceSettingsFields.ConnectTimeoutMs));
            return errors;
        }

        /// <summary>
        /// Returns a copy in which every invalid value is replaced: strings fall back to their
        /// defaults, numeric ranges are clamped. One warning is added per replaced value.
        /// </summary>
        public static DeviceSettings Sanitize(DeviceSettings input, ICollection<string> warnings)
        {
            var result = input with
            {
                Host = input.Host?.Trim() ?? string.Empty,
                FallbackAddress = input.FallbackAddress?.Trim() ?? string.Empty,
            };

            if (ValidateHost(result.Host) is { } hostError)
            {
                warnings.Add($"{hostError} Using default '{DeviceSettings.DefaultHost}'.");
                result.Host = DeviceSettings.DefaultHost;
            }

            if (ValidateFallbackAddress(result.FallbackAddress) is { } fallbackError)
            {
                warnings.Add($"{fallbackError} Fallback disabled.");
                result.FallbackAddress = string.Empty;
            }

            if (ValidatePort(result.Port) is { } portError)
            {
                warnings.Add($"{portError} Using default {DeviceSettings.DefaultPort}.");
                result.Port = DeviceSettings.DefaultPort;
            }

            if (ValidateTopic(result.CpuTopic, nameof(DeviceSettings.CpuTopic)) is { } cpuError)
            {
                warnings.Add($"{cpuError} Using default '{DeviceSettings.DefaultCpuTopic}'.");
                result.CpuTopic = DeviceSettings.DefaultCpuTopic;
            }

            if (ValidateTopic(result.MemTopic, nameof(DeviceSettings.MemTopic)) is { } memError)
            {
                warnings.Add($"{memError} Using default '{DeviceSettings.DefaultMemTopic}'.");
                result.MemTopic = DeviceSettings.DefaultMemTopic;
            }

            if (result.CpuTopic == result.MemTopic)
            {
                warnings.Add("CpuTopic and MemTopic are identical. Using default topics.");
                result.CpuTopic = DeviceSettings.DefaultCpuTopic;
                result.MemTopic = DeviceSettings.DefaultMemTopic;
            }

            if (ValidateTopic(result.StatusTopic, nameof(DeviceSettings.StatusTopic)) is { } statusError)
            {
                warnings.Add($"{statusError} Using default '{DeviceSettings.DefaultStatusTopic}'.");
                result.StatusTopic = DeviceSettings.DefaultStatusTopic;
            }

            if (result.StatusTopic == result.CpuTopic || result.StatusTopic == result.MemTopic)
            {
                warnings.Add($"StatusTopic '{result.StatusTopic}' collides with a metric topic. Using default '{DeviceSettings.DefaultStatusTopic}'.");
                result.StatusTopic = DeviceSettings.DefaultStatusTopic;
            }

            result.SampleIntervalMs = Clamp(result.SampleIntervalMs, MinSampleIntervalMs, MaxSampleIntervalMs, nameof(DeviceSettings.SampleIntervalMs), warnings);
            result.ReconnectInitialBackoffMs = Clamp(result.ReconnectInitialBackoffMs, MinBackoffMs, MaxBackoffMs, nameof(DeviceSettings.ReconnectInitialBackoffMs), warnings);
            result.ReconnectMaxBackoffMs = Clamp(result.ReconnectMaxBackoffMs, MinBackoffMs, MaxBackoffMs, nameof(DeviceSettings.ReconnectMaxBackoffMs), warnings);
            if (result.ReconnectMaxBackoffMs < result.ReconnectInitialBackoffMs)
            {
                warnings.Add($"ReconnectMaxBackoffMs {result.ReconnectMaxBackoffMs} is smaller than ReconnectInitialBackoffMs; using {result.ReconnectInitialBackoffMs}.");
                result.ReconnectMaxBackoffMs = result.ReconnectInitialBackoffMs;
            }

            result.ConnectTimeoutMs = Clamp(result.ConnectTimeoutMs, MinConnectTimeoutMs, MaxConnectTimeoutMs, nameof(DeviceSettings.ConnectTimeoutMs), warnings);
            return result;
        }

        private static int Clamp(int value, int min, int max, string name, ICollection<string> warnings)
        {
            var clamped = Math.Clamp(value, min, max);
            if (clamped != value)
            {
                warnings.Add($"{name} {value} is outside {min}..{max}; clamped to {clamped}.");
            }

            return clamped;
        }
    }

    /// <summary>
    /// The <see cref="DeviceSettings"/> fields as named in messages: configuration key in logs,
    /// dialog label (<c>Settings_*</c> resource) in the UI.
    /// </summary>
    public static class DeviceSettingsFields
    {
        public static readonly UiField Host = new(nameof(DeviceSettings.Host), nameof(Strings.Settings_Host));
        public static readonly UiField FallbackAddress = new(nameof(DeviceSettings.FallbackAddress), nameof(Strings.Settings_FallbackAddress));
        public static readonly UiField Port = new(nameof(DeviceSettings.Port), nameof(Strings.Settings_Port));
        public static readonly UiField CpuTopic = new(nameof(DeviceSettings.CpuTopic), nameof(Strings.Settings_CpuTopic));
        public static readonly UiField MemTopic = new(nameof(DeviceSettings.MemTopic), nameof(Strings.Settings_MemTopic));
        public static readonly UiField StatusTopic = new(nameof(DeviceSettings.StatusTopic), nameof(Strings.Settings_StatusTopic));
        public static readonly UiField SampleIntervalMs = new(nameof(DeviceSettings.SampleIntervalMs), nameof(Strings.Settings_SampleInterval));
        public static readonly UiField ReconnectInitialBackoffMs = new(nameof(DeviceSettings.ReconnectInitialBackoffMs), nameof(Strings.Settings_ReconnectInitialBackoff));
        public static readonly UiField ReconnectMaxBackoffMs = new(nameof(DeviceSettings.ReconnectMaxBackoffMs), nameof(Strings.Settings_ReconnectMaxBackoff));
        public static readonly UiField ConnectTimeoutMs = new(nameof(DeviceSettings.ConnectTimeoutMs), nameof(Strings.Settings_ConnectTimeout));

        private static readonly UiField[] All =
        {
            Host, FallbackAddress, Port, CpuTopic, MemTopic, StatusTopic, SampleIntervalMs, ReconnectInitialBackoffMs, ReconnectMaxBackoffMs, ConnectTimeoutMs,
        };

        /// <summary>Topic collision: StatusTopic vs. the two metric topics.</summary>
        public static UiMessage StatusTopicCollides => UiMessage.Settings_ErrorStatusTopicCollides(StatusTopic, CpuTopic, MemTopic);

        /// <summary>ReconnectMaxBackoffMs smaller than ReconnectInitialBackoffMs.</summary>
        public static UiMessage BackoffOrder => UiMessage.Settings_ErrorBackoffOrder(ReconnectMaxBackoffMs, ReconnectInitialBackoffMs);

        /// <summary>The field with configuration key <paramref name="name"/>, or one that shows <paramref name="name"/> as is.</summary>
        public static UiField ForName(string name) =>
            All.FirstOrDefault(field => string.Equals(field.Identifier, name, StringComparison.OrdinalIgnoreCase)) ?? new UiField(name, null);
    }
}
