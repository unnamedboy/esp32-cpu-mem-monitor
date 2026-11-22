namespace ResourceMonitorDaemon
{
    /// <summary>One sample of the existing sampling loop (no extra performance counters).</summary>
    /// <param name="CpuPercent">CPU usage, 0..100.</param>
    /// <param name="MemoryPercent">Physical memory load, 0..100.</param>
    /// <param name="MemoryUsedGb">Used physical memory in GiB.</param>
    /// <param name="MemoryTotalGb">Total physical memory in GiB.</param>
    /// <param name="MqttState"><c>connected</c>, <c>disconnected</c> or <c>paused</c>.</param>
    public readonly record struct MetricsSample(double CpuPercent, double MemoryPercent, double MemoryUsedGb, double MemoryTotalGb, string MqttState);

    /// <summary>
    /// Receives every sample of <see cref="MonitorService"/>'s loop. Called on the sampling thread:
    /// implementations must return immediately and must not throw.
    /// </summary>
    public interface IMetricsObserver
    {
        void OnSample(in MetricsSample sample);
    }
}
