using System.Globalization;
using System.Resources;

namespace ResourceMonitorDaemon.Localization
{
    /// <summary>
    /// Strongly-typed access to the user-visible texts in <c>Resources/Strings.resx</c> (English, the
    /// neutral language) and its satellite translations (<c>Strings.zh-Hans.resx</c>, built into
    /// <c>zh-Hans/ResourceMonitorDaemon.resources.dll</c>). One member per resource key: a property
    /// for plain text, a method for a format string whose parameters fill <c>{0}</c>, <c>{1}</c>, …
    /// in order (the English resx comment names them). <c>LocalizationTests</c> checks that this
    /// class, the English file and every translation have the same keys and placeholders.
    /// </summary>
    /// <remarks>
    /// Keys are grouped by prefix: <c>Common_</c>, <c>Tray_</c>, <c>Settings_</c>, <c>Observability_</c>,
    /// <c>Pairing_</c>, <c>Provisioning_</c>. Lookups use <see cref="Culture"/>, set by
    /// <see cref="UiCulture"/>; the thread cultures (number formatting, MQTT payloads) are never
    /// changed. Log messages do not use this class except through <see cref="UiMessage.English"/>,
    /// which always resolves the neutral English text.
    /// </remarks>
    public static class Strings
    {
        /// <summary>Manifest name of <c>Resources/Strings.resx</c> (root namespace + folder + file name).</summary>
        public const string BaseName = "ResourceMonitorDaemon.Resources.Strings";

        private static CultureInfo _culture = UiLanguage.EnglishCulture;

        public static ResourceManager ResourceManager { get; } = new(BaseName, typeof(Strings).Assembly);

        /// <summary>The culture texts are looked up in. Set through <see cref="UiCulture.Apply"/>.</summary>
        public static CultureInfo Culture
        {
            get => Volatile.Read(ref _culture);
            internal set => Volatile.Write(ref _culture, value);
        }

        /// <summary>
        /// The texts bound to the current UI culture. Dialogs take one when they are created and keep
        /// using it, so an open dialog never mixes languages.
        /// </summary>
        public static StringSet Current => new(Culture);

        /// <summary>The text for <paramref name="key"/> in <paramref name="culture"/> (falls back to English).</summary>
        public static string Get(string key, CultureInfo culture) =>
            ResourceManager.GetString(key, culture) ?? $"[{key}]";

        /// <summary>Formats the text for <paramref name="key"/> in <paramref name="culture"/> (numbers use that culture too).</summary>
        public static string Format(CultureInfo culture, string key, params object?[] args) =>
            string.Format(culture, Get(key, culture), args);

        private static string Get(string key) => Get(key, Culture);

        private static string Format(string key, params object?[] args)
        {
            var culture = Culture;
            return string.Format(culture, Get(key, culture), args);
        }

        public static string Common_ProductName => Get(nameof(Common_ProductName));
        public static string Common_Save => Get(nameof(Common_Save));
        public static string Common_Cancel => Get(nameof(Common_Cancel));
        public static string Common_Close => Get(nameof(Common_Close));
        public static string Common_None => Get(nameof(Common_None));
        public static string Common_FieldLabel(string label) => Format(nameof(Common_FieldLabel), label);
        public static string Common_EnvFieldLabel(string label) => Format(nameof(Common_EnvFieldLabel), label);
        public static string Common_SavedTo(string path) => Format(nameof(Common_SavedTo), path);
        public static string Common_EnvironmentFieldsNote(string fields) => Format(nameof(Common_EnvironmentFieldsNote), fields);
        public static string Common_InvalidSettingsCaption => Get(nameof(Common_InvalidSettingsCaption));
        public static string Common_SaveFailed(string path) => Format(nameof(Common_SaveFailed), path);
        public static string Common_SaveFailedInvalidFile(string path) => Format(nameof(Common_SaveFailedInvalidFile), path);
        public static string Common_StartupFailed(string error, string logPath) => Format(nameof(Common_StartupFailed), error, logPath);

