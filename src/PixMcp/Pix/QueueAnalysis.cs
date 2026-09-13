using PixMcp.Pix.Handles;

namespace PixMcp.Pix;

/// <summary>One timed leaf event's replay window: [TOP start (or EOP start), EOP end).</summary>
internal readonly record struct LeafWindow(uint Index, ulong Start, ulong End);

/// <summary>A queue's timed leaf windows sorted by start, with their merged busy intervals; the input to overlap and bubble analysis.</summary>
internal sealed record QueueTimeline(int QueueIndex, string Name, string Type, LeafWindow[] Leaves, ulong SpanStart, ulong SpanEnd, int ClockAnomalies, bool EopOnly,
    IReadOnlyList<Interval> Busy);

internal static class QueueTimelines
{
    /// <summary>
    /// Timed leaf rows only: a marker with children is timed by PIX as the span of its contents, so its window would bridge
    /// the gaps being measured. A TOP start after the EOP end is a clock anomaly; that row falls back to its EOP window.
    /// </summary>
    public static QueueTimeline Build(int queueIndex, string name, string type, EventRecord[] events, int[] childCounts, IReadOnlyList<EventTimingRow> rows)
    {
        var leaves = new List<LeafWindow>();
        int anomalies = 0;
        bool anyTop = false;
        foreach (EventTimingRow row in rows)
        {
            if (row.EopDuration == GpuCaptureHandle.TimingNone || row.EopStart == GpuCaptureHandle.TimingNone || row.Index >= events.Length) continue;
            if (row.Index < childCounts.Length && childCounts[row.Index] > 0) continue;
            ulong end = row.EopStart + row.EopDuration, start = row.EopStart;
            if (row.TopStart != GpuCaptureHandle.TimingNone)
            {
                anyTop = true;
                if (row.TopStart <= end) start = Math.Min(row.TopStart, row.EopStart);
                else anomalies++;
            }
            leaves.Add(new(row.Index, start, end));
        }
        LeafWindow[] sorted = leaves.OrderBy(l => l.Start).ThenBy(l => l.End).ThenBy(l => l.Index).ToArray();
        return new(queueIndex, name, type, sorted, sorted.Length == 0 ? 0 : sorted[0].Start, sorted.Length == 0 ? 0 : sorted.Max(l => l.End), anomalies, !anyTop,
            Intervals.Union(sorted.Select(l => new Interval(l.Start, l.End))));
    }
}

/// <summary>A queue's busy time inside the analysed window.</summary>
internal sealed record QueueBusy(QueueTimeline Timeline, IReadOnlyList<Interval> Busy, ulong BusyNs, ulong SpanStart, ulong SpanEnd, ulong SoloBusyNs)
{
    public ulong SpanNs => SpanEnd > SpanStart ? SpanEnd - SpanStart : 0;
    public ulong IdleNs => SpanNs > BusyNs ? SpanNs - BusyNs : 0;
}

internal sealed record QueueOverlapPair(int A, int B, ulong OverlapNs, double? PercentOfA, double? PercentOfB, string Verdict);

internal sealed record OverlapResult(IReadOnlyList<QueueBusy> Queues, IReadOnlyList<(QueueTimeline Timeline, string Reason)> Unavailable,
    IReadOnlyList<QueueOverlapPair> Pairs, ulong CaptureStart, ulong CaptureEnd, int? CriticalPath);

internal static class QueueOverlapAnalysis
{
    public const double OverlappingPercent = 50, MostlySerializedPercent = 10;
    public const long DefaultMinGapNs = 50_000;
    public const string Semantics = "Replay timing of this capture on this machine. Busy is the union of TOP-to-EOP windows of timed leaf events; markers with children " +
        "are left out because their spans bridge the gaps being measured. Replay may serialise queues and inserts its own synchronisation, so overlap is an upper bound " +
        "and gaps are a lower bound; neither is application frame latency.";
    public const string PairDenominator = "percentOfABusy and percentOfBBusy divide the overlap by each queue's busy time; the verdict divides it by the smaller busy time";

