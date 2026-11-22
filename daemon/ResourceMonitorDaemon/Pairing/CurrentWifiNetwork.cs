using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Windows.Networking.Connectivity;

namespace ResourceMonitorDaemon.Pairing
{
    /// <summary>
    /// The Wi-Fi network this PC is connected to. <see cref="Is24GHz"/> is null when the band could
    /// not be determined.
    /// </summary>
    public sealed record CurrentWifiInfo(string Ssid, bool? Is24GHz)
    {
        /// <summary>True only when the PC is known to use 5 or 6 GHz (the ESP32 needs 2.4 GHz).</summary>
        public bool IsKnownNot24GHz => Is24GHz == false;
    }

    /// <summary>
    /// Best-effort lookup of the PC's current Wi-Fi network to prefill the wizard. No extra packages
    /// and no admin rights are needed. Any problem simply yields null (or an unknown band), and the
    /// user types the SSID. The SSID is never logged.
    /// </summary>
    /// <remarks>
    /// The SSID comes from WinRT (<c>NetworkInformation</c> /
    /// <c>WlanConnectionProfileDetails.GetConnectedSsid</c>), so there is no console code-page
    /// conversion. Only the optional 2.4 GHz hint uses <c>netsh wlan show interfaces</c>, decoded with
    /// the system OEM code page (<c>GetOEMCP</c>, which a hidden console uses, not the user's format
    /// culture). It is read from the "Band" line, or from the channel number (&gt; 14 means 5 GHz)
    /// when there is no band line. Labels are matched in English and Chinese.
    /// </remarks>
    public static class CurrentWifiNetwork
    {
        private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(3);

        private static readonly string[] BandLabels = { "Band", "波段", "频带", "頻帶", "频段", "頻段" };
        private static readonly string[] ChannelLabels = { "Channel", "信道", "通道", "频道", "頻道" };
        private static readonly Regex HighBand = new(@"(?<![\d.])[56](\.\d+)?\s*GHz", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static async Task<CurrentWifiInfo?> TryGetAsync(CancellationToken cancellationToken)
        {
            var ssid = await Task.Run(TryGetConnectedSsid, cancellationToken).ConfigureAwait(false);
            if (ssid is null)
            {
                return null;
            }

            var is24GHz = await TryGetIs24GHzAsync(cancellationToken).ConfigureAwait(false);
            return new CurrentWifiInfo(ssid, is24GHz);
        }

        /// <summary>The SSID of the single connected WLAN profile, or null (none, several, or not readable).</summary>
        private static string? TryGetConnectedSsid()
        {
            try
            {
                var candidates = new List<string?>();
                foreach (var profile in NetworkInformation.GetConnectionProfiles())
                {
                    if (profile.IsWlanConnectionProfile
                        && profile.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None)
                    {
                        candidates.Add(profile.WlanConnectionProfileDetails?.GetConnectedSsid());
                    }
                }

                return SelectSsid(candidates);
            }
            catch (Exception)
            {
                return null; // optional convenience only (e.g. location permission required)
            }
        }

        /// <summary>Returns the SSID when exactly one distinct, plausible SSID is connected; otherwise null.</summary>
        public static string? SelectSsid(IEnumerable<string?> connectedSsids)
        {
            var distinct = connectedSsids
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => s!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return distinct.Count == 1 && IsPlausibleSsid(distinct[0]) ? distinct[0] : null;
        }

        private static bool IsPlausibleSsid(string ssid) =>
            Encoding.UTF8.GetByteCount(ssid) <= Provisioning.ProvisioningConstants.MaxSsidBytes
            && !ssid.Contains('�')
            && !ssid.Any(char.IsControl);

        private static async Task<bool?> TryGetIs24GHzAsync(CancellationToken cancellationToken)
        {
            try
            {
                var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"), "wlan show interfaces")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                };

                using var process = Process.Start(startInfo);
                if (process is null)
                {
                    return null;
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(CommandTimeout);
                try
                {
                    using var buffer = new MemoryStream();
                    await process.StandardOutput.BaseStream.CopyToAsync(buffer, timeout.Token).ConfigureAwait(false);
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                    return process.ExitCode == 0 ? ParseIs24GHz(DecodeConsoleOutput(buffer.ToArray(), GetOEMCP())) : null;
                }
                catch (OperationCanceledException)
                {
                    TryKill(process);
                    cancellationToken.ThrowIfCancellationRequested();
                    return null;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Decodes console output bytes written in <paramref name="codePage"/> (e.g. 936 on Chinese Windows).</summary>
        public static string DecodeConsoleOutput(byte[] output, int codePage)
        {
            Encoding encoding;
            try
            {
                encoding = CodePagesEncodingProvider.Instance.GetEncoding(codePage) ?? Encoding.GetEncoding(codePage);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                encoding = Encoding.UTF8;
            }

            return encoding.GetString(output);
        }

        /// <summary>
        /// Reads the band of the single connected interface from <c>netsh wlan show interfaces</c> output:
        /// true for 2.4 GHz, false for 5/6 GHz, null when unknown (no or several connected interfaces,
        /// unrecognized labels). The "SSID" key itself is not localized and is used to count interfaces.
        /// </summary>
        public static bool? ParseIs24GHz(string? output)
        {
            if (string.IsNullOrEmpty(output))
            {
                return null;
            }

            var ssidLines = 0;
            string? band = null;
            string? channel = null;
            foreach (var rawLine in output.Split('\n'))
            {
                var separator = rawLine.IndexOf(':');
                if (separator <= 0)
                {
                    continue;
                }

                var key = rawLine[..separator].Trim();
                var value = rawLine[(separator + 1)..].Trim();
                if (string.Equals(key, "SSID", StringComparison.OrdinalIgnoreCase))
                {
                    ssidLines++;
                }
                else if (band is null && BandLabels.Any(label => string.Equals(key, label, StringComparison.OrdinalIgnoreCase)))
                {
                    band = value;
                }
                else if (channel is null && ChannelLabels.Any(label => string.Equals(key, label, StringComparison.OrdinalIgnoreCase)))
                {
                    channel = value;
                }
            }

            if (ssidLines != 1)
            {
                return null;
            }

            if (band is not null)
            {
                if (band.Contains("2.4", StringComparison.Ordinal))
                {
                    return true;
                }

                if (HighBand.IsMatch(band))
                {
                    return false;
                }
            }

            // Without a band line (older builds): 2.4 GHz channels are 1-14, 5 GHz channels start at 32.
            // 6 GHz reuses low channel numbers, but builds that support 6 GHz print the band line.
            if (channel is not null && int.TryParse(channel, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number > 0)
            {
                return number <= 14;
            }

            return null;
        }

        private static void TryKill(Process process)
        {
            try
            {
                process.Kill();
            }
            catch (Exception)
            {
                // Already exited.
            }
        }

        [DllImport("kernel32.dll")]
        private static extern int GetOEMCP();
    }
}
