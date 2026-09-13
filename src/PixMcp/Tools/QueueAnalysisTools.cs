using System.ComponentModel;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using PixMcp.Pix.Handles;

namespace PixMcp.Tools;

[McpServerToolType]
public static class QueueAnalysisTools
{
    public static readonly string[] BubbleSortKeys = ["duration", "start", "index"];

    /// <summary>Overlap, gaps and the event arrays they index, computed once per call on the PIX worker.</summary>
    internal sealed record QueueAnalysisSnapshot(OverlapResult Overlap, IReadOnlyDictionary<int, BubbleScan> Scans, IReadOnlyDictionary<int, EventRecord[]> Events, Interval? Window);

    [McpServerTool(Name = "pix_gpu_queue_overlap", Title = "Queue overlap and idle gaps", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Replays the capture on the local GPU if analysis is not started. Cross-queue view of replay timing: per queue the busy time (union of TOP-to-EOP windows of timed leaf events), idle and solo-busy time; every queue pair's overlap with a verdict (overlapping, mostlySerialized, serialized); the critical-path queue; the longest idle gaps with their likely cause; and insights. Replay may serialise queues and inserts its own synchronisation, so overlap is an upper bound and gaps a lower bound; neither is application frame latency.")]
    public static Task<string> QueueOverlap(PixSession session, JobManager jobs,
        [Description("GPU capture handle (from pix_gpu_open).")] string handle,
        [Description("Only pairs and gaps that involve this queue; omit for every queue.")] int? queueIndex = null,
        [Description(EventScope.Description + " Clips every queue to the selection's replay-clock window and keeps the gaps next to its events.")] EventRef? scope = null,
        [Description(EventScope.PrefixDescription)] string? markerPathPrefix = null,
        [Description("Shortest idle gap counted, in nanoseconds (default 50000).")] long minGapNs = QueueOverlapAnalysis.DefaultMinGapNs,
        [Description("Longest gaps to list in topGaps (default 10, max 1000).")] int limit = 10,
        [Description("Include each queue's merged busy intervals (default false).")] bool includeIntervals = false,
        [Description(Tools.ReadyWaitDescription)] double waitSeconds = Tools.DefaultReadyWaitSeconds,
        [Description(Tools.IncludeProvenanceDescription)] bool includeProvenance = false,
        CancellationToken cancellationToken = default)
    {
        ValidateGap(minGapNs);
        ReferenceValidation.Page(0, limit);
        ScopeSelection selection = Selection(session, handle, queueIndex, scope, markerPathPrefix);
        return Tools.RunWhenReady(session, jobs, "pix_gpu_queue_overlap", handle, CountersTools.TimingPreparation(handle),
            h => OverlapReport(h, Analyze(h, selection, (ulong)minGapNs), selection, queueIndex, (ulong)minGapNs, limit, includeIntervals), waitSeconds, cancellationToken);
    }

    [McpServerTool(Name = "pix_gpu_bubbles", Title = "Idle gaps between GPU events", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Replays the capture on the local GPU if analysis is not started. Idle gaps (bubbles) between consecutive timed leaf events on each queue's replay clock: the events around each gap, its likely cause from the events between them (present, queueWait, queueSignal, barrier, commandListBoundary or unknown, in that precedence), how busy the other queues were meanwhile, and totals per cause. Replay inserts its own synchronisation, so gaps are a lower bound and not application frame latency.")]
    public static Task<string> Bubbles(PixSession session, JobManager jobs,
        [Description("GPU capture handle (from pix_gpu_open).")] string handle,
        [Description("Only this queue; omit for every timed queue.")] int? queueIndex = null,
        [Description(EventScope.Description + " Clips gaps to the selection's replay-clock window and keeps those next to its events.")] EventRef? scope = null,
        [Description(EventScope.PrefixDescription)] string? markerPathPrefix = null,
        [Description("Shortest idle gap counted, in nanoseconds (default 50000).")] long minGapNs = QueueOverlapAnalysis.DefaultMinGapNs,
        [Description("Only gaps with this primary cause: present, queueWait, queueSignal, barrier, commandListBoundary or unknown.")] string? cause = null,
        [Description("duration (default), start or index (queue, then the event after the gap).")] string sortBy = "duration",
        [Description("Reverse the order (default: longest first for duration, ascending for start and index).")] bool? descending = null,
        [Description("First gap to return (default 0).")] int offset = 0,
        [Description("Maximum gaps to return (default 25, max 1000).")] int limit = Paging.DefaultLimit,
        [Description(Shaping.FormatDescription)] string format = "objects",
        [Description(Shaping.BriefDescription)] bool brief = false,
        [Description(Shaping.MaxStringLengthDescription)] int? maxStringLength = null,
        [Description(Tools.ReadyWaitDescription)] double waitSeconds = Tools.DefaultReadyWaitSeconds,
        [Description(Tools.IncludeProvenanceDescription)] bool includeProvenance = false,
        CancellationToken cancellationToken = default)
    {
        ValidateGap(minGapNs);
        string? causeName = cause is null ? null : RollupTools.Canonical(cause, BubbleAnalysis.Causes, "cause");
        string sort = RollupTools.Canonical(sortBy, BubbleSortKeys, "sortBy");
        bool desc = descending ?? sort == "duration";
        ReferenceValidation.Page(offset, limit);
        ShapingOptions shaping = Shaping.Options(format, brief, null, maxStringLength, offset);
        ScopeSelection selection = Selection(session, handle, queueIndex, scope, markerPathPrefix);
        return Tools.RunWhenReady(session, jobs, "pix_gpu_bubbles", handle, CountersTools.TimingPreparation(handle), h =>
        {
            QueueAnalysisSnapshot s = Analyze(h, selection, (ulong)minGapNs);
            var rows = new List<(QueueBusy Queue, Bubble Bubble)>();
            foreach (QueueBusy q in s.Overlap.Queues)
                if (queueIndex is null || q.Timeline.QueueIndex == queueIndex)
                    rows.AddRange(s.Scans[q.Timeline.QueueIndex].Bubbles.Where(b => InSelection(h, selection, b)).Select(b => (q, b)));
            BubbleCauseDto[] perCause = rows.GroupBy(r => (r.Queue.Timeline.QueueIndex, r.Bubble.PrimaryCause))
                .OrderBy(g => g.Key.QueueIndex).ThenBy(g => Array.IndexOf(BubbleAnalysis.Causes, g.Key.PrimaryCause))
                .Select(g => new BubbleCauseDto(g.Key.QueueIndex, g.Key.PrimaryCause, g.Count(), OfSpan(g.Aggregate(0UL, (sum, r) => sum + r.Bubble.DurationNs), g.First().Queue)))
                .ToArray();
            if (causeName is not null) rows = rows.Where(r => r.Bubble.PrimaryCause == causeName).ToList();
            List<(QueueBusy Queue, Bubble Bubble)> sorted = (sort, desc) switch
            {
                ("start", false) => rows.OrderBy(r => r.Bubble.Start).ThenBy(r => r.Queue.Timeline.QueueIndex).ToList(),
                ("start", true) => rows.OrderByDescending(r => r.Bubble.Start).ThenBy(r => r.Queue.Timeline.QueueIndex).ToList(),
                ("index", false) => rows.OrderBy(r => r.Queue.Timeline.QueueIndex).ThenBy(r => r.Bubble.AfterIndex).ToList(),
                ("index", true) => rows.OrderByDescending(r => r.Queue.Timeline.QueueIndex).ThenByDescending(r => r.Bubble.AfterIndex).ToList(),
                (_, false) => rows.OrderBy(r => r.Bubble.DurationNs).ThenBy(r => r.Queue.Timeline.QueueIndex).ThenBy(r => r.Bubble.Start).ToList(),
                _ => rows.OrderByDescending(r => r.Bubble.DurationNs).ThenBy(r => r.Queue.Timeline.QueueIndex).ThenBy(r => r.Bubble.Start).ToList(),
            };
            (int o, int l) = Shaping.Window(shaping, offset, limit);
            BubbleDto[] page = sorted.Skip(o).Take(l).Select(r => ToDto(handle, s.Events[r.Queue.Timeline.QueueIndex], r.Queue, r.Bubble)).ToArray();
            ScopeDescriptionDto? described = selection.DescribeOrNull(h);
            QueueUnavailableDto[] unavailable = Unavailable(s.Overlap);
            ToolCallDto Call(int at, int? strings) => new("pix_gpu_bubbles", new { handle, queueIndex, scope, markerPathPrefix, minGapNs, cause = causeName, sortBy = sort,
                descending = desc, offset = at, limit = l, format, brief, maxStringLength = strings });
            if (shaping.Table)
                return Shaping.Apply(page, sorted.Count, o, l, shaping, RowShapes.Bubbles, handle,
                    new { semantics = QueueOverlapAnalysis.Semantics, minGapNs, cause = causeName, perCause, unavailable, scope = described, provenance = h.Provenance() },
                    next => Call(next, maxStringLength), () => Call(o, Shaping.FullStringLength));
            int? nextOffset = o + (long)page.Length < sorted.Count ? o + page.Length : null;
            var calls = new List<ToolCallDto>();
            if (nextOffset.HasValue) calls.Add(Call(nextOffset.Value, maxStringLength));
            if (page.Length > 0) calls.Add(new("pix_gpu_inspect_event", new { eventRef = page[0].After }));
            calls.Add(new("pix_gpu_queue_overlap", new { handle, scope, markerPathPrefix }));
            return RowShapes.Finish(new BubblesDto(handle, QueueOverlapAnalysis.Semantics, (ulong)minGapNs, causeName, sort, desc, perCause, unavailable, sorted.Count, o,
                page.Length, nextOffset, page, calls) { Scope = described, Provenance = h.Provenance() }, shaping);
        }, waitSeconds, cancellationToken);
    }

    private static void ValidateGap(long minGapNs)
    {
        if (minGapNs < 0) throw PixErrors.InvalidArguments("minGapNs must be nonnegative.");
    }

    private static ScopeSelection Selection(PixSession session, string handle, int? queueIndex, EventRef? scope, string? markerPathPrefix)
    {
        GpuCaptureHandle capture = session.Get<GpuCaptureHandle>(handle);
        if (queueIndex.HasValue) capture.Queue(queueIndex.Value);
        return EventScope.Resolve(session, handle, scope?.QueueIndex ?? queueIndex, scope, markerPathPrefix);
    }

    /// <summary>Timelines (cached per timing collection), overlap over the scope's window, and every queue's gaps at the threshold.</summary>
    internal static QueueAnalysisSnapshot Analyze(GpuCaptureHandle h, ScopeSelection selection, ulong minGapNs)
    {
        Interval? window = null;
        if (!selection.IsUnrestricted)
        {
            if (EventScope.ToTimeWindow(h, selection) is not { } timed)
            {
                ScopeDescriptionDto? described = selection.DescribeOrNull(h);
                throw PixErrors.InvalidArguments("The scope contains no timed events, so it has no replay-clock window to analyse.",
                    [new ToolCallDto("pix_gpu_events", new { handle = h.Id, scope = described?.Root, markerPathPrefix = described?.MarkerPathPrefix })]);
            }
            window = new Interval(timed.StartNs, timed.EndNs);
        }
        OverlapResult overlap = QueueOverlapAnalysis.Compute(h.Queues.Select(q => Timeline(h, q)).ToList(), window);
        var scans = new Dictionary<int, BubbleScan>();
        var events = new Dictionary<int, EventRecord[]>();
        foreach (QueueBusy q in overlap.Queues)
        {
            int index = q.Timeline.QueueIndex;
            EventRecord[] all = h.AllEvents(index);
            int[] children = h.ChildCounts(index);
            List<Interval> others = Intervals.Union(overlap.Queues.Where(o => o.Timeline.QueueIndex != index).SelectMany(o => o.Timeline.Busy));
            scans[index] = BubbleAnalysis.Find(q.Timeline, all, i => Tools.Classify(all[i], i < children.Length && children[i] > 0), others, minGapNs, window);
            events[index] = all;
        }
        return new(overlap, scans, events, window);
    }

    private static QueueTimeline Timeline(GpuCaptureHandle h, QueueEntry queue)
    {
        string type = Json.EnumName(queue.Type);
        if (!h.TimingRowsByQueue.TryGetValue(queue.Index, out EventTimingRow[]? rows) || rows.Length == 0)
            return QueueTimelines.Build(queue.Index, queue.Name, type, [], [], []);
        if (h.TimelineCache.TryGetValue(queue.Index, out var cached) && ReferenceEquals(cached.Rows, rows)) return cached.Timeline;
        QueueTimeline built = QueueTimelines.Build(queue.Index, queue.Name, type, h.AllEvents(queue.Index), h.ChildCounts(queue.Index), rows);
        h.TimelineCache[queue.Index] = (rows, built);
        return built;
    }

    internal static QueueOverlapDto OverlapReport(GpuCaptureHandle h, QueueAnalysisSnapshot s, ScopeSelection selection, int? queueIndex, ulong minGapNs, int limit,
        bool includeIntervals)
    {
        OverlapResult o = s.Overlap;
        var queues = new List<QueueBusyDto>();
        var gaps = new List<BubbleDto>();
        var kept = new Dictionary<int, IReadOnlyList<Bubble>>();
        foreach (QueueBusy q in o.Queues)
        {
            int index = q.Timeline.QueueIndex;
            Bubble[] bubbles = s.Scans[index].Bubbles.Where(b => InSelection(h, selection, b)).ToArray();
            kept[index] = bubbles;
            EventRecord[] events = s.Events[index];
            ulong bubbleNs = bubbles.Aggregate(0UL, (sum, b) => sum + b.DurationNs);
            queues.Add(new QueueBusyDto(index, q.Timeline.Name, q.Timeline.Type, OfSpan(q.BusyNs, q), OfSpan(q.IdleNs, q), OfSpan(q.SoloBusyNs, q), q.SpanStart, q.SpanEnd,
                Metrics.Ms(q.SpanNs), q.Timeline.Leaves.Length, q.Timeline.EopOnly, q.Timeline.ClockAnomalies, s.Scans[index].OverlappedPairs, bubbles.Length,
                OfSpan(bubbleNs, q), bubbles.MaxBy(b => b.DurationNs) is { } largest ? ToDto(h.Id, events, q, largest) : null)
            { Intervals = includeIntervals ? q.Busy.Select(i => new IntervalDto(i.Start, i.End)).ToArray() : null });
            if (queueIndex is null || queueIndex == index)
                gaps.AddRange(bubbles.OrderByDescending(b => b.DurationNs).ThenBy(b => b.Start).Take(limit).Select(b => ToDto(h.Id, events, q, b)));
        }
        ScopeDescriptionDto? described = selection.DescribeOrNull(h);
        BubbleDto[] topGaps = gaps.OrderByDescending(b => b.Duration.Ns).ThenBy(b => b.QueueIndex).ThenBy(b => b.StartNs).Take(limit).ToArray();
        var calls = new List<ToolCallDto>
        {
            new("pix_gpu_bubbles", new { handle = h.Id, queueIndex, scope = described?.Root, markerPathPrefix = described?.MarkerPathPrefix, minGapNs = (long)minGapNs }),
        };
        if (topGaps.Length > 0) calls.Add(new("pix_gpu_inspect_event", new { eventRef = topGaps[0].After }));
        return new QueueOverlapDto(h.Id, QueueOverlapAnalysis.Semantics, o.CaptureStart, o.CaptureEnd, Metrics.Ms(o.CaptureEnd - o.CaptureStart), queues, Unavailable(o),
            o.Pairs.Where(p => queueIndex is null || p.A == queueIndex || p.B == queueIndex).Select(QueueOverlapAnalysis.ToDto).ToArray(), o.CriticalPath, topGaps,
            new OverlapThresholdsDto(QueueOverlapAnalysis.OverlappingPercent, QueueOverlapAnalysis.MostlySerializedPercent, minGapNs, QueueOverlapAnalysis.PairDenominator),
            QueueAnalysisInsights.Evaluate(h.Id, o, kept), calls)
        { Scope = described, Window = s.Window is Interval w ? new IntervalDto(w.Start, w.End) : null, Provenance = h.Provenance() };
    }

    /// <summary>A gap belongs to a restricted selection when an event on either side of it does.</summary>
    private static bool InSelection(GpuCaptureHandle h, ScopeSelection selection, Bubble b)
        => selection.IsUnrestricted || selection.Contains(h, b.QueueIndex, b.AfterIndex) || selection.Contains(h, b.QueueIndex, b.BeforeIndex);

    private static QueueUnavailableDto[] Unavailable(OverlapResult o)
        => o.Unavailable.Select(u => new QueueUnavailableDto(u.Timeline.QueueIndex, u.Timeline.Name, u.Timeline.Type, u.Reason)).ToArray();

    private static DurationDto OfSpan(ulong ns, QueueBusy q) => new(ns, Metrics.Ms(ns), Metrics.Percent(ns, q.SpanNs), null, null, null);

    private static BubbleDto ToDto(string handle, EventRecord[] events, QueueBusy q, Bubble b)
        => new(b.QueueIndex, b.Start, OfSpan(b.DurationNs, q), new EventRef(handle, b.QueueIndex, b.BeforeIndex), events[b.BeforeIndex].Name,
            new EventRef(handle, b.QueueIndex, b.AfterIndex), events[b.AfterIndex].Name, string.Join("/", EventNavigation.MarkerPath(events, b.AfterIndex)),
            b.PrimaryCause, b.Causes, b.EvidenceIndex is uint evidence ? new EventRef(handle, b.QueueIndex, evidence) : null, b.CommandListChanged, b.OtherQueueBusyPercent);
}
