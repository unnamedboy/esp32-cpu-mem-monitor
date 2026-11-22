using Microsoft.Extensions.Logging;

namespace ResourceMonitorDaemon.Observability
{
    /// <summary>The fixed, low-cardinality streams the daemon writes (see docs/protocol.md).</summary>
    public enum LokiStreamKind
    {
        /// <summary><c>{app="resmon-daemon", kind="log", level="&lt;lvl&gt;"}</c></summary>
        Log,

        /// <summary><c>{app="resmon-daemon", kind="metrics"}</c></summary>
        Metrics,

        /// <summary><c>{app="resmon-device", kind="status"}</c></summary>
        Status,
    }

    /// <summary>One log line waiting to be pushed. <see cref="Level"/> is set for <see cref="LokiStreamKind.Log"/> only.</summary>
    public readonly record struct LokiEntry(LokiStreamKind Kind, string? Level, long TimestampNs, string Line);

    /// <summary>Label sets of the streams. Only constant values plus an optional host label.</summary>
    public static class LokiLabels
    {
        public const string DaemonApp = "resmon-daemon";
        public const string DeviceApp = "resmon-device";

        /// <summary>
        /// Labels for an entry, in a stable order. <paramref name="host"/> is added as
        /// <c>host</c> when not null (IncludeHostLabel).
        /// </summary>
        public static IReadOnlyList<KeyValuePair<string, string>> For(LokiStreamKind kind, string? level, string? host)
        {
            var labels = new List<KeyValuePair<string, string>>(4);
            switch (kind)
            {
                case LokiStreamKind.Log:
                    labels.Add(new("app", DaemonApp));
                    labels.Add(new("kind", "log"));
                    labels.Add(new("level", level ?? "info"));
                    break;
                case LokiStreamKind.Metrics:
                    labels.Add(new("app", DaemonApp));
                    labels.Add(new("kind", "metrics"));
                    break;
                default:
                    labels.Add(new("app", DeviceApp));
                    labels.Add(new("kind", "status"));
                    break;
            }

            if (host != null)
            {
                labels.Add(new("host", host));
            }

            return labels;
        }

        /// <summary>Grafana-style level values: trace, debug, info, warn, error, critical.</summary>
        public static string Level(LogLevel level) => level switch
        {
            LogLevel.Trace => "trace",
            LogLevel.Debug => "debug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "error",
            _ => "critical",
        };
    }

    /// <summary>
    /// Unix-epoch nanosecond timestamps that are strictly increasing within the process (two
    /// entries never share a timestamp, even within the same clock tick), so every stream is
    /// accepted in order by Loki.
    /// </summary>
    public sealed class MonotonicNanoClock
    {
        private readonly Func<DateTimeOffset> _clock;
        private long _last;

        public MonotonicNanoClock(Func<DateTimeOffset>? clock = null)
        {
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        /// <summary>The process-wide instance used by default.</summary>
        public static MonotonicNanoClock Shared { get; } = new();

        public long Next()
        {
            var now = (_clock() - DateTimeOffset.UnixEpoch).Ticks * 100;
            while (true)
            {
                var last = Volatile.Read(ref _last);
                var next = now > last ? now : last + 1;
                if (Interlocked.CompareExchange(ref _last, next, last) == last)
                {
                    return next;
                }
            }
        }
    }
}
