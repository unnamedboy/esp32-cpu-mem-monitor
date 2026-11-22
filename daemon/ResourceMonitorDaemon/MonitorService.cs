using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ResourceMonitorDaemon
{
    /// <summary>
    /// Samples CPU/memory every <see cref="DeviceSettings.SampleIntervalMs"/> and publishes the
    /// values through <see cref="MqttConnection"/>. Settings changes are applied live.
    /// </summary>
    public class MonitorService : BackgroundService
    {
        private readonly ILogger<MonitorService> _logger;
        private readonly IOptionsMonitor<DeviceSettings> _settingsMonitor;
        private readonly MqttConnection _connection;
        private readonly ISettingsStore _settingsStore;
        private readonly IMetricsObserver? _metricsObserver;
        private PerformanceCounter? _cpuCounter;
        private DeviceSettings _settings;
        private DeviceSettings? _pendingSettings;
        private IDisposable? _settingsSubscription;

        // Released by the settings OnChange callback so the loop applies changes right away
        // instead of at the next sample tick (which can be up to 60 s away).
        private readonly SemaphoreSlim _settingsChanged = new(0, 1);
        private volatile bool _isPaused;
        private int _stopRequested;

        public MonitorService(
            ILogger<MonitorService> logger,
            IOptionsMonitor<DeviceSettings> settingsMonitor,
            MqttConnection connection,
            ISettingsStore settingsStore,
            IMetricsObserver? metricsObserver = null)
        {
            _logger = logger;
            _settingsMonitor = settingsMonitor;
            _connection = connection;
            _settingsStore = settingsStore;
            _metricsObserver = metricsObserver;
            _settings = settingsMonitor.CurrentValue;
        }

        /// <summary>
        /// User-visible notification sink (set by the tray before the host starts). May be
        /// invoked from any thread.
        /// </summary>
        public Action<string, string, ToolTipIcon>? OnNotification
        {
            get => _connection.OnNotification;
            set => _connection.OnNotification = value;
        }

        /// <summary>Settings currently in effect.</summary>
        public DeviceSettings CurrentSettings => Volatile.Read(ref _settings);

        public bool IsPaused => _isPaused;

        /// <summary>True while the MQTT connection is up (read-only; used by the tray and pairing wizard).</summary>
        public bool IsConnected => _connection.IsConnected;

        /// <summary>Changes with every new MQTT connection (see <see cref="MqttConnection.ConnectionGeneration"/>).</summary>
        public int ConnectionGeneration => _connection.ConnectionGeneration;

        public override Task StartAsync(CancellationToken cancellationToken)
        {
            // Subscribe before reading CurrentValue so a reload during start-up (e.g. while the
            // performance counter initializes) is not lost; equal values are ignored later.
            _settingsSubscription = _settingsMonitor.OnChange(OnSettingsChanged);
            _settings = _settingsMonitor.CurrentValue;
            LogStartupDiagnostics(_settings);

            try
            {
                _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                _cpuCounter.NextValue();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to initialize the CPU performance counter; CPU will read as 0.");
            }

            _connection.Configure(_settings);
            return base.StartAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Start connecting right away instead of waiting one sample period.
            _connection.MaintainConnection();

            var clock = Stopwatch.StartNew();
            var nextTick = clock.Elapsed + SampleInterval;
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    var delay = nextTick - clock.Elapsed;
                    if (delay > TimeSpan.Zero && await _settingsChanged.WaitAsync(delay, stoppingToken))
                    {
                        // Woken by a settings change: apply it now, then keep waiting. Like a
                        // sample, a failure here is logged and the loop keeps running.
                        try
                        {
                            if (ApplyPendingSettings(out var intervalChanged))
                            {
                                if (intervalChanged)
                                {
                                    nextTick = clock.Elapsed + SampleInterval;
                                }

                                if (!_isPaused)
                                {
                                    _connection.MaintainConnection(); // reconnects at once on endpoint change
                                }
                            }
                        }
                        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                        {
                            _logger.LogError(ex, "An error occurred while applying changed settings.");
                        }

                        continue;
                    }

                    // Fixed-rate schedule without drift; skip missed ticks instead of bursting.
                    nextTick += SampleInterval;
                    if (nextTick <= clock.Elapsed)
                    {
                        nextTick = clock.Elapsed + SampleInterval;
                    }

                    await SampleAndPublishAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown.
            }
        }

        private TimeSpan SampleInterval => TimeSpan.FromMilliseconds(_settings.SampleIntervalMs);

        private void NotifyObserver(float cpuUsage, (double TotalGB, double UsedGB, uint MemoryLoad) memInfo)
        {
            if (_metricsObserver == null)
            {
                return;
            }

            try
            {
                var mqtt = _isPaused ? "paused" : _connection.IsConnected ? "connected" : "disconnected";
                _metricsObserver.OnSample(new MetricsSample(cpuUsage, memInfo.MemoryLoad, memInfo.UsedGB, memInfo.TotalGB, mqtt));
            }
            catch (Exception ex)
            {
                // Observers must not throw; never let one affect sampling or publishing.
                _logger.LogDebug("Metrics observer failed: {Error}", ex.Message);
            }
        }

        private async Task SampleAndPublishAsync(CancellationToken stoppingToken)
        {
            try
            {
                ApplyPendingSettings(out _);

                float cpuUsage = _cpuCounter?.NextValue() ?? 0;
                var memInfo = NativeMemory.GetMemoryStatus();
                _logger.LogDebug(
                    "[Metrics] CPU: {Cpu:F1}% | RAM: {UsedGb:F2}/{TotalGb:F2} GB ({MemoryLoad}%)",
                    cpuUsage, memInfo.UsedGB, memInfo.TotalGB, memInfo.MemoryLoad);
                NotifyObserver(cpuUsage, memInfo);

                if (_isPaused)
                {
                    return;
                }

                // Never waits for the network: at most starts a background connect attempt.
                _connection.MaintainConnection();

                var settings = _settings;
                if (await _connection.PublishAsync(settings.CpuTopic, MetricPayloadFormatter.FormatPercentPayload(cpuUsage), stoppingToken))
                {
                    await _connection.PublishAsync(settings.MemTopic, MetricPayloadFormatter.FormatPercentPayload(memInfo.MemoryLoad), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while sampling resources.");
            }
        }

        /// <summary>IOptionsMonitor callback (any thread): queue the settings and wake the loop.</summary>
        private void OnSettingsChanged(DeviceSettings updated)
        {
            Volatile.Write(ref _pendingSettings, updated);

            // Always wake, even if the value equals the current settings: an A→B→A revert can
            // race with the loop applying B. The semaphore's cap of 1 prevents spinning, and the
            // loop ignores values equal to what is already in effect.
            try
            {
                _settingsChanged.Release();
            }
            catch (SemaphoreFullException)
            {
                // A wake-up is already pending; the loop will read the latest settings.
            }
        }

        /// <summary>Applies queued settings on the loop thread. Returns true if anything changed.</summary>
        private bool ApplyPendingSettings(out bool intervalChanged)
        {
            intervalChanged = false;
            var updated = Interlocked.Exchange(ref _pendingSettings, null);
            if (updated == null || updated == _settings)
            {
                return false;
            }

            _logger.LogInformation("Settings changed: {Settings}", updated.Describe());
            intervalChanged = updated.SampleIntervalMs != _settings.SampleIntervalMs;
            Volatile.Write(ref _settings, updated);
            _connection.Configure(updated);
            return true;
        }

        private void LogStartupDiagnostics(DeviceSettings settings)
        {
            var assembly = typeof(MonitorService).Assembly;
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString()
                ?? "unknown";
            var overrides = DaemonConfiguration.GetEnvironmentOverrideNames();

            _logger.LogInformation("ResourceMonitorDaemon {Version} starting (.NET {Runtime}).", version, Environment.Version);
            _logger.LogInformation(
                "Settings sources: appsettings={AppSettings} ({AppSettingsState}), user={UserSettings} ({UserSettingsState}), env overrides={Overrides}.",
                DaemonPaths.AppSettingsPath, File.Exists(DaemonPaths.AppSettingsPath) ? "present" : "missing",
                _settingsStore.FilePath, File.Exists(_settingsStore.FilePath) ? "present" : "not created",
                overrides.Count == 0 ? "none" : string.Join(", ", overrides));
            _logger.LogInformation("Effective settings: {Settings}", settings.Describe());
            _logger.LogInformation("Log file: {LogFile}", DaemonPaths.LogFilePath);
            _logger.LogInformation(
                "Runtime: working set {WorkingSetMb:F1} MB, managed heap {HeapMb:F1} MB. MQTT: not connected yet (connecting in background).",
                Environment.WorkingSet / (1024.0 * 1024.0), GC.GetTotalMemory(false) / (1024.0 * 1024.0));
        }

        public void Pause()
        {
            _isPaused = true;
            _logger.LogInformation("Monitoring send paused");
        }

        public void Resume()
        {
            _isPaused = false;
            _logger.LogInformation("Monitoring send resumed");
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            // Idempotent: a second stop (e.g. a duplicate host stop) is a no-op.
            if (Interlocked.Exchange(ref _stopRequested, 1) == 1)
            {
                return;
            }

            _logger.LogInformation("ResourceMonitorDaemon stopping...");
            _settingsSubscription?.Dispose();

            // Cancel in-flight connect attempts first so shutdown never waits for a timeout.
            var connectionClosed = _connection.DisposeAsync().AsTask();
            try
            {
                await base.StopAsync(cancellationToken);
                await connectionClosed.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Shutdown did not complete before the stop timeout.");
            }
            finally
            {
                _cpuCounter?.Dispose();
            }
        }
    }
}
