using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResourceMonitorDaemon.Pairing;
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
        private PairingWizardForm? _pairingWizard;
        private readonly FirstRunPairingPrompt _firstRunPrompt;
        private bool _pairingBalloonShown;
        private bool _exiting;

        public TrayContext(IHost host, string logFilePath, MonitorService monitorService, ISettingsStore settingsStore)
        {
            _host = host;
            _logFilePath = logFilePath;
            _monitorService = monitorService;
            _settingsStore = settingsStore;
            _settingsMonitor = host.Services.GetRequiredService<IOptionsMonitor<DeviceSettings>>();
            _firstRunPrompt = new FirstRunPairingPrompt(
                FirstRunPairingPrompt.DefaultMarkerPath,
                host.Services.GetRequiredService<ILoggerFactory>().CreateLogger<FirstRunPairingPrompt>());

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
            contextMenu.Items.Add("配对设备 (Pair device)…", null, (_, _) => OpenPairingWizard());
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
            _notifyIcon.BalloonTipClicked += OnBalloonTipClicked;
            _notifyIcon.BalloonTipClosed += (_, _) => _pairingBalloonShown = false;
            
            _monitorService.OnNotification = (title, message, icon) =>
            {
                _uiContext?.Post(_ => ShowNotification(title, message, icon), null);
            };
            
            _updateTimer = new System.Windows.Forms.Timer();
            _updateTimer.Interval = 1000;
            _updateTimer.Tick += (s, e) =>
            {
                UpdatePauseResumeLabel();
                _firstRunPrompt.ObserveConnection(_monitorService.IsConnected);
            };
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
            _pairingBalloonShown = false;
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

        /// <summary>
        /// Shows a connection notification. The first connection failure on an unconfigured PC
        /// (no fallback address, never connected, prompt never shown) is replaced by a one-time
        /// "click to pair" balloon; see <see cref="FirstRunPairingPrompt"/>.
        /// </summary>
        private void ShowNotification(string title, string message, ToolTipIcon icon)
        {
            var isFailure = icon == ToolTipIcon.Error && !_monitorService.IsConnected;
            if (!_exiting && _firstRunPrompt.TryClaim(isFailure, _settingsMonitor.CurrentValue.FallbackAddress))
            {
                ShowBalloon(
                    15000,
                    "未找到设备 (No device found)",
                    "未找到设备，点击进行配对 (No device found — click to pair)\n" +
                    "也可以右键托盘图标 → 配对设备 (or tray menu → Pair device)",
                    ToolTipIcon.Info);
                _pairingBalloonShown = true;
                return;
            }

            ShowBalloon(5000, title, message, icon);
        }

        private void OnBalloonTipClicked(object? sender, EventArgs e)
        {
            if (_pairingBalloonShown)
            {
                _pairingBalloonShown = false;
                OpenPairingWizard();
            }
        }

        private void OpenPairingWizard()
        {
            if (_exiting)
            {
                return;
            }

            if (_pairingWizard is { IsDisposed: false })
            {
                _pairingWizard.Activate();
                return;
            }

            _pairingWizard = new PairingWizardForm(
                _settingsStore,
                _settingsMonitor,
                _monitorService,
                _host.Services.GetRequiredService<ILoggerFactory>());
            _pairingWizard.FormClosed += (_, _) =>
            {
                _pairingWizard?.Dispose();
                _pairingWizard = null;
            };
            _pairingWizard.Show();
            _pairingWizard.Activate();
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
            _pairingWizard?.Close();
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
            _pairingWizard?.Close();

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
