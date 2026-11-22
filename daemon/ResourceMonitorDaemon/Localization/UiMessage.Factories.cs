namespace ResourceMonitorDaemon.Localization
{
    /// <summary>
    /// Typed factories, one per message key that is both logged and shown: the parameters match the
    /// placeholders of the resource text, so the argument count is checked by the compiler
    /// (LocalizationTests checks the factories against the resources). A <see cref="UiField"/>
    /// argument is shown by its display name and logged by its configuration key.
    /// </summary>
    public sealed partial class UiMessage
    {
        public static UiMessage Tray_MqttFailedTitle() => new(nameof(Strings.Tray_MqttFailedTitle));
        public static UiMessage Tray_MqttFailedText(object endpoint) => new(nameof(Strings.Tray_MqttFailedText), endpoint);
        public static UiMessage Tray_MqttEndpoint(string host, int port) => new(nameof(Strings.Tray_MqttEndpoint), host, port);
        public static UiMessage Tray_MqttEndpointWithFallback(string host, int port, string fallback) => new(nameof(Strings.Tray_MqttEndpointWithFallback), host, port, fallback);
        public static UiMessage Tray_MqttReconnectedTitle() => new(nameof(Strings.Tray_MqttReconnectedTitle));
        public static UiMessage Tray_MqttReconnectedText(string address, int port) => new(nameof(Strings.Tray_MqttReconnectedText), address, port);
        public static UiMessage Tray_MqttReconnectedViaFallback(string address, int port, string host) => new(nameof(Strings.Tray_MqttReconnectedViaFallback), address, port, host);
        public static UiMessage Settings_ErrorEmpty(UiField name) => new(nameof(Strings.Settings_ErrorEmpty), name);
        public static UiMessage Settings_ErrorHostInvalid(UiField name, string host) => new(nameof(Strings.Settings_ErrorHostInvalid), name, host);
        public static UiMessage Settings_ErrorFallbackInvalid(UiField name, string address) => new(nameof(Strings.Settings_ErrorFallbackInvalid), name, address);
        public static UiMessage Settings_ErrorTopicTooLong(UiField name, int maxLength) => new(nameof(Strings.Settings_ErrorTopicTooLong), name, maxLength);
        public static UiMessage Settings_ErrorTopicWildcard(UiField name, string topic) => new(nameof(Strings.Settings_ErrorTopicWildcard), name, topic);
        public static UiMessage Settings_ErrorTopicWhitespace(UiField name) => new(nameof(Strings.Settings_ErrorTopicWhitespace), name);
        public static UiMessage Settings_ErrorRange(UiField name, int value, int min, int max) => new(nameof(Strings.Settings_ErrorRange), name, value, min, max);
        public static UiMessage Settings_ErrorTopicsEqual(UiField cpuTopic, UiField memTopic) => new(nameof(Strings.Settings_ErrorTopicsEqual), cpuTopic, memTopic);
        public static UiMessage Settings_ErrorStatusTopicCollides(UiField statusTopic, UiField cpuTopic, UiField memTopic) => new(nameof(Strings.Settings_ErrorStatusTopicCollides), statusTopic, cpuTopic, memTopic);
        public static UiMessage Settings_ErrorBackoffOrder(UiField maxBackoff, UiField initialBackoff) => new(nameof(Strings.Settings_ErrorBackoffOrder), maxBackoff, initialBackoff);
        public static UiMessage Observability_RequestTimedOut(int seconds) => new(nameof(Strings.Observability_RequestTimedOut), seconds);
        public static UiMessage Observability_ErrorUrlEmpty(UiField url) => new(nameof(Strings.Observability_ErrorUrlEmpty), url);
        public static UiMessage Observability_ErrorUrlTooLong(UiField url, int maxLength) => new(nameof(Strings.Observability_ErrorUrlTooLong), url, maxLength);
        public static UiMessage Observability_ErrorUrlNotHttp(UiField url) => new(nameof(Strings.Observability_ErrorUrlNotHttp), url);
        public static UiMessage Observability_ErrorUrlUserInfo(UiField url, UiField authMode, UiField username) => new(nameof(Strings.Observability_ErrorUrlUserInfo), url, authMode, username);
        public static UiMessage Observability_ErrorUrlFragment(UiField url) => new(nameof(Strings.Observability_ErrorUrlFragment), url);
        public static UiMessage Observability_ErrorUsername(UiField username, int maxLength) => new(nameof(Strings.Observability_ErrorUsername), username, maxLength);
        public static UiMessage Observability_ErrorToken(UiField token, int maxLength) => new(nameof(Strings.Observability_ErrorToken), token, maxLength);
        public static UiMessage Observability_ErrorTenantId(UiField tenant, int maxLength) => new(nameof(Strings.Observability_ErrorTenantId), tenant, maxLength);
        public static UiMessage Observability_ErrorAuthMode(UiField authMode) => new(nameof(Strings.Observability_ErrorAuthMode), authMode);
        public static UiMessage Observability_ErrorBasicNeedsUsername(UiField authMode, UiField username) => new(nameof(Strings.Observability_ErrorBasicNeedsUsername), authMode, username);
    }
}
