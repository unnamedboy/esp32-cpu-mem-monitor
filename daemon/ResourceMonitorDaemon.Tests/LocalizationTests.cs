using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ResourceMonitorDaemon.Localization;
using ResourceMonitorDaemon.Observability;

namespace ResourceMonitorDaemon.Tests
{
    /// <summary>
    /// Tests for the UI localization: resource completeness (English neutral + zh-Hans satellite),
    /// <see cref="Strings"/> members, placeholders, culture resolution, the <c>Ui:Language</c>
    /// setting, and a source scan that keeps hard-coded Chinese / bilingual texts out of the code.
    /// </summary>
    internal static class LocalizationTests
    {
        private static readonly CultureInfo Chinese = UiLanguage.ChineseSimplifiedCulture;
        private static readonly CultureInfo English = UiLanguage.EnglishCulture;

        private static readonly Regex Placeholder = new(@"\{(\d+)(?:[,:][^}]*)?\}", RegexOptions.CultureInvariant);
        private static readonly Regex Cjk = new(@"[　-〿㐀-䶿一-鿿豈-﫿＀-￯]", RegexOptions.CultureInvariant);

        // "中文 (English words)": a CJK character, then a parenthesis whose content starts with two or more English words.
        private static readonly Regex Bilingual = new(@"[一-鿿]\s*[(（][A-Za-z][^()（）]*\s[A-Za-z][^()（）]*[)）]", RegexOptions.CultureInvariant);

