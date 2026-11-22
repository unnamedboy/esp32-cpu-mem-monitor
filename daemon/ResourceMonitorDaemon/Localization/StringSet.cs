using System.Globalization;

namespace ResourceMonitorDaemon.Localization
{
    /// <summary>
    /// The <see cref="Strings"/> texts bound to one culture. A dialog takes one
    /// (<see cref="Strings.Current"/>) when it is created and reads every text through it, so it stays
    /// in that language even if the UI language changes while it is open. Same members as
    /// <see cref="Strings"/> (checked by LocalizationTests).
    /// </summary>
    public sealed class StringSet
    {
        public StringSet(CultureInfo culture)
        {
            Culture = culture;
        }

        /// <summary>The culture every text of this set is looked up and formatted in.</summary>
        public CultureInfo Culture { get; }

        private string Get(string key) => Strings.Get(key, Culture);

        private string Format(string key, params object?[] args) => Strings.Format(Culture, key, args);

        public string Common_ProductName => Get(nameof(Strings.Common_ProductName));
        public string Common_Save => Get(nameof(Strings.Common_Save));
        public string Common_Cancel => Get(nameof(Strings.Common_Cancel));
        public string Common_Close => Get(nameof(Strings.Common_Close));
        public string Common_None => Get(nameof(Strings.Common_None));
        public string Common_FieldLabel(string label) => Format(nameof(Strings.Common_FieldLabel), label);
        public string Common_EnvFieldLabel(string label) => Format(nameof(Strings.Common_EnvFieldLabel), label);
        public string Common_SavedTo(string path) => Format(nameof(Strings.Common_SavedTo), path);
        public string Common_EnvironmentFieldsNote(string fields) => Format(nameof(Strings.Common_EnvironmentFieldsNote), fields);
        public string Common_InvalidSettingsCaption => Get(nameof(Strings.Common_InvalidSettingsCaption));
        public string Common_SaveFailed(string path) => Format(nameof(Strings.Common_SaveFailed), path);
        public string Common_SaveFailedInvalidFile(string path) => Format(nameof(Strings.Common_SaveFailedInvalidFile), path);
        public string Common_StartupFailed(string error, string logPath) => Format(nameof(Strings.Common_StartupFailed), error, logPath);

        public string Tray_Pause => Get(nameof(Strings.Tray_Pause));
        public string Tray_Resume => Get(nameof(Strings.Tray_Resume));
        public string Tray_Settings => Get(nameof(Strings.Tray_Settings));
        public string Tray_PairDevice => Get(nameof(Strings.Tray_PairDevice));
        public string Tray_ViewLog => Get(nameof(Strings.Tray_ViewLog));
        public string Tray_Observability => Get(nameof(Strings.Tray_Observability));
        public string Tray_Exit => Get(nameof(Strings.Tray_Exit));
        public string Tray_PausedTitle => Get(nameof(Strings.Tray_PausedTitle));
        public string Tray_PausedText => Get(nameof(Strings.Tray_PausedText));
        public string Tray_ResumedTitle => Get(nameof(Strings.Tray_ResumedTitle));
        public string Tray_ResumedText => Get(nameof(Strings.Tray_ResumedText));
        public string Tray_SettingsSavedTitle => Get(nameof(Strings.Tray_SettingsSavedTitle));
        public string Tray_ObservabilitySavedTitle => Get(nameof(Strings.Tray_ObservabilitySavedTitle));
        public string Tray_AppliedWithoutRestart => Get(nameof(Strings.Tray_AppliedWithoutRestart));
        public string Tray_AppliedEnvironmentOverride(string fields) => Format(nameof(Strings.Tray_AppliedEnvironmentOverride), fields);
        public string Tray_NoLogYet => Get(nameof(Strings.Tray_NoLogYet));
        public string Tray_MqttFailedTitle => Get(nameof(Strings.Tray_MqttFailedTitle));
        public string Tray_MqttFailedText(object endpoint) => Format(nameof(Strings.Tray_MqttFailedText), endpoint);
        public string Tray_MqttEndpoint(string host, int port) => Format(nameof(Strings.Tray_MqttEndpoint), host, port);
        public string Tray_MqttEndpointWithFallback(string host, int port, string fallback) => Format(nameof(Strings.Tray_MqttEndpointWithFallback), host, port, fallback);
        public string Tray_MqttReconnectedTitle => Get(nameof(Strings.Tray_MqttReconnectedTitle));
        public string Tray_MqttReconnectedText(string address, int port) => Format(nameof(Strings.Tray_MqttReconnectedText), address, port);
        public string Tray_MqttReconnectedViaFallback(string address, int port, string host) => Format(nameof(Strings.Tray_MqttReconnectedViaFallback), address, port, host);

