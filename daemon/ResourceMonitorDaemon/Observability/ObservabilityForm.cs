using System.Windows.Forms;

namespace ResourceMonitorDaemon.Observability
{
    /// <summary>
    /// Small dialog for the optional Grafana Loki sink (tray "可观测性 (Observability)…").
    /// </summary>
    /// <remarks>
    /// Same rules as <see cref="SettingsForm"/>: shows the effective values, fields overridden by
    /// <c>RESMON_Observability__Loki__*</c> are read-only and never saved, and Save touches only
    /// fields the user changed (<see cref="UserLokiSettingsPatch.ApplyDialogEdit"/>). The token box
    /// is masked and never shows a stored token: an empty box keeps the saved token, typing replaces
    /// it (stored DPAPI-encrypted), and "清除 (Clear)" removes it. "测试连接 (Test)" pushes one line
    /// with the values currently in the dialog and shows the HTTP result.
    /// </remarks>
    public sealed class ObservabilityForm : Form
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(12);

        private readonly IObservabilitySettingsStore _store;
        private readonly ISecretProtector _protector;
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
            IReadOnlySet<string> environmentOverriddenFields)
        {
            _store = store;
            _protector = protector;
            _effective = effective;
            _lowerLayers = lowerLayers;
            _environmentFields = environmentOverriddenFields;
            _tokenFromEnvironment = environmentOverriddenFields.Contains(LokiSettings.TokenKey);
            _hasSavedToken = !string.IsNullOrEmpty(saved.TokenProtected);
            EnvironmentOverriddenFields = UserLokiSettingsPatch.DialogFieldNames.Append(LokiSettings.TokenKey)
                .Where(environmentOverriddenFields.Contains)
                .ToList();

            Text = "可观测性 (Observability) - Resource Monitor";
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

            _enabled = new CheckBox { Text = "发送到 Grafana Loki (send to Grafana Loki)", Checked = effective.Enabled, AutoSize = true };
            _url = new TextBox { Text = effective.Url, Width = 340, PlaceholderText = "https://logs-prod-xxx.grafana.net  /  http://loki:3100" };
            _authMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
            _authMode.Items.AddRange(Enum.GetNames<LokiAuthMode>());
            _authMode.SelectedItem = effective.AuthMode.ToString();
            _username = new TextBox { Text = effective.Username, Width = 200 };
            _token = new TextBox
            {
                Width = 240,
                UseSystemPasswordChar = true,
                PlaceholderText = _tokenFromEnvironment ? "(env)" : _hasSavedToken ? "已保存 (saved)" : "未设置 (not set)",
            };
            _clearToken = new Button { Text = "清除 (Clear)", AutoSize = true, Enabled = _hasSavedToken && !_tokenFromEnvironment };
            _clearToken.Click += OnClearToken;
            _tenant = new TextBox { Text = effective.TenantId, Width = 200, PlaceholderText = "(optional X-Scope-OrgID)" };
            _hostLabel = new CheckBox { Text = "添加 host 标签 (add host=<PC name> label)", Checked = effective.IncludeHostLabel, AutoSize = true };
            _metricsInterval = new NumericUpDown
            {
                Minimum = LokiSettingsValidator.MinMetricsIntervalSeconds,
                Maximum = LokiSettingsValidator.MaxMetricsIntervalSeconds,
                Value = Math.Clamp(effective.MetricsIntervalSeconds, LokiSettingsValidator.MinMetricsIntervalSeconds, LokiSettingsValidator.MaxMetricsIntervalSeconds),
                Width = 100,
            };

            AddField(layout, "启用 (Enabled)", nameof(LokiSettings.Enabled), _enabled);
            AddField(layout, "地址 (Url)", nameof(LokiSettings.Url), _url);
            AddField(layout, "认证 (Auth mode)", nameof(LokiSettings.AuthMode), _authMode);
            AddField(layout, "用户名 (Username)", nameof(LokiSettings.Username), _username);
            var tokenRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            tokenRow.Controls.Add(_token);
            tokenRow.Controls.Add(_clearToken);
            if (_tokenFromEnvironment)
            {
                _token.ReadOnly = true;
            }

            AddRow(layout, _tokenFromEnvironment ? "令牌 (Token) (env):" : "令牌 (Token):", tokenRow);
            AddField(layout, "租户 (Tenant)", nameof(LokiSettings.TenantId), _tenant);
            AddField(layout, "主机标签 (Host label)", nameof(LokiSettings.IncludeHostLabel), _hostLabel);
            AddField(layout, "指标间隔 s (Metrics, 0 = off)", nameof(LokiSettings.MetricsIntervalSeconds), _metricsInterval);

            _test = new Button { Text = "测试连接 (Test)", AutoSize = true };
            _test.Click += OnTest;
            _testResult = new Label { AutoSize = true, MaximumSize = new Size(360, 0), Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) };
            var testRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            testRow.Controls.Add(_test);
            testRow.Controls.Add(_testResult);
            AddRow(layout, string.Empty, testRow);

            AddNote(
                layout,
                "令牌只以 DPAPI 加密形式保存（仅当前 Windows 用户可解密），不会写入日志；留空保留已保存的令牌。" + Environment.NewLine +
                "(The token is stored DPAPI-encrypted for this Windows user only and never logged; leave it empty to keep the saved token.)",
                SystemColors.GrayText);
            AddNote(layout, $"保存到 (Saved to): {store.FilePath}", SystemColors.GrayText);
            if (EnvironmentOverriddenFields.Count > 0)
            {
                AddNote(
                    layout,
                    "标记 (env) 的字段由环境变量决定，只读且不会保存 (fields marked (env) come from RESMON_ environment variables; read-only, not saved): "
                        + string.Join(", ", EnvironmentOverriddenFields),
                    Color.DarkOrange);
            }

            var save = new Button { Text = "保存 (Save)", AutoSize = true };
            var cancel = new Button { Text = "取消 (Cancel)", AutoSize = true, DialogResult = DialogResult.Cancel };
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

            AddRow(layout, fromEnvironment ? $"{label} (env):" : $"{label}:", control);
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
            _token.PlaceholderText = "将清除 (will be cleared on Save)";
            _clearToken.Enabled = false;
        }

        private async void OnTest(object? sender, EventArgs e)
        {
            var candidate = ReadControls() with { Token = CandidateToken() };
            if (LokiSettingsValidator.ValidateToken(candidate.Token) is { } tokenError)
            {
                ShowTestResult(false, tokenError);
                return;
            }

            _test.Enabled = false;
            ShowTestResult(null, "测试中… (testing…)");
            _testCancellation?.Dispose();
            _testCancellation = new CancellationTokenSource(TestTimeout);
            try
            {
                var result = await LokiPushClient.TestAsync(candidate, _testCancellation.Token);
                ShowTestResult(result.Outcome == LokiPushOutcome.Success, result.ToString());
            }
            catch (OperationCanceledException)
            {
                ShowTestResult(false, "已取消或超时 (cancelled or timed out)");
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
                true => "成功 (OK): " + text,
                false => "失败 (Failed): " + text,
                null => text,
            };
        }

        private void OnSave(object? sender, EventArgs e)
        {
            var edited = ReadControls();
            var typedToken = _tokenFromEnvironment ? string.Empty : _token.Text.Trim();

            var errors = LokiSettingsValidator.Validate(edited with { Token = typedToken }).ToList();
            if (errors.Count > 0)
            {
                MessageBox.Show(this, string.Join(Environment.NewLine, errors), "设置无效 (Invalid settings)", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (edited.Enabled && edited.AuthMode != LokiAuthMode.None
                && Uri.TryCreate(edited.Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp
                && MessageBox.Show(
                    this,
                    "凭据将通过未加密的 HTTP 发送。继续保存？" + Environment.NewLine + "(Credentials would be sent over unencrypted HTTP. Save anyway?)",
                    "Observability",
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
                MessageBox.Show(this, $"无法保存设置 (Unable to save settings):{Environment.NewLine}{ex.Message}", "Observability", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _token.Text = string.Empty;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
