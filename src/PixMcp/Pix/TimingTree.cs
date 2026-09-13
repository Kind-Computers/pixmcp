using PixMcp.Pix.Handles;

namespace PixMcp.Pix;

/// <summary>How an inclusive value came to be; the reader must not treat a derived sum as a PIX measurement.</summary>
public static class TimingSemantics
{
    /// <summary>PIX measured this event itself (markers are measured as the span of their contents).</summary>
    public const string Measured = "measured";
    /// <summary>No own measurement; the value is the serialized sum of the children's inclusive values.</summary>
    public const string DerivedSum = "derivedSum";
    /// <summary>A derived sum whose contributors mix measured spans and derived sums; can over- or understate.</summary>
    public const string Mixed = "mixed";
    /// <summary>Neither this event nor anything beneath it carries timing.</summary>
    public const string Untimed = "untimed";
}

/// <summary>One event of a queue with GPU time rolled up from its subtree.</summary>
/// <param name="Index">Event index within the queue.</param>
/// <param name="ParentIndex">Parent event index, or null for a top-level event.</param>
/// <param name="Semantics">measured, derivedSum, mixed or untimed (see <see cref="TimingSemantics"/>).</param>
/// <param name="MeasuredEopNs">PIX's own end-of-pipe duration for this event, or null when PIX has none.</param>
/// <param name="InclusiveEopNs">Time attributed to this event and everything beneath it: the measured value when PIX has one, otherwise the sum of the children's inclusive time.</param>
/// <param name="SelfEopNs">Inclusive time not explained by the children, clamped at 0.</param>
/// <param name="ChildSumEopNs">Sum of the children's inclusive values.</param>
/// <param name="ChildrenExceedMeasured">True when the children sum to more than PIX measured for this event (pipelined or overlapping children).</param>
/// <param name="ChildOverflowNs">How much the children exceed the measured value.</param>
/// <param name="TimedDescendants">Number of descendants (not counting this event) with their own PIX measurement.</param>
/// <param name="ChildCount">Number of direct children.</param>
/// <param name="UntimedChildren">Direct children whose subtree carries no timing at all.</param>
/// <param name="Repaired">True when a corrupt parent link involving this event was repaired (the event was re-rooted or a cycle was cut).</param>
/// <param name="TopStartNs">TOP start of a measured event, or the earliest TOP start beneath a derived marker.</param>
/// <param name="ExecutionNs">EOP end minus TOP start of a measured event; overlaps neighbours; null for derived markers.</param>
public sealed record TimingTreeNode(uint Index, uint? ParentIndex, string Name, string ApiCallData, uint? GpuId,
    string Semantics, ulong? MeasuredEopNs, ulong InclusiveEopNs, ulong SelfEopNs, ulong ChildSumEopNs,
    bool ChildrenExceedMeasured, ulong ChildOverflowNs, int TimedDescendants, int ChildCount, int UntimedChildren,
    bool Repaired, ulong? TopStartNs, ulong? EopStartNs, ulong? EopEndNs, ulong? ExecutionNs)
{
    public bool HasOwnTiming => MeasuredEopNs.HasValue;
    public bool IsTimed => Semantics != TimingSemantics.Untimed;
}

/// <summary>The rolled-up tree of one queue plus the totals every percentage is computed against.</summary>
public sealed record TimingTreeResult(TimingTreeNode[] Nodes, QueueTotals Totals, int Repairs);

/// <summary>
/// Rolls per-event GPU timing up the marker hierarchy so "which pass is slowest" is one query.
/// PIX times markers as the span of the work inside them, so a measured event is taken as-is and
/// its children are never added on top; only unmeasured markers are summed from their children,
/// and every node says which of the two it is.
/// </summary>
public static class TimingTree
{
    public static readonly string[] SortKeys = { "inclusive", "self", "childCount", "index", "topStart" };

