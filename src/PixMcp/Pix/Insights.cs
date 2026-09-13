namespace PixMcp.Pix;

/// <summary>A finding with the numbers behind it, what it implies, and the calls that dig further.</summary>
public sealed record InsightDto(string Id, string Severity, string Summary, IReadOnlyDictionary<string, object?> Evidence, string Implication,
    IReadOnlyList<ToolCallDto> NextCalls);

/// <summary>Facts the insight rules need beyond the overview itself, computed once by the builder over the selected scope and frame.</summary>
internal sealed record OverviewInsightFacts(int WorkEvents, int TimedWorkEvents, ulong WorkEopNs, ulong ExecuteIndirectEopNs, IReadOnlyList<ulong> WorkEopSorted,
    int BarrierEvents, int MarkerEvents, bool ExperimentsLoaded);

/// <summary>
/// Rules that turn a GPU overview into up to eight findings, warnings first. Every threshold is a named constant and every
/// suggested call is filtered through <see cref="ToolRegistry"/>.
/// </summary>
internal static class InsightRules
{
    public const int MaxInsights = 8;
    public const double IdlePercent = 50, DominantPassPercent = 50, LongTailRatio = 5, ExecuteIndirectShare = 50, SelfShare = 50, SelfPassMinSpanPercent = 10,
        FrameVarianceRatio = 1.5;
    public const int LongTailMinEvents = 10, BarrierMin = 20, FrameVarianceMinFrames = 5;
    /// <summary>Async work shorter than this on either queue cannot shorten a frame noticeably, so serialised replay is not worth a finding.</summary>
    public const ulong AsyncOverlapMinBusyNs = 500_000;

