namespace ResourceMonitorDaemon.Observability
{
    /// <summary>
    /// Fixed user-visible texts of the Observability dialog and its tray entry, kept in one place so
    /// they can move to resources (i18n) later. Currently bilingual: Chinese, then English.
    /// </summary>
    public static class ObservabilityText
    {
        // ---- Tray ------------------------------------------------------------------------------

        public const string TrayMenuItem = "可观测性 (Observability)…";
        public const string SavedBalloonTitle = "可观测性已保存 (Observability saved)";
        public const string SavedBalloonText = "新设置已生效，无需重启 (applied without restart)";

        // ---- Dialog: frame and fields --------------------------------------------------------------

        public const string WindowTitle = "可观测性 (Observability) - Resource Monitor";
        public const string MessageBoxCaption = "Observability";
        public const string EnabledCheckBox = "发送到 Grafana Loki (send to Grafana Loki)";
        public const string UrlPlaceholder = "https://logs-prod-xxx.grafana.net  /  http://loki:3100";
        public const string TenantPlaceholder = "(optional X-Scope-OrgID)";
        public const string HostLabelCheckBox = "添加 host 标签 (add host=<PC name> label)";

        public const string EnabledLabel = "启用 (Enabled)";
        public const string UrlLabel = "地址 (Url)";
        public const string AuthModeLabel = "认证 (Auth mode)";
        public const string UsernameLabel = "用户名 (Username)";
        public const string TokenLabel = "令牌 (Token)";
        public const string TenantLabel = "租户 (Tenant)";
        public const string HostLabelLabel = "主机标签 (Host label)";
        public const string MetricsIntervalLabel = "指标间隔 s (Metrics, 0 = off)";

        /// <summary>Suffix for fields set by an environment variable.</summary>
        public const string EnvironmentSuffix = " (env)";

        // ---- Dialog: token ---------------------------------------------------------------------------

        public const string TokenFromEnvironmentPlaceholder = "(env)";
        public const string TokenSavedPlaceholder = "已保存 (saved)";
        public const string TokenNotSetPlaceholder = "未设置 (not set)";
        public const string TokenWillBeClearedPlaceholder = "将清除 (will be cleared on Save)";
        public const string ClearTokenButton = "清除 (Clear)";

        // ---- Dialog: notes and buttons ---------------------------------------------------------------

        public static readonly string TokenNote =
            "令牌只以 DPAPI 加密形式保存（仅当前 Windows 用户可解密），不会写入日志；留空保留已保存的令牌。" + Environment.NewLine +
            "(The token is stored DPAPI-encrypted for this Windows user only and never logged; leave it empty to keep the saved token.)";

        public static string SavedToNote(string path) => $"保存到 (Saved to): {path}";

        public static string EnvironmentFieldsNote(IEnumerable<string> fields) =>
            "标记 (env) 的字段由环境变量决定，只读且不会保存 (fields marked (env) come from RESMON_ environment variables; read-only, not saved): "
            + string.Join(", ", fields);

        public const string SaveButton = "保存 (Save)";
        public const string CancelButton = "取消 (Cancel)";

        // ---- Dialog: test and save -------------------------------------------------------------------

        public const string TestButton = "测试连接 (Test)";
        public const string Testing = "测试中… (testing…)";
        public const string TestCancelled = "已取消或超时 (cancelled or timed out)";
        public const string TestNotSent = "未发送 (not sent)";
        public static string TestSucceeded(string detail) => "成功 (OK): " + detail;
        public static string TestFailed(string detail) => "失败 (Failed): " + detail;

        public const string InvalidSettingsCaption = "设置无效 (Invalid settings)";

        public static readonly string InsecureSaveQuestion =
            "凭据将通过未加密的 HTTP 发送。继续保存？" + Environment.NewLine + "(Credentials would be sent over unencrypted HTTP. Save anyway?)";

        public static readonly string InsecureTestQuestion =
            "测试会通过未加密的 HTTP 发送令牌。继续？" + Environment.NewLine + "(The test would send the token over unencrypted HTTP. Continue?)";

        public static string SaveFailed(string error) => $"无法保存设置 (Unable to save settings):{Environment.NewLine}{error}";
    }
}
