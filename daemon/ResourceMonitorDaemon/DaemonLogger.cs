using System.Text;
using Microsoft.Extensions.Logging;

namespace ResourceMonitorDaemon
{
    /// <summary>
    /// Appends log lines to a single file with simple size-based rotation
    /// (<c>daemon.log</c> → <c>daemon.log.1</c>). Thread-safe.
    /// </summary>
    /// <remarks>
    /// Level filtering is done by the logging framework from the <c>Logging</c> configuration
    /// section (default minimum level: Information). Use the alias <c>DaemonFile</c> to set a
    /// provider-specific level, e.g. <c>Logging:DaemonFile:LogLevel:Default</c>.
    /// </remarks>
    [ProviderAlias("DaemonFile")]
    public sealed class DaemonLoggerProvider : ILoggerProvider
    {
        public const long DefaultMaxFileSizeBytes = 5 * 1024 * 1024;

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        private readonly object _gate = new();
        private readonly long _maxFileSizeBytes;
        private long _currentSize = -1;

        public DaemonLoggerProvider(string filePath, long maxFileSizeBytes = DefaultMaxFileSizeBytes)
        {
            FilePath = Path.GetFullPath(filePath);
            _maxFileSizeBytes = Math.Max(1024, maxFileSizeBytes);
        }

        public string FilePath { get; }

        /// <summary>Path of the previous (rotated) log file.</summary>
        public string RolledFilePath => FilePath + ".1";

        public ILogger CreateLogger(string categoryName) => new DaemonLogger(this, categoryName);

        public void Dispose()
        {
        }

        internal void Write(string line)
        {
            var bytes = Utf8NoBom.GetByteCount(line);
            lock (_gate)
            {
                try
                {
                    if (_currentSize < 0)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                        _currentSize = File.Exists(FilePath) ? new FileInfo(FilePath).Length : 0;
                    }

                    if (_currentSize > 0 && _currentSize + bytes > _maxFileSizeBytes)
                    {
                        Rotate();
                    }

                    File.AppendAllText(FilePath, line, Utf8NoBom);
                    _currentSize += bytes;
                }
                catch (Exception)
                {
                    // Logging must never take the daemon down (disk full, file locked, ...).
                    // Re-read the size on the next write.
                    _currentSize = -1;
                }
            }
        }

        private void Rotate()
        {
            try
            {
                File.Move(FilePath, RolledFilePath, overwrite: true);
                _currentSize = 0;
            }
            catch (IOException)
            {
                // File held open elsewhere; keep appending and retry rotation on a later write.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public sealed class DaemonLogger : ILogger
    {
        private readonly DaemonLoggerProvider _provider;
        private readonly string _category;

        public DaemonLogger(DaemonLoggerProvider provider, string categoryName)
        {
            _provider = provider;
            var lastDot = categoryName.LastIndexOf('.');
            _category = lastDot >= 0 ? categoryName[(lastDot + 1)..] : categoryName;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var builder = new StringBuilder()
                .Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("] ")
                .Append('[').Append(logLevel).Append("] ")
                .Append('[').Append(_category).Append("] ")
                .Append(formatter(state, exception));

            if (exception != null)
            {
                builder.AppendLine().Append(exception);
            }

            builder.AppendLine();
            _provider.Write(builder.ToString());
        }
    }
}
