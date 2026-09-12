using System.Text.Json;
using PixMcp.Pix.Handles;

namespace PixMcp.Pix;

public sealed record EventMetricDto(EventRef EventRef, IReadOnlyList<string> MarkerPath, string Name,
    ulong EopDurationNs, bool Derived);
public sealed record QueueOverviewDto(int QueueIndex, string Name, string Type, uint EventCount,
    IReadOnlyDictionary<string, int> EventKinds);
public sealed record CaptureOverviewDto(string Handle, IReadOnlyList<QueueOverviewDto> Queues,
    IReadOnlyDictionary<string, CapabilityDto> Capabilities, ReplayProvenance? Provenance,
    IReadOnlyList<EventMetricDto> TopPasses, IReadOnlyList<EventMetricDto> TopDraws,
    IReadOnlyList<ToolCallDto> NextCalls);

public sealed record QueuePair(int BaselineQueueIndex, int CandidateQueueIndex);
public sealed record EventPair(EventRef Baseline, EventRef Candidate);
public sealed record ComparisonEvent(EventRef EventRef, string[] MarkerPath, string Name, string Kind,
    bool IsMarker, ulong? EopNs, bool Derived, string? ShaderKey, IReadOnlyDictionary<string, JsonElement> Sections);
public sealed record ComparisonQueue(int Index, string Name, string Type, ComparisonEvent[] Events);
public sealed record ComparisonSnapshot(string Handle, ComparisonQueue[] Queues, ReplayProvenance Provenance,
    IReadOnlyList<object> Coverage);
public sealed record FieldChange(string Section, string Path, JsonElement? Before, JsonElement? After);
public sealed record EventChange(EventRef Baseline, EventRef Candidate, string Name, IReadOnlyList<string> MarkerPath,
    string MatchMethod, ulong? BaselineEopNs, ulong? CandidateEopNs, decimal? DeltaNs, double? DeltaPercent,
    bool BaselineDerived, bool CandidateDerived, IReadOnlyList<FieldChange> Fields);
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
