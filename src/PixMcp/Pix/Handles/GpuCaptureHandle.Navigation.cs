using Microsoft.PIX;

namespace PixMcp.Pix.Handles;

public sealed record CapabilityDto(string State, string? Reason = null);
public sealed record ReplayProvenance(string Source, string PixVersion, string? Adapter,
    uint? PowerState, string? Flags, string TimingSemantics);
public sealed record OccupancyCache(IPixGpuCaptureOccupancyData Data,
    IPixGpuCaptureOccupancyType[] Types, IPixGpuCaptureOccupancyStage[] Stages);

public sealed partial class GpuCaptureHandle
{
    internal OccupancyCache? OccupancyData { get; set; }
    internal object? HighFrequencyCatalog { get; set; }
    internal Dictionary<string, object> OptionalUnavailable { get; } = new();
    private readonly Dictionary<string, CapabilityDto> _capabilities = new();

    public EventDto DescribeEvent(int queueIndex, EventRecord record) => record.ToDto(queueIndex) with
    {
        EventRef = new EventRef(Id, queueIndex, record.Index),
        MarkerPath = EventNavigation.MarkerPath(AllEvents(queueIndex), record.Index),
    };

    public void MarkCapability(string feature, string state, string? reason = null)
        => _capabilities[feature] = new(state, reason);

    public IReadOnlyDictionary<string, CapabilityDto> CapabilitiesSnapshot()
    {
        var result = new Dictionary<string, CapabilityDto>(_capabilities)
        {
            ["events"] = new("supported"),
            ["resourceMetadata"] = new("supported"),
            ["timing"] = new(Timing is null ? "notCollected" : "supported"),
            ["counters"] = new(CounterList is null ? "unknown" : "supported"),
            ["resourceContents"] = new("unsupported", "The installed API has no general resource readback interface."),
            ["pixelHistory"] = new("unsupported", "Not exposed by the installed API."),
            ["captureShaderStepping"] = new("unsupported", "Not exposed by the installed API."),
            ["preview"] = new(File.Exists(System.IO.Path.Combine(PixDiscovery.InstallDir ?? "", "pixtool.exe"))
                ? "supported" : "unsupported", "RTV/depth through pixtool; native analyses must be stopped first."),
            ["exactEventPreview"] = new("unsupported", "Native event to pixtool Global ID mapping has not been verified."),
        };
        foreach (string feature in new[] { "occupancy", "highFrequencyCounters", "shaderProfiling", "accessedResources" })
            result.TryAdd(feature, new("unknown"));
        return result;
    }

    public ReplayProvenance Provenance() => new("gpuReplay", PixDiscovery.Version ?? "unknown",
        SelectedAdapter?.ToString(System.Globalization.CultureInfo.InvariantCulture), SelectedPowerState,
        SelectedFlags is null ? null : Json.EnumName(SelectedFlags.Value),
        "EOP duration is the interval between successive completion timestamps. Execution duration is TOP-to-EOP. Replay queue spans and nested event sums are not application frame latency.");
}
