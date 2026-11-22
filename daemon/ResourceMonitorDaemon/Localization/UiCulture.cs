using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace ResourceMonitorDaemon.Localization
{
    /// <summary>
    /// The current UI language of the process: sets <see cref="Strings.Culture"/> only. The thread
    /// cultures stay as Windows set them, so number/date formatting elsewhere (MQTT payloads use
    /// the invariant culture anyway) is unaffected.
    /// </summary>
    public static class UiCulture
    {
        private static readonly object Gate = new();

        /// <summary>The Windows display language, captured when the process starts (decides <c>auto</c>).</summary>
        public static CultureInfo SystemUiCulture { get; } = CultureInfo.CurrentUICulture;

        public static CultureInfo Current => Strings.Culture;

        /// <summary>
        /// Raised (on the thread that changed it) after the UI culture changed. Open windows keep their
        /// language; the tray menu re-reads its texts.
        /// </summary>
        public static event EventHandler? Changed;

        /// <summary>Switches the UI culture; returns true and raises <see cref="Changed"/> when it differs from the current one.</summary>
        public static bool Apply(CultureInfo culture)
        {
            ArgumentNullException.ThrowIfNull(culture);
            lock (Gate)
            {
                if (Strings.Culture.Name == culture.Name)
                {
                    return false;
                }

                Strings.Culture = culture;
            }

            Changed?.Invoke(null, EventArgs.Empty);
            return true;
        }

        /// <summary>Resolves <paramref name="setting"/> against <see cref="SystemUiCulture"/> and applies it.</summary>
        public static bool ApplySetting(string? setting, out string? warning) =>
            Apply(UiLanguage.Resolve(setting, SystemUiCulture, out warning));
    }

    /// <summary>
    /// Applies <c>Ui:Language</c> from configuration at start-up and again whenever the configuration
    /// reloads (user settings file saved, no restart). Logs (in English) the start-up language, each
    /// change and, once per distinct value, an invalid setting.
    /// </summary>
    public sealed class UiLanguageMonitor : IDisposable
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger _logger;
        private readonly object _gate = new();
        private IDisposable? _subscription;
        private string? _lastWarning;

        public UiLanguageMonitor(IConfiguration configuration, ILogger<UiLanguageMonitor> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        /// <summary>The configured value (normalized), e.g. for the settings dialog.</summary>
        public string Setting => UiLanguage.Normalize(_configuration[UiLanguage.ConfigurationKey], out _);

        /// <summary>Applies the configured language and starts following configuration reloads. Idempotent.</summary>
        public void Start()
        {
            lock (_gate)
            {
                if (_subscription != null)
                {
                    return;
                }

                Refresh(startup: true);
                _subscription = ChangeToken.OnChange(_configuration.GetReloadToken, () => Refresh(startup: false));
            }
        }

        /// <summary>
        /// Applies a value just saved by the settings dialog without waiting for the configuration
        /// file watcher (which then finds nothing to change).
        /// </summary>
        public void ApplySaved(string? setting) => Apply(setting, startup: false);

        private void Refresh(bool startup) => Apply(_configuration[UiLanguage.ConfigurationKey], startup);

        private void Apply(string? raw, bool startup)
        {
            var changed = UiCulture.ApplySetting(raw, out var warning);
            lock (_gate)
            {
                if (warning != _lastWarning && warning != null)
                {
                    _logger.LogWarning("Invalid setting: {Warning}", warning);
                }

                _lastWarning = warning;
            }

            if (startup)
            {
                _logger.LogInformation(
                    "UI language: {Culture} (Ui:Language={Setting}, Windows display language {System}).",
                    UiCulture.Current.Name, UiLanguage.Normalize(raw, out _), UiCulture.SystemUiCulture.Name);
            }
            else if (changed)
            {
                _logger.LogInformation("UI language changed to {Culture}.", UiCulture.Current.Name);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _subscription?.Dispose();
                _subscription = null;
            }
        }
    }
}