        public static string Tray_Pause => Get(nameof(Tray_Pause));
        public static string Tray_Resume => Get(nameof(Tray_Resume));
        public static string Tray_Settings => Get(nameof(Tray_Settings));
        public static string Tray_PairDevice => Get(nameof(Tray_PairDevice));
        public static string Tray_ViewLog => Get(nameof(Tray_ViewLog));
        public static string Tray_Observability => Get(nameof(Tray_Observability));
        public static string Tray_Exit => Get(nameof(Tray_Exit));
        public static string Tray_PausedTitle => Get(nameof(Tray_PausedTitle));
        public static string Tray_PausedText => Get(nameof(Tray_PausedText));
        public static string Tray_ResumedTitle => Get(nameof(Tray_ResumedTitle));
        public static string Tray_ResumedText => Get(nameof(Tray_ResumedText));
        public static string Tray_SettingsSavedTitle => Get(nameof(Tray_SettingsSavedTitle));
        public static string Tray_ObservabilitySavedTitle => Get(nameof(Tray_ObservabilitySavedTitle));
        public static string Tray_AppliedWithoutRestart => Get(nameof(Tray_AppliedWithoutRestart));
        public static string Tray_AppliedEnvironmentOverride(string fields) => Format(nameof(Tray_AppliedEnvironmentOverride), fields);
        public static string Tray_NoLogYet => Get(nameof(Tray_NoLogYet));
        public static string Tray_MqttFailedTitle => Get(nameof(Tray_MqttFailedTitle));
        public static string Tray_MqttFailedText(object endpoint) => Format(nameof(Tray_MqttFailedText), endpoint);
        public static string Tray_MqttEndpoint(string host, int port) => Format(nameof(Tray_MqttEndpoint), host, port);
        public static string Tray_MqttEndpointWithFallback(string host, int port, string fallback) => Format(nameof(Tray_MqttEndpointWithFallback), host, port, fallback);
        public static string Tray_MqttReconnectedTitle => Get(nameof(Tray_MqttReconnectedTitle));
        public static string Tray_MqttReconnectedText(string address, int port) => Format(nameof(Tray_MqttReconnectedText), address, port);
        public static string Tray_MqttReconnectedViaFallback(string address, int port, string host) => Format(nameof(Tray_MqttReconnectedViaFallback), address, port, host);

        public static string Settings_WindowTitle => Get(nameof(Settings_WindowTitle));
        public static string Settings_Host => Get(nameof(Settings_Host));
        public static string Settings_Port => Get(nameof(Settings_Port));
        public static string Settings_CpuTopic => Get(nameof(Settings_CpuTopic));
        public static string Settings_MemTopic => Get(nameof(Settings_MemTopic));
        public static string Settings_SampleInterval => Get(nameof(Settings_SampleInterval));
        public static string Settings_StatusTopic => Get(nameof(Settings_StatusTopic));
        public static string Settings_ReconnectInitialBackoff => Get(nameof(Settings_ReconnectInitialBackoff));
        public static string Settings_ReconnectMaxBackoff => Get(nameof(Settings_ReconnectMaxBackoff));
        public static string Settings_ConnectTimeout => Get(nameof(Settings_ConnectTimeout));
        public static string Settings_FallbackAddress => Get(nameof(Settings_FallbackAddress));
        public static string Settings_Language => Get(nameof(Settings_Language));
        public static string Settings_LanguageAutomatic => Get(nameof(Settings_LanguageAutomatic));
        public static string Settings_LanguageEnglish => Get(nameof(Settings_LanguageEnglish));
        public static string Settings_LanguageChineseSimplified => Get(nameof(Settings_LanguageChineseSimplified));
        public static string Settings_LanguageNote => Get(nameof(Settings_LanguageNote));
        public static string Settings_ErrorEmpty(string name) => Format(nameof(Settings_ErrorEmpty), name);
        public static string Settings_ErrorHostInvalid(string name, string host) => Format(nameof(Settings_ErrorHostInvalid), name, host);
        public static string Settings_ErrorFallbackInvalid(string name, string address) => Format(nameof(Settings_ErrorFallbackInvalid), name, address);
        public static string Settings_ErrorTopicTooLong(string name, int maxLength) => Format(nameof(Settings_ErrorTopicTooLong), name, maxLength);
        public static string Settings_ErrorTopicWildcard(string name, string topic) => Format(nameof(Settings_ErrorTopicWildcard), name, topic);
        public static string Settings_ErrorTopicWhitespace(string name) => Format(nameof(Settings_ErrorTopicWhitespace), name);
        public static string Settings_ErrorRange(string name, int value, int min, int max) => Format(nameof(Settings_ErrorRange), name, value, min, max);
        public static string Settings_ErrorTopicsEqual(string cpuTopic, string memTopic) => Format(nameof(Settings_ErrorTopicsEqual), cpuTopic, memTopic);
        public static string Settings_ErrorStatusTopicCollides(string statusTopic, string cpuTopic, string memTopic) => Format(nameof(Settings_ErrorStatusTopicCollides), statusTopic, cpuTopic, memTopic);
        public static string Settings_ErrorBackoffOrder(string maxBackoff, string initialBackoff) => Format(nameof(Settings_ErrorBackoffOrder), maxBackoff, initialBackoff);

