using System.ComponentModel;
using PixMcp.Pix.Handles;

namespace PixMcp.Pix;

/// <summary>Count, mean, nearest-rank p50/p95 and maximum of recorded durations (nanoseconds, and milliseconds rounded to 3 decimals).</summary>
public sealed record RecordedStatsDto(long Count, long AvgNs, long P50Ns, long P95Ns, long MaxNs, double AvgMs, double P50Ms, double P95Ms, double MaxMs);

/// <summary>Busy, idle, span and summed length of one recorded lane's intervals, clipped to the selected window.</summary>
public sealed record RecordedLaneTotalsDto(
    [property: Description("Union of the lane's intervals clipped to the window; overlapping intervals count once.")] DurationDto Busy,
    [property: Description("Span minus busy.")] DurationDto Idle,
    [property: Description("First clipped interval start to last clipped interval end.")] DurationDto Span,
    [property: Description("Sum of clipped interval lengths; exceeds busy when intervals overlap.")] DurationDto SumOfIntervals,
    [property: Description("True when intervals overlap, so the sum exceeds busy.")] bool IntervalsOverlap,
    [property: Description("Busy as a percentage of the selected window [startNs,endNs).")] double? BusyPercentOfWindow,
    string FirstStartNs, string LastEndNs);

/// <summary>Explains every percentage in recorded-timing rollups.</summary>
public sealed record RecordedDenominatorsDto(string PercentOfQueueSpan, string PercentOfQueueSum, string BusyPercentOfWindow, string Busy)
{
    public string? PercentOfParent { get; init; }
}

/// <summary>A thread that submitted to a queue in the window, with the submit-to-GPU-begin latency of its valid submissions.</summary>
public sealed record TimingGpuThreadDto(string? ThreadRowId, uint? ProcessId, uint? ThreadId, string? Name, long Submissions, long ValidSubmissions,
    RecordedStatsDto? SubmitLatency);

/// <summary>One of the longest recorded GPU executions of a queue; pass submissionRef to pix_timing_submissions for the full row.</summary>
public sealed record TimingGpuExecutionDto(string SubmissionRef, string? ThreadRowId, string SubmitNs, string BeginNs, long DurationNs, double DurationMs, long LatencyNs);

/// <summary>Recorded GPU work of one API command queue in the window.</summary>
public sealed record TimingGpuQueueRollupDto(string QueueId, string? Name, string? Type, string? AdapterName,
    [property: Description("Submissions whose CPU submit timestamp is inside the window.")] long Submissions,
    [property: Description("Submissions with submit >= 0, begin >= submit and end > begin.")] long ValidSubmissions,
    [property: Description("Why the other submissions have no usable GPU timing: missingTimestamps, zeroDuration or inconsistentTimestamps.")] IReadOnlyDictionary<string, long> InvalidReasons,
    [property: Description("Busy/idle/span/sum over valid execution intervals clipped to the window; null when none is inside it.")] RecordedLaneTotalsDto? Totals,
    [property: Description("GPU begin minus CPU submit over valid submissions.")] RecordedStatsDto? SubmitLatency,
    [property: Description("GPU end minus GPU begin over valid submissions (unclipped).")] RecordedStatsDto? Execution,
    IReadOnlyList<TimingGpuThreadDto> TopSubmittingThreads, IReadOnlyList<TimingGpuExecutionDto> LongestExecutions);

/// <summary>A hardware (kernel) queue with GPU work ranges in the window; system-wide, not filtered by process.</summary>
public sealed record TimingHardwareQueueRollupDto(string HardwareQueueId, string? Name, string? AdapterName, long WorkRanges,
    RecordedLaneTotalsDto? Totals, long? MaxOverlapLevel);

/// <summary>VSync pacing of one monitor lane in the window (frames_vsync).</summary>
public sealed record TimingVsyncMonitorDto(string Lane, string? Monitor, long Intervals, double AvgMs, double? P50Ms, double? P95Ms, double MaxMs, double? AvgHz);

/// <summary>Whether PIX recorded links from CPU markers to GPU work (CpuGpuExecutionMap, ApiMarkerGpuWorkMap).</summary>
public sealed record TimingCausalityDto(
    [property: Description("available (links recorded), empty (tables present without rows) or unsupported (tables absent).")] string State,
    long? CpuGpuExecutionMapRows, long? ApiMarkerGpuWorkMapRows,
    [property: Description("Rows of PixGpuExecution (GPU-side PIX events).")] long? GpuMarkerRows,
    string Reason);

