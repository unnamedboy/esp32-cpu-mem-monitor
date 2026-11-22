using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace ResourceMonitorDaemon.Provisioning
{
    /// <summary>Outcome of sending Wi-Fi credentials to the device.</summary>
    public abstract record WifiProvisioningResult
    {
        private WifiProvisioningResult() { }

        /// <summary>
        /// The device joined Wi-Fi. <paramref name="Ip4Address"/> is the address it reported, or null
        /// if it did not report a valid dotted-quad IPv4 address.
        /// </summary>
        public sealed record Connected(string? Ip4Address) : WifiProvisioningResult;

        /// <summary>The device gave up joining Wi-Fi (wrong password or network not found).</summary>
        public sealed record Failed(WifiConnectFailedReason Reason) : WifiProvisioningResult;

        /// <summary>The device did not report a final state in time.</summary>
        public sealed record Timeout(WifiStationState LastState) : WifiProvisioningResult;
    }

    /// <summary>A single decoded GetStatus answer.</summary>
    public sealed record WifiStatus(WifiStationState State, WifiConnectFailedReason? FailReason, string? Ip4Address);

    /// <summary>
    /// Client for the prov-config endpoint (wifi_config.proto), following esp_prov wifi_prov.py:
    /// SetConfig -> ApplyConfig -> poll GetStatus until Connected or ConnectionFailed.
    /// https://github.com/espressif/esp-idf/blob/v4.4.7/tools/esp_prov/prov/wifi_prov.py
    /// </summary>
    public sealed class WifiConfigClient
    {
        private readonly Security1Session _session;
        private readonly TimeSpan _pollInterval;
        private readonly TimeSpan _connectTimeout;

        /// <summary>Uses the contract limits: one poll per second, at most 30 s.</summary>
        public WifiConfigClient(Security1Session session)
            : this(session, ProvisioningConstants.StatusPollInterval, ProvisioningConstants.WifiConnectTimeout)
        {
        }

        /// <summary>Test hook: shorter intervals for the in-memory fake device.</summary>
        internal WifiConfigClient(Security1Session session, TimeSpan pollInterval, TimeSpan connectTimeout)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _pollInterval = pollInterval;
            _connectTimeout = connectTimeout;
        }

        /// <summary>
        /// Sends SetConfig, ApplyConfig and waits for the device to join the network.
        /// The passphrase is only held in buffers that are zeroed after encryption.
        /// </summary>
        public async Task<WifiProvisioningResult> ConfigureAsync(string ssid, string passphrase, CancellationToken cancellationToken)
        {
            await SetConfigAsync(ssid, passphrase, cancellationToken).ConfigureAwait(false);
            await ApplyConfigAsync(cancellationToken).ConfigureAwait(false);
            return await WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task SetConfigAsync(string ssid, string passphrase, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(ssid);
            ArgumentNullException.ThrowIfNull(passphrase);

            var ssidBytes = Encoding.UTF8.GetBytes(ssid);
            var passphraseBytes = Encoding.UTF8.GetBytes(passphrase);
            byte[]? plaintext = null;
            try
            {
                if (ssidBytes.Length == 0 || ssidBytes.Length > ProvisioningConstants.MaxSsidBytes)
                {
                    throw new ArgumentException(
                        $"The Wi-Fi network name must be 1 to {ProvisioningConstants.MaxSsidBytes} bytes long.", nameof(ssid));
                }

                if (passphraseBytes.Length > ProvisioningConstants.MaxPassphraseBytes)
                {
                    throw new ArgumentException(
                        $"The Wi-Fi password must be at most {ProvisioningConstants.MaxPassphraseBytes} bytes long.", nameof(passphrase));
                }

                plaintext = WifiConfigMessages.EncodeSetConfig(ssidBytes, passphraseBytes);
                var response = await SendAsync(plaintext, WifiConfigMsgType.RespSetConfig, cancellationToken).ConfigureAwait(false);
                if (response.Status != ProtocommStatus.Success)
                {
                    throw new ProvisioningProtocolException(
                        $"The device rejected the Wi-Fi settings (status {response.Status}).");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passphraseBytes);
                if (plaintext is not null)
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
        }

        public async Task ApplyConfigAsync(CancellationToken cancellationToken)
        {
            var response = await SendAsync(WifiConfigMessages.EncodeApplyConfig(), WifiConfigMsgType.RespApplyConfig, cancellationToken)
                .ConfigureAwait(false);
            if (response.Status != ProtocommStatus.Success)
            {
                throw new ProvisioningProtocolException($"The device could not apply the Wi-Fi settings (status {response.Status}).");
            }
        }

        public async Task<WifiStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            var response = await SendAsync(WifiConfigMessages.EncodeGetStatus(), WifiConfigMsgType.RespGetStatus, cancellationToken)
                .ConfigureAwait(false);
            if (response.Status != ProtocommStatus.Success)
            {
                throw new ProvisioningProtocolException($"The device could not report its Wi-Fi status (status {response.Status}).");
            }

            return response.StaState switch
            {
                WifiStationState.Connected => new WifiStatus(response.StaState, null, response.Ip4Address),
                WifiStationState.ConnectionFailed => new WifiStatus(response.StaState, response.FailReason, null),
                _ => new WifiStatus(response.StaState, null, null),
            };
        }

        /// <summary>
        /// Polls GetStatus at most once per poll interval until Connected, ConnectionFailed or the timeout.
        /// Connecting and Disconnected are treated as transient (esp_prov retries on them too).
        /// </summary>
        public async Task<WifiProvisioningResult> WaitForConnectionAsync(CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            var lastState = WifiStationState.Connecting;
            while (true)
            {
                var remaining = _connectTimeout - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    return new WifiProvisioningResult.Timeout(lastState);
                }

                await Task.Delay(remaining < _pollInterval ? remaining : _pollInterval, cancellationToken).ConfigureAwait(false);

                var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
                lastState = status.State;
                switch (status.State)
                {
                    case WifiStationState.Connected:
                        return new WifiProvisioningResult.Connected(NormalizeIp4(status.Ip4Address));
                    case WifiStationState.ConnectionFailed:
                        return new WifiProvisioningResult.Failed(status.FailReason ?? WifiConnectFailedReason.AuthError);
                }
            }
        }

        internal static string? NormalizeIp4(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return null;
            }

            var trimmed = candidate.Trim().TrimEnd('\0');
            var parts = trimmed.Split('.');
            return parts.Length == 4
                   && IPAddress.TryParse(trimmed, out var address)
                   && address.AddressFamily == AddressFamily.InterNetwork
                ? address.ToString()
                : null;
        }

        private async Task<WifiConfigResponse> SendAsync(byte[] plaintext, WifiConfigMsgType expectedResponse, CancellationToken cancellationToken)
        {
            var decrypted = await _session.SendEncryptedAsync(ProvisioningConstants.ConfigEndpoint, plaintext, cancellationToken)
                .ConfigureAwait(false);
            var response = WifiConfigMessages.DecodeResponse(decrypted);
            if (response.MsgType != expectedResponse)
            {
                throw new ProvisioningProtocolException(
                    $"Unexpected Wi-Fi configuration message from device: {response.MsgType}, expected {expectedResponse}.");
            }

            return response;
        }
    }
}