        public static string Observability_WindowTitle => Get(nameof(Observability_WindowTitle));
        public static string Observability_MessageBoxCaption => Get(nameof(Observability_MessageBoxCaption));
        public static string Observability_EnabledCheckBox => Get(nameof(Observability_EnabledCheckBox));
        public static string Observability_TenantPlaceholder => Get(nameof(Observability_TenantPlaceholder));
        public static string Observability_HostLabelCheckBox => Get(nameof(Observability_HostLabelCheckBox));
        public static string Observability_Enabled => Get(nameof(Observability_Enabled));
        public static string Observability_Url => Get(nameof(Observability_Url));
        public static string Observability_AuthMode => Get(nameof(Observability_AuthMode));
        public static string Observability_Username => Get(nameof(Observability_Username));
        public static string Observability_Token => Get(nameof(Observability_Token));
        public static string Observability_Tenant => Get(nameof(Observability_Tenant));
        public static string Observability_HostLabel => Get(nameof(Observability_HostLabel));
        public static string Observability_MetricsInterval => Get(nameof(Observability_MetricsInterval));
        public static string Observability_BatchMaxEntries => Get(nameof(Observability_BatchMaxEntries));
        public static string Observability_QueueCapacity => Get(nameof(Observability_QueueCapacity));
        public static string Observability_FlushInterval => Get(nameof(Observability_FlushInterval));
        public static string Observability_TokenFromEnvironment => Get(nameof(Observability_TokenFromEnvironment));
        public static string Observability_TokenSaved => Get(nameof(Observability_TokenSaved));
        public static string Observability_TokenNotSet => Get(nameof(Observability_TokenNotSet));
        public static string Observability_TokenWillBeCleared => Get(nameof(Observability_TokenWillBeCleared));
        public static string Observability_ClearToken => Get(nameof(Observability_ClearToken));
        public static string Observability_TokenNote => Get(nameof(Observability_TokenNote));
        public static string Observability_Test => Get(nameof(Observability_Test));
        public static string Observability_Testing => Get(nameof(Observability_Testing));
        public static string Observability_TestCancelled => Get(nameof(Observability_TestCancelled));
        public static string Observability_TestNotSent => Get(nameof(Observability_TestNotSent));
        public static string Observability_TestSucceeded(string detail) => Format(nameof(Observability_TestSucceeded), detail);
        public static string Observability_TestFailed(string detail) => Format(nameof(Observability_TestFailed), detail);
        public static string Observability_RequestTimedOut(int seconds) => Format(nameof(Observability_RequestTimedOut), seconds);
        public static string Observability_InsecureSaveQuestion => Get(nameof(Observability_InsecureSaveQuestion));
        public static string Observability_InsecureTestQuestion => Get(nameof(Observability_InsecureTestQuestion));
        public static string Observability_ErrorUrlEmpty(string url) => Format(nameof(Observability_ErrorUrlEmpty), url);
        public static string Observability_ErrorUrlTooLong(string url, int maxLength) => Format(nameof(Observability_ErrorUrlTooLong), url, maxLength);
        public static string Observability_ErrorUrlNotHttp(string url) => Format(nameof(Observability_ErrorUrlNotHttp), url);
        public static string Observability_ErrorUrlUserInfo(string url, string authMode, string username) => Format(nameof(Observability_ErrorUrlUserInfo), url, authMode, username);
        public static string Observability_ErrorUrlFragment(string url) => Format(nameof(Observability_ErrorUrlFragment), url);
        public static string Observability_ErrorUsername(string username, int maxLength) => Format(nameof(Observability_ErrorUsername), username, maxLength);
        public static string Observability_ErrorToken(string token, int maxLength) => Format(nameof(Observability_ErrorToken), token, maxLength);
        public static string Observability_ErrorTenantId(string tenant, int maxLength) => Format(nameof(Observability_ErrorTenantId), tenant, maxLength);
        public static string Observability_ErrorAuthMode(string authMode) => Format(nameof(Observability_ErrorAuthMode), authMode);
        public static string Observability_ErrorBasicNeedsUsername(string authMode, string username) => Format(nameof(Observability_ErrorBasicNeedsUsername), authMode, username);