/// <summary>Recorded GPU work rolled up per API command queue, with hardware queues, VSync pacing and CPU-to-GPU link availability.</summary>
public sealed record TimingGpuSummaryDto(string Handle, TimingRangeDto Provenance,
    [property: Description("Process whose queues are summarized; null when every process is included.")] uint? ProcessId,
    string Selection,
    IReadOnlyList<TimingGpuQueueRollupDto> Queues,
    IReadOnlyList<TimingHardwareQueueRollupDto>? HardwareQueues,
    IReadOnlyList<TimingVsyncMonitorDto>? Vsync,
    TimingCausalityDto CpuGpuCausality,
    RecordedDenominatorsDto Denominators,
    IReadOnlyList<string> Notes,
    [property: Description("Sections this capture could not compute, with the reason.")] IReadOnlyList<string> Unavailable,
    IReadOnlyList<ToolCallDto> NextCalls);

/// <summary>A lane of recorded PIX events: a thread (CPU events) or an API command queue (GPU-side events).</summary>
public sealed record RecordedLaneDto(
    [property: Description("thread or queue.")] string Kind, string Id, string? Name, uint? ProcessId, uint? ThreadId,
    [property: Description("Busy/idle/span over top-level events clipped to the window; sumOfIntervals is the top-level sum.")] RecordedLaneTotalsDto? Totals,
    [property: Description("Events read for this lane in the window.")] long Events,
    [property: Description("Top-level occurrences.")] int Roots,
    [property: Description("Top-level occurrences deeper than the lane's shallowest level (their parent was not read).")] long Orphans,
    [property: Description("Occurrences extending outside their parent's interval.")] long MalformedNestings);

/// <summary>One marker path aggregated over its occurrences on a lane.</summary>
public sealed record RecordedTreeNodeDto(string Path, string Name,
    [property: Description("Path segments (1 = top-level event).")] int Depth,
    long Occurrences,
    [property: Description("Occurrence time clipped to the window; rank is the position among listed siblings.")] DurationDto Inclusive,
    [property: Description("Inclusive minus the children inside each occurrence.")] DurationDto Self,
    [property: Description("Children's time clipped to the window.")] long ChildSumNs,
    bool ChildrenExceedMeasured, long ChildOverflowNs,
    [property: Description("Complete (unclipped) occurrence durations.")] RecordedStatsDto OccurrenceDuration,
    [property: Description("Recorded execution summed over occurrences with a consistent PixCpuExecutionTimes row.")] long? ExecutionNs,
    [property: Description("Recorded stall summed over the same occurrences.")] long? StallNs,
    [property: Description("available, partial, unavailable, skipped, unsupported or notApplicable (GPU lanes).")] string ExecutionTiming,
    [property: Description("Distinct child paths.")] int ChildPaths,
    string FirstStartNs,
    [property: Description("Start of the longest occurrence.")] string SlowestStartNs,
    [property: Description("End of the longest occurrence.")] string SlowestEndNs,
    string Semantics = "measured");

/// <summary>Recorded PIX events of one lane nested by Level and interval and aggregated by marker path.</summary>
public sealed record RecordedMarkerTreeDto(string Handle, TimingRangeDto Provenance,
    [property: Description("cpu (PixCpuExecution) or gpuMarkers (PixGpuExecution).")] string Domain,
    RecordedLaneDto Lane, string? ParentPath, int Depth, string SortBy, long? MinSelfNs,
    [property: Description("Pre-order: each listed path is followed by its listed descendants.")] PageResult<RecordedTreeNodeDto> Nodes,
    [property: Description("True when the lane had more events than one call reads; aggregation stops at truncatedAtNs.")] bool Truncated,
    string? TruncatedAtNs,
    TimingCapabilityDto ExecutionTiming, RecordedDenominatorsDto Denominators, string Interpretation,
    IReadOnlyList<string> Notes, IReadOnlyList<ToolCallDto> NextCalls);

