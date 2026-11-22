using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ResourceMonitorDaemon.Observability
{
    /// <summary>
    /// Writes one metrics line per <see cref="LokiSettings.MetricsIntervalSeconds"/> to
    /// <c>{app="resmon-daemon", kind="metrics"}</c> from the samples the daemon already takes, e.g.
    /// <c>{"cpu":12.3,"mem":45.0,"mem_used_gb":7.21,"mem_total_gb":15.86,"mqtt":"connected"}</c>.
    /// </summary>
    public sealed class LokiMetricsForwarder : IMetricsObserver
    {
        // Sampling jitter must not push a line into the next interval; smaller than the minimum sample period.
        private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(200);

        private readonly LokiSink _sink;
        private readonly Func<DateTimeOffset> _clock;
        private DateTimeOffset? _lastSent;

        public LokiMetricsForwarder(LokiSink sink, Func<DateTimeOffset>? clock = null)
        {
            _sink = sink;
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        public void OnSample(in MetricsSample sample)
        {
            try
            {
                var settings = _sink.Settings;
                if (!settings.IsActive || settings.MetricsIntervalSeconds <= 0)
                {
                    _lastSent = null;
                    return;
                }

                var now = _clock();
                if (_lastSent is { } last && now - last < TimeSpan.FromSeconds(settings.MetricsIntervalSeconds) - Tolerance)
                {
                    return;
                }

                if (_sink.Queue.TryEnqueue(LokiStreamKind.Metrics, null, FormatLine(sample)))
                {
                    _lastSent = now;
                }
            }
            catch (Exception)
            {
                // Observability must never affect sampling.
            }
        }

        /// <summary>Compact JSON, invariant formatting, percentages clamped to 0..100.</summary>
        public static string FormatLine(in MetricsSample sample)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteNumber("cpu", Math.Round(Math.Clamp(Finite(sample.CpuPercent), 0, 100), 1));
                writer.WriteNumber("mem", Math.Round(Math.Clamp(Finite(sample.MemoryPercent), 0, 100), 1));
                writer.WriteNumber("mem_used_gb", Math.Round(Math.Max(0, Finite(sample.MemoryUsedGb)), 2));
                writer.WriteNumber("mem_total_gb", Math.Round(Math.Max(0, Finite(sample.MemoryTotalGb)), 2));
                writer.WriteString("mqtt", sample.MqttState);
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        private static double Finite(double value) => double.IsFinite(value) ? value : 0;
    }

    /// <summary>
    /// Validates <c>resmon/status</c> payloads (see COMMUNICATION.md): a UTF-8 JSON object of at
    /// most <see cref="MaxPayloadBytes"/> bytes with an integer <c>v</c> between 1 and
    /// <see cref="SupportedVersion"/>. Unknown fields are accepted but never forwarded: the result is
    /// re-serialized from the contract fields only, with type checks and capped string lengths, so a
    /// device (or anyone on the unauthenticated broker) cannot smuggle arbitrary data into Loki.
    /// </summary>
    public static class DeviceStatusValidator
    {
        public const int MaxPayloadBytes = 1024;
        public const int SupportedVersion = 1;
        public const int MaxBuildLength = 64;
        public const int MaxStateLength = 32;

        private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        /// <summary>
        /// Returns true with the normalized JSON (contract fields only, contract order), or false
        /// with a short reason (never the payload). A contract field with the wrong type is omitted.
        /// </summary>
        public static bool TryValidate(ReadOnlySpan<byte> payload, out string json, out string error)
        {
            json = string.Empty;
            if (payload.Length == 0)
            {
                error = "empty payload";
                return false;
            }

            if (payload.Length > MaxPayloadBytes)
            {
                error = $"payload is {payload.Length} bytes (max {MaxPayloadBytes})";
                return false;
            }

            string text;
            try
            {
                text = StrictUtf8.GetString(payload);
            }
            catch (DecoderFallbackException)
            {
                error = "payload is not valid UTF-8";
                return false;
            }

            try
            {
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    error = "payload is not a JSON object";
                    return false;
                }

                if (!root.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var v))
                {
                    error = "missing or non-integer \"v\"";
                    return false;
                }

                if (v < 1 || v > SupportedVersion)
                {
                    error = $"unsupported schema version v={v} (supported: 1..{SupportedVersion})";
                    return false;
                }

                json = Normalize(root, v);
            }
            catch (JsonException)
            {
                error = "malformed JSON";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static string Normalize(JsonElement root, int version)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteNumber("v", version);
                WriteString(writer, root, "build", MaxBuildLength);
                WriteInteger(writer, root, "uptime_s", nonNegative: true);
                WriteInteger(writer, root, "heap_free", nonNegative: true);
                WriteInteger(writer, root, "heap_min", nonNegative: true);
                WriteInteger(writer, root, "rssi", nonNegative: false);
                WriteString(writer, root, "wifi", MaxStateLength);
                WriteString(writer, root, "prov", MaxStateLength);
                if (root.TryGetProperty("dac", out var dac) && dac.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    writer.WriteBoolean("dac", dac.GetBoolean());
                }

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        private static void WriteString(Utf8JsonWriter writer, JsonElement root, string name, int maxLength)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString() ?? string.Empty;
                writer.WriteString(name, text.Length > maxLength ? text[..maxLength] : text);
            }
        }

        private static void WriteInteger(Utf8JsonWriter writer, JsonElement root, string name, bool nonNegative)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt64(out var number) && (!nonNegative || number >= 0))
            {
                writer.WriteNumber(name, number);
            }
        }
    }

    /// <summary>
    /// Handles messages on <see cref="DeviceSettings.StatusTopic"/>: validates and normalizes them,
    /// logs a concise local Debug line, and forwards them to
    /// <c>{app="resmon-device", kind="status"}</c> when the sink is enabled. A device that never
    /// publishes status (older firmware) is normal.
    /// </summary>
    /// <remarks>
    /// The broker is unauthenticated, so forwarding is rate-limited by a token bucket
    /// (<see cref="Burst"/> messages, then one per <see cref="RefillInterval"/>; the contract
    /// interval is 10 s). Excess messages are counted and reported locally at most once per
    /// <see cref="ExcessReportInterval"/>; they never reach Loki or evict queued daemon logs.
    /// </remarks>
    public sealed class DeviceStatusForwarder
    {
        public const int Burst = 3;
        public static readonly TimeSpan RefillInterval = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan ExcessReportInterval = TimeSpan.FromMinutes(1);

        private readonly LokiSink _sink;
        private readonly ILogger<DeviceStatusForwarder> _logger;
        private readonly Func<DateTimeOffset> _clock;
        private readonly object _gate = new();
        private readonly HashSet<string> _reportedErrors = new();
        private double _tokens = Burst;
        private DateTimeOffset _lastRefill;
        private DateTimeOffset _lastExcessReport = DateTimeOffset.MinValue;
        private long _excessSinceReport;
        private long _received;
        private long _rateLimited;

        public DeviceStatusForwarder(LokiSink sink, ILogger<DeviceStatusForwarder> logger, Func<DateTimeOffset>? clock = null)
        {
            _sink = sink;
            _logger = logger;
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
            _lastRefill = _clock();
        }

        /// <summary>Number of valid status messages received (forwarded or rate-limited).</summary>
        public long Received => Interlocked.Read(ref _received);

        /// <summary>Valid messages not forwarded because of the rate limit.</summary>
        public long RateLimited => Interlocked.Read(ref _rateLimited);

        public void Handle(byte[] payload)
        {
            try
            {
                if (!DeviceStatusValidator.TryValidate(payload, out var json, out var error))
                {
                    bool first;
                    lock (_reportedErrors)
                    {
                        // Bounded: a misbehaving device cannot grow this set.
                        first = _reportedErrors.Count < 32 && _reportedErrors.Add(error);
                    }

                    if (first)
                    {
                        _logger.LogInformation("Ignoring device status message: {Error}. Similar messages are logged at Debug.", error);
                    }
                    else
                    {
                        _logger.LogDebug("Ignoring device status message: {Error}.", error);
                    }

                    return;
                }

                Interlocked.Increment(ref _received);
                if (!TryTakeToken(out var excessToReport))
                {
                    Interlocked.Increment(ref _rateLimited);
                    if (excessToReport > 0)
                    {
                        _logger.LogWarning(
                            "Device status arrives faster than expected: {Count} message(s) since the last report were not forwarded to Loki (limit: burst {Burst}, then 1 per {Refill:F0} s).",
                            excessToReport, Burst, RefillInterval.TotalSeconds);
                    }

                    return;
                }

                _logger.LogDebug("Device status: {Status}", json);
                _sink.Queue.TryEnqueue(LokiStreamKind.Status, null, json);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Device status handling failed: {Error}", ex.Message);
            }
        }

        /// <summary>
        /// Takes a token if available. Otherwise counts the excess and, for the first excess and then
        /// at most once per <see cref="ExcessReportInterval"/>, returns the count to report.
        /// </summary>
        private bool TryTakeToken(out long excessToReport)
        {
            excessToReport = 0;
            lock (_gate)
            {
                var now = _clock();
                if (now > _lastRefill)
                {
                    _tokens = Math.Min(Burst, _tokens + (now - _lastRefill) / RefillInterval);
                    _lastRefill = now;
                }

                if (_tokens >= 1)
                {
                    _tokens -= 1;
                    return true;
                }

                _excessSinceReport++;
                if (now - _lastExcessReport >= ExcessReportInterval)
                {
                    excessToReport = _excessSinceReport;
                    _excessSinceReport = 0;
                    _lastExcessReport = now;
                }

                return false;
            }
        }
    }
}
