using System.Net;
using System.Net.Sockets;
using ResourceMonitorDaemon.Localization;
using ResourceMonitorDaemon.Provisioning;

namespace ResourceMonitorDaemon.Pairing
{
    /// <summary>How a pairing attempt ended, as far as the wizard is concerned.</summary>
    public enum PairingOutcomeKind
    {
        /// <summary>The device joined Wi-Fi (or is reachable over the network).</summary>
        Succeeded,
        /// <summary>The attempt failed; the user can correct the input and try again.</summary>
        Failed,
        /// <summary>The user cancelled (or closed the wizard).</summary>
        Cancelled,
        /// <summary>The Bluetooth link was lost after the settings were applied: check the broker before deciding.</summary>
        CheckBroker,
    }

    /// <summary>Input field the wizard should move the focus to after an outcome.</summary>
    public enum PairingField
    {
        None,
        Device,
        PairingCode,
        Ssid,
        Password,
    }

    /// <summary>
    /// User-facing result of one pairing attempt. <see cref="AddressToSave"/> is non-null only when the
    /// device reported a usable IPv4 address, which the wizard then saves as <c>FallbackAddress</c>.
    /// </summary>
    public sealed record PairingOutcome(PairingOutcomeKind Kind, string Message)
    {
        public string? AddressToSave { get; init; }

        /// <summary>
        /// The device had already joined Wi-Fi before this attempt sent anything
        /// (<see cref="WifiProvisioningResult.Connected.AlreadyConnected"/>).
        /// </summary>
        public bool DeviceWasAlreadyConnected { get; init; }

        /// <summary>
        /// The device was already on Wi-Fi and reports a different network from the one entered
        /// (<see cref="WifiProvisioningResult.Connected.JoinedDifferentNetwork"/>): not a success.
        /// </summary>
        public bool DeviceOnDifferentNetwork { get; init; }

        public PairingField Focus { get; init; }
        public bool SuggestRescan { get; init; }
    }

    /// <summary>
    /// Pure decisions for the pairing wizard and the first-run prompt: result/exception to message
    /// mapping, whether to save the reported address, input validation and scan status text. No UI,
    /// no I/O, so it can be unit-tested. Messages are in the UI language (<see cref="Strings"/>) and
    /// never contain the Wi-Fi password or the pairing code. Exceptions are mapped by type and code
    /// (<see cref="ProvisioningTransportError"/>, <see cref="PairingCodeRejectionReason"/>, parameter
    /// name), never by showing their English message, which the wizard logs instead.
    /// </summary>
    public static class PairingOutcomePresenter
    {
        /// <summary>How long the wizard keeps trying the broker after <see cref="WifiProvisioningResult.ConnectionLost"/>.</summary>
        public static readonly TimeSpan BrokerCheckBudget = TimeSpan.FromSeconds(15);

        private static int BrokerCheckSeconds => (int)BrokerCheckBudget.TotalSeconds;

        /// <summary>
        /// Every text method takes an optional <see cref="StringSet"/>: the wizard passes the one it was
        /// created with, so its messages stay in its language; null means the current UI language.
        /// </summary>
        private static StringSet T(StringSet? text) => text ?? Strings.Current;

        // ---- Provisioning results -------------------------------------------------------------

