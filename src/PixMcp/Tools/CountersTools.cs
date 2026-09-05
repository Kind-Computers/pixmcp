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

    [McpServerTool(Name = "pix_gpu_timing_collect"), Description("Collects per-event GPU timing (top-of-pipe and end-of-pipe start/duration in ns) for the whole capture by replaying it. Returns a job; the result is a summary with the slowest events per queue. Starts analysis if needed.")]
    public static async Task<string> TimingCollect(
        PixSession session,
        JobManager jobs,
        [Description("GPU capture handle")] string handle,
        [Description("Seconds to wait inline for completion (default 0 = return job immediately).")] double waitSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            GpuCaptureHandle h = session.Get<GpuCaptureHandle>(handle);
            Job job = jobs.Start("timing", $"Collect GPU timing for {h.Id}", j =>
            {
                CollectTiming(h, j);
                return TimingSummary(h);
            });
            return Json.Serialize(await jobs.WaitOrStatus(job, waitSeconds, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            throw PixErrors.ToMcp(ex, "pix_gpu_timing_collect");
        }
    }

    private static void CollectTiming(GpuCaptureHandle h, Job? job)
    {
        if (h.Timing is not null)
        {
            return;
        }
        h.EnsureAnalysisStarted(job);
        job?.AddMessage("Collecting GPU timing...");
        h.Timing = PixApiExtensionsGpuCaptureAnalysis.CollectTiming<IPixGpuCaptureTiming>(h.GetAnalysis());
        h.TimingRowsByQueue.Clear();
        foreach (QueueEntry queue in h.Queues)
        {
            h.TimingRowsByQueue[queue.Index] = BuildTimingRows(h, queue);
        }
        job?.AddMessage("Timing collected.");
    }

    private static EventTimingRow[] BuildTimingRows(GpuCaptureHandle h, QueueEntry queue)
    {
        var rows = new List<EventTimingRow>();
        IPixGpuCaptureTiming timing = h.Timing!;
        for (uint i = 0; i < queue.EventCount; i++)
        {
            PIX_EVENT_INFO info = PixApiExtensionsGpuCapture.GetEvent(queue.Info, i);
            bool has;
            try { has = PixApiExtensionsGpuCaptureTiming.HasEventData(timing, info); }
            catch { continue; }
            if (!has)
            {
                continue;
            }
            PIX_EVENT_TIMING t = PixApiExtensionsGpuCaptureTiming.GetEventData(timing, info);
            rows.Add(new EventTimingRow(queue.Index, i, info.GpuId, Interop.A(info.Name), t.TopStart, t.TopDuration, t.EopStart, t.EopDuration));
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

    [McpServerTool(Name = "pix_gpu_timing_events"), Description("Per-event GPU timing rows, sortable by duration so 'the N slowest draws' is one call. Collects timing first if needed (can take a while on large captures; use pix_gpu_timing_collect to do that as a job).")]
    public static Task<string> TimingEvents(
        PixSession session,
        [Description("GPU capture handle")] string handle,
        [Description("Queue index; omit for all queues.")] int? queueIndex = null,
        [Description("First item (default 0).")] int offset = 0,
        [Description("Maximum items (default 100).")] int limit = Paging.DefaultLimit,
        [Description("Sort key: eopDuration (default), topDuration, eopStart, index.")] string sortBy = "eopDuration",
        [Description("Sort descending (default true).")] bool descending = true,
        [Description("Only events with EOP duration >= this many nanoseconds.")] ulong minDurationNs = 0,
        [Description("Only events whose name contains this text.")] string? nameContains = null,
        [Description(Tools.KindDescription)] string? kind = null)
        => Tools.Run(session, "pix_gpu_timing_events", () =>
        {
            GpuCaptureHandle h = session.Get<GpuCaptureHandle>(handle);
            CollectTiming(h, null);
            (int o, int l) = Paging.Normalize(offset, limit);

            IEnumerable<EventTimingRow> rows = queueIndex.HasValue
                ? h.TimingRowsByQueue.TryGetValue(h.Queue(queueIndex.Value).Index, out EventTimingRow[]? r) ? r : Array.Empty<EventTimingRow>()
                : h.TimingRowsByQueue.OrderBy(kv => kv.Key).SelectMany(kv => kv.Value);

            rows = rows.Where(r => r.EopDuration != GpuCaptureHandle.TimingNone && r.EopDuration >= minDurationNs && Tools.Contains(r.Name, nameContains));
            if (!string.IsNullOrEmpty(kind))
            {
                rows = rows.Where(r => Tools.MatchesKind(new EventRecord(r.Index, r.GpuId, uint.MaxValue, r.Name, string.Empty, 0, 0), kind));
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
        });

    // ---- GPU hardware counters ----

    [McpServerTool(Name = "pix_gpu_counters_list"), Description("Lists the GPU hardware counters and counter groups available for this capture on the local GPU (id, name, description, data type). Starts analysis if needed.")]
    public static Task<string> CountersList(PixSession session, [Description("GPU capture handle")] string handle)
        => Tools.Run(session, "pix_gpu_counters_list", () =>
        {
            GpuCaptureHandle h = session.Get<GpuCaptureHandle>(handle);
            List<CounterInfo> counters = LoadCounters(h);
            var groups = counters.SelectMany(c => c.Groups).Distinct().OrderBy(g => g).ToArray();
            return new { count = counters.Count, groups, counters = counters.Select(c => new { id = c.Id, name = c.Name, description = c.Description, dataType = c.DataType, groups = c.Groups }).ToArray() };
        });

    private static List<CounterInfo> LoadCounters(GpuCaptureHandle h)
    {
        if (h.CounterList is not null)
        {
            return h.CounterList;
        }
        h.EnsureAnalysisStarted(null);
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

    [McpServerTool(Name = "pix_gpu_counters_collect"), Description("Collects the given GPU hardware counters (ids from pix_gpu_counters_list) by replaying the capture, then returns per-event values for a page of events. Collection is cached per counter set.")]
    public static Task<string> CountersCollect(
        PixSession session,
        [Description("GPU capture handle")] string handle,
        [Description("Counter ids to collect (from pix_gpu_counters_list). Keep the set small; each set replays the capture.")] uint[] counterIds,
        [Description("Queue index (default 0).")] int queueIndex = 0,
        [Description("First event (default 0).")] int offset = 0,
        [Description("Maximum events (default 100).")] int limit = Paging.DefaultLimit,
        [Description(Tools.KindDescription)] string? kind = null,
        [Description("Only events whose name contains this text.")] string? nameContains = null,
        [Description("Skip events that have no data for any requested counter (default true).")] bool onlyEventsWithData = true)
        => Tools.Run(session, "pix_gpu_counters_collect", () =>
        {
            if (counterIds is null || counterIds.Length == 0)
            {
                throw new McpException("counterIds must contain at least one counter id.");
            }
            GpuCaptureHandle h = session.Get<GpuCaptureHandle>(handle);
            List<CounterInfo> known = LoadCounters(h);
            var byId = known.ToDictionary(c => c.Id);
            foreach (uint id in counterIds)
            {
                if (!byId.ContainsKey(id))
                {
                    throw new McpException($"Unknown counter id {id}; use pix_gpu_counters_list.");
                }
            }

            uint[] ids = counterIds.Distinct().OrderBy(x => x).ToArray();
            string key = string.Join(",", ids);
            if (!h.CollectedCounters.TryGetValue(key, out IPixGpuCaptureCounterData? data))
            {
                data = PixApiExtensionsGpuCaptureCounters.CollectCounters(h.Counters!, ids);
                h.CollectedCounters[key] = data;
            }

            (int o, int l) = Paging.Normalize(offset, limit);
            QueueEntry queue = h.Queue(queueIndex);
            IEnumerable<EventRecord> events = h.AllEvents(queue.Index);
            if (!string.IsNullOrEmpty(kind) || !string.IsNullOrEmpty(nameContains))
            {
                events = Tools.FilterEvents(events, nameContains, null, null, kind, null, null, null);
            }

            var page = new List<object>();
            long total = 0;
            foreach (EventRecord e in events)
            {
                PIX_EVENT_INFO info = PixApiExtensionsGpuCapture.GetEvent(queue.Info, e.Index);
                var values = new Dictionary<string, object?>();
                bool any = false;
                foreach (uint id in ids)
                {
                    object? value = null;
                    try
                    {
                        if (data.HasEventData(id, ref info))
                        {
                            ulong bits = PixApiExtensionsGpuCaptureCounters.GetEventData(data, id, info);
                            value = Interop.NumericValue(bits, byId[id].FormatSpecifier);
                            any = true;
                        }
                    }
                    catch { }
                    values[byId[id].Name] = value;
                }
                if (onlyEventsWithData && !any)
                {
                    continue;
                }
                if (total >= o && page.Count < l)
                {
                    page.Add(new { index = e.Index, gpuId = e.GpuId == uint.MaxValue ? (uint?)null : e.GpuId, name = e.Name, values });
                }
                total++;
            }
            return Paging.Page(page, total, o, l, new { counters = ids.Select(id => new { id, name = byId[id].Name, dataType = byId[id].DataType }).ToArray() });
        });

    // ---- Occupancy and high-frequency counters (optional per hardware) ----

    [McpServerTool(Name = "pix_gpu_occupancy"), Description("GPU occupancy over the capture (per occupancy type and shader stage), downsampled. Unavailable on some hardware; returns an 'unavailable' marker instead of failing.")]
    public static Task<string> Occupancy(
        PixSession session,
        [Description("GPU capture handle")] string handle,
        [Description("Maximum sample points per series (default 200).")] int maxPoints = 200)
        => Tools.Run(session, "pix_gpu_occupancy", () =>
        {
            GpuCaptureHandle h = session.Get<GpuCaptureHandle>(handle);
            h.EnsureAnalysisStarted(null);
            try
            {
                return OccupancyCore(h.GetAnalysis(), Math.Clamp(maxPoints, 2, 5000));
            }
            catch (Exception ex)
            {
                return PixErrors.Unavailable("occupancy", ex);
            }
        });

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
                int step = (int)Math.Max(1, count / (ulong)maxPoints);
                var sampled = new List<object>();
                uint maxSlots = 0;
                for (ulong i = 0; i < count; i += (ulong)step)
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

    [McpServerTool(Name = "pix_gpu_hf_counters"), Description("High-frequency GPU counters: lists counters/groups/sets and, when a set is chosen, collects and returns downsampled samples. Unavailable on some hardware.")]
    public static Task<string> HighFrequencyCounters(
        PixSession session,
        [Description("GPU capture handle")] string handle,
        [Description("Counter set index to collect (omit to only list).")] int? setIndex = null,
        [Description("Maximum samples per counter (default 200).")] int maxSamples = 200)
        => Tools.Run(session, "pix_gpu_hf_counters", () =>
        {
            GpuCaptureHandle h = session.Get<GpuCaptureHandle>(handle);
            h.EnsureAnalysisStarted(null);
            try
            {
                return HfCore(h.GetAnalysis(), setIndex, Math.Clamp(maxSamples, 2, 5000));
            }
            catch (Exception ex)
            {
                return PixErrors.Unavailable("highFrequencyCounters", ex);
            }
        });

    private static unsafe object HfCore(IPixGpuCaptureAnalysis analysis, int? setIndex, int maxSamples)
    {
        Guid hfGuid = typeof(IPixGpuCaptureHighFrequencyCounters).GUID;
        _IPixGpuCaptureAnalysis_Extensions.GetHighFrequencyCounters(analysis, in hfGuid, out object hfObj);
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
            Guid dataGuid = typeof(IPixGpuCaptureHighFrequencyCounterData).GUID;
            _IPixGpuCaptureHighFrequencyCounters_Extensions.CollectCounterData(hf, sets, in dataGuid, out object dataObj);
            var data = (IPixGpuCaptureHighFrequencyCounterData)dataObj;
            var set = sets.Get<IPixGpuCaptureCounterCollection>((ulong)setIndex.Value);
            var perCounter = new List<object>();
            foreach (IPixGpuCaptureHighFrequencyCounter counter in Interop.Items<IPixGpuCaptureHighFrequencyCounter>(set))
            {
                ulong batchId = 0, count = 0;
                ulong* timestamps = null;
                double* values = null;
                try { _IPixGpuCaptureHighFrequencyCounterData_Extensions.GetSamples(data, set, counter, ref batchId, ref count, ref timestamps, ref values); }
                catch (Exception ex) { perCounter.Add(new { counter = Interop.W(counter.GetName()), unavailable = true, reason = PixErrors.Describe(ex) }); continue; }
                int step = (int)Math.Max(1, count / (ulong)maxSamples);
                var sampled = new List<object>();
                double min = double.MaxValue, max = double.MinValue, sum = 0;
                for (ulong i = 0; i < count; i++)
                {
                    double v = values[i];
                    min = Math.Min(min, v); max = Math.Max(max, v); sum += v;
                    if (i % (ulong)step == 0)
                    {
                        sampled.Add(new { t = timestamps[i], v });
                    }
                }
                perCounter.Add(new
                {
                    counter = Interop.W(counter.GetName()),
                    batchId,
                    sampleCount = count,
                    min = count == 0 ? (double?)null : min,
                    max = count == 0 ? (double?)null : max,
                    average = count == 0 ? (double?)null : sum / count,
                    samples = sampled,
                });
            }
            samples = new { set = Interop.W(set.GetName()), counters = perCounter };
        }

        return new
        {
            counterCount = counters.GetCount(),
            groups = Interop.Items<IPixGpuCaptureCounterCollection>(groups).Select(g => new { name = Interop.W(g.GetName()), count = g.GetCount() }).ToArray(),
            sets = setList,
            samples,
        };
    }
}
