using System.Globalization;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResourceMonitorDaemon.Localization;
using ResourceMonitorDaemon.Provisioning;

namespace ResourceMonitorDaemon.Pairing
{
    /// <summary>
    /// "Pair device" wizard: scan for <c>PROV_RESMON_*</c> devices over BLE, get the pairing
    /// code (automatically over USB, or typed), send the Wi-Fi credentials, then save the reported IPv4
    /// address as <c>FallbackAddress</c> and wait for the daemon's own MQTT connection.
    /// </summary>
    /// <remarks>
    /// Runs on the UI thread with async/await and never blocks it. Closing the form cancels every
    /// in-flight operation (scan, USB query, provisioning, checks); <see cref="ProvisioningService"/>
    /// then disposes the BLE connection. The Wi-Fi password only lives in the password box and in the
    /// <see cref="ProvisioningService.ProvisionAsync"/> call: the box is cleared after every attempt and
    /// the password is never logged or saved. Decisions and messages come from
    /// <see cref="PairingOutcomePresenter"/>. Texts come from <see cref="Strings"/>: labels are set once
    /// when the form is created, so an open wizard keeps its language if the UI language changes;
    /// status messages shown later use the language current at that moment.
    /// </remarks>
    public sealed class PairingWizardForm : Form
    {
        private const int ContentWidth = 420;
        private static readonly TimeSpan DaemonConnectWait = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan DaemonPollInterval = TimeSpan.FromMilliseconds(500);

        private const int StepConnect = 0;
        private const int StepSecure = 1;
        private const int StepSend = 2;
        private const int StepWifi = 3;
        private const int StepSave = 4;
        private const int StepDaemon = 5;

        private enum StepState { Pending, Running, Done, Failed, Skipped, Warning }

        private enum CodeSource { None, Usb, User }

        private enum Tone { Info, Success, Warning, Error }

        private readonly ProvisioningService _service;
        private readonly ISettingsStore _settingsStore;
        private readonly IOptionsMonitor<DeviceSettings> _settingsMonitor;
        private readonly MonitorService _monitor;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _lifetime = new();

        private readonly ListView _devices;
        private readonly Button _rescan;
        private readonly Label _scanStatus;
        private readonly TextBox _code;
        private readonly LinkLabel _codeLink;
        private readonly Label _codeStatus;
        private readonly TextBox _ssid;
        private readonly Label _ssidNote;
        private readonly LinkLabel _useThisPcWifi;
        private readonly TextBox _password;
        private readonly CheckBox _showPassword;
        private readonly Label[] _steps;
        private readonly string[] _stepTexts;
        private readonly Label _result;
        private readonly Label _finalStatus;
        private readonly Button _pair;
        private readonly Button _cancel;

        private CancellationTokenSource? _scanCts;
        private CancellationTokenSource? _popCts;
        private CancellationTokenSource? _operationCts;
        private ulong? _popDeviceAddress;
        private string? _usbPop;
        private CodeSource _codeSource;
        private bool _settingCode;
        private string? _prefilledSsid;
        private bool _busy;
        private bool _scanning;
        private bool _closed;

