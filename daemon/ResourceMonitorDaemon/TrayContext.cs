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
        private readonly string _logFilePath;
        private readonly NotifyIcon _notifyIcon;
        private readonly MonitorService _monitorService;
        private readonly ISettingsStore _settingsStore;
        private readonly IOptionsMonitor<DeviceSettings> _settingsMonitor;
        private readonly ILoggerFactory _loggerFactory;
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
            _logFilePath = logFilePath;
            _monitorService = monitorService;
            _settingsStore = settingsStore;
            _settingsMonitor = host.Services.GetRequiredService<IOptionsMonitor<DeviceSettings>>();
            _loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
            _firstRunPrompt = FirstRunPairingPrompt.CreateDefault(_loggerFactory.CreateLogger<FirstRunPairingPrompt>());

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
            contextMenu.Items.Add(PairingText.TrayMenuItem, null, (_, _) => OpenPairingWizard());
            contextMenu.Items.Add("查看日志 (Log)", null, OnViewLog);
            contextMenu.Items.Add("-");
            contextMenu.Items.Add("退出 (Exit)", null, OnExit);

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
                ShowBalloon(2000, "已恢复", "监控发送已恢复运行", ToolTipIcon.Info);
            }
            else
            {
                _monitorService.Pause();
                ShowBalloon(2000, "已暂停", "监控发送已暂停", ToolTipIcon.Info);
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
        /// (no fallback address, never connected, prompt never shown) is shown as a one-time
        /// "click to pair" balloon that keeps the failure's title and states the failure; see
        /// <see cref="FirstRunPairingPrompt"/>. The prompt is recorded as shown even if Windows
        /// suppresses the balloon (Focus Assist / notifications off): at most once, by design.
        /// </summary>
        private void ShowNotification(string title, string message, ToolTipIcon icon)
        {
            var isFailure = icon == ToolTipIcon.Error && !_monitorService.IsConnected;
            if (!_exiting && _firstRunPrompt.TryClaim(isFailure, _settingsMonitor.CurrentValue))
            {
                ShowBalloon(15000, title, PairingText.FirstRunBalloonText, icon);
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
                _loggerFactory);
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
                // Fresh effective settings: MonitorService.CurrentSettings can briefly lag a reload.
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

        /// <summary>
        /// Removes the tray icon. Never stops the host: <c>Program.Main</c> owns stopping and
        /// disposing it.
        /// </summary>
        public void Dispose()
        {
            _updateTimer.Stop();
            _updateTimer.Dispose();
            _settingsForm?.Close();
            _pairingWizard?.Close();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }

        private void OnExit(object? sender, EventArgs e)
        {
            if (_exiting)
            {
                return;
            }

            // Hide the icon and disable the menu immediately so Exit feels instant and cannot be
            // re-triggered; Program.Main stops the host (3 s budget) after the message loop ends.
            _exiting = true;
            _updateTimer.Stop();
            if (_notifyIcon.ContextMenuStrip != null)
            {
                _notifyIcon.ContextMenuStrip.Enabled = false;
            }

            _notifyIcon.Visible = false;
            _settingsForm?.Close();
            _pairingWizard?.Close();
            Application.ExitThread();
        }
    }
}
