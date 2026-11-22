using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ResourceMonitorDaemon.Pairing
{
    /// <summary>The Wi-Fi network this PC is connected to. <see cref="Band"/> is null when not reported.</summary>
    public sealed record CurrentWifiInfo(string Ssid, string? Band)
    {
        /// <summary>True when Windows reports a 5 GHz or 6 GHz band (the ESP32 needs 2.4 GHz).</summary>
        public bool IsNot24GHz =>
            Band is not null
            && (Band.Contains('5', StringComparison.Ordinal) || Band.Contains('6', StringComparison.Ordinal))
            && !Band.Contains("2.4", StringComparison.Ordinal);
    }

    /// <summary>
    /// Best-effort lookup of the PC's current Wi-Fi SSID to prefill the wizard, using the built-in
    /// <c>netsh wlan show interfaces</c> (no extra packages, no admin rights). Any problem (no Wi-Fi
    /// adapter, location permission required on newer Windows builds, unexpected or garbled output,
    /// several connected adapters) simply yields null and the user types the SSID.
    /// The SSID is never logged.
    /// </summary>
    public static class CurrentWifiNetwork
    {
        private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(3);

        public static async Task<CurrentWifiInfo?> TryGetAsync(CancellationToken cancellationToken)
        {
            try
            {
                var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"), "wlan show interfaces")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = GetOemEncoding(),
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
                    var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
                    _ = process.StandardError.ReadToEndAsync(timeout.Token);
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                    return process.ExitCode == 0 ? ParseNetshInterfaces(await outputTask.ConfigureAwait(false)) : null;
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
                return null; // optional convenience only
            }
        }

        /// <summary>
        /// Parses <c>netsh wlan show interfaces</c> output. Returns the SSID when exactly one interface
        /// reports a plausible one (the "SSID" key is not localized; "BSSID"/"AP BSSID" lines are
        /// ignored), with the "Band" value if present (English output only).
        /// </summary>
        public static CurrentWifiInfo? ParseNetshInterfaces(string? output)
        {
            if (string.IsNullOrEmpty(output))
            {
                return null;
            }

            var found = new List<CurrentWifiInfo>();
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
                    found.Add(new CurrentWifiInfo(value, null));
                }
                else if (string.Equals(key, "Band", StringComparison.OrdinalIgnoreCase) && found.Count > 0 && found[^1].Band is null)
                {
                    found[^1] = found[^1] with { Band = value };
                }
            }

            if (found.Count != 1 || !IsPlausibleSsid(found[0].Ssid))
            {
                return null;
            }

            return found[0];
        }

        private static bool IsPlausibleSsid(string ssid) =>
            ssid.Length > 0
            && Encoding.UTF8.GetByteCount(ssid) <= Provisioning.ProvisioningConstants.MaxSsidBytes
            && !ssid.Contains('�')
            && !ssid.Contains('?')            // a code-page conversion lost characters
            && !ssid.Any(char.IsControl);

        private static Encoding GetOemEncoding()
        {
            try
            {
                return CodePagesEncodingProvider.Instance.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage)
                    ?? Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            }
            catch (Exception)
            {
                return Encoding.UTF8;
            }
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
    }
}
