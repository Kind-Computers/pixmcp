using System.ComponentModel;
using System.Globalization;
using PixMcp.Pix.Handles;
using ToolKinds = PixMcp.Tools.Tools;

namespace PixMcp.Pix;

/// <summary>Timed siblings of an event (children of the same parent, the event included) and this event's share of their inclusive time.</summary>
public sealed record SiblingStatsDto(int Count, int Timed, ulong MedianNs, ulong MaxNs, ulong SumNs,
    [property: Description("This event's inclusive EOP time as a percent of the timed siblings' summed inclusive time.")] double? ThisPercentOfSiblings);

/// <summary>EOP time per work item launched by the call.</summary>
public sealed record PerWorkItemDto(
    [property: Description("vertices, indices, threadGroups or maxCommands.")] string Kind, long Items, double NsPerItem);

/// <summary>An event's replay timing in the context of its queue, its parent and its siblings.</summary>
public sealed record EventTimingInspectionDto(
    [property: Description("measured (PIX timed the event), derivedSum or mixed (a marker summed from its children), or untimed.")] string Semantics,
    [property: Description("Inclusive EOP time; percentOfParent is the share of the parent's inclusive time and rank the 1-based rank among timed siblings.")] DurationDto? Eop,
    [property: Description("TOP start to EOP end: how long the event occupied the GPU pipeline. Null with execReason when TOP timing is unavailable.")] DurationDto? Exec,
    string? ExecReason, ulong? TopStartNs, ulong? EopStartNs, ulong? EopEndNs,
    [property: Description("1-based rank by EOP duration among the queue's timed work events (draw, dispatch, executeIndirect); null for other kinds.")] int? RankInQueue,
    int TimedWorkEvents,
    [property: Description("1-based rank by inclusive EOP time among timed siblings.")] int? RankInParent,
    SiblingStatsDto? Siblings,
    [property: Description("True when this work event started before the previous timed work event on the queue finished (GPU pipelining).")] bool? PipelinedWithPrevious,
    [property: Description("This event's start (TOP, else EOP) minus the previous timed work event's EOP end; negative when they overlap.")] long? PreviousGapNs,
    EventRef? PreviousWork, PerWorkItemDto? PerWorkItem, DenominatorsDto Denominators);

/// <summary>A render target or depth-stencil view bound at the event, with the pixels it covers.</summary>
public sealed record EventTargetDto(
    [property: Description("renderTarget or depthStencil.")] string ViewType, ResourceRef? ResourceRef, string? Name,
    [property: Description("View format, or the resource format when the view does not name one.")] string? Format,
    ulong Width, uint Height, uint SampleCount, uint MipSlice,
    [property: Description("Width and height at the view's mip slice times the sample count.")] ulong PixelCount,
    [property: Description("The event's inclusive EOP nanoseconds per megapixel of this target; null without timing.")] double? NsPerMegapixel);

/// <summary>Targets bound at an event. state: available, none, notApplicable (markers and labels) or unavailable.</summary>
public sealed record EventTargetsDto(string State, IReadOnlyList<EventTargetDto> Targets, string? Reason = null,
    [property: Description("boundViews (the pipeline's bound views) or accessedResources (the view collection augmented with accessed resources).")] string? Source = null)
{
    public IReadOnlyList<ToolCallDto> NextCalls { get; init; } = [];
}

/// <summary>One cached counter value of the inspected event.</summary>
public sealed record EventCounterValueDto(uint Id, string Name, object? Value, string Unit, string UnitConfidence,
    [property: Description("Value divided by the event's inclusive EOP milliseconds; null for percent, ratio, boolean, bitmask and rate units or without timing.")] double? PerMs);

/// <summary>The event's row in one cached counter collection.</summary>
public sealed record EventCounterSetDto(string CollectionKey, string Source,
    [property: Description("marker: PIX's own measurement over the marker's span (a separate playback round, never a sum of child rows); event: the event's own row.")] string RowKind,
    bool HasData, IReadOnlyList<EventCounterValueDto> Values, int OmittedCounters);

/// <summary>Counter values of the event from collections already cached on the handle; inspection never collects counters. state: available or notCollected.</summary>
public sealed record EventCountersDto(string State, IReadOnlyList<EventCounterSetDto> Sets, string? Reason = null, int OmittedSets = 0)
{
    public IReadOnlyList<ToolCallDto> NextCalls { get; init; } = [];
}

/// <summary>A section event inspection does not fill for one event, with the tool that does.</summary>
public sealed record InspectionSectionStateDto(string State, string Reason)
{
    public IReadOnlyList<ToolCallDto> NextCalls { get; init; } = [];
}

