using System.ComponentModel;
using System.Text.Json;
using Microsoft.PIX;
using Microsoft.PIX.Extension;
using Microsoft.PIX.Extension.GpuCapture;
using Microsoft.PIX.Extension.GpuCapture.Analysis;
using Microsoft.PIX.Extension.GpuCapture.Analysis.Counters;
using Microsoft.PIX.Extension.GpuCapture.Analysis.Timing;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using PixMcp.Pix.Handles;

namespace PixMcp.Tools;

[McpServerToolType]
public static partial class CountersTools
{
    // ---- Per-event GPU timing ----

    [McpServerTool(Name = "pix_gpu_timing_prepare", Title = "Prepare GPU timing", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), Description("Collects per-event GPU timing (top-of-pipe and end-of-pipe start/duration in ns) for the whole capture by replaying it. Returns a job; the result is a summary with the slowest events per queue. Starts analysis if needed. pix_gpu_timing_events does this implicitly; call this first on big captures so the wait is visible.")]
    public static Task<string> TimingCollect(
        PixSession session,
        JobManager jobs,
        [Description("GPU capture handle")] string handle,
        [Description(Tools.WaitSecondsDescription)] double waitSeconds = 0,
        CancellationToken cancellationToken = default)
        => Tools.RunJob(jobs, "pix_gpu_timing_prepare", () =>
            // The explicit tool returns the canonical timing job: it joins a running timing job, never a related one.
            Tools.StartPreparation(session, jobs, handle, TimingPreparation(handle) with { JoinKeys = [] }), waitSeconds, cancellationToken);

    /// <summary>Preparation for tools that need per-event timing rows; its job result is the timing summary whoever started it.</summary>
    internal static Preparation<GpuCaptureHandle> TimingPreparation(string handle)
    {
        // The verify flag is read here, on the caller's context, because the job body runs on the worker thread.
        bool verify = ServerOptions.Current.VerifyBulkReadback;
        return new(PreparationKeys.Timing, "timing", $"Collect GPU timing for {handle}", h => h.Timing is not null, (h, job) => CollectTiming(h, job, verify))
        { JoinKeys = PreparationKeys.CollectingTiming, Result = TimingSummary };
    }

    /// <summary>One shared counter-set job collects once and materializes every queue; a materialised superset satisfies a subset without replay.</summary>
    internal static Preparation<GpuCaptureHandle> CounterSetPreparation(string handle, uint[] ids)
    {
        string key = CounterSetKey(ids);
        return SharedCounterPreparation<GpuCaptureHandle>(handle, key, h => h.Queues.Select(queue => queue.Index).ToArray(),
            h => h.CounterCollections.GetValueOrDefault(key) ?? MaterialisedSuperset(h, ids)?.Cache,
            (h, job) => CollectCounterSet(h, ids, job),
            (h, cache, queueIndex, job) => CounterRows(cache, h.Queue(queueIndex), h.Id, h, job.Cancellation.Token))
            with { Result = h => CounterSetSummary(h, ids) };
    }

    /// <summary>The smallest already-collected set that contains <paramref name="ids"/> and has rows for every queue.</summary>
    internal static (string Key, CounterCollectionCache Cache)? MaterialisedSuperset(GpuCaptureHandle h, uint[] ids)
    {
        (string Key, CounterCollectionCache Cache)? best = null;
        foreach ((string key, CounterCollectionCache cache) in h.CounterCollections)
        {
            if (cache.Source != CounterCollectionCache.Exact || !cache.Covers(ids) || !h.Queues.All(q => cache.RowsByQueue.ContainsKey(q.Index))) continue;
            if (best is null || cache.Counters.Length < best.Value.Cache.Counters.Length) best = (key, cache);
        }
        return best;
    }