        public string Settings_WindowTitle => Get(nameof(Strings.Settings_WindowTitle));
        public string Settings_Host => Get(nameof(Strings.Settings_Host));
        public string Settings_Port => Get(nameof(Strings.Settings_Port));
        public string Settings_CpuTopic => Get(nameof(Strings.Settings_CpuTopic));
        public string Settings_MemTopic => Get(nameof(Strings.Settings_MemTopic));
        public string Settings_SampleInterval => Get(nameof(Strings.Settings_SampleInterval));
        public string Settings_StatusTopic => Get(nameof(Strings.Settings_StatusTopic));
        public string Settings_ReconnectInitialBackoff => Get(nameof(Strings.Settings_ReconnectInitialBackoff));
        public string Settings_ReconnectMaxBackoff => Get(nameof(Strings.Settings_ReconnectMaxBackoff));
        public string Settings_ConnectTimeout => Get(nameof(Strings.Settings_ConnectTimeout));
        public string Settings_FallbackAddress => Get(nameof(Strings.Settings_FallbackAddress));
        public string Settings_Language => Get(nameof(Strings.Settings_Language));
        public string Settings_LanguageAutomatic => Get(nameof(Strings.Settings_LanguageAutomatic));
        public string Settings_LanguageEnglish => Get(nameof(Strings.Settings_LanguageEnglish));
        public string Settings_LanguageChineseSimplified => Get(nameof(Strings.Settings_LanguageChineseSimplified));
        public string Settings_LanguageNote => Get(nameof(Strings.Settings_LanguageNote));
        public string Settings_ErrorEmpty(string name) => Format(nameof(Strings.Settings_ErrorEmpty), name);
        public string Settings_ErrorHostInvalid(string name, string host) => Format(nameof(Strings.Settings_ErrorHostInvalid), name, host);
        public string Settings_ErrorFallbackInvalid(string name, string address) => Format(nameof(Strings.Settings_ErrorFallbackInvalid), name, address);
        public string Settings_ErrorTopicTooLong(string name, int maxLength) => Format(nameof(Strings.Settings_ErrorTopicTooLong), name, maxLength);
        public string Settings_ErrorTopicWildcard(string name, string topic) => Format(nameof(Strings.Settings_ErrorTopicWildcard), name, topic);
        public string Settings_ErrorTopicWhitespace(string name) => Format(nameof(Strings.Settings_ErrorTopicWhitespace), name);
        public string Settings_ErrorRange(string name, int value, int min, int max) => Format(nameof(Strings.Settings_ErrorRange), name, value, min, max);
        public string Settings_ErrorTopicsEqual(string cpuTopic, string memTopic) => Format(nameof(Strings.Settings_ErrorTopicsEqual), cpuTopic, memTopic);
        public string Settings_ErrorStatusTopicCollides(string statusTopic, string cpuTopic, string memTopic) => Format(nameof(Strings.Settings_ErrorStatusTopicCollides), statusTopic, cpuTopic, memTopic);
        public string Settings_ErrorBackoffOrder(string maxBackoff, string initialBackoff) => Format(nameof(Strings.Settings_ErrorBackoffOrder), maxBackoff, initialBackoff);

