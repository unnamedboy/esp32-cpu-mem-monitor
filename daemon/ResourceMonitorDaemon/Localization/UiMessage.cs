using System.Globalization;

namespace ResourceMonitorDaemon.Localization
{
    /// <summary>
    /// A message that is both logged and shown to the user (validation errors, Loki test details,
    /// MQTT notifications): a <see cref="Strings"/> key plus its format arguments. Created only
    /// through the typed factories in <c>UiMessage.Factories.cs</c>, so the argument count always
    /// matches the placeholders. <see cref="English"/> is for logs and exceptions (always the
    /// neutral English text, invariant number formatting, configuration keys for
    /// <see cref="UiField"/> arguments); <see cref="Localized"/> is for the UI.
    /// </summary>
    public sealed partial class UiMessage
    {
        private readonly object?[] _args;

        private UiMessage(string key, params object?[] args)
        {
            Key = key;
            _args = args;
        }

        public string Key { get; }

        public IReadOnlyList<object?> Args => _args;

        /// <summary>The neutral (English) text, independent of the UI language. Use it for logs.</summary>
        public string English => Format(CultureInfo.InvariantCulture);

        /// <summary>The text in the current UI language.</summary>
        public string Localized => Format(Strings.Culture);

        /// <summary>
        /// The text in <paramref name="culture"/>. With the invariant culture (logs) a
        /// <see cref="UiField"/> argument is its configuration key; otherwise its display name. A
        /// nested <see cref="UiMessage"/> argument is formatted in the same culture, so one call
        /// never mixes languages.
        /// </summary>
        public string Format(CultureInfo culture)
        {
            var forLog = culture.Equals(CultureInfo.InvariantCulture);
            var args = _args.Select(arg => arg switch
            {
                UiField field => forLog ? field.Identifier : field.Display(culture),
                UiMessage message => message.Format(culture),
                _ => arg,
            }).ToArray();
            return Strings.Format(culture, Key, args);
        }

        public override string ToString() => English;
    }

    /// <summary>
    /// A settings field named in a message: logged by its configuration key (<see cref="Identifier"/>,
    /// e.g. <c>CpuTopic</c>) and shown by its dialog label (<see cref="DisplayKey"/>, e.g.
    /// <c>Settings_CpuTopic</c>).
    /// </summary>
    public sealed class UiField
    {
        /// <param name="displayKey">A <see cref="Strings"/> key; null shows the identifier in every language.</param>
        public UiField(string identifier, string? displayKey)
        {
            Identifier = identifier;
            DisplayKey = displayKey;
        }

        public string Identifier { get; }

        public string? DisplayKey { get; }

        public string Display(CultureInfo culture) => DisplayKey is null ? Identifier : Strings.Get(DisplayKey, culture);

        public override string ToString() => Identifier;
    }
}
