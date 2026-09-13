using Microsoft.PIX;
using PixMcp.Pix.Handles;

namespace PixMcp.Pix;

/// <summary>What a scope selection resolved to, so the reader can see which subtrees a prefix matched.</summary>
public sealed record ScopeDescriptionDto(EventRef? Root, string? MarkerPathPrefix, int? QueueIndex,
    IReadOnlyList<EventRef> MatchedRoots, int MatchedRootCount, string Semantics);

/// <summary>The GPU-id range of a selection, taken from cached event metadata (no PIX calls).</summary>
/// <param name="FirstIndex">Lowest event index with a GPU id inside the selection (recording order).</param>
/// <param name="LastIndex">Highest event index with a GPU id inside the selection.</param>
/// <param name="WorkEvents">Events with a GPU id inside the selection.</param>
/// <param name="SkippedWithoutGpuId">Selected events without a GPU id (markers, state calls).</param>
public readonly record struct IndexRange(int QueueIndex, uint FirstIndex, uint LastIndex, uint FirstGpuId, uint LastGpuId,
    int WorkEvents, int SkippedWithoutGpuId, int LastQueueIndex)
{
    public object ToDto(string handle, bool swapped = false) => new
    {
        queueIndex = QueueIndex == LastQueueIndex ? QueueIndex : (int?)null,
        firstEventRef = new EventRef(handle, QueueIndex, FirstIndex),
        lastEventRef = new EventRef(handle, LastQueueIndex, LastIndex),
        firstGpuId = FirstGpuId,
        lastGpuId = LastGpuId,
        workEvents = WorkEvents,
        skippedWithoutGpuId = SkippedWithoutGpuId,
        swapped,
    };
}

/// <summary>An index range plus the live PIX_EVENT_INFO pair PIX APIs take, ordered by MomentHandle.</summary>
internal sealed record EventRange(IndexRange Range, PIX_EVENT_INFO First, PIX_EVENT_INFO Last, bool Swapped)
{
    internal object ToDto(string handle) => Range.ToDto(handle, Swapped);
}

/// <summary>
/// One resolved scope: an optional root event (event and descendants), an optional marker-path prefix
/// (every marker subtree whose path starts with it), and an optional queue. Masks are computed lazily
/// from cached event metadata; nothing here calls PIX.
/// </summary>
internal sealed class ScopeSelection
{
    private readonly Dictionary<int, bool[]> _masks = new();
    private readonly Dictionary<int, uint[]> _roots = new();

    internal ScopeSelection(string handle, int? queueIndex, EventRef? root, string? markerPathPrefix, string[]? prefixSegments)
    {
        Handle = handle;
        QueueIndex = queueIndex;
        Root = root;
        MarkerPathPrefix = markerPathPrefix;
        PrefixSegments = prefixSegments;
    }

    internal string Handle { get; }
    internal int? QueueIndex { get; }
    internal EventRef? Root { get; }
    internal string? MarkerPathPrefix { get; }
    internal string[]? PrefixSegments { get; }

    /// <summary>True when neither a root nor a prefix restricts the selection (a queue alone is not a restriction).</summary>
    internal bool IsUnrestricted => Root is null && PrefixSegments is null;

    /// <summary>Queues the selection can touch, given the capture's queue count.</summary>
    internal IEnumerable<int> Queues(int queueCount)
        => QueueIndex.HasValue ? [QueueIndex.Value] : Root is not null ? [Root.QueueIndex] : Enumerable.Range(0, queueCount);

    internal IEnumerable<int> Queues(GpuCaptureHandle h) => Queues(h.Queues.Count);

    /// <summary>Per-event membership for one queue (cached per selection).</summary>
    internal bool[] Mask(int queueIndex, EventRecord[] events)
    {
        if (_masks.TryGetValue(queueIndex, out bool[]? cached)) return cached;
        var mask = new bool[events.Length];
        var roots = new List<uint>();
        bool queueApplies = (Root is null || Root.QueueIndex == queueIndex) && (!QueueIndex.HasValue || QueueIndex.Value == queueIndex);
        if (queueApplies)
        {
            string[][]? paths = PrefixSegments is null ? null : FullPaths(events);
            for (uint i = 0; i < events.Length; i++)
            {
                if (Root is not null && !EventNavigation.IsWithin(events, i, Root.EventIndex)) continue;
                if (paths is null) { mask[i] = true; continue; }
                if (!StartsWith(paths[i], PrefixSegments!)) continue;
                mask[i] = true;
                if (paths[i].Length == PrefixSegments!.Length) roots.Add(i);
            }
        }
        _masks[queueIndex] = mask;
        _roots[queueIndex] = roots.ToArray();
        return mask;
    }

