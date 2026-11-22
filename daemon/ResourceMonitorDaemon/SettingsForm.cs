using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using ResourceMonitorDaemon.Localization;

namespace ResourceMonitorDaemon
{
    /// <summary>
    /// What the settings dialog needs for <c>Ui:Language</c>: the effective value (normalized), the
    /// value of the lower layers (defaults + <c>appsettings.json</c>) and whether
    /// <c>RESMON_Ui__Language</c> overrides it.
    /// </summary>
    public sealed record UiLanguageSetting(string Effective, string LowerLayers, bool FromEnvironment);

    /// <summary>
    /// The user-facing text for a failed settings save. The exception text (English, or Windows'
    /// own language for I/O errors) only goes to the log.
    /// </summary>
    public static class SettingsSaveFailure
    {
        /// <param name="exception">What <see cref="ISettingsStore.Update"/> (or a sibling store method) threw.</param>
        /// <param name="filePath">The user settings file.</param>
        /// <param name="text">The dialog's texts (its language); null for the current UI language.</param>
        public static string Describe(Exception exception, string filePath, StringSet? text = null)
        {
            text ??= Strings.Current;
            return exception is ArgumentException
                ? text.Common_SaveFailedInvalidFile(filePath)   // validation of the whole patch, e.g. a bad hand-edited value
                : text.Common_SaveFailed(filePath);
        }
    }

    /// <summary>
    /// Small dialog for editing the user-facing connection settings and the UI language.
    /// </summary>
    /// <remarks>
    /// Shows the effective values. Fields overridden by <c>RESMON_Device__*</c> (or
    /// <c>RESMON_Ui__Language</c>) environment variables are read-only and never saved. On Save,
    /// only fields the user changed are touched, and a changed value equal to what
    /// <c>appsettings.json</c> + defaults resolve to is removed from the user settings file instead
    /// of being copied into it (see <see cref="UserDeviceSettingsPatch.ApplyDialogEdit"/> and
    /// <see cref="UiLanguage.ValueToStore"/>); unchanged fields and fields the dialog does not show,
    /// such as FallbackAddress, are left untouched. Texts come from <see cref="Strings"/>; the dialog
    /// keeps the language it was opened in.
    /// </remarks>
    public sealed class SettingsForm : Form
    {
        private readonly StringSet _text = Strings.Current;   // the dialog's language, fixed when it opens
        private readonly ISettingsStore _store;
        private readonly IUiSettingsStore _uiStore;
        private readonly DeviceSettings _effective;
        private readonly DeviceSettings _displayed;
        private readonly DeviceSettings _lowerLayers;
        private readonly IReadOnlySet<string> _environmentFields;
        private readonly UiLanguageSetting _language;
        private readonly ILogger _logger;
        private string _savedLanguage;
        private readonly TextBox _host;
        private readonly NumericUpDown _port;
        private readonly TextBox _cpuTopic;
        private readonly TextBox _memTopic;
        private readonly NumericUpDown _interval;
        private readonly ComboBox _languageBox;

