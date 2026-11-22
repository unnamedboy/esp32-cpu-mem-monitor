using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ResourceMonitorDaemon.Provisioning
{
    /// <summary>
    /// What the registry says about one COM port. Any field can be unknown (null).
    /// </summary>
    /// <param name="PortName">COM port name, e.g. <c>COM5</c>.</param>
    /// <param name="KernelDeviceName">
    /// Device name from <c>HKLM\HARDWARE\DEVICEMAP\SERIALCOMM</c>, e.g. <c>\Device\Silabser0</c>,
    /// <c>\Device\Serial0</c> (legacy UART) or <c>\Device\BthModem0</c> (Bluetooth).
    /// </param>
    /// <param name="UsbVendorId">USB vendor ID of the device that owns the port, if it is a USB device.</param>
    public sealed record SerialPortCandidate(string PortName, string? KernelDeviceName, ushort? UsbVendorId);

    /// <summary>
    /// Decides which COM ports the USB pairing-code query (<c>RESMON?POP</c>) may be sent to, so
    /// unrelated serial devices are not probed. Pure; see <see cref="SerialPortInventory"/> for the
    /// registry lookup.
    /// </summary>
    /// <remarks>
    /// Rules, in order:
    /// <list type="number">
    /// <item>USB vendor ID known: probe only USB-UART bridges commonly used on ESP32 boards
    /// (<see cref="Esp32BridgeVendorIds"/>). Any other USB serial device is skipped.</item>
    /// <item>Vendor ID unknown: skip ports whose kernel device name is clearly not USB
    /// (legacy <c>\Device\Serial&lt;n&gt;</c> UARTs, Bluetooth <c>BthModem</c>/<c>BthEnum</c> ports).</item>
    /// <item>Otherwise (nothing known, e.g. registry unreadable) probe, as before, so auto-read keeps
    /// working; the user can always type the code instead.</item>
    /// </list>
    /// </remarks>
    public static class SerialPortFilter
    {
        /// <summary>Silicon Labs CP210x, WCH CH340/CH343/CH9102, FTDI, Espressif native USB (S2/S3/C3/C6).</summary>
        public static IReadOnlyCollection<ushort> Esp32BridgeVendorIds { get; } = new HashSet<ushort>
        {
            0x10C4, // Silicon Labs (CP2102/CP2104)
            0x1A86, // WCH (CH340, CH343, CH9102)
            0x0403, // FTDI
            0x303A, // Espressif (native USB Serial/JTAG, USB-CDC)
        };

        private static readonly Regex LegacyUart = new(@"^\\Device\\Serial\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex VendorIdInEnumKey = new(@"(?:^|[\\&+])VID_([0-9A-F]{4})(?=$|[&+\\])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Returns true if the port may be probed; <paramref name="reason"/> explains the decision (for Debug logs).</summary>
        public static bool ShouldProbe(SerialPortCandidate candidate, out string reason)
        {
            if (candidate.UsbVendorId is { } vendorId)
            {
                var known = Esp32BridgeVendorIds.Contains(vendorId);
                reason = known
                    ? $"USB vendor {vendorId:X4} is a known ESP32 USB-UART bridge"
                    : $"USB vendor {vendorId:X4} is not a known ESP32 USB-UART bridge";
                return known;
            }

            var device = candidate.KernelDeviceName;
            if (device is not null)
            {
                if (LegacyUart.IsMatch(device))
                {
                    reason = $"{device} is a legacy (non-USB) UART";
                    return false;
                }

                if (device.Contains("BthModem", StringComparison.OrdinalIgnoreCase)
                    || device.Contains("BthEnum", StringComparison.OrdinalIgnoreCase))
                {
                    reason = $"{device} is a Bluetooth serial port";
                    return false;
                }
            }

            reason = "USB vendor unknown; probing";
            return true;
        }

        /// <summary>
        /// Extracts the vendor ID from a device-enumeration key name such as
        /// <c>VID_10C4&amp;PID_EA60</c>, <c>VID_303A&amp;PID_1001&amp;MI_00</c> or the FTDI bus form
        /// <c>VID_0403+PID_6001+A50285BIA</c>. Returns null if there is none.
        /// </summary>
        public static ushort? ParseVendorId(string? enumKeyName)
        {
            if (enumKeyName is null)
            {
                return null;
            }

            var match = VendorIdInEnumKey.Match(enumKeyName);
            return match.Success && ushort.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id)
                ? id
                : null;
        }
    }

    /// <summary>
    /// Reads <see cref="SerialPortCandidate"/>s from the registry (no extra packages, no WMI):
    /// <c>HKLM\HARDWARE\DEVICEMAP\SERIALCOMM</c> for present ports' kernel device names, and
    /// <c>HKLM\SYSTEM\CurrentControlSet\Enum\{USB,FTDIBUS}\VID_…\&lt;instance&gt;\Device Parameters\PortName</c>
    /// for USB vendor IDs. Any registry error just leaves the field unknown.
    /// </summary>
    internal static class SerialPortInventory
    {
        private const string SerialCommKey = @"HARDWARE\DEVICEMAP\SERIALCOMM";
        private static readonly string[] UsbEnumRoots = { @"SYSTEM\CurrentControlSet\Enum\USB", @"SYSTEM\CurrentControlSet\Enum\FTDIBUS" };

        public static IReadOnlyList<SerialPortCandidate> Describe(IEnumerable<string> portNames)
        {
            var deviceByPort = ReadSerialComm();
            var vendorByPort = ReadUsbVendorIds();
            return portNames
                .Select(port => new SerialPortCandidate(
                    port,
                    deviceByPort.GetValueOrDefault(port),
                    vendorByPort.GetValueOrDefault(port)))
                .ToArray();
        }

        private static Dictionary<string, string> ReadSerialComm()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(SerialCommKey);
                if (key is null)
                {
                    return result;
                }

                foreach (var deviceName in key.GetValueNames())
                {
                    if (key.GetValue(deviceName) is string port)
                    {
                        result[port] = deviceName;
                    }
                }
            }
            catch (Exception)
            {
                // Unknown device names fall back to probing.
            }

            return result;
        }

        private static Dictionary<string, ushort?> ReadUsbVendorIds()
        {
            // null value = several devices (e.g. a stale, unplugged one) claim the same COM name:
            // treat the vendor as unknown rather than guess.
            var result = new Dictionary<string, ushort?>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in UsbEnumRoots)
            {
                try
                {
                    using var rootKey = Registry.LocalMachine.OpenSubKey(root);
                    if (rootKey is null)
                    {
                        continue;
                    }

                    foreach (var deviceKeyName in rootKey.GetSubKeyNames())
                    {
                        if (SerialPortFilter.ParseVendorId(deviceKeyName) is not { } vendor)
                        {
                            continue;
                        }

                        using var deviceKey = rootKey.OpenSubKey(deviceKeyName);
                        foreach (var instance in deviceKey?.GetSubKeyNames() ?? Array.Empty<string>())
                        {
                            using var parameters = deviceKey!.OpenSubKey(instance + @"\Device Parameters");
                            if (parameters?.GetValue("PortName") is string port && !string.IsNullOrWhiteSpace(port))
                            {
                                // Non-present devices can keep an old PortName; only ports that exist
                                // right now (SerialPort.GetPortNames) are ever looked up.
                                var name = port.Trim();
                                result[name] = result.TryGetValue(name, out var existing) && existing != vendor ? null : vendor;
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // Unknown vendor IDs fall back to the device-name rule.
                }
            }

            return result;
        }
    }
}