internal sealed record EventInspectionInput(string Handle, int QueueIndex, EventRecord[] Events, int[] ChildCounts, IReadOnlyList<EventTimingRow> Rows, TimingTreeResult Tree, ApiCallDto? Call);

internal sealed record CounterSetSnapshot(string Key, string Source, CounterInfo[] Counters, object?[]? Values, bool HasData);

/// <summary>Pure analysis behind pix_gpu_inspect_event: timing in context, target costs and cached counter values.</summary>
internal static class EventInspection
{
    public const int MaxCounterSets = 4, MaxCountersPerSet = 50;

    public static bool IsWork(string kind) => kind is "draw" or "dispatch" or "executeIndirect";

    public static string[] Kinds(EventRecord[] events, int[] childCounts)
        => events.Select((e, i) => ToolKinds.Classify(e, i < childCounts.Length && childCounts[i] > 0)).ToArray();

    private static ulong StartOf(EventTimingRow row)
        => row.TopStart != GpuCaptureHandle.TimingNone && row.TopStart <= row.EopStart + row.EopDuration ? row.TopStart : row.EopStart;

    public static EventTimingInspectionDto Timing(EventInspectionInput input, uint eventIndex)
    {
        EventRecord[] events = input.Events;
        string[] kinds = Kinds(events, input.ChildCounts);
        TimingTreeNode[] nodes = input.Tree.Nodes;
        QueueTotals totals = input.Tree.Totals;
        TimingTreeNode node = nodes[eventIndex];
        var rows = new Dictionary<uint, EventTimingRow>();
        foreach (EventTimingRow r in input.Rows)
            if (r.Index < events.Length && r.EopStart != GpuCaptureHandle.TimingNone && r.EopDuration != GpuCaptureHandle.TimingNone) rows.TryAdd(r.Index, r);
        rows.TryGetValue(eventIndex, out EventTimingRow? row);

        uint? ParentOf(TimingTreeNode n) => n.ParentIndex is uint p && p < nodes.Length && p != n.Index ? p : null;
        uint? parent = ParentOf(node);
        ulong? parentInclusive = parent is uint pi && nodes[pi].IsTimed ? nodes[pi].InclusiveEopNs : null;
        ulong? inclusive = node.IsTimed ? node.InclusiveEopNs : null;
        ulong? eopEnd = row is null ? null : row.EopStart + row.EopDuration;
        ulong? topStart = row is not null && row.TopStart != GpuCaptureHandle.TimingNone ? row.TopStart : null;
        ulong? exec = topStart is ulong top && eopEnd is ulong end && end >= top ? end - top : null;
        string? execReason = exec is not null ? null
            : row is not null ? "TOP timing is unavailable for this event."
            : node.IsTimed ? "Derived from children: a marker has no TOP timing of its own."
            : "No replay timing row exists for this event.";

        List<EventTimingRow> timedWork = rows.Values.Where(r => IsWork(kinds[r.Index])).OrderByDescending(r => r.EopDuration).ThenBy(r => r.Index).ToList();
        int? rankInQueue = row is not null && IsWork(kinds[eventIndex]) ? timedWork.FindIndex(r => r.Index == eventIndex) + 1 : null;

        TimingTreeNode[] siblings = nodes.Where(n => ParentOf(n) == parent).ToArray();
        List<TimingTreeNode> timedSiblings = siblings.Where(n => n.IsTimed).OrderByDescending(n => n.InclusiveEopNs).ThenBy(n => n.Index).ToList();
        int? rankInParent = node.IsTimed ? timedSiblings.FindIndex(n => n.Index == eventIndex) + 1 : null;
        SiblingStatsDto? siblingStats = null;
        if (timedSiblings.Count > 0)
        {
            ulong[] sorted = timedSiblings.Select(n => n.InclusiveEopNs).Order().ToArray();
            ulong sum = 0;
            foreach (ulong value in sorted) sum += value;
            siblingStats = new(siblings.Length, sorted.Length, sorted[(50 * sorted.Length + 99) / 100 - 1], sorted[^1], sum,
                inclusive is ulong mine ? Metrics.Percent(mine, sum) : null);
        }

        EventTimingRow? previous = null;
        long? gap = null;
        if (row is not null && IsWork(kinds[eventIndex]))
        {
            ulong start = StartOf(row);
            previous = timedWork
                .Where(r => r.Index != eventIndex && (StartOf(r) < start || StartOf(r) == start && r.Index < eventIndex) && !EventNavigation.IsWithin(events, eventIndex, r.Index))
                .OrderByDescending(StartOf).ThenByDescending(r => r.Index).FirstOrDefault();
            if (previous is not null) gap = (long)start - (long)(previous.EopStart + previous.EopDuration);
        }

        PerWorkItemDto? perItem = input.Call is { WorkItems: > 0 and < long.MaxValue, WorkItemKind: { } itemKind } call && row is not null
            ? new(itemKind, call.WorkItems.Value, Math.Round((double)row.EopDuration / call.WorkItems.Value, 1)) : null;

        return new EventTimingInspectionDto(node.Semantics,
            inclusive is ulong ns ? Metrics.Duration(ns, totals, parentInclusive, rankInParent) : null,
            exec is ulong executed ? Metrics.Duration(executed, totals) : null, execReason,
            topStart, row?.EopStart, eopEnd, rankInQueue, timedWork.Count, rankInParent, siblingStats,
            gap is long g ? g < 0 : null, gap, previous is null ? null : new EventRef(input.Handle, input.QueueIndex, previous.Index), perItem, Metrics.Denominators);
    }

