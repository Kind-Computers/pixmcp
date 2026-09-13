using System.ComponentModel;
using System.Text.Json;
using PixMcp.Pix.Handles;

namespace PixMcp.Pix;

/// <summary>A ranked pass or work event. Semantics says whether Eop was measured by PIX or derived from children.</summary>
public sealed record EventMetricDto(EventRef EventRef, IReadOnlyList<string> MarkerPath, string Name, string Kind,
    string Semantics, DurationDto Eop, DurationDto? Exec);
public sealed record QueueOverviewDto(int QueueIndex, string Name, string Type, uint EventCount,
    IReadOnlyDictionary<string, int> EventKinds, QueueTotals? Timing);
public sealed record CaptureOverviewDto(string Handle, IReadOnlyList<QueueOverviewDto> Queues,
    IReadOnlyDictionary<string, CapabilityDto> Capabilities, DenominatorsDto? Denominators, ReplayProvenance? Provenance,
    IReadOnlyList<EventMetricDto> TopPasses, IReadOnlyList<EventMetricDto> TopDraws,
    IReadOnlyList<ToolCallDto> NextCalls)
{
    /// <summary>What scope/markerPathPrefix resolved to; null when the overview is unrestricted.</summary>
    public ScopeDescriptionDto? Scope { get; init; }
    /// <summary>Set while the timing replay is still running: the rest of the overview is already valid.</summary>
    public PendingSectionDto? Timing { get; init; }
    /// <summary>The GPU vendor the capture was taken on (replay provenance names the replay adapter).</summary>
    public VendorIdentity? Vendor { get; init; }
}

public sealed record QueuePair(
    [property: Description("Queue index in the baseline capture.")] int BaselineQueueIndex,
    [property: Description("Queue index in the candidate capture.")] int CandidateQueueIndex);
public sealed record EventPair(
    [property: Description("Event reference in the baseline capture.")] EventRef Baseline,
    [property: Description("Event reference in the candidate capture.")] EventRef Candidate);
public sealed record ComparisonEvent(EventRef EventRef, string[] MarkerPath, string Name, string Kind,
    bool IsMarker, ulong? EopNs, string Semantics, string? ShaderKey, IReadOnlyDictionary<string, JsonElement> Sections);
public sealed record ComparisonQueue(int Index, string Name, string Type, ComparisonEvent[] Events);
public sealed record ComparisonSnapshot(string Handle, ComparisonQueue[] Queues, ReplayProvenance Provenance,
    IReadOnlyList<object> Coverage);
public sealed record FieldChange(string Section, string Path, JsonElement? Before, JsonElement? After);
public sealed record EventChange(EventRef Baseline, EventRef Candidate, string Name, IReadOnlyList<string> MarkerPath,
    string MatchMethod, ulong? BaselineEopNs, ulong? CandidateEopNs, decimal? DeltaNs, double? DeltaMs, double? DeltaPercent,
    string BaselineSemantics, string CandidateSemantics, IReadOnlyList<FieldChange> Fields);
public sealed record MatchAmbiguity(string Kind, string Key, IReadOnlyList<EventRef> Baseline, IReadOnlyList<EventRef> Candidate);
public sealed record ComparisonResultDto(string BaselineHandle, string CandidateHandle, int MatchedCount,
    IReadOnlyList<EventChange> Items, IReadOnlyList<EventRef> BaselineOnly, IReadOnlyList<EventRef> CandidateOnly,
    IReadOnlyList<MatchAmbiguity> Ambiguous, IReadOnlyList<object> Coverage,
    ReplayProvenance BaselineProvenance, ReplayProvenance CandidateProvenance);
public sealed record ComparisonSummaryDto(string BaselineHandle, string CandidateHandle, int MatchedCount,
    int ChangedCount, int BaselineOnlyCount, int CandidateOnlyCount, int AmbiguousCount,
    IReadOnlyList<EventChange> TopChanges, IReadOnlyList<MatchAmbiguity> Ambiguous,
    ReplayProvenance BaselineProvenance, ReplayProvenance CandidateProvenance,
    string FullResultRef, IReadOnlyList<ToolCallDto> NextCalls, IReadOnlyList<object> Coverage);
