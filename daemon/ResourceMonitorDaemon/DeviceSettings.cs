namespace ResourceMonitorDaemon
{
    /// <summary>
    /// Non-secret daemon connection settings (configuration section <c>Device</c>).
    /// This type intentionally has no Wi-Fi credential fields; passwords must never be
    /// persisted by the daemon.
    /// </summary>
    /// <remarks>
    /// Value equality (record) is used to detect whether a configuration reload actually
    /// changed anything.
    /// </remarks>
    public sealed record DeviceSettings
    {
        public const string SectionName = "Device";

        public const string DefaultHost = "resmon.local";
        public const int DefaultPort = 1883;
        public const string DefaultCpuTopic = "resmon/cpu";
        public const string DefaultMemTopic = "resmon/mem";
        public const string DefaultStatusTopic = "resmon/status";
        public const int DefaultSampleIntervalMs = 1000;
        public const int DefaultReconnectInitialBackoffMs = 1000;
        public const int DefaultReconnectMaxBackoffMs = 30000;
        public const int DefaultConnectTimeoutMs = 3000;

        /// <summary>Broker host name or IP address. Defaults to the ESP32 mDNS name.</summary>
        public string Host { get; set; } = DefaultHost;

        /// <summary>
        /// Optional IPv4 address tried when <see cref="Host"/> cannot be resolved or connected.
        /// Empty means "no fallback". Written by the (future) pairing wizard.
        /// </summary>
        public string FallbackAddress { get; set; } = string.Empty;

        /// <summary>Broker TCP port, 1..65535.</summary>
        public int Port { get; set; } = DefaultPort;

        /// <summary>MQTT topic for the CPU percentage. No wildcards.</summary>
        public string CpuTopic { get; set; } = DefaultCpuTopic;

        /// <summary>MQTT topic for the memory percentage. No wildcards.</summary>
        public string MemTopic { get; set; } = DefaultMemTopic;

        /// <summary>
        /// MQTT topic the daemon subscribes to for the optional device status JSON
        /// (see COMMUNICATION.md). No wildcards; must differ from the metric topics.
        /// </summary>
        public string StatusTopic { get; set; } = DefaultStatusTopic;

        /// <summary>Sampling/publish period in milliseconds.</summary>
        public int SampleIntervalMs { get; set; } = DefaultSampleIntervalMs;

        /// <summary>Delay before the second connect attempt of an outage; doubles per failure.</summary>
        public int ReconnectInitialBackoffMs { get; set; } = DefaultReconnectInitialBackoffMs;

        /// <summary>Upper bound for the reconnect delay.</summary>
        public int ReconnectMaxBackoffMs { get; set; } = DefaultReconnectMaxBackoffMs;

        /// <summary>Timeout for a single connect attempt to one address.</summary>
        public int ConnectTimeoutMs { get; set; } = DefaultConnectTimeoutMs;

        /// <summary>True when the broker address or port differs (requires reconnect).</summary>
        public bool EndpointDiffers(DeviceSettings other) =>
            !string.Equals(Host, other.Host, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(FallbackAddress, other.FallbackAddress, StringComparison.OrdinalIgnoreCase)
            || Port != other.Port;

        /// <summary>Copies every value into <paramref name="target"/> (used by the options pipeline).</summary>
        public void CopyTo(DeviceSettings target)
        {
            target.Host = Host;
            target.FallbackAddress = FallbackAddress;
            target.Port = Port;
            target.CpuTopic = CpuTopic;
            target.MemTopic = MemTopic;
            target.StatusTopic = StatusTopic;
            target.SampleIntervalMs = SampleIntervalMs;
            target.ReconnectInitialBackoffMs = ReconnectInitialBackoffMs;
            target.ReconnectMaxBackoffMs = ReconnectMaxBackoffMs;
            target.ConnectTimeoutMs = ConnectTimeoutMs;
        }

        /// <summary>Single-line description for diagnostics. Contains no secrets.</summary>
        public string Describe() =>
            $"host={Host}, fallback={(string.IsNullOrEmpty(FallbackAddress) ? "(none)" : FallbackAddress)}, " +
            $"port={Port}, cpuTopic={CpuTopic}, memTopic={MemTopic}, statusTopic={StatusTopic}, intervalMs={SampleIntervalMs}, " +
            $"backoffMs={ReconnectInitialBackoffMs}..{ReconnectMaxBackoffMs}, connectTimeoutMs={ConnectTimeoutMs}";
    }
}
