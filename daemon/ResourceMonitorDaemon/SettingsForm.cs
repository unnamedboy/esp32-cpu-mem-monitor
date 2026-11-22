using System.Windows.Forms;

namespace ResourceMonitorDaemon
{
    /// <summary>
    /// Small dialog for editing the user-facing connection settings.
    /// </summary>
    /// <remarks>
    /// Shows the effective values. Fields overridden by <c>RESMON_Device__*</c> environment
    /// variables are read-only and never saved. On Save, only fields the user changed are
    /// touched, and a changed value equal to what <c>appsettings.json</c> + defaults resolve to
    /// is removed from the user settings file instead of being copied into it
    /// (see <see cref="UserDeviceSettingsPatch.ApplyDialogEdit"/>); unchanged fields and fields
    /// the dialog does not show, such as FallbackAddress, are left untouched.
    /// </remarks>
    public sealed class SettingsForm : Form
    {
        private readonly ISettingsStore _store;
        private readonly DeviceSettings _effective;
        private readonly DeviceSettings _displayed;
        private readonly DeviceSettings _lowerLayers;
        private readonly IReadOnlySet<string> _environmentFields;
        private readonly TextBox _host;
        private readonly NumericUpDown _port;
        private readonly TextBox _cpuTopic;
        private readonly TextBox _memTopic;
        private readonly NumericUpDown _interval;

        public SettingsForm(DeviceSettings effective, DeviceSettings lowerLayers, ISettingsStore store, IReadOnlySet<string> environmentOverriddenFields)
        {
            _store = store;
            _effective = effective;
            _lowerLayers = lowerLayers;
            _environmentFields = environmentOverriddenFields;
            EnvironmentOverriddenFields = UserDeviceSettingsPatch.DialogFieldNames
                .Where(environmentOverriddenFields.Contains)
                .ToList();

            Text = "设置 (Settings) - Resource Monitor";
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

            _host = new TextBox { Text = effective.Host, Width = 260 };
            _port = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = Math.Clamp(effective.Port, 1, 65535), Width = 100 };
            _cpuTopic = new TextBox { Text = effective.CpuTopic, Width = 260 };
            _memTopic = new TextBox { Text = effective.MemTopic, Width = 260 };
            _interval = new NumericUpDown
            {
                Minimum = DeviceSettingsValidator.MinSampleIntervalMs,
                Maximum = DeviceSettingsValidator.MaxSampleIntervalMs,
                Increment = 250,
                Value = Math.Clamp(effective.SampleIntervalMs, DeviceSettingsValidator.MinSampleIntervalMs, DeviceSettingsValidator.MaxSampleIntervalMs),
                Width = 100,
            };

            AddField(layout, "主机 (Host)", nameof(DeviceSettings.Host), _host);
            AddField(layout, "端口 (Port)", nameof(DeviceSettings.Port), _port);
            AddField(layout, "CPU 主题 (CPU topic)", nameof(DeviceSettings.CpuTopic), _cpuTopic);
            AddField(layout, "内存主题 (Memory topic)", nameof(DeviceSettings.MemTopic), _memTopic);
            AddField(layout, "采样间隔 ms (Interval)", nameof(DeviceSettings.SampleIntervalMs), _interval);
            AddRow(layout, "备用地址 (Fallback):", new Label
            {
                Text = string.IsNullOrEmpty(effective.FallbackAddress) ? "(none)" : effective.FallbackAddress,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
            });

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

            // The dialog is modeless (Show), so DialogResult alone does not close it; Esc
            // triggers CancelButton, which ends up here as well.
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

            // Baseline for "did the user change this field?" — read through the same path as Save.
            _displayed = ReadControls();
        }

        /// <summary>Dialog fields whose effective value comes from an environment variable.</summary>
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
            var note = new Label { Text = text, AutoSize = true, MaximumSize = new Size(420, 0), ForeColor = color, Margin = new Padding(3, 8, 3, 0) };
            layout.Controls.Add(note, 0, row);
            layout.SetColumnSpan(note, 2);
        }

        private DeviceSettings ReadControls() => _effective with
        {
            Host = _host.Text.Trim(),
            Port = (int)_port.Value,
            CpuTopic = _cpuTopic.Text.Trim(),
            MemTopic = _memTopic.Text.Trim(),
            SampleIntervalMs = (int)_interval.Value,
        };

        private void OnSave(object? sender, EventArgs e)
        {
            var edited = ReadControls();

            var errors = DeviceSettingsValidator.Validate(edited);
            if (errors.Count > 0)
            {
                MessageBox.Show(this, string.Join(Environment.NewLine, errors), "设置无效 (Invalid settings)", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                _store.Update(patch => patch.ApplyDialogEdit(_lowerLayers, _displayed, edited, _environmentFields));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                MessageBox.Show(this, $"无法保存设置 (Unable to save settings):{Environment.NewLine}{ex.Message}", "Daemon", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
