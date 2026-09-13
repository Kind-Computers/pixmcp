using PixMcp.Pix.Handles;

namespace PixMcp.Pix;

/// <summary>One queue's cached events and, when timing was collected, its timing rows.</summary>
internal sealed record FrameQueueInput(int QueueIndex, EventRecord[] Events, EventTimingRow[]? Rows);

/// <summary>One frame: its event range on the presenting queue, the Present that ends it, and its replay-clock window when known.</summary>
internal sealed record FrameSpan(int Index, uint FirstEventIndex, uint LastEventIndex, uint? PresentEventIndex, bool Partial, ulong? WindowStartNs, ulong? WindowEndNs);

/// <summary>
/// Frames of a GPU capture delimited by Present calls. The presenting queue (most Presents, lowest index on ties) is split by
/// event index: frame k ends with the k-th Present, and events after the last Present form a partial frame. Other queues are
/// assigned by the replay-clock window between consecutive Present completions when every queue has timing (assignment
/// present); otherwise only the presenting queue is split (indexOnly). Without Presents the capture is one frame (none).
/// </summary>
internal sealed class FrameTable
{
    private readonly Dictionary<int, int[]> _frameOf;

    internal FrameTable(Dictionary<int, int[]> frameOf, int? presentQueueIndex, string assignment, IReadOnlyList<FrameSpan> frames)
    {
        _frameOf = frameOf;
        PresentQueueIndex = presentQueueIndex;
        Assignment = assignment;
        Frames = frames;
    }

    public int Count => Frames.Count;
    public int? PresentQueueIndex { get; }
    /// <summary>present (other queues by replay-clock windows), indexOnly (only the presenting queue is split) or none (no Present).</summary>
    public string Assignment { get; }
    public IReadOnlyList<FrameSpan> Frames { get; }

    /// <summary>The frame an event belongs to, or null when its queue cannot be split.</summary>
    public int? FrameOf(int queueIndex, uint eventIndex)
        => _frameOf.TryGetValue(queueIndex, out int[]? map) && eventIndex < map.Length && map[eventIndex] >= 0 ? map[eventIndex] : null;

    public bool Contains(int frame, int queueIndex, uint eventIndex) => Count <= 1 ? frame == 0 : FrameOf(queueIndex, eventIndex) == frame;
}

internal static class FrameSegmentation
{
    public static FrameTable Build(IReadOnlyList<FrameQueueInput> queues, Func<EventRecord, bool> isPresent)
    {
        var frameOf = new Dictionary<int, int[]>();
        var presents = queues.Select(q => (Queue: q, Indices: q.Events.Where(isPresent).Select(e => e.Index).Order().ToArray()))
            .Where(p => p.Indices.Length > 0).OrderByDescending(p => p.Indices.Length).ThenBy(p => p.Queue.QueueIndex).ToList();
        if (presents.Count == 0)
        {
            foreach (FrameQueueInput queue in queues) frameOf[queue.QueueIndex] = new int[queue.Events.Length];
            uint last = (uint)Math.Max(0, (queues.FirstOrDefault()?.Events.Length ?? 1) - 1);
            return new FrameTable(frameOf, null, "none", [new FrameSpan(0, 0, last, null, false, null, null)]);
        }

        FrameQueueInput main = presents[0].Queue;
        uint[] presentIndices = presents[0].Indices;
        var spans = new List<(uint First, uint Last, uint? Present, bool Partial)>();
        uint start = 0;
        foreach (uint present in presentIndices)
        {
            spans.Add((start, present, present, false));
            start = present + 1;
        }
        if (start < main.Events.Length) spans.Add((start, (uint)main.Events.Length - 1, null, true));
        var mainMap = new int[main.Events.Length];
        for (int frame = 0; frame < spans.Count; frame++)
            for (uint i = spans[frame].First; i <= spans[frame].Last && i < mainMap.Length; i++) mainMap[i] = frame;
        frameOf[main.QueueIndex] = mainMap;

        // Present completion times on the replay clock close each frame's window on the other queues.
        Dictionary<uint, EventTimingRow>? mainRows = main.Rows?.GroupBy(r => r.Index).ToDictionary(g => g.Key, g => g.First());
        var ends = new ulong[presentIndices.Length];
        bool windows = mainRows is not null && queues.All(q => q.Rows is not null);
        for (int k = 0; k < presentIndices.Length && windows; k++)
        {
            if (mainRows!.TryGetValue(presentIndices[k], out EventTimingRow? row) && row.EopDuration != GpuCaptureHandle.TimingNone)
                ends[k] = row.EopStart + row.EopDuration;
            else windows = false;
        }

        foreach (FrameQueueInput queue in queues.Where(q => q.QueueIndex != main.QueueIndex))
        {
            int[] map = Enumerable.Repeat(-1, queue.Events.Length).ToArray();
            if (windows)
            {
                foreach (EventTimingRow row in queue.Rows!)
                    if (row.EopDuration != GpuCaptureHandle.TimingNone && row.Index < map.Length) map[row.Index] = WindowOf(row.EopStart);
                // Untimed events (markers open before their work) take the frame of the next timed event, else the previous one.
                int next = -1;
                for (int i = map.Length - 1; i >= 0; i--)
                    if (map[i] >= 0) next = map[i]; else map[i] = next;
                int previous = -1;
                for (int i = 0; i < map.Length; i++)
                    if (map[i] >= 0) previous = map[i]; else map[i] = previous;
            }
            else if (spans.Count == 1) Array.Fill(map, 0);
            frameOf[queue.QueueIndex] = map;
        }

        var frames = spans.Select((s, k) => new FrameSpan(k, s.First, s.Last, s.Present, s.Partial,
            windows ? k == 0 ? 0 : ends[k - 1] : null,
            windows && k < ends.Length ? ends[k] : null)).ToArray();
        return new FrameTable(frameOf, main.QueueIndex, windows ? "present" : "indexOnly", frames);

        int WindowOf(ulong timestamp)
        {
            int low = 0, high = ends.Length;
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (ends[middle] <= timestamp) low = middle + 1; else high = middle;
            }
            return Math.Min(low, spans.Count - 1);
        }
    }
}
