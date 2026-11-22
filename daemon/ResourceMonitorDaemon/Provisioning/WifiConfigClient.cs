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

        /// <summary>
        /// The Bluetooth link dropped while waiting for the result, after the device had accepted and
        /// applied the settings. The device may well have joined Wi-Fi (it stops BLE once it reports
        /// Connected), so the caller should check the normal connection (e.g. MQTT to resmon.local)
        /// before declaring failure. <paramref name="Reason"/> is a user-presentable description.
        /// </summary>
        public sealed record ConnectionLost(WifiStationState LastState, string Reason) : WifiProvisioningResult;
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
        private bool _previousAttemptFailed;

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
        /// The byte copies made from the passphrase are zeroed after encryption; the caller's string
        /// itself cannot be wiped, so drop references to it promptly.
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
                ValidateLengths(ssidBytes.Length, passphraseBytes.Length);
                plaintext = WifiConfigMessages.EncodeSetConfig(ssidBytes, passphraseBytes);
                var response = await SendAsync(plaintext, WifiConfigMsgType.RespSetConfig, cancellationToken).ConfigureAwait(false);
                EnsureAccepted(response.Status, "rejected the Wi-Fi settings");
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
            EnsureAccepted(response.Status, "could not apply the Wi-Fi settings");
        }

        public async Task<WifiStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            var response = await SendAsync(WifiConfigMessages.EncodeGetStatus(), WifiConfigMsgType.RespGetStatus, cancellationToken)
                .ConfigureAwait(false);
            if (response.Status != ProtocommStatus.Success)
            {
                throw new ProvisioningProtocolException($"The device could not report its Wi-Fi status (status {response.Status}).");
            }

            if (response.StaState == WifiStationState.ConnectionFailed)
            {
                _previousAttemptFailed = true;
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
        /// The whole wait, including an in-flight GetStatus exchange, is bounded by the timeout.
        /// A dropped link is reported as <see cref="WifiProvisioningResult.ConnectionLost"/>, because the
        /// device tears BLE down shortly after it reports Connected and may have joined Wi-Fi.
        /// </summary>
        public async Task<WifiProvisioningResult> WaitForConnectionAsync(CancellationToken cancellationToken)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(_connectTimeout);
            var lastState = WifiStationState.Connecting;
            try
            {
                while (true)
                {
                    await Task.Delay(_pollInterval, budget.Token).ConfigureAwait(false);

                    var status = await GetStatusAsync(budget.Token).ConfigureAwait(false);
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
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new WifiProvisioningResult.Timeout(lastState);
            }
            catch (ProvisioningTransportException ex) when (ex.Error is not ProvisioningTransportError.BluetoothOff
                                                                and not ProvisioningTransportError.BluetoothUnavailable)
            {
                return new WifiProvisioningResult.ConnectionLost(
                    lastState,
                    "The device disconnected before reporting the result. It may have joined Wi-Fi; " +
                    "check the device or try connecting to it normally.");
            }
        }

        /// <summary>
        /// Checks the device's limits (SSID 1..32 bytes, passphrase at most 63 bytes, UTF-8) without
        /// echoing either value. Throws <see cref="ArgumentException"/>.
        /// </summary>
        public static void ValidateCredentials(string ssid, string passphrase)
        {
            ArgumentNullException.ThrowIfNull(ssid);
            ArgumentNullException.ThrowIfNull(passphrase);
            ValidateLengths(Encoding.UTF8.GetByteCount(ssid), Encoding.UTF8.GetByteCount(passphrase));
        }

        private static void ValidateLengths(int ssidByteCount, int passphraseByteCount)
        {
            if (ssidByteCount == 0 || ssidByteCount > ProvisioningConstants.MaxSsidBytes)
            {
                throw new ArgumentException(
                    $"The Wi-Fi network name must be 1 to {ProvisioningConstants.MaxSsidBytes} bytes long.", "ssid");
            }

            if (passphraseByteCount > ProvisioningConstants.MaxPassphraseBytes)
            {
                throw new ArgumentException(
                    $"The Wi-Fi password must be at most {ProvisioningConstants.MaxPassphraseBytes} bytes long.", "passphrase");
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

        private void EnsureAccepted(ProtocommStatus status, string action)
        {
            if (status == ProtocommStatus.Success)
            {
                return;
            }

            if (status == ProtocommStatus.InternalError && _previousAttemptFailed)
            {
                // wifi_prov_mgr refuses new credentials after a failure unless the firmware calls
                // wifi_prov_mgr_reset_sm_state_on_failure() (required by the pairing contract).
                throw new ProvisioningRetryRejectedException();
            }

            throw new ProvisioningProtocolException($"The device {action} (status {status}).");
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
