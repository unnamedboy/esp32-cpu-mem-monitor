namespace ResourceMonitorDaemon
{
    /// <summary>
    /// The <c>Device</c> values explicitly set in the user settings file. A <c>null</c> property
    /// means "not set here": the value then comes from <c>appsettings.json</c> or the built-in
    /// default, so saving a patch never shadows lower configuration layers with copies of their
    /// own values. Contains no secrets (there is no Wi-Fi credential field).
    /// </summary>
    public sealed record UserDeviceSettingsPatch
    {
        /// <summary>Every <see cref="DeviceSettings"/> property a patch can carry.</summary>
        public static IReadOnlyList<string> FieldNames { get; } = new[]
        {
            nameof(DeviceSettings.Host),
            nameof(DeviceSettings.FallbackAddress),
            nameof(DeviceSettings.Port),
            nameof(DeviceSettings.CpuTopic),
            nameof(DeviceSettings.MemTopic),
            nameof(DeviceSettings.SampleIntervalMs),
            nameof(DeviceSettings.ReconnectInitialBackoffMs),
            nameof(DeviceSettings.ReconnectMaxBackoffMs),
            nameof(DeviceSettings.ConnectTimeoutMs),
        };

        /// <summary>Fields the tray settings dialog edits.</summary>
        public static IReadOnlyList<string> DialogFieldNames { get; } = new[]
        {
            nameof(DeviceSettings.Host),
            nameof(DeviceSettings.Port),
            nameof(DeviceSettings.CpuTopic),
            nameof(DeviceSettings.MemTopic),
            nameof(DeviceSettings.SampleIntervalMs),
        };

        public string? Host { get; set; }
        public string? FallbackAddress { get; set; }
        public int? Port { get; set; }
        public string? CpuTopic { get; set; }
        public string? MemTopic { get; set; }
        public int? SampleIntervalMs { get; set; }
        public int? ReconnectInitialBackoffMs { get; set; }
        public int? ReconnectMaxBackoffMs { get; set; }
        public int? ConnectTimeoutMs { get; set; }

        public bool IsEmpty => this == new UserDeviceSettingsPatch();

        /// <summary>Returns <paramref name="lowerLayers"/> with every set field replaced.</summary>
        public DeviceSettings ApplyTo(DeviceSettings lowerLayers) => lowerLayers with
        {
            Host = Host ?? lowerLayers.Host,
            FallbackAddress = FallbackAddress ?? lowerLayers.FallbackAddress,
            Port = Port ?? lowerLayers.Port,
            CpuTopic = CpuTopic ?? lowerLayers.CpuTopic,
            MemTopic = MemTopic ?? lowerLayers.MemTopic,
            SampleIntervalMs = SampleIntervalMs ?? lowerLayers.SampleIntervalMs,
            ReconnectInitialBackoffMs = ReconnectInitialBackoffMs ?? lowerLayers.ReconnectInitialBackoffMs,
            ReconnectMaxBackoffMs = ReconnectMaxBackoffMs ?? lowerLayers.ReconnectMaxBackoffMs,
            ConnectTimeoutMs = ConnectTimeoutMs ?? lowerLayers.ConnectTimeoutMs,
        };

        /// <summary>
        /// Applies the dialog's values: for each <see cref="DialogFieldNames"/> field that is not
        /// overridden by an environment variable, store the edited value if it differs from what
        /// the lower layers (<paramref name="lowerLayers"/> = defaults + appsettings.json)
        /// resolve to, otherwise clear it. Env-overridden fields and fields the dialog does not
        /// show are left exactly as they were.
        /// </summary>
        public void ApplyDialogEdit(DeviceSettings lowerLayers, DeviceSettings edited, IReadOnlySet<string> environmentOverriddenFields)
        {
            bool Editable(string name) => !environmentOverriddenFields.Contains(name);

            if (Editable(nameof(Host)))
            {
                Host = string.Equals(edited.Host, lowerLayers.Host, StringComparison.OrdinalIgnoreCase) ? null : edited.Host;
            }

            if (Editable(nameof(Port)))
            {
                Port = edited.Port == lowerLayers.Port ? null : edited.Port;
            }

            if (Editable(nameof(CpuTopic)))
            {
                CpuTopic = edited.CpuTopic == lowerLayers.CpuTopic ? null : edited.CpuTopic;
            }

            if (Editable(nameof(MemTopic)))
            {
                MemTopic = edited.MemTopic == lowerLayers.MemTopic ? null : edited.MemTopic;
            }

            if (Editable(nameof(SampleIntervalMs)))
            {
                SampleIntervalMs = edited.SampleIntervalMs == lowerLayers.SampleIntervalMs ? null : edited.SampleIntervalMs;
            }
        }

        /// <summary>Validates each set field (and pairs where both members are set).</summary>
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

            if (Host != null)
            {
                Add(DeviceSettingsValidator.ValidateHost(Host));
            }

            if (FallbackAddress != null)
            {
                Add(DeviceSettingsValidator.ValidateFallbackAddress(FallbackAddress));
            }

            if (Port is { } port)
            {
                Add(DeviceSettingsValidator.ValidatePort(port));
            }

            if (CpuTopic != null)
            {
                Add(DeviceSettingsValidator.ValidateTopic(CpuTopic, nameof(CpuTopic)));
            }

            if (MemTopic != null)
            {
                Add(DeviceSettingsValidator.ValidateTopic(MemTopic, nameof(MemTopic)));
            }

            if (CpuTopic != null && CpuTopic == MemTopic)
            {
                errors.Add("CpuTopic and MemTopic must be different.");
            }

            if (SampleIntervalMs is { } interval)
            {
                Add(DeviceSettingsValidator.ValidateRange(interval, DeviceSettingsValidator.MinSampleIntervalMs, DeviceSettingsValidator.MaxSampleIntervalMs, nameof(SampleIntervalMs)));
            }

            if (ReconnectInitialBackoffMs is { } initial)
            {
                Add(DeviceSettingsValidator.ValidateRange(initial, DeviceSettingsValidator.MinBackoffMs, DeviceSettingsValidator.MaxBackoffMs, nameof(ReconnectInitialBackoffMs)));
            }

            if (ReconnectMaxBackoffMs is { } max)
            {
                Add(DeviceSettingsValidator.ValidateRange(max, DeviceSettingsValidator.MinBackoffMs, DeviceSettingsValidator.MaxBackoffMs, nameof(ReconnectMaxBackoffMs)));
            }

            if (ReconnectInitialBackoffMs is { } i && ReconnectMaxBackoffMs is { } m && m < i)
            {
                errors.Add("ReconnectMaxBackoffMs must not be smaller than ReconnectInitialBackoffMs.");
            }

            if (ConnectTimeoutMs is { } timeout)
            {
                Add(DeviceSettingsValidator.ValidateRange(timeout, DeviceSettingsValidator.MinConnectTimeoutMs, DeviceSettingsValidator.MaxConnectTimeoutMs, nameof(ConnectTimeoutMs)));
            }

            return errors;
        }

        /// <summary>Single-line description of the set fields, for logs.</summary>
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

            Add(nameof(Host), Host);
            Add(nameof(FallbackAddress), FallbackAddress);
            Add(nameof(Port), Port);
            Add(nameof(CpuTopic), CpuTopic);
            Add(nameof(MemTopic), MemTopic);
            Add(nameof(SampleIntervalMs), SampleIntervalMs);
            Add(nameof(ReconnectInitialBackoffMs), ReconnectInitialBackoffMs);
            Add(nameof(ReconnectMaxBackoffMs), ReconnectMaxBackoffMs);
            Add(nameof(ConnectTimeoutMs), ConnectTimeoutMs);
            return parts.Count == 0 ? "(nothing set)" : string.Join(", ", parts);
        }
    }
}
