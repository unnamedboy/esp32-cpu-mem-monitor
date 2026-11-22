using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ResourceMonitorDaemon.Pairing;
using ResourceMonitorDaemon.Provisioning;

namespace ResourceMonitorDaemon.Tests
{
    /// <summary>
    /// Tests for the pairing wizard's pure decisions (<see cref="PairingOutcomePresenter"/>), the
    /// first-run prompt marker, the netsh SSID parser and the broker reachability probe. The WinForms
    /// wizard itself is not tested here.
    /// </summary>
    internal static class PairingTests
    {
        private const string Host = "resmon.local";
        private const string Secret = "S3cret-Passw0rd!";

        public static readonly (string Name, Action Run)[] All =
        {
            ("Pairing: Connected with a valid IPv4 succeeds and saves it as FallbackAddress", () =>
            {
                var outcome = PairingOutcomePresenter.Present(new WifiProvisioningResult.Connected("192.168.1.50"), Host);
                AssertEqual(PairingOutcomeKind.Succeeded, outcome.Kind);
                AssertEqual("192.168.1.50", outcome.AddressToSave);
                AssertTrue(outcome.Message.Contains("192.168.1.50"), "message names the address");
            }),
            ("Pairing: Connected without a usable IPv4 succeeds but saves nothing", () =>
            {
                foreach (var ip in new string?[] { null, "", "  ", "0.0.0.0", "255.255.255.255", "127.0.0.1", "224.0.0.251",
                             "169.254.10.20", "1.2.3", "fe80::1", "not-an-ip", "300.1.1.1" })
                {
                    var outcome = PairingOutcomePresenter.Present(new WifiProvisioningResult.Connected(ip), Host);
                    AssertEqual(PairingOutcomeKind.Succeeded, outcome.Kind);
                    AssertTrue(outcome.AddressToSave is null, $"no address for '{ip}'");
                    AssertTrue(outcome.Message.Contains(Host), "message names the host used instead");
                }

                AssertEqual("10.0.0.7", PairingOutcomePresenter.GetAddressToSave(" 10.0.0.7 "));
            }),
            ("Pairing: Wi-Fi failures map to password / 2.4 GHz messages and focus the right field", () =>
            {
                var auth = PairingOutcomePresenter.Present(new WifiProvisioningResult.Failed(WifiConnectFailedReason.AuthError), Host);
                AssertEqual(PairingOutcomeKind.Failed, auth.Kind);
                AssertTrue(auth.Message.Contains("Wi-Fi 密码错误"), auth.Message);
                AssertEqual(PairingField.Password, auth.Focus);
                AssertTrue(auth.AddressToSave is null, "nothing saved on failure");

                var notFound = PairingOutcomePresenter.Present(new WifiProvisioningResult.Failed(WifiConnectFailedReason.NetworkNotFound), Host);
                AssertTrue(notFound.Message.Contains("找不到该 Wi-Fi（ESP32 仅支持 2.4 GHz）"), notFound.Message);
                AssertEqual(PairingField.Ssid, notFound.Focus);

                var unknown = PairingOutcomePresenter.Present(new WifiProvisioningResult.Failed((WifiConnectFailedReason)7), Host);
                AssertEqual(PairingOutcomeKind.Failed, unknown.Kind);

                var timeout = PairingOutcomePresenter.Present(new WifiProvisioningResult.Timeout(WifiStationState.Connecting), Host);
                AssertEqual(PairingOutcomeKind.Failed, timeout.Kind);
                AssertTrue(timeout.Message.Contains("30"), "timeout names the 30 s limit");
            }),
            ("Pairing: ConnectionLost asks for a broker check, then succeeds or fails without saving", () =>
            {
                var lost = PairingOutcomePresenter.Present(new WifiProvisioningResult.ConnectionLost(WifiStationState.Connecting, "lost"), Host);
                AssertEqual(PairingOutcomeKind.CheckBroker, lost.Kind);
                AssertTrue(lost.AddressToSave is null, "no address");

                var reachable = PairingOutcomePresenter.PresentBrokerCheck(true, Host, 1883);
                AssertEqual(PairingOutcomeKind.Succeeded, reachable.Kind);
                AssertTrue(reachable.AddressToSave is null, "nothing reported, nothing saved");
                AssertTrue(reachable.Message.Contains("resmon.local:1883"), reachable.Message);

                var unreachable = PairingOutcomePresenter.PresentBrokerCheck(false, Host, 1883);
                AssertEqual(PairingOutcomeKind.Failed, unreachable.Kind);
                AssertTrue(unreachable.SuggestRescan, "suggest rescan");
                AssertTrue(unreachable.Message.Contains(ProvisioningConstants.ReprovisionInstruction), "reprovision hint (en)");
                AssertTrue(unreachable.Message.Contains(PairingOutcomePresenter.ReprovisionInstructionZh), "reprovision hint (zh)");
            }),
            ("Pairing: provisioning exceptions map to clear messages", () =>
            {
                var code = PairingOutcomePresenter.PresentException(new PairingCodeRejectedException(PairingCodeRejectionReason.DeviceClosedConnection));
                AssertTrue(code.Message.Contains("配对码错误"), code.Message);
                AssertEqual(PairingField.PairingCode, code.Focus);

                var retryException = new ProvisioningRetryRejectedException(previousAttemptFailed: true);
                var retry = PairingOutcomePresenter.PresentException(retryException);
                AssertTrue(retry.Message.Contains(retryException.Message), "keeps the library message");
                AssertTrue(retry.Message.Contains(ProvisioningConstants.ReprovisionInstruction), "BOOT instruction");
                AssertTrue(retry.SuggestRescan, "rescan after restart");

                var off = PairingOutcomePresenter.PresentException(
                    new ProvisioningTransportException(ProvisioningTransportError.BluetoothOff, "Bluetooth is turned off."));
                AssertTrue(off.Message.Contains("蓝牙已关闭") && off.Message.Contains("Bluetooth is turned off."), off.Message);
                AssertTrue(!off.SuggestRescan, "rescan does not help while Bluetooth is off");

                var gone = PairingOutcomePresenter.PresentException(
                    new ProvisioningTransportException(ProvisioningTransportError.DeviceNotFound, "not found"));
                AssertTrue(gone.SuggestRescan, "rescan");
                AssertEqual(PairingField.Device, gone.Focus);

                var protocol = PairingOutcomePresenter.PresentException(new ProvisioningProtocolException("odd answer"));
                AssertEqual(PairingOutcomeKind.Failed, protocol.Kind);
                AssertTrue(protocol.Message.Contains("odd answer"), protocol.Message);

                AssertEqual(PairingOutcomeKind.Cancelled, PairingOutcomePresenter.PresentException(new OperationCanceledException()).Kind);
                AssertEqual(PairingOutcomeKind.Cancelled, PairingOutcomePresenter.PresentException(new TaskCanceledException()).Kind);

                var generic = PairingOutcomePresenter.PresentException(new InvalidOperationException("internal detail"));
                AssertEqual(PairingOutcomeKind.Failed, generic.Kind);
                AssertTrue(generic.Message.Contains("InvalidOperationException") && !generic.Message.Contains("internal detail"), generic.Message);
            }),
            ("Pairing: invalid input (ArgumentException) focuses the field and drops the parameter suffix", () =>
            {
                var tooLong = new string('x', ProvisioningConstants.MaxPassphraseBytes + 1);
                var ex = AssertThrows<ArgumentException>(() => WifiConfigClient.ValidateCredentials("home", tooLong));
                var outcome = PairingOutcomePresenter.PresentException(ex);
                AssertEqual(PairingField.Password, outcome.Focus);
                AssertTrue(!outcome.Message.Contains("(Parameter"), outcome.Message);
                AssertTrue(!outcome.Message.Contains(tooLong), "password not echoed");

                var ssid = PairingOutcomePresenter.PresentException(new ArgumentException("bad", "ssid"));
                AssertEqual(PairingField.Ssid, ssid.Focus);
                var pop = PairingOutcomePresenter.PresentException(new ArgumentException("bad", "pop"));
                AssertEqual(PairingField.PairingCode, pop.Focus);
            }),
            ("Pairing: ValidateInputs checks device, code, SSID and password without echoing them", () =>
            {
                AssertEqual(PairingField.Device, Validate(false, "ABCDEFGH", "home", Secret).Field);
                AssertEqual(PairingField.PairingCode, Validate(true, "ABCDEFG0", "home", Secret).Field);   // 0 not in alphabet
                AssertEqual(PairingField.PairingCode, Validate(true, "", "home", Secret).Field);
                AssertEqual(PairingField.Ssid, Validate(true, "ABCDEFGH", "", Secret).Field);
                var ssid33 = new string('s', ProvisioningConstants.MaxSsidBytes + 1);
                var longSsid = Validate(true, "ABCDEFGH", ssid33, Secret);
                AssertEqual(PairingField.Ssid, longSsid.Field);
                AssertTrue(!longSsid.Error!.Contains(ssid33) && !longSsid.Error.Contains(Secret), "values not echoed");
                AssertEqual(PairingField.Password, Validate(true, "ABCDEFGH", "home", new string('p', 64)).Field);

                var ok = Validate(true, " abcd-efgh ", "home", Secret);
                AssertTrue(ok.Error is null, "valid input");
                AssertEqual("ABCDEFGH", ok.Code);
                AssertTrue(Validate(true, "ABCDEFGH", "open-network", "").Error is null, "open network (empty password) allowed");
            }),
            ("Pairing: scan status texts cover none / one / many / Bluetooth off / unavailable", () =>
            {
                AssertTrue(PairingOutcomePresenter.DescribeScanResult(0).Contains(ProvisioningConstants.ReprovisionInstruction), "none: BOOT hint");
                AssertTrue(PairingOutcomePresenter.DescribeScanResult(1).Contains("已自动选择"), "one: preselected");
                AssertTrue(PairingOutcomePresenter.DescribeScanResult(3).Contains("3"), "many: count");
                AssertTrue(PairingOutcomePresenter.DescribeScanFailure(
                    new ProvisioningTransportException(ProvisioningTransportError.BluetoothOff, "off")).Contains("蓝牙已关闭"), "off");
                AssertTrue(PairingOutcomePresenter.DescribeScanFailure(
                    new ProvisioningTransportException(ProvisioningTransportError.BluetoothUnavailable, "none")).Contains("低功耗蓝牙"), "unavailable");
                AssertTrue(PairingOutcomePresenter.DescribeScanFailure(new InvalidOperationException("x")).Contains("see the log"), "generic");
            }),
            ("Pairing: daemon status text after saving", () =>
            {
                AssertEqual("已连接，正在发送数据 (connected, publishing)", PairingOutcomePresenter.DescribeDaemonStatus(true, false, Host));
                AssertTrue(PairingOutcomePresenter.DescribeDaemonStatus(false, true, Host).Contains("paused"), "paused");
                AssertTrue(PairingOutcomePresenter.DescribeDaemonStatus(false, false, Host).Contains(Host), "retrying");
            }),
            ("Pairing: first-run prompt decision", () =>
            {
                AssertTrue(PairingOutcomePresenter.ShouldOfferFirstRunPairing(true, false, "", false), "unconfigured, first failure");
                AssertTrue(PairingOutcomePresenter.ShouldOfferFirstRunPairing(true, false, null, false), "null fallback");
                AssertTrue(!PairingOutcomePresenter.ShouldOfferFirstRunPairing(false, false, "", false), "not a failure notification");
                AssertTrue(!PairingOutcomePresenter.ShouldOfferFirstRunPairing(true, true, "", false), "already prompted");
                AssertTrue(!PairingOutcomePresenter.ShouldOfferFirstRunPairing(true, false, "192.168.1.50", false), "fallback configured");
                AssertTrue(!PairingOutcomePresenter.ShouldOfferFirstRunPairing(true, false, "", true), "connected earlier");
            }),
            ("Pairing: first-run prompt is recorded in a marker file and shown once", () =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "resmon-pairing-tests-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var marker = Path.Combine(directory, FirstRunPairingPrompt.MarkerFileName);
                    var prompt = new FirstRunPairingPrompt(marker);
                    AssertTrue(!prompt.TryClaim(false, ""), "info notification does not trigger");
                    AssertTrue(!File.Exists(marker), "nothing recorded yet");
                    AssertTrue(prompt.TryClaim(true, ""), "first failure triggers");
                    AssertTrue(File.Exists(marker), "marker written");
                    AssertTrue(!prompt.TryClaim(true, ""), "only once per session");
                    AssertTrue(!new FirstRunPairingPrompt(marker).TryClaim(true, ""), "not again after restart");

                    var connected = new FirstRunPairingPrompt(Path.Combine(directory, "other"));
                    connected.ObserveConnection(false);
                    connected.ObserveConnection(true);
                    connected.ObserveConnection(false);
                    AssertTrue(!connected.TryClaim(true, ""), "no prompt after a connection this session");
                    AssertTrue(!new FirstRunPairingPrompt(Path.Combine(directory, "third")).TryClaim(true, "10.0.0.2"), "fallback configured");
                }
                finally
                {
                    if (Directory.Exists(directory))
                    {
                        Directory.Delete(directory, recursive: true);
                    }
                }
            }),
            ("Pairing: netsh output parser finds one SSID and its band, ignores BSSID and odd output", () =>
            {
                const string connected =
                    "There is 1 interface on the system: \r\n\r\n" +
                    "    Name                   : Wi-Fi\r\n" +
                    "    State                  : connected\r\n" +
                    "    SSID                   : Home Net: 2\r\n" +
                    "    AP BSSID               : 00:00:5e:00:53:01\r\n" +
                    "    Band                   : 5 GHz\r\n" +
                    "    Channel                : 40\r\n" +
                    "    Profile                : Home Net: 2 \r\n";
                var info = CurrentWifiNetwork.ParseNetshInterfaces(connected);
                AssertTrue(info is not null, "parsed");
                AssertEqual("Home Net: 2", info!.Ssid);
                AssertEqual("5 GHz", info.Band);
                AssertTrue(info.IsNot24GHz, "5 GHz flagged");

                var on24 = CurrentWifiNetwork.ParseNetshInterfaces(connected.Replace("5 GHz", "2.4 GHz"));
                AssertTrue(on24 is not null && !on24.IsNot24GHz, "2.4 GHz not flagged");

                var localized = CurrentWifiNetwork.ParseNetshInterfaces("    名称 : WLAN\n    SSID : 家里\n    波段 : 5 GHz\n");
                AssertTrue(localized is not null && localized.Ssid == "家里" && localized.Band is null && !localized.IsNot24GHz, "localized keys");

                AssertTrue(CurrentWifiNetwork.ParseNetshInterfaces("    Name : Wi-Fi\n    State : disconnected\n") is null, "disconnected");
                AssertTrue(CurrentWifiNetwork.ParseNetshInterfaces(
                    "Network shell commands need location permission to access WLAN information.\n") is null, "location permission");
                AssertTrue(CurrentWifiNetwork.ParseNetshInterfaces("    SSID : A\n    SSID : B\n") is null, "two adapters: ambiguous");
                AssertTrue(CurrentWifiNetwork.ParseNetshInterfaces("    SSID : Caf? Wi-Fi\n") is null, "garbled code page");
                AssertTrue(CurrentWifiNetwork.ParseNetshInterfaces("    SSID : \n") is null, "empty");
                AssertTrue(CurrentWifiNetwork.ParseNetshInterfaces(null) is null, "null");
            }),
            ("Pairing: broker probe reports a listening port, an already-connected daemon, and gives up on its budget", () =>
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                try
                {
                    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    AssertTrue(BrokerProbe.WaitForBrokerAsync("127.0.0.1", port, null, TimeSpan.FromSeconds(5), CancellationToken.None)
                        .GetAwaiter().GetResult(), "listening port reachable");
                }
                finally
                {
                    listener.Stop();
                }

                AssertTrue(BrokerProbe.WaitForBrokerAsync("host.invalid", 1883, () => true, TimeSpan.FromSeconds(5), CancellationToken.None)
                    .GetAwaiter().GetResult(), "daemon already connected");

                var closed = new TcpListener(IPAddress.Loopback, 0);
                closed.Start();
                var closedPort = ((IPEndPoint)closed.LocalEndpoint).Port;
                closed.Stop();
                var watch = Stopwatch.StartNew();
                AssertTrue(!BrokerProbe.WaitForBrokerAsync("127.0.0.1", closedPort, () => false, TimeSpan.FromSeconds(1.5), CancellationToken.None)
                    .GetAwaiter().GetResult(), "closed port not reachable");
                AssertTrue(watch.Elapsed < TimeSpan.FromSeconds(5), $"gave up on budget ({watch.Elapsed.TotalSeconds:F1} s)");

                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                AssertThrowsAsync<OperationCanceledException>(() =>
                    BrokerProbe.WaitForBrokerAsync("127.0.0.1", closedPort, null, TimeSpan.FromSeconds(5), cancelled.Token))
                    .GetAwaiter().GetResult();
            }),
        };

        // Class members win over the top-level helpers in Program.cs; reuse ProvisioningTests' asserts.
        private static void AssertTrue(bool condition, string message) => ProvisioningTests.AssertTrue(condition, message);

        private static void AssertEqual<T>(T expected, T actual) => ProvisioningTests.AssertEqual(expected, actual);

        private static TException AssertThrows<TException>(Action action) where TException : Exception =>
            ProvisioningTests.AssertThrows<TException>(action);

        private static Task<TException> AssertThrowsAsync<TException>(Func<Task> action) where TException : Exception =>
            ProvisioningTests.AssertThrowsAsync<TException>(action);

        private static (string? Error, PairingField Field, string Code) Validate(bool deviceSelected, string code, string ssid, string password)
        {
            var error = PairingOutcomePresenter.ValidateInputs(deviceSelected, code, ssid, password, out var normalized, out var field);
            return (error, field, normalized);
        }
    }
}
