using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ResourceMonitorDaemon.Pairing
{
    /// <summary>
    /// One-time "No device found — click to pair" tray prompt for a PC that has never used a device.
    /// The decision is <see cref="PairingOutcomePresenter.ShouldOfferFirstRunPairing"/>; this class
    /// supplies its inputs and keeps a marker file next to the user settings
    /// (<c>%LocalAppData%\ResourceMonitorDaemon\pairing-prompted</c>). The marker is written when the
    /// prompt is shown and also the first time the daemon connects to a broker, so the prompt is shown
    /// at most once per user and never on a PC that has connected to a device. Delete the marker (and
    /// the daemon logs) to see the prompt again. Not thread-safe: use it from the UI thread.
    /// </summary>
    public sealed class FirstRunPairingPrompt
    {
        public const string MarkerFileName = "pairing-prompted";

        private readonly string _markerPath;
        private readonly ILogger? _logger;
        private readonly bool _daemonRanBefore;
        private bool _connectedSinceStart;
        private bool _shownThisSession;

        /// <param name="markerPath">Marker file location.</param>
        /// <param name="logger">Optional logger.</param>
        /// <param name="daemonRanBefore">
        /// True when the daemon already ran on this PC before this process (see <see cref="DetectEarlierRun"/>):
        /// covers installations that connected before this marker existed.
        /// </param>
        public FirstRunPairingPrompt(string markerPath, ILogger? logger = null, bool daemonRanBefore = false)
        {
            _markerPath = Path.GetFullPath(markerPath);
            _logger = logger;
            _daemonRanBefore = daemonRanBefore;
        }

        public static string DefaultMarkerPath => Path.Combine(DaemonPaths.AppDataDirectory, MarkerFileName);

        public string MarkerPath => _markerPath;

        /// <summary>The prompt for the real daemon: default marker, earlier runs detected from the daemon log.</summary>
        public static FirstRunPairingPrompt CreateDefault(ILogger? logger)
        {
            DateTime processStartUtc;
            try
            {
                using var process = Process.GetCurrentProcess();
                processStartUtc = process.StartTime.ToUniversalTime();
            }
            catch (Exception)
            {
                processStartUtc = DateTime.UtcNow;
            }

            return new FirstRunPairingPrompt(DefaultMarkerPath, logger, DetectEarlierRun(DaemonPaths.LogFilePath, processStartUtc));
        }

        /// <summary>
        /// True when a daemon log from an earlier run exists: <paramref name="logFilePath"/> created before
        /// <paramref name="processStartUtc"/>, or its rotated <c>.1</c> file. A log created by this process
        /// does not count.
        /// </summary>
        public static bool DetectEarlierRun(string logFilePath, DateTime processStartUtc)
        {
            try
            {
                if (File.Exists(logFilePath + ".1"))
                {
                    return true;
                }

                return File.Exists(logFilePath) && File.GetCreationTimeUtc(logFilePath) < processStartUtc;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Call periodically with the daemon's MQTT state. The first successful connection records the
        /// marker, so this PC never gets the prompt again; no prompt is shown later in this session either.
        /// </summary>
        public void ObserveConnection(bool connected)
        {
            if (!connected || _connectedSinceStart)
            {
                return;
            }

            _connectedSinceStart = true;
            if (!File.Exists(_markerPath))
            {
                WriteMarker("Connected to a device");
            }
        }

        /// <summary>
        /// Returns true when the prompt should be shown now (and records it, so it is not shown again);
        /// false otherwise. <paramref name="isConnectionFailure"/> is true for the "MQTT connection
        /// failed" notification.
        /// </summary>
        public bool TryClaim(bool isConnectionFailure, DeviceSettings effectiveSettings)
        {
            ArgumentNullException.ThrowIfNull(effectiveSettings);
            if (_shownThisSession
                || !PairingOutcomePresenter.ShouldOfferFirstRunPairing(
                    isConnectionFailure,
                    File.Exists(_markerPath),
                    effectiveSettings.FallbackAddress,
                    effectiveSettings.Host,
                    _connectedSinceStart,
                    _daemonRanBefore))
            {
                return false;
            }

            _shownThisSession = true;
            WriteMarker("First-run pairing prompt shown");
            _logger?.LogInformation("No device configured and the broker is unreachable; showing the one-time pairing prompt.");
            return true;
        }

        private void WriteMarker(string reason)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_markerPath)!);
                File.WriteAllText(_markerPath, $"{reason} {DateTimeOffset.Now:O}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The in-memory flags still prevent a prompt in this session; it may come back after a restart.
                _logger?.LogWarning("Could not record the pairing prompt state in {Path}: {Error}", _markerPath, ex.Message);
            }
        }
    }
}
