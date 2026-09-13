using System.ComponentModel;
using System.Text.Json;
using PixMcp.Pix.Handles;

namespace PixMcp.Pix;

/// <summary>A ranked pass: a timed marker with children, its inclusive and self time, and how much work it contains.</summary>
public sealed record OverviewPassDto(EventRef EventRef, IReadOnlyList<string> MarkerPath, string Name,
    [property: Description("measured (PIX timed the marker), derivedSum (sum of children) or mixed.")] string Semantics,
    [property: Description("Inclusive EOP time; rank is the position in topPasses.")] DurationDto Inclusive,
    [property: Description("Inclusive minus timed children; percentOfParent is the share of this pass's own inclusive time.")] DurationDto Self,
    bool ChildrenExceedMeasured, ulong ChildOverflowNs,
    [property: Description("Direct children.")] int ChildCount,
    [property: Description("Draw, dispatch and executeIndirect events in the subtree.")] int WorkCount);

/// <summary>A ranked work event (draw, dispatch or executeIndirect) with its replay timing and captured call text.</summary>
public sealed record OverviewWorkDto(EventRef EventRef, IReadOnlyList<string> MarkerPath, string Name, string Kind,
    [property: Description("End-of-pipe duration (includes idle before the event); rank is the position in topDraws.")] DurationDto Eop,
    [property: Description("TOP-to-EOP execution; null without TOP timing.")] DurationDto? Exec,
    OverviewParametersDto? Parameters);

public sealed record OverviewParametersDto([property: Description("Captured API call text.")] string Raw);

public sealed record OverviewHistogramBucketDto(string Label, ulong MinNs, ulong? MaxNs, int Count, ulong SumNs, double SumMs,
    [property: Description("SumNs as a percentage of the histogram denominator.")] double? PercentOfDenominator);

/// <summary>Selected work events by EOP duration: the non-empty 1-2-5 buckets (edges 10 us to 10 ms, plus overflow), in edge order.</summary>
public sealed record OverviewHistogramDto(string Denominator, ulong DenominatorNs, IReadOnlyList<OverviewHistogramBucketDto> Buckets);

public sealed record OverviewFrameDto(int Index,
    [property: Description("First event index of the frame on the presenting queue.")] uint FirstEventIndex, uint LastEventIndex, uint? PresentEventIndex,
    [property: Description("True for events after the last Present.")] bool Partial,
    int WorkEvents,
    [property: Description("Union of TOP..EOP windows of the frame's timed work events across queues.")] ulong BusyNs, double BusyMs,
    [property: Description("Replay-clock window between Present completions; null without timing and for the partial frame.")] ulong? WindowNs);

public sealed record OverviewFramePercentilesDto(double P50BusyMs, double? P95BusyMs, double? P99BusyMs, double MaxBusyMs, string Method = "nearestRank; p95 and p99 need 5 frames");

public sealed record OverviewFramesDto(int Total, bool Truncated, IReadOnlyList<OverviewFrameDto> PerFrame, OverviewFramePercentilesDto Percentiles);

public sealed record OverviewFrameInfoDto(int Count, int? PresentQueueIndex,
    [property: Description("present (other queues by replay-clock windows), indexOnly (only the presenting queue is split) or none (no Present).")] string FrameAssignment,
    int? SelectedFrame);

public sealed record OverviewCaptureDto(string Path, long EventTotal, int QueueCount,
    [property: Description("The GPU vendor the capture was taken on (provenance names the replay adapter).")] VendorIdentity Adapter,
    OverviewFrameInfoDto Frames, string Semantics);

public sealed record QueueOverviewDto(int QueueIndex, string Name, string Type, uint EventCount,
    [property: Description("Event counts per kind; work = draw + dispatch + executeIndirect. Empty while the queue's events are not cached.")] IReadOnlyDictionary<string, int> Kinds,
    [property: Description("Replay totals (busy, span, idle); null before timing is collected.")] QueueTotals? Totals);

public sealed record CaptureOverviewDto(string Handle, OverviewCaptureDto Capture, IReadOnlyList<QueueOverviewDto> Queues,
    IReadOnlyDictionary<string, CapabilityDto> Capabilities, DenominatorsDto? Denominators, ReplayProvenance? Provenance,
    IReadOnlyList<OverviewPassDto> TopPasses, IReadOnlyList<OverviewWorkDto> TopDraws,
    OverviewHistogramDto? Histogram,
    [property: Description("Per-frame busy time; present only when the capture has more than one frame.")] OverviewFramesDto? Frames,
    IReadOnlyList<ToolCallDto> NextCalls)
{
    /// <summary>What scope/markerPathPrefix resolved to; null when the overview is unrestricted.</summary>
    public ScopeDescriptionDto? Scope { get; init; }
    /// <summary>Set while the timing replay is still running: the rest of the overview is already valid.</summary>
    public PendingSectionDto? Timing { get; init; }
    /// <summary>Up to eight findings, warnings first; null without timing or with includeInsights=false.</summary>
    public IReadOnlyList<InsightDto>? Insights { get; init; }
    /// <summary>Cross-queue overlap and idle gaps over the whole capture; null before timing, with a scope or frame, or with fewer than two timed queues.</summary>
    public OverviewOverlapDto? Overlap { get; init; }
}