    /// <summary>The canonical payload of a counter-set job: counter metadata and per-queue row counts.</summary>
    private static object CounterSetSummary(GpuCaptureHandle h, uint[] ids)
    {
        CounterCollectionCache cache = h.CounterCollections[CounterSetKey(ids)];
        var queues = new List<object>();
        foreach (QueueEntry queue in h.Queues)
        {
            CounterEventRow[] rows = cache.RowsByQueue[queue.Index];
            queues.Add(new { queueIndex = queue.Index, eventCount = rows.Length, dataEventCount = rows.Count(r => r.HasData) });
        }
        return new { handle = h.Id, counters = CounterMetadata(cache), collection = Collection(CounterSetKey(ids), cache), queues,
            coverage = cache.CoverageByQueue.ToDictionary(kv => kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), kv => kv.Value) };
    }

    /// <summary>Where a set's rows came from: its own replay, or a projection of a larger collected set.</summary>
    private static object Collection(string key, CounterCollectionCache cache)
        => new { key, source = cache.Source, supersetKey = cache.SupersetKey, replayed = cache.Source == CounterCollectionCache.Exact };

    public const string RoundsRule = "Marker rows are PIX's own measurements over the marker's span, collected in separate playback rounds; they are never the sum of the event rows below them, and summing event rows into a marker is not PIX's rollup.";

    internal static Preparation<T> SharedCounterPreparation<T>(string handle, string key,
        Func<T, int[]> queueIndices, Func<T, CounterCollectionCache?> current,
        Func<T, Job, CounterCollectionCache> collect, Action<T, CounterCollectionCache, int, Job> materialize) where T : PixHandle
        => new("counters:" + key, "counters", $"Collect GPU counters [{key}] for {handle}",
            h => current(h) is { } cache && queueIndices(h).All(cache.RowsByQueue.ContainsKey),
            (h, job) =>
            {
                CounterCollectionCache cache = collect(h, job);
                int[] queues = queueIndices(h);
                for (int i = 0; i < queues.Length; i++)
                {
                    job.ThrowIfCancellationRequested();
                    materialize(h, cache, queues[i], job);
                    job.SetProgress((float)(i + 1) / queues.Length);
                }
            });

    private static string CounterSetKey(uint[] ids) => string.Join(",", ids);

    internal static void CollectTiming(GpuCaptureHandle h, Job? job, bool verifyBulkReadback = false)
    {
        if (h.Timing is not null)
        {
            return;
        }
        h.EnsureAnalysisStarted(job);
        // Warm the event caches so metadata (queues, kinds, marker paths) can be served off the worker while timing runs.
        foreach (QueueEntry queue in h.Queues) h.AllEvents(queue.Index);
        job?.AddMessage("Collecting GPU timing...");
        var coverage = new Dictionary<int, ReadbackCoverage>();
        TimingCollection.Collect(
            () => PixApiExtensionsGpuCaptureAnalysis.CollectTiming<IPixGpuCaptureTiming>(h.GetAnalysis()),
            h.Queues,
            (timing, queue, cancellationToken) =>
            {
                var reader = new PixTimingReader(h, timing);
                EventRecord[] events = h.AllEvents(queue.Index);
                EventTimingRow[] rows = BulkReadback.TimingRows(reader, queue.Index, events, cancellationToken, out ReadbackCoverage queueCoverage);
                if (verifyBulkReadback && queueCoverage.Readback == BulkReadback.Bulk)
                {
                    ReadbackVerification verify = BulkReadback.Verify(reader, queue.Index, events, rows);
                    queueCoverage = queueCoverage with { Verify = verify };
                    job?.AddMessage($"Queue {queue.Index}: verified {verify.Sampled} bulk rows against the per-event API, {verify.Mismatches} mismatch(es){(verify.FirstMismatch is null ? "" : ": " + verify.FirstMismatch)}.");
                }
                job?.AddMessage($"Queue {queue.Index}: {queueCoverage.Readback} readback, {queueCoverage.TimedEvents} of {queueCoverage.Events} events timed" +
                    (queueCoverage.Reason is null ? "" : $" ({queueCoverage.Reason})") + (queueCoverage.FailedReads > 0 ? $", {queueCoverage.FailedReads} failed reads" : "") + ".");
                coverage[queue.Index] = queueCoverage;
                return rows;
            },
            (timing, rows) =>
            {
                h.TimingRowsByQueue = rows; h.Timing = timing;
                h.TimingReadbackByQueue.Clear();
                foreach ((int queue, ReadbackCoverage c) in coverage) h.TimingReadbackByQueue[queue] = c;
            },
            job?.Cancellation.Token ?? default);
        job?.AddMessage("Timing collected.");
    }

    /// <summary>ITimingReader over the PIX timing object; the per-event path fetches PIX_EVENT_INFO through the queue.</summary>
    private sealed class PixTimingReader(GpuCaptureHandle h, IPixGpuCaptureTiming timing) : ITimingReader
    {
        public ulong? QueueDataCount(int queueIndex, out Exception? error)
        {
            try { error = null; return PixApiExtensionsGpuCaptureTiming.GetQueueDataCount(timing, h.Queue(queueIndex).Info); }
            catch (Exception ex) { error = ex; return null; }
        }
        public TimingSample QueueData(int queueIndex, ulong index)
        {
            PIX_EVENT_TIMING t = PixApiExtensionsGpuCaptureTiming.GetQueueData(timing, h.Queue(queueIndex).Info, index);
            return new(t.TopStart, t.TopDuration, t.EopStart, t.EopDuration);
        }
        public bool HasEventData(int queueIndex, uint eventIndex)
            => PixApiExtensionsGpuCaptureTiming.HasEventData(timing, PixApiExtensionsGpuCapture.GetEvent(h.Queue(queueIndex).Info, eventIndex));
        public TimingSample EventData(int queueIndex, uint eventIndex)
        {
            PIX_EVENT_TIMING t = PixApiExtensionsGpuCaptureTiming.GetEventData(timing, PixApiExtensionsGpuCapture.GetEvent(h.Queue(queueIndex).Info, eventIndex));
            return new(t.TopStart, t.TopDuration, t.EopStart, t.EopDuration);
        }
    }

    /// <summary>ICounterReader over PIX's collected counter data.</summary>
    private sealed class PixCounterReader(GpuCaptureHandle h, IPixGpuCaptureCounterData data) : ICounterReader
    {
        public ulong? QueueDataCount(int queueIndex, out Exception? error)
        {
            try { error = null; return PixApiExtensionsGpuCaptureCounters.GetQueueDataCount(data, h.Queue(queueIndex).Info); }
            catch (Exception ex) { error = ex; return null; }
        }
        public ulong QueueData(uint counterId, int queueIndex, ulong index)
            => PixApiExtensionsGpuCaptureCounters.GetQueueData(data, counterId, h.Queue(queueIndex).Info, index);
        public bool HasEventData(uint counterId, int queueIndex, uint eventIndex)
        {
            PIX_EVENT_INFO info = PixApiExtensionsGpuCapture.GetEvent(h.Queue(queueIndex).Info, eventIndex);
            return data.HasEventData(counterId, ref info);
        }
        public ulong EventData(uint counterId, int queueIndex, uint eventIndex)
            => PixApiExtensionsGpuCaptureCounters.GetEventData(data, counterId, PixApiExtensionsGpuCapture.GetEvent(h.Queue(queueIndex).Info, eventIndex));
    }

    internal static TimingEventDto TimingRowDto(GpuCaptureHandle h, EventTimingRow r)
    {
        var (record, hasChildren) = RowEvent(h, r);
        QueueTotals totals = h.QueueTotals(r.QueueIndex);
        ulong? eopEnd = r.EopDuration == GpuCaptureHandle.TimingNone ? null : r.EopStart + r.EopDuration;
        ulong? exec = r.TopStart != GpuCaptureHandle.TimingNone && eopEnd.HasValue && eopEnd.Value >= r.TopStart ? eopEnd.Value - r.TopStart : null;
        return new(new EventRef(h.Id, r.QueueIndex, r.Index), EventNavigation.MarkerPath(h.AllEvents(r.QueueIndex), r.Index),
            r.QueueIndex, r.Index, r.GpuId == uint.MaxValue ? null : r.GpuId, r.Name, Tools.Classify(record, hasChildren),
            Metrics.Duration(r.EopDuration == GpuCaptureHandle.TimingNone ? 0 : r.EopDuration, totals),
            exec.HasValue ? Metrics.Duration(exec.Value, totals) : null,
            Ns(r.TopStart), Ns(r.TopDuration), Ns(r.EopStart), eopEnd);
    }

    /// <summary>The cached event record behind a timing row plus whether it has children, for kind matching.</summary>
    private static (EventRecord Record, bool HasChildren) RowEvent(GpuCaptureHandle h, EventTimingRow r)
    {
        EventRecord[] events = h.AllEvents(r.QueueIndex);
        int[] childCounts = h.ChildCounts(r.QueueIndex);
        EventRecord record = r.Index < events.Length ? events[r.Index] : new EventRecord(r.Index, r.GpuId, uint.MaxValue, r.Name, r.ApiCallData, 0, 0);
        return (record, r.Index < childCounts.Length && childCounts[r.Index] > 0);
    }

    private static ulong? Ns(ulong v) => v == GpuCaptureHandle.TimingNone ? null : v;

    private static object TimingSummary(GpuCaptureHandle h)
    {
        var queues = new List<object>();
        var readback = h.TimingReadbackByQueue.ToDictionary(kv => kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), kv => kv.Value);
        foreach (QueueEntry queue in h.Queues)
        {
            if (!h.TimingRowsByQueue.TryGetValue(queue.Index, out EventTimingRow[]? rows))
            {
                continue;
            }
            queues.Add(new
            {
                queueIndex = queue.Index,
                name = queue.Name,
                type = queue.Type,
                totals = h.QueueTotals(queue.Index),
                slowest = rows.Where(r => r.EopDuration != GpuCaptureHandle.TimingNone).OrderByDescending(r => r.EopDuration).Take(10)
                    .Select(r => TimingRowDto(h, r)).ToArray(),
            });
        }
        return new { handle = h.Id, provenance = h.Provenance(), denominators = Metrics.Denominators, readback, queues };
    }

    [McpServerTool(Name = "pix_gpu_timing_events", Title = "GPU timing per event", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Replays the capture on the local GPU if analysis is not started. Per-event GPU timing rows, sortable by duration so 'the N slowest draws' is one call. Timing is collected first if needed, as a job (see waitSeconds; pix_gpu_timing_prepare runs the same job explicitly).")]
    public static Task<string> TimingEvents(
        PixSession session,
        JobManager jobs,
        [Description("GPU capture handle")] string handle,
        [Description("Queue index; omit for all queues.")] int? queueIndex = null,
        [Description("First item (default 0).")] int offset = 0,
        [Description("Maximum items (default 25, max 1000).")] int limit = Paging.DefaultLimit,
        [Description("Sort key: eopDuration (default), topDuration, eopStart, index.")] string sortBy = "eopDuration",
        [Description("Sort descending (default true).")] bool descending = true,
        [Description("Only events with EOP duration >= this many nanoseconds.")] ulong minDurationNs = 0,
        [Description("Only events whose name contains this text.")] string? nameContains = null,
        [Description(Tools.KindDescription)] string? kind = null,
        [Description(Tools.ReadyWaitDescription)] double waitSeconds = Tools.DefaultReadyWaitSeconds,
        [Description(EventScope.Description)] EventRef? scope = null,
        [Description(EventScope.PrefixDescription)] string? markerPathPrefix = null,
        [Description(Shaping.FormatDescription)] string format = "objects",
        [Description(Shaping.BriefDescription)] bool brief = false,
        [Description(Shaping.TopNDescription)] int? topN = null,
        [Description(Shaping.MaxStringLengthDescription)] int? maxStringLength = null,
        [Description(Tools.IncludeProvenanceDescription)] bool includeProvenance = false,
        CancellationToken cancellationToken = default)
    {
        ScopeSelection selection = EventScope.Resolve(session, handle, queueIndex, scope, markerPathPrefix);
        ShapingOptions shaping = Shaping.Options(format, brief, topN, maxStringLength, offset);
        queueIndex ??= scope?.QueueIndex;
        GpuCaptureHandle capture = session.Get<GpuCaptureHandle>(handle);
        if (queueIndex.HasValue) capture.Queue(queueIndex.Value);
        Tools.ValidateKind(kind);
        if (sortBy?.ToLowerInvariant() is not ("eopduration" or "topduration" or "eopstart" or "index"))
            throw new PixToolException(PixErrors.Codes.InvalidArguments, "sortBy must be eopDuration, topDuration, eopStart or index.");
        return Tools.RunWhenReady(session, jobs, "pix_gpu_timing_events", handle, TimingPreparation(handle), h =>
        {
            (int o, int l) = Shaping.Window(shaping, offset, limit);

            IEnumerable<EventTimingRow> rows = queueIndex.HasValue
                ? h.TimingRowsByQueue.TryGetValue(h.Queue(queueIndex.Value).Index, out EventTimingRow[]? r) ? r : Array.Empty<EventTimingRow>()
                : h.TimingRowsByQueue.OrderBy(kv => kv.Key).SelectMany(kv => kv.Value);

            rows = rows.Where(r => r.EopDuration != GpuCaptureHandle.TimingNone && r.EopDuration >= minDurationNs && Tools.Contains(r.Name, nameContains));
            if (!selection.IsUnrestricted)
                rows = rows.Where(r => selection.Contains(h, r.QueueIndex, r.Index));
            if (!string.IsNullOrEmpty(kind))
            {
                rows = rows.Where(r => { var (record, hasChildren) = RowEvent(h, r); return Tools.MatchesKind(record, kind, hasChildren); });
            }

            Func<EventTimingRow, ulong> key = sortBy.ToLowerInvariant() switch
            {
                "eopduration" => r => r.EopDuration,
                "topduration" => r => r.TopDuration == GpuCaptureHandle.TimingNone ? 0 : r.TopDuration,
                "eopstart" => r => r.EopStart,
                "index" => r => ((ulong)r.QueueIndex << 32) | r.Index,
                _ => throw new PixToolException(PixErrors.Codes.InvalidArguments, $"Unknown sortBy '{sortBy}'. Use eopDuration, topDuration, eopStart or index."),
            };
            EventTimingRow[] sorted = (descending ? rows.OrderByDescending(key) : rows.OrderBy(key)).ToArray();
            var page = sorted.Skip(o).Take(l).Select(r => TimingRowDto(h, r)).ToList();
            ToolCallDto Call(int at, int? strings) => new("pix_gpu_timing_events", new { handle, queueIndex, offset = at, limit = l, sortBy, descending,
                minDurationNs, nameContains, kind, scope, markerPathPrefix, format, brief, maxStringLength = strings });
            return Shaping.Apply(page, sorted.Length, o, l, shaping, RowShapes.TimingEvents, handle,
                new
                {
                    provenance = h.Provenance(), denominators = Metrics.Denominators, scope = selection.DescribeOrNull(h),
                    readback = h.TimingReadbackByQueue.ToDictionary(kv => kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), kv => kv.Value),
                },
                next => Call(next, maxStringLength), () => Call(o, Shaping.FullStringLength));
        }, waitSeconds, cancellationToken);
    }

    [McpServerTool(Name = "pix_gpu_timing_tree", Title = "GPU timing marker tree", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Replays the capture on the local GPU if analysis is not started. GPU time rolled up the marker hierarchy of one queue: lists the children of the scope event (top-level events when scope is omitted) with inclusive and self time as DurationDto (ns, ms, percent of queue span, percent of queue sum, percent of parent, sibling rank), semantics (measured by PIX, derivedSum of children, mixed, or untimed), child overflow when children exceed the measured span, and timed-descendant counts. Answers 'which pass is slowest' directly; drill down by passing a child's eventRef as scope, or set depth > 1.")]
    public static Task<string> TimingTreeTool(
        PixSession session,
        JobManager jobs,
        [Description("GPU capture handle")] string handle,
        [Description("Queue index; defaults to the scope's queue, else 0.")] int? queueIndex = null,
        [Description(EventScope.Description + " Its children form the first level of the tree.")] EventRef? scope = null,
        [Description(EventScope.PrefixDescription + " The matched markers form the first level of the tree.")] string? markerPathPrefix = null,
        [Description("Levels of children to expand (default 1, max 4); limit applies per node at every level.")] int depth = 1,
        [Description("Maximum children per node (default 25, max 1000).")] int limit = Paging.DefaultLimit,
        [Description("Skip children whose inclusive time is below this many nanoseconds (default 0).")] ulong minInclusiveNs = 0,
        [Description("Skip children whose self time is below this many nanoseconds (default 0).")] ulong minSelfNs = 0,
        [Description("Sibling order: inclusive (default), self, childCount, index, topStart.")] string sortBy = "inclusive",
        [Description(Tools.ReadyWaitDescription)] double waitSeconds = Tools.DefaultReadyWaitSeconds,
        [Description("First direct child in the chosen order (default 0).")] int offset = 0,
        [Description("Maximum total nodes in the expanded tree (default 100, max 1000).")] int maxNodes = 100,
        [Description(Shaping.FormatDescription + " Table rows are the expanded tree in pre-order with depth and parentEventIndex columns.")] string format = "objects",
        [Description(Shaping.BriefDescription)] bool brief = false,
        [Description(Shaping.MaxStringLengthDescription)] int? maxStringLength = null,
        [Description(Tools.IncludeProvenanceDescription)] bool includeProvenance = false,
        CancellationToken cancellationToken = default)
    {
        ShapingOptions shaping = Shaping.Options(format, brief, null, maxStringLength, 0);
        string sort = TimingTree.NormalizeSortBy(sortBy) ?? throw new PixToolException(PixErrors.Codes.InvalidArguments,
            $"sortBy must be one of {string.Join(", ", TimingTree.SortKeys)}.",
            nextCalls: [new("pix_gpu_timing_tree", new { handle, queueIndex, scope, markerPathPrefix, depth, limit, minInclusiveNs, minSelfNs, sortBy = "inclusive", offset, maxNodes })]);
        ScopeSelection selection = EventScope.Resolve(session, handle, queueIndex, scope, markerPathPrefix);
        GpuCaptureHandle capture = session.Get<GpuCaptureHandle>(handle);
        if (queueIndex.HasValue) capture.Queue(queueIndex.Value);
        return Tools.RunWhenReady(session, jobs, "pix_gpu_timing_tree", handle, TimingPreparation(handle), h =>
        {
            // A prefix alone may pin the queue; that needs the cached events, so it resolves inside the query.
            int selectedQueue = queueIndex ?? scope?.QueueIndex ?? (selection.PrefixSegments is null ? 0 : EventScope.SingleQueue(selection, h.Queues.Count, h.AllEvents));
            uint[]? firstLevel = selection.PrefixSegments is null ? null : selection.MatchedRoots(selectedQueue, h.AllEvents(selectedQueue));
            TimingTreeDto tree = BuildTimingTree(h.Id, selectedQueue, h.TimingTreeFor(selectedQueue), scope, offset, limit, depth, maxNodes, minInclusiveNs, minSelfNs, sort,
                h.Provenance(), firstLevel, selection.DescribeOrNull(h));
            return ShapeTimingTree(tree, shaping);
        }, waitSeconds, cancellationToken);
    }

    /// <summary>Table format flattens the expanded tree in pre-order; objects format keeps the nested DTO.</summary>
    internal static object ShapeTimingTree(TimingTreeDto tree, ShapingOptions shaping)
    {
        if (!shaping.Table) return RowShapes.Finish(tree, shaping);
        List<TimingTreeRow> rows = RowShapes.Flatten(tree.Children);
        var table = (TableDto)Shaping.Apply(rows, rows.Count, 0, Math.Max(1, rows.Count), shaping, RowShapes.TimingTree, tree.Handle, new
        {
            queueIndex = tree.QueueIndex, scope = tree.Scope, sortBy = tree.SortBy, queue = tree.Queue, denominators = tree.Denominators,
            timedEvents = tree.TimedEvents, untimedEvents = tree.UntimedEvents, repairedLinks = tree.RepairedLinks,
            offset = tree.Offset, childCount = tree.ChildCount, nextOffset = tree.NextOffset, childrenTruncated = tree.ChildrenTruncated,
            returnedNodes = tree.ReturnedNodes, nodeBudgetExhausted = tree.NodeBudgetExhausted, selection = tree.Selection, provenance = tree.Provenance,
            branchNextCalls = rows.Where(r => r.Node.NextCalls.Count > 0).Select(r => new { eventIndex = r.Node.Index, nextCalls = r.Node.NextCalls }).ToArray(),
        }, null, null);
        return table with { NextCalls = tree.NextCalls };
    }

    internal static TimingTreeDto BuildTimingTree(string handle, int queueIndex, TimingTreeResult tree, EventRef? scope,
        int offset, int limit, int depth, int maxNodes, ulong minInclusiveNs, ulong minSelfNs, string sortBy, object provenance,
        uint[]? firstLevel = null, ScopeDescriptionDto? selection = null)
    {
        TimingTreeNode[] nodes = tree.Nodes;
        QueueTotals totals = tree.Totals;
        (int start, int take) = Paging.Normalize(offset, limit);
        int budget = Math.Clamp(maxNodes, 1, 1000), initialBudget = budget;
        int levels = Math.Clamp(depth, 1, 4);
        uint? rootIndex = scope?.EventIndex;
        if (rootIndex.HasValue && rootIndex.Value >= nodes.Length)
            throw new PixToolException(PixErrors.Codes.InvalidReference, $"scope is outside queue {queueIndex}, which has {nodes.Length} event(s).",
                nextCalls: [new("pix_gpu_events", new { handle, queueIndex, limit = 25 })]);
        bool Keep(TimingTreeNode c) => c.InclusiveEopNs >= minInclusiveNs && c.SelfEopNs >= minSelfNs;
        TimingTreeNode[] Siblings(uint? parent) => TimingTree.Children(nodes, parent, sortBy).Where(Keep).ToArray();
        string? prefix = selection?.MarkerPathPrefix;
        ToolCallDto Next(uint? parent, int next, bool selectionPage = false) => new("pix_gpu_timing_tree", new
        {
            handle, queueIndex, scope = parent.HasValue ? new EventRef(handle, queueIndex, parent.Value) : null,
            markerPathPrefix = selectionPage ? prefix : null,
            offset = next, limit = take, depth = levels, maxNodes = initialBudget, minInclusiveNs, minSelfNs, sortBy,
        });
        TimingBranchDto Branch(TimingTreeNode n, int remainingLevels, int rank, ulong? parentInclusive)
        {
            budget--;
            var self = new EventRef(handle, queueIndex, n.Index);
            TimingTreeNode[] candidates = Siblings(n.Index);
            var children = new List<TimingBranchDto>();
            if (remainingLevels > 1)
                foreach (TimingTreeNode child in candidates.Take(take))
                {
                    if (budget == 0) break;
                    children.Add(Branch(child, remainingLevels - 1, children.Count + 1, n.InclusiveEopNs));
                }
            bool more = children.Count < candidates.Length;
            var next = new List<ToolCallDto>();
            if (more) next.Add(Next(n.Index, children.Count));
            if (n.ChildrenExceedMeasured)
                next.Add(new("pix_gpu_timing_events", new { handle, scope = self, sortBy = "eopStart", descending = false }));
            return new(self, n.Index, n.Name, n.GpuId, n.Semantics,
                Metrics.Duration(n.InclusiveEopNs, totals, parentInclusive, rank), Metrics.Duration(n.SelfEopNs, totals, parentInclusive),
                n.MeasuredEopNs, n.ChildSumEopNs, n.ChildrenExceedMeasured, n.ChildOverflowNs, n.UntimedChildren, n.Repaired,
                n.TopStartNs, n.ExecutionNs, n.HasOwnTiming, n.TimedDescendants, candidates.Length, children, more, next);
        }
        ulong? rootInclusive = rootIndex.HasValue ? nodes[rootIndex.Value].InclusiveEopNs : null;
        TimingTreeNode[] roots = firstLevel is null ? Siblings(rootIndex)
            : TimingTree.Sort(firstLevel.Where(i => i < nodes.Length).Select(i => nodes[i]), sortBy)
                .Where(Keep).ToArray();
        var page = new List<TimingBranchDto>();
        foreach (TimingTreeNode child in roots.Skip(start).Take(take))
        {
            if (budget == 0) break;
            page.Add(Branch(child, levels, start + page.Count + 1, rootInclusive));
        }
        int? nextOffset = start + page.Count < roots.Length ? start + page.Count : null;
        return new(handle, queueIndex, scope, sortBy, totals, Metrics.Denominators, totals.TimedEvents, totals.UntimedEvents, tree.Repairs,
            start, roots.Length, page, nextOffset, nextOffset.HasValue, initialBudget - budget, budget == 0, provenance,
            nextOffset.HasValue ? [Next(rootIndex, nextOffset.Value, selectionPage: true)] : []) { Selection = selection };
    }

    // ---- GPU hardware counters ----

    [McpServerTool(Name = "pix_gpu_counters_list", Title = "List GPU counters", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Replays the capture on the local GPU if analysis is not started. Pages the GPU hardware counters available for this capture on the local GPU (id, name, description, data type, groups); extra.groups lists every group name. Filter with nameContains or group, then pass ids to pix_gpu_counters_prepare / pix_gpu_counters_read. Needs GPU analysis: started automatically as a job (see waitSeconds). For sampled-over-time counters see pix_gpu_hf_counters; for occupancy see pix_gpu_occupancy.")]
    public static Task<string> CountersList(
        PixSession session,
        JobManager jobs,
        [Description("GPU capture handle")] string handle,
        [Description("First counter (default 0).")] int offset = 0,
        [Description("Maximum counters (default 25, max 1000).")] int limit = Paging.DefaultLimit,
        [Description("Only counters whose name or description contains this text (case-insensitive).")] string? nameContains = null,
        [Description("Only counters in this group (exact name from extra.groups, case-insensitive).")] string? group = null,
        [Description(Tools.ReadyWaitDescription)] double waitSeconds = Tools.DefaultReadyWaitSeconds,
        [Description("Only the counters a named preset resolves to for this capture's vendor (see extra.presets).")] string? preset = null,
        CancellationToken cancellationToken = default)
        => Tools.RunWhenReady(session, jobs, "pix_gpu_counters_list", handle, GpuCaptureHandle.AnalysisPreparation(handle), h =>
        {
            (int o, int l) = Paging.Normalize(offset, limit);
            List<CounterInfo> counters = LoadCounters(h);
            GpuVendor vendor = h.EffectiveVendor().Vendor;
            string[] groups = counters.SelectMany(c => c.Groups).Distinct().OrderBy(g => g).ToArray();
            HashSet<uint>? presetIds = null;
            if (preset is not null)
            {
                PresetResolution resolution = CounterPresets.Resolve(vendor, preset, counters, cap: 1000)
                    ?? throw PixErrors.InvalidArguments($"Unknown preset '{preset}'. Presets: {string.Join(", ", CounterPresets.Names)}.");
                presetIds = resolution.Ids.ToHashSet();
            }
            IEnumerable<CounterInfo> matching = counters.Where(c =>
                (Tools.Contains(c.Name, nameContains) || Tools.Contains(c.Description, nameContains)) &&
                (string.IsNullOrEmpty(group) || c.Groups.Any(g => g.Equals(group, StringComparison.OrdinalIgnoreCase))) &&
                (presetIds is null || presetIds.Contains(c.Id)));
            var presets = CounterPresets.Names.Select(name => CounterPresets.Resolve(vendor, name, counters)!)
                .Select(r => new { preset = r.Preset, description = r.Description, confidence = r.Confidence, matched = r.Matches.Count,
                    counterIds = r.Ids, unmatchedPatterns = r.UnmatchedPatterns.Count, capped = r.Capped }).ToArray();
            return Paging.Collect(matching, o, l,
                c => new { id = c.Id, name = c.Name, description = c.Description, dataType = c.DataType, groups = c.Groups,
                    unit = c.Unit.Unit, unitSource = c.Unit.UnitSource, unitConfidence = c.Unit.UnitConfidence,
                    range = c.Unit.RangeMin is null && c.Unit.RangeMax is null ? null : new { min = c.Unit.RangeMin, max = c.Unit.RangeMax }, aggregationHint = c.Unit.AggregationHint },
                new { groups, counterCount = counters.Count, vendor = h.EffectiveVendor(), presets, notes = CompatibilityNotes.Texts("counters", vendor, PixDiscovery.Version) });
        }, waitSeconds, cancellationToken);

    internal static List<CounterInfo> LoadCounters(GpuCaptureHandle h, Job? job = null)
    {
        if (h.CounterList is not null)
        {
            return h.CounterList;
        }
        h.EnsureAnalysisStarted(job);
        h.Counters ??= PixApiExtensionsGpuCaptureAnalysis.GetGpuCounters(h.GetAnalysis());

        var groupsById = new Dictionary<uint, List<string>>();
        try
        {
            IPixCollection groups = PixApiExtensionsGpuCaptureCounters.GetCounterGroups(h.Counters);
            foreach (IPixGpuCounterGroupDescription group in Interop.Items<IPixGpuCounterGroupDescription>(groups))
            {
                string groupName = Interop.W(group.GetName());
                IPixCollection members = PixApiExtensionsGpuCaptureCounters.GetCounters(group);
                foreach (IPixGpuCounterDescription member in Interop.Items<IPixGpuCounterDescription>(members))
                {
                    if (!groupsById.TryGetValue(member.GetId(), out List<string>? list))
                    {
                        groupsById[member.GetId()] = list = new List<string>();
                    }
                    if (!list.Contains(groupName))
                    {
                        list.Add(groupName);
                    }
                }
            }
        }
        catch { }

        var result = new List<CounterInfo>();
        IPixCollection all = PixApiExtensionsGpuCaptureCounters.GetCounters(h.Counters);
        foreach (IPixGpuCounterDescription c in Interop.Items<IPixGpuCounterDescription>(all))
        {
            uint id = c.GetId();
            PIX_FORMAT_SPECIFIER_TYPE formatSpecifier = c.GetDataType();
            result.Add(new CounterInfo(id, Interop.W(c.GetName()), Interop.W(c.GetDescription()), Interop.FormatSpecifierName(formatSpecifier),
                groupsById.TryGetValue(id, out List<string>? g) ? g.ToArray() : Array.Empty<string>())
            {
                FormatSpecifier = formatSpecifier,
            });
        }
        h.CounterList = result;
        return result;
    }

    [McpServerTool(Name = "pix_gpu_counters_prepare", Title = "Prepare counter collection", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), Description("Collects GPU hardware counters and materializes per-event results for every queue as a job. Poll pix_job_status, then page the cached rows with pix_gpu_counters_read (which joins this job if it is still running). Each distinct counter set replays the capture once per analysis session.")]
    public static async Task<string> CountersStart(
        PixSession session,
        JobManager jobs,
        [Description("GPU capture handle")] string handle,
        [Description("Counter ids from pix_gpu_counters_list. Exactly one of counterIds or preset.")] uint[]? counterIds = null,
        [Description(CounterPresets.NamesDescription + " Exactly one of counterIds or preset.")] string? preset = null,
        [Description(Tools.WaitSecondsDescription)] double waitSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        if (preset is not null)
        {
            if (counterIds is { Length: > 0 }) throw PixErrors.InvalidArguments("Pass counterIds or preset, not both.");
            return await WithPreset(session, jobs, "pix_gpu_counters_prepare", handle, preset, waitSeconds, cancellationToken,
                ids => CountersStart(session, jobs, handle, ids, null, waitSeconds, cancellationToken)).ConfigureAwait(false);
        }
        return await Tools.RunJob(jobs, "pix_gpu_counters_prepare", () =>
            Tools.StartPreparation(session, jobs, handle, CounterSetPreparation(handle, NormalizeCounterIds(counterIds))), waitSeconds, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves a preset to counter ids on the worker (needs the counter list, so analysis is prepared first as a job),
    /// then continues with the id-based path so cache keys stay id-based. A pending or deferred hop is returned as is.
    /// </summary>
    internal static async Task<string> WithPreset(PixSession session, JobManager jobs, string tool, string handle, string preset, double waitSeconds,
        CancellationToken cancellationToken, Func<uint[], Task<string>> next)
    {
        string hop = await Tools.RunWhenReady(session, jobs, tool, handle, GpuCaptureHandle.AnalysisPreparation(handle), h => ResolvePreset(h, preset), waitSeconds, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(hop);
        if (StructuredToolResults.IsPending(document.RootElement) || document.RootElement.TryGetProperty("deferred", out _)) return hop;
        uint[] ids = document.RootElement.GetProperty("ids").EnumerateArray().Select(e => e.GetUInt32()).ToArray();
        return await next(ids).ConfigureAwait(false);
    }

    private static object ResolvePreset(GpuCaptureHandle h, string preset)
    {
        List<CounterInfo> counters = LoadCounters(h);
        PresetResolution resolution = CounterPresets.Resolve(h.EffectiveVendor().Vendor, preset, counters)
            ?? throw PixErrors.InvalidArguments($"Unknown preset '{preset}'. Presets: {string.Join(", ", CounterPresets.Names)}.");
        if (resolution.Matches.Count == 0)
            throw PixErrors.InvalidArguments($"Preset '{preset}' matches none of this capture's counters for vendor {resolution.Vendor} ({resolution.UnmatchedPatterns.Count} patterns tried). List the counters and pass counterIds.",
                [new ToolCallDto("pix_gpu_counters_list", new { handle = h.Id }, CostHints.Replay)]);
        return new { ids = resolution.Ids, preset = resolution };
    }

    internal static uint[] NormalizeCounterIds(uint[]? counterIds)
    {
        if (counterIds is null || counterIds.Length == 0)
            throw PixErrors.InvalidArguments("counterIds must contain at least one counter id.");
        return counterIds.Distinct().OrderBy(x => x).ToArray();
    }

    private static object[] CounterMetadata(CounterCollectionCache cache)
        => cache.Counters.Select(c => (object)new { id = c.Id, name = c.Name, description = c.Description, dataType = c.DataType,
            unit = c.Unit.Unit, unitSource = c.Unit.UnitSource, unitConfidence = c.Unit.UnitConfidence, aggregationHint = c.Unit.AggregationHint,
            range = c.Unit.RangeMin is null && c.Unit.RangeMax is null ? null : new { min = c.Unit.RangeMin, max = c.Unit.RangeMax } }).ToArray();

    private static CounterCollectionCache CollectCounterSet(GpuCaptureHandle h, uint[] ids, Job? job)
    {
        string key = CounterSetKey(ids);
        if (h.CounterCollections.TryGetValue(key, out CounterCollectionCache? cached)) return cached;
        if (MaterialisedSuperset(h, ids) is (string supersetKey, CounterCollectionCache superset))
        {
            // Every requested column already exists in a materialised set: copy it instead of replaying.
            CounterCollectionCache projected = superset.Project(ids, supersetKey);
            h.CounterCollections[key] = projected;
            job?.AddMessage($"Counter set [{key}] projected from the collected superset [{supersetKey}]; no replay.");
            return projected;
        }
        job?.Cancellation.Token.ThrowIfCancellationRequested();
        var known = LoadCounters(h, job).ToDictionary(c => c.Id);
        foreach (uint id in ids)
            if (!known.ContainsKey(id)) throw PixErrors.UnknownCounter(id, h.Id);
        job?.AddMessage($"Collecting {ids.Length} GPU counter(s)...");
        job?.Cancellation.Token.ThrowIfCancellationRequested();
        IPixGpuCaptureCounterData data = PixApiExtensionsGpuCaptureCounters.CollectCounters(h.Counters!, ids);
        job?.Cancellation.Token.ThrowIfCancellationRequested();
        var result = new CounterCollectionCache(data, ids.Select(id => known[id]).ToArray());
        h.CollectedCounters[key] = data;
        h.CounterCollections[key] = result;
        return result;
    }

    private static CounterEventRow[] CounterRows(CounterCollectionCache cache, QueueEntry queue, string handle, CancellationToken ct = default)
        => CounterRows(cache, queue, handle, null, ct);

    /// <summary>A cached collection's rows for one queue, materialised with one bulk readback on first use (PIX worker only).</summary>
    internal static CounterEventRow[] CachedCounterRows(GpuCaptureHandle h, CounterCollectionCache cache, int queueIndex)
        => CounterRows(cache, h.Queue(queueIndex), h.Id, h, default);

    /// <summary>The collected counter set for <paramref name="ids"/>, exact or projected from a materialised superset (PIX worker only).</summary>
    internal static CounterCollectionCache CounterSet(GpuCaptureHandle h, uint[] ids, Job? job = null) => CollectCounterSet(h, ids, job);

    private static CounterEventRow[] CounterRows(CounterCollectionCache cache, QueueEntry queue, string handle, GpuCaptureHandle? h, CancellationToken ct)
        => cache.GetOrCreateRows(queue.Index, () =>
        {
            GpuCaptureHandle owner = h ?? throw new InvalidOperationException("Counter rows can only be materialised with the owning handle.");
            EventRecord[] events = owner.AllEvents(queue.Index);
            CounterEventRow[] rows = BulkReadback.CounterRows(new PixCounterReader(owner, cache.Data), cache.Counters, queue.Index, events, handle, ct, out CounterQueueCoverage[] coverage);
            cache.CoverageByQueue[queue.Index] = coverage;
            return rows;
        });

    public static readonly string[] CounterSortKeys = ["index", "name", "eop", "counter"];

    [McpServerTool(Name = "pix_gpu_counters_read", Title = "Read counter rows", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Replays the capture on the local GPU if analysis is not started. Marker rows (rowKind=marker) are PIX's own measurements over the marker's span collected in separate playback rounds, never the sum of the event rows below them. Pages per-event GPU hardware counter values for one queue. Reuses decoded results (a subset of an already collected set is projected without replay); a counter set not collected yet is collected as a job first (see waitSeconds), the same job pix_gpu_counters_prepare runs explicitly. Rows from every queue carry the event's replay timing (includeTiming) and optional normalized and ratio columns; groupBy returns per-group counter aggregates through the rollup engine.")]
    public static Task<string> CountersCollect(
        PixSession session,
        JobManager jobs,
        [Description("GPU capture handle")] string handle,
        [Description("Counter ids to collect (from pix_gpu_counters_list). Keep the set small; each set replays the capture. Exactly one of counterIds or preset.")] uint[]? counterIds = null,
        [Description(CounterPresets.NamesDescription + " Exactly one of counterIds or preset.")] string? preset = null,
        [Description("Only this queue; omit for every queue.")] int? queueIndex = null,
        [Description("First row (default 0).")] int offset = 0,
        [Description("Maximum rows (default 25, max 1000).")] int limit = Paging.DefaultLimit,
        [Description(Tools.KindDescription)] string? kind = null,
        [Description("Only events whose name contains this text.")] string? nameContains = null,
        [Description("Skip events that have no data for any requested counter (default true).")] bool onlyEventsWithData = true,
        [Description(Tools.ReadyWaitDescription)] double waitSeconds = Tools.DefaultReadyWaitSeconds,
        [Description("Join each row's replay timing as eop and exec durations (default true; collects timing in the same job when it is missing).")] bool includeTiming = true,
        [Description("none (default), perMs (divide by the event's inclusive EOP milliseconds), perThreadGroup (divide by a dispatch's thread groups) or perPixel (divide by the pixels of a draw's largest bound render target). Percent, ratio, rate, boolean and bitmask counters are never normalized.")] string normalize = "none",
        [Description("Ratio columns: each { name, numeratorId, denominatorId } adds derived[name] = numerator / denominator per row (null when the denominator is 0 or missing); both ids must be in the collected set; at most 8.")] CounterRatioSpec[]? derived = null,
        [Description("none (default: rows) or a pix_gpu_rollup grouping (marker, markerPath, markerDepth, shader, psoKey, kind, queue, commandList, api) that returns per-group counter aggregates instead of rows.")] string groupBy = "none",
        [Description("Marker depth for groupBy=markerDepth (default 1).")] int depth = 1,
        [Description("index (default), name, eop (needs includeTiming) or counter (with sortCounterId).")] string sortBy = "index",
        [Description("Counter id to sort by when sortBy=counter; must be in the collected set.")] uint? sortCounterId = null,
        [Description("Sort descending; when omitted, eop and counter sort largest first while index and name ascend.")] bool? descending = null,
        [Description("Counter id that minValue and maxValue filter on; must be in the collected set.")] uint? filterCounterId = null,
        [Description("Minimum numeric value of filterCounterId, inclusive.")] decimal? minValue = null,
        [Description("Maximum numeric value of filterCounterId, inclusive.")] decimal? maxValue = null,
        [Description(EventScope.Description)] EventRef? scope = null,
        [Description(EventScope.PrefixDescription)] string? markerPathPrefix = null,
        [Description(Shaping.FormatDescription)] string format = "objects",
        [Description(Shaping.BriefDescription)] bool brief = false,
        [Description(Shaping.TopNDescription)] int? topN = null,
        [Description(Shaping.MaxStringLengthDescription)] int? maxStringLength = null,
        [Description(Tools.IncludeProvenanceDescription)] bool includeProvenance = false,
        CancellationToken cancellationToken = default)
    {
        if (preset is not null)
        {
            if (counterIds is { Length: > 0 }) throw PixErrors.InvalidArguments("Pass counterIds or preset, not both.");
            return WithPreset(session, jobs, "pix_gpu_counters_read", handle, preset, waitSeconds, cancellationToken,
                ids => CountersCollect(session, jobs, handle, ids, null, queueIndex, offset, limit, kind, nameContains, onlyEventsWithData, waitSeconds, includeTiming, normalize,
                    derived, groupBy, depth, sortBy, sortCounterId, descending, filterCounterId, minValue, maxValue, scope, markerPathPrefix, format, brief, topN, maxStringLength,
                    includeProvenance, cancellationToken));
        }
        uint[] ids = NormalizeCounterIds(counterIds);
        string grouping = RollupTools.Canonical(groupBy, ["none", .. Rollups.GroupBys], "groupBy");
        string normalization = RollupTools.Canonical(normalize, CounterNormalization.Modes, "normalize");
        string sort = RollupTools.Canonical(sortBy, CounterSortKeys, "sortBy");
        CounterRatioSpec[] specs = derived ?? [];
        if (grouping != "none")
        {
            if (normalization is not ("none" or "perMs")) throw PixErrors.InvalidArguments("groupBy supports normalize=none or perMs.");
            if (specs.Length > 0 || sort != "index" || sortCounterId.HasValue || filterCounterId.HasValue || minValue.HasValue || maxValue.HasValue)
                throw PixErrors.InvalidArguments("derived, sortBy, sortCounterId and value filters apply to rows; with groupBy use pix_gpu_rollup (sortBy, minPercent, minCount).",
                    [new("pix_gpu_rollup", new { handle, groupBy = grouping, metric = "counter", counterIds = ids })]);
            return RollupTools.Rollup(session, jobs, handle, grouping, depth, "counter", ids, null, queueIndex, scope, markerPathPrefix, kind, normalization, "sum", true,
                null, 1, offset, limit, format, brief, maxStringLength, waitSeconds, includeProvenance, cancellationToken);
        }
        ShapingOptions shaping = Shaping.Options(format, brief, topN, maxStringLength, offset);
        if (sort == "counter" && (!sortCounterId.HasValue || !ids.Contains(sortCounterId.Value)))
            throw PixErrors.InvalidArguments("sortBy=counter needs sortCounterId from counterIds.");
        if (sortCounterId.HasValue && sort != "counter") throw PixErrors.InvalidArguments("sortCounterId applies only to sortBy=counter.");
        if (filterCounterId.HasValue && !ids.Contains(filterCounterId.Value)) throw PixErrors.InvalidArguments("filterCounterId must be present in counterIds.");
        if ((minValue.HasValue || maxValue.HasValue) && !filterCounterId.HasValue) throw PixErrors.InvalidArguments("minValue and maxValue need filterCounterId.");
        if (minValue > maxValue) throw PixErrors.InvalidArguments("minValue must not exceed maxValue.");
        if (!includeTiming && (sort == "eop" || normalization == "perMs")) throw PixErrors.InvalidArguments("sortBy=eop and normalize=perMs need includeTiming=true.");
        if (specs.Length > 8) throw PixErrors.InvalidArguments("At most 8 derived columns.");
        foreach (CounterRatioSpec spec in specs)
        {
            if (string.IsNullOrWhiteSpace(spec.Name)) throw PixErrors.InvalidArguments("Every derived column needs a name.");
            if (!ids.Contains(spec.NumeratorId) || !ids.Contains(spec.DenominatorId))
                throw PixErrors.InvalidArguments($"Derived column {spec.Name} names a counter outside counterIds.", [new("pix_gpu_counters_list", new { handle })]);
        }
        if (specs.Select(s => s.Name).Distinct(StringComparer.Ordinal).Count() != specs.Length) throw PixErrors.InvalidArguments("Derived column names must be unique.");
        ScopeSelection selection = EventScope.Resolve(session, handle, queueIndex, scope, markerPathPrefix);
        GpuCaptureHandle capture = session.Get<GpuCaptureHandle>(handle);
        if (queueIndex.HasValue) capture.Queue(queueIndex.Value);
        // Validate the filter before an expensive replay, even if the queue is empty.
        Tools.ValidateKind(kind);
        var parts = new List<Preparation<GpuCaptureHandle>> { CounterSetPreparation(handle, ids) };
        if (includeTiming) parts.Add(TimingPreparation(handle));
        return Tools.RunWhenReady(session, jobs, "pix_gpu_counters_read", handle, Tools.Combine(handle, parts), h =>
        {
            (int o, int l) = Shaping.Window(shaping, offset, limit);
            CounterCollectionCache cache = CollectCounterSet(h, ids, null);
            uint[] orderedIds = cache.Counters.Select(c => c.Id).ToArray();
            int Column(uint? id) => id is uint value ? Array.IndexOf(orderedIds, value) : -1;
            int sortColumn = Column(sortCounterId), filterColumn = Column(filterCounterId);
            var candidates = new List<CounterCandidate>();
            var descendantsByQueue = new Dictionary<int, int[]>();
            var trees = new Dictionary<int, TimingTreeResult>();
            bool rollupAvailable = false;
            foreach (QueueEntry queue in h.Queues)
            {
                if (queueIndex.HasValue && queue.Index != queueIndex.Value) continue;
                EventRecord[] all = h.AllEvents(queue.Index);
                int[] childCounts = h.ChildCounts(queue.Index);
                if (includeTiming && h.Timing is not null) trees[queue.Index] = h.TimingTreeFor(queue.Index);
                CounterEventRow[] materialised = CounterRows(cache, queue, h.Id, h, default);
                // Leaf rows with data under each marker, one bottom-up pass over parentIndex.
                var descendants = new int[all.Length];
                foreach (CounterEventRow row in materialised)
                {
                    if (!row.HasData || row.Event.Index >= all.Length || childCounts[row.Event.Index] > 0) continue;
                    for (uint parent = all[row.Event.Index].ParentIndex, hops = 0; parent < all.Length && hops < all.Length; parent = all[parent].ParentIndex, hops++) descendants[parent]++;
                }
                descendantsByQueue[queue.Index] = descendants;
                rollupAvailable |= materialised.Any(r => r.HasData && r.Event.Index < all.Length && Tools.IsMarker(r.Event, childCounts[r.Event.Index] > 0));
                foreach (CounterEventRow row in materialised)
                {
                    EventRecord e = row.Event;
                    bool hasChildren = e.Index < childCounts.Length && childCounts[e.Index] > 0;
                    if (!Tools.Contains(e.Name, nameContains) || !Tools.MatchesKind(e, kind, hasChildren) || (onlyEventsWithData && !row.HasData)) continue;
                    if (!selection.Contains(queue.Index, all, e.Index)) continue;
                    if (filterColumn >= 0 && !CounterQuery.InRange(row.Values[filterColumn], minValue, maxValue)) continue;
                    candidates.Add(new(queue.Index, row, all, childCounts));
                }
            }

            TimingTreeNode? NodeOf(CounterCandidate c)
                => trees.TryGetValue(c.Queue, out TimingTreeResult? tree) && c.Row.Event.Index < tree.Nodes.Length && tree.Nodes[c.Row.Event.Index].IsTimed ? tree.Nodes[c.Row.Event.Index] : null;
            bool desc = descending ?? sort is "counter" or "eop";
            IEnumerable<CounterCandidate> ordered = sort switch
            {
                "counter" => (desc
                    ? candidates.OrderBy(c => CounterQuery.IsNumeric(c.Row.Values[sortColumn]) ? 0 : 1).ThenByDescending(c => c.Row.Values[sortColumn], CounterQuery.ValueComparer)
                    : candidates.OrderBy(c => CounterQuery.IsNumeric(c.Row.Values[sortColumn]) ? 0 : 1).ThenBy(c => c.Row.Values[sortColumn], CounterQuery.ValueComparer))
                    .ThenBy(c => c.Queue).ThenBy(c => c.Row.Event.Index),
                "eop" => (desc
                    ? candidates.OrderBy(c => NodeOf(c) is null ? 1 : 0).ThenByDescending(c => NodeOf(c)?.InclusiveEopNs ?? 0)
                    : candidates.OrderBy(c => NodeOf(c) is null ? 1 : 0).ThenBy(c => NodeOf(c)?.InclusiveEopNs ?? 0))
                    .ThenBy(c => c.Queue).ThenBy(c => c.Row.Event.Index),
                "name" => (desc ? candidates.OrderByDescending(c => c.Row.Event.Name, StringComparer.OrdinalIgnoreCase) : candidates.OrderBy(c => c.Row.Event.Name, StringComparer.OrdinalIgnoreCase))
                    .ThenBy(c => c.Queue).ThenBy(c => c.Row.Event.Index),
                _ => desc ? candidates.OrderByDescending(c => c.Queue).ThenByDescending(c => c.Row.Event.Index) : candidates.OrderBy(c => c.Queue).ThenBy(c => c.Row.Event.Index),
            };
            CounterCandidate[] rows = ordered.ToArray();
            CounterValueRowDto[] page = rows.Skip(o).Take(l).Select(c =>
            {
                EventRecord e = c.Row.Event;
                bool hasChildren = e.Index < c.Children.Length && c.Children[e.Index] > 0;
                bool marker = e.Index < c.All.Length && Tools.IsMarker(e, hasChildren);
                var eventRef = new EventRef(handle, c.Queue, e.Index);
                DurationDto? eop = null, exec = null;
                if (NodeOf(c) is TimingTreeNode node)
                {
                    TimingTreeResult tree = trees[c.Queue];
                    ulong? parentInclusive = node.ParentIndex is uint p && p < tree.Nodes.Length && p != node.Index && tree.Nodes[p].IsTimed ? tree.Nodes[p].InclusiveEopNs : null;
                    eop = Metrics.Duration(node.InclusiveEopNs, tree.Totals, parentInclusive);
                    exec = node.ExecutionNs is ulong executed ? Metrics.Duration(executed, tree.Totals) : null;
                }
                (double? divisor, string? reason) = CounterNormalization.Divisor(normalization, Tools.Classify(e, hasChildren), eop, ApiCallParser.Parse(e.ApiCallData, e.Name), () =>
                {
                    EventTargetsDto targets = ResourceTools.QueryBoundTargets(h, eventRef);
                    EventTargetDto? largest = targets.Targets.Where(t => t.ViewType == "renderTarget").MaxBy(t => t.PixelCount);
                    return (largest?.PixelCount ?? 0, targets.Reason);
                });
                return new CounterValueRowDto(eventRef, e.Index, e.GpuId == uint.MaxValue ? null : e.GpuId, e.Name, EventNavigation.MarkerPath(c.All, e.Index),
                    CounterQuery.Values(orderedIds, c.Row.Values), marker ? "marker" : "event", marker ? descendantsByQueue[c.Queue][e.Index] : null)
                {
                    Eop = eop,
                    Exec = exec,
                    Normalized = divisor is double d ? CounterNormalization.Normalize(cache.Counters, c.Row.Values, d) : null,
                    NormalizeReason = normalization == "none" || divisor is not null ? null : reason,
                    Derived = specs.Length == 0 ? null : specs.ToDictionary(s => s.Name, s => CounterNormalization.Ratio(c.Row.Values[Column(s.NumeratorId)], c.Row.Values[Column(s.DenominatorId)])),
                };
            }).ToArray();
            bool mixed = page.Any(r => r.RowKind == "marker") && page.Any(r => r.RowKind == "event");
            ToolCallDto Call(int at, int? strings) => new("pix_gpu_counters_read", new { handle, counterIds = ids, queueIndex, offset = at, limit = l, kind, nameContains,
                onlyEventsWithData, includeTiming, normalize = normalization, derived = specs.Length == 0 ? null : specs, sortBy = sort, sortCounterId, descending,
                filterCounterId, minValue, maxValue, scope, markerPathPrefix, format, brief, maxStringLength = strings });
            return Shaping.Apply(page, rows.Length, o, l, shaping, RowShapes.Counters(cache.Counters, specs, normalization != "none", includeTiming), handle,
                new
                {
                    counters = CounterMetadata(cache), provenance = h.Provenance(),
                    integerEncoding = "Integers outside the JavaScript safe range are decimal strings.", scope = selection.DescribeOrNull(h),
                    collection = Collection(CounterSetKey(ids), cache),
                    coverage = cache.CoverageByQueue.Where(kv => !queueIndex.HasValue || kv.Key == queueIndex.Value)
                        .ToDictionary(kv => kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), kv => kv.Value),
                    vendor = h.EffectiveVendor(), notes = CompatibilityNotes.Texts("counters", h.EffectiveVendor().Vendor, PixDiscovery.Version),
                    rollupSemantics = RoundsRule,
                    rollupAvailable,
                    warning = mixed ? "This page mixes marker rows and event rows; do not add them together (see rollupSemantics)." : null,
                    timing = includeTiming ? "eop is the event's inclusive replay EOP time (percentages divide by its queue); exec runs from TOP start to EOP end and overlaps neighbouring events." : null,
                    normalization = normalization == "none" ? null : new { mode = normalization, divisor = CounterNormalization.Describe(normalization) },
                    derived = specs.Length == 0 ? null : specs,
                },
                next => Call(next, maxStringLength), () => Call(o, Shaping.FullStringLength));
        }, waitSeconds, cancellationToken);
    }

    private sealed record CounterCandidate(int Queue, CounterEventRow Row, EventRecord[] All, int[] Children);
}
