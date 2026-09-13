using System.ComponentModel;
using PixMcp.Pix.Handles;

namespace PixMcp.Pix;

public sealed record IntervalDto(ulong StartNs, ulong EndNs);

/// <summary>An idle gap between consecutive timed leaf events on one queue's replay clock.</summary>
public sealed record BubbleDto(
    int QueueIndex,
    [property: Description("Gap start: the end of the latest-ending earlier leaf window.")] ulong StartNs,
    [property: Description("Gap length; percentOfQueueSpan is of this queue's span.")] DurationDto Duration,
    [property: Description("Leaf event whose window ends the busy stretch before the gap.")] EventRef Before,
    string BeforeName,
    [property: Description("Leaf event that starts after the gap.")] EventRef After,
    string AfterName,
    [property: Description("Ancestor markers of the event after the gap, joined with '/'.")] string AfterMarkerPath,
    [property: Description("present, queueWait, queueSignal, barrier, commandListBoundary or unknown: the highest-precedence cause among the events between the pair and the event after the gap; the event before the gap counts for present, queueWait and queueSignal.")] string PrimaryCause,
    [property: Description("Every cause found, in precedence order.")] IReadOnlyList<string> Causes,
    [property: Description("The event that shows the primary cause; null for unknown.")] EventRef? Evidence,
    [property: Description("A different nonzero command list id appears across the gap.")] bool CommandListChanged,
    [property: Description("Percent of the gap during which another timed queue was busy; null without another timed queue.")] double? OtherQueueBusyPercent);

public sealed record QueueBusyDto(int QueueIndex, string Name, string Type,
    [property: Description("Union of TOP-to-EOP windows of timed leaf events; percentOfQueueSpan is of this queue's span.")] DurationDto Busy,
    DurationDto Idle,
    [property: Description("Busy time during which no other timed queue was busy.")] DurationDto SoloBusy,
    ulong SpanStartNs, ulong SpanEndNs, double SpanMs,
    [property: Description("Timed leaf rows that feed busy and gaps.")] int TimedLeafEvents,
    [property: Description("True when no row carries a TOP timestamp, so every window starts at its EOP start.")] bool EopOnly,
    [property: Description("Rows whose TOP start lies after their EOP end; their windows use EOP only.")] int ClockAnomalies,
    [property: Description("Consecutive leaf windows that overlap (negative gaps).")] int OverlappedPairs,
    [property: Description("Gaps at least minGapNs long.")] int BubbleCount,
    DurationDto BubbleTotal,
    BubbleDto? LargestBubble)
{
    /// <summary>Merged busy intervals; only with includeIntervals=true.</summary>
    public IReadOnlyList<IntervalDto>? Intervals { get; init; }
}

public sealed record QueueUnavailableDto(int QueueIndex, string Name, string Type,
    [property: Description("noTimedEvents (no timed leaf rows) or noTimedEventsInWindow (none inside the scope's window).")] string Reason);

public sealed record QueuePairOverlapDto(int QueueA, int QueueB, DurationDto Overlap,
    [property: Description("Overlap as a percent of queue A's busy time.")] double? PercentOfABusy,
    [property: Description("Overlap as a percent of queue B's busy time.")] double? PercentOfBBusy,
    [property: Description("overlapping (overlap at least 50 % of the smaller busy time), mostlySerialized (at least 10 %) or serialized.")] string Verdict);

public sealed record OverlapThresholdsDto(double OverlappingPercent, double MostlySerializedPercent, ulong MinGapNs, string PairDenominator);

public sealed record QueueOverlapDto(string Handle, string Semantics, ulong CaptureStartNs, ulong CaptureEndNs, double CaptureSpanMs,
    IReadOnlyList<QueueBusyDto> Queues, IReadOnlyList<QueueUnavailableDto> Unavailable, IReadOnlyList<QueuePairOverlapDto> Pairs,
    [property: Description("Queue with the most busy time (ties: longer span, then lower index); null without timed queues.")] int? CriticalPathQueueIndex,
    [property: Description("Longest gaps of the selected queues, longest first.")] IReadOnlyList<BubbleDto> TopGaps,
    OverlapThresholdsDto Thresholds, IReadOnlyList<InsightDto> Insights, IReadOnlyList<ToolCallDto> NextCalls)
{
    public ScopeDescriptionDto? Scope { get; init; }
    /// <summary>The scope's replay-clock window every queue was clipped to; null when unrestricted.</summary>
    public IntervalDto? Window { get; init; }
    public ReplayProvenance? Provenance { get; init; }
}

public sealed record BubbleCauseDto(int QueueIndex, string Cause, int Count, DurationDto Total);

public sealed record BubblesDto(string Handle, string Semantics, ulong MinGapNs, string? Cause, string SortBy, bool Descending,
    [property: Description("Gap count and total per queue and primary cause, over every gap that passed the threshold and scope (before the cause filter).")] IReadOnlyList<BubbleCauseDto> PerCause,
    IReadOnlyList<QueueUnavailableDto> Unavailable,
    long Total, int Offset, int Count, int? NextOffset, IReadOnlyList<BubbleDto> Items, IReadOnlyList<ToolCallDto> NextCalls)
{
    public ScopeDescriptionDto? Scope { get; init; }
    public ReplayProvenance? Provenance { get; init; }
}

public sealed record OverviewQueueGapsDto(int QueueIndex, double SoloBusyMs, int BubbleCount, double BubbleMs, double? LargestBubbleMs);

/// <summary>pix_gpu_overview's cross-queue section; pix_gpu_queue_overlap has the full report.</summary>
public sealed record OverviewOverlapDto(IReadOnlyList<QueuePairOverlapDto> Pairs, int? CriticalPathQueueIndex,
    [property: Description("Timed queues with idle gaps of at least 50 us: solo-busy time and gap totals; null when no queue has such gaps or with brief=true.")] IReadOnlyList<OverviewQueueGapsDto>? Queues);
