using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
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
    /// Every line passes through <see cref="LokiLogRedactor"/> first, so per-user folders, the
    /// Windows user name and (unless the host label is enabled) the PC name never reach Loki.
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
        private readonly LokiLogRedactor _redactor;

        public LokiLoggerProvider(LokiEntryQueue queue, LokiLogRedactor? redactor = null)
        {
            _queue = queue;
            _redactor = redactor ?? LokiLogRedactor.ForCurrentUser();
        }

        public static bool IsExcluded(string categoryName) =>
            ExcludedCategoryPrefixes.Any(prefix => categoryName.StartsWith(prefix, StringComparison.Ordinal));

        public ILogger CreateLogger(string categoryName) =>
            IsExcluded(categoryName) ? NullLogger.Instance : new LokiLogger(_queue, _redactor, categoryName);

        public void Dispose()
        {
        }

        private sealed class LokiLogger : ILogger
        {
            private readonly LokiEntryQueue _queue;
            private readonly LokiLogRedactor _redactor;
            private readonly string _category;

            public LokiLogger(LokiEntryQueue queue, LokiLogRedactor redactor, string categoryName)
            {
                _queue = queue;
                _redactor = redactor;
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

                    _queue.TryEnqueue(LokiStreamKind.Log, LokiLabels.Level(logLevel), _redactor.Redact(line.ToString(), _queue.KeepHostName));
                }
                catch (Exception)
                {
                    // Forwarding must never affect the caller.
                }
            }
        }
    }

    /// <summary>
    /// Removes personal data from daemon log lines before they are forwarded to Loki (the local
    /// <c>daemon.log</c> is unchanged):
    /// <list type="bullet">
    /// <item>per-user folders are replaced by <c>%LOCALAPPDATA%</c>, <c>%APPDATA%</c> or
    /// <c>%USERPROFILE%</c> (longest match first, case-insensitive);</item>
    /// <item>the Windows user name is replaced by <c>&lt;user&gt;</c> (whole word, case-insensitive);</item>
    /// <item>the machine name is replaced by <c>&lt;host&gt;</c> unless the user opted into the host label;</item>
    /// <item>as a fallback, the profile folder in any absolute path <c>X:\Users\&lt;name&gt;</c> or
    /// <c>\\server\Users\&lt;name&gt;</c> (either slash, any case, spaces allowed, 8.3 short forms
    /// such as <c>C:\Users\ABCDEF~1</c> included) becomes <c>&lt;user&gt;</c>; the shared folders
    /// Public, Default, Default User and All Users are kept, and relative paths or URLs are not touched.</item>
    /// </list>
    /// The current user's folders are registered in their long and short (8.3) forms.
    /// Names shorter than <see cref="MinNameLength"/> characters are not replaced as words (that
    /// would mangle ordinary text); paths containing them are still covered by the folder rules.
    /// </summary>
    public sealed class LokiLogRedactor
    {
        public const string UserPlaceholder = "<user>";
        public const string HostPlaceholder = "<host>";
        public const int MinNameLength = 3;

        private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

        // A profile folder in an absolute drive (C:\Users\name) or UNC (\\server\Users\name) path. The
        // name may contain spaces and runs up to the next separator, or to the end of the line.
        private static readonly Regex ProfileFolder = new(
            @"(?<prefix>(?:(?<![\p{L}\p{N}_])[A-Za-z]:|\\\\[^\\/\s:*?""<>|]+)[\\/]Users[\\/])(?<name>[^\\/:*?""<>|\r\n]+)(?=[\\/\r]|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline,
            MatchTimeout);

        private static readonly HashSet<string> SharedProfileFolders = new(StringComparer.OrdinalIgnoreCase)
        {
            "Public", "Default", "Default User", "All Users",
        };

        private readonly (string Prefix, string Token)[] _prefixes;
        private readonly Regex? _user;
        private readonly Regex? _host;
        private readonly string? _userName;
        private readonly string? _hostName;

        public LokiLogRedactor(IEnumerable<(string Prefix, string Token)> folders, string? userName, string? machineName)
        {
            _prefixes = folders
                .Where(f => !string.IsNullOrWhiteSpace(f.Prefix))
                .Select(f => (Prefix: f.Prefix.TrimEnd('\\', '/'), f.Token))
                .OrderByDescending(f => f.Prefix.Length)
                .ToArray();
            _user = WholeWord(userName);
            _host = WholeWord(machineName);
            _userName = _user == null ? null : userName!.Trim();
            _hostName = _host == null ? null : machineName!.Trim();
        }

        /// <summary>Redactor for the current Windows user and PC.</summary>
        public static LokiLogRedactor ForCurrentUser()
        {
            var folders = new List<(string, string)>();
            foreach (var (folder, token) in new[]
            {
                (Environment.SpecialFolder.LocalApplicationData, "%LOCALAPPDATA%"),
                (Environment.SpecialFolder.ApplicationData, "%APPDATA%"),
                (Environment.SpecialFolder.UserProfile, "%USERPROFILE%"),
            })
            {
                var path = Environment.GetFolderPath(folder);
                folders.Add((path, token));
                if (ShortPath(path) is { } shortPath && !string.Equals(shortPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    folders.Add((shortPath, token));   // e.g. TEMP is usually reported as C:\Users\ABCDEF~1\...
                }
            }

            return new LokiLogRedactor(folders, Environment.UserName, Environment.MachineName);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint bufferLength);

        /// <summary>The 8.3 form of an existing path, or null.</summary>
        internal static string? ShortPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            try
            {
                var buffer = new StringBuilder(path.Length + 16);
                var length = GetShortPathName(path, buffer, (uint)buffer.Capacity);
                return length > 0 && length < buffer.Capacity ? buffer.ToString() : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public string Redact(string text, bool keepHostName)
        {
            foreach (var (prefix, token) in _prefixes)
            {
                text = text.Replace(prefix, token, StringComparison.OrdinalIgnoreCase);
            }

            // Cheap substring checks first: most lines contain none of these, and the regexes
            // (with look-around) would otherwise scan every line.
            if (_userName != null && text.Contains(_userName, StringComparison.OrdinalIgnoreCase))
            {
                text = Replace(_user, text, UserPlaceholder);
            }

            if (text.Contains("Users", StringComparison.OrdinalIgnoreCase))
            {
                text = ReplaceProfileFolders(text);
            }

            if (!keepHostName && _hostName != null && text.Contains(_hostName, StringComparison.OrdinalIgnoreCase))
            {
                text = Replace(_host, text, HostPlaceholder);
            }

            return text;
        }

        private static string ReplaceProfileFolders(string text)
        {
            try
            {
                return ProfileFolder.Replace(text, match =>
                    SharedProfileFolders.Contains(match.Groups["name"].Value.Trim())
                        ? match.Value
                        : match.Groups["prefix"].Value + UserPlaceholder);
            }
            catch (RegexMatchTimeoutException)
            {
                return "(line withheld: redaction timed out)";
            }
        }

        private static Regex? WholeWord(string? name) =>
            string.IsNullOrWhiteSpace(name) || name.Trim().Length < MinNameLength
                ? null
                : new Regex(@"(?<![\p{L}\p{N}_])" + Regex.Escape(name.Trim()) + @"(?![\p{L}\p{N}_])",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);

        private static string Replace(Regex? pattern, string text, string placeholder)
        {
            if (pattern == null)
            {
                return text;
            }

            try
            {
                return pattern.Replace(text, placeholder);
            }
            catch (RegexMatchTimeoutException)
            {
                // Never forward a line that could not be checked.
                return "(line withheld: redaction timed out)";
            }
        }
    }
}