    /// <summary>Indices of the marker subtrees the prefix matched on one queue (empty without a prefix).</summary>
    internal uint[] MatchedRoots(int queueIndex, EventRecord[] events)
    {
        Mask(queueIndex, events);
        return _roots[queueIndex];
    }

    internal bool Contains(int queueIndex, EventRecord[] events, uint eventIndex)
        => IsUnrestricted ? (!QueueIndex.HasValue || QueueIndex.Value == queueIndex) : eventIndex < events.Length && Mask(queueIndex, events)[eventIndex];

    internal bool Contains(GpuCaptureHandle h, int queueIndex, uint eventIndex)
        => IsUnrestricted ? (!QueueIndex.HasValue || QueueIndex.Value == queueIndex) : Contains(queueIndex, h.AllEvents(queueIndex), eventIndex);

    internal bool Contains(GpuCaptureHandle h, EventRef? candidate)
        => IsUnrestricted || candidate is not null && candidate.Handle == Handle && Contains(h, candidate.QueueIndex, candidate.EventIndex);

    /// <summary>Every matched prefix root across the candidate queues, as event references.</summary>
    internal List<EventRef> MatchedRoots(int queueCount, Func<int, EventRecord[]> events)
        => PrefixSegments is null ? []
            : Queues(queueCount).SelectMany(q => MatchedRoots(q, events(q)).Select(i => new EventRef(Handle, q, i))).ToList();

    internal ScopeDescriptionDto Describe(int queueCount, Func<int, EventRecord[]> events)
    {
        List<EventRef> roots = MatchedRoots(queueCount, events);
        string semantics = Root is not null && PrefixSegments is not null
            ? "marker subtrees under the root event whose path starts with the prefix"
            : Root is not null ? "event and descendants"
            : PrefixSegments is not null ? "all subtrees whose marker path starts with the prefix"
            : "unrestricted";
        return new(Root, MarkerPathPrefix, QueueIndex, roots.Take(10).ToArray(), roots.Count, semantics);
    }

    internal ScopeDescriptionDto Describe(GpuCaptureHandle h) => Describe(h.Queues.Count, h.AllEvents);

    /// <summary>The description when something restricts the selection; null otherwise so unrestricted pages stay small.</summary>
    internal ScopeDescriptionDto? DescribeOrNull(GpuCaptureHandle h) => IsUnrestricted ? null : Describe(h);

    private static string[][] FullPaths(EventRecord[] events)
    {
        var paths = new string[events.Length][];
        for (uint i = 0; i < events.Length; i++)
        {
            string[] ancestors = EventNavigation.MarkerPath(events, i);
            var path = new string[ancestors.Length + 1];
            ancestors.CopyTo(path, 0);
            path[^1] = events[i].Name;
            paths[i] = path;
        }
        return paths;
    }

