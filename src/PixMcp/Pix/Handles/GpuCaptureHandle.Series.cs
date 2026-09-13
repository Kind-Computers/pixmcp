namespace PixMcp.Pix.Handles;

public sealed partial class GpuCaptureHandle
{
    /// <summary>What IPixGpuCaptureTiming reported for optional data (occupancy, highFrequencyCounters); cleared with the analysis.</summary>
    internal Dictionary<string, bool> TimingPassProbe { get; } = new(StringComparer.Ordinal);

    /// <summary>Timed leaf timelines per queue, keyed by the timing rows they were built from so a new collection rebuilds them.</summary>
    internal Dictionary<int, (EventTimingRow[] Rows, QueueTimeline Timeline)> TimelineCache { get; } = new();

    /// <summary>pix_gpu_bottleneck results per scope and evidence request; cleared with the analysis.</summary>
    internal Dictionary<string, BottleneckDto> BottleneckCache { get; } = new(StringComparer.Ordinal);
}