    public static OverlapResult Compute(IReadOnlyList<QueueTimeline> timelines, Interval? window)
    {
        var available = new List<(QueueTimeline Timeline, IReadOnlyList<Interval> Busy, ulong Start, ulong End)>();
        var unavailable = new List<(QueueTimeline, string)>();
        foreach (QueueTimeline t in timelines)
        {
            if (t.Leaves.Length == 0)
            {
                unavailable.Add((t, "noTimedEvents"));
                continue;
            }
            ulong start = t.SpanStart, end = t.SpanEnd;
            IReadOnlyList<Interval> busy = t.Busy;
            if (window is Interval w)
            {
                start = Math.Max(start, w.Start);
                end = Math.Min(end, w.End);
                if (end <= start)
                {
                    unavailable.Add((t, "noTimedEventsInWindow"));
                    continue;
                }
                busy = Intervals.Clip(t.Busy, start, end);
            }
            available.Add((t, busy, start, end));
        }

        var queues = new List<QueueBusy>();
        foreach (var q in available)
        {
            List<Interval> others = Intervals.Union(available.Where(o => o.Timeline.QueueIndex != q.Timeline.QueueIndex).SelectMany(o => o.Busy));
            ulong busyNs = Intervals.Length(q.Busy);
            ulong shared = Intervals.Length(Intervals.Intersection(q.Busy, others));
            queues.Add(new(q.Timeline, q.Busy, busyNs, q.Start, q.End, busyNs - shared));
        }
        var pairs = new List<QueueOverlapPair>();
        for (int i = 0; i < queues.Count; i++)
            for (int j = i + 1; j < queues.Count; j++)
            {
                QueueBusy a = queues[i], b = queues[j];
                ulong overlap = Intervals.Length(Intervals.Intersection(a.Busy, b.Busy));
                pairs.Add(new(a.Timeline.QueueIndex, b.Timeline.QueueIndex, overlap, Metrics.Percent(overlap, a.BusyNs), Metrics.Percent(overlap, b.BusyNs),
                    Verdict(overlap, Math.Min(a.BusyNs, b.BusyNs))));
            }
        int? critical = queues.OrderByDescending(q => q.BusyNs).ThenByDescending(q => q.SpanNs).ThenBy(q => q.Timeline.QueueIndex)
            .Select(q => (int?)q.Timeline.QueueIndex).FirstOrDefault();
        return new(queues, unavailable, pairs, queues.Count == 0 ? 0 : queues.Min(q => q.SpanStart), queues.Count == 0 ? 0 : queues.Max(q => q.SpanEnd), critical);
    }

    /// <summary>overlapping at 50 % of the smaller busy time or more, mostlySerialized at 10 % or more, else serialized.</summary>
    public static string Verdict(ulong overlapNs, ulong smallerBusyNs)
    {
        if (smallerBusyNs == 0) return "serialized";
        double percent = 100.0 * overlapNs / smallerBusyNs;
        return percent >= OverlappingPercent ? "overlapping" : percent >= MostlySerializedPercent ? "mostlySerialized" : "serialized";
    }

    public static QueuePairOverlapDto ToDto(QueueOverlapPair pair)
        => new(pair.A, pair.B, new DurationDto(pair.OverlapNs, Metrics.Ms(pair.OverlapNs), null, null, null, null), pair.PercentOfA, pair.PercentOfB, pair.Verdict);
}

/// <summary>An idle gap between consecutive timed leaf windows on one queue.</summary>
internal sealed record Bubble(int QueueIndex, ulong Start, ulong DurationNs, uint BeforeIndex, uint AfterIndex, string PrimaryCause, IReadOnlyList<string> Causes,
    uint? EvidenceIndex, bool CommandListChanged, double? OtherQueueBusyPercent);

internal sealed record BubbleScan(IReadOnlyList<Bubble> Bubbles, int OverlappedPairs);

internal static class BubbleAnalysis
{
    /// <summary>Causes in precedence order; the first one found between a gap's events is its primary cause.</summary>
    public static readonly string[] Causes = ["present", "queueWait", "queueSignal", "barrier", "commandListBoundary", "unknown"];
    public const int MaxScannedEvents = 4096;

