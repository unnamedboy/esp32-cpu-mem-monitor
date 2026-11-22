using ResourceMonitorDaemon.Localization;

namespace ResourceMonitorDaemon.Observability
{
    /// <summary>URL rules for the Loki push endpoint. Nothing here ever returns credentials.</summary>
    public static class LokiUrl
    {
        public const int MaxLength = 2048;

        /// <summary>
        /// Returns null when <paramref name="url"/> is an absolute http(s) URL without user info
        /// or fragment; otherwise an English message that does not repeat the URL.
        /// </summary>
        public static string? Validate(string? url) => Problem(url)?.English;

        /// <summary>Same rule as <see cref="Validate"/>, as a message the dialog can show localized.</summary>
        public static UiMessage? Problem(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return UiMessage.Observability_ErrorUrlEmpty(LokiSettingsFields.Url);
            }

            var trimmed = url.Trim();
            if (trimmed.Length > MaxLength)
            {
                return UiMessage.Observability_ErrorUrlTooLong(LokiSettingsFields.Url, MaxLength);
            }

            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || string.IsNullOrEmpty(uri.Host))
            {
                return UiMessage.Observability_ErrorUrlNotHttp(LokiSettingsFields.Url);
            }

            if (uri.UserInfo.Length > 0)
            {
                return UiMessage.Observability_ErrorUrlUserInfo(LokiSettingsFields.Url, LokiSettingsFields.AuthMode, LokiSettingsFields.Username);
            }

            if (uri.Fragment.Length > 0)
            {
                return UiMessage.Observability_ErrorUrlFragment(LokiSettingsFields.Url);
            }

            return null;
        }

        /// <summary>
        /// Removes <c>user:password@</c> from an absolute URL. Returns the input unchanged when
        /// it has no user info or cannot be parsed.
        /// </summary>
        public static string StripUserInfo(string url)
        {
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.UserInfo.Length == 0)
            {
                return url;
            }

            return new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri.AbsoluteUri;
        }

        /// <summary>
        /// True when <paramref name="settings"/> would send a token (Basic or Bearer) over plain
        /// <c>http://</c>.
        /// </summary>
        public static bool SendsCredentialsInsecurely(LokiSettings settings) =>
            settings.AuthMode != LokiAuthMode.None && settings.HasToken
            && Uri.TryCreate(settings.Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp;

        /// <summary>True when the absolute URL carries user info.</summary>
        public static bool HasUserInfo(string url) =>
            Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && uri.UserInfo.Length > 0;

        /// <summary>
        /// The push endpoint: <see cref="LokiSettings.PushPath"/> is appended only when the URL has
        /// no path (or just "/"); an explicit path (reverse proxy, full push URL) is kept as is.
        /// User info is always removed.
        /// </summary>
        public static Uri BuildPushUri(string url)
        {
            var uri = new Uri(StripUserInfo(url.Trim()), UriKind.Absolute);
            if (uri.AbsolutePath is "" or "/")
            {
                uri = new UriBuilder(uri) { Path = LokiSettings.PushPath }.Uri;
            }

            return uri;
        }

        /// <summary>Host and non-default port only, e.g. <c>logs.example.net</c> or <c>loki:3100</c>; "(none)" when unset.</summary>
        public static string DescribeHost(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return "(none)";
            }

            // Uri.Authority never includes user info.
            return Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && uri.Host.Length > 0
                ? uri.Authority
                : "(invalid)";
        }
    }
}
