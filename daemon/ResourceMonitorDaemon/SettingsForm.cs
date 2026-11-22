using System.Windows.Forms;

namespace ResourceMonitorDaemon
{
    /// <summary>
    /// Small dialog for editing the user-facing connection settings. Values not shown here
    /// (fallback address, backoff, timeouts) are carried over unchanged from the settings the
    /// dialog was opened with. Saving goes through <see cref="ISettingsStore"/>, which triggers
    /// a live reload in the running daemon.
    /// </summary>
    public sealed class SettingsForm : Form
    {
        private readonly ISettingsStore _store;
        private readonly DeviceSettings _original;
        private readonly TextBox _host;
        private readonly NumericUpDown _port;
        private readonly TextBox _cpuTopic;
        private readonly TextBox _memTopic;
        private readonly NumericUpDown _interval;

        public SettingsForm(DeviceSettings current, ISettingsStore store)
        {
            _store = store;
            _original = current;

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

            _host = new TextBox { Text = current.Host, Width = 260 };
            _port = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = Math.Clamp(current.Port, 1, 65535), Width = 100 };
            _cpuTopic = new TextBox { Text = current.CpuTopic, Width = 260 };
            _memTopic = new TextBox { Text = current.MemTopic, Width = 260 };
            _interval = new NumericUpDown
            {
                Minimum = DeviceSettingsValidator.MinSampleIntervalMs,
                Maximum = DeviceSettingsValidator.MaxSampleIntervalMs,
                Increment = 250,
                Value = Math.Clamp(current.SampleIntervalMs, DeviceSettingsValidator.MinSampleIntervalMs, DeviceSettingsValidator.MaxSampleIntervalMs),
                Width = 100,
            };

            AddRow(layout, "主机 (Host):", _host);
            AddRow(layout, "端口 (Port):", _port);
            AddRow(layout, "CPU 主题 (CPU topic):", _cpuTopic);
            AddRow(layout, "内存主题 (Memory topic):", _memTopic);
            AddRow(layout, "采样间隔 ms (Interval):", _interval);
            AddRow(layout, "备用地址 (Fallback):", new Label
            {
                Text = string.IsNullOrEmpty(current.FallbackAddress) ? "(none)" : current.FallbackAddress,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
            });

            AddNote(layout, $"保存到 (Saved to): {store.FilePath}", SystemColors.GrayText);
            var deviceOverrides = DaemonConfiguration.GetEnvironmentOverrideNames()
                .Where(name => name.StartsWith(DaemonConfiguration.EnvironmentPrefix + DeviceSettings.SectionName + "__", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (deviceOverrides.Count > 0)
            {
                AddNote(layout, "注意：环境变量优先于此处的设置 (environment variables override these values):\n" + string.Join(", ", deviceOverrides), Color.DarkOrange);
            }

            var save = new Button { Text = "保存 (Save)", AutoSize = true };
            var cancel = new Button { Text = "取消 (Cancel)", AutoSize = true, DialogResult = DialogResult.Cancel };
            save.Click += OnSave;
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

        private void OnSave(object? sender, EventArgs e)
        {
            var updated = _original with
            {
                Host = _host.Text.Trim(),
                Port = (int)_port.Value,
                CpuTopic = _cpuTopic.Text.Trim(),
                MemTopic = _memTopic.Text.Trim(),
                SampleIntervalMs = (int)_interval.Value,
            };

            var errors = DeviceSettingsValidator.Validate(updated);
            if (errors.Count > 0)
            {
                MessageBox.Show(this, string.Join(Environment.NewLine, errors), "设置无效 (Invalid settings)", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                _store.Save(updated);
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
