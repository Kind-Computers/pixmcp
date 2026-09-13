using System.ComponentModel;
using System.Text.Json;
using Microsoft.PIX;
using Microsoft.PIX.Extension;
using Microsoft.PIX.Extension.GpuCapture;
using Microsoft.PIX.Extension.GpuCapture.Analysis;
using Microsoft.PIX.Extension.GpuCapture.Analysis.Counters;
using Microsoft.PIX.Extension.GpuCapture.Analysis.Timing;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using PixMcp.Pix.Handles;

namespace PixMcp.Tools;

/// <summary>One occupancy series copied into managed memory so window math never touches PIX pointers.</summary>
internal sealed record OccupancySeriesSnapshot(string Type, string Stage, string StageAbbreviation, uint MaxSlots, (ulong TimeNs, uint Slots)[] Points,
    IPixGpuCaptureOccupancyType NativeType, IPixGpuCaptureOccupancyStage NativeStage);

/// <summary>One high-frequency counter's samples copied into managed memory.</summary>
internal sealed record HfSeriesSnapshot(string Counter, string Unit, (ulong TimeNs, double Value)[] Samples, double? Average);

/// <summary>A timed work event and its replay-clock window.</summary>
internal sealed record TimedWorkWindow(EventRef EventRef, string Name, ulong StartNs, ulong EndNs, ulong EopNs);

public static partial class CountersTools
{
    public static readonly string[] OccupancyGroupings = ["none", "event", "marker"];
    public static readonly string[] HfGroupings = ["none", "event"];
    internal const string TimingPassSource = "timingPass", StandaloneSource = "standaloneReplay";
    internal const int MaxOccupancyEventRows = 100, MaxHfEventRows = 25, MaxInspectionHfCounters = 32;

    private const string OccupancySemantics = "Occupancy counts occupied execution slots per occupancy type and pipeline stage on the replay clock; each point holds until the next. " +
        "Percentages divide by the type's maximum slots and averages are time-weighted over the window.";

    // ---- Occupancy ----

