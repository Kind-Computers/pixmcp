using PixMcp.Pix.Handles;

namespace PixMcp.Pix;

/// <summary>One event of a queue with GPU time rolled up from its subtree.</summary>
/// <param name="Index">Event index within the queue.</param>
/// <param name="ParentIndex">Parent event index, or null for a top-level event.</param>
/// <param name="MeasuredEopNs">PIX's own end-of-pipe duration for this event (markers are measured as the span of their contents), or null when PIX has none.</param>
/// <param name="InclusiveEopNs">Time attributed to this event and everything beneath it: the measured value when PIX has one, otherwise the sum of the children's inclusive time.</param>
/// <param name="SelfEopNs">Inclusive time not explained by the children (a draw's own cost; for a marker the gaps and untimed work between its children).</param>
/// <param name="TimedDescendants">Number of descendants (not counting this event) with their own PIX measurement.</param>
/// <param name="ChildCount">Number of direct children.</param>
public sealed record TimingTreeNode(uint Index, uint? ParentIndex, string Name, string ApiCallData, uint? GpuId,
    ulong? MeasuredEopNs, ulong InclusiveEopNs, ulong SelfEopNs, int TimedDescendants, int ChildCount)
{
    public bool HasOwnTiming => MeasuredEopNs.HasValue;
}

/// <summary>
/// Rolls per-event GPU timing up the marker hierarchy so "which pass is slowest" is one query.
/// PIX times markers as the span of the work inside them, so a measured event is taken as-is and
/// its children are never added on top; only unmeasured markers are summed from their children.
/// </summary>
public static class TimingTree
{
    /// <summary>Builds one node per event of <paramref name="events"/> (indexed by event index).</summary>
    public static TimingTreeNode[] Build(EventRecord[] events, IEnumerable<EventTimingRow> rows)
    {
        int n = events.Length;
        var measured = new ulong?[n];
        foreach (EventTimingRow row in rows)
        {
            if (row.Index < n && row.EopDuration != GpuCaptureHandle.TimingNone)
            {
                measured[row.Index] = (measured[row.Index] ?? 0) + row.EopDuration;
            }
        }

        var children = new List<int>[n];
        var roots = new List<int>();
        for (int i = 0; i < n; i++)
        {
            uint parent = events[i].ParentIndex;
            if (parent != uint.MaxValue && parent < n && parent != i)
            {
                (children[parent] ??= new List<int>()).Add(i);
            }
            else
            {
                roots.Add(i);
            }
        }

        var inclusive = new ulong[n];
        var timedDescendants = new int[n];
        var state = new byte[n]; // 0 = unvisited, 1 = in progress (cycle guard), 2 = done
        var nodes = new TimingTreeNode[n];

        void Visit(int i)
        {
            if (state[i] != 0) return;
            state[i] = 1;
            ulong childSum = 0;
            int descendants = 0;
            foreach (int c in children[i] ?? (IEnumerable<int>)Array.Empty<int>())
            {
                if (state[c] == 1) continue; // parent link cycle: skip the back edge
                Visit(c);
                childSum += inclusive[c];
                descendants += timedDescendants[c] + (measured[c].HasValue ? 1 : 0);
            }
            inclusive[i] = measured[i] ?? childSum;
            timedDescendants[i] = descendants;
            state[i] = 2;
            EventRecord e = events[i];
            nodes[i] = new TimingTreeNode(e.Index,
                e.ParentIndex == uint.MaxValue || e.ParentIndex >= n || e.ParentIndex == i ? null : e.ParentIndex,
                e.Name, e.ApiCallData, e.GpuId == uint.MaxValue ? null : e.GpuId,
                measured[i], inclusive[i], inclusive[i] > childSum ? inclusive[i] - childSum : 0,
                descendants, children[i]?.Count ?? 0);
        }

        // Iterative-friendly order: roots first, then any node a cycle kept unreachable.
        foreach (int r in roots) Visit(r);
        for (int i = 0; i < n; i++) Visit(i);
        return nodes;
    }

    /// <summary>Direct children of <paramref name="parentIndex"/> (top-level events when null), most expensive first.</summary>
    public static IEnumerable<TimingTreeNode> Children(TimingTreeNode[] nodes, uint? parentIndex)
        => nodes.Where(n => n.ParentIndex == parentIndex).OrderByDescending(n => n.InclusiveEopNs).ThenBy(n => n.Index);

    /// <summary>Total GPU time of the queue: the inclusive time of its top-level events.</summary>
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
