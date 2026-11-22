using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace ResourceMonitorDaemon
{
    public class TrayContext : IDisposable
    {
        private readonly IHost _host;
        private readonly string _logFilePath;
        private readonly NotifyIcon _notifyIcon;
        private readonly MonitorService _monitorService;
        private readonly ISettingsStore _settingsStore;
        private readonly IOptionsMonitor<DeviceSettings> _settingsMonitor;
        private readonly SynchronizationContext? _uiContext;
        private readonly System.Windows.Forms.Timer _updateTimer;
        private ToolStripMenuItem? _pauseResumeItem;
        private SettingsForm? _settingsForm;
        private bool _exiting;

        public TrayContext(IHost host, string logFilePath, MonitorService monitorService, ISettingsStore settingsStore)
        {
            _host = host;
            _logFilePath = logFilePath;
            _monitorService = monitorService;
            _settingsStore = settingsStore;
            _settingsMonitor = host.Services.GetRequiredService<IOptionsMonitor<DeviceSettings>>();

            var contextMenu = new ContextMenuStrip();

            // Creating the first control installs the WinForms synchronization context;
            // background notifications are marshalled through it to the UI thread.
            _uiContext = SynchronizationContext.Current;
            
            _pauseResumeItem = new ToolStripMenuItem(
                _monitorService.IsPaused ? "▶ 恢复发送 (Resume)" : "⏸ 暂停发送 (Pause)",
                null,
                OnPauseResumeClick
            );
            contextMenu.Items.Add(_pauseResumeItem);
            
            contextMenu.Items.Add("-");
            contextMenu.Items.Add("⚙ 设置 (Settings)…", null, OnSettingsClick);
            contextMenu.Items.Add("查看日志 (Log)", null, OnViewLog);
            contextMenu.Items.Add("-");
            contextMenu.Items.Add("退出 (Exit)", null, OnExitAsync);

            _notifyIcon = new NotifyIcon
            {
                Icon = SystemIcons.Shield,
                ContextMenuStrip = contextMenu,
                Text = "Resource Monitor Daemon",
                Visible = true
            };

            _notifyIcon.DoubleClick += OnViewLog;
            
            _monitorService.OnNotification = (title, message, icon) =>
            {
                _uiContext?.Post(_ => ShowBalloon(5000, title, message, icon), null);
            };
            
            _updateTimer = new System.Windows.Forms.Timer();
            _updateTimer.Interval = 1000;
            _updateTimer.Tick += (s, e) => UpdatePauseResumeLabel();
            _updateTimer.Start();
        }

        private void UpdatePauseResumeLabel()
        {
            if (_pauseResumeItem != null)
            {
                string newText = _monitorService.IsPaused 
                    ? "▶ 恢复发送 (Resume)" 
                    : "⏸ 暂停发送 (Pause)";
                
                if (_pauseResumeItem.Text != newText)
                {
                    _pauseResumeItem.Text = newText;
                }
            }
        }

        private void OnPauseResumeClick(object? sender, EventArgs e)
        {
            if (_monitorService.IsPaused)
            {
                _monitorService.Resume();
                _notifyIcon.ShowBalloonTip(2000, "已恢复", "监控发送已恢复运行", ToolTipIcon.Info);
            }
            else
            {
                _monitorService.Pause();
                _notifyIcon.ShowBalloonTip(2000, "已暂停", "监控发送已暂停", ToolTipIcon.Info);
            }
        }

        private void ShowBalloon(int timeoutMs, string title, string message, ToolTipIcon icon)
        {
            try
            {
                if (_notifyIcon.Visible)
                {
                    _notifyIcon.ShowBalloonTip(timeoutMs, title, message, icon);
                }
            }
            catch (ObjectDisposedException)
            {
                // Notification raced with exit.
            }
        }

        private void OnSettingsClick(object? sender, EventArgs e)
        {
            if (_settingsForm is { IsDisposed: false })
            {
                _settingsForm.Activate();
                return;
            }

            _settingsForm = new SettingsForm(
                // Fresh effective settings: MonitorService.CurrentSettings lags until its next tick.
                _settingsMonitor.CurrentValue,
                DaemonConfiguration.LoadLowerLayers(AppContext.BaseDirectory),
                _settingsStore,
                DaemonConfiguration.GetEnvironmentOverriddenDeviceFields());
            _settingsForm.FormClosed += (_, _) =>
            {
                if (_settingsForm?.DialogResult == DialogResult.OK)
                {
                    var overridden = _settingsForm.EnvironmentOverriddenFields;
                    var message = overridden.Count == 0
                        ? "新设置已生效，无需重启 (applied without restart)"
                        : "已应用；以下字段仍由环境变量决定 (applied; still overridden by environment variables): " + string.Join(", ", overridden);
                    ShowBalloon(3000, "设置已保存 (Settings saved)", message, ToolTipIcon.Info);
                }

                _settingsForm?.Dispose();
                _settingsForm = null;
            };
            _settingsForm.Show();
            _settingsForm.Activate();
        }

        private void OnViewLog(object? sender, EventArgs e)
        {
            if (File.Exists(_logFilePath))
            {
                Process.Start(new ProcessStartInfo(_logFilePath) { UseShellExecute = true });
            }
            else
            {
                MessageBox.Show("暂无日志记录！", "Daemon", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        /// <summary>Removes the tray icon without stopping the host (used if start-up fails).</summary>
        public void Dispose()
        {
            _updateTimer.Stop();
            _updateTimer.Dispose();
            _settingsForm?.Close();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }

        private async void OnExitAsync(object? sender, EventArgs e)
        {
            if (_exiting)
            {
                return;
            }

            // Hide the icon and disable the menu first so a slow stop cannot be re-triggered.
            _exiting = true;
            _updateTimer.Stop();
            if (_notifyIcon.ContextMenuStrip != null)
            {
                _notifyIcon.ContextMenuStrip.Enabled = false;
            }

            _notifyIcon.Visible = false;
            _settingsForm?.Close();

            try
            {
                await _host.StopAsync(TimeSpan.FromSeconds(3));
            }
            catch (Exception)
            {
                // Exit regardless; MonitorService logs stop problems itself.
            }

            _host.Dispose();
            _notifyIcon.Dispose();
            Application.Exit();
        }
    }
}