    [McpServerTool(Name = "pix_gpu_occupancy", Title = "GPU occupancy series", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Replays the capture on the local GPU if analysis is not started. GPU occupancy: occupied execution slots per occupancy type and shader stage over the replay clock, with peak and time-weighted average percentages. Reads the occupancy collected with the timing pass when PIX provides it (collecting timing first) and replays only as a fallback or with forceStandalone. scope or markerPathPrefix add statistics over the selection's replay-clock window, eventRef adds PIX's per-event points, and groupBy lists work events or child markers.")]
    public static Task<string> Occupancy(
        PixSession session,
        JobManager jobs,
        [Description("GPU capture handle")] string handle,
        [Description("Maximum sample points per series (default 200, max 5000).")] int maxPoints = 200,
        [Description(Tools.ReadyWaitDescription)] double waitSeconds = Tools.DefaultReadyWaitSeconds,
        [Description("Original point offset for lossless pages; omit for evenly spaced overview samples.")] int? pointOffset = null,
        [Description(EventScope.Description + " Adds per-series statistics over the selection's replay-clock window.")] EventRef? scope = null,
        [Description(EventScope.PrefixDescription)] string? markerPathPrefix = null,
        [Description("Queue the scope refers to; defaults to the scope's queue.")] int? queueIndex = null,
        [Description(Tools.IncludeProvenanceDescription)] bool includeProvenance = false,
        [Description("One event: PIX's occupancy points for it per series (GetEventPoints) plus the series over its TOP-to-EOP window. Exclusive with scope and markerPathPrefix.")] EventRef? eventRef = null,
        [Description("none (default), event (average and peak per series for the selection's 100 longest timed work events) or marker (per direct child marker of scope).")] string groupBy = "none",
        [Description("Collect occupancy with its own replay even when the timing pass carries occupancy data (default false).")] bool forceStandalone = false,
        CancellationToken cancellationToken = default)
    {
        string grouping = RollupTools.Canonical(groupBy, OccupancyGroupings, "groupBy");
        ValidateEventWindowArguments(session, handle, eventRef, scope, markerPathPrefix);
        if (grouping == "marker" && scope is null) throw PixErrors.InvalidArguments("groupBy=marker needs scope; its direct child markers are the groups.");
        if (grouping == "event" && scope is null && markerPathPrefix is null) throw PixErrors.InvalidArguments("groupBy=event needs scope or markerPathPrefix.");
        ScopeSelection selection = EventScope.Resolve(session, handle, queueIndex, scope, markerPathPrefix);
        int points = Math.Clamp(maxPoints, 2, 5000);
        return Tools.RunWhenReady(session, jobs, "pix_gpu_occupancy", handle, OccupancyPreparation(handle, forceStandalone), h =>
        {
            if (h.OptionalUnavailable.TryGetValue("occupancy", out object? unavailable)) return unavailable;
            return OccupancyCore(h, points, pointOffset, selection, eventRef, grouping);
        }, waitSeconds, cancellationToken);
    }

    private static void ValidateEventWindowArguments(PixSession session, string handle, EventRef? eventRef, EventRef? scope, string? markerPathPrefix)
    {
        if (eventRef is null) return;
        if (scope is not null || markerPathPrefix is not null) throw PixErrors.InvalidArguments("eventRef is exclusive with scope and markerPathPrefix.");
        ReferenceValidation.Event(session, eventRef);
        if (eventRef.Handle != handle)
            throw PixErrors.InvalidReference($"eventRef addresses {eventRef.Handle}, not {handle}.", new ToolCallDto("pix_handles", new { }, CostHints.Cached));
    }

    /// <summary>Replay-clock window of a restricted selection, or null when unrestricted or timing has not been collected.</summary>
    private static object? Window(GpuCaptureHandle h, ScopeSelection selection)
    {
        if (selection.IsUnrestricted) return null;
        var window = EventScope.ToTimeWindow(h, selection);
        return window is null
            ? new { state = h.Timing is null ? "timingNotCollected" : "noTimedEvents", nextCalls = new[] { new ToolCallDto("pix_gpu_timing_prepare", new { handle = h.Id }) } }
            : new { startNs = window.Value.StartNs, endNs = window.Value.EndNs, timedEvents = window.Value.TimedEvents, clock = "PIX replay clock (the timing rows' clock; see clockCheck for the series range)" };
    }

    internal static Preparation<GpuCaptureHandle> OccupancyPreparation(string handle, bool forceStandalone)
        => new(forceStandalone ? PreparationKeys.Occupancy + ":standalone" : PreparationKeys.Occupancy, "occupancy",
            forceStandalone ? $"Collect GPU occupancy with its own replay for {handle}" : $"Collect GPU occupancy for {handle}",
            h => h.OptionalUnavailable.ContainsKey("occupancy") || (forceStandalone ? h.OccupancyData is { Source: StandaloneSource } : h.OccupancyData is not null),
            (h, job) =>
            {
                h.EnsureAnalysisStarted(job);
                if (!forceStandalone)
                {
                    CollectTiming(h, job);
                    if (TimingPassOccupancy(h, job))
                    {
                        h.MarkCapability("occupancy", "supported", "Collected with the timing pass.");
                        return;
                    }
                    job.AddMessage("The timing pass carries no occupancy data; collecting occupancy with its own replay.");
                }
                try
                {
                    CollectOccupancy(h, job);
                    h.MarkCapability("occupancy", "supported");
                }
                catch (Exception ex) when (ExplicitlyUnsupported(ex))
                {
                    h.OptionalUnavailable["occupancy"] = PixErrors.Unavailable("occupancy", ex);
                    h.MarkCapability("occupancy", "unsupported", PixErrors.Describe(ex));
                }
            })
        { JoinKeys = forceStandalone ? [] : [PreparationKeys.Timing, PreparationKeys.Inspection] };

    internal static bool ExplicitlyUnsupported(Exception ex)
        => PixErrors.HResultOf(ex) is unchecked((int)0x80004001) or unchecked((int)0x80070032) or unchecked((int)0x887A0004);

    private static (IPixGpuCaptureOccupancy Occupancy, IPixGpuCaptureOccupancyType[] Types, IPixGpuCaptureOccupancyStage[] Stages) OccupancyCatalog(GpuCaptureHandle h)
    {
        Guid occupancyGuid = typeof(IPixGpuCaptureOccupancy).GUID;
        _IPixGpuCaptureAnalysis_Extensions.GetOccupancy(h.GetAnalysis(), in occupancyGuid, out object occupancyObject);
        var occupancy = (IPixGpuCaptureOccupancy)occupancyObject;
        Guid collectionGuid = typeof(IPixCollection).GUID;
        _IPixGpuCaptureOccupancy_Extensions.GetOccupancyTypes(occupancy, in collectionGuid, out object typesObject);
        _IPixGpuCaptureOccupancy_Extensions.GetOccupancyStages(occupancy, in collectionGuid, out object stagesObject);
        return (occupancy, Interop.Items<IPixGpuCaptureOccupancyType>((IPixCollection)typesObject).ToArray(),
            Interop.Items<IPixGpuCaptureOccupancyStage>((IPixCollection)stagesObject).ToArray());
    }

    /// <summary>Uses the occupancy data collected with the timing pass when PIX reports it; records the probe either way.</summary>
    private static bool TimingPassOccupancy(GpuCaptureHandle h, Job job)
    {
        if (h.Timing is not IPixGpuCaptureTiming timing) return false;
        bool present;
        try { present = timing.HasOccupancyData(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            job.AddMessage("HasOccupancyData failed: " + PixErrors.Describe(ex));
            present = false;
        }
        h.TimingPassProbe["occupancy"] = present;
        if (!present) return false;
        try
        {
            (_, IPixGpuCaptureOccupancyType[] types, IPixGpuCaptureOccupancyStage[] stages) = OccupancyCatalog(h);
            Guid dataGuid = typeof(IPixGpuCaptureOccupancyData).GUID;
            _IPixGpuCaptureTiming_Extensions.GetOccupancyData(timing, in dataGuid, out object data);
            h.OccupancyData = new((IPixGpuCaptureOccupancyData)data, types, stages, TimingPassSource);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            job.AddMessage("Timing-pass occupancy data could not be read: " + PixErrors.Describe(ex));
            return false;
        }
    }

    private static void CollectOccupancy(GpuCaptureHandle h, Job job)
    {
        (IPixGpuCaptureOccupancy occupancy, IPixGpuCaptureOccupancyType[] types, IPixGpuCaptureOccupancyStage[] stages) = OccupancyCatalog(h);
        Guid dataGuid = typeof(IPixGpuCaptureOccupancyData).GUID;
        _IPixGpuCaptureOccupancy_Extensions.CollectOccupancy(occupancy, in dataGuid, out object dataObject);
        job.ThrowIfCancellationRequested();
        h.OccupancyData = new((IPixGpuCaptureOccupancyData)dataObject, types, stages, StandaloneSource);
    }

    internal static unsafe List<OccupancySeriesSnapshot> OccupancySnapshots(OccupancyCache cache)
    {
        var result = new List<OccupancySeriesSnapshot>();
        foreach (IPixGpuCaptureOccupancyType type in cache.Types)
            foreach (IPixGpuCaptureOccupancyStage stage in cache.Stages)
            {
                ulong count = 0;
                PIX_OCCUPANCY_POINT* points = null;
                try { _IPixGpuCaptureOccupancyData_Extensions.GetPoints(cache.Data, type, stage, ref count, ref points); }
                catch { continue; }
                if (count == 0 || points == null) continue;
                result.Add(new(Interop.W(type.GetName()), Interop.W(stage.GetName()), Interop.W(stage.GetAbbreviation()), type.GetMaxSlots(), Copy(points, count), type, stage));
                // PIX owns the returned pointers for the lifetime of the data object.
                GC.KeepAlive(cache.Data);
            }
        return result;
    }

    private static unsafe (ulong TimeNs, uint Slots)[] Copy(PIX_OCCUPANCY_POINT* points, ulong count)
    {
        var copy = new (ulong TimeNs, uint Slots)[count];
        bool sorted = true;
        for (ulong i = 0; i < count; i++)
        {
            copy[i] = (points[i].TimeNanoseconds, points[i].Slots);
            if (i > 0 && copy[i].TimeNs < copy[i - 1].TimeNs) sorted = false;
        }
        if (!sorted) Array.Sort(copy, (a, b) => a.TimeNs.CompareTo(b.TimeNs));
        return copy;
    }

    private static object OccupancyCore(GpuCaptureHandle h, int maxPoints, int? pointOffset, ScopeSelection selection, EventRef? eventRef, string grouping)
    {
        OccupancyCache cache = h.OccupancyData!;
        List<OccupancySeriesSnapshot> snapshots = OccupancySnapshots(cache);
        (ulong StartNs, ulong EndNs, int TimedEvents)? window = selection.IsUnrestricted ? null : EventScope.ToTimeWindow(h, selection);
        var series = new List<object>();
        foreach (OccupancySeriesSnapshot s in snapshots)
        {
            (ulong TimeNs, uint Slots)[] p = s.Points;
            ulong count = (ulong)p.Length;
            var sampled = SampleIndices(count, maxPoints, pointOffset).Select(i => new
            {
                index = i, timeNs = p[i].TimeNs, slots = p[i].Slots,
                percent = s.MaxSlots == 0 ? (double?)null : Math.Round(100.0 * p[i].Slots / s.MaxSlots, 2),
            }).ToArray();
            StepWindowStats whole = SeriesWindow.Step(p, p[0].TimeNs, Math.Max(p[^1].TimeNs, p[0].TimeNs + 1), s.MaxSlots);
            ClockCheckDto? clock = window is { } w ? SeriesWindow.Clock(p[0].TimeNs, p[^1].TimeNs, w.StartNs, w.EndNs) : null;
            uint peak = p.Max(x => x.Slots);
            series.Add(new
            {
                type = s.Type, stage = s.Stage, stageAbbreviation = s.StageAbbreviation, maxSlotsAvailable = s.MaxSlots, pointCount = count, peakSlots = peak,
                peakPercent = s.MaxSlots == 0 ? (double?)null : Math.Round(100.0 * peak / s.MaxSlots, 2),
                timeWeightedAveragePercent = whole.AveragePercent, activeDurationNs = whole.ActiveNs,
                points = sampled, returnedPoints = sampled.Length, sampling = pointOffset.HasValue ? "consecutive" : "evenlySpaced",
                window = window is { } ww && clock is { State: not "mismatch" } ? SeriesWindow.Step(p, ww.StartNs, ww.EndNs, s.MaxSlots) : null,
                clockCheck = clock,
                nextCalls = SampleNextCalls("pix_gpu_occupancy", h.Id, "pointOffset", count, maxPoints, pointOffset, null),
            });
        }
        return new
        {
            provenance = h.Provenance(),
            source = cache.Source,
            timingPassProbe = h.TimingPassProbe.TryGetValue("occupancy", out bool probe) ? probe : (bool?)null,
            timeOrigin = "PIX replay clock; not calibrated to application wall time",
            semantics = OccupancySemantics,
            types = cache.Types.Select(t => new { name = Interop.W(t.GetName()), description = Interop.WOrNull(t.GetDescription()), maxSlots = t.GetMaxSlots() }).ToArray(),
            stages = cache.Stages.Select(st => new { name = Interop.W(st.GetName()), abbreviation = Interop.W(st.GetAbbreviation()) }).ToArray(),
            series,
            scope = selection.DescribeOrNull(h),
            windowNs = Window(h, selection),
            @event = eventRef is null ? null : EventOccupancy(h, cache, snapshots, eventRef),
            perEvent = grouping switch
            {
                "event" => OccupancyByEvent(h, selection, snapshots),
                "marker" => OccupancyByMarker(h, selection, snapshots),
                _ => null,
            },
            notes = CompatibilityNotes.Texts("occupancy", h.EffectiveVendor().Vendor, PixDiscovery.Version),
        };
    }

    /// <summary>The event's TOP-to-EOP window on the replay clock, or null without a timing row.</summary>
    private static (ulong StartNs, ulong EndNs)? EventWindow(GpuCaptureHandle h, EventRef eventRef)
    {
        EventTimingRow? row = h.TimingRowsByQueue.GetValueOrDefault(eventRef.QueueIndex)?
            .FirstOrDefault(r => r.Index == eventRef.EventIndex && r.EopStart != GpuCaptureHandle.TimingNone && r.EopDuration != GpuCaptureHandle.TimingNone);
        if (row is null) return null;
        ulong end = row.EopStart + row.EopDuration;
        return (row.TopStart != GpuCaptureHandle.TimingNone && row.TopStart <= end ? Math.Min(row.TopStart, row.EopStart) : row.EopStart, end);
    }

    internal static unsafe object EventOccupancy(GpuCaptureHandle h, OccupancyCache cache, IReadOnlyList<OccupancySeriesSnapshot> snapshots, EventRef eventRef)
    {
        PIX_EVENT_INFO info = h.EventInfo(eventRef.QueueIndex, eventRef.EventIndex);
        (ulong StartNs, ulong EndNs)? eopWindow = EventWindow(h, eventRef);
        var rows = new List<object>();
        foreach (OccupancySeriesSnapshot s in snapshots)
        {
            ulong count = 0;
            PIX_OCCUPANCY_POINT* points = null;
            StepWindowStats? eventPoints = null;
            string? reason = null;
            try
            {
                _IPixGpuCaptureOccupancyData_Extensions.GetEventPoints(cache.Data, s.NativeType, s.NativeStage, ref info, ref count, ref points);
                if (count > 0 && points != null)
                {
                    (ulong TimeNs, uint Slots)[] copy = Copy(points, count);
                    eventPoints = SeriesWindow.Step(copy, copy[0].TimeNs, Math.Max(copy[^1].TimeNs, copy[0].TimeNs + 1), s.MaxSlots);
                }
                GC.KeepAlive(cache.Data);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { reason = PixErrors.Describe(ex); }
            rows.Add(new
            {
                type = s.Type, stage = s.Stage, stageAbbreviation = s.StageAbbreviation, eventPointCount = count, eventPoints,
                window = eopWindow is { } w ? SeriesWindow.Step(s.Points, w.StartNs, w.EndNs, s.MaxSlots) : null,
                reason,
            });
        }
        return new
        {
            eventRef,
            eopWindow = eopWindow is { } ew ? new { startNs = ew.StartNs, endNs = ew.EndNs } : null,
            method = "eventPoints: PIX's points for this event (GetEventPoints); window: the capture series held over the event's TOP-to-EOP window",
            series = rows,
        };
    }

    private static List<TimedWorkWindow> TimedWorkWindows(GpuCaptureHandle h, ScopeSelection selection)
    {
        var windows = new List<TimedWorkWindow>();
        foreach (int q in selection.Queues(h))
        {
            EventRecord[] events = h.AllEvents(q);
            var seen = new HashSet<uint>();
            foreach (EventTimingRow row in h.TimingRowsByQueue.GetValueOrDefault(q, []))
            {
                if (row.EopDuration == GpuCaptureHandle.TimingNone || row.EopStart == GpuCaptureHandle.TimingNone || row.Index >= events.Length || !seen.Add(row.Index)) continue;
                if (!Tools.MatchesKind(events[row.Index], "work") || !selection.Contains(q, events, row.Index)) continue;
                ulong end = row.EopStart + row.EopDuration;
                ulong start = row.TopStart != GpuCaptureHandle.TimingNone && row.TopStart <= end ? Math.Min(row.TopStart, row.EopStart) : row.EopStart;
                windows.Add(new(new EventRef(h.Id, q, row.Index), events[row.Index].Name, start, end, row.EopDuration));
            }
        }
        return windows.OrderByDescending(w => w.EopNs).ThenBy(w => w.EventRef.QueueIndex).ThenBy(w => w.EventRef.EventIndex).ToList();
    }

    private static object OccupancyByEvent(GpuCaptureHandle h, ScopeSelection selection, IReadOnlyList<OccupancySeriesSnapshot> snapshots)
    {
        List<TimedWorkWindow> windows = TimedWorkWindows(h, selection);
        object[] rows = windows.Take(MaxOccupancyEventRows).Select(w => (object)new
        {
            eventRef = w.EventRef, name = w.Name, eopMs = Metrics.Ms(w.EopNs),
            series = snapshots.Select(s =>
            {
                StepWindowStats stats = SeriesWindow.Step(s.Points, w.StartNs, w.EndNs, s.MaxSlots);
                return new { type = s.Type, stageAbbreviation = s.StageAbbreviation, averagePercent = stats.AveragePercent, peakPercent = stats.PeakPercent };
            }).ToArray(),
        }).ToArray();
        return new { groupBy = "event", total = windows.Count, truncated = windows.Count > MaxOccupancyEventRows, rows };
    }

    private static object OccupancyByMarker(GpuCaptureHandle h, ScopeSelection selection, IReadOnlyList<OccupancySeriesSnapshot> snapshots)
    {
        EventRef root = selection.Root!;
        EventRecord[] events = h.AllEvents(root.QueueIndex);
        int[] children = h.ChildCounts(root.QueueIndex);
        TimingTreeNode[] nodes = h.Timing is null ? [] : h.TimingTreeFor(root.QueueIndex).Nodes;
        var rows = new List<object>();
        foreach (EventRecord e in events)
        {
            if (e.ParentIndex != root.EventIndex || e.Index == root.EventIndex || !Tools.IsMarker(e, children[e.Index] > 0)) continue;
            var eventRef = new EventRef(h.Id, root.QueueIndex, e.Index);
            if (e.Index >= nodes.Length || nodes[e.Index] is not { IsTimed: true, EopStartNs: ulong start, EopEndNs: ulong end } node)
            {
                rows.Add(new { eventRef, name = e.Name, untimed = true });
                continue;
            }
            ulong windowStart = node.TopStartNs is ulong top && top < start ? top : start;
            rows.Add(new
            {
                eventRef, name = e.Name, inclusiveMs = Metrics.Ms(node.InclusiveEopNs),
                series = snapshots.Select(s =>
                {
                    StepWindowStats stats = SeriesWindow.Step(s.Points, windowStart, end, s.MaxSlots);
                    return new { type = s.Type, stageAbbreviation = s.StageAbbreviation, averagePercent = stats.AveragePercent, peakPercent = stats.PeakPercent };
                }).ToArray(),
            });
        }
        return new { groupBy = "marker", total = rows.Count, truncated = false, rows };
    }

    /// <summary>pix_gpu_inspect_event's occupancy section: PIX's points for the event and the series over its window, from data already collected.</summary>
    internal static object EventOccupancySection(GpuCaptureHandle h, EventRef eventRef)
    {
        if (h.OptionalUnavailable.TryGetValue("occupancy", out object? unavailable)) return unavailable;
        if (h.OccupancyData is not OccupancyCache cache)
            return new InspectionSectionStateDto("notCollected", "Occupancy is not collected for this capture; inspection never collects it.")
            {
                NextCalls = [new("pix_gpu_occupancy", new { handle = h.Id, eventRef }, CostHints.Replay)],
            };
        return new { state = "available", source = cache.Source, occupancy = EventOccupancy(h, cache, OccupancySnapshots(cache), eventRef) };
    }

    // ---- High-frequency counters ----

    [McpServerTool(Name = "pix_gpu_hf_counters", Title = "High-frequency counters", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Replays the capture on the local GPU if analysis is not started. High-frequency GPU counters sampled over the replay clock: lists counter sets with descriptions and units, and for a set returns samples with min, max and average. Reads samples collected with the timing pass when PIX provides them (collecting timing first) and replays only as a fallback or with forceStandalone. scope, markerPathPrefix or eventRef add window statistics per counter, groupBy=event lists work events, and three or more percent-unit counters add a heuristic utilization ranking.")]
    public static Task<string> HighFrequencyCounters(
        PixSession session,
        JobManager jobs,
        [Description("GPU capture handle")] string handle,
        [Description("Counter set index to collect (omit to only list).")] int? setIndex = null,
        [Description("Maximum samples per counter (default 200, max 5000).")] int maxSamples = 200,
        [Description(Tools.ReadyWaitDescription)] double waitSeconds = Tools.DefaultReadyWaitSeconds,
        [Description("Original sample offset for lossless pages; omit for evenly spaced overview samples.")] int? sampleOffset = null,
        [Description(EventScope.Description + " With a set, adds statistics per counter over the selection's replay-clock window.")] EventRef? scope = null,
        [Description(EventScope.PrefixDescription)] string? markerPathPrefix = null,
        [Description(Tools.IncludeProvenanceDescription)] bool includeProvenance = false,
        [Description("Counter set name (case-insensitive) instead of setIndex.")] string? setName = null,
        [Description("One event: statistics per counter over its TOP-to-EOP window (needs a set). Exclusive with scope and markerPathPrefix.")] EventRef? eventRef = null,
        [Description("none (default) or event (per-counter window statistics for the selection's 25 longest timed work events; needs a set).")] string groupBy = "none",
        [Description("Collect the set with its own replay even when the timing pass carries high-frequency data (default false).")] bool forceStandalone = false,
        CancellationToken cancellationToken = default)
    {
        string grouping = RollupTools.Canonical(groupBy, HfGroupings, "groupBy");
        if (setIndex < 0) throw PixErrors.InvalidArguments("setIndex must be nonnegative.");
        if (setIndex.HasValue && setName is not null) throw PixErrors.InvalidArguments("Pass setIndex or setName, not both.");
        ValidateEventWindowArguments(session, handle, eventRef, scope, markerPathPrefix);
        if (grouping == "event" && scope is null && markerPathPrefix is null) throw PixErrors.InvalidArguments("groupBy=event needs scope or markerPathPrefix.");
        if ((grouping == "event" || eventRef is not null) && setIndex is null && setName is null)
            throw PixErrors.InvalidArguments("Window statistics need a counter set: pass setIndex or setName.", [new("pix_gpu_hf_counters", new { handle }, CostHints.Replay)]);
        if (setName is not null)
            return ResolveHfSet(session, jobs, handle, setName, waitSeconds, cancellationToken, index => HighFrequencyCounters(session, jobs, handle, index, maxSamples, waitSeconds,
                sampleOffset, scope, markerPathPrefix, includeProvenance, null, eventRef, grouping, forceStandalone, cancellationToken));
        ScopeSelection selection = EventScope.Resolve(session, handle, eventRef?.QueueIndex, scope, markerPathPrefix);
        int samples = Math.Clamp(maxSamples, 2, 5000);
        return Tools.RunWhenReady(session, jobs, "pix_gpu_hf_counters", handle, HfPreparation(handle, setIndex, forceStandalone), h =>
        {
            if (h.OptionalUnavailable.TryGetValue("hf:" + setIndex, out object? unavailable)) return unavailable;
            return HfCore(h, setIndex, samples, sampleOffset, selection, eventRef, grouping);
        }, waitSeconds, cancellationToken);
    }

    private static async Task<string> ResolveHfSet(PixSession session, JobManager jobs, string handle, string setName, double waitSeconds, CancellationToken cancellationToken,
        Func<int, Task<string>> next)
    {
        string hop = await Tools.RunWhenReady(session, jobs, "pix_gpu_hf_counters", handle, HfPreparation(handle, null), h =>
        {
            if (h.OptionalUnavailable.TryGetValue("hf:", out object? unavailable)) return unavailable;
            string[] names = Interop.Items<IPixGpuCaptureCounterCollection>(HfCatalog(h).Sets).Select(s => Interop.W(s.GetName())).ToArray();
            int index = Array.FindIndex(names, n => n.Equals(setName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                throw PixErrors.InvalidReference($"No high-frequency counter set is named '{setName}'. Sets: {(names.Length == 0 ? "none" : string.Join(", ", names))}.",
                    new ToolCallDto("pix_gpu_hf_counters", new { handle }, CostHints.Cached));
            return new { setIndex = index };
        }, waitSeconds, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(hop);
        if (!document.RootElement.TryGetProperty("setIndex", out JsonElement resolved) || resolved.ValueKind != JsonValueKind.Number) return hop;
        return await next(resolved.GetInt32()).ConfigureAwait(false);
    }

    internal static Preparation<GpuCaptureHandle> HfPreparation(string handle, int? setIndex, bool forceStandalone = false)
    {
        if (setIndex < 0) throw PixErrors.InvalidArguments("setIndex must be nonnegative.");
        string unavailableKey = "hf:" + setIndex;
        return new(unavailableKey + (forceStandalone ? ":standalone" : ""), "hf-counters", $"Collect high-frequency counters for {handle}",
            h => h.OptionalUnavailable.ContainsKey(unavailableKey) || (setIndex.HasValue
                ? h.HighFrequencyCollections.TryGetValue(setIndex.Value, out HfCollectionCache? cached) && (!forceStandalone || cached.Source == StandaloneSource)
                : h.HighFrequencyCatalog is not null),
            (h, job) =>
            {
                h.EnsureAnalysisStarted(job);
                try
                {
                    h.HighFrequencyCatalog ??= HfSetsListing(HfCatalog(h).Sets);
                    if (setIndex.HasValue)
                    {
                        if (!forceStandalone) CollectTiming(h, job);
                        EnsureHfSet(h, setIndex.Value, forceStandalone, job);
                    }
                    job.ThrowIfCancellationRequested();
                    h.MarkCapability("highFrequencyCounters", "supported");
                }
                catch (Exception ex) when (ExplicitlyUnsupported(ex))
                {
                    h.OptionalUnavailable[unavailableKey] = PixErrors.Unavailable("highFrequencyCounters", ex);
                    h.MarkCapability("highFrequencyCounters", "unsupported", PixErrors.Describe(ex));
                }
            })
        { JoinKeys = forceStandalone || !setIndex.HasValue ? [] : [PreparationKeys.Timing, PreparationKeys.Inspection] };
    }

    private static (IPixGpuCaptureHighFrequencyCounters Hf, IPixCollection Counters, IPixCollection Groups, IPixCollection Sets) HfCatalog(GpuCaptureHandle h)
    {
        Guid hfGuid = typeof(IPixGpuCaptureHighFrequencyCounters).GUID;
        _IPixGpuCaptureAnalysis_Extensions.GetHighFrequencyCounters(h.GetAnalysis(), in hfGuid, out object hfObject);
        var hf = (IPixGpuCaptureHighFrequencyCounters)hfObject;
        Guid collectionGuid = typeof(IPixCollection).GUID;
        _IPixGpuCaptureHighFrequencyCounters_Extensions.GetCounters(hf, in collectionGuid, out object countersObject);
        _IPixGpuCaptureHighFrequencyCounters_Extensions.GetCounterGroups(hf, in collectionGuid, out object groupsObject);
        _IPixGpuCaptureHighFrequencyCounters_Extensions.GetGpuCounterSets(hf, in collectionGuid, out object setsObject);
        return (hf, (IPixCollection)countersObject, (IPixCollection)groupsObject, (IPixCollection)setsObject);
    }

    private static object[] HfSetsListing(IPixCollection sets)
        => Interop.Items<IPixGpuCaptureCounterCollection>(sets).Select((s, i) => (object)new
        {
            index = i,
            name = Interop.W(s.GetName()),
            description = Interop.WOrNull(s.GetDescription()),
            counters = Interop.Items<IPixGpuCaptureHighFrequencyCounter>(s).Select(c =>
            {
                string name = Interop.W(c.GetName());
                string? description = HfDescription(c);
                UnitGuess unit = CounterUnits.Infer(name, description, "FLOAT64");
                return new { id = c.GetId(), name, description, unit = unit.Unit, unitSource = unit.UnitSource, unitConfidence = unit.UnitConfidence };
            }).ToArray(),
        }).ToArray();

    /// <summary>The set's samples: from the timing pass when PIX has them for every counter of the set, else from its own collection replay.</summary>
    private static HfCollectionCache EnsureHfSet(GpuCaptureHandle h, int setIndex, bool forceStandalone, Job? job)
    {
        if (h.HighFrequencyCollections.TryGetValue(setIndex, out HfCollectionCache? cached) && (!forceStandalone || cached.Source == StandaloneSource)) return cached;
        (IPixGpuCaptureHighFrequencyCounters hf, _, _, IPixCollection sets) = HfCatalog(h);
        if ((ulong)setIndex >= sets.GetCount())
            throw PixErrors.InvalidReference($"setIndex {setIndex} is out of range; there are {sets.GetCount()} counter set(s).",
                new ToolCallDto("pix_gpu_hf_counters", new { handle = h.Id }, CostHints.Cached));
        var set = sets.Get<IPixGpuCaptureCounterCollection>((ulong)setIndex);
        if (!forceStandalone && h.Timing is IPixGpuCaptureTiming timing)
        {
            bool present;
            try { present = timing.HasHighFrequencyCounterData(); }
            catch (Exception ex) when (ex is not OperationCanceledException) { present = false; }
            h.TimingPassProbe["highFrequencyCounters"] = present;
            if (present)
            {
                try
                {
                    Guid timingGuid = typeof(IPixGpuCaptureHighFrequencyCounterData).GUID;
                    _IPixGpuCaptureTiming_Extensions.GetHighFrequencyCounterData(timing, in timingGuid, out object timingData);
                    var data = (IPixGpuCaptureHighFrequencyCounterData)timingData;
                    HfCounterSamples[] fromTiming = ReadHfSamples(data, set);
                    if (fromTiming.Length > 0 && fromTiming.All(c => c.Error is null && c.SampleCount > 0))
                        return h.HighFrequencyCollections[setIndex] = new HfCollectionCache(Interop.W(set.GetName()), data, set, fromTiming, TimingPassSource);
                    job?.AddMessage("The timing pass has no samples for this counter set; collecting it with its own replay.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    job?.AddMessage("Timing-pass high-frequency data could not be read: " + PixErrors.Describe(ex));
                }
            }
        }
        var selected = new SelectedPixCollection(sets, (ulong)setIndex);
        Guid dataGuid = typeof(IPixGpuCaptureHighFrequencyCounterData).GUID;
        _IPixGpuCaptureHighFrequencyCounters_Extensions.CollectCounterData(hf, selected, in dataGuid, out object collectedObject);
        var collected = (IPixGpuCaptureHighFrequencyCounterData)collectedObject;
        return h.HighFrequencyCollections[setIndex] = new HfCollectionCache(Interop.W(set.GetName()), collected, set, ReadHfSamples(collected, set), StandaloneSource);
    }

    private static unsafe HfCounterSamples[] ReadHfSamples(IPixGpuCaptureHighFrequencyCounterData data, IPixGpuCaptureCounterCollection set)
    {
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
                    min = Math.Min(min, v);
                    max = Math.Max(max, v);
                    sum += v;
                }
                collected.Add(new HfCounterSamples(name, counter, batchId, count, count == 0 ? null : min, count == 0 ? null : max, count == 0 ? null : sum / count));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                collected.Add(new HfCounterSamples(name, counter, batchId, count, null, null, null, PixErrors.Describe(ex)));
            }
        }
        GC.KeepAlive(data);
        return collected.ToArray();
    }

    internal static unsafe List<HfSeriesSnapshot> HfSnapshots(HfCollectionCache cache)
    {
        var result = new List<HfSeriesSnapshot>();
        foreach (HfCounterSamples counter in cache.Counters)
        {
            if (counter.Error is not null) continue;
            ulong batchId = 0, count = 0;
            ulong* timestamps = null;
            double* values = null;
            try { _IPixGpuCaptureHighFrequencyCounterData_Extensions.GetSamples(cache.Data, cache.CounterSet, counter.NativeCounter, ref batchId, ref count, ref timestamps, ref values); }
            catch (Exception ex) when (ex is not OperationCanceledException) { continue; }
            if (count > 0 && (timestamps == null || values == null)) continue;
            var copy = new (ulong TimeNs, double Value)[count];
            bool sorted = true;
            for (ulong i = 0; i < count; i++)
            {
                copy[i] = (timestamps[i], values[i]);
                if (i > 0 && copy[i].TimeNs < copy[i - 1].TimeNs) sorted = false;
            }
            if (!sorted) Array.Sort(copy, (a, b) => a.TimeNs.CompareTo(b.TimeNs));
            GC.KeepAlive(cache.Data);
            result.Add(new(counter.Counter, CounterUnits.Infer(counter.Counter, HfDescription(counter.NativeCounter), "FLOAT64").Unit, copy, counter.Average));
        }
        return result;
    }

    private static object HfCore(GpuCaptureHandle h, int? setIndex, int maxSamples, int? sampleOffset, ScopeSelection selection, EventRef? eventRef, string grouping)
    {
        (_, IPixCollection counters, IPixCollection groups, IPixCollection sets) = HfCatalog(h);
        object? samples = null, perEvent = null;
        string? source = null;
        UtilizationRankingDto? ranking = null;
        (ulong StartNs, ulong EndNs)? window = null;
        if (setIndex.HasValue)
        {
            HfCollectionCache cache = EnsureHfSet(h, setIndex.Value, false, null);
            source = cache.Source;
            List<HfSeriesSnapshot> snapshots = HfSnapshots(cache);
            window = eventRef is not null ? EventWindow(h, eventRef)
                : !selection.IsUnrestricted && EventScope.ToTimeWindow(h, selection) is { } scoped ? (scoped.StartNs, scoped.EndNs) : null;
            samples = new
            {
                set = cache.Set,
                counters = cache.Counters.Select(c => HfSamplesDto(h, setIndex.Value, c, snapshots.FirstOrDefault(s => s.Counter == c.Counter), maxSamples, sampleOffset, window)).ToArray(),
            };
            ranking = SeriesWindow.Rank(snapshots.Select(s => (s.Counter, s.Unit,
                window is { } w ? SeriesWindow.Samples(s.Samples, w.StartNs, w.EndNs).TimeWeightedAverage : s.Average)).ToArray());
            if (grouping == "event") perEvent = HfByEvent(h, selection, snapshots);
        }
        return new
        {
            provenance = h.Provenance(),
            source,
            timingPassProbe = h.TimingPassProbe.TryGetValue("highFrequencyCounters", out bool probe) ? probe : (bool?)null,
            timeOrigin = "PIX replay clock; batch IDs identify sample collections",
            counterCount = counters.GetCount(),
            groups = Interop.Items<IPixGpuCaptureCounterCollection>(groups).Select(g => new { name = Interop.W(g.GetName()), count = g.GetCount() }).ToArray(),
            sets = HfSetsListing(sets),
            samples,
            scope = selection.DescribeOrNull(h),
            windowNs = Window(h, selection),
            eventWindow = eventRef is null ? null : window is { } ew ? (object)new { eventRef, startNs = ew.StartNs, endNs = ew.EndNs } : new { eventRef, state = "untimed" },
            perEvent,
            utilizationRanking = ranking,
            notes = CompatibilityNotes.Texts("highFrequencyCounters", h.EffectiveVendor().Vendor, PixDiscovery.Version),
        };
    }

    private static object HfByEvent(GpuCaptureHandle h, ScopeSelection selection, IReadOnlyList<HfSeriesSnapshot> snapshots)
    {
        List<TimedWorkWindow> windows = TimedWorkWindows(h, selection);
        object[] rows = windows.Take(MaxHfEventRows).Select(w => (object)new
        {
            eventRef = w.EventRef, name = w.Name, eopMs = Metrics.Ms(w.EopNs),
            counters = snapshots.Select(s =>
            {
                SampleWindowStats stats = SeriesWindow.Samples(s.Samples, w.StartNs, w.EndNs);
                return new { counter = s.Counter, timeWeightedAverage = stats.TimeWeightedAverage, coverage = stats.Coverage, samples = stats.Count };
            }).ToArray(),
        }).ToArray();
        return new { groupBy = "event", total = windows.Count, truncated = windows.Count > MaxHfEventRows, rows };
    }

    private static string? HfDescription(IPixGpuCaptureHighFrequencyCounter counter)
    {
        try { return Interop.WOrNull(counter.GetDescription()); }
        catch (Exception) { return null; }
    }

    private static object HfSamplesDto(GpuCaptureHandle h, int setIndex, HfCounterSamples counter, HfSeriesSnapshot? snapshot, int maxSamples, int? sampleOffset,
        (ulong StartNs, ulong EndNs)? window)
    {
        if (counter.Error is not null || snapshot is null)
            return new { counter = counter.Counter, unavailable = true, reason = counter.Error ?? "PIX did not return this counter's samples." };
        ulong count = (ulong)snapshot.Samples.Length;
        var sampled = SampleIndices(count, maxSamples, sampleOffset).Select(i => new { index = i, timeNs = snapshot.Samples[i].TimeNs, value = snapshot.Samples[i].Value }).ToArray();
        string? description = HfDescription(counter.NativeCounter);
        UnitGuess unit = CounterUnits.Infer(counter.Counter, description, "FLOAT64");
        return new
        {
            counter = counter.Counter,
            description,
            unit = unit.Unit, unitSource = unit.UnitSource, unitConfidence = unit.UnitConfidence,
            batchId = counter.BatchId,
            sampleCount = count,
            min = counter.Min,
            max = counter.Max,
            average = counter.Average,
            samples = sampled,
            returnedSamples = sampled.Length,
            sampling = sampleOffset.HasValue ? "consecutive" : "evenlySpaced",
            window = window is { } w ? SeriesWindow.Samples(snapshot.Samples, w.StartNs, w.EndNs) : null,
            nextCalls = SampleNextCalls("pix_gpu_hf_counters", h.Id, "sampleOffset", count, maxSamples, sampleOffset, setIndex),
        };
    }

    /// <summary>pix_gpu_inspect_event's hf section: window statistics of every collected set over the event's window, from data already collected.</summary>
    internal static object EventHfSection(GpuCaptureHandle h, EventRef eventRef)
    {
        if (h.HighFrequencyCollections.Count == 0 && h.OptionalUnavailable.FirstOrDefault(kv => kv.Key.StartsWith("hf:", StringComparison.Ordinal)).Value is { } unsupported)
            return unsupported;
        if (h.HighFrequencyCollections.Count == 0)
            return new InspectionSectionStateDto("notCollected", "No high-frequency counter set is collected for this capture; inspection never collects one.")
            {
                NextCalls = [new("pix_gpu_hf_counters", new { handle = h.Id }, CostHints.Replay), new("pix_gpu_hf_counters", new { handle = h.Id, setIndex = 0, eventRef }, CostHints.Replay)],
            };
        if (EventWindow(h, eventRef) is not { } window)
            return new InspectionSectionStateDto("untimed", "The event has no replay timing window, which high-frequency statistics need.")
            {
                NextCalls = [new("pix_gpu_timing_prepare", new { handle = h.Id }, CostHints.Job)],
            };
        object[] sets = h.HighFrequencyCollections.OrderBy(kv => kv.Key).Select(kv =>
        {
            var counters = HfSnapshots(kv.Value).Take(MaxInspectionHfCounters)
                .Select(s => (s.Counter, s.Unit, Stats: SeriesWindow.Samples(s.Samples, window.StartNs, window.EndNs))).ToArray();
            return (object)new
            {
                setIndex = kv.Key, set = kv.Value.Set, source = kv.Value.Source,
                counters = counters.Select(c => new { counter = c.Counter, unit = c.Unit, stats = c.Stats }).ToArray(),
                utilizationRanking = SeriesWindow.Rank(counters.Select(c => (c.Counter, c.Unit, c.Stats.TimeWeightedAverage)).ToArray()),
            };
        }).ToArray();
        return new { state = "available", eopWindow = new { startNs = window.StartNs, endNs = window.EndNs }, sets };
    }

    // ---- Sampling helpers ----

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
