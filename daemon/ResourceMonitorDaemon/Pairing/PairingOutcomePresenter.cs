using System.Net;
using System.Net.Sockets;
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

        public PairingField Focus { get; init; }
        public bool SuggestRescan { get; init; }
    }

    /// <summary>
    /// Pure decisions for the pairing wizard and the first-run prompt: result/exception to message
    /// mapping, whether to save the reported address, input validation and scan status text. No UI,
    /// no I/O, so it can be unit-tested. Messages are bilingual (Chinese first, then English) and never
    /// contain the Wi-Fi password or the pairing code.
    /// </summary>
    public static class PairingOutcomePresenter
    {
        /// <summary>Chinese form of <see cref="ProvisioningConstants.ReprovisionInstruction"/>.</summary>
        public const string ReprovisionInstructionZh = "按住设备上的 BOOT 键 5–15 秒后松开";

        /// <summary>How long the wizard keeps trying the broker after <see cref="WifiProvisioningResult.ConnectionLost"/>.</summary>
        public static readonly TimeSpan BrokerCheckBudget = TimeSpan.FromSeconds(15);

        // ---- Provisioning results -------------------------------------------------------------

        /// <summary>Maps the result of <see cref="ProvisioningService.ProvisionAsync"/> to an outcome.</summary>
        public static PairingOutcome Present(WifiProvisioningResult result, string host)
        {
            ArgumentNullException.ThrowIfNull(result);
            switch (result)
            {
                case WifiProvisioningResult.Connected connected:
                    var address = GetAddressToSave(connected.Ip4Address);
                    var message = address is null
                        ? $"配对成功：设备已连接 Wi-Fi，但没有报告 IP 地址，将通过 {host} 连接（未保存备用地址）。\n" +
                          $"Paired: the device joined Wi-Fi but reported no IP address; connecting via {host} (no fallback address saved)."
                        : $"配对成功：设备已连接 Wi-Fi，IP 地址 {address}（已保存为备用地址）。\n" +
                          $"Paired: the device joined Wi-Fi at {address} (saved as the fallback address).";
                    if (connected.AlreadyConnected)
                    {
                        message += "\n设备已在之前的尝试中连接 Wi-Fi，本次没有重新发送设置。\n" +
                                   "The device had already joined Wi-Fi after an earlier attempt; the settings were not sent again.";
                    }

                    return new PairingOutcome(PairingOutcomeKind.Succeeded, message)
                    {
                        AddressToSave = address,
                        DeviceWasAlreadyConnected = connected.AlreadyConnected,
                    };

                case WifiProvisioningResult.Failed { Reason: WifiConnectFailedReason.AuthError }:
                    return new PairingOutcome(
                        PairingOutcomeKind.Failed,
                        "Wi-Fi 密码错误，请重新输入密码后重试。\nWrong Wi-Fi password. Re-enter it and try again.")
                    {
                        Focus = PairingField.Password,
                    };

                case WifiProvisioningResult.Failed { Reason: WifiConnectFailedReason.NetworkNotFound }:
                    return new PairingOutcome(
                        PairingOutcomeKind.Failed,
                        "找不到该 Wi-Fi（ESP32 仅支持 2.4 GHz）。请检查网络名称，并确认路由器开启了 2.4 GHz。\n" +
                        "Wi-Fi network not found (the ESP32 supports 2.4 GHz only). Check the network name and that the router offers 2.4 GHz.")
                    {
                        Focus = PairingField.Ssid,
                    };

                case WifiProvisioningResult.Failed failed:
                    return new PairingOutcome(
                        PairingOutcomeKind.Failed,
                        $"设备无法连接 Wi-Fi（原因 {(int)failed.Reason}）。请检查网络名称和密码后重试。\n" +
                        $"The device could not join Wi-Fi (reason {(int)failed.Reason}). Check the network name and password, then try again.")
                    {
                        Focus = PairingField.Ssid,
                    };

                case WifiProvisioningResult.Timeout:
                    return new PairingOutcome(
                        PairingOutcomeKind.Failed,
                        $"设备在 {ProvisioningConstants.WifiConnectTimeout.TotalSeconds:F0} 秒内没有报告 Wi-Fi 连接结果。请检查网络名称、密码，并确认是 2.4 GHz 网络，然后重试。\n" +
                        $"The device did not report a Wi-Fi result within {ProvisioningConstants.WifiConnectTimeout.TotalSeconds:F0} s. " +
                        "Check the network name, the password and that the network is 2.4 GHz, then try again.")
                    {
                        Focus = PairingField.Ssid,
                    };

                case WifiProvisioningResult.ConnectionLost:
                    return new PairingOutcome(
                        PairingOutcomeKind.CheckBroker,
                        $"与设备的蓝牙连接已断开，正在检查设备是否已加入网络（最多 {BrokerCheckBudget.TotalSeconds:F0} 秒）…\n" +
                        $"Lost the Bluetooth link; checking whether the device joined the network (up to {BrokerCheckBudget.TotalSeconds:F0} s)…");

                default:
                    return new PairingOutcome(
                        PairingOutcomeKind.Failed,
                        "配对失败，详情见日志。\nPairing failed; see the log for details.");
            }
        }

        /// <summary>
        /// Final outcome after <see cref="WifiProvisioningResult.ConnectionLost"/>: success when the
        /// broker answered at <paramref name="host"/>:<paramref name="port"/> within the check budget.
        /// No address is saved: the device did not report one.
        /// </summary>
        public static PairingOutcome PresentBrokerCheck(bool reachable, string host, int port) =>
            reachable
                ? new PairingOutcome(
                    PairingOutcomeKind.Succeeded,
                    $"配对成功：设备已加入网络，可通过 {host}:{port} 连接（设备没有报告 IP，未保存备用地址）。\n" +
                    $"Paired: the device joined the network and is reachable at {host}:{port} (no IP reported; no fallback address saved).")
                : new PairingOutcome(
                    PairingOutcomeKind.Failed,
                    $"蓝牙连接已断开，且 {BrokerCheckBudget.TotalSeconds:F0} 秒内无法连接 {host}:{port}。设备可能没有加入 Wi-Fi：请重新扫描后重试；" +
                    $"如果设备不再出现，请{ReprovisionInstructionZh}，然后重新配对。\n" +
                    $"Lost the Bluetooth link and {host}:{port} did not answer within {BrokerCheckBudget.TotalSeconds:F0} s. " +
                    "The device may not have joined Wi-Fi: rescan and try again. If the device no longer appears, " +
                    ProvisioningConstants.ReprovisionInstruction + ", then pair again.")
                {
                    SuggestRescan = true,
                };

        /// <summary>Maps an exception thrown by <see cref="ProvisioningService.ProvisionAsync"/> to an outcome.</summary>
        public static PairingOutcome PresentException(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            switch (exception)
            {
                case OperationCanceledException:
                    return new PairingOutcome(PairingOutcomeKind.Cancelled, "已取消。\nCancelled.");

                case PairingCodeRejectedException:
                    return new PairingOutcome(
                        PairingOutcomeKind.Failed,
                        "配对码错误：设备拒绝了该配对码。请核对配对码（串口日志 \"Provisioning: name=... pop=...\"）后重试。\n" +
                        "Pairing code rejected by the device. Check the code (serial log line \"Provisioning: name=... pop=...\") and try again.")
                    {
                        Focus = PairingField.PairingCode,
                    };

                case ProvisioningRetryRejectedException retry:
                    return new PairingOutcome(
                        PairingOutcomeKind.Failed,
                        $"设备不接受新的尝试（可能仍在处理之前的尝试）。请{ReprovisionInstructionZh}，然后重新扫描并重试。\n{retry.Message}")
                    {
                        SuggestRescan = true,
                    };

                case ProvisioningTransportException transport:
                    return PresentTransportFailure(transport);

                case ProvisioningProtocolException protocol:
                    return new PairingOutcome(
                        PairingOutcomeKind.Failed,
                        $"设备返回了意外的应答，请重新扫描后重试。\nUnexpected answer from the device: {protocol.Message}")
                    {
                        SuggestRescan = true,
                    };

                case ArgumentException argument:
                    return PresentInvalidInput(argument);

                default:
                    return new PairingOutcome(
                        PairingOutcomeKind.Failed,
                        $"配对失败（{exception.GetType().Name}），详情见日志。\nPairing failed ({exception.GetType().Name}); see the log for details.");
            }
        }

        private static PairingOutcome PresentTransportFailure(ProvisioningTransportException transport)
        {
            var (zh, rescan) = transport.Error switch
            {
                ProvisioningTransportError.BluetoothOff => ("蓝牙已关闭，请在 Windows 设置中打开蓝牙后重试。", false),
                ProvisioningTransportError.BluetoothUnavailable => ("此电脑没有可用的低功耗蓝牙 (BLE)。", false),
                ProvisioningTransportError.DeviceNotFound => ("找不到该设备，它可能已退出配对模式。请重新扫描。", true),
                ProvisioningTransportError.DeviceDisconnected => ("设备断开了蓝牙连接。请靠近设备，重新扫描后重试。", true),
                ProvisioningTransportError.Timeout => ("设备响应超时。请靠近设备，重新扫描后重试。", true),
                ProvisioningTransportError.ServiceNotFound or ProvisioningTransportError.EndpointNotFound =>
                    ("该设备没有提供配对服务（可能不在配对模式或固件不兼容）。", true),
                ProvisioningTransportError.GattError => ("蓝牙通信错误，请重新扫描后重试。", true),
                _ => ("蓝牙通信失败，请重新扫描后重试。", true),
            };

            return new PairingOutcome(PairingOutcomeKind.Failed, $"{zh}\n{transport.Message}")
            {
                SuggestRescan = rescan,
                Focus = rescan ? PairingField.Device : PairingField.None,
            };
        }

        private static PairingOutcome PresentInvalidInput(ArgumentException argument)
        {
            var (zh, field) = argument.ParamName switch
            {
                "ssid" => ($"Wi-Fi 名称需为 1–{ProvisioningConstants.MaxSsidBytes} 字节。", PairingField.Ssid),
                "passphrase" or "password" => ($"Wi-Fi 密码最多 {ProvisioningConstants.MaxPassphraseBytes} 字节。", PairingField.Password),
                "pop" => (PairingCodeFormatZh, PairingField.PairingCode),
                _ => ("输入无效。", PairingField.None),
            };

            // ArgumentException.Message appends " (Parameter 'x')"; the library never puts values in it.
            var english = argument.ParamName is null ? argument.Message : argument.Message.Replace($" (Parameter '{argument.ParamName}')", string.Empty);
            return new PairingOutcome(PairingOutcomeKind.Failed, $"{zh}\n{english}") { Focus = field };
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

        private const string PairingCodeFormatZh = "配对码为 8 位（字母和数字 2–9，不含 0、O、1、I）。";
        private const string PairingCodeFormatEn = "The pairing code is 8 characters (letters and digits 2–9; no 0, O, 1 or I).";

        /// <summary>
        /// Validates the wizard inputs before an attempt. Returns null and the normalized pairing code
        /// when everything is usable, otherwise a message and the field to focus. Never echoes values.
        /// </summary>
        public static string? ValidateInputs(
            bool deviceSelected, string? pairingCodeInput, string ssid, string password, out string pairingCode, out PairingField field)
        {
            pairingCode = string.Empty;
            field = PairingField.None;
            if (!deviceSelected)
            {
                field = PairingField.Device;
                return "请先选择设备。\nSelect a device first.";
            }

            if (!PairingCode.TryNormalize(pairingCodeInput, out var code))
            {
                field = PairingField.PairingCode;
                return $"{PairingCodeFormatZh}\n{PairingCodeFormatEn}";
            }

            try
            {
                WifiConfigClient.ValidateCredentials(ssid, password);
            }
            catch (ArgumentException ex)
            {
                var outcome = PresentInvalidInput(ex);
                field = outcome.Focus;
                return outcome.Message;
            }

            pairingCode = code;
            return null;
        }

        // ---- Scan status ----------------------------------------------------------------------

        /// <summary>Status text after a completed scan that found <paramref name="count"/> devices.</summary>
        public static string DescribeScanResult(int count) => count switch
        {
            0 => $"未找到处于配对模式的设备。请确认设备已通电并处于配对模式（首次启动，或{ReprovisionInstructionZh}），然后点击“重新扫描”。\n" +
                 "No device in pairing mode found. Check that it is powered and in pairing mode (first start, or " +
                 ProvisioningConstants.ReprovisionInstruction + "), then click Rescan.",
            1 => "找到 1 台设备，已自动选择。\nFound 1 device (selected).",
            _ => $"找到 {count} 台设备，请选择要配对的设备。\nFound {count} devices; select the one to pair.",
        };

        /// <summary>Status text when the scan could not run (Bluetooth off or unavailable, etc.).</summary>
        public static string DescribeScanFailure(Exception exception) => exception switch
        {
            ProvisioningTransportException { Error: ProvisioningTransportError.BluetoothOff } transport =>
                $"蓝牙已关闭。请在 Windows 设置中打开蓝牙，然后点击“重新扫描”。\n{transport.Message}",
            ProvisioningTransportException { Error: ProvisioningTransportError.BluetoothUnavailable } transport =>
                $"此电脑没有可用的低功耗蓝牙 (BLE)，无法扫描设备。\n{transport.Message}",
            ProvisioningTransportException transport =>
                $"蓝牙扫描失败，请点击“重新扫描”。\n{transport.Message}",
            _ => "蓝牙扫描失败，详情见日志。\nBluetooth scan failed; see the log for details.",
        };

        // ---- After saving: the daemon's own connection -----------------------------------------

        /// <summary>Final status line once the wizard stops waiting for the daemon's MQTT connection.</summary>
        public static string DescribeDaemonStatus(bool connected, bool paused, string host) =>
            connected
                ? "已连接，正在发送数据 (connected, publishing)"
                : paused
                    ? "发送已暂停，恢复发送后会自动连接设备 (sending is paused; resume it to connect)"
                    : $"守护进程仍在后台重试连接 {host} (the daemon keeps retrying {host} in the background)";

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