    /// <summary>
    /// Gaps between the running end of the earlier leaf windows and the next leaf start, at least <paramref name="minGapNs"/>
    /// (and 1 ns) long after clipping to <paramref name="window"/>. Consecutive windows that overlap count as overlapped pairs.
    /// </summary>
    public static BubbleScan Find(QueueTimeline timeline, EventRecord[] events, Func<uint, string> kindOf, IReadOnlyList<Interval> otherBusy, ulong minGapNs,
        Interval? window, bool attribute = true)
    {
        var bubbles = new List<Bubble>();
        LeafWindow[] leaves = timeline.Leaves;
        if (leaves.Length == 0) return new(bubbles, 0);
        int overlapped = 0;
        ulong reach = leaves[0].End;
        LeafWindow reachLeaf = leaves[0];
        for (int i = 1; i < leaves.Length; i++)
        {
            LeafWindow leaf = leaves[i];
            if (leaf.Start < leaves[i - 1].End) overlapped++;
            if (leaf.Start > reach)
            {
                ulong start = reach, end = leaf.Start;
                if (window is Interval w)
                {
                    start = Math.Max(start, w.Start);
                    end = Math.Min(end, w.End);
                }
                if (end > start && end - start >= Math.Max(minGapNs, 1))
                    bubbles.Add(attribute
                        ? Attribute(timeline.QueueIndex, start, end - start, reachLeaf.Index, leaf.Index, events, kindOf, otherBusy)
                        : new(timeline.QueueIndex, start, end - start, reachLeaf.Index, leaf.Index, "unknown", [], null, false, null));
            }
            if (leaf.End > reach)
            {
                reach = leaf.End;
                reachLeaf = leaf;
            }
        }
        return new(bubbles, overlapped);
    }

    /// <summary>Scans the events between the pair, the event after the gap included, for causes; the event before it counts only for present, Wait and Signal (capped at <see cref="MaxScannedEvents"/>).</summary>
    internal static Bubble Attribute(int queueIndex, ulong start, ulong duration, uint before, uint after, EventRecord[] events, Func<uint, string> kindOf,
        IReadOnlyList<Interval> otherBusy)
    {
        var found = new Dictionary<string, uint>();
        uint? commandList = before < events.Length && events[before].CommandListId != 0 ? events[before].CommandListId : null;
        void Examine(uint i)
        {
            if (i >= events.Length) return;
            EventRecord e = events[i];
            string kind = kindOf(i);
            // The event that ends the busy stretch counts for queue-level synchronisation: a gap right after a Present, Wait or Signal follows from it.
            if (kind == "present") found.TryAdd("present", i);
            if (e.Name.StartsWith("Wait", StringComparison.OrdinalIgnoreCase)) found.TryAdd("queueWait", i);
            if (e.Name.StartsWith("Signal", StringComparison.OrdinalIgnoreCase)) found.TryAdd("queueSignal", i);
            if (i == before) return;
            if (kind == "barrier") found.TryAdd("barrier", i);
            if (e.CommandListId == 0) return;
            if (commandList is null) commandList = e.CommandListId;
            else if (e.CommandListId != commandList) found.TryAdd("commandListBoundary", i);
        }
        uint lo = Math.Min(before, after), hi = Math.Max(before, after);
        uint last = hi - lo > MaxScannedEvents ? lo + MaxScannedEvents : hi;
        for (uint i = lo; i <= last; i++) Examine(i);
        if (last < hi) Examine(hi);
        string[] causes = Causes.Where(found.ContainsKey).ToArray();
        string primary = causes.FirstOrDefault() ?? "unknown";
        return new(queueIndex, start, duration, before, after, primary, causes, primary == "unknown" ? null : found[primary], found.ContainsKey("commandListBoundary"),
            otherBusy.Count == 0 ? null : Metrics.Percent(Intervals.CoveredLength(otherBusy, start, start + duration), duration));
    }
}

/// <summary>Findings over queue overlap and gaps; warnings first, every call filtered through <see cref="ToolRegistry"/>.</summary>
internal static class QueueAnalysisInsights
{
    public const double BubblesDominantPercent = 25, CopyCoveragePercent = 50;
    public const int MaxInsights = 8;