        public static string Pairing_ReprovisionInstruction => Get(nameof(Pairing_ReprovisionInstruction));
        public static string Pairing_FirstRunBalloonText => Get(nameof(Pairing_FirstRunBalloonText));
        public static string Pairing_WindowTitle => Get(nameof(Pairing_WindowTitle));
        public static string Pairing_Intro(string reprovisionInstruction) => Format(nameof(Pairing_Intro), reprovisionInstruction);
        public static string Pairing_PairButton => Get(nameof(Pairing_PairButton));
        public static string Pairing_DeviceGroup => Get(nameof(Pairing_DeviceGroup));
        public static string Pairing_DeviceColumn => Get(nameof(Pairing_DeviceColumn));
        public static string Pairing_SignalColumn => Get(nameof(Pairing_SignalColumn));
        public static string Pairing_RescanButton => Get(nameof(Pairing_RescanButton));
        public static string Pairing_Scanning => Get(nameof(Pairing_Scanning));
        public static string Pairing_CodeGroup => Get(nameof(Pairing_CodeGroup));
        public static string Pairing_SelectDeviceFirst => Get(nameof(Pairing_SelectDeviceFirst));
        public static string Pairing_CodeReadingUsb => Get(nameof(Pairing_CodeReadingUsb));
        public static string Pairing_CodeReadOverUsb => Get(nameof(Pairing_CodeReadOverUsb));
        public static string Pairing_CodeNotFoundOverUsb => Get(nameof(Pairing_CodeNotFoundOverUsb));
        public static string Pairing_CodeUsbQueryStopped => Get(nameof(Pairing_CodeUsbQueryStopped));
        public static string Pairing_CodeEnterManually => Get(nameof(Pairing_CodeEnterManually));
        public static string Pairing_CodeUseUsb => Get(nameof(Pairing_CodeUseUsb));
        public static string Pairing_CodeHint => Get(nameof(Pairing_CodeHint));
        public static string Pairing_WifiGroup => Get(nameof(Pairing_WifiGroup));
        public static string Pairing_SsidLabel => Get(nameof(Pairing_SsidLabel));
        public static string Pairing_PasswordLabel => Get(nameof(Pairing_PasswordLabel));
        public static string Pairing_ShowPassword => Get(nameof(Pairing_ShowPassword));
        public static string Pairing_WifiNote => Get(nameof(Pairing_WifiNote));
        public static string Pairing_UseThisPcWifi => Get(nameof(Pairing_UseThisPcWifi));
        public static string Pairing_ReadingThisPcWifi => Get(nameof(Pairing_ReadingThisPcWifi));
        public static string Pairing_ThisPcWifiUnavailable => Get(nameof(Pairing_ThisPcWifiUnavailable));
        public static string Pairing_SsidPrefilled => Get(nameof(Pairing_SsidPrefilled));
        public static string Pairing_SsidPrefilledNot24GHz => Get(nameof(Pairing_SsidPrefilledNot24GHz));
        public static string Pairing_ProgressGroup => Get(nameof(Pairing_ProgressGroup));
        public static string Pairing_InProgress => Get(nameof(Pairing_InProgress));
        public static string Pairing_StepConnect => Get(nameof(Pairing_StepConnect));
        public static string Pairing_StepSecure => Get(nameof(Pairing_StepSecure));
        public static string Pairing_StepSend => Get(nameof(Pairing_StepSend));
        public static string Pairing_StepWifi => Get(nameof(Pairing_StepWifi));
        public static string Pairing_StepSave => Get(nameof(Pairing_StepSave));
        public static string Pairing_StepDaemon => Get(nameof(Pairing_StepDaemon));
        public static string Pairing_StepCheckNetwork => Get(nameof(Pairing_StepCheckNetwork));
        public static string Pairing_StepNoAddress => Get(nameof(Pairing_StepNoAddress));
        public static string Pairing_StepSaved(string address) => Format(nameof(Pairing_StepSaved), address);
        public static string Pairing_SaveFailed => Get(nameof(Pairing_SaveFailed));
        public static string Pairing_FallbackOverriddenByEnvironment => Get(nameof(Pairing_FallbackOverriddenByEnvironment));
        public static string Pairing_Succeeded(string address) => Format(nameof(Pairing_Succeeded), address);
        public static string Pairing_SucceededNoAddress(string host) => Format(nameof(Pairing_SucceededNoAddress), host);
        public static string Pairing_AlreadyConnected => Get(nameof(Pairing_AlreadyConnected));
        public static string Pairing_WrongPassword => Get(nameof(Pairing_WrongPassword));
        public static string Pairing_NetworkNotFound => Get(nameof(Pairing_NetworkNotFound));
        public static string Pairing_WifiFailed(int reason) => Format(nameof(Pairing_WifiFailed), reason);
        public static string Pairing_WifiTimeout(int seconds) => Format(nameof(Pairing_WifiTimeout), seconds);
        public static string Pairing_ConnectionLost(int seconds) => Format(nameof(Pairing_ConnectionLost), seconds);
        public static string Pairing_Failed => Get(nameof(Pairing_Failed));
        public static string Pairing_BrokerReachable(string host, int port) => Format(nameof(Pairing_BrokerReachable), host, port);
        public static string Pairing_BrokerUnreachable(string host, int port, int seconds, string reprovisionInstruction) => Format(nameof(Pairing_BrokerUnreachable), host, port, seconds, reprovisionInstruction);
        public static string Pairing_AlreadyOnDifferentNetwork(string joinedSsid, string reprovisionInstruction) => Format(nameof(Pairing_AlreadyOnDifferentNetwork), joinedSsid, reprovisionInstruction);
        public static string Pairing_Cancelled => Get(nameof(Pairing_Cancelled));
        public static string Pairing_ScanNone(string reprovisionInstruction) => Format(nameof(Pairing_ScanNone), reprovisionInstruction);
        public static string Pairing_ScanOne => Get(nameof(Pairing_ScanOne));
        public static string Pairing_ScanMany(int count) => Format(nameof(Pairing_ScanMany), count);
        public static string Pairing_ScanBluetoothOff => Get(nameof(Pairing_ScanBluetoothOff));
        public static string Pairing_ScanBluetoothUnavailable => Get(nameof(Pairing_ScanBluetoothUnavailable));
        public static string Pairing_ScanFailedRescan => Get(nameof(Pairing_ScanFailedRescan));
        public static string Pairing_ScanFailed => Get(nameof(Pairing_ScanFailed));
        public static string Pairing_DaemonConnected => Get(nameof(Pairing_DaemonConnected));
        public static string Pairing_DaemonPaused => Get(nameof(Pairing_DaemonPaused));
        public static string Pairing_DaemonRetrying(string host) => Format(nameof(Pairing_DaemonRetrying), host);

