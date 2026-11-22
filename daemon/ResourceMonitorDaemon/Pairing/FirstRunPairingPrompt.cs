using Microsoft.Extensions.Logging;

namespace ResourceMonitorDaemon.Pairing
{
    /// <summary>
    /// One-time "No device found — click to pair" tray prompt. The decision is
    /// <see cref="PairingOutcomePresenter.ShouldOfferFirstRunPairing"/>; this class only supplies its
    /// inputs and records that the prompt was shown, as a marker file next to the user settings
    /// (<c>%LocalAppData%\ResourceMonitorDaemon\pairing-prompted</c>), so it is shown at most once per
    /// user even across restarts. Delete the marker to see the prompt again. Not thread-safe: use it
    /// from the UI thread.
    /// </summary>
    public sealed class FirstRunPairingPrompt
    {
        public const string MarkerFileName = "pairing-prompted";

        private readonly string _markerPath;
        private readonly ILogger? _logger;
        private bool _connectedSinceStart;
        private bool _shownThisSession;

        public FirstRunPairingPrompt(string markerPath, ILogger? logger = null)
        {
            _markerPath = Path.GetFullPath(markerPath);
            _logger = logger;
        }

        public static string DefaultMarkerPath => Path.Combine(DaemonPaths.AppDataDirectory, MarkerFileName);

        public string MarkerPath => _markerPath;

        /// <summary>Call periodically with the daemon's MQTT state; once connected, no prompt is shown this session.</summary>
        public void ObserveConnection(bool connected)
        {
            if (connected)
            {
                _connectedSinceStart = true;
            }
        }

        /// <summary>
        /// Returns true when the prompt should be shown now (and records it, so it is not shown again);
        /// false otherwise. <paramref name="isConnectionFailure"/> is true for the "MQTT connection
        /// failed" notification.
        /// </summary>
        public bool TryClaim(bool isConnectionFailure, string? effectiveFallbackAddress)
        {
            if (_shownThisSession
                || !PairingOutcomePresenter.ShouldOfferFirstRunPairing(
                    isConnectionFailure, File.Exists(_markerPath), effectiveFallbackAddress, _connectedSinceStart))
            {
                return false;
            }

            _shownThisSession = true;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_markerPath)!);
                File.WriteAllText(_markerPath, $"First-run pairing prompt shown {DateTimeOffset.Now:O}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still shown only once in this session; it may come back after a restart.
                _logger?.LogWarning("Could not record the first-run pairing prompt in {Path}: {Error}", _markerPath, ex.Message);
            }

            _logger?.LogInformation("No device configured and the broker is unreachable; showing the one-time pairing prompt.");
            return true;
        }
    }
}
