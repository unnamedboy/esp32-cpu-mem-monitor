using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ResourceMonitorDaemon.Observability
{
    /// <summary>
    /// Forwards daemon log entries at or above <see cref="LokiSettings.MinLevel"/> to the Loki queue
    /// (stream <c>{app="resmon-daemon", kind="log", level="&lt;lvl&gt;"}</c>).
    /// </summary>
    /// <remarks>
    /// Never recurses: categories of the observability pipeline itself and of
    /// <c>System.Net.Http.*</c> get a no-op logger, so the sink's own messages (and HttpClient
    /// logging, if ever enabled) reach the local file only. Logging is otherwise filtered by the
    /// normal <c>Logging</c> configuration (provider alias <c>Loki</c>). Enqueueing never blocks.
    /// </remarks>
    [ProviderAlias("Loki")]
    public sealed class LokiLoggerProvider : ILoggerProvider
    {
        /// <summary>Category prefixes that are never forwarded.</summary>
        public static IReadOnlyList<string> ExcludedCategoryPrefixes { get; } = new[]
        {
            typeof(LokiLoggerProvider).Namespace!,   // ResourceMonitorDaemon.Observability
            "System.Net.Http",
        };

        private readonly LokiEntryQueue _queue;

        public LokiLoggerProvider(LokiEntryQueue queue)
        {
            _queue = queue;
        }

        public static bool IsExcluded(string categoryName) =>
            ExcludedCategoryPrefixes.Any(prefix => categoryName.StartsWith(prefix, StringComparison.Ordinal));

        public ILogger CreateLogger(string categoryName) =>
            IsExcluded(categoryName) ? NullLogger.Instance : new LokiLogger(_queue, categoryName);

        public void Dispose()
        {
        }

        private sealed class LokiLogger : ILogger
        {
            private readonly LokiEntryQueue _queue;
            private readonly string _category;

            public LokiLogger(LokiEntryQueue queue, string categoryName)
            {
                _queue = queue;
                var lastDot = categoryName.LastIndexOf('.');
                _category = lastDot >= 0 ? categoryName[(lastDot + 1)..] : categoryName;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) =>
                logLevel != LogLevel.None && _queue.Accepting && logLevel >= _queue.MinLogLevel;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel))
                {
                    return;
                }

                try
                {
                    var line = new StringBuilder(_category).Append(": ").Append(formatter(state, exception));
                    if (exception != null)
                    {
                        line.Append('\n').Append(exception);
                    }

                    _queue.TryEnqueue(LokiStreamKind.Log, LokiLabels.Level(logLevel), line.ToString());
                }
                catch (Exception)
                {
                    // Forwarding must never affect the caller.
                }
            }
        }
    }
}