        /// <summary>Maps the result of <see cref="ProvisioningService.ProvisionAsync"/> to an outcome.</summary>
        public static PairingOutcome Present(WifiProvisioningResult result, string host, StringSet? text = null)
        {
            ArgumentNullException.ThrowIfNull(result);
            switch (result)
            {
                case WifiProvisioningResult.Connected { AlreadyConnected: true, JoinedDifferentNetwork: true, JoinedSsid: { Length: > 0 } joinedSsid }:
                    // Nothing was sent: the device stays on the other network. Not a success, nothing saved.
                    return new PairingOutcome(PairingOutcomeKind.Failed, T(text).Pairing_AlreadyOnDifferentNetwork(joinedSsid, T(text).Pairing_ReprovisionInstruction))
                    {
                        DeviceWasAlreadyConnected = true,
                        DeviceOnDifferentNetwork = true,
                        Focus = PairingField.Ssid,
                    };

                case WifiProvisioningResult.Connected connected:
                    // Includes an already-connected device that did not report its SSID (JoinedSsid null).
                    var address = GetAddressToSave(connected.Ip4Address);
                    var message = address is null ? T(text).Pairing_SucceededNoAddress(host) : T(text).Pairing_Succeeded(address);
                    if (connected.AlreadyConnected)
                    {
                        message += "\n" + T(text).Pairing_AlreadyConnected;
                    }

                    return new PairingOutcome(PairingOutcomeKind.Succeeded, message)
                    {
                        AddressToSave = address,
                        DeviceWasAlreadyConnected = connected.AlreadyConnected,
                    };

                case WifiProvisioningResult.Failed { Reason: WifiConnectFailedReason.AuthError }:
                    return new PairingOutcome(PairingOutcomeKind.Failed, T(text).Pairing_WrongPassword)
                    {
                        Focus = PairingField.Password,
                    };

                case WifiProvisioningResult.Failed { Reason: WifiConnectFailedReason.NetworkNotFound }:
                    return new PairingOutcome(PairingOutcomeKind.Failed, T(text).Pairing_NetworkNotFound)
                    {
                        Focus = PairingField.Ssid,
                    };

                case WifiProvisioningResult.Failed failed:
                    return new PairingOutcome(PairingOutcomeKind.Failed, T(text).Pairing_WifiFailed((int)failed.Reason))
                    {
                        Focus = PairingField.Ssid,
                    };

                case WifiProvisioningResult.Timeout:
                    return new PairingOutcome(
                        PairingOutcomeKind.Failed,
                        T(text).Pairing_WifiTimeout((int)ProvisioningConstants.WifiConnectTimeout.TotalSeconds))
                    {
                        Focus = PairingField.Ssid,
                    };

                case WifiProvisioningResult.ConnectionLost:
                    return new PairingOutcome(PairingOutcomeKind.CheckBroker, T(text).Pairing_ConnectionLost(BrokerCheckSeconds));

                default:
                    return new PairingOutcome(PairingOutcomeKind.Failed, T(text).Pairing_Failed);
            }
        }

        /// <summary>
        /// Final outcome after <see cref="WifiProvisioningResult.ConnectionLost"/>: success when the
        /// broker answered at <paramref name="host"/>:<paramref name="port"/> within the check budget.
        /// No address is saved: the device did not report one.
        /// </summary>
        public static PairingOutcome PresentBrokerCheck(bool reachable, string host, int port, StringSet? text = null) =>
            reachable
                ? new PairingOutcome(PairingOutcomeKind.Succeeded, T(text).Pairing_BrokerReachable(host, port))
                : new PairingOutcome(
                    PairingOutcomeKind.Failed,
                    T(text).Pairing_BrokerUnreachable(host, port, BrokerCheckSeconds, T(text).Pairing_ReprovisionInstruction))
                {
                    SuggestRescan = true,
                };

        /// <summary>Maps an exception thrown by <see cref="ProvisioningService.ProvisionAsync"/> to an outcome.</summary>
        public static PairingOutcome PresentException(Exception exception, StringSet? text = null)
        {
            ArgumentNullException.ThrowIfNull(exception);
            switch (exception)
            {
                case OperationCanceledException:
                    return new PairingOutcome(PairingOutcomeKind.Cancelled, T(text).Pairing_Cancelled);

                case PairingCodeRejectedException rejected:
                    return new PairingOutcome(
                        PairingOutcomeKind.Failed,
                        rejected.Reason == PairingCodeRejectionReason.VerificationMismatch
                            ? T(text).Provisioning_CodeNotVerified
                            : T(text).Provisioning_CodeRejected)
                    {
                        Focus = PairingField.PairingCode,
                    };

                case ProvisioningRetryRejectedException retry:
                    return new PairingOutcome(
                        PairingOutcomeKind.Failed,
                        retry.PreviousAttemptFailed
                            ? T(text).Provisioning_RetryRejectedAfterFailure(T(text).Pairing_ReprovisionInstruction)
                            : T(text).Provisioning_RetryRejected(T(text).Pairing_ReprovisionInstruction))
                    {
                        SuggestRescan = true,
                    };

                case ProvisioningTransportException transport:
                    return PresentTransportFailure(transport, text);

                case ProvisioningProtocolException:
                    return new PairingOutcome(PairingOutcomeKind.Failed, T(text).Provisioning_UnexpectedAnswer)
                    {
                        SuggestRescan = true,
                    };

                case ArgumentException argument:
                    return PresentInvalidInput(argument, text);

                default:
                    return new PairingOutcome(PairingOutcomeKind.Failed, T(text).Provisioning_FailedWithType(exception.GetType().Name));
            }
        }

