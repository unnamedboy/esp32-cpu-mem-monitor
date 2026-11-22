using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ResourceMonitorDaemon.Observability
{
    /// <summary>
    /// Hosts the optional Loki pipeline: applies <see cref="LokiSettings"/> live, attaches the
    /// device status forwarder to the MQTT connection, runs the sender, logs one startup
    /// diagnostics line and flushes on shutdown within <see cref="ShutdownFlushBudget"/>.
    /// </summary>
    /// <remarks>
    /// Registered before <see cref="MonitorService"/>, so it starts first and stops last (the
    /// host stops services in reverse order), which lets the shutdown messages of the other
    /// services be flushed. Nothing here can fail sampling or MQTT publishing.
    /// </remarks>
    public sealed class ObservabilityService : IHostedService, IHostedLifecycleService
    {
        public static readonly TimeSpan ShutdownFlushBudget = TimeSpan.FromSeconds(2);

        private readonly LokiSink _sink;
        private readonly IOptionsMonitor<LokiSettings> _settingsMonitor;
        private readonly MqttConnection _connection;
        private readonly DeviceStatusForwarder _statusForwarder;
        private readonly ILogger<ObservabilityService> _logger;
        private readonly object _gate = new();
        private IDisposable? _settingsSubscription;

        public ObservabilityService(
            LokiSink sink,
            IOptionsMonitor<LokiSettings> settingsMonitor,
            MqttConnection connection,
            DeviceStatusForwarder statusForwarder,
            ILogger<ObservabilityService> logger)
        {
            _sink = sink;
            _settingsMonitor = settingsMonitor;
            _connection = connection;
            _statusForwarder = statusForwarder;
            _logger = logger;
        }

        public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                _settingsSubscription = _settingsMonitor.OnChange(OnSettingsChanged);
                _sink.Configure(_settingsMonitor.CurrentValue);
                _connection.StatusMessageReceived += _statusForwarder.Handle;
                _sink.Start();
            }
            catch (Exception ex)
            {
                // Observability is optional: never fail the host start.
                _logger.LogError(ex, "Observability could not be started; Loki forwarding is off.");
            }

            return Task.CompletedTask;
        }

        public Task StartedAsync(CancellationToken cancellationToken)
        {
            var settings = _sink.Settings;
            _logger.LogInformation(
                "Observability: Loki {State}; url host={Host}, auth={Auth}, token configured={Token}.",
                settings.IsActive ? "enabled" : "disabled", settings.UrlHost, settings.AuthMode,
                settings.HasToken ? "yes" : "no");
            return Task.CompletedTask;
        }

        public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _connection.StatusMessageReceived -= _statusForwarder.Handle;
            _settingsSubscription?.Dispose();
            await _sink.StopAsync(ShutdownFlushBudget, cancellationToken).ConfigureAwait(false);
        }

        public async Task StoppedAsync(CancellationToken cancellationToken) => await _sink.DisposeAsync().ConfigureAwait(false);

        private void OnSettingsChanged(LokiSettings updated)
        {
            lock (_gate)
            {
                if (updated == _sink.Settings)
                {
                    return;
                }

                _sink.Configure(updated);
            }

            _logger.LogInformation("Observability settings changed: {Settings}", updated.Describe());
        }
    }

    public static class ObservabilityServiceCollectionExtensions
    {
        /// <summary>
        /// Registers the Loki settings, sink, sources and hosted service. Call before registering
        /// <see cref="MonitorService"/> as a hosted service. Also call
        /// <see cref="AddLokiForwarding"/> from <c>ConfigureLogging</c>.
        /// </summary>
        public static IServiceCollection AddObservability(this IServiceCollection services)
        {
            services.TryAddSingleton<LokiEntryQueue>();
            services.TryAddSingleton<ISecretProtector, DpapiSecretProtector>();
            services.AddLokiSettings();
            services.TryAddSingleton(provider => new LokiSink(provider.GetRequiredService<LokiEntryQueue>(), provider.GetRequiredService<ILogger<LokiSink>>()));
            services.TryAddSingleton<DeviceStatusForwarder>();
            services.TryAddSingleton<IMetricsObserver>(provider => new LokiMetricsForwarder(provider.GetRequiredService<LokiSink>()));
            services.TryAddSingleton<IObservabilitySettingsStore>(provider =>
                provider.GetService<ISettingsStore>() as IObservabilitySettingsStore ?? new UserSettingsStore(DaemonPaths.UserSettingsPath));
            services.TryAddSingleton<ObservabilityService>();
            services.AddHostedService(provider => provider.GetRequiredService<ObservabilityService>());
            return services;
        }

        /// <summary>Adds the <see cref="LokiLoggerProvider"/> (after <c>ClearProviders()</c>).</summary>
        public static ILoggingBuilder AddLokiForwarding(this ILoggingBuilder logging)
        {
            logging.Services.TryAddSingleton<LokiEntryQueue>();
            logging.Services.AddSingleton<ILoggerProvider>(provider => new LokiLoggerProvider(provider.GetRequiredService<LokiEntryQueue>()));
            return logging;
        }

        /// <summary>
        /// Registers <see cref="LokiSettings"/> for <c>IOptionsMonitor&lt;LokiSettings&gt;</c>, parsed
        /// leniently and sanitized like <see cref="DeviceSettings"/>, recomputed on every reload.
        /// Warnings go to a category that is never forwarded to Loki.
        /// </summary>
        public static IServiceCollection AddLokiSettings(this IServiceCollection services)
        {
            var deduplicator = new WarningDeduplicator();
            services.AddOptions<LokiSettings>()
                .Configure<IConfiguration, ILoggerFactory, ISecretProtector>((target, configuration, loggerFactory, protector) =>
                {
                    var warnings = new List<string>();
                    var loaded = LokiSettingsLoader.Load(configuration, warnings, protector);
                    CopyTo(loaded, target);
                    if (!deduplicator.IsNew(warnings))
                    {
                        return;
                    }

                    var logger = loggerFactory.CreateLogger("ResourceMonitorDaemon.Observability.Settings");
                    foreach (var warning in warnings)
                    {
                        logger.LogWarning("Invalid setting: {Warning}", warning);
                    }
                });

            services.AddSingleton<IOptionsChangeTokenSource<LokiSettings>>(provider =>
                new ConfigurationChangeTokenSource<LokiSettings>(provider.GetRequiredService<IConfiguration>()));
            return services;
        }

        private static void CopyTo(LokiSettings source, LokiSettings target)
        {
            target.Enabled = source.Enabled;
            target.Url = source.Url;
            target.AuthMode = source.AuthMode;
            target.Username = source.Username;
            target.Token = source.Token;
            target.TokenSource = source.TokenSource;
            target.TenantId = source.TenantId;
            target.IncludeHostLabel = source.IncludeHostLabel;
            target.MetricsIntervalSeconds = source.MetricsIntervalSeconds;
            target.MinLevel = source.MinLevel;
            target.BatchMaxEntries = source.BatchMaxEntries;
            target.QueueCapacity = source.QueueCapacity;
            target.FlushIntervalMs = source.FlushIntervalMs;
            target.Gzip = source.Gzip;
        }
    }
}
