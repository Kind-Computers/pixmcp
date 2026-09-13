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
public static class CountersTools
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
        ToolCallDto Next(uint? parent, int next) => new("pix_gpu_timing_tree", new
        {
            handle, queueIndex, scope = parent.HasValue ? new EventRef(handle, queueIndex, parent.Value) : null,
            markerPathPrefix = parent.HasValue ? null : prefix,
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
            : TimingTree.Children(firstLevel.Where(i => i < nodes.Length).Select(i => nodes[i]).ToArray(), null, sortBy)
                .Concat(TimingTree.Children(firstLevel.Where(i => i < nodes.Length).Select(i => nodes[i]).Where(n => n.ParentIndex is not null).ToArray(), null, sortBy))
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
            nextOffset.HasValue ? [Next(rootIndex, nextOffset.Value)] : []) { Selection = selection };
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

    private static List<CounterInfo> LoadCounters(GpuCaptureHandle h, Job? job = null)
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
    private static async Task<string> WithPreset(PixSession session, JobManager jobs, string tool, string handle, string preset, double waitSeconds,
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

    private static CounterEventRow[] CounterRows(CounterCollectionCache cache, QueueEntry queue, string handle, GpuCaptureHandle? h, CancellationToken ct)
        => cache.GetOrCreateRows(queue.Index, () =>
        {
            GpuCaptureHandle owner = h ?? throw new InvalidOperationException("Counter rows can only be materialised with the owning handle.");
            EventRecord[] events = owner.AllEvents(queue.Index);
            CounterEventRow[] rows = BulkReadback.CounterRows(new PixCounterReader(owner, cache.Data), cache.Counters, queue.Index, events, handle, ct, out CounterQueueCoverage[] coverage);
            cache.CoverageByQueue[queue.Index] = coverage;
            return rows;
        });

    [McpServerTool(Name = "pix_gpu_counters_read", Title = "Read counter rows", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Replays the capture on the local GPU if analysis is not started. Marker rows (rowKind=marker) are PIX's own measurements over the marker's span collected in separate playback rounds, never the sum of the event rows below them. Pages per-event GPU hardware counter values for one queue. Reuses decoded results (a subset of an already collected set is projected without replay); a counter set not collected yet is collected as a job first (see waitSeconds), the same job pix_gpu_counters_prepare runs explicitly.")]
    public static Task<string> CountersCollect(
        PixSession session,
        JobManager jobs,
        [Description("GPU capture handle")] string handle,
        [Description("Counter ids to collect (from pix_gpu_counters_list). Keep the set small; each set replays the capture. Exactly one of counterIds or preset.")] uint[]? counterIds = null,
        [Description(CounterPresets.NamesDescription + " Exactly one of counterIds or preset.")] string? preset = null,
        [Description("Queue index (default 0).")] int queueIndex = 0,
        [Description("First event (default 0).")] int offset = 0,
        [Description("Maximum events (default 25, max 1000).")] int limit = Paging.DefaultLimit,
        [Description(Tools.KindDescription)] string? kind = null,
        [Description("Only events whose name contains this text.")] string? nameContains = null,
        [Description("Skip events that have no data for any requested counter (default true).")] bool onlyEventsWithData = true,
        [Description(Tools.ReadyWaitDescription)] double waitSeconds = Tools.DefaultReadyWaitSeconds,
        [Description("Sort by this requested counter ID. Numeric thresholds also apply to this counter.")] uint? orderByCounterId = null,
        [Description("Sort largest first (default true).")] bool descending = true,
        [Description("Minimum numeric value of orderByCounterId, inclusive.")] decimal? minValue = null,
        [Description("Maximum numeric value of orderByCounterId, inclusive.")] decimal? maxValue = null,
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
                ids => CountersCollect(session, jobs, handle, ids, null, queueIndex, offset, limit, kind, nameContains, onlyEventsWithData, waitSeconds,
                    orderByCounterId, descending, minValue, maxValue, scope, markerPathPrefix, format, brief, topN, maxStringLength, includeProvenance, cancellationToken));
        }
        uint[] ids = NormalizeCounterIds(counterIds);
        ShapingOptions shaping = Shaping.Options(format, brief, topN, maxStringLength, offset);
        if (orderByCounterId.HasValue && !ids.Contains(orderByCounterId.Value))
            throw new PixToolException(PixErrors.Codes.InvalidArguments, "orderByCounterId must be present in counterIds.");
        if ((minValue.HasValue || maxValue.HasValue) && !orderByCounterId.HasValue)
            throw new PixToolException(PixErrors.Codes.InvalidArguments, "Numeric thresholds require orderByCounterId.");
        if (minValue > maxValue)
            throw new PixToolException(PixErrors.Codes.InvalidArguments, "minValue must not exceed maxValue.");
        ScopeSelection selection = EventScope.Resolve(session, handle, queueIndex, scope, markerPathPrefix);
        session.Get<GpuCaptureHandle>(handle).Queue(queueIndex);
        // Validate the filter before an expensive replay, even if the queue is empty.
        Tools.ValidateKind(kind);
        return Tools.RunWhenReady(session, jobs, "pix_gpu_counters_read", handle, CounterSetPreparation(handle, ids), h =>
        {
            (int o, int l) = Shaping.Window(shaping, offset, limit);
            QueueEntry queue = h.Queue(queueIndex);
            CounterCollectionCache cache = CollectCounterSet(h, ids, null);
            EventRecord[] all = h.AllEvents(queueIndex);
            int[] childCounts = h.ChildCounts(queueIndex);
            int valueIndex = orderByCounterId.HasValue ? Array.FindIndex(cache.Counters, c => c.Id == orderByCounterId.Value) : -1;
            CounterEventRow[] materialised = CounterRows(cache, queue, h.Id, h, default);
            // Leaf rows with data under each marker, one bottom-up pass over parentIndex.
            var descendants = new int[all.Length];
            foreach (CounterEventRow row in materialised)
            {
                if (!row.HasData || row.Event.Index >= all.Length || childCounts[row.Event.Index] > 0) continue;
                for (uint parent = all[row.Event.Index].ParentIndex, hops = 0; parent < all.Length && hops < all.Length; parent = all[parent].ParentIndex, hops++) descendants[parent]++;
            }
            IEnumerable<CounterEventRow> matching = materialised.Where(row =>
            {
                EventRecord e = row.Event;
                if (!Tools.Contains(e.Name, nameContains) || !Tools.MatchesKind(e, kind, e.Index < childCounts.Length && childCounts[e.Index] > 0)
                    || (onlyEventsWithData && !row.HasData)) return false;
                return selection.Contains(queueIndex, all, e.Index);
            });
            CounterEventRow[] rows = CounterQuery.ApplyNumericQuery(matching, valueIndex, descending, minValue, maxValue).ToArray();
            uint[] orderedIds = cache.Counters.Select(c => c.Id).ToArray();
            CounterValueRowDto[] page = rows.Skip(o).Take(l).Select(row =>
            {
                bool marker = row.Event.Index < all.Length && Tools.IsMarker(row.Event, childCounts[row.Event.Index] > 0);
                return new CounterValueRowDto(
                    new(handle, queueIndex, row.Event.Index), row.Event.Index, row.Event.GpuId == uint.MaxValue ? null : row.Event.GpuId,
                    row.Event.Name, EventNavigation.MarkerPath(all, row.Event.Index), CounterQuery.Values(orderedIds, row.Values),
                    marker ? "marker" : "event", marker && row.Event.Index < all.Length ? descendants[row.Event.Index] : null);
            }).ToArray();
            bool mixed = page.Any(r => r.RowKind == "marker") && page.Any(r => r.RowKind == "event");
            ToolCallDto Call(int at, int? strings) => new("pix_gpu_counters_read", new { handle, counterIds = ids, queueIndex, offset = at, limit = l, kind, nameContains,
                onlyEventsWithData, orderByCounterId, descending, minValue, maxValue, scope, markerPathPrefix, format, brief, maxStringLength = strings });
            return Shaping.Apply(page, rows.Length, o, l, shaping, RowShapes.Counters(cache.Counters), handle,
                new
                {
                    counters = CounterMetadata(cache), provenance = h.Provenance(),
                    integerEncoding = "Integers outside the JavaScript safe range are decimal strings.", scope = selection.DescribeOrNull(h),
                    collection = Collection(CounterSetKey(ids), cache), coverage = cache.CoverageByQueue.GetValueOrDefault(queueIndex),
                    vendor = h.EffectiveVendor(), notes = CompatibilityNotes.Texts("counters", h.EffectiveVendor().Vendor, PixDiscovery.Version),
                    rollupSemantics = RoundsRule,
                    rollupAvailable = materialised.Any(r => r.HasData && r.Event.Index < all.Length && Tools.IsMarker(r.Event, childCounts[r.Event.Index] > 0)),
                    warning = mixed ? "This page mixes marker rows and event rows; do not add them together (see rollupSemantics)." : null,
                },
                next => Call(next, maxStringLength), () => Call(o, Shaping.FullStringLength));
        }, waitSeconds, cancellationToken);
    }

    // ---- Occupancy and high-frequency counters (optional per hardware) ----

    [McpServerTool(Name = "pix_gpu_occupancy", Title = "GPU occupancy series", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Replays the capture on the local GPU if analysis is not started. GPU occupancy over the capture (per occupancy type and shader stage), downsampled to maxPoints. Unavailable on some hardware; returns an 'unavailable' marker instead of failing. Needs GPU analysis: started automatically as a job (see waitSeconds).")]
    public static Task<string> Occupancy(
        PixSession session,
        JobManager jobs,
        [Description("GPU capture handle")] string handle,
        [Description("Maximum sample points per series (default 200, max 5000).")] int maxPoints = 200,
        [Description(Tools.ReadyWaitDescription)] double waitSeconds = Tools.DefaultReadyWaitSeconds,
        [Description("Original point offset for lossless pages; omit for evenly spaced overview samples.")] int? pointOffset = null,
        [Description(EventScope.Description + " Reports the selection's replay-clock window (windowNs) once timing is collected; the series itself stays capture-wide.")] EventRef? scope = null,
        [Description(EventScope.PrefixDescription)] string? markerPathPrefix = null,
        [Description("Queue the scope refers to; defaults to the scope's queue.")] int? queueIndex = null,
        [Description(Tools.IncludeProvenanceDescription)] bool includeProvenance = false,
        CancellationToken cancellationToken = default)
    {
        ScopeSelection selection = EventScope.Resolve(session, handle, queueIndex, scope, markerPathPrefix);
        return Tools.RunWhenReady(session, jobs, "pix_gpu_occupancy", handle, OccupancyPreparation(handle), h =>
        {
            if (h.OptionalUnavailable.TryGetValue("occupancy", out object? unavailable)) return unavailable;
            return OccupancyCore(h, Math.Clamp(maxPoints, 2, 5000), pointOffset, selection.DescribeOrNull(h), Window(h, selection));
        }, waitSeconds, cancellationToken);
    }

    /// <summary>Replay-clock window of a restricted selection, or null when unrestricted or timing has not been collected.</summary>
    private static object? Window(GpuCaptureHandle h, ScopeSelection selection)
    {
        if (selection.IsUnrestricted) return null;
        var window = EventScope.ToTimeWindow(h, selection);
        return window is null
            ? new { state = h.Timing is null ? "timingNotCollected" : "noTimedEvents", nextCalls = new[] { new ToolCallDto("pix_gpu_timing_prepare", new { handle = h.Id }) } }
            : new { startNs = window.Value.StartNs, endNs = window.Value.EndNs, timedEvents = window.Value.TimedEvents, clock = "PIX replay clock (same clock as timing rows; alignment with sampled series is not verified)" };
    }

    private static Preparation<GpuCaptureHandle> OccupancyPreparation(string handle)
        => new("occupancy", "occupancy", $"Collect GPU occupancy for {handle}",
            h => h.OccupancyData is not null || h.OptionalUnavailable.ContainsKey("occupancy"), (h, job) =>
            {
                h.EnsureAnalysisStarted(job);
                try { CollectOccupancy(h, job); h.MarkCapability("occupancy", "supported"); }
                catch (Exception ex) when (ExplicitlyUnsupported(ex))
                {
                    h.OptionalUnavailable["occupancy"] = PixErrors.Unavailable("occupancy", ex);
                    h.MarkCapability("occupancy", "unsupported", PixErrors.Describe(ex));
                }
            });

    internal static bool ExplicitlyUnsupported(Exception ex)
        => PixErrors.HResultOf(ex) is unchecked((int)0x80004001) or unchecked((int)0x80070032) or unchecked((int)0x887A0004);

    private static void CollectOccupancy(GpuCaptureHandle h, Job job)
    {
        Guid occGuid = typeof(IPixGpuCaptureOccupancy).GUID;
        _IPixGpuCaptureAnalysis_Extensions.GetOccupancy(h.GetAnalysis(), in occGuid, out object occObj);
        var occupancy = (IPixGpuCaptureOccupancy)occObj;

        Guid colGuid = typeof(IPixCollection).GUID;
        _IPixGpuCaptureOccupancy_Extensions.GetOccupancyTypes(occupancy, in colGuid, out object typesObj);
        _IPixGpuCaptureOccupancy_Extensions.GetOccupancyStages(occupancy, in colGuid, out object stagesObj);
        var types = Interop.Items<IPixGpuCaptureOccupancyType>((IPixCollection)typesObj).ToList();
        var stages = Interop.Items<IPixGpuCaptureOccupancyStage>((IPixCollection)stagesObj).ToList();

        Guid dataGuid = typeof(IPixGpuCaptureOccupancyData).GUID;
        _IPixGpuCaptureOccupancy_Extensions.CollectOccupancy(occupancy, in dataGuid, out object dataObj);
        var data = (IPixGpuCaptureOccupancyData)dataObj;
        job.ThrowIfCancellationRequested();
        h.OccupancyData = new(data, types.ToArray(), stages.ToArray());
    }

    private static unsafe object OccupancyCore(GpuCaptureHandle h, int maxPoints, int? pointOffset, object? scope = null, object? windowNs = null)
    {
        OccupancyCache cache = h.OccupancyData!;
        var types = cache.Types; var stages = cache.Stages; var data = cache.Data;
        var series = new List<object>();
        foreach (IPixGpuCaptureOccupancyType type in types)
        {
            foreach (IPixGpuCaptureOccupancyStage stage in stages)
            {
                ulong count = 0;
                PIX_OCCUPANCY_POINT* points = null;
                try { _IPixGpuCaptureOccupancyData_Extensions.GetPoints(data, type, stage, ref count, ref points); }
                catch { continue; }
                if (count == 0 || points == null)
                {
                    continue;
                }
                var sampled = new List<object>();
                uint maxSlots = 0;
                foreach (ulong i in SampleIndices(count, maxPoints, pointOffset))
                {
                    sampled.Add(new { index = i, timeNs = points[i].TimeNanoseconds, slots = points[i].Slots });
                }
                for (ulong i = 0; i < count; i++)
                {
                    maxSlots = Math.Max(maxSlots, points[i].Slots);
                }
                series.Add(new
                {
                    type = Interop.W(type.GetName()),
                    stage = Interop.W(stage.GetName()),
                    stageAbbreviation = Interop.W(stage.GetAbbreviation()),
                    maxSlotsAvailable = type.GetMaxSlots(),
                    pointCount = count,
                    peakSlots = maxSlots,
                    points = sampled,
                    returnedPoints = sampled.Count,
                    sampling = pointOffset.HasValue ? "consecutive" : "evenlySpaced",
                    nextCalls = SampleNextCalls("pix_gpu_occupancy", h.Id, "pointOffset", count, maxPoints, pointOffset, null),
                });
            }
        }

        return new
        {
            provenance = h.Provenance(), timeOrigin = "PIX replay clock; not calibrated to application wall time",
            types = types.Select(t => new { name = Interop.W(t.GetName()), description = Interop.WOrNull(t.GetDescription()), maxSlots = t.GetMaxSlots() }).ToArray(),
            stages = stages.Select(s => new { name = Interop.W(s.GetName()), abbreviation = Interop.W(s.GetAbbreviation()) }).ToArray(),
            series,
            scope,
            windowNs,
            notes = CompatibilityNotes.Texts("occupancy", h.EffectiveVendor().Vendor, PixDiscovery.Version),
        };
    }

    [McpServerTool(Name = "pix_gpu_hf_counters", Title = "High-frequency counters", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Replays the capture on the local GPU if analysis is not started. High-frequency (sampled over time) GPU counters: lists counters/groups/sets and, when a set is chosen, collects and returns samples downsampled to maxSamples. Unavailable on some hardware. Per-event counter values come from pix_gpu_counters_read instead. Needs GPU analysis: started automatically as a job (see waitSeconds).")]
    public static Task<string> HighFrequencyCounters(
        PixSession session,
        JobManager jobs,
        [Description("GPU capture handle")] string handle,
        [Description("Counter set index to collect (omit to only list).")] int? setIndex = null,
        [Description("Maximum samples per counter (default 200, max 5000).")] int maxSamples = 200,
        [Description(Tools.ReadyWaitDescription)] double waitSeconds = Tools.DefaultReadyWaitSeconds,
        [Description("Original sample offset for lossless pages; omit for evenly spaced overview samples.")] int? sampleOffset = null,
        [Description(EventScope.Description + " Reports the selection's replay-clock window (windowNs) once timing is collected; the samples stay capture-wide.")] EventRef? scope = null,
        [Description(EventScope.PrefixDescription)] string? markerPathPrefix = null,
        [Description(Tools.IncludeProvenanceDescription)] bool includeProvenance = false,
        CancellationToken cancellationToken = default)
    {
        ScopeSelection selection = EventScope.Resolve(session, handle, null, scope, markerPathPrefix);
        return Tools.RunWhenReady(session, jobs, "pix_gpu_hf_counters", handle, HfPreparation(handle, setIndex), h =>
        {
            if (setIndex < 0) throw new PixToolException(PixErrors.Codes.InvalidArguments, "setIndex must be nonnegative.");
            if (h.OptionalUnavailable.TryGetValue("hf:" + setIndex, out object? unavailable)) return unavailable;
            return HfCore(h, setIndex, Math.Clamp(maxSamples, 2, 5000), sampleOffset, selection.DescribeOrNull(h), Window(h, selection));
        }, waitSeconds, cancellationToken);
    }

    private static Preparation<GpuCaptureHandle> HfPreparation(string handle, int? setIndex)
    {
        if (setIndex < 0) throw PixErrors.InvalidArguments("setIndex must be nonnegative.");
        string key = "hf:" + setIndex;
        return new(key, "hf-counters", $"Collect high-frequency counters for {handle}",
            h => h.OptionalUnavailable.ContainsKey(key) || (setIndex.HasValue ? h.HighFrequencyCollections.ContainsKey(setIndex.Value) : h.HighFrequencyCatalog is not null),
            (h, job) =>
            {
                h.EnsureAnalysisStarted(job);
                try
                {
                    h.HighFrequencyCatalog ??= HfCore(h, null, 2);
                    if (setIndex.HasValue) HfCore(h, setIndex, 2);
                    job.ThrowIfCancellationRequested();
                    h.MarkCapability("highFrequencyCounters", "supported");
                }
                catch (Exception ex) when (ExplicitlyUnsupported(ex))
                {
                    h.OptionalUnavailable[key] = PixErrors.Unavailable("highFrequencyCounters", ex);
                    h.MarkCapability("highFrequencyCounters", "unsupported", PixErrors.Describe(ex));
                }
            });
    }

    private static unsafe object HfCore(GpuCaptureHandle h, int? setIndex, int maxSamples, int? sampleOffset = null, object? scope = null, object? windowNs = null)
    {
        Guid hfGuid = typeof(IPixGpuCaptureHighFrequencyCounters).GUID;
        _IPixGpuCaptureAnalysis_Extensions.GetHighFrequencyCounters(h.GetAnalysis(), in hfGuid, out object hfObj);
        var hf = (IPixGpuCaptureHighFrequencyCounters)hfObj;
        Guid colGuid = typeof(IPixCollection).GUID;
        _IPixGpuCaptureHighFrequencyCounters_Extensions.GetCounters(hf, in colGuid, out object countersObj);
        _IPixGpuCaptureHighFrequencyCounters_Extensions.GetCounterGroups(hf, in colGuid, out object groupsObj);
        _IPixGpuCaptureHighFrequencyCounters_Extensions.GetGpuCounterSets(hf, in colGuid, out object setsObj);
        var counters = (IPixCollection)countersObj;
        var groups = (IPixCollection)groupsObj;
        var sets = (IPixCollection)setsObj;

        var setList = Interop.Items<IPixGpuCaptureCounterCollection>(sets).Select((s, i) => new
        {
            index = i,
            name = Interop.W(s.GetName()),
            description = Interop.WOrNull(s.GetDescription()),
            counters = Interop.Items<IPixGpuCaptureHighFrequencyCounter>(s).Select(c =>
            {
                string name = Interop.W(c.GetName()); string? description = HfDescription(c);
                UnitGuess unit = CounterUnits.Infer(name, description, "FLOAT64");
                return new { id = c.GetId(), name, description, unit = unit.Unit, unitSource = unit.UnitSource, unitConfidence = unit.UnitConfidence };
            }).ToArray(),
        }).ToArray();

        object? samples = null;
        if (setIndex.HasValue)
        {
            if (setIndex.Value < 0 || (ulong)setIndex.Value >= sets.GetCount())
            {
                throw PixErrors.InvalidReference($"setIndex {setIndex} is out of range; there are {sets.GetCount()} counter set(s).",
                    new ToolCallDto("pix_gpu_hf_counters", new { handle = h.Id }, CostHints.Cached));
            }
            if (!h.HighFrequencyCollections.TryGetValue(setIndex.Value, out HfCollectionCache? cached))
            {
                var set = sets.Get<IPixGpuCaptureCounterCollection>((ulong)setIndex.Value);
                var selected = new SelectedPixCollection(sets, (ulong)setIndex.Value);
                Guid dataGuid = typeof(IPixGpuCaptureHighFrequencyCounterData).GUID;
                _IPixGpuCaptureHighFrequencyCounters_Extensions.CollectCounterData(hf, selected, in dataGuid, out object dataObj);
                var data = (IPixGpuCaptureHighFrequencyCounterData)dataObj;
                var collected = new List<HfCounterSamples>();
                foreach (IPixGpuCaptureHighFrequencyCounter counter in Interop.Items<IPixGpuCaptureHighFrequencyCounter>(set))
                {
                    string name = Interop.W(counter.GetName());
                    ulong batchId = 0, count = 0;
                    ulong* timestamps = null;
                    double* values = null;
                    try
                    {
                        _IPixGpuCaptureHighFrequencyCounterData_Extensions.GetSamples(data, set, counter, ref batchId, ref count, ref timestamps, ref values);
                        if (count > 0 && (timestamps == null || values == null))
                            throw new InvalidOperationException("PIX returned sample counts without sample data.");
                        double min = double.MaxValue, max = double.MinValue, sum = 0;
                        for (ulong i = 0; i < count; i++)
                        {
                            double v = values[i];
                            min = Math.Min(min, v); max = Math.Max(max, v); sum += v;
                        }
                        collected.Add(new HfCounterSamples(name, counter, batchId, count,
                            count == 0 ? null : min, count == 0 ? null : max, count == 0 ? null : sum / count));
                    }
                    catch (Exception ex) { collected.Add(new HfCounterSamples(name, counter, batchId, count, null, null, null, PixErrors.Describe(ex))); }
                }
                cached = new HfCollectionCache(Interop.W(set.GetName()), data, set, collected.ToArray());
                h.HighFrequencyCollections[setIndex.Value] = cached;
            }
            samples = new
            {
                set = cached.Set,
                counters = cached.Counters.Select(c => HfSamplesDto(h, setIndex.Value, cached, c, maxSamples, sampleOffset)).ToArray(),
            };
        }

        return new
        {
            provenance = h.Provenance(), timeOrigin = "PIX replay clock; batch IDs identify sample collections",
            counterCount = counters.GetCount(),
            groups = Interop.Items<IPixGpuCaptureCounterCollection>(groups).Select(g => new { name = Interop.W(g.GetName()), count = g.GetCount() }).ToArray(),
            sets = setList,
            samples,
            scope,
            windowNs,
            notes = CompatibilityNotes.Texts("highFrequencyCounters", h.EffectiveVendor().Vendor, PixDiscovery.Version),
        };
    }

    private static string? HfDescription(IPixGpuCaptureHighFrequencyCounter counter)
    {
        try { return Interop.WOrNull(counter.GetDescription()); }
        catch (Exception) { return null; }
    }

    private static unsafe object HfSamplesDto(GpuCaptureHandle h, int setIndex, HfCollectionCache cache, HfCounterSamples counter, int maxSamples, int? sampleOffset)
    {
        if (counter.Error is not null) return new { counter = counter.Counter, unavailable = true, reason = counter.Error };
        try
        {
            ulong batchId = 0, count = 0;
            ulong* timestamps = null;
            double* values = null;
            _IPixGpuCaptureHighFrequencyCounterData_Extensions.GetSamples(cache.Data, cache.CounterSet, counter.NativeCounter,
                ref batchId, ref count, ref timestamps, ref values);
            if (count > 0 && (timestamps == null || values == null))
                throw new InvalidOperationException("PIX returned sample counts without sample data.");
            var sampled = SampleIndices(count, maxSamples, sampleOffset).Select(i => new { index = i, timeNs = timestamps[i], value = values[i] }).ToArray();
            // PIX owns the returned pointers for the lifetime of this data object.
            GC.KeepAlive(cache.Data);
            string? description = HfDescription(counter.NativeCounter);
            UnitGuess unit = CounterUnits.Infer(counter.Counter, description, "FLOAT64");
            return new
            {
                counter = counter.Counter,
                description,
                unit = unit.Unit, unitSource = unit.UnitSource, unitConfidence = unit.UnitConfidence,
                batchId,
                sampleCount = count,
                min = counter.Min,
                max = counter.Max,
                average = counter.Average,
                samples = sampled,
                returnedSamples = sampled.Length,
                sampling = sampleOffset.HasValue ? "consecutive" : "evenlySpaced",
                nextCalls = SampleNextCalls("pix_gpu_hf_counters", h.Id, "sampleOffset", count, maxSamples, sampleOffset, setIndex),
            };
        }
        catch (Exception ex) { return new { counter = counter.Counter, unavailable = true, reason = PixErrors.Describe(ex) }; }
    }

    private static IEnumerable<ulong> SampleIndices(ulong count, int limit, int? offset)
        => offset.HasValue ? Enumerable.Range(0, (int)Math.Min((ulong)limit, count > (ulong)Math.Max(0, offset.Value) ? count - (ulong)Math.Max(0, offset.Value) : 0))
            .Select(i => (ulong)Math.Max(0, offset.Value) + (ulong)i) : Sampling.Indices(count, limit);

    private static ToolCallDto[] SampleNextCalls(string tool, string handle, string offsetName, ulong count, int limit, int? offset, int? setIndex)
    {
        long next = offset.HasValue ? (long)offset.Value + limit : 0;
        if ((offset.HasValue && (ulong)next >= count) || (!offset.HasValue && count <= (ulong)limit)) return [];
        var args = new Dictionary<string, object> { ["handle"] = handle, [offsetName] = next,
            [tool == "pix_gpu_occupancy" ? "maxPoints" : "maxSamples"] = limit };
        if (setIndex.HasValue) args["setIndex"] = setIndex.Value;
        return [new(tool, args)];
    }
}
