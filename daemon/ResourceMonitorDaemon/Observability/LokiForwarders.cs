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
    /// <see cref="SupportedVersion"/>. Unknown fields are allowed.
    /// </summary>
    public static class DeviceStatusValidator
    {
        public const int MaxPayloadBytes = 1024;
        public const int SupportedVersion = 1;

        private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        /// <summary>Returns true with the payload text, or false with a short reason (never the payload).</summary>
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
            }
            catch (JsonException)
            {
                error = "malformed JSON";
                return false;
            }

            json = text;
            error = string.Empty;
            return true;
        }
    }

    /// <summary>
    /// Handles messages on <see cref="DeviceSettings.StatusTopic"/>: validates them, logs a concise
    /// local Debug line, and forwards the raw JSON to <c>{app="resmon-device", kind="status"}</c>
    /// when the sink is enabled. A device that never publishes status (older firmware) is normal.
    /// </summary>
    public sealed class DeviceStatusForwarder
    {
        private readonly LokiSink _sink;
        private readonly ILogger<DeviceStatusForwarder> _logger;
        private readonly HashSet<string> _reportedErrors = new();
        private long _received;

        public DeviceStatusForwarder(LokiSink sink, ILogger<DeviceStatusForwarder> logger)
        {
            _sink = sink;
            _logger = logger;
        }

        /// <summary>Number of valid status messages received.</summary>
        public long Received => Interlocked.Read(ref _received);

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
                _logger.LogDebug("Device status: {Status}", json);
                _sink.Queue.TryEnqueue(LokiStreamKind.Status, null, json);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Device status handling failed: {Error}", ex.Message);
            }
        }
    }
}