        public static string Provisioning_CodeRejected => Get(nameof(Provisioning_CodeRejected));
        public static string Provisioning_CodeNotVerified => Get(nameof(Provisioning_CodeNotVerified));
        public static string Provisioning_RetryRejectedAfterFailure(string reprovisionInstruction) => Format(nameof(Provisioning_RetryRejectedAfterFailure), reprovisionInstruction);
        public static string Provisioning_RetryRejected(string reprovisionInstruction) => Format(nameof(Provisioning_RetryRejected), reprovisionInstruction);
        public static string Provisioning_BluetoothOff => Get(nameof(Provisioning_BluetoothOff));
        public static string Provisioning_BluetoothUnavailable => Get(nameof(Provisioning_BluetoothUnavailable));
        public static string Provisioning_DeviceNotFound => Get(nameof(Provisioning_DeviceNotFound));
        public static string Provisioning_DeviceDisconnected => Get(nameof(Provisioning_DeviceDisconnected));
        public static string Provisioning_Timeout => Get(nameof(Provisioning_Timeout));
        public static string Provisioning_ServiceNotFound(string reprovisionInstruction) => Format(nameof(Provisioning_ServiceNotFound), reprovisionInstruction);
        public static string Provisioning_GattError => Get(nameof(Provisioning_GattError));
        public static string Provisioning_TransportFailed => Get(nameof(Provisioning_TransportFailed));
        public static string Provisioning_UnexpectedAnswer => Get(nameof(Provisioning_UnexpectedAnswer));
        public static string Provisioning_SsidLength(int maxBytes) => Format(nameof(Provisioning_SsidLength), maxBytes);
        public static string Provisioning_PasswordLength(int maxBytes) => Format(nameof(Provisioning_PasswordLength), maxBytes);
        public static string Provisioning_PairingCodeFormat => Get(nameof(Provisioning_PairingCodeFormat));
        public static string Provisioning_InvalidInput => Get(nameof(Provisioning_InvalidInput));
        public static string Provisioning_FailedWithType(string exceptionType) => Format(nameof(Provisioning_FailedWithType), exceptionType);
    }
}
