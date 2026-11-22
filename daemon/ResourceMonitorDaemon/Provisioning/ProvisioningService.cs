using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ResourceMonitorDaemon.Provisioning
{
    /// <summary>High-level steps reported while provisioning a device.</summary>
    public enum ProvisioningStep
    {
        ConnectingToDevice,
        SecuringSession,
        SendingWifiConfig,
        WaitingForWifiConnection,
    }

    /// <summary>Parsed proto-ver answer, e.g. {"prov":{"ver":"v1.1","cap":["wifi_scan"]}} (wifi_provisioning manager.c).</summary>
    public sealed record ProtocolVersionInfo(string? Version, IReadOnlyList<string> Capabilities)
    {
        public static ProtocolVersionInfo Parse(byte[] response)
        {
            var text = Encoding.UTF8.GetString(response).Trim('\0', ' ', '\r', '\n', '\t');
            try
            {
                using var json = JsonDocument.Parse(text);
                if (json.RootElement.ValueKind == JsonValueKind.Object
                    && json.RootElement.TryGetProperty("prov", out var prov)
                    && prov.ValueKind == JsonValueKind.Object)
                {
                    var version = prov.TryGetProperty("ver", out var ver) && ver.ValueKind == JsonValueKind.String ? ver.GetString() : null;
                    var capabilities = prov.TryGetProperty("cap", out var cap) && cap.ValueKind == JsonValueKind.Array
                        ? cap.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()!).ToArray()
                        : Array.Empty<string>();
                    return new ProtocolVersionInfo(version, capabilities);
                }
            }
            catch (JsonException)
            {
                // esp_prov also accepts a plain version string.
            }

            return new ProtocolVersionInfo(text.Length == 0 ? null : text[..Math.Min(text.Length, 64)], Array.Empty<string>());
        }
    }

    /// <summary>
    /// Facade for the setup wizard: discover devices, read the pairing code over USB, and provision
    /// Wi-Fi over BLE (protocomm Security1 + PoP). Wi-Fi passwords and pairing codes are never logged
    /// or stored; callers should drop their own references as soon as <see cref="ProvisionAsync"/> returns.
    /// </summary>
    public sealed class ProvisioningService
    {
        public static readonly TimeSpan DefaultScanDuration = TimeSpan.FromSeconds(5);

        private readonly ILogger _logger;
        private readonly Func<ProvisioningDeviceInfo, CancellationToken, Task<IProvisioningTransport>> _transportFactory;
        private readonly SerialPopReader _popReader;
        private readonly BleDeviceScanner _scanner = new();
        private readonly TimeSpan _pollInterval;
        private readonly TimeSpan _connectTimeout;

        public ProvisioningService(ILogger<ProvisioningService>? logger = null)
            : this(
                logger,
                async (device, ct) => await BleProvisioningTransport.ConnectAsync(device.BluetoothAddress, ct).ConfigureAwait(false),
                ProvisioningConstants.StatusPollInterval,
                ProvisioningConstants.WifiConnectTimeout)
        {
        }

        /// <summary>Test hook: inject a transport (e.g. the in-memory fake device) and fast polling.</summary>
        internal ProvisioningService(
            ILogger? logger,
            Func<ProvisioningDeviceInfo, CancellationToken, Task<IProvisioningTransport>> transportFactory,
            TimeSpan pollInterval,
            TimeSpan connectTimeout)
        {
            _logger = logger ?? NullLogger.Instance;
            _transportFactory = transportFactory;
            _popReader = new SerialPopReader(_logger);
            _pollInterval = pollInterval;
            _connectTimeout = connectTimeout;
        }

        /// <summary>Scans for devices in provisioning mode (name prefix PROV_RESMON_), strongest signal first.</summary>
        public Task<IReadOnlyList<ProvisioningDeviceInfo>> ScanAsync(
            TimeSpan duration, IProgress<ProvisioningDeviceInfo>? deviceFound, CancellationToken cancellationToken) =>
            _scanner.ScanAsync(duration, deviceFound, cancellationToken);

        /// <summary>
        /// Asks every COM port for the pairing code and returns the one reported by
        /// <paramref name="deviceName"/>, or null (the wizard then asks the user to type it).
        /// </summary>
        public Task<string?> TryReadPopFromUsbAsync(string deviceName, CancellationToken cancellationToken) =>
            _popReader.TryReadPopAsync(deviceName, cancellationToken);

        /// <summary>
        /// Connects to <paramref name="device"/>, establishes the Security1 session with
        /// <paramref name="pop"/>, sends the Wi-Fi credentials and waits (up to 30 s) for the device to
        /// join. Returns Connected(ip4) / Failed(reason) / Timeout, or ConnectionLost when the link
        /// dropped after the settings were applied (the device may have joined Wi-Fi: check the normal
        /// MQTT connection to resmon.local before reporting failure).
        /// Throws <see cref="ArgumentException"/> for an invalid pairing code format or credential length,
        /// <see cref="PairingCodeRejectedException"/> when the device rejects the code,
        /// <see cref="ProvisioningRetryRejectedException"/> when the device refuses a retry after a failure,
        /// <see cref="ProvisioningTransportException"/> for Bluetooth failures and
        /// <see cref="ProvisioningProtocolException"/> for unexpected device answers.
        /// </summary>
        public async Task<WifiProvisioningResult> ProvisionAsync(
            ProvisioningDeviceInfo device,
            string pop,
            string ssid,
            string password,
            IProgress<ProvisioningStep>? progress,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(device);
            if (!PairingCode.TryNormalize(pop, out var pairingCode))
            {
                throw new ArgumentException(
                    $"The pairing code must be {ProvisioningConstants.PopLength} characters (letters and digits 2-9, no 0, O, 1 or I).",
                    nameof(pop));
            }

            WifiConfigClient.ValidateCredentials(ssid, password);

            progress?.Report(ProvisioningStep.ConnectingToDevice);
            _logger.LogInformation("Provisioning {Device} ({Address}): connecting.", device.Name, device.AddressText);
            var transport = await _transportFactory(device, cancellationToken).ConfigureAwait(false);
            await using (transport.ConfigureAwait(false))
            {
                await LogProtocolVersionAsync(transport, cancellationToken).ConfigureAwait(false);

                progress?.Report(ProvisioningStep.SecuringSession);
                using var session = await Security1Session.EstablishAsync(transport, pairingCode, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Provisioning {Device}: secure session established.", device.Name);

                var client = new WifiConfigClient(session, _pollInterval, _connectTimeout);
                progress?.Report(ProvisioningStep.SendingWifiConfig);
                var result = await client.ConfigureAsync(
                    ssid,
                    password,
                    beforeWaiting: () => progress?.Report(ProvisioningStep.WaitingForWifiConnection),
                    cancellationToken).ConfigureAwait(false);

                if (client.PreviousAttemptFailed)
                {
                    _logger.LogInformation("Provisioning {Device}: device had reported an earlier failed Wi-Fi attempt.", device.Name);
                }

                // The SSID is never logged, only whether it matched.
                switch (result)
                {
                    case WifiProvisioningResult.Connected { AlreadyConnected: true } already:
                        _logger.LogInformation(
                            "Provisioning {Device}: device already connected to Wi-Fi ({Network}); credentials were not sent again. Address {Address}.",
                            device.Name,
                            already.JoinedDifferentNetwork ? "a different network than requested"
                                : already.JoinedSsid is null ? "network not reported" : "the requested network",
                            already.Ip4Address ?? "(not reported)");
                        break;
                    case WifiProvisioningResult.Connected connected:
                        _logger.LogInformation("Provisioning {Device}: joined Wi-Fi, address {Address}.",
                            device.Name, connected.Ip4Address ?? "(not reported)");
                        break;
                    case WifiProvisioningResult.Failed failed:
                        _logger.LogWarning("Provisioning {Device}: Wi-Fi connection failed ({Reason}).", device.Name, failed.Reason);
                        break;
                    case WifiProvisioningResult.ConnectionLost lost:
                        _logger.LogWarning("Provisioning {Device}: link lost while waiting for Wi-Fi (last state {State}); " +
                            "the device may have joined Wi-Fi.", device.Name, lost.LastState);
                        break;
                    case WifiProvisioningResult.Timeout timeout:
                        _logger.LogWarning("Provisioning {Device}: no Wi-Fi result within {Seconds} s (last state {State}).",
                            device.Name, _connectTimeout.TotalSeconds, timeout.LastState);
                        break;
                }

                return result;
            }
        }

        private async Task LogProtocolVersionAsync(IProvisioningTransport transport, CancellationToken cancellationToken)
        {
            try
            {
                // Plain text (not encrypted) probe, as esp_prov.py get_version() sends.
                var response = await transport.SendAsync(
                    ProvisioningConstants.VersionEndpoint, Encoding.ASCII.GetBytes(ProvisioningConstants.VersionProbe), cancellationToken)
                    .ConfigureAwait(false);
                var info = ProtocolVersionInfo.Parse(response);
                _logger.LogInformation("Device provisioning protocol version {Version}, capabilities [{Capabilities}].",
                    info.Version ?? "(unknown)", string.Join(", ", info.Capabilities));
            }
            catch (ProvisioningTransportException ex)
            {
                _logger.LogInformation("Device did not report a provisioning protocol version: {Error}", ex.Message);
            }
        }
    }
}
