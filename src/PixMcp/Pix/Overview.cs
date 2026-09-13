using PixMcp.Pix.Handles;
using ToolKinds = PixMcp.Tools.Tools;

namespace PixMcp.Pix;

/// <summary>One queue as the overview sees it: cached events (null while not cached), child counts, and timing when collected.</summary>
internal sealed record OverviewQueueInput(int QueueIndex, string Name, string Type, uint EventCount, EventRecord[]? Events, int[]? ChildCounts,
    TimingTreeResult? Tree, EventTimingRow[]? Rows);

internal sealed record OverviewInputs(string Handle, string Path, VendorIdentity Vendor, IReadOnlyList<OverviewQueueInput> Queues,
    IReadOnlyDictionary<string, CapabilityDto> Capabilities, ReplayProvenance? Provenance, bool Timing, Func<int, uint, bool> InScope, ScopeDescriptionDto? Scope,
    bool ExperimentsLoaded = false);

internal sealed record OverviewOptions(int Limit, int? FrameIndex = null, bool IncludeInsights = true);

/// <summary>Builds pix_gpu_overview from cached capture data; the only handle access happens in the caller.</summary>
internal static class OverviewBuilder
{
    public const int MaxPerFrame = 50;
    public const string Semantics = "Replay timing of this capture on this machine: per-queue totals on the replay clock and frames delimited by Present calls; not application frame latency.";
    public static readonly string[] KindOrder = ["work", "draw", "dispatch", "executeIndirect", "copy", "clear", "resolve", "barrier", "present", "marker", "label", "other"];
    private static readonly ulong[] Edges = [10_000, 20_000, 50_000, 100_000, 200_000, 500_000, 1_000_000, 2_000_000, 5_000_000, 10_000_000];

    private static bool IsWork(string kind) => kind is "draw" or "dispatch" or "executeIndirect";

    /// <summary>A pass is a marker with children that carries timing itself or through its descendants; timed leaf labels are not passes.</summary>
    public static bool IsPass(TimingTreeNode node, EventRecord record, bool hasChildren)
        => hasChildren && ToolKinds.Classify(record, hasChildren) == "marker" && node.IsTimed;

