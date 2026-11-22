using System.Globalization;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using ResourceMonitorDaemon.Localization;

namespace ResourceMonitorDaemon.Observability
{
    /// <summary>
    /// Small dialog for the optional Grafana Loki sink (tray "Observability…").
    /// </summary>
    /// <remarks>
    /// Same rules as <see cref="SettingsForm"/>: shows the effective values, fields overridden by
    /// <c>RESMON_Observability__Loki__*</c> are read-only and never saved, and Save touches only
    /// fields the user changed (<see cref="UserLokiSettingsPatch.ApplyDialogEdit"/>). The token box
    /// is masked and never shows a stored token: an empty box keeps the saved token, typing replaces
    /// it (stored DPAPI-encrypted), and "Clear" removes it. "Test" pushes one line with the values
    /// currently in the dialog and shows the HTTP result. Every text comes from the
    /// <see cref="StringSet"/> taken when the dialog is created, so it keeps its language while open;
    /// validation messages are shown in that language and logged in English.
    /// </remarks>
    public sealed class ObservabilityForm : Form
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(12);

        /// <summary>Example endpoints; the same in every language.</summary>
        private const string UrlPlaceholder = "https://logs-prod-xxx.grafana.net  /  http://loki:3100";

        private readonly StringSet _text = Strings.Current;   // the dialog's language, fixed when it opens
        private readonly IObservabilitySettingsStore _store;
        private readonly ISecretProtector _protector;
        private readonly ILogger _logger;
        private readonly LokiSettings _effective;
        private readonly LokiSettings _lowerLayers;
        private readonly LokiSettings _displayed;
        private readonly IReadOnlySet<string> _environmentFields;
        private readonly bool _tokenFromEnvironment;
        private readonly bool _hasSavedToken;
        private readonly CheckBox _enabled;
        private readonly TextBox _url;
        private readonly ComboBox _authMode;
        private readonly TextBox _username;
        private readonly TextBox _token;
        private readonly Button _clearToken;
        private readonly TextBox _tenant;
        private readonly CheckBox _hostLabel;
        private readonly NumericUpDown _metricsInterval;
        private readonly Button _test;
        private readonly Label _testResult;
        private bool _clearRequested;
        private CancellationTokenSource? _testCancellation;