/// <summary>One recorded frame: its duration, the shares of it the GPU was busy and the render thread was on CPU, blocked or ready, and the matching rule.</summary>
public sealed record TimingFrameVerdictDto(int Index, string StartNs, long FrameNs, double FrameMs,
    [property: Description("Union of the process's recorded queue execution inside the frame, as % of the frame.")] double GpuBusyPercent,
    double? OnCpuPercent, double? BlockedPercent,
    [property: Description("Ready but not running: preempted, or readied while waiting for a core.")] double? ReadyPercent,
    [property: Description("Render-thread state not covered by recorded context switches.")] double UnknownPercent,
    [property: Description("Render-thread submissions whose CPU submit time falls in the frame.")] int Submissions,
    double? FirstSubmitOffsetMs, double? MaxSubmitLatencyMs, double? PresentToVsyncMs, string Verdict);

public sealed record TimingVerdictFramesDto(
    [property: Description("present, cpuMarker, vsync or submission.")] string Source, string? Detail, string Selection,
    [property: Description("Frames the source defines in the window.")] long Available, int Analyzed, bool Truncated,
    [property: Description("Start of the first frame not analyzed, when truncated.")] string? NextStartNs);

public sealed record TimingRenderThreadDto(string ThreadRowId, uint ProcessId, uint ThreadId, string? Name,
    [property: Description("Submissions of this thread with a CPU submit time in the window.")] long Submissions,
    [property: Description("explicit, mostSubmissions or mostPixEvents.")] string Selection);

public sealed record TimingVerdictSummaryDto(string DominantVerdict,
    [property: Description("Share of analyzed frames with the dominant verdict.")] double DominantPercent,
    [property: Description("high, medium or low: a heuristic from dominance, frame count and scheduling coverage.")] string Confidence,
    IReadOnlyDictionary<string, long> VerdictFrames,
    [property: Description("Duration-weighted share of all analyzed frame time.")] double? GpuBusyPercent,
    double? OnCpuPercent, double? BlockedPercent, double? ReadyPercent, double? UnknownPercent,
    RecordedStatsDto? FrameDuration,
    [property: Description("Ready event to switch-in latency of the render thread.")] RecordedStatsDto? ReadyLatency,
    string Implication);

public sealed record TimingWaitReasonDto(
    [property: Description("blocked or readyNotRunning.")] string State,
    [property: Description("Raw FromThreadWaitReason of the switch-out; null when none was recorded.")] int? Code,
    [property: Description("Probable KWAIT_REASON name; the recorder stores codes only.")] string? ProbableName,
    long Ns, double Ms,
    [property: Description("Share of all analyzed frame time.")] double? PercentOfFrameTime,
    [property: Description("Wait intervals overlapping analyzed frames (a wait spanning two frames counts in both).")] long Waits);

public sealed record TimingQueueShareDto(string QueueId, string? Name,
    [property: Description("Busy share of all analyzed frame time.")] double? BusyPercent);

public sealed record TimingVerdictCoverageDto(
    [property: Description("available when ContextSwitch was recorded; otherwise render-thread states are unknown.")] string Scheduling,
    long SwitchEvents,
    [property: Description("Switch-ins that name their ready event.")] long ReadyLinks,
    long RenderThreadSubmissions, long GpuIntervals,
    [property: Description("Share of analyzed frame time with a known render-thread state.")] double? KnownStatePercent);

public sealed record TimingVerdictRuleDto(string Verdict, string Condition);

public sealed record TimingVerdictRulesDto(bool Heuristic, IReadOnlyList<TimingVerdictRuleDto> Rules,
    [property: Description("Switch-out wait reasons treated as runnable (ready, not blocked).")] IReadOnlyList<int> RunnableWaitReasons, string Evaluation);

/// <summary>Heuristic CPU/GPU/sync-bound classification of recorded frames with the evidence, rules and follow-up calls behind it.</summary>
public sealed record TimingVerdictDto(string Handle, TimingRangeDto Provenance, uint? ProcessId,
    TimingVerdictFramesDto Frames, TimingRenderThreadDto RenderThread, TimingVerdictSummaryDto Summary,
    [property: Description("The five longest analyzed frames.")] IReadOnlyList<TimingFrameVerdictDto> WorstFrames,
    PageResult<TimingFrameVerdictDto> PerFrame,
    [property: Description("Blocked and ready-not-running time per wait reason, largest first (top 6).")] IReadOnlyList<TimingWaitReasonDto> WaitsByReason,
    IReadOnlyList<TimingQueueShareDto> GpuQueues, TimingVerdictCoverageDto Coverage,
    [property: Description("overBudget, nearBudget, withinBudget or unavailable, from the vram_budget named query.")] TimingCapabilityDto Vram,
    TimingVerdictRulesDto Rules, IReadOnlyList<string> Semantics, IReadOnlyList<ToolCallDto> NextCalls);