        /// <summary>The UI text for a Bluetooth transport failure, by error code (the English exception message is only logged).</summary>
        public static string DescribeTransportError(ProvisioningTransportError error, StringSet? text = null) => error switch
        {
            ProvisioningTransportError.BluetoothOff => T(text).Provisioning_BluetoothOff,
            ProvisioningTransportError.BluetoothUnavailable => T(text).Provisioning_BluetoothUnavailable,
            ProvisioningTransportError.DeviceNotFound => T(text).Provisioning_DeviceNotFound,
            ProvisioningTransportError.DeviceDisconnected => T(text).Provisioning_DeviceDisconnected,
            ProvisioningTransportError.Timeout => T(text).Provisioning_Timeout,
            ProvisioningTransportError.ServiceNotFound or ProvisioningTransportError.EndpointNotFound =>
                T(text).Provisioning_ServiceNotFound(T(text).Pairing_ReprovisionInstruction),
            ProvisioningTransportError.GattError => T(text).Provisioning_GattError,
            _ => T(text).Provisioning_TransportFailed,
        };

        private static PairingOutcome PresentTransportFailure(ProvisioningTransportException transport, StringSet? text)
        {
            var rescan = transport.Error is not (ProvisioningTransportError.BluetoothOff or ProvisioningTransportError.BluetoothUnavailable);
            return new PairingOutcome(PairingOutcomeKind.Failed, DescribeTransportError(transport.Error, text))
            {
                SuggestRescan = rescan,
                Focus = rescan ? PairingField.Device : PairingField.None,
            };
        }

        private static PairingOutcome PresentInvalidInput(ArgumentException argument, StringSet? text)
        {
            // Mapped by parameter name only: ArgumentException.Message is English and never shown.
            var (message, field) = argument.ParamName switch
            {
                "ssid" => (T(text).Provisioning_SsidLength(ProvisioningConstants.MaxSsidBytes), PairingField.Ssid),
                "passphrase" or "password" => (T(text).Provisioning_PasswordLength(ProvisioningConstants.MaxPassphraseBytes), PairingField.Password),
                "pop" => (T(text).Provisioning_PairingCodeFormat, PairingField.PairingCode),
                _ => (T(text).Provisioning_InvalidInput, PairingField.None),
            };

            return new PairingOutcome(PairingOutcomeKind.Failed, message) { Focus = field };
        }

        // ---- Saving the reported address ------------------------------------------------------

        /// <summary>
        /// Returns the dotted IPv4 address to save as <c>FallbackAddress</c>, or null when
        /// <paramref name="reportedIp4"/> is missing, not a dotted IPv4 address, or not a usable unicast
        /// address (0.0.0.0, broadcast, loopback, multicast, link-local).
        /// </summary>
        public static string? GetAddressToSave(string? reportedIp4)
        {
            if (string.IsNullOrWhiteSpace(reportedIp4))
            {
                return null;
            }

            var trimmed = reportedIp4.Trim();
            if (DeviceSettingsValidator.ValidateFallbackAddress(trimmed) is not null
                || !IPAddress.TryParse(trimmed, out var ip)
                || ip.AddressFamily != AddressFamily.InterNetwork)
            {
                return null;
            }

            var bytes = ip.GetAddressBytes();
            var unusable = ip.Equals(IPAddress.Any)
                || ip.Equals(IPAddress.Broadcast)
                || IPAddress.IsLoopback(ip)
                || bytes[0] >= 224                       // multicast and reserved
                || (bytes[0] == 169 && bytes[1] == 254)  // link-local: DHCP did not work
                || bytes[0] == 0;
            return unusable ? null : ip.ToString();
        }

        // ---- Input validation -----------------------------------------------------------------