public sealed record QueuePair(
    [property: Description("Queue index in the baseline capture.")] int BaselineQueueIndex,
    [property: Description("Queue index in the candidate capture.")] int CandidateQueueIndex);
public sealed record EventPair(
    [property: Description("Event reference in the baseline capture.")] EventRef Baseline,
    [property: Description("Event reference in the candidate capture.")] EventRef Candidate);
public sealed record ComparisonEvent(EventRef EventRef, string[] MarkerPath, string Name, string Kind,
    bool IsMarker, ulong? EopNs, string Semantics, string? ShaderKey, IReadOnlyDictionary<string, JsonElement> Sections)
{
    /// <summary>Bound shaders with hashes, for code diffs.</summary>
    public IReadOnlyList<ComparisonShader>? Shaders { get; init; }
    /// <summary>Max minus min EOP over repeated collections; EopNs is then their median.</summary>
    public ulong? SpreadNs { get; init; }
    public int Samples { get; init; }
}
public sealed record ComparisonShader(string Stage, string Hash, int Index);
public sealed record ComparisonQueue(int Index, string Name, string Type, ComparisonEvent[] Events)
{
    /// <summary>Union of TOP-to-EOP windows of the timed leaf events inside the compared selection.</summary>
    public ulong? BusyNs { get; init; }
}
public sealed record ComparisonSnapshot(string Handle, ComparisonQueue[] Queues, ReplayProvenance Provenance,
    IReadOnlyList<object> Coverage)
{
    /// <summary>HLSL of the bound shaders by hash, captured before the analysis could stop.</summary>
    public IReadOnlyDictionary<string, string>? HlslByHash { get; init; }
}
public sealed record FieldChange(string Section, string Path, JsonElement? Before, JsonElement? After);
public sealed record EventChange(EventRef Baseline, EventRef Candidate, string Name, IReadOnlyList<string> MarkerPath,
    string MatchMethod, ulong? BaselineEopNs, ulong? CandidateEopNs, decimal? DeltaNs, double? DeltaMs, double? DeltaPercent,
    string BaselineSemantics, string CandidateSemantics, IReadOnlyList<FieldChange> Fields)
{
    public string? Kind { get; init; }
    /// <summary>high (explicit pair or unique path), medium (unique shader within a path) or low (order within a path).</summary>
    public string? Confidence { get; init; }
    public ulong? BaselineSpreadNs { get; init; }
    public ulong? CandidateSpreadNs { get; init; }
    /// <summary>The delta is within the larger side's spread over repeated collections.</summary>
    public bool BelowNoiseFloor { get; init; }
}
public sealed record MatchAmbiguity(string Kind, string Key, IReadOnlyList<EventRef> Baseline, IReadOnlyList<EventRef> Candidate);
public sealed record ComparisonResultDto(string BaselineHandle, string CandidateHandle, int MatchedCount,
    IReadOnlyList<EventChange> Items, IReadOnlyList<EventRef> BaselineOnly, IReadOnlyList<EventRef> CandidateOnly,
    IReadOnlyList<MatchAmbiguity> Ambiguous, IReadOnlyList<object> Coverage,
    ReplayProvenance BaselineProvenance, ReplayProvenance CandidateProvenance)
{
    public ComparisonTotalsDto? Totals { get; init; }
    /// <summary>Every marker-path rollup row, largest delta first.</summary>
    public IReadOnlyList<MarkerPathDeltaDto>? ByMarkerPath { get; init; }
    public ComparisonNoiseDto? Noise { get; init; }
    public IReadOnlyList<ComparisonWarningDto>? Warnings { get; init; }
    public bool ProvenanceMismatch { get; init; }
    public IReadOnlyList<ShaderCodeDiffDto>? CodeDiffs { get; init; }
    public IReadOnlyList<QueuePair>? QueueMatches { get; init; }
    public ComparisonOptionsDto? Options { get; init; }
}
public sealed record ComparisonSummaryDto(string BaselineHandle, string CandidateHandle, int MatchedCount,
    int ChangedCount, int BaselineOnlyCount, int CandidateOnlyCount, int AmbiguousCount,
    IReadOnlyList<EventChange> TopChanges, IReadOnlyList<MatchAmbiguity> Ambiguous,
    ReplayProvenance BaselineProvenance, ReplayProvenance CandidateProvenance,
    string FullResultRef, IReadOnlyList<ToolCallDto> NextCalls, IReadOnlyList<object> Coverage)
{
    public ComparisonTotalsDto? Totals { get; init; }
    /// <summary>The largest marker-path rows without an ancestor or descendant of a listed row; the full list is at /byMarkerPath.</summary>
    public IReadOnlyList<MarkerPathDeltaDto>? ByMarkerPath { get; init; }
    public ComparisonNoiseDto? Noise { get; init; }
    public IReadOnlyList<ComparisonWarningDto>? Warnings { get; init; }
    public bool ProvenanceMismatch { get; init; }
    /// <summary>The first shader code diffs; the full list is at /codeDiffs.</summary>
    public IReadOnlyList<ShaderCodeDiffDto>? CodeDiffs { get; init; }
    public ComparisonOptionsDto? Options { get; init; }
}
