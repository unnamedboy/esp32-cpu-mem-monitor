using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ResourceMonitorDaemon.Observability;

namespace ResourceMonitorDaemon
{
    internal static class Program
    {
        // The exe directory may not be writable, so the log lives under %LocalAppData%.
        private static readonly string _logFilePath = DaemonPaths.LogFilePath;

        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();
            TryCreateAppDataDirectory();
            var configurationErrors = new DeferredWarningLog();

            var builder = Host.CreateDefaultBuilder()
                .UseContentRoot(AppContext.BaseDirectory)
                .ConfigureAppConfiguration((context, configuration) =>
                {
                    // Replace the generic defaults (current-directory appsettings, unprefixed
                    // environment, command line) with the daemon's layered sources:
                    // appsettings.json < user settings.json < RESMON_* environment variables.
                    configuration.Sources.Clear();
                    configuration.AddDaemonSources(AppContext.BaseDirectory, DaemonPaths.UserSettingsPath, configurationErrors.Report);
                })
                .ConfigureServices((hostContext, services) =>
                {
                    services.AddDeviceSettings();
                    services.AddSingleton<ISettingsStore>(provider =>
                        new UserSettingsStore(DaemonPaths.UserSettingsPath, provider.GetRequiredService<ILogger<UserSettingsStore>>()));
                    services.AddSingleton(provider => new MqttConnection(provider.GetRequiredService<ILogger<MqttConnection>>()));
                    services.AddObservability();   // optional Loki sink; before MonitorService so it stops last
                    services.AddSingleton<MonitorService>();
                    services.AddHostedService(provider => provider.GetRequiredService<MonitorService>());
                })
                .ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.AddConsole();
                    logging.AddProvider(new DaemonLoggerProvider(_logFilePath));
                    logging.AddLokiForwarding();
                });

            using var host = builder.Build();
            configurationErrors.Attach(host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ResourceMonitorDaemon.Configuration"));

            // Settings saved by the tray dialog (or pairing wizard) reach MonitorService through
            // the user settings file watcher and IOptionsMonitor; no explicit reload is needed.
            var settingsStore = host.Services.GetRequiredService<ISettingsStore>();

            // Create the tray first so its notification handler is attached before the
            // connection's first attempt can fail.
            var svc = host.Services.GetRequiredService<MonitorService>();
            var tray = new TrayContext(host, _logFilePath, svc, settingsStore);

            try
            {
                host.StartAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                // Do not leave an orphaned tray icon behind a process that is about to exit.
                tray.Dispose();
                host.Services.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("ResourceMonitorDaemon.Program")
                    .LogCritical(ex, "Resource Monitor Daemon failed to start.");
                MessageBox.Show(
                    $"启动失败 (Failed to start):{Environment.NewLine}{ex.GetBaseException().Message}{Environment.NewLine}{Environment.NewLine}{_logFilePath}",
                    "Resource Monitor Daemon",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            Application.Run();

            try
            {
                host.StopAsync().GetAwaiter().GetResult();
            }
            catch (ObjectDisposedException)
            {
                // The tray Exit handler already stopped and disposed the host.
            }
        }

        private static void TryCreateAppDataDirectory()
        {
            try
            {
                Directory.CreateDirectory(DaemonPaths.AppDataDirectory);
            }
            catch (Exception)
            {
                // Logging and settings writes report their own errors; startup must continue.
            }
        }
    }
}