    private static bool StartsWith(string[] path, string[] prefix)
    {
        if (path.Length < prefix.Length) return false;
        for (int i = 0; i < prefix.Length; i++)
            if (!string.Equals(path[i].Trim(), prefix[i], StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}

/// <summary>The single scope engine: <c>scope</c> (event and descendants) plus <c>markerPathPrefix</c> on every GPU analysis tool.</summary>
internal static class EventScope
{
    internal const string Description = "Restrict to this event and its descendants ({handle, queueIndex, eventIndex}); must belong to handle and any selected queue. Combine with markerPathPrefix to narrow further.";
    internal const string PrefixDescription = "Restrict to every marker subtree whose path (ancestor names plus own name, '/'-joined, case-insensitive) starts with this prefix, e.g. 'Frame/Shadow'. Repeated marker names select every matching subtree; extra.scope.matchedRootCount says how many.";

    /// <summary>Validates identities and the prefix syntax without touching PIX; membership is computed lazily inside queries and jobs.</summary>
    internal static ScopeSelection Resolve(PixSession session, string handle, int? queueIndex, EventRef? scope, string? markerPathPrefix)
    {
        ValidateIdentity(handle, queueIndex, scope);
        if (scope is not null) ReferenceValidation.Event(session, scope);
        string[]? segments = ParsePrefix(markerPathPrefix);
        return new(handle, queueIndex, scope, segments is null ? null : markerPathPrefix!.Trim(), segments);
    }

    /// <summary>Splits a prefix into trimmed segments; blank or empty-segment prefixes are invalid_arguments.</summary>
    internal static string[]? ParsePrefix(string? markerPathPrefix)
    {
        if (markerPathPrefix is null) return null;
        string text = markerPathPrefix.Trim();
        if (text.EndsWith('/')) text = text[..^1];
        if (text.Length == 0)
            throw new PixToolException(PixErrors.Codes.InvalidArguments, "markerPathPrefix must name at least one marker segment, e.g. 'Frame' or 'Frame/Shadow'.");
        string[] segments = text.Split('/').Select(s => s.Trim()).ToArray();
        if (segments.Any(string.IsNullOrEmpty))
            throw new PixToolException(PixErrors.Codes.InvalidArguments, "markerPathPrefix contains an empty segment; separate marker names with single '/' characters.");
        return segments;
    }

    internal static int? ResolveQueue(PixSession session, string handle, int? queueIndex, EventRef? scope)
    {
        ValidateIdentity(handle, queueIndex, scope);
        if (scope is not null) ReferenceValidation.Event(session, scope);
        return queueIndex ?? scope?.QueueIndex;
    }

    internal static void ValidateIdentity(string handle, int? queueIndex, EventRef? scope)
    {
        if (scope is not null && (scope.Handle != handle || queueIndex.HasValue && queueIndex != scope.QueueIndex))
            throw new PixToolException(PixErrors.Codes.InvalidReference, "scope must belong to the selected capture and queue.",
                nextCalls: [new("pix_gpu_queues", new { handle })]);
    }

    internal static bool Contains(GpuCaptureHandle capture, EventRef? candidate, EventRef? scope)
        => scope is null || candidate is not null && candidate.Handle == scope.Handle &&
            candidate.QueueIndex == scope.QueueIndex &&
            EventNavigation.IsWithin(capture.AllEvents(scope.QueueIndex), candidate.EventIndex, scope.EventIndex);

    /// <summary>The one queue a range consumer can use: the explicit queue, the root's queue, or the single queue a prefix matched on.</summary>
    internal static int SingleQueue(ScopeSelection sel, int queueCount, Func<int, EventRecord[]> events)
    {
        if (sel.QueueIndex.HasValue) return sel.QueueIndex.Value;
        if (sel.Root is not null) return sel.Root.QueueIndex;
        if (sel.PrefixSegments is not null)
        {
            int[] queues = Enumerable.Range(0, queueCount).Where(q => sel.MatchedRoots(q, events(q)).Length > 0).ToArray();
            if (queues.Length == 1) return queues[0];
            if (queues.Length == 0)
                throw new PixToolException(PixErrors.Codes.InvalidReference, $"markerPathPrefix '{sel.MarkerPathPrefix}' matches no marker in this capture.",
                    nextCalls: [new("pix_gpu_events", new { handle = sel.Handle, kind = "marker", nameContains = sel.PrefixSegments[^1], limit = 25 })]);
            throw new PixToolException(PixErrors.Codes.InvalidArguments,
                $"markerPathPrefix '{sel.MarkerPathPrefix}' matches markers on queues {string.Join(", ", queues)}; pass queueIndex to pick one.",
                nextCalls: queues.Select(q => new ToolCallDto("pix_gpu_events", new { handle = sel.Handle, queueIndex = q, markerPathPrefix = sel.MarkerPathPrefix, kind = "marker", limit = 25 })).ToArray());
        }
        throw new PixToolException(PixErrors.Codes.InvalidArguments, "Pass scope, markerPathPrefix or queueIndex to select an event range.",
            nextCalls: [new("pix_gpu_overview", new { handle = sel.Handle })]);
    }

    /// <summary>GPU-id range of the selection on its single queue; pure over cached events.</summary>
    internal static IndexRange ToIndexRange(ScopeSelection sel, int queueCount, Func<int, EventRecord[]> events)
    {
        int queue = SingleQueue(sel, queueCount, events);
        EventRecord[] all = events(queue);
        uint? first = null, last = null;
        uint minGpu = uint.MaxValue, maxGpu = 0;
        int work = 0, skipped = 0;
        for (uint i = 0; i < all.Length; i++)
        {
            if (!sel.Contains(queue, all, i)) continue;
            if (all[i].GpuId == uint.MaxValue) { skipped++; continue; }
            work++;
            first ??= i;
            last = i;
            minGpu = Math.Min(minGpu, all[i].GpuId);
            maxGpu = Math.Max(maxGpu, all[i].GpuId);
        }
        if (work == 0)
            throw new PixToolException(PixErrors.Codes.InvalidReference, "The selected scope contains no GPU events (draws, dispatches, copies, clears or barriers).",
                nextCalls:
                [
                    new("pix_gpu_events", new { handle = sel.Handle, queueIndex = queue, scope = sel.Root, markerPathPrefix = sel.MarkerPathPrefix, kind = "work", limit = 25 }),
                    new("pix_gpu_events", new { handle = sel.Handle, queueIndex = queue, kind = "marker", limit = 25 }),
                ]);
        return new(queue, first!.Value, last!.Value, minGpu, maxGpu, work, skipped, queue);
    }

    internal static IndexRange ToIndexRange(GpuCaptureHandle h, ScopeSelection sel) => ToIndexRange(sel, h.Queues.Count, h.AllEvents);

    /// <summary>Index range plus exactly two live PIX_EVENT_INFO reads, ordered so that PIX's MomentHandle precondition holds.</summary>
    internal static EventRange ToEventRange(GpuCaptureHandle h, ScopeSelection sel)
        => ToEventRange(h, ToIndexRange(h, sel));

    internal static EventRange ToEventRange(GpuCaptureHandle h, IndexRange range)
    {
        PIX_EVENT_INFO first = h.EventInfo(range.QueueIndex, range.FirstIndex);
        PIX_EVENT_INFO last = h.EventInfo(range.LastQueueIndex, range.LastIndex);
        bool swapped = OrderByMoment(ref first, ref last);
        if (swapped)
            range = range with { FirstIndex = range.LastIndex, LastIndex = range.FirstIndex, QueueIndex = range.LastQueueIndex, LastQueueIndex = range.QueueIndex };
        return new(range, first, last, swapped);
    }

    /// <summary>PIX requires last.MomentHandle >= first.MomentHandle; swaps the pair when inverted and reports it.</summary>
    internal static bool OrderByMoment(ref PIX_EVENT_INFO first, ref PIX_EVENT_INFO last)
    {
        if (last.MomentHandle >= first.MomentHandle) return false;
        (first, last) = (last, first);
        return true;
    }

    /// <summary>The whole capture: the lowest and highest GPU ids across every queue, from the caches.</summary>
    internal static EventRange WholeCapture(GpuCaptureHandle h)
    {
        int? firstQueue = null, lastQueue = null;
        uint firstIndex = 0, lastIndex = 0, minGpu = uint.MaxValue, maxGpu = 0;
        int work = 0, skipped = 0;
        foreach (QueueEntry queue in h.Queues)
        {
            foreach (EventRecord e in h.AllEvents(queue.Index))
            {
                if (e.GpuId == uint.MaxValue) { skipped++; continue; }
                work++;
                if (firstQueue is null || e.GpuId < minGpu) { minGpu = e.GpuId; firstQueue = queue.Index; firstIndex = e.Index; }
                if (lastQueue is null || e.GpuId > maxGpu) { maxGpu = e.GpuId; lastQueue = queue.Index; lastIndex = e.Index; }
            }
        }
        if (firstQueue is null)
            throw new PixToolException(PixErrors.Codes.InvalidReference, "The capture contains no GPU events.", nextCalls: [new("pix_gpu_queues", new { handle = h.Id })]);
        return ToEventRange(h, new IndexRange(firstQueue.Value, firstIndex, lastIndex, minGpu, maxGpu, work, skipped, lastQueue!.Value));
    }

    /// <summary>Replay-clock window covered by the selection's timed events; null until timing is collected or when nothing is timed.</summary>
    internal static (ulong StartNs, ulong EndNs, int TimedEvents)? ToTimeWindow(ScopeSelection sel, int queueCount,
        Func<int, EventRecord[]> events, IReadOnlyDictionary<int, EventTimingRow[]> rowsByQueue)
    {
        ulong? start = null, end = null;
        int timed = 0;
        foreach (int q in sel.Queues(queueCount))
        {
            if (!rowsByQueue.TryGetValue(q, out EventTimingRow[]? rows)) continue;
            foreach (EventTimingRow row in rows)
            {
                if (row.EopDuration == GpuCaptureHandle.TimingNone) continue;
                if (!sel.Contains(q, events(q), row.Index)) continue;
                ulong rowStart = row.TopStart != GpuCaptureHandle.TimingNone && row.TopStart <= row.EopStart ? row.TopStart : row.EopStart;
                ulong rowEnd = row.EopStart + row.EopDuration;
                start = start.HasValue ? Math.Min(start.Value, rowStart) : rowStart;
                end = end.HasValue ? Math.Max(end.Value, rowEnd) : rowEnd;
                timed++;
            }
        }
        return timed == 0 ? null : (start!.Value, end!.Value, timed);
    }

    internal static (ulong StartNs, ulong EndNs, int TimedEvents)? ToTimeWindow(GpuCaptureHandle h, ScopeSelection sel)
        => h.Timing is null ? null : ToTimeWindow(sel, h.Queues.Count, h.AllEvents, h.TimingRowsByQueue);
}