    /// <summary>Canonical sort key for a case-insensitive spelling, or null when unknown.</summary>
    public static string? NormalizeSortBy(string? sortBy)
        => string.IsNullOrWhiteSpace(sortBy) ? "inclusive" : SortKeys.FirstOrDefault(k => k.Equals(sortBy.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Builds one node per event of <paramref name="events"/> (indexed by event index) plus the queue totals.</summary>
    public static TimingTreeResult Build(EventRecord[] events, IEnumerable<EventTimingRow> rows, int queueIndex = 0)
    {
        int n = events.Length;
        EventTimingRow[] rowList = rows as EventTimingRow[] ?? rows.ToArray();
        var measured = new ulong?[n];
        var topStart = new ulong?[n];
        var eopStart = new ulong?[n];
        var eopEnd = new ulong?[n];
        foreach (EventTimingRow row in rowList)
        {
            if (row.Index >= n || row.EopDuration == GpuCaptureHandle.TimingNone) continue;
            measured[row.Index] = (measured[row.Index] ?? 0) + row.EopDuration;
            eopStart[row.Index] = row.EopStart;
            eopEnd[row.Index] = row.EopStart + row.EopDuration;
            topStart[row.Index] = row.TopStart == GpuCaptureHandle.TimingNone ? null : row.TopStart;
        }

        var children = new List<int>[n];
        var roots = new List<int>();
        var repaired = new bool[n];
        for (int i = 0; i < n; i++)
        {
            uint parent = events[i].ParentIndex;
            if (parent != uint.MaxValue && parent < n && parent != i)
            {
                (children[parent] ??= new List<int>()).Add(i);
            }
            else
            {
                if (parent != uint.MaxValue) repaired[i] = true; // out-of-range or self parent: re-rooted
                roots.Add(i);
            }
        }

        var inclusive = new ulong[n];
        var timedDescendants = new int[n];
        var semantics = new string[n];
        var state = new byte[n]; // 0 = unvisited, 1 = in progress (cycle guard), 2 = done
        var nodes = new TimingTreeNode[n];

        void Visit(int i)
        {
            if (state[i] != 0) return;
            state[i] = 1;
            ulong childSum = 0;
            int descendants = 0, untimedChildren = 0;
            bool anyMixed = false, anyMeasured = false, anyDerived = false;
            foreach (int c in children[i] ?? (IEnumerable<int>)Array.Empty<int>())
            {
                if (state[c] == 1) { repaired[c] = true; continue; } // parent link cycle: cut the back edge
                Visit(c);
                childSum += inclusive[c];
                descendants += timedDescendants[c] + (measured[c].HasValue ? 1 : 0);
                switch (semantics[c])
                {
                    case TimingSemantics.Untimed: untimedChildren++; break;
                    case TimingSemantics.Measured: anyMeasured = true; break;
                    case TimingSemantics.DerivedSum: anyDerived = true; break;
                    case TimingSemantics.Mixed: anyMixed = true; break;
                }
            }
            inclusive[i] = measured[i] ?? childSum;
            timedDescendants[i] = descendants;
            if (!measured[i].HasValue && descendants > 0)
            {
                // A derived marker spans its timed children: earliest start to latest end.
                foreach (int c in children[i]!)
                {
                    if (state[c] != 2) continue;
                    ulong? childStart = topStart[c] ?? eopStart[c];
                    if (childStart.HasValue && (!topStart[i].HasValue || childStart < topStart[i])) topStart[i] = childStart;
                    if (eopStart[c].HasValue && (!eopStart[i].HasValue || eopStart[c] < eopStart[i])) eopStart[i] = eopStart[c];
                    if (eopEnd[c].HasValue && (!eopEnd[i].HasValue || eopEnd[c] > eopEnd[i])) eopEnd[i] = eopEnd[c];
                }
            }
            semantics[i] = measured[i].HasValue ? TimingSemantics.Measured
                : descendants == 0 ? TimingSemantics.Untimed
                : anyMixed || (anyMeasured && anyDerived) ? TimingSemantics.Mixed
                : TimingSemantics.DerivedSum;
            state[i] = 2;
            EventRecord e = events[i];
            bool overflow = measured[i].HasValue && childSum > measured[i]!.Value;
            ulong? exec = measured[i].HasValue && topStart[i].HasValue && eopEnd[i].HasValue && eopEnd[i]!.Value >= topStart[i]!.Value
                ? eopEnd[i]!.Value - topStart[i]!.Value : null;
            nodes[i] = new TimingTreeNode(e.Index,
                e.ParentIndex == uint.MaxValue || e.ParentIndex >= n || e.ParentIndex == i ? null : e.ParentIndex,
                e.Name, e.ApiCallData, e.GpuId == uint.MaxValue ? null : e.GpuId,
                semantics[i], measured[i], inclusive[i], inclusive[i] > childSum ? inclusive[i] - childSum : 0, childSum,
                overflow, overflow ? childSum - measured[i]!.Value : 0,
                descendants, children[i]?.Count ?? 0, untimedChildren, repaired[i],
                topStart[i], eopStart[i], eopEnd[i], exec);
        }

        // Iterative-friendly order: roots first, then any node a cycle kept unreachable.
        foreach (int r in roots) Visit(r);
        for (int i = 0; i < n; i++) Visit(i);
        // A back edge is discovered while its target is still in progress; refresh that node so the flag is visible.
        for (int i = 0; i < n; i++)
            if (repaired[i] && !nodes[i].Repaired) nodes[i] = nodes[i] with { Repaired = true };
        int repairs = repaired.Count(r => r);
        QueueTotals totals = Metrics.Totals(queueIndex, rowList, Total(nodes), n, index => index < n && children[index] is { Count: > 0 });
        return new(nodes, totals, repairs);
    }

    /// <summary>Direct children of <paramref name="parentIndex"/> (top-level events when null) in the requested order.</summary>
    public static IEnumerable<TimingTreeNode> Children(TimingTreeNode[] nodes, uint? parentIndex, string sortBy = "inclusive")
    {
        IEnumerable<TimingTreeNode> siblings = nodes.Where(n => n.ParentIndex == parentIndex);
        return (NormalizeSortBy(sortBy) ?? throw new ArgumentException($"Unknown sortBy '{sortBy}'.", nameof(sortBy))) switch
        {
            "self" => siblings.OrderByDescending(n => n.SelfEopNs).ThenBy(n => n.Index),
            "childCount" => siblings.OrderByDescending(n => n.ChildCount).ThenBy(n => n.Index),
            "index" => siblings.OrderBy(n => n.Index),
            "topStart" => siblings.OrderBy(n => n.TopStartNs ?? n.EopStartNs ?? ulong.MaxValue).ThenBy(n => n.Index),
            _ => siblings.OrderByDescending(n => n.InclusiveEopNs).ThenBy(n => n.Index),
        };
    }

    /// <summary>Sum of the inclusive time of the top-level events (equals the queue span only when nothing overlaps).</summary>
    public static ulong Total(TimingTreeNode[] nodes)
    {
        ulong total = 0;
        foreach (TimingTreeNode n in nodes)
        {
            if (n.ParentIndex is null) total += n.InclusiveEopNs;
        }
        return total;
    }
}