        public string Observability_WindowTitle => Get(nameof(Strings.Observability_WindowTitle));
        public string Observability_MessageBoxCaption => Get(nameof(Strings.Observability_MessageBoxCaption));
        public string Observability_EnabledCheckBox => Get(nameof(Strings.Observability_EnabledCheckBox));
        public string Observability_TenantPlaceholder => Get(nameof(Strings.Observability_TenantPlaceholder));
        public string Observability_HostLabelCheckBox => Get(nameof(Strings.Observability_HostLabelCheckBox));
        public string Observability_Enabled => Get(nameof(Strings.Observability_Enabled));
        public string Observability_Url => Get(nameof(Strings.Observability_Url));
        public string Observability_AuthMode => Get(nameof(Strings.Observability_AuthMode));
        public string Observability_Username => Get(nameof(Strings.Observability_Username));
        public string Observability_Token => Get(nameof(Strings.Observability_Token));
        public string Observability_Tenant => Get(nameof(Strings.Observability_Tenant));
        public string Observability_HostLabel => Get(nameof(Strings.Observability_HostLabel));
        public string Observability_MetricsInterval => Get(nameof(Strings.Observability_MetricsInterval));
        public string Observability_BatchMaxEntries => Get(nameof(Strings.Observability_BatchMaxEntries));
        public string Observability_QueueCapacity => Get(nameof(Strings.Observability_QueueCapacity));
        public string Observability_FlushInterval => Get(nameof(Strings.Observability_FlushInterval));
        public string Observability_TokenFromEnvironment => Get(nameof(Strings.Observability_TokenFromEnvironment));
        public string Observability_TokenSaved => Get(nameof(Strings.Observability_TokenSaved));
        public string Observability_TokenNotSet => Get(nameof(Strings.Observability_TokenNotSet));
        public string Observability_TokenWillBeCleared => Get(nameof(Strings.Observability_TokenWillBeCleared));
        public string Observability_ClearToken => Get(nameof(Strings.Observability_ClearToken));
        public string Observability_TokenNote => Get(nameof(Strings.Observability_TokenNote));
        public string Observability_Test => Get(nameof(Strings.Observability_Test));
        public string Observability_Testing => Get(nameof(Strings.Observability_Testing));
        public string Observability_TestCancelled => Get(nameof(Strings.Observability_TestCancelled));
        public string Observability_TestNotSent => Get(nameof(Strings.Observability_TestNotSent));
        public string Observability_TestSucceeded(string detail) => Format(nameof(Strings.Observability_TestSucceeded), detail);
        public string Observability_TestFailed(string detail) => Format(nameof(Strings.Observability_TestFailed), detail);
        public string Observability_RequestTimedOut(int seconds) => Format(nameof(Strings.Observability_RequestTimedOut), seconds);
        public string Observability_InsecureSaveQuestion => Get(nameof(Strings.Observability_InsecureSaveQuestion));
        public string Observability_InsecureTestQuestion => Get(nameof(Strings.Observability_InsecureTestQuestion));
        public string Observability_ErrorUrlEmpty(string url) => Format(nameof(Strings.Observability_ErrorUrlEmpty), url);
        public string Observability_ErrorUrlTooLong(string url, int maxLength) => Format(nameof(Strings.Observability_ErrorUrlTooLong), url, maxLength);
        public string Observability_ErrorUrlNotHttp(string url) => Format(nameof(Strings.Observability_ErrorUrlNotHttp), url);
        public string Observability_ErrorUrlUserInfo(string url, string authMode, string username) => Format(nameof(Strings.Observability_ErrorUrlUserInfo), url, authMode, username);
        public string Observability_ErrorUrlFragment(string url) => Format(nameof(Strings.Observability_ErrorUrlFragment), url);
        public string Observability_ErrorUsername(string username, int maxLength) => Format(nameof(Strings.Observability_ErrorUsername), username, maxLength);
        public string Observability_ErrorToken(string token, int maxLength) => Format(nameof(Strings.Observability_ErrorToken), token, maxLength);
        public string Observability_ErrorTenantId(string tenant, int maxLength) => Format(nameof(Strings.Observability_ErrorTenantId), tenant, maxLength);
        public string Observability_ErrorAuthMode(string authMode) => Format(nameof(Strings.Observability_ErrorAuthMode), authMode);
        public string Observability_ErrorBasicNeedsUsername(string authMode, string username) => Format(nameof(Strings.Observability_ErrorBasicNeedsUsername), authMode, username);

