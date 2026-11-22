using System.Windows.Forms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
                    services.AddSingleton<MonitorService>();
                    services.AddHostedService(provider => provider.GetRequiredService<MonitorService>());
                })
                .ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.AddConsole();
                    logging.AddProvider(new DaemonLoggerProvider(_logFilePath));
                });

            using var host = builder.Build();
            configurationErrors.Attach(host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ResourceMonitorDaemon.Configuration"));

            // Apply settings saved from the tray dialog (or pairing wizard) immediately rather
            // than waiting for the file watcher; IOptionsMonitor then notifies MonitorService.
            var settingsStore = host.Services.GetRequiredService<ISettingsStore>();
            if (host.Services.GetRequiredService<IConfiguration>() is IConfigurationRoot configurationRoot)
            {
                settingsStore.Saved += (_, _) => configurationRoot.Reload();
            }

            host.StartAsync().GetAwaiter().GetResult();

            var svc = host.Services.GetRequiredService<MonitorService>();
            var tray = new TrayContext(host, _logFilePath, svc);

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