    /// <summary>The target with its EOP nanoseconds per megapixel when the event is timed.</summary>
    public static EventTargetDto WithCost(EventTargetDto target, DurationDto? eop)
        => target with { NsPerMegapixel = eop is null || target.PixelCount == 0 ? null : Math.Round(eop.Ns / (target.PixelCount / 1e6), 1) };

    /// <summary>Pixels at the mip slice times samples; 0 when the size is unknown.</summary>
    public static ulong PixelCount(ulong width, uint height, uint sampleCount, uint mipSlice)
    {
        if (width == 0 || height == 0) return 0;
        int shift = (int)Math.Min(mipSlice, 31u);
        ulong w = Math.Max(1UL, width >> shift), h = Math.Max(1UL, (ulong)height >> shift);
        return w * h * Math.Max(1U, sampleCount);
    }

    public static EventCountersDto Counters(EventRef eventRef, IReadOnlyList<CounterSetSnapshot> sets, int cachedSets, string rowKind, DurationDto? eop)
    {
        string handle = eventRef.Handle;
        if (cachedSets == 0)
            return new EventCountersDto("notCollected", [], "No counter collection is cached for this capture. Collect one (a preset or counter ids), then inspect again; inspection never collects counters itself.")
            {
                NextCalls = [new("pix_gpu_counters_list", new { handle, preset = "utilization" }), new("pix_gpu_counters_prepare", new { handle, preset = "utilization" })],
            };
        EventCounterSetDto[] dtos = sets.Select(set => new EventCounterSetDto(set.Key, set.Source, rowKind, set.HasData,
            set.Counters.Take(MaxCountersPerSet).Select((counter, column) =>
            {
                object? value = set.Values is not null && column < set.Values.Length ? set.Values[column] : null;
                UnitGuess unit = counter.Unit;
                return new EventCounterValueDto(counter.Id, counter.Name, value, unit.Unit, unit.UnitConfidence, PerMs(value, unit.Unit, eop));
            }).ToArray(), Math.Max(0, set.Counters.Length - MaxCountersPerSet))).ToArray();
        return new EventCountersDto("available", dtos,
            rowKind == "marker" ? "Marker rows are PIX's own measurement over the marker's span, collected in a separate playback round; never add child rows to them." : null,
            Math.Max(0, cachedSets - sets.Count));
    }

    private static double? PerMs(object? value, string unit, DurationDto? eop)
    {
        if (eop is not { Ms: > 0 } || unit.Contains("percent", StringComparison.OrdinalIgnoreCase) || unit.Contains("PerSecond", StringComparison.OrdinalIgnoreCase)
            || unit is "boolean" or "bitmask" or "ratio") return null;
        double? number = value switch
        {
            float f => f,
            double d => d,
            Half half => (double)half,
            _ => CounterQuery.Number(value) is decimal m ? (double)m : null,
        };
        return number is double n && double.IsFinite(n) ? Math.Round(n / eop.Ms, 3) : null;
    }
}

/// <summary>Rules that point out what is unusual about one inspected event. Thresholds are named constants.</summary>
internal static class InspectionHints
{
    public const int MaxHints = 6;
    public const long TinyDispatchThreadGroups = 64;
    public const ulong PipelineLatencyMinNs = 100_000, LargeTargetPixels = 1920UL * 1080;
    public const double PipelineLatencyRatio = 4, DominantSiblingPercent = 50;