    public static CaptureOverviewDto Build(OverviewInputs inputs, OverviewOptions options)
    {
        List<OverviewQueueInput> cached = inputs.Queues.Where(q => q.Events is not null).ToList();
        FrameTable frames = FrameSegmentation.Build(cached.Select(q => new FrameQueueInput(q.QueueIndex, q.Events!, inputs.Timing ? q.Rows : null)).ToList(),
            e => ToolKinds.MatchesKind(e, "present"));
        if (options.FrameIndex is int requested && (requested < 0 || requested >= frames.Count))
            throw PixErrors.InvalidArguments($"frameIndex {requested} is outside this capture's {frames.Count} frame(s) (0..{frames.Count - 1}).",
                [new ToolCallDto("pix_gpu_overview", new { handle = inputs.Handle })]);
        bool Selected(int queue, uint index) => inputs.InScope(queue, index) && (options.FrameIndex is not int frame || frames.Contains(frame, queue, index));

        var queueRows = new List<QueueOverviewDto>();
        var passes = new List<(OverviewPassDto Pass, ulong Inclusive)>();
        var work = new List<(OverviewWorkDto Row, ulong Eop)>();
        var allWork = new List<(int Queue, EventTimingRow Row)>();
        int markerEvents = 0, barrierEvents = 0, selectedWork = 0;
        foreach (OverviewQueueInput q in inputs.Queues)
        {
            if (q.Events is null || q.ChildCounts is null)
            {
                queueRows.Add(new(q.QueueIndex, q.Name, q.Type, q.EventCount, new Dictionary<string, int>(), null));
                continue;
            }
            EventRecord[] events = q.Events;
            int[] children = q.ChildCounts;
            var kinds = new string[events.Length];
            Dictionary<string, int> counts = KindOrder.ToDictionary(k => k, _ => 0);
            var workCount = new int[events.Length];
            for (int i = 0; i < events.Length; i++)
            {
                kinds[i] = ToolKinds.Classify(events[i], i < children.Length && children[i] > 0);
                counts[kinds[i]] = counts.GetValueOrDefault(kinds[i]) + 1;
                if (!IsWork(kinds[i])) continue;
                counts["work"]++;
                workCount[i] = 1;
            }
            for (int i = events.Length - 1; i > 0; i--)
                if (events[i].ParentIndex < (uint)i) workCount[events[i].ParentIndex] += workCount[i];

            markerEvents += counts["marker"];
            barrierEvents += counts["barrier"];
            QueueTotals? totals = inputs.Timing ? q.Tree?.Totals : null;
            queueRows.Add(new(q.QueueIndex, q.Name, q.Type, q.EventCount, counts, totals));
            if (totals is null) continue;

            TimingTreeNode[] nodes = q.Tree!.Nodes;
            for (uint i = 0; i < events.Length; i++)
                if (IsWork(kinds[i]) && Selected(q.QueueIndex, i)) selectedWork++;
            for (uint i = 0; i < events.Length && i < nodes.Length; i++)
            {
                TimingTreeNode node = nodes[i];
                if (kinds[i] != "marker" || children[i] == 0 || !node.IsTimed || !Selected(q.QueueIndex, i)) continue;
                passes.Add((new OverviewPassDto(new EventRef(inputs.Handle, q.QueueIndex, i), EventNavigation.MarkerPath(events, i), node.Name, node.Semantics,
                    Metrics.Duration(node.InclusiveEopNs, totals), Metrics.Duration(node.SelfEopNs, totals, node.InclusiveEopNs),
                    node.ChildrenExceedMeasured, node.ChildOverflowNs, children[i], workCount[i]), node.InclusiveEopNs));
            }
            foreach (EventTimingRow row in q.Rows ?? [])
            {
                if (row.EopDuration == GpuCaptureHandle.TimingNone || row.Index >= events.Length || !IsWork(kinds[row.Index])) continue;
                allWork.Add((q.QueueIndex, row));
                if (!Selected(q.QueueIndex, row.Index)) continue;
                ulong eopEnd = row.EopStart + row.EopDuration;
                ulong? exec = row.TopStart != GpuCaptureHandle.TimingNone && eopEnd >= row.TopStart ? eopEnd - row.TopStart : null;
                EventRecord e = events[row.Index];
                work.Add((new OverviewWorkDto(new EventRef(inputs.Handle, q.QueueIndex, row.Index), EventNavigation.MarkerPath(events, row.Index), e.Name, kinds[row.Index],
                    Metrics.Duration(row.EopDuration, totals), exec is ulong executed ? Metrics.Duration(executed, totals) : null,
                    string.IsNullOrEmpty(e.ApiCallData) ? null : new OverviewParametersDto(e.ApiCallData)), row.EopDuration));
            }
        }

        OverviewPassDto[] topPasses = passes.OrderByDescending(p => p.Inclusive).ThenBy(p => p.Pass.EventRef.QueueIndex).ThenBy(p => p.Pass.EventRef.EventIndex)
            .Take(options.Limit).Select((p, i) => p.Pass with { Inclusive = p.Pass.Inclusive with { Rank = i + 1 } }).ToArray();
        OverviewWorkDto[] topDraws = work.OrderByDescending(w => w.Eop).ThenBy(w => w.Row.EventRef.QueueIndex).ThenBy(w => w.Row.EventRef.EventIndex)
            .Take(options.Limit).Select((w, i) => w.Row with { Eop = w.Row.Eop with { Rank = i + 1 } }).ToArray();

        OverviewHistogramDto? histogram = work.Count == 0 ? null : Histogram(work, queueRows);
        OverviewFramesDto? framesSection = inputs.Timing && frames.Count > 1 ? FramesSection(frames, allWork) : null;

        var calls = new List<ToolCallDto>
        {
            new("pix_gpu_events", new { handle = inputs.Handle, kind = "work", limit = options.Limit, scope = inputs.Scope?.Root, markerPathPrefix = inputs.Scope?.MarkerPathPrefix }),
        };
        if (topPasses.Length > 0) calls.Add(new("pix_gpu_timing_tree", new { handle = inputs.Handle, scope = topPasses[0].EventRef, sortBy = "self" }));
        if (inputs.Timing) calls.Add(new("pix_gpu_timing_events", new { handle = inputs.Handle, sortBy = "eopDuration", limit = options.Limit }));
        if (framesSection is not null && options.FrameIndex is null)
            calls.Add(new("pix_gpu_overview", new { handle = inputs.Handle, frameIndex = framesSection.PerFrame.MaxBy(f => f.BusyNs)!.Index }));

        var capture = new OverviewCaptureDto(inputs.Path, inputs.Queues.Sum(q => (long)q.EventCount), inputs.Queues.Count, inputs.Vendor,
            new OverviewFrameInfoDto(frames.Count, frames.PresentQueueIndex, frames.Assignment, options.FrameIndex), Semantics);
        var overview = new CaptureOverviewDto(inputs.Handle, capture, queueRows, inputs.Capabilities, inputs.Timing ? Metrics.Denominators : null, inputs.Provenance,
            topPasses, topDraws, histogram, framesSection, calls)
        { Scope = inputs.Scope, Overlap = inputs.Timing && inputs.Scope is null && options.FrameIndex is null ? OverlapSection(cached) : null };
        if (!inputs.Timing || !options.IncludeInsights) return overview;
        ulong[] eops = work.Select(w => w.Eop).Order().ToArray();
        var facts = new OverviewInsightFacts(selectedWork, work.Count, eops.Aggregate(0UL, (sum, value) => sum + value),
            work.Where(w => w.Row.Kind == "executeIndirect").Aggregate(0UL, (sum, w) => sum + w.Eop), eops, barrierEvents, markerEvents, inputs.ExperimentsLoaded);
        return overview with { Insights = InsightRules.Evaluate(overview, facts) };
    }