        public string Pairing_ReprovisionInstruction => Get(nameof(Strings.Pairing_ReprovisionInstruction));
        public string Pairing_FirstRunBalloonText => Get(nameof(Strings.Pairing_FirstRunBalloonText));
        public string Pairing_WindowTitle => Get(nameof(Strings.Pairing_WindowTitle));
        public string Pairing_Intro(string reprovisionInstruction) => Format(nameof(Strings.Pairing_Intro), reprovisionInstruction);
        public string Pairing_PairButton => Get(nameof(Strings.Pairing_PairButton));
        public string Pairing_DeviceGroup => Get(nameof(Strings.Pairing_DeviceGroup));
        public string Pairing_DeviceColumn => Get(nameof(Strings.Pairing_DeviceColumn));
        public string Pairing_SignalColumn => Get(nameof(Strings.Pairing_SignalColumn));
        public string Pairing_RescanButton => Get(nameof(Strings.Pairing_RescanButton));
        public string Pairing_Scanning => Get(nameof(Strings.Pairing_Scanning));
        public string Pairing_CodeGroup => Get(nameof(Strings.Pairing_CodeGroup));
        public string Pairing_SelectDeviceFirst => Get(nameof(Strings.Pairing_SelectDeviceFirst));
        public string Pairing_CodeReadingUsb => Get(nameof(Strings.Pairing_CodeReadingUsb));
        public string Pairing_CodeReadOverUsb => Get(nameof(Strings.Pairing_CodeReadOverUsb));
        public string Pairing_CodeNotFoundOverUsb => Get(nameof(Strings.Pairing_CodeNotFoundOverUsb));
        public string Pairing_CodeUsbQueryStopped => Get(nameof(Strings.Pairing_CodeUsbQueryStopped));
        public string Pairing_CodeEnterManually => Get(nameof(Strings.Pairing_CodeEnterManually));
        public string Pairing_CodeUseUsb => Get(nameof(Strings.Pairing_CodeUseUsb));
        public string Pairing_CodeHint => Get(nameof(Strings.Pairing_CodeHint));
        public string Pairing_WifiGroup => Get(nameof(Strings.Pairing_WifiGroup));
        public string Pairing_SsidLabel => Get(nameof(Strings.Pairing_SsidLabel));
        public string Pairing_PasswordLabel => Get(nameof(Strings.Pairing_PasswordLabel));
        public string Pairing_ShowPassword => Get(nameof(Strings.Pairing_ShowPassword));
        public string Pairing_WifiNote => Get(nameof(Strings.Pairing_WifiNote));
        public string Pairing_UseThisPcWifi => Get(nameof(Strings.Pairing_UseThisPcWifi));
        public string Pairing_ReadingThisPcWifi => Get(nameof(Strings.Pairing_ReadingThisPcWifi));
        public string Pairing_ThisPcWifiUnavailable => Get(nameof(Strings.Pairing_ThisPcWifiUnavailable));
        public string Pairing_SsidPrefilled => Get(nameof(Strings.Pairing_SsidPrefilled));
        public string Pairing_SsidPrefilledNot24GHz => Get(nameof(Strings.Pairing_SsidPrefilledNot24GHz));
        public string Pairing_ProgressGroup => Get(nameof(Strings.Pairing_ProgressGroup));
        public string Pairing_InProgress => Get(nameof(Strings.Pairing_InProgress));
        public string Pairing_StepConnect => Get(nameof(Strings.Pairing_StepConnect));
        public string Pairing_StepSecure => Get(nameof(Strings.Pairing_StepSecure));
        public string Pairing_StepSend => Get(nameof(Strings.Pairing_StepSend));
        public string Pairing_StepWifi => Get(nameof(Strings.Pairing_StepWifi));
        public string Pairing_StepSave => Get(nameof(Strings.Pairing_StepSave));
        public string Pairing_StepDaemon => Get(nameof(Strings.Pairing_StepDaemon));
        public string Pairing_StepCheckNetwork => Get(nameof(Strings.Pairing_StepCheckNetwork));
        public string Pairing_StepNoAddress => Get(nameof(Strings.Pairing_StepNoAddress));
        public string Pairing_StepSaved(string address) => Format(nameof(Strings.Pairing_StepSaved), address);
        public string Pairing_SaveFailed => Get(nameof(Strings.Pairing_SaveFailed));
        public string Pairing_FallbackOverriddenByEnvironment => Get(nameof(Strings.Pairing_FallbackOverriddenByEnvironment));
        public string Pairing_Succeeded(string address) => Format(nameof(Strings.Pairing_Succeeded), address);
        public string Pairing_SucceededNoAddress(string host) => Format(nameof(Strings.Pairing_SucceededNoAddress), host);
        public string Pairing_AlreadyConnected => Get(nameof(Strings.Pairing_AlreadyConnected));
        public string Pairing_WrongPassword => Get(nameof(Strings.Pairing_WrongPassword));
        public string Pairing_NetworkNotFound => Get(nameof(Strings.Pairing_NetworkNotFound));
        public string Pairing_WifiFailed(int reason) => Format(nameof(Strings.Pairing_WifiFailed), reason);
        public string Pairing_WifiTimeout(int seconds) => Format(nameof(Strings.Pairing_WifiTimeout), seconds);
        public string Pairing_ConnectionLost(int seconds) => Format(nameof(Strings.Pairing_ConnectionLost), seconds);
        public string Pairing_Failed => Get(nameof(Strings.Pairing_Failed));
        public string Pairing_BrokerReachable(string host, int port) => Format(nameof(Strings.Pairing_BrokerReachable), host, port);
        public string Pairing_BrokerUnreachable(string host, int port, int seconds, string reprovisionInstruction) => Format(nameof(Strings.Pairing_BrokerUnreachable), host, port, seconds, reprovisionInstruction);
        public string Pairing_AlreadyOnDifferentNetwork(string joinedSsid, string reprovisionInstruction) => Format(nameof(Strings.Pairing_AlreadyOnDifferentNetwork), joinedSsid, reprovisionInstruction);
        public string Pairing_Cancelled => Get(nameof(Strings.Pairing_Cancelled));
        public string Pairing_ScanNone(string reprovisionInstruction) => Format(nameof(Strings.Pairing_ScanNone), reprovisionInstruction);
        public string Pairing_ScanOne => Get(nameof(Strings.Pairing_ScanOne));
        public string Pairing_ScanMany(int count) => Format(nameof(Strings.Pairing_ScanMany), count);
        public string Pairing_ScanBluetoothOff => Get(nameof(Strings.Pairing_ScanBluetoothOff));
        public string Pairing_ScanBluetoothUnavailable => Get(nameof(Strings.Pairing_ScanBluetoothUnavailable));
        public string Pairing_ScanFailedRescan => Get(nameof(Strings.Pairing_ScanFailedRescan));
        public string Pairing_ScanFailed => Get(nameof(Strings.Pairing_ScanFailed));
        public string Pairing_DaemonConnected => Get(nameof(Strings.Pairing_DaemonConnected));
        public string Pairing_DaemonPaused => Get(nameof(Strings.Pairing_DaemonPaused));
        public string Pairing_DaemonRetrying(string host) => Format(nameof(Strings.Pairing_DaemonRetrying), host);

