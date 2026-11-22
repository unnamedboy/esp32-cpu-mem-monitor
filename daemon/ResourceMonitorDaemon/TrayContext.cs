using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResourceMonitorDaemon.Localization;
using ResourceMonitorDaemon.Observability;
using ResourceMonitorDaemon.Pairing;
using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace ResourceMonitorDaemon
{
    /// <summary>
    /// The tray icon, its menu and balloons, and the dialogs it opens. Menu texts follow the UI
    /// language live (<see cref="UiCulture.Changed"/>); balloons and dialogs use the language current
    /// when they are shown.
    /// </summary>
    public class TrayContext : IDisposable
    {
        private readonly string _logFilePath;
        private readonly NotifyIcon _notifyIcon;
        private readonly MonitorService _monitorService;
        private readonly ISettingsStore _settingsStore;
        private readonly IOptionsMonitor<DeviceSettings> _settingsMonitor;
        private readonly IServiceProvider _services;   // dialog dependencies only; the host is owned by Program.Main
        private readonly ILoggerFactory _loggerFactory;
        private readonly SynchronizationContext? _uiContext;
        private readonly System.Windows.Forms.Timer _updateTimer;
        private readonly ToolStripMenuItem _pauseResumeItem;
        private readonly ToolStripMenuItem _settingsItem;
        private readonly ToolStripMenuItem _pairItem;
        private readonly ToolStripMenuItem _logItem;
        private readonly ToolStripMenuItem _observabilityItem;
        private readonly ToolStripMenuItem _exitItem;
        private readonly UiLanguageMonitor _languageMonitor;
        private SettingsForm? _settingsForm;
        private ObservabilityForm? _observabilityForm;
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
            _services = host.Services;
            _loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
            _firstRunPrompt = FirstRunPairingPrompt.CreateDefault(_loggerFactory.CreateLogger<FirstRunPairingPrompt>());
            _languageMonitor = host.Services.GetRequiredService<UiLanguageMonitor>();

            var contextMenu = new ContextMenuStrip();

            // Creating the first control installs the WinForms synchronization context;
            // background notifications are marshalled through it to the UI thread.
            _uiContext = SynchronizationContext.Current;

            _pauseResumeItem = new ToolStripMenuItem(string.Empty, null, OnPauseResumeClick);
            _settingsItem = new ToolStripMenuItem(string.Empty, null, OnSettingsClick);
            _pairItem = new ToolStripMenuItem(string.Empty, null, (_, _) => OpenPairingWizard());
            _logItem = new ToolStripMenuItem(string.Empty, null, OnViewLog);
            _observabilityItem = new ToolStripMenuItem(string.Empty, null, OnObservabilityClick);
            _exitItem = new ToolStripMenuItem(string.Empty, null, OnExit);
            contextMenu.Items.Add(_pauseResumeItem);
            contextMenu.Items.Add("-");
            contextMenu.Items.Add(_settingsItem);
            contextMenu.Items.Add(_pairItem);
            contextMenu.Items.Add(_logItem);
            contextMenu.Items.Add(_observabilityItem);
            contextMenu.Items.Add("-");
            contextMenu.Items.Add(_exitItem);

            _notifyIcon = new NotifyIcon
            {
                Icon = SystemIcons.Shield,
                ContextMenuStrip = contextMenu,
                Text = Strings.Common_ProductName,
                Visible = true
            };
            ApplyMenuTexts();
            UiCulture.Changed += OnUiCultureChanged;

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
            var newText = _monitorService.IsPaused ? Strings.Tray_Resume : Strings.Tray_Pause;
            if (_pauseResumeItem.Text != newText)
            {
                _pauseResumeItem.Text = newText;
            }
        }

        /// <summary>Sets every menu text from <see cref="Strings"/> in the current UI language.</summary>
        private void ApplyMenuTexts()
        {
            UpdatePauseResumeLabel();
            _settingsItem.Text = Strings.Tray_Settings;
            _pairItem.Text = Strings.Tray_PairDevice;
            _logItem.Text = Strings.Tray_ViewLog;
            _observabilityItem.Text = Strings.Tray_Observability;
            _exitItem.Text = Strings.Tray_Exit;
            _notifyIcon.Text = Strings.Common_ProductName;
        }

        /// <summary>Raised on the thread that changed the language (e.g. the configuration reload thread).</summary>
        private void OnUiCultureChanged(object? sender, EventArgs e) =>
            _uiContext?.Post(_ =>
            {
                if (!_exiting)
                {
                    ApplyMenuTexts();
                }
            }, null);

        private void OnPauseResumeClick(object? sender, EventArgs e)
        {
            if (_monitorService.IsPaused)
            {
                _monitorService.Resume();
                ShowBalloon(2000, Strings.Tray_ResumedTitle, Strings.Tray_ResumedText, ToolTipIcon.Info);
            }
            else
            {
                _monitorService.Pause();
                ShowBalloon(2000, Strings.Tray_PausedTitle, Strings.Tray_PausedText, ToolTipIcon.Info);
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
                // Keeps the failure's title and states the failure in its text.
                ShowBalloon(15000, title, Strings.Pairing_FirstRunBalloonText, icon);
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
                DaemonConfiguration.GetEnvironmentOverriddenDeviceFields(),
                new UiLanguageSetting(
                    _languageMonitor.Setting,
                    DaemonConfiguration.LoadLowerUiLanguage(AppContext.BaseDirectory),
                    DaemonConfiguration.IsUiLanguageOverriddenByEnvironment()),
                _services.GetRequiredService<IUiSettingsStore>(),
                _loggerFactory.CreateLogger<SettingsForm>());
            _settingsForm.FormClosed += (_, _) =>
            {
                if (_settingsForm?.DialogResult == DialogResult.OK)
                {
                    // Switch now (menu and this balloon); the file watcher reload then finds nothing to change.
                    _languageMonitor.ApplySaved(_settingsForm.LanguageSetting);
                    var overridden = _settingsForm.EnvironmentOverriddenFields;
                    var message = overridden.Count == 0
                        ? Strings.Tray_AppliedWithoutRestart
                        : Strings.Tray_AppliedEnvironmentOverride(string.Join(", ", overridden));
                    ShowBalloon(3000, Strings.Tray_SettingsSavedTitle, message, ToolTipIcon.Info);
                }

                _settingsForm?.Dispose();
                _settingsForm = null;
            };
            _settingsForm.Show();
            _settingsForm.Activate();
        }

        private void OnObservabilityClick(object? sender, EventArgs e)
        {
            if (_observabilityForm is { IsDisposed: false })
            {
                _observabilityForm.Activate();
                return;
            }

            var services = _services;
            var store = services.GetRequiredService<IObservabilitySettingsStore>();
            _observabilityForm = new ObservabilityForm(
                services.GetRequiredService<IOptionsMonitor<LokiSettings>>().CurrentValue,
                DaemonConfiguration.LoadLowerLokiLayers(AppContext.BaseDirectory),
                store.LoadLoki(),
                store,
                services.GetRequiredService<ISecretProtector>(),
                DaemonConfiguration.GetEnvironmentOverriddenLokiFields(),
                _loggerFactory.CreateLogger<ObservabilityForm>());
            _observabilityForm.FormClosed += (_, _) =>
            {
                if (_observabilityForm?.DialogResult == DialogResult.OK)
                {
                    ShowBalloon(3000, Strings.Tray_ObservabilitySavedTitle, Strings.Tray_AppliedWithoutRestart, ToolTipIcon.Info);
                }

                _observabilityForm?.Dispose();
                _observabilityForm = null;
            };
            _observabilityForm.Show();
            _observabilityForm.Activate();
        }

        private void OnViewLog(object? sender, EventArgs e)
        {
            if (File.Exists(_logFilePath))
            {
                Process.Start(new ProcessStartInfo(_logFilePath) { UseShellExecute = true });
            }
            else
            {
                MessageBox.Show(Strings.Tray_NoLogYet, Strings.Common_ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        /// <summary>
        /// Removes the tray icon. Never stops the host: <c>Program.Main</c> owns stopping and
        /// disposing it.
        /// </summary>
        public void Dispose()
        {
            UiCulture.Changed -= OnUiCultureChanged;
            _updateTimer.Stop();
            _updateTimer.Dispose();
            _settingsForm?.Close();
            _observabilityForm?.Close();
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
            _observabilityForm?.Close();
            _pairingWizard?.Close();
            Application.ExitThread();
        }
    }
}
