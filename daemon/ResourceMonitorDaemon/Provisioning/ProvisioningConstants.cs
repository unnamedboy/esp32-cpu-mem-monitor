namespace ResourceMonitorDaemon.Provisioning
{
    /// <summary>
    /// Values fixed by the shared pairing contract (PROVISIONING.md) and by
    /// ESP-IDF v4.4.7 wifi_provisioning (the IDF version bundled with Arduino-ESP32 2.0.17).
    /// Keep every protocol constant here instead of scattering literals through the client code.
    /// </summary>
    public static class ProvisioningConstants
    {
        /// <summary>Arduino WiFiProv default BLE provisioning service UUID.</summary>
        public static readonly Guid ServiceUuid = new("021a9004-0382-4aea-bff4-6b3f1c5adfb4");

        /// <summary>Advertised name prefix: PROV_RESMON_ + last 3 bytes of the STA MAC (uppercase hex).</summary>
        public const string DeviceNamePrefix = "PROV_RESMON_";

        // Endpoint names, as published in each characteristic's 0x2901 User Description descriptor.
        public const string SessionEndpoint = "prov-session";
        public const string ConfigEndpoint = "prov-config";
        public const string VersionEndpoint = "proto-ver";
        public const string ScanEndpoint = "prov-scan";

        /// <summary>
        /// Re-provisioning instruction in English, for exception messages and logs (pairing contract:
        /// hold BOOT 5–15 s, then release; holds longer than 15 s are ignored by the firmware). The UI
        /// shows <c>Strings.Pairing_ReprovisionInstruction</c> in the UI language instead.
        /// </summary>
        public const string ReprovisionInstruction = "hold its BOOT button for 5–15 seconds, then release it";

        /// <summary>Plain-text probe esp_prov.py get_version() sends to proto-ver.</summary>
        public const string VersionProbe = "---";

        /// <summary>X25519 public key and shared secret length.</summary>
        public const int PublicKeyLength = 32;

        /// <summary>protocomm Security1 device_random length (the AES-CTR initial counter block).</summary>
        public const int DeviceRandomLength = 16;

        /// <summary>wifi_prov_config_set_data_t.ssid is char[33]: at most 32 bytes.</summary>
        public const int MaxSsidBytes = 32;

        /// <summary>wifi_prov_config_set_data_t.password is char[64]: at most 63 bytes.</summary>
        public const int MaxPassphraseBytes = 63;

        /// <summary>Contract: poll GetStatus at most once per second.</summary>
        public static readonly TimeSpan StatusPollInterval = TimeSpan.FromSeconds(1);

        /// <summary>Contract: give up waiting for the device to join Wi-Fi after 30 s.</summary>
        public static readonly TimeSpan WifiConnectTimeout = TimeSpan.FromSeconds(30);

        // USB serial pairing-code query.
        public const int SerialBaudRate = 115200;
        public const string SerialPopQuery = "RESMON?POP\n";

        /// <summary>Contract: at most 1.5 s per COM port, including open.</summary>
        public static readonly TimeSpan SerialPortProbeBudget = TimeSpan.FromMilliseconds(1500);

        /// <summary>Pairing code: 8 characters, no 0/O/1/I.</summary>
        public const int PopLength = 8;
        public const string PopAlphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    }
}