        public ObservabilityForm(
            LokiSettings effective,
            LokiSettings lowerLayers,
            UserLokiSettingsPatch saved,
            IObservabilitySettingsStore store,
            ISecretProtector protector,
            IReadOnlySet<string> environmentOverriddenFields,
            ILogger logger)
        {
            _store = store;
            _protector = protector;
            _logger = logger;
            _effective = effective;
            _lowerLayers = lowerLayers;
            _environmentFields = environmentOverriddenFields;
            _tokenFromEnvironment = environmentOverriddenFields.Contains(LokiSettings.TokenKey);
            _hasSavedToken = !string.IsNullOrEmpty(saved.TokenProtected);
            EnvironmentOverriddenFields = UserLokiSettingsPatch.DialogFieldNames.Append(LokiSettings.TokenKey)
                .Where(environmentOverriddenFields.Contains)
                .ToList();

            Text = _text.Observability_WindowTitle;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(12);
            Icon = SystemIcons.Shield;

            var layout = new TableLayoutPanel
            {
                ColumnCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            _enabled = new CheckBox { Text = _text.Observability_EnabledCheckBox, Checked = effective.Enabled, AutoSize = true };
            _url = new TextBox { Text = effective.Url, Width = 340, PlaceholderText = UrlPlaceholder };
            _authMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
            _authMode.Items.AddRange(Enum.GetNames<LokiAuthMode>());
            _authMode.SelectedItem = effective.AuthMode.ToString();
            _username = new TextBox { Text = effective.Username, Width = 200 };
            _token = new TextBox
            {
                Width = 240,
                UseSystemPasswordChar = true,
                PlaceholderText = _tokenFromEnvironment ? _text.Observability_TokenFromEnvironment : _hasSavedToken ? _text.Observability_TokenSaved : _text.Observability_TokenNotSet,
            };
            _clearToken = new Button { Text = _text.Observability_ClearToken, AutoSize = true, Enabled = _hasSavedToken && !_tokenFromEnvironment };
            _clearToken.Click += OnClearToken;
            _tenant = new TextBox { Text = effective.TenantId, Width = 200, PlaceholderText = _text.Observability_TenantPlaceholder };
            _hostLabel = new CheckBox { Text = _text.Observability_HostLabelCheckBox, Checked = effective.IncludeHostLabel, AutoSize = true };
            _metricsInterval = new NumericUpDown
            {
                Minimum = LokiSettingsValidator.MinMetricsIntervalSeconds,
                Maximum = LokiSettingsValidator.MaxMetricsIntervalSeconds,
                Value = Math.Clamp(effective.MetricsIntervalSeconds, LokiSettingsValidator.MinMetricsIntervalSeconds, LokiSettingsValidator.MaxMetricsIntervalSeconds),
                Width = 100,
            };

            AddField(layout, _text.Observability_Enabled, nameof(LokiSettings.Enabled), _enabled);
            AddField(layout, _text.Observability_Url, nameof(LokiSettings.Url), _url);
            AddField(layout, _text.Observability_AuthMode, nameof(LokiSettings.AuthMode), _authMode);
            AddField(layout, _text.Observability_Username, nameof(LokiSettings.Username), _username);
            var tokenRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            tokenRow.Controls.Add(_token);
            tokenRow.Controls.Add(_clearToken);
            if (_tokenFromEnvironment)
            {
                _token.ReadOnly = true;
            }

            AddRow(layout, _tokenFromEnvironment ? _text.Common_EnvFieldLabel(_text.Observability_Token) : _text.Common_FieldLabel(_text.Observability_Token), tokenRow);
            AddField(layout, _text.Observability_Tenant, nameof(LokiSettings.TenantId), _tenant);
            AddField(layout, _text.Observability_HostLabel, nameof(LokiSettings.IncludeHostLabel), _hostLabel);
            AddField(layout, _text.Observability_MetricsInterval, nameof(LokiSettings.MetricsIntervalSeconds), _metricsInterval);

            _test = new Button { Text = _text.Observability_Test, AutoSize = true };
            _test.Click += OnTest;
            _testResult = new Label { AutoSize = true, MaximumSize = new Size(360, 0), Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) };
            var testRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            testRow.Controls.Add(_test);
            testRow.Controls.Add(_testResult);
            AddRow(layout, string.Empty, testRow);

            AddNote(layout, _text.Observability_TokenNote, SystemColors.GrayText);
            AddNote(layout, _text.Common_SavedTo(store.FilePath), SystemColors.GrayText);
            if (EnvironmentOverriddenFields.Count > 0)
            {
                AddNote(layout, _text.Common_EnvironmentFieldsNote(string.Join(", ", EnvironmentOverriddenFields)), Color.DarkOrange);
            }

            var save = new Button { Text = _text.Common_Save, AutoSize = true };
            var cancel = new Button { Text = _text.Common_Cancel, AutoSize = true, DialogResult = DialogResult.Cancel };
            save.Click += OnSave;

            // The form is shown modeless, where a button's DialogResult does not close it.
            cancel.Click += (_, _) => Close();
            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 8, 0, 0),
            };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(save);
            layout.Controls.Add(buttons, 0, layout.RowCount++);
            layout.SetColumnSpan(buttons, 2);

            Controls.Add(layout);
            AcceptButton = save;
            CancelButton = cancel;
            FormClosed += (_, _) => _testCancellation?.Cancel();

            // Baseline for "did the user change this field?" — read through the same path as Save.
            _displayed = ReadControls();
        }

        /// <summary>Dialog fields (and Token) whose effective value comes from an environment variable.</summary>
        public IReadOnlyList<string> EnvironmentOverriddenFields { get; }

        private void AddField(TableLayoutPanel layout, string label, string field, Control control)
        {
            var fromEnvironment = _environmentFields.Contains(field);
            if (fromEnvironment)
            {
                if (control is TextBox textBox)
                {
                    textBox.ReadOnly = true;
                }
                else
                {
                    control.Enabled = false;
                }
            }

            AddRow(layout, fromEnvironment ? _text.Common_EnvFieldLabel(label) : _text.Common_FieldLabel(label), control);
        }

        private static void AddRow(TableLayoutPanel layout, string label, Control control)
        {
            var row = layout.RowCount++;
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) }, 0, row);
            layout.Controls.Add(control, 1, row);
        }

        private static void AddNote(TableLayoutPanel layout, string text, Color color)
        {
            var row = layout.RowCount++;
            var note = new Label { Text = text, AutoSize = true, MaximumSize = new Size(520, 0), ForeColor = color, Margin = new Padding(3, 8, 3, 0) };
            layout.Controls.Add(note, 0, row);
            layout.SetColumnSpan(note, 2);
        }

        private LokiSettings ReadControls() => _effective with
        {
            Enabled = _enabled.Checked,
            Url = _url.Text.Trim(),
            AuthMode = Enum.TryParse<LokiAuthMode>(_authMode.SelectedItem as string, out var mode) ? mode : LokiAuthMode.None,
            Username = _username.Text.Trim(),
            TenantId = _tenant.Text.Trim(),
            IncludeHostLabel = _hostLabel.Checked,
            MetricsIntervalSeconds = (int)_metricsInterval.Value,
        };

        /// <summary>The token the dialog would use: typed, else the current one unless cleared.</summary>
        private string CandidateToken()
        {
            var typed = _token.Text.Trim();
            if (_tokenFromEnvironment)
            {
                return _effective.Token;
            }

            return typed.Length > 0 ? typed : _clearRequested ? string.Empty : _effective.Token;
        }

        private void OnClearToken(object? sender, EventArgs e)
        {
            _clearRequested = true;
            _token.Text = string.Empty;
            _token.PlaceholderText = _text.Observability_TokenWillBeCleared;
            _clearToken.Enabled = false;
        }

        private async void OnTest(object? sender, EventArgs e)
        {
            var candidate = ReadControls() with { Token = CandidateToken() };
            if (LokiSettingsValidator.TokenProblem(candidate.Token) is { } tokenError)
            {
                ShowTestResult(false, tokenError.Format(_text.Culture));
                return;
            }

            if (LokiUrl.SendsCredentialsInsecurely(candidate)
                && MessageBox.Show(this, _text.Observability_InsecureTestQuestion, _text.Observability_MessageBoxCaption, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                ShowTestResult(null, _text.Observability_TestNotSent);
                return;
            }

            _test.Enabled = false;
            ShowTestResult(null, _text.Observability_Testing);
            _testCancellation?.Dispose();
            _testCancellation = new CancellationTokenSource(TestTimeout);
            try
            {
                var result = await LokiPushClient.TestAsync(candidate, _testCancellation.Token);
                ShowTestResult(result.Outcome == LokiPushOutcome.Success, DescribeTestResult(result, _text.Culture));
            }
            catch (OperationCanceledException)
            {
                ShowTestResult(false, _text.Observability_TestCancelled);
            }
            catch (Exception ex)
            {
                ShowTestResult(false, LokiRedactor.Redact(ex.Message, candidate));
            }
            finally
            {
                if (!IsDisposed)
                {
                    _test.Enabled = true;
                }
            }
        }

        /// <summary>
        /// The Test result detail: the HTTP status and the server's reason/body as received, or the
        /// daemon's own description (timeout, invalid URL) in the UI language.
        /// </summary>
        internal static string DescribeTestResult(LokiPushResult result, CultureInfo culture) =>
            result.StatusCode is null && result.LocalizedDetail is { } detail ? detail.Format(culture) : result.ToString();

        private void ShowTestResult(bool? success, string text)
        {
            if (IsDisposed)
            {
                return;
            }

            _testResult.ForeColor = success switch
            {
                true => Color.DarkGreen,
                false => Color.Firebrick,
                null => SystemColors.ControlText,
            };
            _testResult.Text = success switch
            {
                true => _text.Observability_TestSucceeded(text),
                false => _text.Observability_TestFailed(text),
                null => text,
            };
        }

        private void OnSave(object? sender, EventArgs e)
        {
            var edited = ReadControls();
            var typedToken = _tokenFromEnvironment ? string.Empty : _token.Text.Trim();

            var errors = LokiSettingsValidator.Problems(edited with { Token = typedToken }).Select(problem => problem.Format(_text.Culture)).ToList();
            if (errors.Count > 0)
            {
                MessageBox.Show(this, string.Join(Environment.NewLine, errors), _text.Common_InvalidSettingsCaption, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (edited.Enabled
                && LokiUrl.SendsCredentialsInsecurely(edited with { Token = CandidateToken() })
                && MessageBox.Show(
                    this,
                    _text.Observability_InsecureSaveQuestion,
                    _text.Observability_MessageBoxCaption,
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return;
            }

            try
            {
                _store.UpdateLoki(patch =>
                {
                    patch.ApplyDialogEdit(_lowerLayers, _displayed, edited, _environmentFields);
                    if (_tokenFromEnvironment)
                    {
                        return;   // never persist or clear anything because of an env token
                    }

                    if (typedToken.Length > 0)
                    {
                        patch.SetToken(typedToken, _protector);
                    }
                    else if (_clearRequested)
                    {
                        patch.TokenProtected = null;
                    }
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.Cryptography.CryptographicException)
            {
                // ex.Message never contains the token (validators and DPAPI errors do not repeat it).
                _logger.LogWarning("Observability dialog: could not save to {Path}: {Error}", _store.FilePath, ex.Message);
                MessageBox.Show(this, SettingsSaveFailure.Describe(ex, _store.FilePath, _text), _text.Observability_MessageBoxCaption, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _token.Text = string.Empty;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