        public string Provisioning_CodeRejected => Get(nameof(Strings.Provisioning_CodeRejected));
        public string Provisioning_CodeNotVerified => Get(nameof(Strings.Provisioning_CodeNotVerified));
        public string Provisioning_RetryRejectedAfterFailure(string reprovisionInstruction) => Format(nameof(Strings.Provisioning_RetryRejectedAfterFailure), reprovisionInstruction);
        public string Provisioning_RetryRejected(string reprovisionInstruction) => Format(nameof(Strings.Provisioning_RetryRejected), reprovisionInstruction);
        public string Provisioning_BluetoothOff => Get(nameof(Strings.Provisioning_BluetoothOff));
        public string Provisioning_BluetoothUnavailable => Get(nameof(Strings.Provisioning_BluetoothUnavailable));
        public string Provisioning_DeviceNotFound => Get(nameof(Strings.Provisioning_DeviceNotFound));
        public string Provisioning_DeviceDisconnected => Get(nameof(Strings.Provisioning_DeviceDisconnected));
        public string Provisioning_Timeout => Get(nameof(Strings.Provisioning_Timeout));
        public string Provisioning_ServiceNotFound(string reprovisionInstruction) => Format(nameof(Strings.Provisioning_ServiceNotFound), reprovisionInstruction);
        public string Provisioning_GattError => Get(nameof(Strings.Provisioning_GattError));
        public string Provisioning_TransportFailed => Get(nameof(Strings.Provisioning_TransportFailed));
        public string Provisioning_UnexpectedAnswer => Get(nameof(Strings.Provisioning_UnexpectedAnswer));
        public string Provisioning_SsidLength(int maxBytes) => Format(nameof(Strings.Provisioning_SsidLength), maxBytes);
        public string Provisioning_PasswordLength(int maxBytes) => Format(nameof(Strings.Provisioning_PasswordLength), maxBytes);
        public string Provisioning_PairingCodeFormat => Get(nameof(Strings.Provisioning_PairingCodeFormat));
        public string Provisioning_InvalidInput => Get(nameof(Strings.Provisioning_InvalidInput));
        public string Provisioning_FailedWithType(string exceptionType) => Format(nameof(Strings.Provisioning_FailedWithType), exceptionType);
    }
}
