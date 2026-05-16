using Microsoft.Extensions.Logging;
using System;
using System.IO;

namespace ResourceMonitorDaemon
{
    public class DaemonLoggerProvider : ILoggerProvider
    {
        private readonly string _path;
        public DaemonLoggerProvider(string path) => _path = path;
        public ILogger CreateLogger(string categoryName) => new DaemonLogger(_path);
        public void Dispose() { }
    }

    public class DaemonLogger : ILogger
    {
        private readonly string _path;
        public DaemonLogger(string path) => _path = path;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            try
            {
                var message = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{logLevel}] {formatter(state, exception)}{Environment.NewLine}";
                File.AppendAllText(_path, message);
            }
            catch { }
        }
    }
}