    public static IReadOnlyList<InsightDto> Evaluate(CaptureOverviewDto overview, OverviewInsightFacts facts)
    {
        var found = new List<InsightDto>();
        string handle = overview.Handle;
        void Add(string id, string severity, string summary, Dictionary<string, object?> evidence, string implication, params ToolCallDto[] calls)
            => found.Add(new(id, severity, summary, evidence, implication, calls.Where(ToolRegistry.Accepts).ToArray()));

        if (overview.Provenance is { VendorMismatch: true } provenance)
            Add("vendor_mismatch", "warning", $"The capture was taken on {provenance.CaptureVendor} hardware but replays on {provenance.Vendor}.",
                new() { ["captureVendor"] = provenance.CaptureVendor, ["replayVendor"] = provenance.Vendor, ["adapterName"] = provenance.AdapterName },
                "Replay timing describes the replay GPU, not the one the capture came from; vendor-specific costs differ.",
                new ToolCallDto("pix_gpu_analysis_adapters", new { handle }));

        if (overview.TopPasses.Where(p => p.ChildrenExceedMeasured).MaxBy(p => p.ChildOverflowNs) is { } overflowing)
            Add("children_exceed_measured", "warning", $"Children of {overflowing.Name} add up to {Metrics.Ms(overflowing.ChildOverflowNs)} ms more than PIX measured for it.",
                new() { ["eventRef"] = overflowing.EventRef, ["childOverflowMs"] = Metrics.Ms(overflowing.ChildOverflowNs), ["inclusiveMs"] = overflowing.Inclusive.Ms },
                "Child events overlap or run past their marker on the replay clock, so child sums overstate the pass; read the children in start order.",
                new ToolCallDto("pix_gpu_timing_events", new { handle, scope = overflowing.EventRef, sortBy = "eopStart", descending = false }));

        int untimed = facts.WorkEvents - facts.TimedWorkEvents;
        if (overview.Denominators is not null && untimed > 0)
            Add("untimed_events", "warning", $"{untimed} of {facts.WorkEvents} work events carry no replay timing.",
                new() { ["untimedWorkEvents"] = untimed, ["workEvents"] = facts.WorkEvents },
                "Totals, ranks and the histogram leave these events out.",
                new ToolCallDto("pix_gpu_events", new { handle, kind = "work", scope = overview.Scope?.Root, markerPathPrefix = overview.Scope?.MarkerPathPrefix }));

        if (overview.Frames is { PerFrame.Count: >= FrameVarianceMinFrames } frames && frames.Percentiles.P95BusyMs is double p95
            && frames.Percentiles.P50BusyMs > 0 && p95 >= FrameVarianceRatio * frames.Percentiles.P50BusyMs)
            Add("frame_variance_high", "warning", $"Frame busy time p95 is {p95} ms against a p50 of {frames.Percentiles.P50BusyMs} ms.",
                new() { ["frames"] = frames.Total, ["p50BusyMs"] = frames.Percentiles.P50BusyMs, ["p95BusyMs"] = p95, ["maxBusyMs"] = frames.Percentiles.MaxBusyMs },
                "A few frames cost much more than a typical one; compare the slowest frame with a typical frame.",
                new ToolCallDto("pix_gpu_overview", new { handle, frameIndex = frames.PerFrame.MaxBy(f => f.BusyNs)!.Index }));

        if (overview.Queues.Where(q => q.Totals is { SpanNs: > 0, TimedEvents: >= 2 } && q.Totals.IdlePercent >= IdlePercent).MaxBy(q => q.Totals!.IdlePercent) is { } idle)
            Add("queue_idle_high", "info", $"{idle.Name} was idle for {idle.Totals!.IdlePercent} % of its replay span.",
                new() { ["queueIndex"] = idle.QueueIndex, ["idlePercent"] = idle.Totals.IdlePercent, ["busyMs"] = idle.Totals.BusyMs, ["spanMs"] = idle.Totals.SpanMs },
                "Gaps between command lists, barriers or synchronization fill this queue's replay span rather than GPU work.",
                new ToolCallDto("pix_gpu_timing_events", new { handle, queueIndex = idle.QueueIndex, sortBy = "eopStart", descending = false }));

        foreach (QueuePairOverlapDto pair in overview.Overlap?.Pairs.Where(p => p.Verdict == "serialized") ?? [])
        {
            QueueOverviewDto? a = overview.Queues.FirstOrDefault(q => q.QueueIndex == pair.QueueA), b = overview.Queues.FirstOrDefault(q => q.QueueIndex == pair.QueueB);
            if (a?.Totals is not { } aTotals || b?.Totals is not { } bTotals || aTotals.BusyNs < AsyncOverlapMinBusyNs || bTotals.BusyNs < AsyncOverlapMinBusyNs) continue;
            QueueOverviewDto? asyncQueue = a.Type is "COMPUTE" or "COPY" ? a : b.Type is "COMPUTE" or "COPY" ? b : null;
            if (asyncQueue is null) continue;
            QueueOverviewDto other = ReferenceEquals(asyncQueue, a) ? b : a;
            Add("async_not_overlapping", "info", $"{asyncQueue.Name} and {other.Name} overlapped for {pair.Overlap.Ms} ms on replay.",
                new() { ["queueA"] = pair.QueueA, ["queueB"] = pair.QueueB, ["overlapMs"] = pair.Overlap.Ms },
                "On replay the async work did not overlap the other queue, so it could not shorten it; replay may serialise queues itself.",
                new ToolCallDto("pix_gpu_queue_overlap", new { handle }));
            break;
        }

        if (overview.TopPasses.Where(p => p.MarkerPath.Count > 0 && p.Inclusive.PercentOfQueueSpan >= DominantPassPercent).MaxBy(p => p.Inclusive.Ns) is { } dominant)
        {
            var calls = new List<ToolCallDto> { new("pix_gpu_timing_tree", new { handle, scope = dominant.EventRef, sortBy = "self" }) };
            if (facts.ExperimentsLoaded) calls.Add(new("pix_gpu_drpix_run", new { handle, scope = dominant.EventRef }));
            Add("single_pass_dominates", "info", $"{dominant.Name} takes {dominant.Inclusive.PercentOfQueueSpan} % of its queue's replay span.",
                new() { ["eventRef"] = dominant.EventRef, ["inclusiveMs"] = dominant.Inclusive.Ms, ["percentOfQueueSpan"] = dominant.Inclusive.PercentOfQueueSpan },
                "Optimizing other passes cannot change this queue's time much; look inside this pass first.", calls.ToArray());
        }

        if (overview.TopPasses.Where(p => p.ChildCount > 0 && p.Inclusive.Ns > 0 && (double)p.Self.Ns * 100 >= (double)p.Inclusive.Ns * SelfShare
            && p.Inclusive.PercentOfQueueSpan >= SelfPassMinSpanPercent).MaxBy(p => p.Self.Ns) is { } heavy)
            Add("pass_self_time_high", "info", $"{heavy.Self.Ms} ms of {heavy.Name}'s {heavy.Inclusive.Ms} ms is not in its timed children.",
                new() { ["eventRef"] = heavy.EventRef, ["selfMs"] = heavy.Self.Ms, ["inclusiveMs"] = heavy.Inclusive.Ms },
                "Most of the pass's time is untimed work, state changes or idle between its children rather than its draws.",
                new ToolCallDto("pix_gpu_timing_tree", new { handle, scope = heavy.EventRef, sortBy = "self" }));

        int n = facts.WorkEopSorted.Count;
        if (n >= LongTailMinEvents)
        {
            ulong median = facts.WorkEopSorted[(50 * n + 99) / 100 - 1], slowest = facts.WorkEopSorted[^1];
            if (median > 0 && slowest >= LongTailRatio * median)
                Add("long_tail_draws", "info", $"The slowest work event takes {Metrics.Ms(slowest)} ms, {Math.Round((double)slowest / median, 1)}x the median.",
                    new() { ["workEvents"] = n, ["medianMs"] = Metrics.Ms(median), ["maxMs"] = Metrics.Ms(slowest) },
                    "A few draws or dispatches dominate; inspect them before tuning the typical event.",
                    new ToolCallDto("pix_gpu_timing_events", new { handle, kind = "work", sortBy = "eopDuration" }));
        }

        if (facts.MarkerEvents == 0 && facts.WorkEvents > 0)
            Add("no_markers", "info", "The capture has no PIX markers, so no passes can be ranked.",
                new() { ["workEvents"] = facts.WorkEvents },
                "Wrap render passes in PIXBeginEvent and PIXEndEvent to get per-pass timing and scoping.",
                new ToolCallDto("pix_gpu_events", new { handle, kind = "work" }));

        if (facts.WorkEopNs > 0 && facts.ExecuteIndirectEopNs * 100.0 / facts.WorkEopNs >= ExecuteIndirectShare)
            Add("executeindirect_heavy", "info", $"ExecuteIndirect accounts for {Math.Round(100.0 * facts.ExecuteIndirectEopNs / facts.WorkEopNs, 1)} % of timed work.",
                new() { ["executeIndirectMs"] = Metrics.Ms(facts.ExecuteIndirectEopNs), ["workMs"] = Metrics.Ms(facts.WorkEopNs) },
                "GPU-driven submission dominates; argument buffers and command signatures decide the cost.",
                new ToolCallDto("pix_gpu_timing_events", new { handle, kind = "executeIndirect", sortBy = "eopDuration" }));

        if (facts.BarrierEvents >= BarrierMin && facts.BarrierEvents >= 2 * Math.Max(1, facts.WorkEvents))
            Add("many_barriers", "info", $"{facts.BarrierEvents} resource barriers for {facts.WorkEvents} work events.",
                new() { ["barrierEvents"] = facts.BarrierEvents, ["workEvents"] = facts.WorkEvents },
                "Frequent state transitions add driver overhead and can serialize GPU work.",
                new ToolCallDto("pix_gpu_events", new { handle, kind = "barrier" }));

        return found.OrderBy(i => i.Severity == "warning" ? 0 : 1).Take(MaxInsights).ToArray();
    }
}
