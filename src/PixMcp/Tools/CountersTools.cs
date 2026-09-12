using System.ComponentModel;
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

    [McpServerTool(Name = "pix_gpu_timing_collect", Idempotent = true), Description("Collects per-event GPU timing (top-of-pipe and end-of-pipe start/duration in ns) for the whole capture by replaying it. Returns a job; the result is a summary with the slowest events per queue. Starts analysis if needed. pix_gpu_timing_events does this implicitly; call this first on big captures so the wait is visible.")]
    public static Task<string> TimingCollect(
        PixSession session,
        JobManager jobs,
        [Description("GPU capture handle")] string handle,
        [Description(Tools.WaitSecondsDescription)] double waitSeconds = 0,
        CancellationToken cancellationToken = default)
        => Tools.RunJob(jobs, "pix_gpu_timing_collect", () =>
        {
            Job job = jobs.StartForHandle<GpuCaptureHandle>("timing", $"Collect GPU timing for {handle}", handle, (j, h) =>
            {
                CollectTiming(h, j);
                return TimingSummary(h);
            });
            Tools.RegisterPreparation(session, handle, TimingPreparation(handle).Key, job);
            return job;
        }, waitSeconds, cancellationToken);

    /// <summary>Preparation for tools that need per-event timing rows.</summary>
    internal static Preparation<GpuCaptureHandle> TimingPreparation(string handle)
        => new("timing", "timing", $"Collect GPU timing for {handle}", h => h.Timing is not null, (h, job) => CollectTiming(h, job));

    /// <summary>One shared counter-set job collects once and materializes every queue.</summary>
    internal static Preparation<GpuCaptureHandle> CounterSetPreparation(string handle, uint[] ids)
    {
        string key = CounterSetKey(ids);
        return SharedCounterPreparation<GpuCaptureHandle>(handle, key, h => h.Queues.Select(queue => queue.Index).ToArray(),
            h => h.CounterCollections.GetValueOrDefault(key),
            (h, job) => CollectCounterSet(h, ids, job),
            (h, cache, queueIndex, job) => CounterRows(cache, h.Queue(queueIndex), job.Cancellation.Token));
    }

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

    internal static void CollectTiming(GpuCaptureHandle h, Job? job)
    {
        if (h.Timing is not null)
        {
            return;
        }
        h.EnsureAnalysisStarted(job);
        job?.AddMessage("Collecting GPU timing...");
        TimingCollection.Collect(
            () => PixApiExtensionsGpuCaptureAnalysis.CollectTiming<IPixGpuCaptureTiming>(h.GetAnalysis()),
            h.Queues, BuildTimingRows,
            (timing, rows) => { h.TimingRowsByQueue = rows; h.Timing = timing; },
            job?.Cancellation.Token ?? default);
        job?.AddMessage("Timing collected.");
    }

    private static EventTimingRow[] BuildTimingRows(IPixGpuCaptureTiming timing, QueueEntry queue, CancellationToken cancellationToken)
    {
        var rows = new List<EventTimingRow>();
        for (uint i = 0; i < queue.EventCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PIX_EVENT_INFO info = PixApiExtensionsGpuCapture.GetEvent(queue.Info, i);
            bool has = PixApiExtensionsGpuCaptureTiming.HasEventData(timing, info);
            if (!has)
            {
                continue;
            }
            PIX_EVENT_TIMING t = PixApiExtensionsGpuCaptureTiming.GetEventData(timing, info);
            rows.Add(new EventTimingRow(queue.Index, i, info.GpuId, Interop.A(info.Name), Interop.A(info.ApiCallData), t.TopStart, t.TopDuration, t.EopStart, t.EopDuration));
        }
        return rows.ToArray();
    }

    internal static TimingEventDto TimingRowDto(GpuCaptureHandle h, EventTimingRow r) => new(
        new EventRef(h.Id, r.QueueIndex, r.Index), EventNavigation.MarkerPath(h.AllEvents(r.QueueIndex), r.Index),
        r.QueueIndex, r.Index, r.GpuId == uint.MaxValue ? null : r.GpuId, r.Name,
        Ns(r.TopStart), Ns(r.TopDuration), Ns(r.EopStart), Ns(r.EopDuration));

    private static ulong? Ns(ulong v) => v == GpuCaptureHandle.TimingNone ? null : v;

    private static object TimingSummary(GpuCaptureHandle h)
    {
        var queues = new List<object>();
        foreach (QueueEntry queue in h.Queues)
        {
            if (!h.TimingRowsByQueue.TryGetValue(queue.Index, out EventTimingRow[]? rows))
            {
                continue;
            }
            EventTimingRow[] timed = rows.Where(r => r.EopDuration != GpuCaptureHandle.TimingNone).ToArray();
            ulong first = timed.Length == 0 ? 0 : timed.Min(r => r.EopStart);
            ulong last = timed.Length == 0 ? 0 : timed.Max(r => r.EopStart + r.EopDuration);
            queues.Add(new
            {
                queueIndex = queue.Index,
                name = queue.Name,
                type = queue.Type,
                timedEvents = timed.Length,
                sumEopDurationNs = timed.Length == 0 ? 0 : timed.Aggregate(0UL, (a, r) => a + r.EopDuration),
                spanNs = last - first,
                slowest = timed.OrderByDescending(r => r.EopDuration).Take(10).Select(r => TimingRowDto(h, r)).ToArray(),
            });
        }
        return new { handle = h.Id, provenance = h.Provenance(), queues };
    }

    [McpServerTool(Name = "pix_gpu_timing_events", ReadOnly = true), Description("Per-event GPU timing rows, sortable by duration so 'the N slowest draws' is one call. Timing is collected first if needed, as a job (see waitSeconds; pix_gpu_timing_collect runs the same job explicitly).")]
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
        CancellationToken cancellationToken = default)
    {
        queueIndex = EventScope.ResolveQueue(session, handle, queueIndex, scope);
        GpuCaptureHandle capture = session.Get<GpuCaptureHandle>(handle);
        if (queueIndex.HasValue) capture.Queue(queueIndex.Value);
        Tools.MatchesKind(new EventRecord(0, 0, uint.MaxValue, "", "", 0, 0), kind);
        if (sortBy?.ToLowerInvariant() is not ("eopduration" or "topduration" or "eopstart" or "index"))
            throw new PixToolException("invalid_arguments", "sortBy must be eopDuration, topDuration, eopStart or index.");
        return Tools.RunWhenReady(session, jobs, "pix_gpu_timing_events", handle, TimingPreparation(handle), h =>
        {
            (int o, int l) = Paging.Normalize(offset, limit);

            IEnumerable<EventTimingRow> rows = queueIndex.HasValue
                ? h.TimingRowsByQueue.TryGetValue(h.Queue(queueIndex.Value).Index, out EventTimingRow[]? r) ? r : Array.Empty<EventTimingRow>()
                : h.TimingRowsByQueue.OrderBy(kv => kv.Key).SelectMany(kv => kv.Value);

            rows = rows.Where(r => r.EopDuration != GpuCaptureHandle.TimingNone && r.EopDuration >= minDurationNs && Tools.Contains(r.Name, nameContains));
            if (scope is not null)
                rows = rows.Where(r => EventNavigation.IsWithin(h.AllEvents(scope.QueueIndex), r.Index, scope.EventIndex));
            if (!string.IsNullOrEmpty(kind))
            {
                rows = rows.Where(r => Tools.MatchesKind(new EventRecord(r.Index, r.GpuId, uint.MaxValue, r.Name, r.ApiCallData, 0, 0), kind));
            }

            Func<EventTimingRow, ulong> key = sortBy.ToLowerInvariant() switch
            {
                "eopduration" => r => r.EopDuration,
                "topduration" => r => r.TopDuration == GpuCaptureHandle.TimingNone ? 0 : r.TopDuration,
                "eopstart" => r => r.EopStart,
                "index" => r => ((ulong)r.QueueIndex << 32) | r.Index,
                _ => throw new McpException($"Unknown sortBy '{sortBy}'. Use eopDuration, topDuration, eopStart or index."),
            };
            EventTimingRow[] sorted = (descending ? rows.OrderByDescending(key) : rows.OrderBy(key)).ToArray();
            var page = sorted.Skip(o).Take(l).Select(r => TimingRowDto(h, r)).ToList();
            return Paging.Page(page, sorted.Length, o, l, new { provenance = h.Provenance() });
        }, waitSeconds, cancellationToken);
    }

    [McpServerTool(Name = "pix_gpu_timing_tree", ReadOnly = true), Description("GPU time rolled up the marker hierarchy of one queue: lists the children of an event (top-level events when parentIndex is omitted) with inclusive time (PIX's own measurement when it has one, since PIX times markers as the span of their contents; otherwise the sum of the children), self time (inclusive minus children), timed-descendant count and share of the queue total, most expensive first. Answers 'which pass is slowest' directly; drill down by passing a child's index as parentIndex, or set depth > 1. Timing is collected first if needed, as a job (see waitSeconds).")]
    public static Task<string> TimingTreeTool(
        PixSession session,
        JobManager jobs,
        [Description("GPU capture handle")] string handle,
        [Description("Queue index (default 0).")] int queueIndex = 0,
        [Description("Event whose children to list; omit for the top level of the queue.")] uint? parentIndex = null,
        [Description("Levels of children to expand (default 1, max 4); limit applies per node at every level.")] int depth = 1,
        [Description("Maximum children per node, most expensive first (default 25, max 1000).")] int limit = Paging.DefaultLimit,
        [Description("Skip children whose inclusive time is below this many nanoseconds (default 0).")] ulong minInclusiveNs = 0,
        [Description(Tools.ReadyWaitDescription)] double waitSeconds = Tools.DefaultReadyWaitSeconds,
        [Description("First direct child, in descending time order.")] int offset = 0,
        [Description("Maximum total nodes in the expanded tree (default 100, max 1000).")] int maxNodes = 100,
        CancellationToken cancellationToken = default)
    {
        GpuCaptureHandle capture = session.Get<GpuCaptureHandle>(handle);
        QueueEntry queue = capture.Queue(queueIndex);
        if (parentIndex.HasValue && parentIndex.Value >= queue.EventCount)
            throw new PixToolException("invalid_arguments", $"parentIndex is outside queue {queueIndex}, which has {queue.EventCount} events.");
        return Tools.RunWhenReady(session, jobs, "pix_gpu_timing_tree", handle, TimingPreparation(handle), h =>
        {
            TimingTreeNode[] nodes = h.TimingTreeNodes(queueIndex);
            if (parentIndex.HasValue && parentIndex.Value >= nodes.Length)
            {
                throw new McpException($"parentIndex {parentIndex} is out of range; queue {queueIndex} has {nodes.Length} event(s).");
            }
            return BuildTimingTree(h.Id, queueIndex, nodes, parentIndex, offset, limit, depth, maxNodes, minInclusiveNs, h.Provenance());
        }, waitSeconds, cancellationToken);
    }

    internal static TimingTreeDto BuildTimingTree(string handle, int queueIndex, TimingTreeNode[] nodes,
        uint? parentIndex, int offset, int limit, int depth, int maxNodes, ulong minInclusiveNs, object provenance)
    {
        (int start, int take) = Paging.Normalize(offset, limit);
        int budget = Math.Clamp(maxNodes, 1, 1000), initialBudget = budget;
        ulong total = TimingTree.Total(nodes);
        TimingTreeNode[] Children(uint? parent) => TimingTree.Children(nodes, parent).Where(c => c.InclusiveEopNs >= minInclusiveNs).ToArray();
        ToolCallDto Next(uint? parent, int next) => new("pix_gpu_timing_tree", new
        {
            handle, queueIndex, parentIndex = parent, offset = next, limit = take, depth = Math.Clamp(depth, 1, 4), maxNodes = initialBudget, minInclusiveNs,
        });
        TimingBranchDto Branch(TimingTreeNode n, int levels)
        {
            budget--;
            TimingTreeNode[] candidates = Children(n.Index);
            var children = new List<TimingBranchDto>();
            if (levels > 1)
                foreach (TimingTreeNode child in candidates.Take(take))
                {
                    if (budget == 0) break;
                    children.Add(Branch(child, levels - 1));
                }
            bool more = children.Count < candidates.Length;
            return new(new(handle, queueIndex, n.Index), n.Index, n.Name, n.GpuId, n.MeasuredEopNs,
                n.InclusiveEopNs, n.SelfEopNs, total == 0 ? 0 : Math.Round(100.0 * n.InclusiveEopNs / total, 2),
                n.HasOwnTiming, n.TimedDescendants, candidates.Length, children, more,
                more ? [Next(n.Index, children.Count)] : []);
        }
        TimingTreeNode[] roots = Children(parentIndex);
        var page = new List<TimingBranchDto>();
        foreach (TimingTreeNode child in roots.Skip(start).Take(take))
        {
            if (budget == 0) break;
            page.Add(Branch(child, Math.Clamp(depth, 1, 4)));
        }
        int? nextOffset = start + page.Count < roots.Length ? start + page.Count : null;
        return new(handle, queueIndex, total, nodes.Count(n => n.HasOwnTiming), start, roots.Length, page,
            nextOffset, nextOffset.HasValue, initialBudget - budget, budget == 0, provenance,
            nextOffset.HasValue ? [Next(parentIndex, nextOffset.Value)] : []);
    }

    // ---- GPU hardware counters ----

    [McpServerTool(Name = "pix_gpu_counters_list", ReadOnly = true), Description("Pages the GPU hardware counters available for this capture on the local GPU (id, name, description, data type, groups); extra.groups lists every group name. Filter with nameContains or group, then pass ids to pix_gpu_counters_start / pix_gpu_counters_collect. Needs GPU analysis: started automatically as a job (see waitSeconds). For sampled-over-time counters see pix_gpu_hf_counters; for occupancy see pix_gpu_occupancy.")]
    public static Task<string> CountersList(
        PixSession session,
        JobManager jobs,
        [Description("GPU capture handle")] string handle,
        [Description("First counter (default 0).")] int offset = 0,
        [Description("Maximum counters (default 25, max 1000).")] int limit = Paging.DefaultLimit,
        [Description("Only counters whose name or description contains this text (case-insensitive).")] string? nameContains = null,
        [Description("Only counters in this group (exact name from extra.groups, case-insensitive).")] string? group = null,
        [Description(Tools.ReadyWaitDescription)] double waitSeconds = Tools.DefaultReadyWaitSeconds,
        CancellationToken cancellationToken = default)
        => Tools.RunWhenReady(session, jobs, "pix_gpu_counters_list", handle, GpuCaptureHandle.AnalysisPreparation(handle), h =>
        {
            (int o, int l) = Paging.Normalize(offset, limit);
            List<CounterInfo> counters = LoadCounters(h);
            string[] groups = counters.SelectMany(c => c.Groups).Distinct().OrderBy(g => g).ToArray();
            IEnumerable<CounterInfo> matching = counters.Where(c =>
                (Tools.Contains(c.Name, nameContains) || Tools.Contains(c.Description, nameContains)) &&
                (string.IsNullOrEmpty(group) || c.Groups.Any(g => g.Equals(group, StringComparison.OrdinalIgnoreCase))));
            return Paging.Collect(matching, o, l,
                c => new { id = c.Id, name = c.Name, description = c.Description, dataType = c.DataType, groups = c.Groups },
                new { groups, counterCount = counters.Count });
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

    [McpServerTool(Name = "pix_gpu_counters_start", Idempotent = true), Description("Collects GPU hardware counters and materializes per-event results for every queue as a job. Poll pix_job_status, then page the cached rows with pix_gpu_counters_collect (which joins this job if it is still running). Each distinct counter set replays the capture once per analysis session.")]
    public static Task<string> CountersStart(
        PixSession session,
        JobManager jobs,
        [Description("GPU capture handle")] string handle,
        [Description("Counter ids from pix_gpu_counters_list.")] uint[] counterIds,
        [Description(Tools.WaitSecondsDescription)] double waitSeconds = 0,
        CancellationToken cancellationToken = default)
        => Tools.RunJob(jobs, "pix_gpu_counters_start", () =>
        {
            uint[] ids = NormalizeCounterIds(counterIds);
            Preparation<GpuCaptureHandle> preparation = CounterSetPreparation(handle, ids);
            Job job = jobs.StartForHandle<GpuCaptureHandle>("counters", $"Collect GPU counters [{CounterSetKey(ids)}] for {handle}", handle, (j, h) =>
            {
                preparation.Prepare(h, j);
                CounterCollectionCache cache = h.CounterCollections[CounterSetKey(ids)];
                var queues = new List<object>();
                foreach (QueueEntry queue in h.Queues)
                {
                    CounterEventRow[] rows = cache.RowsByQueue[queue.Index];
                    queues.Add(new { queueIndex = queue.Index, eventCount = rows.Length, dataEventCount = rows.Count(r => r.HasData) });
                    j.SetProgress((float)queues.Count / h.Queues.Count);
                }
                return new { handle = h.Id, counters = CounterMetadata(cache), queues };
            });
            Tools.RegisterPreparation(session, handle, preparation.Key, job);
            return job;
        }, waitSeconds, cancellationToken);

    internal static uint[] NormalizeCounterIds(uint[]? counterIds)
    {
        if (counterIds is null || counterIds.Length == 0)
            throw new McpException("counterIds must contain at least one counter id.");
        return counterIds.Distinct().OrderBy(x => x).ToArray();
    }

    private static object[] CounterMetadata(CounterCollectionCache cache)
        => cache.Counters.Select(c => (object)new { id = c.Id, name = c.Name, description = c.Description,
            dataType = c.DataType, unit = "unknown", unitReason = "PIX counter metadata does not expose a separate unit field." }).ToArray();

    private static CounterCollectionCache CollectCounterSet(GpuCaptureHandle h, uint[] ids, Job? job)
    {
        string key = CounterSetKey(ids);
        if (h.CounterCollections.TryGetValue(key, out CounterCollectionCache? cached)) return cached;
        job?.Cancellation.Token.ThrowIfCancellationRequested();
        var known = LoadCounters(h, job).ToDictionary(c => c.Id);
        foreach (uint id in ids)
            if (!known.ContainsKey(id)) throw new McpException($"Unknown counter id {id}; use pix_gpu_counters_list.");
        job?.AddMessage($"Collecting {ids.Length} GPU counter(s)...");
        job?.Cancellation.Token.ThrowIfCancellationRequested();
        IPixGpuCaptureCounterData data = PixApiExtensionsGpuCaptureCounters.CollectCounters(h.Counters!, ids);
        job?.Cancellation.Token.ThrowIfCancellationRequested();
        var result = new CounterCollectionCache(data, ids.Select(id => known[id]).ToArray());
        h.CollectedCounters[key] = data;
        h.CounterCollections[key] = result;
        return result;
    }

    private static CounterEventRow[] CounterRows(CounterCollectionCache cache, QueueEntry queue, CancellationToken ct = default)
        => cache.GetOrCreateRows(queue.Index, () =>
        {
            var rows = new List<CounterEventRow>();
            for (uint i = 0; i < queue.EventCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                PIX_EVENT_INFO info = PixApiExtensionsGpuCapture.GetEvent(queue.Info, i);
                EventRecord e = queue.Cache is EventRecord[] events ? events[i] : EventRecord.From(i, info);
                var values = new object?[cache.Counters.Length];
                bool any = false;
                for (int counterIndex = 0; counterIndex < cache.Counters.Length; counterIndex++)
                {
                    CounterInfo counter = cache.Counters[counterIndex];
                    object? value = null;
                    try
                    {
                        if (cache.Data.HasEventData(counter.Id, ref info))
                        {
                            ulong bits = PixApiExtensionsGpuCaptureCounters.GetEventData(cache.Data, counter.Id, info);
                            value = Interop.NumericValue(bits, counter.FormatSpecifier);
                            any = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        throw new McpException($"Reading counter {counter.Id} ('{counter.Name}') at queue {queue.Index}, event {e.Index}: {PixErrors.Describe(ex)}");
                    }
                    values[counterIndex] = value;
                }
                rows.Add(new CounterEventRow(e, values, any));
            }
            ct.ThrowIfCancellationRequested();
            return rows.ToArray();
        });

    [McpServerTool(Name = "pix_gpu_counters_collect", ReadOnly = true), Description("Pages per-event GPU hardware counter values for one queue. Reuses decoded results; a counter set not collected yet is collected as a job first (see waitSeconds), the same job pix_gpu_counters_start runs explicitly.")]
    public static Task<string> CountersCollect(
        PixSession session,
        JobManager jobs,
        [Description("GPU capture handle")] string handle,
        [Description("Counter ids to collect (from pix_gpu_counters_list). Keep the set small; each set replays the capture.")] uint[] counterIds,
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
        [Description("Restrict to this event and its descendants; must belong to handle and queueIndex.")] EventRef? scope = null,
        [Description("First queue event index, inclusive.")] uint? firstEventIndex = null,
        [Description("Last queue event index, inclusive.")] uint? lastEventIndex = null,
        CancellationToken cancellationToken = default)
    {
        uint[] ids = NormalizeCounterIds(counterIds);
        if (orderByCounterId.HasValue && !ids.Contains(orderByCounterId.Value))
            throw new McpException("orderByCounterId must be present in counterIds.");
        if ((minValue.HasValue || maxValue.HasValue) && !orderByCounterId.HasValue)
            throw new McpException("Numeric thresholds require orderByCounterId.");
        if (minValue > maxValue || firstEventIndex > lastEventIndex)
            throw new McpException("The minimum of a range must not exceed its maximum.");
        if (scope is not null && (scope.Handle != handle || scope.QueueIndex != queueIndex))
            throw new McpException("scope must belong to handle and queueIndex.");
        GpuCaptureHandle selectedHandle = session.Get<GpuCaptureHandle>(handle);
        QueueEntry selectedQueue = selectedHandle.Queue(queueIndex);
        if (scope?.EventIndex >= selectedQueue.EventCount || firstEventIndex >= selectedQueue.EventCount || lastEventIndex >= selectedQueue.EventCount)
            throw new McpException("The event scope is outside the selected queue.");
        // Validate the filter before an expensive replay, even if the queue is empty.
        Tools.MatchesKind(default(EventRecord) with { Name = string.Empty, ApiCallData = string.Empty }, kind);
        return Tools.RunWhenReady(session, jobs, "pix_gpu_counters_collect", handle, CounterSetPreparation(handle, ids), h =>
        {
            (int o, int l) = Paging.Normalize(offset, limit);
            QueueEntry queue = h.Queue(queueIndex);
            CounterCollectionCache cache = CollectCounterSet(h, ids, null);
            EventRecord[] all = h.AllEvents(queueIndex);
            int valueIndex = orderByCounterId.HasValue ? Array.FindIndex(cache.Counters, c => c.Id == orderByCounterId.Value) : -1;
            IEnumerable<CounterEventRow> matching = CounterRows(cache, queue).Where(row =>
            {
                EventRecord e = row.Event;
                if (!Tools.Contains(e.Name, nameContains) || !Tools.MatchesKind(e, kind) || (onlyEventsWithData && !row.HasData)) return false;
                if (e.Index < firstEventIndex || e.Index > lastEventIndex || (scope is not null && !EventNavigation.IsWithin(all, e.Index, scope.EventIndex))) return false;
                return true;
            });
            CounterEventRow[] rows = CounterQuery.ApplyNumericQuery(matching, valueIndex, descending, minValue, maxValue).ToArray();
            uint[] orderedIds = cache.Counters.Select(c => c.Id).ToArray();
            CounterValueRowDto[] page = rows.Skip(o).Take(l).Select(row => new CounterValueRowDto(
                new(handle, queueIndex, row.Event.Index), row.Event.Index, row.Event.GpuId == uint.MaxValue ? null : row.Event.GpuId,
                row.Event.Name, EventNavigation.MarkerPath(all, row.Event.Index), CounterQuery.Values(orderedIds, row.Values))).ToArray();
            return Paging.Page(page, rows.Length, o, l, new { counters = CounterMetadata(cache), provenance = h.Provenance(),
                integerEncoding = "Integers outside the JavaScript safe range are decimal strings." });
        }, waitSeconds, cancellationToken);
    }

    // ---- Occupancy and high-frequency counters (optional per hardware) ----

    [McpServerTool(Name = "pix_gpu_occupancy", ReadOnly = true), Description("GPU occupancy over the capture (per occupancy type and shader stage), downsampled to maxPoints. Unavailable on some hardware; returns an 'unavailable' marker instead of failing. Needs GPU analysis: started automatically as a job (see waitSeconds).")]
    public static Task<string> Occupancy(
        PixSession session,
        JobManager jobs,
        [Description("GPU capture handle")] string handle,
        [Description("Maximum sample points per series (default 200, max 5000).")] int maxPoints = 200,
        [Description(Tools.ReadyWaitDescription)] double waitSeconds = Tools.DefaultReadyWaitSeconds,
        [Description("Original point offset for lossless pages; omit for evenly spaced overview samples.")] int? pointOffset = null,
        CancellationToken cancellationToken = default)
        => Tools.RunWhenReady(session, jobs, "pix_gpu_occupancy", handle, OccupancyPreparation(handle), h =>
        {
            if (h.OptionalUnavailable.TryGetValue("occupancy", out object? unavailable)) return unavailable;
            return OccupancyCore(h, Math.Clamp(maxPoints, 2, 5000), pointOffset);
        }, waitSeconds, cancellationToken);

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

    private static unsafe object OccupancyCore(GpuCaptureHandle h, int maxPoints, int? pointOffset)
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
        };
    }

    [McpServerTool(Name = "pix_gpu_hf_counters", ReadOnly = true), Description("High-frequency (sampled over time) GPU counters: lists counters/groups/sets and, when a set is chosen, collects and returns samples downsampled to maxSamples. Unavailable on some hardware. Per-event counter values come from pix_gpu_counters_collect instead. Needs GPU analysis: started automatically as a job (see waitSeconds).")]
    public static Task<string> HighFrequencyCounters(
        PixSession session,
        JobManager jobs,
        [Description("GPU capture handle")] string handle,
        [Description("Counter set index to collect (omit to only list).")] int? setIndex = null,
        [Description("Maximum samples per counter (default 200, max 5000).")] int maxSamples = 200,
        [Description(Tools.ReadyWaitDescription)] double waitSeconds = Tools.DefaultReadyWaitSeconds,
        [Description("Original sample offset for lossless pages; omit for evenly spaced overview samples.")] int? sampleOffset = null,
        CancellationToken cancellationToken = default)
        => Tools.RunWhenReady(session, jobs, "pix_gpu_hf_counters", handle, HfPreparation(handle, setIndex), h =>
        {
            if (setIndex < 0) throw new McpException("setIndex must be nonnegative.");
            if (h.OptionalUnavailable.TryGetValue("hf:" + setIndex, out object? unavailable)) return unavailable;
            return HfCore(h, setIndex, Math.Clamp(maxSamples, 2, 5000), sampleOffset);
        }, waitSeconds, cancellationToken);

    private static Preparation<GpuCaptureHandle> HfPreparation(string handle, int? setIndex)
    {
        if (setIndex < 0) throw new McpException("setIndex must be nonnegative.");
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

    private static unsafe object HfCore(GpuCaptureHandle h, int? setIndex, int maxSamples, int? sampleOffset = null)
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
            counters = Interop.Items<IPixGpuCaptureHighFrequencyCounter>(s).Select(c => new { id = c.GetId(), name = Interop.W(c.GetName()) }).ToArray(),
        }).ToArray();

        object? samples = null;
        if (setIndex.HasValue)
        {
            if (setIndex.Value < 0 || (ulong)setIndex.Value >= sets.GetCount())
            {
                throw new McpException($"setIndex {setIndex} is out of range; there are {sets.GetCount()} counter set(s).");
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
        };
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
            return new
            {
                counter = counter.Counter,
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
