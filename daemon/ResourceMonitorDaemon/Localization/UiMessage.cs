using System.Globalization;

namespace ResourceMonitorDaemon.Localization
{
    /// <summary>
    /// A message that is both logged and shown to the user (validation errors, Loki test details):
    /// a <see cref="Strings"/> key plus its format arguments. <see cref="English"/> is for logs and
    /// exceptions (always the neutral English text, invariant number formatting) and
    /// <see cref="Localized"/> for the UI (<see cref="Strings.Culture"/>).
    /// </summary>
    public sealed class UiMessage
    {
        private readonly object?[] _args;

        /// <param name="key">A <see cref="Strings"/> member name, e.g. <c>nameof(Strings.Settings_ErrorHostEmpty)</c>.</param>
        public UiMessage(string key, params object?[] args)
        {
            Key = key;
            _args = args;
        }

        public string Key { get; }

        public IReadOnlyList<object?> Args => _args;

        /// <summary>The neutral (English) text, independent of the UI language. Use it for logs.</summary>
        public string English => Strings.Format(CultureInfo.InvariantCulture, Key, _args);

        /// <summary>The text in the current UI language.</summary>
        public string Localized => Strings.Format(Strings.Culture, Key, _args);

        public override string ToString() => English;
    }
}