        public SettingsForm(
            DeviceSettings effective,
            DeviceSettings lowerLayers,
            ISettingsStore store,
            IReadOnlySet<string> environmentOverriddenFields,
            UiLanguageSetting language,
            IUiSettingsStore uiStore,
            ILogger logger)
        {
            _store = store;
            _uiStore = uiStore;
            _effective = effective;
            _lowerLayers = lowerLayers;
            _environmentFields = environmentOverriddenFields;
            _language = language;
            _logger = logger;
            _savedLanguage = language.Effective;
            EnvironmentOverriddenFields = UserDeviceSettingsPatch.DialogFieldNames
                .Where(environmentOverriddenFields.Contains)
                .ToList();

            Text = _text.Settings_WindowTitle;
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

            // Each language is listed in its own language; "automatic" in the current UI language.
            _languageBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
            _languageBox.Items.AddRange(new object[]
            {
                new LanguageOption(UiLanguage.Auto, _text.Settings_LanguageAutomatic),
                new LanguageOption(UiLanguage.English, _text.Settings_LanguageEnglish),
                new LanguageOption(UiLanguage.ChineseSimplified, _text.Settings_LanguageChineseSimplified),
            });
            _languageBox.SelectedItem = _languageBox.Items.Cast<LanguageOption>().First(option => option.Value == language.Effective);
            _languageBox.Enabled = !language.FromEnvironment;

            AddField(layout, _text.Settings_Host, nameof(DeviceSettings.Host), _host);
            AddField(layout, _text.Settings_Port, nameof(DeviceSettings.Port), _port);
            AddField(layout, _text.Settings_CpuTopic, nameof(DeviceSettings.CpuTopic), _cpuTopic);
            AddField(layout, _text.Settings_MemTopic, nameof(DeviceSettings.MemTopic), _memTopic);
            AddField(layout, _text.Settings_SampleInterval, nameof(DeviceSettings.SampleIntervalMs), _interval);
            AddRow(layout, _text.Common_FieldLabel(_text.Settings_FallbackAddress), new Label
            {
                Text = string.IsNullOrEmpty(effective.FallbackAddress) ? _text.Common_None : effective.FallbackAddress,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(3, 6, 3, 3),
            });
            AddRow(
                layout,
                language.FromEnvironment ? _text.Common_EnvFieldLabel(_text.Settings_Language) : _text.Common_FieldLabel(_text.Settings_Language),
                _languageBox);
            AddNote(layout, _text.Settings_LanguageNote, SystemColors.GrayText);

            AddNote(layout, _text.Common_SavedTo(store.FilePath), SystemColors.GrayText);
            var environmentFields = language.FromEnvironment
                ? EnvironmentOverriddenFields.Append(UiLanguage.ConfigurationKey).ToList()
                : EnvironmentOverriddenFields;
            if (environmentFields.Count > 0)
            {
                AddNote(layout, _text.Common_EnvironmentFieldsNote(string.Join(", ", environmentFields)), Color.DarkOrange);
            }

            var save = new Button { Text = _text.Common_Save, AutoSize = true };
            var cancel = new Button { Text = _text.Common_Cancel, AutoSize = true, DialogResult = DialogResult.Cancel };
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

        /// <summary>
        /// The <c>Ui:Language</c> value saved because the user changed the drop-down, so the tray can
        /// switch at once instead of waiting for the configuration file watcher; null when the language
        /// was not changed (or is set by <c>RESMON_Ui__Language</c>). A value shown when the dialog
        /// opened may be stale, so it is never re-applied.
        /// </summary>
        public string? ChangedLanguageSetting { get; private set; }

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

        private string SelectedLanguage => (_languageBox.SelectedItem as LanguageOption)?.Value ?? _language.Effective;

        private void OnSave(object? sender, EventArgs e)
        {
            var edited = ReadControls();

            var errors = DeviceSettingsValidator.Problems(edited);
            if (errors.Count > 0)
            {
                MessageBox.Show(
                    this,
                    string.Join(Environment.NewLine, errors.Select(error => error.Format(_text.Culture))),
                    _text.Common_InvalidSettingsCaption,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            // The language is saved on its own and first: a problem with the Device section (for
            // example an invalid hand-edited value in the user file) must not lose the choice.
            var selectedLanguage = SelectedLanguage;
            try
            {
                if (!_language.FromEnvironment && selectedLanguage != _savedLanguage)
                {
                    _uiStore.UpdateUiLanguage(UiLanguage.ValueToStore(selectedLanguage, _language.LowerLayers));
                    _savedLanguage = selectedLanguage;
                }

                // Nothing to write when no device field changed (ApplyDialogEdit would be a no-op).
                if (edited != _displayed)
                {
                    _store.Update(patch => patch.ApplyDialogEdit(_lowerLayers, _displayed, edited, _environmentFields));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _logger.LogWarning("Settings dialog: could not save to {Path}: {Error}", _store.FilePath, ex.Message);
                MessageBox.Show(this, SettingsSaveFailure.Describe(ex, _store.FilePath, _text), _text.Common_ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            ChangedLanguageSetting = !_language.FromEnvironment && selectedLanguage != _language.Effective ? selectedLanguage : null;
            DialogResult = DialogResult.OK;
            Close();
        }

        private sealed record LanguageOption(string Value, string Display)
        {
            public override string ToString() => Display;
        }
    }
}