    public static IReadOnlyList<InsightDto> Evaluate(string handle, OverlapResult overlap, IReadOnlyDictionary<int, IReadOnlyList<Bubble>> bubbles)
    {
        var found = new List<InsightDto>();
        void Add(string id, string severity, string summary, Dictionary<string, object?> evidence, string implication, params ToolCallDto[] calls)
            => found.Add(new(id, severity, summary, evidence, implication, calls.Where(ToolRegistry.Accepts).ToArray()));
        IReadOnlyList<Bubble> GapsOf(QueueBusy q) => bubbles.TryGetValue(q.Timeline.QueueIndex, out IReadOnlyList<Bubble>? gaps) ? gaps : [];
        static ulong Total(IEnumerable<Bubble> gaps) => gaps.Aggregate(0UL, (sum, g) => sum + g.DurationNs);

        foreach (QueueBusy copy in overlap.Queues.Where(q => q.Timeline.Type.Contains("COPY", StringComparison.OrdinalIgnoreCase)))
            foreach (QueueBusy other in overlap.Queues.Where(q => q.Timeline.QueueIndex != copy.Timeline.QueueIndex))
            {
                IReadOnlyList<Bubble> gaps = GapsOf(other);
                ulong total = Total(gaps);
                ulong covered = gaps.Aggregate(0UL, (sum, g) => sum + Intervals.CoveredLength(copy.Busy, g.Start, g.Start + g.DurationNs));
                if (total == 0 || covered * 100.0 < CopyCoveragePercent * total) continue;
                Add("copy_queue_blocking", "warning", $"{copy.Timeline.Name} was busy during {Metrics.Percent(covered, total)} % of the idle gaps on {other.Timeline.Name}.",
                    new() { ["copyQueueIndex"] = copy.Timeline.QueueIndex, ["queueIndex"] = other.Timeline.QueueIndex, ["gapMs"] = Metrics.Ms(total), ["coveredMs"] = Metrics.Ms(covered) },
                    "The queue sits idle while the copy queue works, consistent with waiting on uploads; replay may add its own synchronisation, so confirm with a recorded timing capture.",
                    new ToolCallDto("pix_gpu_bubbles", new { handle, queueIndex = other.Timeline.QueueIndex }));
            }

        Dictionary<int, QueueBusy> byIndex = overlap.Queues.ToDictionary(q => q.Timeline.QueueIndex);
        foreach (QueueOverlapPair pair in overlap.Pairs.Where(p => p.Verdict == "serialized"))
        {
            QueueBusy a = byIndex[pair.A], b = byIndex[pair.B];
            if (a.BusyNs == 0 || b.BusyNs == 0) continue;
            Add("queues_serialized", "info", $"{a.Timeline.Name} and {b.Timeline.Name} overlapped for {Metrics.Ms(pair.OverlapNs)} ms on replay.",
                new() { ["queueA"] = pair.A, ["queueB"] = pair.B, ["overlapMs"] = Metrics.Ms(pair.OverlapNs), ["percentOfABusy"] = pair.PercentOfA, ["percentOfBBusy"] = pair.PercentOfB },
                "The queues ran one after the other during replay, so work on one did not shorten the other; replay may serialise queues itself, so confirm with a recorded timing capture before changing the application.",
                new ToolCallDto("pix_gpu_bubbles", new { handle, queueIndex = a.BusyNs >= b.BusyNs ? pair.A : pair.B }));
        }

        foreach (QueueBusy q in overlap.Queues)
        {
            IReadOnlyList<Bubble> gaps = GapsOf(q);
            ulong total = Total(gaps);
            if (gaps.Count == 0 || q.SpanNs == 0 || total * 100.0 < BubblesDominantPercent * q.SpanNs) continue;
            string cause = gaps.GroupBy(g => g.PrimaryCause).OrderByDescending(g => Total(g)).ThenBy(g => Array.IndexOf(BubbleAnalysis.Causes, g.Key)).First().Key;
            Add("bubbles_dominant", "info", $"Idle gaps fill {Metrics.Percent(total, q.SpanNs)} % of {q.Timeline.Name}'s replay span.",
                new() { ["queueIndex"] = q.Timeline.QueueIndex, ["gapMs"] = Metrics.Ms(total), ["spanMs"] = Metrics.Ms(q.SpanNs), ["gaps"] = gaps.Count, ["largestCause"] = cause },
                "The queue waits more than it works on replay; the causes show whether presents, synchronisation, barriers or command-list boundaries separate its work.",
                new ToolCallDto("pix_gpu_bubbles", new { handle, queueIndex = q.Timeline.QueueIndex, sortBy = "duration" }));
        }
        return found.OrderBy(i => i.Severity == "warning" ? 0 : 1).Take(MaxInsights).ToArray();
    }
}