        public PairingWizardForm(
            ISettingsStore settingsStore,
            IOptionsMonitor<DeviceSettings> settingsMonitor,
            MonitorService monitor,
            ILoggerFactory loggerFactory)
        {
            _settingsStore = settingsStore;
            _settingsMonitor = settingsMonitor;
            _monitor = monitor;
            _logger = loggerFactory.CreateLogger<PairingWizardForm>();
            _service = new ProvisioningService(loggerFactory.CreateLogger<ProvisioningService>());

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = Strings.Pairing_WindowTitle;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(10);
            Icon = SystemIcons.Shield;

            var root = new TableLayoutPanel
            {
                ColumnCount = 1,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            AddRow(root, NewLabel(
                Strings.Pairing_Intro(Strings.Pairing_ReprovisionInstruction),
                SystemColors.ControlText,
                2 * ContentWidth));

            // Two columns (device + code | Wi-Fi + progress) keep the dialog short enough for 150 % scaling.
            var columns = new TableLayoutPanel
            {
                ColumnCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = Padding.Empty,
            };
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var left = NewColumn();
            var right = NewColumn();
            columns.Controls.Add(left, 0, 0);
            columns.Controls.Add(right, 1, 0);
            AddRow(root, columns);

            // 1. Device
            var deviceGroup = NewGroup(Strings.Pairing_DeviceGroup, out var deviceLayout);
            _devices = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                Size = new Size(ContentWidth, 96),
            };
            _devices.Columns.Add(Strings.Pairing_DeviceColumn, 300);
            _devices.Columns.Add(Strings.Pairing_SignalColumn, 120);
            _devices.SelectedIndexChanged += OnDeviceSelectionChanged;
            AddRow(deviceLayout, _devices);
            _rescan = new Button { Text = Strings.Pairing_RescanButton, AutoSize = true };
            _rescan.Click += async (_, _) => await RunSafelyAsync(ScanAsync);
            AddRow(deviceLayout, _rescan);
            _scanStatus = NewLabel(string.Empty, SystemColors.ControlText);
            AddRow(deviceLayout, _scanStatus);
            AddRow(left, deviceGroup);

            // 2. Pairing code
            var codeGroup = NewGroup(Strings.Pairing_CodeGroup, out var codeLayout);
            var codeRow = NewFlow();
            _code = new TextBox { Width = 150, CharacterCasing = CharacterCasing.Upper, MaxLength = 16 };
            _code.TextChanged += OnCodeTextChanged;
            _codeLink = new LinkLabel { AutoSize = true, Visible = false, Margin = new Padding(8, 6, 3, 3) };
            _codeLink.LinkClicked += OnCodeLinkClicked;
            codeRow.Controls.Add(_code);
            codeRow.Controls.Add(_codeLink);
            AddRow(codeLayout, codeRow);
            _codeStatus = NewLabel(Strings.Pairing_SelectDeviceFirst, SystemColors.ControlText);
            AddRow(codeLayout, _codeStatus);
            AddRow(codeLayout, NewLabel(Strings.Pairing_CodeHint, SystemColors.GrayText));
            AddRow(left, codeGroup);

            // 3. Wi-Fi
            var wifiGroup = NewGroup(Strings.Pairing_WifiGroup, out var wifiLayout);
            var wifiGrid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty };
            wifiGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            wifiGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _ssid = new TextBox { Width = 230 };
            _ssid.TextChanged += (_, _) => _ssidNote!.Visible = _prefilledSsid is not null && _ssid.Text == _prefilledSsid;
            _password = new TextBox { Width = 230, UseSystemPasswordChar = true };
            _showPassword = new CheckBox { Text = Strings.Pairing_ShowPassword, AutoSize = true, Margin = new Padding(8, 5, 3, 3) };
            _showPassword.CheckedChanged += (_, _) => _password.UseSystemPasswordChar = !_showPassword.Checked;
            var passwordRow = NewFlow();
            passwordRow.Controls.Add(_password);
            passwordRow.Controls.Add(_showPassword);
            _useThisPcWifi = new LinkLabel { Text = Strings.Pairing_UseThisPcWifi, AutoSize = true, Margin = new Padding(8, 6, 3, 3) };
            _useThisPcWifi.LinkClicked += async (_, _) => await RunSafelyAsync(UseThisPcWifiAsync);
            var ssidRow = NewFlow();
            ssidRow.Controls.Add(_ssid);
            ssidRow.Controls.Add(_useThisPcWifi);
            AddGridRow(wifiGrid, Strings.Pairing_SsidLabel, ssidRow);
            AddGridRow(wifiGrid, Strings.Pairing_PasswordLabel, passwordRow);
            AddRow(wifiLayout, wifiGrid);
            _ssidNote = NewLabel(string.Empty, SystemColors.GrayText);
            _ssidNote.Visible = false;
            AddRow(wifiLayout, _ssidNote);
            AddRow(wifiLayout, NewLabel(Strings.Pairing_WifiNote, SystemColors.GrayText));
            AddRow(right, wifiGroup);

            // 4. Progress
            var progressGroup = NewGroup(Strings.Pairing_ProgressGroup, out var progressLayout);
            // Step texts, in step order: the four ProvisioningStep values, then save and daemon.
            _stepTexts = new[]
            {
                Strings.Pairing_StepConnect,
                Strings.Pairing_StepSecure,
                Strings.Pairing_StepSend,
                Strings.Pairing_StepWifi,
                Strings.Pairing_StepSave,
                Strings.Pairing_StepDaemon,
            };
            _steps = new Label[_stepTexts.Length];
            for (var i = 0; i < _steps.Length; i++)
            {
                _steps[i] = NewLabel(string.Empty, SystemColors.GrayText);
                _steps[i].Margin = new Padding(3, 1, 3, 1);
                AddRow(progressLayout, _steps[i]);
            }

