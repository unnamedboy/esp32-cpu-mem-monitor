using System;
using System.Threading;
using System.Windows.Forms;
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
            var builder = Host.CreateDefaultBuilder()
                .ConfigureServices((hostContext, services) =>
                {
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

            host.StartAsync().GetAwaiter().GetResult();

            var svc = host.Services.GetRequiredService<MonitorService>();
            var tray = new TrayContext(host, _logFilePath, svc);

            Application.Run();

            host.StopAsync().GetAwaiter().GetResult();
        }
    }
}