        /// <summary>
        /// Validates the wizard inputs before an attempt. Returns null and the normalized pairing code
        /// when everything is usable, otherwise a message and the field to focus. Never echoes values.
        /// </summary>
        public static string? ValidateInputs(
            bool deviceSelected, string? pairingCodeInput, string ssid, string password, out string pairingCode, out PairingField field, StringSet? text = null)
        {
            pairingCode = string.Empty;
            field = PairingField.None;
            if (!deviceSelected)
            {
                field = PairingField.Device;
                return T(text).Pairing_SelectDeviceFirst;
            }

            if (!PairingCode.TryNormalize(pairingCodeInput, out var code))
            {
                field = PairingField.PairingCode;
                return T(text).Provisioning_PairingCodeFormat;
            }

            try
            {
                WifiConfigClient.ValidateCredentials(ssid, password);
            }
            catch (ArgumentException ex)
            {
                var outcome = PresentInvalidInput(ex, text);
                field = outcome.Focus;
                return outcome.Message;
            }

            pairingCode = code;
            return null;
        }

        // ---- Scan status ----------------------------------------------------------------------

        /// <summary>Status text after a completed scan that found <paramref name="count"/> devices.</summary>
        public static string DescribeScanResult(int count, StringSet? text = null) => count switch
        {
            0 => T(text).Pairing_ScanNone(T(text).Pairing_ReprovisionInstruction),
            1 => T(text).Pairing_ScanOne,
            _ => T(text).Pairing_ScanMany(count),
        };

        /// <summary>Status text when the scan could not run (Bluetooth off or unavailable, etc.).</summary>
        public static string DescribeScanFailure(Exception exception, StringSet? text = null) => exception switch
        {
            ProvisioningTransportException { Error: ProvisioningTransportError.BluetoothOff } => T(text).Pairing_ScanBluetoothOff,
            ProvisioningTransportException { Error: ProvisioningTransportError.BluetoothUnavailable } => T(text).Pairing_ScanBluetoothUnavailable,
            ProvisioningTransportException => T(text).Pairing_ScanFailedRescan,
            _ => T(text).Pairing_ScanFailed,
        };

        // ---- After saving: the daemon's own connection -----------------------------------------

        /// <summary>Final status line once the wizard stops waiting for the daemon's MQTT connection.</summary>
        public static string DescribeDaemonStatus(bool connected, bool paused, string host, StringSet? text = null) =>
            connected
                ? T(text).Pairing_DaemonConnected
                : paused
                    ? T(text).Pairing_DaemonPaused
                    : T(text).Pairing_DaemonRetrying(host);

        /// <summary>
        /// Whether the final "connected" status needs an MQTT connection made after the attempt started.
        /// It does unless the device was already on Wi-Fi before this attempt (nothing was sent, so its
        /// broker did not restart) and the effective fallback address did not change (so the daemon has
        /// no reason to reconnect): then an existing daemon connection already reaches the device.
        /// </summary>
        public static bool RequiresNewDaemonConnection(
            bool deviceWasAlreadyConnected, string? previousEffectiveFallback, string? newEffectiveFallback) =>
            !deviceWasAlreadyConnected
            || !string.Equals(
                (previousEffectiveFallback ?? string.Empty).Trim(),
                (newEffectiveFallback ?? string.Empty).Trim(),
                StringComparison.OrdinalIgnoreCase);

        // ---- First-run prompt -----------------------------------------------------------------

        /// <summary>
        /// Whether a connection-failure notification should be shown as the one-time
        /// "No device found — click to pair" prompt. All of these must hold:
        /// it is the connection-failure notification; the marker does not exist (it is written when the
        /// prompt is shown and when the daemon first connects to a broker); no fallback address is
        /// configured; <c>Host</c> is the default <see cref="DeviceSettings.DefaultHost"/> (a custom host
        /// means the user configured the connection); the daemon has not connected since it started; and
        /// the daemon did not run on this PC before this process (covers installations from before the
        /// marker existed).
        /// </summary>
        public static bool ShouldOfferFirstRunPairing(
            bool isConnectionFailure,
            bool markerExists,
            string? effectiveFallbackAddress,
            string? effectiveHost,
            bool connectedSinceStart,
            bool daemonRanBefore) =>
            isConnectionFailure
            && !markerExists
            && string.IsNullOrWhiteSpace(effectiveFallbackAddress)
            && string.Equals((effectiveHost ?? string.Empty).Trim(), DeviceSettings.DefaultHost, StringComparison.OrdinalIgnoreCase)
            && !connectedSinceStart
            && !daemonRanBefore;
    }
}