            _result = NewLabel(string.Empty, SystemColors.ControlText);
            _result.Margin = new Padding(3, 8, 3, 3);
            AddRow(progressLayout, _result);
            _finalStatus = NewLabel(string.Empty, Color.DarkGreen);
            _finalStatus.Font = new Font(Font, FontStyle.Bold);
            AddRow(progressLayout, _finalStatus);
            AddRow(right, progressGroup);

            // Buttons
            _pair = new Button { Text = Strings.Pairing_PairButton, AutoSize = true };
            _pair.Click += async (_, _) => await RunSafelyAsync(PairAsync);
            _cancel = new Button { Text = Strings.Common_Close, AutoSize = true };
            _cancel.Click += OnCancelClick;
            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 6, 0, 0),
            };
            buttons.Controls.Add(_cancel);
            buttons.Controls.Add(_pair);
            AddRow(root, buttons);

            Controls.Add(root);
            AcceptButton = _pair;
            CancelButton = _cancel;
            ResetSteps();
            ResumeLayout(false);
            PerformLayout();
        }

        private ProvisioningDeviceInfo? SelectedDevice =>
            _devices.SelectedItems.Count == 1 ? _devices.SelectedItems[0].Tag as ProvisioningDeviceInfo : null;

        // ---- Lifetime ---------------------------------------------------------------------------

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _logger.LogInformation("Pairing wizard opened.");
            // The current SSID is only read when the user asks for it (see UseThisPcWifiAsync).
            await RunSafelyAsync(ScanAsync);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _closed = true;
            _lifetime.Cancel();
            _password.Clear();
            _usbPop = null;
            _logger.LogInformation("Pairing wizard closed.");
            base.OnFormClosed(e);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            // Column widths are not scaled with the form; size them from the (scaled) list width.
            var signalWidth = LogicalToDeviceUnits(110);
            _devices.Columns[1].Width = signalWidth;
            _devices.Columns[0].Width = Math.Max(signalWidth, _devices.ClientSize.Width - signalWidth - SystemInformation.VerticalScrollBarWidth);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Idempotent: Close() and the tray's FormClosed handler may both dispose the form.
                // _lifetime is only cancelled, never disposed: linked sources unregister themselves.
                _closed = true;
                _lifetime.Cancel();
            }

            base.Dispose(disposing);
        }

        /// <summary>Runs an event-handler operation; nothing escapes to the message loop.</summary>
        private async Task RunSafelyAsync(Func<Task> operation)
        {
            try
            {
                await operation();
            }
            catch (Exception) when (_closed)
            {
                // The form went away while the operation was running.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pairing wizard error.");
                ShowResult(PairingOutcomePresenter.PresentException(ex).Message, Tone.Error);
            }
        }

        private CancellationTokenSource NewOperationSource() => CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);

        // ---- 1. Scan --------------------------------------------------------------------------

        private async Task ScanAsync()
        {
            if (_closed || _busy)
            {
                return;
            }

            _scanCts?.Cancel();
            using var cts = NewOperationSource();
            _scanCts = cts;
            _devices.Items.Clear();
            if (_usbPop is null)
            {
                // Selecting the device again after a rescan retries the USB query (e.g. cable plugged in since).
                _popDeviceAddress = null;
            }

            _scanning = true;
            UpdateControls();
            _scanStatus.Text = Strings.Pairing_Scanning;
            var progress = new Progress<ProvisioningDeviceInfo>(device =>
            {
                if (!_closed && ReferenceEquals(_scanCts, cts))
                {
                    AddOrUpdateDevice(device);
                }
            });

            try
            {
                var found = await _service.ScanAsync(ProvisioningService.DefaultScanDuration, progress, cts.Token);
                if (_closed || !ReferenceEquals(_scanCts, cts))
                {
                    return;
                }

                foreach (var device in found)
                {
                    AddOrUpdateDevice(device);
                }

                _scanStatus.Text = PairingOutcomePresenter.DescribeScanResult(_devices.Items.Count);
                if (_devices.Items.Count == 1 && _devices.SelectedItems.Count == 0)
                {
                    _devices.Items[0].Selected = true;
                }

                _logger.LogInformation("Pairing wizard: scan found {Count} device(s).", _devices.Items.Count);
            }
            catch (OperationCanceledException)
            {
                // Rescan, pairing started or wizard closed.
            }
            catch (Exception ex)
            {
                if (_closed || !ReferenceEquals(_scanCts, cts))
                {
                    return;
                }

                _scanStatus.Text = PairingOutcomePresenter.DescribeScanFailure(ex);
                if (ex is ProvisioningTransportException)
                {
                    _logger.LogWarning("Pairing wizard: Bluetooth scan failed: {Error}", ex.Message);
                }
                else
                {
                    _logger.LogError(ex, "Pairing wizard: Bluetooth scan failed.");
                }
            }
            finally
            {
                if (ReferenceEquals(_scanCts, cts))
                {
                    _scanCts = null;
                    if (!_closed)
                    {
                        _scanning = false;
                        UpdateControls();
                    }
                }
            }
        }

        private void AddOrUpdateDevice(ProvisioningDeviceInfo device)
        {
            var signal = string.Create(CultureInfo.InvariantCulture, $"{device.Rssi} dBm");
            foreach (ListViewItem existing in _devices.Items)
            {
                if (existing.Tag is ProvisioningDeviceInfo known && known.BluetoothAddress == device.BluetoothAddress)
                {
                    existing.Tag = device;
                    existing.SubItems[1].Text = signal;
                    return;
                }
            }

            var item = new ListViewItem(device.Name) { Tag = device };
            item.SubItems.Add(signal);
            _devices.Items.Add(item);
        }

        private void OnDeviceSelectionChanged(object? sender, EventArgs e)
        {
            var device = SelectedDevice;
            if (device is null || device.BluetoothAddress == _popDeviceAddress)
            {
                return;
            }

            _popDeviceAddress = device.BluetoothAddress;
            _usbPop = null;
            if (_codeSource == CodeSource.Usb)
            {
                SetCode(string.Empty, CodeSource.None);
            }

            _ = RunSafelyAsync(() => ReadPopFromUsbAsync(device));
        }

        // ---- 2. Pairing code ------------------------------------------------------------------

        private async Task ReadPopFromUsbAsync(ProvisioningDeviceInfo device)
        {
            _popCts?.Cancel();
            using var cts = NewOperationSource();
            _popCts = cts;
            _codeStatus.ForeColor = SystemColors.ControlText;
            _codeStatus.Text = Strings.Pairing_CodeReadingUsb;
            UpdateCodeLink();
            try
            {
                string? pop;
                try
                {
                    pop = await _service.TryReadPopFromUsbAsync(device.Name, cts.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogInformation("Pairing wizard: USB pairing-code query failed: {Error}", ex.Message);
                    pop = null;
                }

                if (_closed || !ReferenceEquals(_popCts, cts))
                {
                    return;
                }

                if (pop is not null && PairingCode.IsValid(pop))
                {
                    _usbPop = pop;
                    if (_codeSource != CodeSource.User)
                    {
                        SetCode(pop, CodeSource.Usb);
                    }

                    _codeStatus.ForeColor = Color.DarkGreen;
                    _codeStatus.Text = Strings.Pairing_CodeReadOverUsb;
                    _logger.LogInformation("Pairing wizard: pairing code for {Device} read over USB.", device.Name);
                }
                else
                {
                    _codeStatus.Text = Strings.Pairing_CodeNotFoundOverUsb;
                }

                UpdateCodeLink();
            }
            catch (OperationCanceledException)
            {
                // Another device selected, pairing started or wizard closed.
                if (!_closed && ReferenceEquals(_popCts, cts))
                {
                    _codeStatus.Text = Strings.Pairing_CodeUsbQueryStopped;
                }
            }
            finally
            {
                if (ReferenceEquals(_popCts, cts))
                {
                    _popCts = null;
                }
            }
        }

        private void SetCode(string code, CodeSource source)
        {
            _settingCode = true;
            try
            {
                _code.UseSystemPasswordChar = source == CodeSource.Usb;
                _code.ReadOnly = source == CodeSource.Usb;
                _code.Text = code;
                _codeSource = source;
            }
            finally
            {
                _settingCode = false;
            }

            UpdateCodeLink();
        }

        private void OnCodeTextChanged(object? sender, EventArgs e)
        {
            if (_settingCode)
            {
                return;
            }

            _codeSource = _code.Text.Length == 0 ? CodeSource.None : CodeSource.User;
            UpdateCodeLink();
        }

        private void OnCodeLinkClicked(object? sender, LinkLabelLinkClickedEventArgs e)
        {
            if (_codeSource == CodeSource.Usb)
            {
                // Manual override: clear the masked code and let the user type one.
                SetCode(string.Empty, CodeSource.None);
                _code.Focus();
            }
            else if (_usbPop is not null)
            {
                SetCode(_usbPop, CodeSource.Usb);
            }
        }

        private void UpdateCodeLink()
        {
            if (_codeSource == CodeSource.Usb)
            {
                _codeLink.Text = Strings.Pairing_CodeEnterManually;
                _codeLink.Visible = true;
            }
            else if (_usbPop is not null)
            {
                _codeLink.Text = Strings.Pairing_CodeUseUsb;
                _codeLink.Visible = true;
            }
            else
            {
                _codeLink.Visible = false;
            }
        }

        // ---- 3. Wi-Fi -------------------------------------------------------------------------

        /// <summary>
        /// Fills the SSID with the PC's current Wi-Fi, on request only: reading the connected SSID can make
        /// Windows 11 show a one-time location-permission prompt, so it never happens automatically.
        /// </summary>
        private async Task UseThisPcWifiAsync()
        {
            if (_closed || _busy)
            {
                return;
            }

            _useThisPcWifi.Enabled = false;
            _ssidNote.ForeColor = SystemColors.GrayText;
            _ssidNote.Text = Strings.Pairing_ReadingThisPcWifi;
            _ssidNote.Visible = true;
            CurrentWifiInfo? info;
            try
            {
                info = await CurrentWifiNetwork.TryGetAsync(_lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                if (!_closed)
                {
                    _useThisPcWifi.Enabled = !_busy;
                }
            }

            if (_closed)
            {
                return;
            }

            if (info is null)
            {
                _prefilledSsid = null;
                _ssidNote.Text = Strings.Pairing_ThisPcWifiUnavailable;
                _ssidNote.Visible = true;
                return;
            }

            _prefilledSsid = info.Ssid;
            _ssid.Text = info.Ssid;
            _ssidNote.ForeColor = info.IsKnownNot24GHz ? Color.DarkOrange : SystemColors.GrayText;
            _ssidNote.Text = info.IsKnownNot24GHz ? Strings.Pairing_SsidPrefilledNot24GHz : Strings.Pairing_SsidPrefilled;
            _ssidNote.Visible = true;
        }

        // ---- 4. Pairing -----------------------------------------------------------------------

        private async Task PairAsync()
        {
            if (_busy || _closed)
            {
                return;
            }

            var device = SelectedDevice;
            var error = PairingOutcomePresenter.ValidateInputs(
                device is not null, _code.Text, _ssid.Text, _password.Text, out var pairingCode, out var invalidField);
            if (error is not null || device is null)
            {
                ShowResult(error ?? string.Empty, Tone.Error);
                FocusField(invalidField);
                return;
            }

            var settings = _settingsMonitor.CurrentValue;

            // Only an MQTT connection established after this point counts as "the daemon reached the
            // device": an older session may still belong to the previous network or device.
            var startGeneration = _monitor.ConnectionGeneration;
            var previousFallback = _monitor.CurrentSettings.FallbackAddress;
            _scanCts?.Cancel();   // a running scan competes with the connection for the radio
            _popCts?.Cancel();
            using var cts = NewOperationSource();
            _operationCts = cts;
            _busy = true;
            UpdateControls();
            ResetSteps();
            _finalStatus.Text = string.Empty;
            ShowResult(Strings.Pairing_InProgress, Tone.Info);

            var progress = new StepProgress(this);
            PairingOutcome outcome;
            try
            {
                outcome = await ProvisionAsync(device, pairingCode, settings, startGeneration, progress, cts.Token);
                if (_closed)
                {
                    return;
                }

                ShowResult(outcome.Message, ToneOf(outcome.Kind));
                if (outcome.Kind == PairingOutcomeKind.Succeeded)
                {
                    await FinishSuccessAsync(outcome, settings, startGeneration, previousFallback, cts.Token);
                }
                else
                {
                    FocusField(outcome.SuggestRescan && outcome.Focus == PairingField.None ? PairingField.Device : outcome.Focus);
                }
            }
            finally
            {
                if (ReferenceEquals(_operationCts, cts))
                {
                    _operationCts = null;
                }

                _busy = false;
                if (!_closed)
                {
                    UpdateControls();
                }
            }
        }

        /// <summary>Runs one provisioning attempt and maps it to an outcome (never throws except when closed).</summary>
        private async Task<PairingOutcome> ProvisionAsync(
            ProvisioningDeviceInfo device,
            string pairingCode,
            DeviceSettings settings,
            int startGeneration,
            StepProgress progress,
            CancellationToken cancellationToken)
        {
            try
            {
                WifiProvisioningResult result;
                string? password = _password.Text;
                try
                {
                    _logger.LogInformation("Pairing wizard: pairing {Device}.", device.Name);
                    result = await _service.ProvisionAsync(device, pairingCode, _ssid.Text, password, progress, cancellationToken);
                }
                finally
                {
                    // Password hygiene: drop our reference and clear the box after every attempt.
                    password = null;
                    progress.Complete();
                    if (!_closed)
                    {
                        _password.Clear();
                        _showPassword.Checked = false;
                    }
                }

                var outcome = PairingOutcomePresenter.Present(result, settings.Host);
                MarkResultSteps(outcome.Kind);
                if (outcome.Kind != PairingOutcomeKind.CheckBroker || _closed)
                {
                    return outcome;
                }

                // The link dropped after the settings were applied: the device may have joined Wi-Fi.
                ShowResult(outcome.Message, Tone.Info);
                SetStep(StepSave, StepState.Running, Strings.Pairing_StepCheckNetwork);
                var reachable = await BrokerProbe.WaitForBrokerAsync(
                    settings.Host,
                    settings.Port,
                    () => IsNewDaemonConnection(startGeneration),
                    PairingOutcomePresenter.BrokerCheckBudget,
                    cancellationToken);
                _logger.LogInformation("Pairing wizard: broker {Host}:{Port} {State} after the Bluetooth link was lost.",
                    settings.Host, settings.Port, reachable ? "reachable" : "not reachable");
                if (!_closed)
                {
                    SetStep(StepSave, reachable ? StepState.Done : StepState.Failed, Strings.Pairing_StepCheckNetwork);
                }

                return PairingOutcomePresenter.PresentBrokerCheck(reachable, settings.Host, settings.Port);
            }
            catch (Exception ex) when (!_closed)
            {
                var outcome = PairingOutcomePresenter.PresentException(ex);
                switch (ex)
                {
                    case OperationCanceledException:
                        _logger.LogInformation("Pairing wizard: attempt cancelled.");
                        break;
                    case ProvisioningException or ArgumentException:
                        _logger.LogWarning("Pairing wizard: attempt failed: {Error}", ex.Message);
                        break;
                    default:
                        _logger.LogError(ex, "Pairing wizard: attempt failed unexpectedly.");
                        break;
                }

                MarkExceptionSteps(progress.LastStep, outcome.Kind);
                return outcome;
            }
        }

        private async Task FinishSuccessAsync(
            PairingOutcome outcome, DeviceSettings settings, int startGeneration, string previousFallback, CancellationToken cancellationToken)
        {
            string? savedAddress = null;
            if (outcome.AddressToSave is { } address)
            {
                SetStep(StepSave, StepState.Running);
                try
                {
                    // Only FallbackAddress is touched; every other user setting stays as it is.
                    await Task.Run(() => _settingsStore.Update(patch => patch.FallbackAddress = address), CancellationToken.None);
                    savedAddress = address;
                    _logger.LogInformation("Pairing wizard: saved FallbackAddress {Address}.", address);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    _logger.LogWarning("Pairing wizard: could not save FallbackAddress: {Error}", ex.Message);
                    if (_closed)
                    {
                        return;
                    }

                    SetStep(StepSave, StepState.Failed);
                    ShowResult($"{outcome.Message}\n{Strings.Pairing_SaveFailed}", Tone.Warning);
                }

                if (_closed)
                {
                    return;
                }

                if (savedAddress is not null)
                {
                    SetStep(StepSave, StepState.Done, Strings.Pairing_StepSaved(savedAddress));
                }
            }
            else if (_steps[StepSave].Tag is StepState.Pending)
            {
                SetStep(StepSave, StepState.Skipped, Strings.Pairing_StepNoAddress);
            }

            var environmentOverride = savedAddress is not null
                && DaemonConfiguration.GetEnvironmentOverriddenDeviceFields().Contains(nameof(DeviceSettings.FallbackAddress));
            if (environmentOverride)
            {
                ShowResult($"{outcome.Message}\n{Strings.Pairing_FallbackOverriddenByEnvironment}", Tone.Warning);
            }

            SetStep(StepDaemon, StepState.Running);
            bool connected;
            try
            {
                var expectedFallback = environmentOverride ? null : savedAddress;
                var requireNewConnection = PairingOutcomePresenter.RequiresNewDaemonConnection(
                    outcome.DeviceWasAlreadyConnected, previousFallback, expectedFallback ?? previousFallback);
                connected = await WaitForDaemonConnectionAsync(expectedFallback, startGeneration, requireNewConnection, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (!_closed)
                {
                    SetStep(StepDaemon, StepState.Skipped);
                }

                return;
            }

            if (_closed)
            {
                return;
            }

            var status = PairingOutcomePresenter.DescribeDaemonStatus(connected, _monitor.IsPaused, settings.Host);
            SetStep(StepDaemon, connected ? StepState.Done : StepState.Warning);
            _finalStatus.ForeColor = connected ? Color.DarkGreen : Color.DarkOrange;
            _finalStatus.Text = status;
            _logger.LogInformation("Pairing wizard: daemon connection after pairing: {State}.", connected ? "connected" : "not yet connected");
        }

        /// <summary>
        /// Waits until the daemon applied the saved address (when one was saved) and has an MQTT
        /// connection, on two consecutive polls; or until it is paused or the wait times out. When
        /// <paramref name="requireNewConnection"/> is set, only a connection established after the attempt
        /// started counts (see <see cref="PairingOutcomePresenter.RequiresNewDaemonConnection"/>).
        /// </summary>
        private async Task<bool> WaitForDaemonConnectionAsync(
            string? expectedFallback, int startGeneration, bool requireNewConnection, CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow + DaemonConnectWait;
            var consecutive = 0;
            while (DateTime.UtcNow < deadline)
            {
                if (_monitor.IsPaused)
                {
                    return false;
                }

                var applied = expectedFallback is null
                    || string.Equals(_monitor.CurrentSettings.FallbackAddress, expectedFallback, StringComparison.OrdinalIgnoreCase);
                var connected = requireNewConnection ? IsNewDaemonConnection(startGeneration) : _monitor.IsConnected;
                consecutive = applied && connected ? consecutive + 1 : 0;
                if (consecutive >= 2)
                {
                    return true;
                }

                await Task.Delay(DaemonPollInterval, cancellationToken);
            }

            return false;
        }

        /// <summary>
        /// True when the daemon is connected through a connection made after <paramref name="startGeneration"/>
        /// was read. Note: with several devices on the network, resmon.local may still name another one.
        /// </summary>
        private bool IsNewDaemonConnection(int startGeneration) =>
            _monitor.IsConnected && _monitor.ConnectionGeneration != startGeneration;

        private void OnCancelClick(object? sender, EventArgs e)
        {
            if (_operationCts is { } operation)
            {
                operation.Cancel();
                return;
            }

            Close();
        }

        // ---- Steps and messages ---------------------------------------------------------------

        private void ResetSteps()
        {
            for (var i = 0; i < _steps.Length; i++)
            {
                SetStep(i, StepState.Pending);
            }
        }

        private void SetStep(int index, StepState state, string? text = null)
        {
            var glyph = state switch
            {
                StepState.Running => "▶",
                StepState.Done => "✓",
                StepState.Failed => "✗",
                StepState.Skipped => "–",
                StepState.Warning => "!",
                _ => "○",
            };
            var label = _steps[index];
            label.Tag = state;
            label.Text = $"{glyph}  {text ?? _stepTexts[index]}";
            label.ForeColor = state switch
            {
                StepState.Running => SystemColors.ControlText,
                StepState.Done => Color.DarkGreen,
                StepState.Failed => Color.Firebrick,
                StepState.Warning => Color.DarkOrange,
                _ => SystemColors.GrayText,
            };
        }

        private void OnStepReported(ProvisioningStep step)
        {
            var current = (int)step;
            for (var i = StepConnect; i < current; i++)
            {
                SetStep(i, StepState.Done);
            }

            SetStep(current, StepState.Running);
        }

        private void MarkResultSteps(PairingOutcomeKind kind)
        {
            if (_closed)
            {
                return;
            }

            for (var i = StepConnect; i < StepWifi; i++)
            {
                SetStep(i, StepState.Done);
            }

            SetStep(StepWifi, kind switch
            {
                PairingOutcomeKind.Succeeded => StepState.Done,
                PairingOutcomeKind.CheckBroker => StepState.Warning,
                _ => StepState.Failed,
            });
        }

        private void MarkExceptionSteps(int lastStep, PairingOutcomeKind kind)
        {
            if (_closed || lastStep < StepConnect)
            {
                return;
            }

            for (var i = StepConnect; i < lastStep; i++)
            {
                SetStep(i, StepState.Done);
            }

            SetStep(lastStep, kind == PairingOutcomeKind.Cancelled ? StepState.Skipped : StepState.Failed);
        }

        private void ShowResult(string message, Tone tone)
        {
            if (_closed)
            {
                return;
            }

            _result.Text = message;
            _result.ForeColor = tone switch
            {
                Tone.Success => Color.DarkGreen,
                Tone.Warning => Color.DarkOrange,
                Tone.Error => Color.Firebrick,
                _ => SystemColors.ControlText,
            };
        }

        private static Tone ToneOf(PairingOutcomeKind kind) => kind switch
        {
            PairingOutcomeKind.Succeeded => Tone.Success,
            PairingOutcomeKind.Failed => Tone.Error,
            _ => Tone.Info,
        };

        private void FocusField(PairingField field)
        {
            if (_closed)
            {
                return;
            }

            Control? target = field switch
            {
                PairingField.Device => _devices.Items.Count > 0 ? _devices : _rescan,
                PairingField.PairingCode => _code.ReadOnly ? _codeLink : _code,
                PairingField.Ssid => _ssid,
                PairingField.Password => _password,
                _ => null,
            };
            if (target is null)
            {
                return;
            }

            target.Focus();
            if (target is TextBox { ReadOnly: false } textBox)
            {
                textBox.SelectAll();
            }
        }

        private void UpdateControls()
        {
            _devices.Enabled = !_busy;
            _rescan.Enabled = !_busy && !_scanning;
            _code.Enabled = !_busy;
            _codeLink.Enabled = !_busy;
            _ssid.Enabled = !_busy;
            _useThisPcWifi.Enabled = !_busy;
            _password.Enabled = !_busy;
            _showPassword.Enabled = !_busy;
            _pair.Enabled = !_busy;
            _cancel.Text = _busy ? Strings.Common_Cancel : Strings.Common_Close;
        }

        // ---- Layout helpers -------------------------------------------------------------------

        private static GroupBox NewGroup(string title, out TableLayoutPanel layout)
        {
            layout = new TableLayoutPanel
            {
                ColumnCount = 1,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var group = new GroupBox
            {
                Text = title,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Padding = new Padding(6),
                Margin = new Padding(3, 6, 3, 3),
            };
            group.Controls.Add(layout);
            return group;
        }

        private static TableLayoutPanel NewColumn()
        {
            var column = new TableLayoutPanel
            {
                ColumnCount = 1,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Margin = Padding.Empty,
            };
            column.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            return column;
        }

        private static FlowLayoutPanel NewFlow() => new()
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = Padding.Empty,
        };

        private static Label NewLabel(string text, Color color, int maxWidth = ContentWidth) => new()
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(maxWidth, 0),
            ForeColor = color,
            Margin = new Padding(3, 4, 3, 3),
        };

        private static void AddRow(TableLayoutPanel layout, Control control)
        {
            layout.Controls.Add(control, 0, layout.RowCount++);
        }

        private static void AddGridRow(TableLayoutPanel grid, string label, Control control)
        {
            var row = grid.RowCount++;
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) }, 0, row);
            grid.Controls.Add(control, 1, row);
        }

        /// <summary>
        /// Records the last reported step synchronously (for marking the failed step) and updates the
        /// step list on the UI thread; reports arriving after the attempt finished are ignored.
        /// </summary>
        private sealed class StepProgress : IProgress<ProvisioningStep>
        {
            private readonly PairingWizardForm _form;
            private readonly SynchronizationContext? _context = SynchronizationContext.Current;
            private int _lastStep = -1;
            private volatile bool _completed;

            public StepProgress(PairingWizardForm form)
            {
                _form = form;
            }

            public int LastStep => Volatile.Read(ref _lastStep);

            public void Complete() => _completed = true;

            public void Report(ProvisioningStep value)
            {
                Volatile.Write(ref _lastStep, (int)value);
                _context?.Post(_ =>
                {
                    if (!_completed && !_form._closed)
                    {
                        _form.OnStepReported(value);
                    }
                }, null);
            }
        }
    }
}