/// <summary>A replayed GPU marker path matched by name to a recorded marker path, with the recorded evidence around its occurrences.</summary>
public sealed record CorrelationRowDto(string GpuPath, EventRef GpuEvent,
    [property: Description("Timed GPU events carrying this path on the queue.")] int GpuOccurrences,
    ulong GpuInclusiveEopNs, double GpuInclusiveEopMs, double GpuAvgEopMs, string GpuSemantics,
    string RecordedPath,
    [property: Description("cpu (PixCpuExecution), gpuMarkers (PixGpuExecution) or mixed.")] string RecordedDomain,
    long RecordedOccurrences,
    [property: Description("Recorded occurrence time clipped to the window, summed.")] long RecordedInclusiveNs,
    [property: Description("Complete recorded occurrence durations.")] RecordedStatsDto RecordedOccurrence,
    [property: Description("Lanes with the most occurrences (up to 3).")] IReadOnlyList<string> RecordedLanes,
    [property: Description("pathMatch (full normalized path) or nameMatch (a leaf unique on both sides).")] string Method,
    [property: Description("legacyPixPrefix and/or trailingNumber when the names needed them.")] IReadOnlyList<string> Normalizations,
    [property: Description("medium for a path match without number normalization, else low; never high, because names are not identity.")] string Confidence,
    [property: Description("Submissions whose CPU submit time falls inside the recorded occurrences on their threads; null without submissions.")] long? SubmissionsInOccurrences,
    [property: Description("Blocked time of the occurrences' threads inside the occurrences; null without context switches.")] long? BlockedNs,
    [property: Description("Ready-not-running time of the occurrences' threads inside the occurrences.")] long? ReadyNs,
    [property: Description("Recorded average occurrence divided by the replay average inclusive EOP (mixes both clocks).")] double? RatioRecordedToReplay,
    IReadOnlyList<ToolCallDto> NextCalls);

public sealed record CorrelationUnmatchedDto(string Path, long Occurrences, double InclusiveMs, EventRef? GpuEvent, string? RecordedDomain);

public sealed record CorrelationQueueMapDto(int GpuQueueIndex, string GpuName, string GpuType, string? RecordedQueueId, string? RecordedName, string? RecordedType,
    [property: Description("typeAndName, type or none.")] string Method, string Confidence);

public sealed record CorrelationCountsDto(int GpuPaths, int GpuTimedMarkers, bool GpuPathsTruncated, int RecordedPaths, long RecordedEvents, bool RecordedEventsTruncated,
    int Matched, int PathMatches, int NameMatches, int UnmatchedGpu, int UnmatchedRecorded);

/// <summary>Replayed GPU marker paths joined to recorded PIX marker paths by name, with unmatched paths on both sides and a queue map.</summary>
public sealed record CorrelationDto(string GpuHandle, string TimingHandle,
    [property: Description("States that a match is a name coincidence, not identity.")] string Identity,
    ScopeDescriptionDto? GpuScope, ReplayProvenance GpuProvenance, TimingRangeDto RecordedProvenance, uint? ProcessId,
    CorrelationCountsDto Counts,
    [property: Description("Matches ordered by replayed inclusive time.")] PageResult<CorrelationRowDto> Matches,
    [property: Description("GPU paths without a match, largest first (up to 20).")] IReadOnlyList<CorrelationUnmatchedDto> UnmatchedGpu,
    [property: Description("Recorded paths without a match, largest first (up to 20).")] IReadOnlyList<CorrelationUnmatchedDto> UnmatchedRecorded,
    IReadOnlyList<CorrelationQueueMapDto> QueueMap, IReadOnlyList<string> Semantics, IReadOnlyList<string> Notes, IReadOnlyList<ToolCallDto> NextCalls);
