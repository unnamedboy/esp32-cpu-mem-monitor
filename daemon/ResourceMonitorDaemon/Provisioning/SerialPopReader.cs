using System.Diagnostics;
using System.IO.Ports;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ResourceMonitorDaemon.Provisioning
{
    /// <summary>Pairing code (protocomm proof of possession) format from the pairing contract.</summary>
    public static class PairingCode
    {
        public static bool IsValid(string? code) =>
            code is not null
            && code.Length == ProvisioningConstants.PopLength
            && code.All(c => ProvisioningConstants.PopAlphabet.Contains(c));

        /// <summary>
        /// Normalizes user input: trims, removes spaces and dashes, upper-cases. Returns false when the
        /// result is not a valid 8-character code. Never logs or echoes the input.
        /// </summary>
        public static bool TryNormalize(string? input, out string code)
        {
            code = string.Empty;
            if (input is null)
            {
                return false;
            }

            var builder = new StringBuilder(input.Length);
            foreach (var c in input)
            {
                if (c is ' ' or '-' or '\t')
                {
                    continue;
                }

                builder.Append(char.ToUpperInvariant(c));
            }

            var candidate = builder.ToString();
            if (!IsValid(candidate))
            {
                return false;
            }

            code = candidate;
            return true;
        }
    }

    /// <summary>A parsed reply to the USB serial pairing-code query.</summary>
    public abstract record PopReply
    {
        private PopReply() { }

        /// <summary><c>RESMON-POP name=PROV_RESMON_XXXXXX pop=ABCDEFGH</c>.</summary>
        public sealed record Available(string DeviceName, string Pop) : PopReply
        {
            public override string ToString() => $"Available {{ DeviceName = {DeviceName}, Pop = <redacted> }}";
        }

        /// <summary><c>RESMON-POP unavailable state=&lt;state&gt;</c> (device not in provisioning mode).</summary>
        public sealed record Unavailable(string State) : PopReply;
    }

    /// <summary>Pure parser for the USB serial pairing-code reply (see pairing contract).</summary>
    public static class PopLineParser
    {
        private const string ReplyPrefix = "RESMON-POP ";

        /// <summary>
        /// Parses one serial line (with or without trailing CR/LF). Returns null for anything that is
        /// not a well-formed reply, e.g. boot logs, the hint line, or a malformed name or code.
        /// </summary>
        public static PopReply? Parse(string? line)
        {
            if (string.IsNullOrEmpty(line))
            {
                return null;
            }

            // Tolerate log noise in front of the reply on the same line.
            var start = line.IndexOf(ReplyPrefix, StringComparison.Ordinal);
            if (start < 0)
            {
                return null;
            }

            var tokens = line[(start + ReplyPrefix.Length)..]
                .TrimEnd('\r', '\n')
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length == 0)
            {
                return null;
            }

            if (tokens[0] == "unavailable")
            {
                var state = tokens.Skip(1).Select(t => ValueOf(t, "state")).FirstOrDefault(v => v is not null);
                return new PopReply.Unavailable(string.IsNullOrEmpty(state) ? "unknown" : state);
            }

            string? name = null;
            string? pop = null;
            foreach (var token in tokens)
            {
                name ??= ValueOf(token, "name");
                pop ??= ValueOf(token, "pop");
            }

            return IsContractDeviceName(name) && PairingCode.IsValid(pop)
                ? new PopReply.Available(name!, pop!)
                : null;
        }

        /// <summary>PROV_RESMON_ followed by exactly 6 uppercase hex digits.</summary>
        public static bool IsContractDeviceName(string? name) =>
            name is not null
            && name.Length == ProvisioningConstants.DeviceNamePrefix.Length + 6
            && name.StartsWith(ProvisioningConstants.DeviceNamePrefix, StringComparison.Ordinal)
            && name[ProvisioningConstants.DeviceNamePrefix.Length..].All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

        private static string? ValueOf(string token, string key) =>
            token.Length > key.Length + 1 && token.StartsWith(key + "=", StringComparison.Ordinal)
                ? token[(key.Length + 1)..]
                : null;
    }

    /// <summary>A device that answered the pairing-code query on a COM port.</summary>
    public sealed record UsbPopMatch(string PortName, string DeviceName, string Pop)
    {
        public override string ToString() => $"UsbPopMatch {{ PortName = {PortName}, DeviceName = {DeviceName}, Pop = <redacted> }}";
    }

    /// <summary>
    /// Reads the pairing code over USB serial: on every COM port, opens 115200 8N1 with DTR and RTS
    /// deasserted (so the ESP32 auto-reset circuit is not triggered), sends <c>RESMON?POP\n</c> and
    /// waits up to 1.5 s for a <c>RESMON-POP</c> reply. Ports are probed in parallel. Busy or
    /// inaccessible ports are skipped silently; this class never throws for port errors.
    /// The pairing code is never logged.
    /// UNVERIFIED: not yet exercised against a physical ESP32 / USB-UART bridge.
    /// </summary>
    public sealed class SerialPopReader
    {
        private const int MaxLineLength = 256;
        private static readonly TimeSpan ResendAfter = TimeSpan.FromMilliseconds(600);

        private readonly ILogger _logger;

        public SerialPopReader(ILogger? logger = null)
        {
            _logger = logger ?? NullLogger.Instance;
        }

        /// <summary>Returns the pairing code reported by <paramref name="deviceName"/>, or null if no port answered for it.</summary>
        public async Task<string?> TryReadPopAsync(string deviceName, CancellationToken cancellationToken)
        {
            var matches = await QueryAllPortsAsync(cancellationToken).ConfigureAwait(false);
            return FindPop(matches, deviceName);
        }

        /// <summary>Queries every COM port in parallel and returns all devices that reported a pairing code.</summary>
        public async Task<IReadOnlyList<UsbPopMatch>> QueryAllPortsAsync(CancellationToken cancellationToken)
        {
            string[] ports;
            try
            {
                ports = SerialPort.GetPortNames().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogInformation("Could not enumerate serial ports: {Error}", ex.Message);
                return Array.Empty<UsbPopMatch>();
            }

            var results = await Task.WhenAll(ports.Select(p => ProbeWithBudgetAsync(p, cancellationToken))).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return results.OfType<UsbPopMatch>().ToArray();
        }

        /// <summary>Picks the code for the BLE device being paired (name match is case-insensitive).</summary>
        public static string? FindPop(IEnumerable<UsbPopMatch> matches, string deviceName) =>
            matches.FirstOrDefault(m => string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))?.Pop;

        private async Task<UsbPopMatch?> ProbeWithBudgetAsync(string portName, CancellationToken cancellationToken)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(ProvisioningConstants.SerialPortProbeBudget);

            // Serial I/O is synchronous; SerialPort.Open can block (e.g. Bluetooth virtual COM ports), so
            // the probe runs on the thread pool and is abandoned if it overruns its budget.
            var probe = Task.Run(() => ProbePort(portName, budget.Token));
            var overrun = Task.Delay(Timeout.Infinite, budget.Token);
            var winner = await Task.WhenAny(probe, overrun).ConfigureAwait(false);
            if (winner != probe)
            {
                _ = probe.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
                _logger.LogDebug("Serial port {Port} did not answer within the probe budget.", portName);
                return null;
            }

            try
            {
                return await probe.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Serial port {Port} probe failed: {Error}", portName, ex.Message);
                return null;
            }
        }

        private UsbPopMatch? ProbePort(string portName, CancellationToken budget)
        {
            try
            {
                using var port = new SerialPort(portName, ProvisioningConstants.SerialBaudRate, Parity.None, 8, StopBits.One)
                {
                    DtrEnable = false,
                    RtsEnable = false,
                    Handshake = Handshake.None,
                    ReadTimeout = 100,
                    WriteTimeout = 300,
                    Encoding = Encoding.ASCII,
                };

                port.Open();
                port.DiscardInBuffer();
                port.Write(ProvisioningConstants.SerialPopQuery);
                var sinceWrite = Stopwatch.StartNew();
                var resent = false;

                var line = new StringBuilder(MaxLineLength);
                var discardingLongLine = false;
                var buffer = new byte[256];
                while (!budget.IsCancellationRequested)
                {
                    int count;
                    try
                    {
                        count = port.Read(buffer, 0, buffer.Length);
                    }
                    catch (TimeoutException)
                    {
                        count = 0;
                    }

                    for (var i = 0; i < count; i++)
                    {
                        var c = (char)buffer[i];
                        if (c == '\n')
                        {
                            if (!discardingLongLine)
                            {
                                var reply = PopLineParser.Parse(line.ToString());
                                if (reply is PopReply.Available available)
                                {
                                    _logger.LogInformation(
                                        "Read pairing code for {Device} from serial port {Port}.", available.DeviceName, portName);
                                    return new UsbPopMatch(portName, available.DeviceName, available.Pop);
                                }

                                if (reply is PopReply.Unavailable unavailable)
                                {
                                    _logger.LogInformation(
                                        "Device on serial port {Port} is not in provisioning mode (state {State}).", portName, unavailable.State);
                                    return null;
                                }
                            }

                            line.Clear();
                            discardingLongLine = false;
                        }
                        else if (line.Length < MaxLineLength)
                        {
                            line.Append(c);
                        }
                        else
                        {
                            line.Clear();
                            discardingLongLine = true;
                        }
                    }

                    if (!resent && sinceWrite.Elapsed >= ResendAfter)
                    {
                        // The first query may have landed while the firmware was busy printing boot logs.
                        port.Write(ProvisioningConstants.SerialPopQuery);
                        resent = true;
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                // Busy (UnauthorizedAccessException), vanished (IOException), invalid name, etc.
                _logger.LogDebug("Serial port {Port} skipped: {Error}", portName, ex.GetType().Name);
                return null;
            }
        }
    }
}
