namespace PixMcp.Pix;

internal enum RecordedThreadState { OnCpu, Blocked, Ready }

/// <summary>A context switch of one thread: a switch-in (with the ready timestamp it names, if any) or a switch-out (with its wait reason).</summary>
internal readonly record struct RecordedSwitch(long Timestamp, bool SwitchIn, int? WaitReason = null, long? ReadyTimestamp = null);

internal readonly record struct RecordedStateSegment(long Start, long End, RecordedThreadState State, int? WaitReason);

/// <summary>On-CPU, blocked and ready time inside a window, with blocked/ready time and wait counts per wait reason (-1 when none was recorded).</summary>
internal sealed record RecordedStateTotals(long OnCpuNs, long BlockedNs, long ReadyNs,
    IReadOnlyDictionary<(RecordedThreadState State, int Reason), (long Ns, long Count)> ByReason);

/// <summary>
/// The scheduling state of one thread rebuilt from its context switches. ContextSwitch does not record the old thread state,
/// so a switch-out with a runnable wait reason (<see cref="WaitReasons.Runnable"/>) counts as ready; any other wait is blocked
/// until the ready event named by the next switch-in and ready from then until that switch-in. Time before the first switch
/// is unknown. Pure: no SQLite types.
/// </summary>
internal sealed class RecordedThreadStates
{
    private readonly List<RecordedStateSegment> _segments;
    private readonly long[] _starts;

    private RecordedThreadStates(List<RecordedStateSegment> segments)
    {
        _segments = segments;
        _starts = segments.Select(s => s.Start).ToArray();
    }

    public IReadOnlyList<RecordedStateSegment> Segments => _segments;

    /// <param name="switches">Switches in any order; a switch-out sorts before a switch-in at the same timestamp.</param>
    /// <param name="closeAt">Where the state after the last switch ends: the analysis end, or the thread's end when earlier.</param>
    /// <param name="trailingReadyTimestamp">Ready time linked by the next switch-in beyond the window, used only to split the final wait.</param>
    public static RecordedThreadStates Build(IEnumerable<RecordedSwitch> switches, long closeAt, long? trailingReadyTimestamp = null)
    {
        var segments = new List<RecordedStateSegment>();
        bool? running = null;
        long since = 0;
        int? reason = null;
        foreach (RecordedSwitch s in switches.OrderBy(s => s.Timestamp).ThenBy(s => s.SwitchIn))
        {
            if (s.SwitchIn)
            {
                if (running == true) continue; // a missing switch-out: keep the running interval open
                if (running == false) AddWait(segments, since, s.Timestamp, reason, s.ReadyTimestamp);
                running = true;
                since = s.Timestamp;
            }
            else
            {
                if (running == false) continue; // a duplicate switch-out keeps the first wait
                if (running == true && s.Timestamp > since) segments.Add(new(since, s.Timestamp, RecordedThreadState.OnCpu, null));
                running = false;
                since = s.Timestamp;
                reason = s.WaitReason;
            }
        }
        if (running == true && closeAt > since) segments.Add(new(since, closeAt, RecordedThreadState.OnCpu, null));
        else if (running == false) AddWait(segments, since, closeAt, reason, trailingReadyTimestamp);
        return new RecordedThreadStates(segments);
    }

    private static void AddWait(List<RecordedStateSegment> segments, long from, long to, int? reason, long? readyAt)
    {
        if (to <= from) return;
        if (WaitReasons.IsRunnable(reason) || readyAt is long early && early <= from)
            segments.Add(new(from, to, RecordedThreadState.Ready, reason));
        else if (readyAt is long ready && ready < to)
        {
            segments.Add(new(from, ready, RecordedThreadState.Blocked, reason));
            segments.Add(new(ready, to, RecordedThreadState.Ready, reason));
        }
        else segments.Add(new(from, to, RecordedThreadState.Blocked, reason));
    }

    public RecordedStateTotals Integrate(long start, long end)
    {
        long onCpu = 0, blocked = 0, ready = 0;
        var byReason = new Dictionary<(RecordedThreadState State, int Reason), (long Ns, long Count)>();
        int i = Array.BinarySearch(_starts, start);
        if (i < 0) i = Math.Max(0, ~i - 1);
        for (; i < _segments.Count && _segments[i].Start < end; i++)
        {
            RecordedStateSegment segment = _segments[i];
            long overlap = Math.Min(segment.End, end) - Math.Max(segment.Start, start);
            if (overlap <= 0) continue;
            switch (segment.State)
            {
                case RecordedThreadState.OnCpu: onCpu += overlap; continue;
                case RecordedThreadState.Blocked: blocked += overlap; break;
                default: ready += overlap; break;
            }
            var key = (segment.State, segment.WaitReason ?? -1);
            byReason.TryGetValue(key, out var sum);
            byReason[key] = (sum.Ns + overlap, sum.Count + 1);
        }
        return new RecordedStateTotals(onCpu, blocked, ready, byReason);
    }
}