    /// <summary>Queue pairs and per-queue gap totals over the whole capture; null with fewer than two timed queues.</summary>
    private static OverviewOverlapDto? OverlapSection(IReadOnlyList<OverviewQueueInput> queues)
    {
        var timed = queues.Where(q => q.Events is not null && q.ChildCounts is not null && q.Rows is { Length: > 0 })
            .Select(q => (Input: q, Timeline: QueueTimelines.Build(q.QueueIndex, q.Name, q.Type, q.Events!, q.ChildCounts!, q.Rows!))).ToList();
        OverlapResult overlap = QueueOverlapAnalysis.Compute(timed.Select(t => t.Timeline).ToList(), null);
        if (overlap.Pairs.Count == 0) return null;
        OverviewQueueGapsDto[] gaps = overlap.Queues.Select(q =>
        {
            EventRecord[] events = timed.First(t => t.Timeline.QueueIndex == q.Timeline.QueueIndex).Input.Events!;
            IReadOnlyList<Bubble> found = BubbleAnalysis.Find(q.Timeline, events, _ => "other", [], (ulong)QueueOverlapAnalysis.DefaultMinGapNs, null, attribute: false).Bubbles;
            return new OverviewQueueGapsDto(q.Timeline.QueueIndex, Metrics.Ms(q.SoloBusyNs), found.Count, Metrics.Ms(found.Aggregate(0UL, (sum, b) => sum + b.DurationNs)),
                found.Count == 0 ? null : Metrics.Ms(found.Max(b => b.DurationNs)));
        }).ToArray();
        OverviewQueueGapsDto[] withGaps = gaps.Where(g => g.BubbleCount > 0).ToArray();
        return new OverviewOverlapDto(overlap.Pairs.Select(QueueOverlapAnalysis.ToDto).ToArray(), overlap.CriticalPath, withGaps.Length == 0 ? null : withGaps);
    }

