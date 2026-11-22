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
        /// if it did not report a usable unicast IPv4 address (do not save a fallback address then).
        /// </summary>
        public sealed record Connected(string? Ip4Address) : WifiProvisioningResult
        {
            /// <summary>
            /// SSID the device reports it joined (WifiConnectedState.ssid), or null if not reported.
            /// For display only; never log it.
            /// </summary>
            public string? JoinedSsid { get; init; }

            /// <summary>
            /// True when the device was already connected before this attempt sent anything, so no
            /// credentials were sent (it joined after an earlier attempt ended in Timeout/ConnectionLost).
            /// </summary>
            public bool AlreadyConnected { get; init; }

            /// <summary>
            /// True when <see cref="AlreadyConnected"/> and the device reports a different SSID from the one
            /// requested now. The UI should then say "the device is already on network X" instead of
            /// reporting success for the requested network. False if the device did not report its SSID.
            /// </summary>
            public bool JoinedDifferentNetwork { get; init; }
        }

        /// <summary>The device gave up joining Wi-Fi (wrong password or network not found).</summary>
        public sealed record Failed(WifiConnectFailedReason Reason) : WifiProvisioningResult;

        /// <summary>The device did not report a final state in time.</summary>
        public sealed record Timeout(WifiStationState LastState) : WifiProvisioningResult;

        /// <summary>
        /// The Bluetooth link dropped while waiting for the result, after the device had accepted and
        /// applied the settings. The device may well have joined Wi-Fi (the firmware keeps BLE up for
        /// about 35 s after success, but the link can also drop for radio reasons), so the caller should
        /// check the normal connection (e.g. MQTT to resmon.local) before declaring failure. <paramref name="Reason"/> is a user-presentable description.
        /// </summary>
        public sealed record ConnectionLost(WifiStationState LastState, string Reason) : WifiProvisioningResult;
    }

    /// <summary>A single decoded GetStatus answer.</summary>
    public sealed record WifiStatus(WifiStationState State, WifiConnectFailedReason? FailReason, string? Ip4Address)
    {
        /// <summary>SSID reported with a Connected status, or null. Never log it.</summary>
        public string? JoinedSsid { get; init; }
    }

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

        /// <summary>True once the device has reported ConnectionFailed (in this or an earlier session).</summary>
        public bool PreviousAttemptFailed => _previousAttemptFailed;

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
        /// The whole Wi-Fi step: checks the current status, then sends SetConfig, ApplyConfig and waits
        /// for the device to join the network.
        /// The status check comes first because the device's Wi-Fi state survives reconnects and
        /// wifi_prov_mgr_reset_sm_state_on_failure():
        /// <list type="bullet">
        /// <item>ConnectionFailed: an earlier attempt failed (sets <see cref="PreviousAttemptFailed"/>);</item>
        /// <item>Connected: the device already joined Wi-Fi after an earlier attempt ended in
        /// Timeout/ConnectionLost. No credentials are sent (ApplyConfig would fail) and a Connected result
        /// with <see cref="WifiProvisioningResult.Connected.AlreadyConnected"/> is returned; compare
        /// <see cref="WifiProvisioningResult.Connected.JoinedDifferentNetwork"/> before reporting success;</item>
        /// <item>Connecting: the normal state of a device that has not been configured yet.</item>
        /// </list>
        /// A non-success or malformed answer to the status check is ignored; transport errors propagate.
        /// The byte copies made from the passphrase are zeroed after encryption; the caller's string
        /// itself cannot be wiped, so drop references to it promptly.
        /// </summary>
        public Task<WifiProvisioningResult> ConfigureAsync(string ssid, string passphrase, CancellationToken cancellationToken) =>
            ConfigureAsync(ssid, passphrase, beforeWaiting: null, cancellationToken);

        /// <summary>
        /// Same as the public overload; <paramref name="beforeWaiting"/> runs after ApplyConfig succeeded,
        /// just before polling starts (the facade reports its progress step there).
        /// </summary>
        internal async Task<WifiProvisioningResult> ConfigureAsync(
            string ssid, string passphrase, Action? beforeWaiting, CancellationToken cancellationToken)
        {
            ValidateCredentials(ssid, passphrase);

            WifiStatus? initial;
            try
            {
                initial = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ProvisioningProtocolException)
            {
                initial = null;
            }

            if (initial?.State == WifiStationState.Connected)
            {
                return new WifiProvisioningResult.Connected(NormalizeIp4(initial.Ip4Address))
                {
                    JoinedSsid = initial.JoinedSsid,
                    AlreadyConnected = true,
                    JoinedDifferentNetwork = initial.JoinedSsid is not null
                                             && !string.Equals(initial.JoinedSsid, ssid, StringComparison.Ordinal),
                };
            }

            await SetConfigAsync(ssid, passphrase, cancellationToken).ConfigureAwait(false);
            await ApplyConfigAsync(cancellationToken).ConfigureAwait(false);
            beforeWaiting?.Invoke();
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
                EnsureAccepted(response.Status, "rejected the Wi-Fi settings", internalErrorMeansRetryRejected: _previousAttemptFailed);
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
            // In v4.4.7 an InternalError here comes from wifi_prov_mgr_configure_sta failing because
            // prov_state is already CRED_RECV or later: an earlier attempt is still connecting (e.g. after a
            // Timeout) or failed without wifi_prov_mgr_reset_sm_state_on_failure(). The only other source is
            // no config having been set. Either way the device needs a provisioning restart.
            EnsureAccepted(response.Status, "could not apply the Wi-Fi settings", internalErrorMeansRetryRejected: true);
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
                WifiStationState.Connected => new WifiStatus(response.StaState, null, response.Ip4Address)
                {
                    JoinedSsid = response.ConnectedSsid,
                },
                WifiStationState.ConnectionFailed => new WifiStatus(response.StaState, response.FailReason, null),
                _ => new WifiStatus(response.StaState, null, null),
            };
        }

        /// <summary>
        /// Polls GetStatus at most once per poll interval until Connected, ConnectionFailed or the timeout.
        /// Connecting and Disconnected are treated as transient (esp_prov retries on them too).
        /// The whole wait, including an in-flight GetStatus exchange, is bounded by the timeout.
        /// Losing the link is reported as <see cref="WifiProvisioningResult.ConnectionLost"/>, because the
        /// device may have joined Wi-Fi anyway (it applied the settings before the link went away).
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
                            return new WifiProvisioningResult.Connected(NormalizeIp4(status.Ip4Address))
                            {
                                JoinedSsid = status.JoinedSsid,
                            };
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
                    "Lost contact with the device before it reported the result. It may have joined Wi-Fi; " +
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
            if (parts.Length != 4
                || !IPAddress.TryParse(trimmed, out var address)
                || address.AddressFamily != AddressFamily.InterNetwork)
            {
                return null;
            }

            // Reject addresses a station can't be reached at: 0.0.0.0/8 ("this network", e.g. no lease
            // yet), 127.0.0.0/8 loopback, 224.0.0.0/4 multicast and 240.0.0.0/4 reserved, which includes
            // the 255.255.255.255 broadcast address.
            var first = address.GetAddressBytes()[0];
            if (first == 0 || first == 127 || first >= 224)
            {
                return null;
            }

            return address.ToString();
        }

        private void EnsureAccepted(ProtocommStatus status, string action, bool internalErrorMeansRetryRejected)
        {
            if (status == ProtocommStatus.Success)
            {
                return;
            }

            if (status == ProtocommStatus.InternalError && internalErrorMeansRetryRejected)
            {
                // wifi_prov_mgr refuses new credentials after a failure unless the firmware calls
                // wifi_prov_mgr_reset_sm_state_on_failure() (required by the pairing contract).
                throw new ProvisioningRetryRejectedException(_previousAttemptFailed);
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