    public static IReadOnlyList<InsightDto> Evaluate(EventRef eventRef, string kind, uint? parentIndex, EventTimingInspectionDto? timing, ApiCallDto? call, EventTargetsDto? targets)
    {
        var hints = new List<InsightDto>();
        string handle = eventRef.Handle;
        void Add(string id, string severity, string summary, Dictionary<string, object?> evidence, string implication, params ToolCallDto[] calls)
            => hints.Add(new InsightDto(id, severity, summary, evidence, implication, calls.Where(ToolRegistry.Accepts).ToArray()));

        if (call is { WorkItems: 0, WorkItemKind: { } zeroKind })
            Add("zero_work", "warning", $"{call.Api} launches no {zeroKind}.", new() { ["workItemKind"] = zeroKind, ["workItems"] = 0L },
                "The call does no GPU work but still costs recording, validation and state setup; remove it or check how its arguments are computed.",
                new ToolCallDto("pix_gpu_pipeline_state", new { eventRef }));

        if (call is { WorkItemKind: "threadGroups", WorkItems: > 0 and < TinyDispatchThreadGroups } dispatch)
            Add("tiny_dispatch", "info", $"{dispatch.Api} launches only {dispatch.WorkItems} thread group(s).",
                new() { ["threadGroups"] = dispatch.WorkItems, ["eopMs"] = timing?.Eop?.Ms, ["nsPerThreadGroup"] = timing?.PerWorkItem?.NsPerItem },
                "Fixed per-dispatch overhead dominates small dispatches; merge several into one or give each thread group more work.",
                new ToolCallDto("pix_gpu_timing_events", new { handle, queueIndex = eventRef.QueueIndex, kind = "dispatch", sortBy = "eopDuration" }));

        if (timing is { Exec: { } exec, Eop: { } eop } && exec.Ns >= eop.Ns + PipelineLatencyMinNs && exec.Ns >= eop.Ns * PipelineLatencyRatio)
            Add("pipeline_latency", "info", $"The event occupied the GPU pipeline for {exec.Ms} ms (TOP start to EOP end) while its EOP time is {eop.Ms} ms.",
                new() { ["execMs"] = exec.Ms, ["eopMs"] = eop.Ms, ["topStartNs"] = timing.TopStartNs, ["eopStartNs"] = timing.EopStartNs },
                "Most of that window is the event waiting behind earlier work in the pipeline, not its own cost; look at the work that ran just before it.",
                new ToolCallDto("pix_gpu_timing_events", new { handle, queueIndex = eventRef.QueueIndex, sortBy = "eopStart", descending = false }));

        if (timing is { PipelinedWithPrevious: true, PreviousWork: { } previousWork, PreviousGapNs: long overlapNs })
            Add("pipelined", "info", $"The GPU started this event {(-overlapNs / 1e6).ToString("0.###", CultureInfo.InvariantCulture)} ms before the previous work event finished.",
                new() { ["previousWork"] = previousWork, ["previousGapNs"] = overlapNs },
                "Neighbouring work overlaps in the GPU pipeline, so adding up their TOP-to-EOP spans double counts; compare EOP durations instead.",
                new ToolCallDto("pix_gpu_inspect_event", new { eventRef = previousWork, sections = new[] { "timing" } }));

        if (parentIndex is uint parent && timing is { Siblings: { Count: >= 3, ThisPercentOfSiblings: >= DominantSiblingPercent } siblings })
            Add("dominant_in_parent", "info", $"This event takes {siblings.ThisPercentOfSiblings} % of the inclusive time of its {siblings.Timed} timed siblings.",
                new() { ["thisPercentOfSiblings"] = siblings.ThisPercentOfSiblings, ["siblings"] = siblings.Count, ["rankInParent"] = timing.RankInParent },
                "It dominates its parent: speeding it up matters more than any sibling.",
                new ToolCallDto("pix_gpu_timing_tree", new { handle, scope = new EventRef(handle, eventRef.QueueIndex, parent), sortBy = "inclusive" }));

        if (kind is "draw" or "executeIndirect" && targets is { State: "available" }
            && targets.Targets.Where(t => t.ViewType == "renderTarget").MaxBy(t => t.PixelCount) is { PixelCount: >= LargeTargetPixels } large)
            Add("large_rt_fill", "info", $"The draw renders into a {large.Width}x{large.Height} target ({(large.PixelCount / 1e6).ToString("0.##", CultureInfo.InvariantCulture)} MP including samples).",
                new() { ["format"] = large.Format, ["sampleCount"] = large.SampleCount, ["pixelCount"] = large.PixelCount, ["nsPerMegapixel"] = large.NsPerMegapixel },
                "Pixel shading cost scales with this resolution and sample count; check whether the pass can render at lower resolution or with fewer samples.",
                new ToolCallDto("pix_gpu_pipeline_state", new { eventRef }));

        return hints.OrderBy(h => h.Severity == "warning" ? 0 : 1).Take(MaxHints).ToArray();
    }
}
