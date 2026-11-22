using System.Globalization;

namespace ResourceMonitorDaemon.Localization
{
    /// <summary>
    /// The <c>Ui:Language</c> setting (<c>auto</c> | <c>en</c> | <c>zh-Hans</c>, default <c>auto</c>;
    /// environment override <c>RESMON_Ui__Language</c>) and how it resolves to a UI culture. Pure
    /// functions, no state: <see cref="UiCulture"/> applies the result.
    /// </summary>
    public static class UiLanguage
    {
        public const string SectionName = "Ui";
        public const string LanguageKey = "Language";

        /// <summary>Configuration key, <c>Ui:Language</c>.</summary>
        public const string ConfigurationKey = SectionName + ":" + LanguageKey;

        /// <summary>Follow the Windows display language: Simplified Chinese for zh-Hans/zh-CN/zh-SG, English otherwise.</summary>
        public const string Auto = "auto";
        public const string English = "en";
        public const string ChineseSimplified = "zh-Hans";

        /// <summary>Every valid setting value, in the order the settings dialog lists them.</summary>
        public static IReadOnlyList<string> Values { get; } = new[] { Auto, English, ChineseSimplified };

        public static CultureInfo EnglishCulture { get; } = CultureInfo.GetCultureInfo(English);
        public static CultureInfo ChineseSimplifiedCulture { get; } = CultureInfo.GetCultureInfo(ChineseSimplified);

        /// <summary>
        /// Returns the canonical setting value for <paramref name="value"/> (case-insensitive; empty
        /// means <see cref="Auto"/>). An unknown value yields <see cref="Auto"/> and an English
        /// <paramref name="warning"/> for the log.
        /// </summary>
        public static string Normalize(string? value, out string? warning)
        {
            warning = null;
            if (string.IsNullOrWhiteSpace(value))
            {
                return Auto;
            }

            var trimmed = value.Trim();
            foreach (var known in Values)
            {
                if (string.Equals(known, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    return known;
                }
            }

            warning = $"{ConfigurationKey} '{trimmed}' is not {string.Join(", ", Values)}. Using {Auto}.";
            return Auto;
        }

        /// <summary>True when <paramref name="value"/> is exactly one of <see cref="Values"/> (what the settings file may store).</summary>
        public static bool IsCanonical(string? value) => value is not null && Values.Contains(value, StringComparer.Ordinal);

        /// <summary>Resolves a setting value to the UI culture; <paramref name="systemUiCulture"/> decides for <see cref="Auto"/>.</summary>
        public static CultureInfo Resolve(string? setting, CultureInfo systemUiCulture, out string? warning) =>
            Normalize(setting, out warning) switch
            {
                English => EnglishCulture,
                ChineseSimplified => ChineseSimplifiedCulture,
                _ => ForSystem(systemUiCulture),
            };

        /// <summary>
        /// Simplified Chinese for any Simplified Chinese UI culture (zh-Hans, zh-CN, zh-SG, zh-Hans-*,
        /// bare zh), English for everything else, including Traditional Chinese (zh-Hant, zh-TW,
        /// zh-HK, zh-MO), for which there is no translation yet.
        /// </summary>
        public static CultureInfo ForSystem(CultureInfo systemUiCulture)
        {
            ArgumentNullException.ThrowIfNull(systemUiCulture);
            var culture = systemUiCulture;
            for (var depth = 0; depth < 8 && culture.Name.Length > 0; depth++, culture = culture.Parent)
            {
                switch (culture.Name.ToLowerInvariant())
                {
                    case "zh-hans" or "zh-cn" or "zh-sg" or "zh-chs" or "zh":
                        return ChineseSimplifiedCulture;
                    case "zh-hant" or "zh-tw" or "zh-hk" or "zh-mo" or "zh-cht":
                        return EnglishCulture;
                }
            }

            return EnglishCulture;
        }

        /// <summary>
        /// The value the settings dialog writes to the user file for <paramref name="selected"/>: null
        /// (remove the key) when it equals what the lower layers already resolve to, so the user file
        /// only holds explicit choices.
        /// </summary>
        public static string? ValueToStore(string selected, string lowerLayers) =>
            string.Equals(selected, lowerLayers, StringComparison.Ordinal) ? null : selected;
    }
}