    private static string Format(ulong ns) => ns >= 1_000_000 ? $"{ns / 1_000_000}ms" : $"{ns / 1_000}us";

    /// <summary>Selected work events by EOP duration: the non-empty 1-2-5 buckets (edges 10 us to 10 ms, plus overflow) in edge order.</summary>
    private static OverviewHistogramDto Histogram(List<(OverviewWorkDto Row, ulong Eop)> work, List<QueueOverviewDto> queues)
    {
        int[] used = work.Select(w => w.Row.EventRef.QueueIndex).Distinct().Order().ToArray();
        ulong denominator = used.Aggregate(0UL, (sum, index) => sum + (queues.First(q => q.QueueIndex == index).Totals?.BusyNs ?? 0));
        string label = used.Length == 1 ? $"busyNs of queue {used[0]}" : $"sum of busyNs over queues [{string.Join(", ", used)}]";
        var buckets = new List<OverviewHistogramBucketDto>(Edges.Length + 1);
        for (int b = 0; b <= Edges.Length; b++)
        {
            ulong min = b == 0 ? 0 : Edges[b - 1];
            ulong? max = b < Edges.Length ? Edges[b] : null;
            var rows = work.Where(w => w.Eop >= min && (max is null || w.Eop < max)).ToList();
            if (rows.Count == 0) continue;
            ulong sum = rows.Aggregate(0UL, (total, w) => total + w.Eop);
            string name = b == 0 ? "<" + Format(max!.Value) : max is null ? ">=" + Format(min) : Format(min) + "-" + Format(max.Value);
            buckets.Add(new(name, min, max, rows.Count, sum, Metrics.Ms(sum), Metrics.Percent(sum, denominator)));
        }
        return new(label, denominator, buckets);
    }

    /// <summary>Per-frame busy time: the union of TOP..EOP windows of every timed work event assigned to the frame, across queues.</summary>
    private static OverviewFramesDto FramesSection(FrameTable frames, List<(int Queue, EventTimingRow Row)> work)
    {
        var windows = Enumerable.Range(0, frames.Count).Select(_ => new List<(ulong Start, ulong End)>()).ToArray();
        var counts = new int[frames.Count];
        foreach (var (queue, row) in work)
        {
            if (frames.FrameOf(queue, row.Index) is not int frame) continue;
            ulong end = row.EopStart + row.EopDuration;
            ulong start = row.TopStart != GpuCaptureHandle.TimingNone && row.TopStart <= end ? row.TopStart : row.EopStart;
            windows[frame].Add((start, end));
            counts[frame]++;
        }
        OverviewFrameDto[] perFrame = frames.Frames.Select(span =>
        {
            ulong busy = Metrics.UnionLength(windows[span.Index]);
            return new OverviewFrameDto(span.Index, span.FirstEventIndex, span.LastEventIndex, span.PresentEventIndex, span.Partial, counts[span.Index], busy, Metrics.Ms(busy),
                span.WindowStartNs is ulong windowStart && span.WindowEndNs is ulong windowEnd ? windowEnd - windowStart : null);
        }).ToArray();
        long[] sorted = perFrame.Select(f => (long)f.BusyNs).Order().ToArray();
        double Rank(int percentile) => Metrics.Ms((ulong)RecordedTiming.NearestRank(sorted, percentile));
        var percentiles = new OverviewFramePercentilesDto(Rank(50), sorted.Length >= 5 ? Rank(95) : null, sorted.Length >= 5 ? Rank(99) : null, Metrics.Ms((ulong)sorted[^1]));
        return new OverviewFramesDto(perFrame.Length, perFrame.Length > MaxPerFrame, perFrame.Take(MaxPerFrame).ToArray(), percentiles);
    }
}
