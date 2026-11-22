using ResourceMonitorDaemon.Provisioning;

namespace ResourceMonitorDaemon.Pairing
{
    /// <summary>
    /// Fixed user-visible texts of the pairing wizard and the first-run tray prompt, kept in one place
    /// so they can move to resources (i18n) later. Result messages that depend on the outcome are
    /// built in <see cref="PairingOutcomePresenter"/>. Currently bilingual: Chinese, then English.
    /// </summary>
    public static class PairingText
    {
        // ---- Tray ------------------------------------------------------------------------------

        public const string TrayMenuItem = "配对设备 (Pair device)…";

        /// <summary>
        /// Text of the one-time first-run balloon. It replaces the first "MQTT connection failed" balloon,
        /// so the tray keeps that failure's title and this text states the failure.
        /// </summary>
        public const string FirstRunBalloonText =
            "无法连接设备。未找到设备，点击进行配对 (Cannot reach the device. No device found — click to pair)\n" +
            "也可以右键托盘图标 → 配对设备 (or tray menu → Pair device)";

        // ---- Wizard: frame -----------------------------------------------------------------------

        public const string WindowTitle = "配对设备 (Pair device) - Resource Monitor";

        public const string Intro =
            "通过蓝牙为设备配置 Wi-Fi。设备需处于配对模式（首次启动，或" + PairingOutcomePresenter.ReprovisionInstructionZh + "）。\n" +
            "Set up the device's Wi-Fi over Bluetooth. The device must be in pairing mode (first start, or " +
            ProvisioningConstants.ReprovisionInstruction + ").";

        public const string PairButton = "开始配对 (Pair)";
        public const string CancelButton = "取消 (Cancel)";
        public const string CloseButton = "关闭 (Close)";

        // ---- 1. Device ---------------------------------------------------------------------------

        public const string DeviceGroup = "1. 选择设备 (Select device)";
        public const string DeviceColumn = "设备 (Device)";
        public const string SignalColumn = "信号 (Signal)";
        public const string RescanButton = "重新扫描 (Rescan)";
        public const string Scanning = "正在扫描蓝牙设备… (Scanning for devices…)";

        public static string SignalStrength(short rssi) => $"{rssi} dBm";

        // ---- 2. Pairing code -----------------------------------------------------------------------

        public const string CodeGroup = "2. 配对码 (Pairing code)";
        public const string CodeSelectDeviceFirst = "请先选择设备 (select a device first)";
        public const string CodeReadingUsb = "正在通过 USB 查找配对码… (looking for the pairing code over USB…)";
        public const string CodeReadOverUsb = "已通过 USB 自动读取配对码 (read automatically over USB)";
        public const string CodeNotFoundOverUsb = "未能通过 USB 读取，请手动输入 (not found over USB — enter it manually)";
        public const string CodeUsbQueryStopped = "未通过 USB 读取配对码 (pairing code not read over USB)";
        public const string CodeEnterManually = "手动输入 (Enter manually)";
        public const string CodeUseUsb = "使用 USB 读取的配对码 (Use the code read over USB)";

        public const string CodeHint =
            "配对码共 8 位，在设备的 USB 串口日志中（115200 波特）：Provisioning: name=PROV_RESMON_… pop=…\n" +
            "The 8-character code is in the device's USB serial log (115200 baud), line \"Provisioning: name=... pop=...\".";

        // ---- 3. Wi-Fi -------------------------------------------------------------------------------

        public const string WifiGroup = "3. Wi-Fi";
        public const string SsidLabel = "网络名称 (SSID):";
        public const string PasswordLabel = "密码 (Password):";
        public const string ShowPassword = "显示 (Show)";

        public const string WifiNote =
            "ESP32 仅支持 2.4 GHz Wi-Fi (2.4 GHz only)。密码只用于本次配对，不会保存 (the password is used for this attempt only and never saved).";

        /// <summary>
        /// Link next to the SSID box. The lookup runs only on click: reading the connected SSID can make
        /// Windows 11 ask once for location permission.
        /// </summary>
        public const string UseThisPcWifi = "使用本机当前 Wi-Fi (Use this PC's Wi-Fi)";

        public const string ReadingThisPcWifi = "正在读取本机 Wi-Fi… (reading this PC's Wi-Fi…)";

        public const string ThisPcWifiUnavailable =
            "无法读取本机当前 Wi-Fi（未连接，或 Windows 未授予位置权限），请手动输入。\n" +
            "Could not read this PC's Wi-Fi (not connected, or Windows did not grant location access); type the network name.";

        public const string SsidPrefilled = "已填入此电脑当前连接的 Wi-Fi (prefilled with this PC's current Wi-Fi)";

        public const string SsidPrefilledNot24GHz =
            "已填入此电脑当前的 Wi-Fi；此电脑通过 5/6 GHz 连接。ESP32 只能连接 2.4 GHz，请确认路由器同时提供 2.4 GHz。\n" +
            "Prefilled with this PC's Wi-Fi, which it uses on 5/6 GHz. The ESP32 needs 2.4 GHz: make sure the router also offers it.";

        // ---- 4. Pair --------------------------------------------------------------------------------

        public const string ProgressGroup = "4. 配对 (Pair)";
        public const string Pairing = "正在配对… (Pairing…)";

        /// <summary>Step texts, in step order: the four <see cref="ProvisioningStep"/> values, then save and daemon.</summary>
        public static readonly IReadOnlyList<string> Steps = new[]
        {
            "连接设备 (Connect to device)",
            "建立加密会话 (Secure session)",
            "发送 Wi-Fi 设置 (Send Wi-Fi settings)",
            "等待设备连接 Wi-Fi (Wait for Wi-Fi)",
            "保存设备地址 (Save device address)",
            "守护进程连接设备 (Daemon connects)",
        };

        public const string StepCheckNetwork = "检查网络连接 (Check network connection)";
        public const string StepNoAddress = "保存设备地址：设备未报告 IP (no IP reported, nothing saved)";

        public static string StepSaved(string address) => $"保存设备地址 {address} (Saved {address})";

        /// <summary>
        /// The device was already on Wi-Fi (no credentials were sent) and reports a different network from
        /// the one entered. <paramref name="joinedSsid"/> may be shown in the UI but must never be logged.
        /// </summary>
        public static string AlreadyOnDifferentNetwork(string joinedSsid) =>
            $"设备已连接到网络“{joinedSsid}”，而不是你输入的网络。如需切换网络，请{PairingOutcomePresenter.ReprovisionInstructionZh}，然后重新配对。\n" +
            $"The device is already connected to network “{joinedSsid}”, not the one you entered. " +
            $"To switch networks, {ProvisioningConstants.ReprovisionInstruction}, and pair again.";

        public static string SaveFailed(string error) => $"无法保存设备地址 (Could not save the device address): {error}";

        public const string FallbackOverriddenByEnvironment =
            "注意：FallbackAddress 由环境变量 RESMON_Device__FallbackAddress 决定，保存的值不会生效。\n" +
            "Note: FallbackAddress is overridden by the RESMON_Device__FallbackAddress environment variable; the saved value is not used.";
    }
}