        public static readonly (string Name, Action Run)[] All =
        {
            ("Localization: English and zh-Hans resources have the same keys and no empty values", () =>
            {
                var english = ReadResourceSet(CultureInfo.InvariantCulture);
                var chinese = ReadResourceSet(Chinese);
                AssertTrue(english.Count > 100, $"only {english.Count} English keys");
                var missing = english.Keys.Except(chinese.Keys).ToList();
                var extra = chinese.Keys.Except(english.Keys).ToList();
                AssertTrue(missing.Count == 0, "missing in zh-Hans: " + string.Join(", ", missing));
                AssertTrue(extra.Count == 0, "only in zh-Hans: " + string.Join(", ", extra));
                var empty = english.Concat(chinese).Where(pair => string.IsNullOrWhiteSpace(pair.Value)).Select(pair => pair.Key).ToList();
                AssertTrue(empty.Count == 0, "empty values: " + string.Join(", ", empty));
            }),
            ("Localization: Strings has exactly one member per key (property = text, method = format with one parameter per placeholder)", () =>
            {
                var english = ReadResourceSet(CultureInfo.InvariantCulture);
                var members = typeof(Strings)
                    .GetMembers(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Where(member => member.Name.Contains('_') && member is PropertyInfo or MethodInfo { IsSpecialName: false })
                    .ToList();
                var names = members.Select(member => member.Name).ToList();
                AssertTrue(names.Count == names.Distinct().Count(), "duplicate (overloaded) members");
                var notInResources = names.Except(english.Keys).ToList();
                var notInStrings = english.Keys.Except(names).ToList();
                AssertTrue(notInResources.Count == 0, "Strings members without resource: " + string.Join(", ", notInResources));
                AssertTrue(notInStrings.Count == 0, "resource keys without Strings member: " + string.Join(", ", notInStrings));
                foreach (var member in members)
                {
                    var placeholders = PlaceholderIndexes(english[member.Name]).Count;
                    var parameters = member is MethodInfo method ? method.GetParameters().Length : 0;
                    AssertTrue(placeholders == parameters, $"{member.Name}: {placeholders} placeholder(s), {parameters} parameter(s)");
                    AssertTrue(member is MethodInfo == placeholders > 0, $"{member.Name}: format strings are methods, plain texts properties");
                }
            }),
            ("Localization: placeholders are identical per key across languages and every text formats", () =>
            {
                var english = ReadResourceSet(CultureInfo.InvariantCulture);
                var chinese = ReadResourceSet(Chinese);
                foreach (var (key, text) in english)
                {
                    var expected = PlaceholderIndexes(text);
                    var actual = PlaceholderIndexes(chinese[key]);
                    AssertTrue(expected.SetEquals(actual), $"{key}: en {{{string.Join(",", expected)}}} vs zh-Hans {{{string.Join(",", actual)}}}");
                    AssertTrue(expected.Count == 0 || expected.Max() == expected.Count - 1, $"{key}: placeholders must be {{0}}..{{n-1}} without gaps");
                    var args = Enumerable.Range(0, expected.Count).Select(i => (object)("arg" + i)).ToArray();
                    foreach (var culture in new[] { English, Chinese })
                    {
                        var formatted = Strings.Format(culture, key, args);
                        AssertTrue(args.All(arg => formatted.Contains((string)arg)), $"{key} ({culture.Name}) dropped an argument: {formatted}");
                    }
                }
            }),
            ("Localization: English texts are English, Chinese texts have no bilingual \"中文 (English)\" labels", () =>
            {
                foreach (var (key, text) in ReadResourceSet(CultureInfo.InvariantCulture))
                {
                    AssertTrue(key == nameof(Strings.Settings_LanguageChineseSimplified) || !Cjk.IsMatch(text), $"CJK in English text {key}: {text}");
                }

                foreach (var (key, text) in ReadResourceSet(Chinese))
                {
                    AssertTrue(!Bilingual.IsMatch(text), $"bilingual zh-Hans text {key}: {text}");
                }
            }),
            ("Localization: Ui:Language resolution (auto follows Windows, explicit wins, invalid -> auto with warning)", () =>
            {
                foreach (var name in new[] { "zh-CN", "zh-Hans", "zh-SG", "zh-Hans-CN", "zh" })
                {
                    AssertEqual("zh-Hans", UiLanguage.Resolve("auto", new CultureInfo(name), out _).Name);
                }

                foreach (var name in new[] { "en-US", "de-DE", "ja-JP", "fr-FR", "zh-TW", "zh-HK", "zh-Hant", "" })
                {
                    AssertEqual("en", UiLanguage.Resolve(UiLanguage.Auto, name.Length == 0 ? CultureInfo.InvariantCulture : new CultureInfo(name), out _).Name);
                }

                AssertEqual("en", UiLanguage.Resolve("en", new CultureInfo("zh-CN"), out var noWarning).Name);
                AssertTrue(noWarning is null, "valid value warns");
                AssertEqual("zh-Hans", UiLanguage.Resolve("zh-Hans", new CultureInfo("en-US"), out _).Name);
                AssertEqual("zh-Hans", UiLanguage.Resolve(" ZH-hans ", new CultureInfo("de-DE"), out _).Name);
                AssertEqual("en", UiLanguage.Resolve(null, new CultureInfo("en-US"), out noWarning).Name);
                AssertTrue(noWarning is null, "missing value warns");

                AssertEqual("zh-Hans", UiLanguage.Resolve("fr", new CultureInfo("zh-CN"), out var warning).Name);
                AssertTrue(warning is not null && warning.Contains("'fr'") && warning.Contains(UiLanguage.ConfigurationKey), "invalid value: " + warning);
                AssertEqual(UiLanguage.Auto, UiLanguage.Normalize("zh-CN", out warning));   // only auto | en | zh-Hans
                AssertTrue(warning is not null, "zh-CN is not a setting value");

                AssertTrue(UiLanguage.ValueToStore("auto", "auto") is null, "lower-layer value is not stored");
                AssertEqual("en", UiLanguage.ValueToStore("en", "auto"));
                AssertEqual("auto", UiLanguage.ValueToStore("auto", "zh-Hans"));
            }),
            ("Localization: the zh-Hans satellite assembly is built and loaded", () =>
            {
                var directory = Path.GetDirectoryName(typeof(Strings).Assembly.Location)!;
                var satellite = Path.Combine(directory, "zh-Hans", "ResourceMonitorDaemon.resources.dll");
                AssertTrue(File.Exists(satellite), "missing " + satellite);
                AssertEqual("退出", Strings.Get(nameof(Strings.Tray_Exit), Chinese));
                AssertEqual("退出", Strings.Get(nameof(Strings.Tray_Exit), new CultureInfo("zh-CN")));   // parent fallback zh-CN -> zh-Hans
                AssertEqual("Exit", Strings.Get(nameof(Strings.Tray_Exit), new CultureInfo("zh-TW")));  // no zh-Hant: neutral English
                AssertEqual("退出", InCulture(Chinese, () => Strings.Tray_Exit));
                AssertEqual("Exit", InCulture(English, () => Strings.Tray_Exit));
                AssertEqual("已保存设备地址 192.168.1.50", InCulture(Chinese, () => Strings.Pairing_StepSaved("192.168.1.50")));
                var loaded = AppDomain.CurrentDomain.GetAssemblies().Any(assembly =>
                    assembly.GetName().Name == "ResourceMonitorDaemon.resources" && assembly.GetName().CultureInfo?.Name == "zh-Hans");
                AssertTrue(loaded, "satellite assembly not loaded");
            }),
            ("Localization: messages that are logged and shown are English for logs and localized for the UI", () =>
            {
                InCulture(Chinese, () =>
                {
                    var problem = DeviceSettingsValidator.HostProblem("");
                    AssertEqual("Host must not be empty.", problem!.English);
                    AssertEqual("主机不能为空。", problem.Localized);
                    AssertEqual("Host must not be empty.", DeviceSettingsValidator.ValidateHost(""));   // string API (logs) stays English
                    AssertEqual("Port 0 is outside 1..65535.", DeviceSettingsValidator.ValidatePort(0));
                    AssertEqual(
                        "SampleIntervalMs 100 is outside 250..60000.",
                        DeviceSettingsValidator.Validate(new DeviceSettings { SampleIntervalMs = 100 }).Single());
                    AssertEqual(
                        "采样间隔（毫秒）的值 100 超出范围 250..60000。",
                        DeviceSettingsValidator.Problems(new DeviceSettings { SampleIntervalMs = 100 }).Single().Localized);

                    var sanitizeWarnings = new List<string>();
                    DeviceSettingsValidator.Sanitize(new DeviceSettings { Host = "" }, sanitizeWarnings);
                    AssertTrue(sanitizeWarnings.Single().StartsWith("Host must not be empty.", StringComparison.Ordinal), "log warning is English: " + sanitizeWarnings.Single());

                    // Observability (zh-Hans spot checks): validation, Test result text.
                    var loki = LokiSettingsValidator.Problems(new LokiSettings { Enabled = true, Url = "" }).Single();
                    AssertEqual("Url must not be empty.", loki.English);
                    AssertEqual("地址不能为空。", loki.Localized);
                    AssertEqual("Url must not be empty.", LokiSettingsValidator.Validate(new LokiSettings { Enabled = true, Url = "" }).Single());

                    var timedOut = UiMessage.Observability_RequestTimedOut(10);
                    var result = new LokiPushResult(LokiPushOutcome.Retry, null, timedOut.English) { LocalizedDetail = timedOut };
                    AssertEqual("timed out after 10 s", result.Detail);
                    AssertEqual("10 秒后超时", ObservabilityForm.DescribeTestResult(result));
                    AssertEqual("HTTP 204 No Content", ObservabilityForm.DescribeTestResult(new LokiPushResult(LokiPushOutcome.Success, 204, "No Content")));
                    var invalid = LokiPushClient.TestAsync(new LokiSettings { Url = "ftp://x" }, CancellationToken.None).GetAwaiter().GetResult();
                    AssertEqual("Url must be an absolute http:// or https:// URL.", invalid.Detail);
                    AssertEqual("地址必须是以 http:// 或 https:// 开头的完整 URL。", ObservabilityForm.DescribeTestResult(invalid));
                    AssertEqual("失败：x", Strings.Observability_TestFailed("x"));
                    return 0;
                });
            }),
            ("Localization: MetricPayloadFormatter stays invariant under zh-Hans and de-DE UI/format cultures", () =>
            {
                var thread = Thread.CurrentThread;
                var (culture, uiCulture) = (thread.CurrentCulture, thread.CurrentUICulture);
                try
                {
                    foreach (var name in new[] { "zh-Hans", "de-DE" })
                    {
                        thread.CurrentCulture = new CultureInfo(name);
                        thread.CurrentUICulture = new CultureInfo(name);
                        AssertEqual("42.3", InCulture(name == "de-DE" ? English : Chinese, () => MetricPayloadFormatter.FormatPercentPayload(42.26)));
                    }
                }
                finally
                {
                    thread.CurrentCulture = culture;
                    thread.CurrentUICulture = uiCulture;
                }

                InCulture(Chinese, () =>
                {
                    AssertEqual("zh-Hans", Strings.Culture.Name);
                    AssertEqual(culture.Name, CultureInfo.CurrentCulture.Name);        // UiCulture never changes the thread cultures
                    AssertEqual(uiCulture.Name, CultureInfo.CurrentUICulture.Name);
                    return 0;
                });
            }),
            ("Localization: Ui:Language is stored only when chosen, other sections are kept, env override is detected", () =>
            {
                using var temp = new TempDirectory();
                var path = Path.Combine(temp.Path, "settings.json");
                var store = new UserSettingsStore(path);
                store.Update(patch => patch.Host = "my-host");
                store.UpdateUiLanguage("zh-Hans");
                var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
                AssertEqual("zh-Hans", (string?)root["Ui"]?["Language"]);
                AssertEqual("my-host", (string?)root["Device"]?["Host"]);
                AssertEqual("zh-Hans", store.LoadUiLanguage());
                AssertEqual("my-host", store.Load().Host);   // device patch API unchanged

                store.Update(patch => patch.Port = 1999);     // a device save keeps the Ui section
                AssertEqual("zh-Hans", store.LoadUiLanguage());

                store.UpdateUiLanguage(null);
                root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
                AssertTrue(!root.ContainsKey("Ui"), "empty Ui section removed");
                AssertEqual(1999, store.Load().Port);
                ProvisioningTests.AssertThrows<ArgumentException>(() => store.UpdateUiLanguage("fr"));
                ProvisioningTests.AssertThrows<ArgumentException>(() => store.UpdateUiLanguage("ZH-HANS"));   // the file only holds canonical values

                File.WriteAllText(Path.Combine(temp.Path, "appsettings.json"), """{ "Ui": { "Language": "en" } }""");
                AssertEqual("en", DaemonConfiguration.LoadLowerUiLanguage(temp.Path));
                store.UpdateUiLanguage("zh-Hans");
                const string envName = "RESMON_Ui__Language";
                var previous = Environment.GetEnvironmentVariable(envName);
                try
                {
                    Environment.SetEnvironmentVariable(envName, null);
                    AssertTrue(!DaemonConfiguration.IsUiLanguageOverriddenByEnvironment(), "no env override");
                    AssertEqual("zh-Hans", Build(temp.Path, path)[UiLanguage.ConfigurationKey]);   // user file beats appsettings
                    Environment.SetEnvironmentVariable(envName, "en");
                    AssertTrue(DaemonConfiguration.IsUiLanguageOverriddenByEnvironment(), "env override detected");
                    AssertEqual("en", Build(temp.Path, path)[UiLanguage.ConfigurationKey]);         // env beats the user file
                }
                finally
                {
                    Environment.SetEnvironmentVariable(envName, previous);
                }
            }),
            ("Localization: UiLanguageMonitor applies the setting, follows reloads and warns once per invalid value", () =>
            {
                var root = (IConfigurationRoot)new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?> { [UiLanguage.ConfigurationKey] = "zh-Hans" })
                    .Build();
                var logger = new ListLogger();
                var changes = 0;
                EventHandler onChanged = (_, _) => Interlocked.Increment(ref changes);
                var previous = Strings.Culture;
                UiCulture.Changed += onChanged;
                try
                {
                    UiCulture.Apply(English);
                    changes = 0;
                    using var monitor = new UiLanguageMonitor(root, logger);
                    monitor.Start();
                    AssertEqual("zh-Hans", Strings.Culture.Name);
                    AssertEqual(1, changes);
                    AssertTrue(logger.Messages.Any(m => m.StartsWith("UI language: zh-Hans", StringComparison.Ordinal)), "startup diagnostic");

                    root[UiLanguage.ConfigurationKey] = "en";
                    root.Reload();
                    AssertEqual("en", Strings.Culture.Name);
                    AssertEqual(2, changes);

                    root[UiLanguage.ConfigurationKey] = "klingon";
                    root.Reload();
                    root.Reload();
                    AssertEqual(1, logger.Messages.Count(m => m.Contains("'klingon'")));
                    AssertEqual(UiLanguage.ForSystem(UiCulture.SystemUiCulture).Name, Strings.Culture.Name);   // invalid -> auto

                    monitor.ApplySaved("zh-Hans");   // settings dialog: immediate, before the file watcher reload
                    AssertEqual("zh-Hans", Strings.Culture.Name);
                    AssertTrue(logger.Messages.All(m => !Cjk.IsMatch(m)), "log messages are English");
                }
                finally
                {
                    UiCulture.Changed -= onChanged;
                    UiCulture.Apply(previous);
                }
            }),
            ("Localization: UiMessage is built only by typed factories whose parameters match the placeholders", () =>
            {
                var english = ReadResourceSet(CultureInfo.InvariantCulture);
                AssertTrue(typeof(UiMessage).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).All(c => c.IsPrivate),
                    "UiMessage constructors must stay private (use the factories)");
                var factories = typeof(UiMessage).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Where(method => method.ReturnType == typeof(UiMessage))
                    .ToList();
                AssertTrue(factories.Count >= 20, $"only {factories.Count} factories");
                foreach (var factory in factories)
                {
                    AssertTrue(english.ContainsKey(factory.Name), $"factory {factory.Name} has no resource");
                    var parameters = factory.GetParameters();
                    AssertEqual(PlaceholderIndexes(english[factory.Name]).Count, parameters.Length);
                    var args = parameters.Select(parameter => parameter.ParameterType == typeof(UiField) ? new UiField("Field" + parameter.Position, null)
                        : parameter.ParameterType == typeof(int) ? (object)(parameter.Position + 1)
                        : "arg" + parameter.Position).ToArray();
                    var message = (UiMessage)factory.Invoke(null, args)!;
                    AssertEqual(factory.Name, message.Key);
                    foreach (var culture in new[] { CultureInfo.InvariantCulture, English, Chinese })
                    {
                        var text = message.Format(culture);
                        AssertTrue(args.All(arg => text.Contains(arg.ToString()!)), $"{factory.Name} ({culture.Name}) dropped an argument: {text}");
                    }
                }
            }),
            ("Localization: validation names fields by their dialog label in the UI and by their configuration key in the log", () =>
            {
                var bad = new DeviceSettings { CpuTopic = "same", MemTopic = "same", StatusTopic = "same", FallbackAddress = "nope", Port = 0, Host = "" };
                var problems = DeviceSettingsValidator.Problems(bad);
                var equal = problems.Single(p => p.Key == nameof(Strings.Settings_ErrorTopicsEqual));
                AssertEqual("CpuTopic and MemTopic must be different.", equal.English);
                var fallback = problems.Single(p => p.Key == nameof(Strings.Settings_ErrorFallbackInvalid));
                AssertEqual("FallbackAddress 'nope' is not a dotted IPv4 address.", fallback.English);
                AssertEqual("StatusTopic must differ from CpuTopic and MemTopic.", DeviceSettingsFields.StatusTopicCollides.English);
                AssertEqual("ReconnectMaxBackoffMs must not be smaller than ReconnectInitialBackoffMs.", DeviceSettingsFields.BackoffOrder.English);
                AssertEqual("CpuTopic must not be empty.", DeviceSettingsValidator.ValidateTopic("", "CpuTopic"));
                AssertEqual("t must not be empty.", DeviceSettingsValidator.ValidateTopic("", "t"));   // unknown name: shown as is

                InCulture(Chinese, () =>
                {
                    AssertEqual("CPU 主题和内存主题不能相同。", equal.Localized);
                    AssertEqual("备用地址“nope”不是点分十进制的 IPv4 地址。", fallback.Localized);
                    AssertEqual("状态主题必须与 CPU 主题、内存主题不同。", DeviceSettingsFields.StatusTopicCollides.Localized);
                    AssertEqual("CPU 主题不能为空。", DeviceSettingsValidator.TopicProblem("", DeviceSettingsFields.CpuTopic)!.Localized);
                    AssertEqual("端口的值 0 超出范围 1..65535。", DeviceSettingsValidator.PortProblem(0)!.Localized);
                    var identifiers = new[] { "Host", "FallbackAddress", "Port", "CpuTopic", "MemTopic", "StatusTopic", "SampleIntervalMs", "Reconnect", "ConnectTimeoutMs" };
                    var all = DeviceSettingsValidator.Problems(bad with { SampleIntervalMs = 1, ReconnectInitialBackoffMs = 1, ReconnectMaxBackoffMs = 1, ConnectTimeoutMs = 1 })
                        .Concat(LokiSettingsValidator.Problems(new LokiSettings { MetricsIntervalSeconds = -1, BatchMaxEntries = 0, QueueCapacity = 0, FlushIntervalMs = 0 }))
                        .Select(p => p.Localized).ToList();
                    var leaking = all.Where(text => identifiers.Any(id => text.Contains(id, StringComparison.Ordinal))).ToList();
                    AssertTrue(leaking.Count == 0, "configuration keys in zh dialog messages: " + string.Join(" | ", leaking));
                    return 0;
                });
            }),
            ("Localization: MQTT balloon texts format in one culture; the endpoint is logged in English", () =>
            {
                var endpoint = UiMessage.Tray_MqttEndpointWithFallback("resmon.local", 1883, "192.168.1.50");
                InCulture(Chinese, () =>
                {
                    AssertEqual("resmon.local:1883, fallback 192.168.1.50", endpoint.English);   // the "endpoint changed" log line
                    var text = UiMessage.Tray_MqttFailedText(endpoint).Format(Chinese);
                    AssertTrue(text.Contains("resmon.local:1883，备用地址 192.168.1.50") && !text.Contains("fallback"), text);
                    AssertTrue(UiMessage.Tray_MqttFailedText(endpoint).Format(English).Contains("(resmon.local:1883, fallback 192.168.1.50)"), "en");
                    AssertEqual("resmon.local:1883", UiMessage.Tray_MqttEndpoint("resmon.local", 1883).English);
                    return 0;
                });
            }),
            ("Localization: save failures show a localized generic text; the language is saved even if the Device section is invalid", () =>
            {
                const string path = @"C:\x\settings.json";
                AssertEqual(Strings.Common_SaveFailedInvalidFile(path), SettingsSaveFailure.Describe(new ArgumentException("Invalid settings: Port 0 is outside 1..65535."), path));
                AssertEqual(Strings.Common_SaveFailed(path), SettingsSaveFailure.Describe(new IOException("disk full"), path));
                AssertTrue(InCulture(Chinese, () => SettingsSaveFailure.Describe(new ArgumentException("x"), path)).StartsWith("无法保存", StringComparison.Ordinal), "zh");

                using var temp = new TempDirectory();
                var file = Path.Combine(temp.Path, "settings.json");
                File.WriteAllText(file, """{ "Device": { "Port": 0 } }""");   // hand-edited invalid value
                var store = new UserSettingsStore(file);
                Exception? failure = null;
                var thread = new Thread(() =>
                {
                    try
                    {
                        // Only the language changes in the dialog: the device patch is not written, so the
                        // invalid Port does not block saving the language.
                        using var form = new SettingsForm(
                            new DeviceSettings(), new DeviceSettings(), store, new HashSet<string>(),
                            new UiLanguageSetting(UiLanguage.Auto, UiLanguage.Auto, FromEnvironment: false), store,
                            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
                        var box = (System.Windows.Forms.ComboBox)typeof(SettingsForm).GetField("_languageBox", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                        box.SelectedIndex = 2;   // zh-Hans
                        typeof(SettingsForm).GetMethod("OnSave", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, new object?[] { null, EventArgs.Empty });
                        AssertEqual("zh-Hans", form.LanguageSetting);
                        AssertEqual(System.Windows.Forms.DialogResult.OK, form.DialogResult);
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                thread.Join();
                if (failure != null)
                {
                    throw new InvalidOperationException("settings dialog save failed: " + failure.GetBaseException().Message, failure);
                }

                AssertEqual("zh-Hans", store.LoadUiLanguage());
                AssertEqual(0, store.Load().Port);   // the Device section was left alone
                ProvisioningTests.AssertThrows<ArgumentException>(() => store.Update(patch => patch.Host = "other"));   // what the dialog avoids
            }),
            ("Localization: no CJK text or bilingual labels in daemon sources outside Resources", () =>
            {
                var sourceRoot = FindDaemonSourceDirectory();
                var offenders = new List<string>();
                foreach (var file in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(sourceRoot, file);
                    if (relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        || relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        || relative.StartsWith("Resources" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var lines = File.ReadAllLines(file);
                    for (var i = 0; i < lines.Length; i++)
                    {
                        // Allow-list: non-UI data such as localized netsh output labels, marked on the line.
                        if (lines[i].Contains("i18n-allow", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        if (Cjk.IsMatch(lines[i]) || Bilingual.IsMatch(lines[i]))
                        {
                            offenders.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
                        }
                    }
                }

                AssertTrue(offenders.Count == 0, "hard-coded CJK/bilingual text (move it to Resources/Strings*.resx):\n" + string.Join("\n", offenders));
            }),
        };

        /// <summary>Runs <paramref name="action"/> with <paramref name="culture"/> as the UI language, then restores it.</summary>
        public static T InCulture<T>(CultureInfo culture, Func<T> action)
        {
            var previous = Strings.Culture;
            UiCulture.Apply(culture);
            try
            {
                return action();
            }
            finally
            {
                UiCulture.Apply(previous);
            }
        }

        private static Dictionary<string, string> ReadResourceSet(CultureInfo culture)
        {
            var set = Strings.ResourceManager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
                ?? throw new InvalidOperationException($"no resources for '{culture.Name}'");
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in set)
            {
                result[(string)entry.Key] = entry.Value as string ?? throw new InvalidOperationException($"{entry.Key} is not a string");
            }

            return result;
        }

        private static HashSet<int> PlaceholderIndexes(string text) =>
            Placeholder.Matches(text).Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).ToHashSet();

        private static IConfiguration Build(string appSettingsDirectory, string userSettingsPath) =>
            new ConfigurationBuilder().AddDaemonSources(appSettingsDirectory, userSettingsPath, reloadOnChange: false).Build();

        private static string FindDaemonSourceDirectory()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "ResourceMonitorDaemon", "ResourceMonitorDaemon.csproj");
                if (File.Exists(candidate))
                {
                    return Path.GetDirectoryName(candidate)!;
                }
            }

            throw new InvalidOperationException("daemon/ResourceMonitorDaemon not found above " + AppContext.BaseDirectory);
        }

        private static void AssertTrue(bool condition, string message) => ProvisioningTests.AssertTrue(condition, message);

        private static void AssertEqual<T>(T expected, T actual) => ProvisioningTests.AssertEqual(expected, actual);

        private sealed class TempDirectory : IDisposable
        {
            public TempDirectory()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "resmon-i18n-tests-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }

            public string Path { get; }

            public void Dispose()
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }

        private sealed class ListLogger : ILogger<UiLanguageMonitor>
        {
            private readonly System.Collections.Concurrent.ConcurrentQueue<string> _messages = new();

            public IEnumerable<string> Messages => _messages.ToArray();

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                _messages.Enqueue(formatter(state, exception));
        }
    }
}
