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

    /// <summary>Preparation for tools that need one counter set decoded for one queue.</summary>
    internal static Preparation<GpuCaptureHandle> CounterSetPreparation(string handle, uint[] ids, int queueIndex)
    {
        string key = CounterSetKey(ids);
        return new("counters:" + key, "counters", $"Collect GPU counters [{key}] for {handle}",
            h => h.CounterCollections.TryGetValue(key, out CounterCollectionCache? cache) && cache.RowsByQueue.ContainsKey(queueIndex),
            (h, job) => CounterRows(CollectCounterSet(h, ids, job), h.Queue(queueIndex), job.Cancellation.Token));
    }

    private static string CounterSetKey(uint[] ids) => string.Join(",", ids);

    private static void CollectTiming(GpuCaptureHandle h, Job? job)
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

    private static object TimingRowDto(EventTimingRow r) => new
    {
        queueIndex = r.QueueIndex,
        index = r.Index,
        gpuId = r.GpuId == uint.MaxValue ? (uint?)null : r.GpuId,
        name = r.Name,
        topStartNs = Ns(r.TopStart),
        topDurationNs = Ns(r.TopDuration),
        eopStartNs = Ns(r.EopStart),
        eopDurationNs = Ns(r.EopDuration),
    };

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
                slowest = timed.OrderByDescending(r => r.EopDuration).Take(10).Select(TimingRowDto).ToArray(),
            });
        }
        return new { handle = h.Id, queues };
    }

    [McpServerTool(Name = "pix_gpu_timing_events", ReadOnly = true), Description("Per-event GPU timing rows, sortable by duration so 'the N slowest draws' is one call. Timing is collected first if needed, as a job (see waitSeconds; pix_gpu_timing_collect runs the same job explicitly).")]
    public static Task<string> TimingEvents(
        PixSession session,
        JobManager jobs,
        [Description("GPU capture handle")] string handle,
        [Description("Queue index; omit for all queues.")] int? queueIndex = null,
        [Description("First item (default 0).")] int offset = 0,
        [Description("Maximum items (default 100).")] int limit = Paging.DefaultLimit,
        [Description("Sort key: eopDuration (default), topDuration, eopStart, index.")] string sortBy = "eopDuration",
        [Description("Sort descending (default true).")] bool descending = true,
        [Description("Only events with EOP duration >= this many nanoseconds.")] ulong minDurationNs = 0,
        [Description("Only events whose name contains this text.")] string? nameContains = null,
        [Description(Tools.KindDescription)] string? kind = null,
        [Description(Tools.ReadyWaitDescription)] double waitSeconds = Tools.DefaultReadyWaitSeconds,
        CancellationToken cancellationToken = default)
        => Tools.RunWhenReady(session, jobs, "pix_gpu_timing_events", handle, TimingPreparation(handle), h =>
        {
            (int o, int l) = Paging.Normalize(offset, limit);

            IEnumerable<EventTimingRow> rows = queueIndex.HasValue
                ? h.TimingRowsByQueue.TryGetValue(h.Queue(queueIndex.Value).Index, out EventTimingRow[]? r) ? r : Array.Empty<EventTimingRow>()
                : h.TimingRowsByQueue.OrderBy(kv => kv.Key).SelectMany(kv => kv.Value);

            rows = rows.Where(r => r.EopDuration != GpuCaptureHandle.TimingNone && r.EopDuration >= minDurationNs && Tools.Contains(r.Name, nameContains));
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
            var page = sorted.Skip(o).Take(l).Select(TimingRowDto).ToList();
            return Paging.Page(page, sorted.Length, o, l);
        }, waitSeconds, cancellationToken);

    // ---- GPU hardware counters ----

    [McpServerTool(Name = "pix_gpu_counters_list", ReadOnly = true), Description("Lists the GPU hardware counters and counter groups available for this capture on the local GPU (id, name, description, data type); pass ids to pix_gpu_counters_start / pix_gpu_counters_collect. Needs GPU analysis: started automatically as a job (see waitSeconds). For sampled-over-time counters see pix_gpu_hf_counters; for occupancy see pix_gpu_occupancy.")]
    public static Task<string> CountersList(
        PixSession session,
        JobManager jobs,
        [Description("GPU capture handle")] string handle,
        [Description(Tools.ReadyWaitDescription)] double waitSeconds = Tools.DefaultReadyWaitSeconds,
        CancellationToken cancellationToken = default)
        => Tools.RunWhenReady(session, jobs, "pix_gpu_counters_list", handle, GpuCaptureHandle.AnalysisPreparation(handle), h =>
        {
            List<CounterInfo> counters = LoadCounters(h);
            var groups = counters.SelectMany(c => c.Groups).Distinct().OrderBy(g => g).ToArray();
            return new { count = counters.Count, groups, counters = counters.Select(c => new { id = c.Id, name = c.Name, description = c.Description, dataType = c.DataType, groups = c.Groups }).ToArray() };
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
            Job job = jobs.StartForHandle<GpuCaptureHandle>("counters", $"Collect GPU counters [{CounterSetKey(ids)}] for {handle}", handle, (j, h) =>
            {
                CounterCollectionCache cache = CollectCounterSet(h, ids, j);
                var queues = new List<object>();
                foreach (QueueEntry queue in h.Queues)
                {
                    CounterEventRow[] rows = CounterRows(cache, queue, j.Cancellation.Token);
                    queues.Add(new { queueIndex = queue.Index, eventCount = rows.Length, dataEventCount = rows.Count(r => r.HasData) });
                    j.SetProgress((float)queues.Count / h.Queues.Count);
                }
                return new { handle = h.Id, counters = CounterMetadata(cache), queues };
            });
            Tools.RegisterPreparation(session, handle, "counters:" + CounterSetKey(ids), job);
            return job;
        }, waitSeconds, cancellationToken);

    internal static uint[] NormalizeCounterIds(uint[]? counterIds)
    {
        if (counterIds is null || counterIds.Length == 0)
            throw new McpException("counterIds must contain at least one counter id.");
        return counterIds.Distinct().OrderBy(x => x).ToArray();
    }

    private static object[] CounterMetadata(CounterCollectionCache cache)
        => cache.Counters.Select(c => (object)new { id = c.Id, name = c.Name, dataType = c.DataType }).ToArray();

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
        [Description("Maximum events (default 100).")] int limit = Paging.DefaultLimit,
        [Description(Tools.KindDescription)] string? kind = null,
        [Description("Only events whose name contains this text.")] string? nameContains = null,
        [Description("Skip events that have no data for any requested counter (default true).")] bool onlyEventsWithData = true,
        [Description(Tools.ReadyWaitDescription)] double waitSeconds = Tools.DefaultReadyWaitSeconds,
        CancellationToken cancellationToken = default)
    {
        uint[] ids = NormalizeCounterIds(counterIds);
        // Validate the filter before an expensive replay, even if the queue is empty.
        Tools.MatchesKind(default(EventRecord) with { Name = string.Empty, ApiCallData = string.Empty }, kind);
        return Tools.RunWhenReady(session, jobs, "pix_gpu_counters_collect", handle, CounterSetPreparation(handle, ids, queueIndex), h =>
        {
            (int o, int l) = Paging.Normalize(offset, limit);
            QueueEntry queue = h.Queue(queueIndex);
            CounterCollectionCache cache = CollectCounterSet(h, ids, null);
            var page = new List<object>();
            long total = 0;
            foreach (CounterEventRow row in CounterRows(cache, queue))
            {
                EventRecord e = row.Event;
                if (!Tools.Contains(e.Name, nameContains) || !Tools.MatchesKind(e, kind) || (onlyEventsWithData && !row.HasData)) continue;
                if (total >= o && page.Count < l)
                {
                    var values = new Dictionary<string, object?>();
                    for (int i = 0; i < cache.Counters.Length; i++) values[cache.Counters[i].Name] = row.Values[i];
                    page.Add(new { index = e.Index, gpuId = e.GpuId == uint.MaxValue ? (uint?)null : e.GpuId, name = e.Name, values });
                }
                total++;
            }
            return Paging.Page(page, total, o, l, new { counters = CounterMetadata(cache) });
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
        CancellationToken cancellationToken = default)
        => Tools.RunWhenReady(session, jobs, "pix_gpu_occupancy", handle, GpuCaptureHandle.AnalysisPreparation(handle), h =>
        {
            try
            {
                return OccupancyCore(h.GetAnalysis(), Math.Clamp(maxPoints, 2, 5000));
            }
            catch (Exception ex)
            {
                return PixErrors.Unavailable("occupancy", ex);
            }
        }, waitSeconds, cancellationToken);

    private static unsafe object OccupancyCore(IPixGpuCaptureAnalysis analysis, int maxPoints)
    {
        Guid occGuid = typeof(IPixGpuCaptureOccupancy).GUID;
        _IPixGpuCaptureAnalysis_Extensions.GetOccupancy(analysis, in occGuid, out object occObj);
        var occupancy = (IPixGpuCaptureOccupancy)occObj;

        Guid colGuid = typeof(IPixCollection).GUID;
        _IPixGpuCaptureOccupancy_Extensions.GetOccupancyTypes(occupancy, in colGuid, out object typesObj);
        _IPixGpuCaptureOccupancy_Extensions.GetOccupancyStages(occupancy, in colGuid, out object stagesObj);
        var types = Interop.Items<IPixGpuCaptureOccupancyType>((IPixCollection)typesObj).ToList();
        var stages = Interop.Items<IPixGpuCaptureOccupancyStage>((IPixCollection)stagesObj).ToList();

        Guid dataGuid = typeof(IPixGpuCaptureOccupancyData).GUID;
        _IPixGpuCaptureOccupancy_Extensions.CollectOccupancy(occupancy, in dataGuid, out object dataObj);
        var data = (IPixGpuCaptureOccupancyData)dataObj;

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
                foreach (ulong i in Sampling.Indices(count, maxPoints))
                {
                    sampled.Add(new { t = points[i].TimeNanoseconds, slots = points[i].Slots });
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
                });
            }
        }

        return new
        {
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
        CancellationToken cancellationToken = default)
        => Tools.RunWhenReady(session, jobs, "pix_gpu_hf_counters", handle, GpuCaptureHandle.AnalysisPreparation(handle), h =>
        {
            if (setIndex < 0) throw new McpException("setIndex must be nonnegative.");
            try
            {
                return HfCore(h, setIndex, Math.Clamp(maxSamples, 2, 5000));
            }
            catch (McpException) { throw; }
            catch (Exception ex)
            {
                return PixErrors.Unavailable("highFrequencyCounters", ex);
            }
        }, waitSeconds, cancellationToken);

    private static unsafe object HfCore(GpuCaptureHandle h, int? setIndex, int maxSamples)
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
                counters = cached.Counters.Select(c => HfSamplesDto(cached, c, maxSamples)).ToArray(),
            };
        }

        return new
        {
            counterCount = counters.GetCount(),
            groups = Interop.Items<IPixGpuCaptureCounterCollection>(groups).Select(g => new { name = Interop.W(g.GetName()), count = g.GetCount() }).ToArray(),
            sets = setList,
            samples,
        };
    }

    private static unsafe object HfSamplesDto(HfCollectionCache cache, HfCounterSamples counter, int maxSamples)
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
            var sampled = Sampling.Select(count, maxSamples, i => new { t = timestamps[i], v = values[i] });
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
            };
        }
        catch (Exception ex) { return new { counter = counter.Counter, unavailable = true, reason = PixErrors.Describe(ex) }; }
    }
}
