using System.Globalization;

namespace ResourceMonitorDaemon
{
    public static class MetricPayloadFormatter
    {
        public static string FormatPercentPayload(double percent)
        {
            var clamped = Math.Clamp(percent, 0.0, 100.0);
            return clamped.ToString("F1", CultureInfo.InvariantCulture);
        }
    }
}
