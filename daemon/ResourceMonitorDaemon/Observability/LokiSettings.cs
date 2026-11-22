using Microsoft.Extensions.Logging;

namespace ResourceMonitorDaemon.Observability
{
    /// <summary>How the daemon authenticates against the Loki push endpoint.</summary>
    public enum LokiAuthMode
    {
        /// <summary>No <c>Authorization</c> header (e.g. a local Loki on the LAN).</summary>
        None,

        /// <summary><c>Authorization: Basic base64(Username:Token)</c> (e.g. Grafana Cloud: user = instance id, token = access policy token).</summary>
        Basic,

        /// <summary><c>Authorization: Bearer Token</c>.</summary>
        Bearer,
    }

    /// <summary>Where the effective secret token came from.</summary>
    public enum LokiTokenSource
    {
        None,

        /// <summary>DPAPI (CurrentUser) ciphertext stored as <c>TokenProtected</c> in the user settings file.</summary>
        Protected,

        /// <summary>The <c>RESMON_Observability__Loki__Token</c> environment variable (never persisted).</summary>
        Environment,
    }

    /// <summary>
    /// Optional Grafana Loki sink settings (configuration section <c>Observability:Loki</c>).
    /// Disabled by default; every user-specific value (URL, user, tenant, token) is supplied by the
    /// user and never shipped in the repository.
    /// </summary>
    /// <remarks>
    /// <see cref="Token"/> is the resolved plain secret held in memory only. It is never read from a
    /// JSON file in plain text, never persisted, and excluded from <see cref="ToString"/> and
    /// <see cref="Describe"/>. Record equality includes it, so a token change is detected as a
    /// settings change.
    /// </remarks>
    public sealed record LokiSettings
    {
        public const string SectionName = "Observability:Loki";

        /// <summary>Configuration key (inside the section) of the DPAPI-protected token.</summary>
        public const string TokenProtectedKey = "TokenProtected";

        /// <summary>Configuration key (inside the section) of the plain token; honoured only from the environment.</summary>
        public const string TokenKey = "Token";

        /// <summary>Environment variable that supplies the token for automation (takes precedence, never persisted).</summary>
        public const string TokenEnvironmentVariable = "RESMON_Observability__Loki__Token";

        /// <summary>Path appended to <see cref="Url"/> when it has no path.</summary>
        public const string PushPath = "/loki/api/v1/push";

        public const int DefaultMetricsIntervalSeconds = 10;
        public const int DefaultBatchMaxEntries = 500;
        public const int DefaultQueueCapacity = 5000;
        public const int DefaultFlushIntervalMs = 2000;

        public bool Enabled { get; set; }

        /// <summary>
        /// Base URL such as <c>https://logs-prod-xxx.grafana.net</c> or <c>http://loki:3100</c>.
        /// <see cref="PushPath"/> is appended when the URL has no path; never contains user info.
        /// </summary>
        public string Url { get; set; } = string.Empty;

        public LokiAuthMode AuthMode { get; set; } = LokiAuthMode.None;

        /// <summary>User name for <see cref="LokiAuthMode.Basic"/>.</summary>
        public string Username { get; set; } = string.Empty;

        /// <summary>Resolved secret (password / API token). In memory only; never logged.</summary>
        public string Token { get; set; } = string.Empty;

        public LokiTokenSource TokenSource { get; set; } = LokiTokenSource.None;

        /// <summary>Optional tenant sent as <c>X-Scope-OrgID</c>; empty = no header.</summary>
        public string TenantId { get; set; } = string.Empty;

        /// <summary>Adds <c>host="&lt;machine name&gt;"</c> to every stream.</summary>
        public bool IncludeHostLabel { get; set; }

        /// <summary>Seconds between metrics lines; 0 disables the metrics stream.</summary>
        public int MetricsIntervalSeconds { get; set; } = DefaultMetricsIntervalSeconds;

        /// <summary>Minimum level of daemon log lines forwarded to Loki.</summary>
        public LogLevel MinLevel { get; set; } = LogLevel.Information;

        public int BatchMaxEntries { get; set; } = DefaultBatchMaxEntries;

        public int QueueCapacity { get; set; } = DefaultQueueCapacity;

        public int FlushIntervalMs { get; set; } = DefaultFlushIntervalMs;

        /// <summary>Gzip the request body (<c>Content-Encoding: gzip</c>).</summary>
        public bool Gzip { get; set; } = true;

        /// <summary>True when entries are collected and pushed (enabled with a usable URL).</summary>
        public bool IsActive => Enabled && Url.Length > 0;

        /// <summary>True when a secret token is available.</summary>
        public bool HasToken => Token.Length > 0;

        /// <summary>Host (and non-default port) of <see cref="Url"/> only: no path, query or credentials.</summary>
        public string UrlHost => LokiUrl.DescribeHost(Url);

        /// <summary>Single-line description for diagnostics. Contains no secrets and no URL path.</summary>
        public string Describe() =>
            $"enabled={(Enabled ? "yes" : "no")}, url host={UrlHost}, auth={AuthMode}, " +
            $"token configured={(HasToken ? $"yes ({TokenSource})" : "no")}, tenant={(TenantId.Length > 0 ? "set" : "none")}, " +
            $"hostLabel={(IncludeHostLabel ? "yes" : "no")}, metricsIntervalS={MetricsIntervalSeconds}, minLevel={MinLevel}, " +
            $"batch={BatchMaxEntries}, queue={QueueCapacity}, flushMs={FlushIntervalMs}, gzip={(Gzip ? "yes" : "no")}";

        /// <summary>Never prints <see cref="Token"/> (overrides the record's generated member dump).</summary>
        public override string ToString() => $"LokiSettings {{ {Describe()} }}";
    }
}
