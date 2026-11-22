using System.Net;
using System.Net.Sockets;

namespace ResourceMonitorDaemon
{
    /// <summary>
    /// Validation rules for <see cref="DeviceSettings"/>.
    /// <see cref="Validate"/> is strict (used by the settings UI; <see cref="UserDeviceSettingsPatch.Validate"/>
    /// applies the same per-field rules before <see cref="UserSettingsStore.Update"/> writes);
    /// <see cref="Sanitize"/> never fails and replaces bad values with defaults or clamped values
    /// (used when loading configuration so a bad file can never crash the daemon).
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

        public static string? ValidateHost(string? host)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                return "Host must not be empty.";
            }

            var trimmed = host.Trim();
            if (trimmed.Length > MaxHostLength || Uri.CheckHostName(trimmed) == UriHostNameType.Unknown)
            {
                return $"Host '{trimmed}' is not a valid host name or IP address.";
            }

            return null;
        }

        public static string? ValidateFallbackAddress(string? address)
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
                return $"FallbackAddress '{trimmed}' is not a dotted IPv4 address.";
            }

            return null;
        }

        public static string? ValidatePort(int port) =>
            port is >= 1 and <= 65535 ? null : $"Port {port} is outside 1..65535.";

        public static string? ValidateTopic(string? topic, string name)
        {
            if (string.IsNullOrWhiteSpace(topic))
            {
                return $"{name} must not be empty.";
            }

            if (topic.Length > MaxTopicLength)
            {
                return $"{name} is longer than {MaxTopicLength} characters.";
            }

            if (topic.IndexOfAny(new[] { '#', '+' }) >= 0)
            {
                return $"{name} '{topic}' must not contain MQTT wildcards '#' or '+'.";
            }

            if (topic.Any(char.IsControl) || topic != topic.Trim())
            {
                return $"{name} must not contain control characters or leading/trailing spaces.";
            }

            return null;
        }

        public static string? ValidateRange(int value, int min, int max, string name) =>
            value >= min && value <= max ? null : $"{name} {value} is outside {min}..{max}.";

        /// <summary>Returns every problem with <paramref name="settings"/>; empty when valid.</summary>
        public static IReadOnlyList<string> Validate(DeviceSettings settings)
        {
            var errors = new List<string>();
            void Add(string? error)
            {
                if (error != null)
                {
                    errors.Add(error);
                }
            }

            Add(ValidateHost(settings.Host));
            Add(ValidateFallbackAddress(settings.FallbackAddress));
            Add(ValidatePort(settings.Port));
            Add(ValidateTopic(settings.CpuTopic, nameof(DeviceSettings.CpuTopic)));
            Add(ValidateTopic(settings.MemTopic, nameof(DeviceSettings.MemTopic)));
            if (settings.CpuTopic == settings.MemTopic)
            {
                errors.Add("CpuTopic and MemTopic must be different.");
            }

            Add(ValidateRange(settings.SampleIntervalMs, MinSampleIntervalMs, MaxSampleIntervalMs, nameof(DeviceSettings.SampleIntervalMs)));
            Add(ValidateRange(settings.ReconnectInitialBackoffMs, MinBackoffMs, MaxBackoffMs, nameof(DeviceSettings.ReconnectInitialBackoffMs)));
            Add(ValidateRange(settings.ReconnectMaxBackoffMs, MinBackoffMs, MaxBackoffMs, nameof(DeviceSettings.ReconnectMaxBackoffMs)));
            if (settings.ReconnectMaxBackoffMs < settings.ReconnectInitialBackoffMs)
            {
                errors.Add("ReconnectMaxBackoffMs must not be smaller than ReconnectInitialBackoffMs.");
            }

            Add(ValidateRange(settings.ConnectTimeoutMs, MinConnectTimeoutMs, MaxConnectTimeoutMs, nameof(DeviceSettings.ConnectTimeoutMs)));
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
}
