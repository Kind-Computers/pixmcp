using System.Globalization;
using PixMcp.Pix.Handles;
using ToolKinds = PixMcp.Tools.Tools;

namespace PixMcp.Pix.Sql;

internal sealed record GpuSqlQueueInput(int QueueIndex, string Name, string Type, EventRecord[] Events, int[] ChildCounts);

internal sealed record GpuSqlTimingInput(int QueueIndex, IReadOnlyList<EventTimingRow> Rows, TimingTreeResult Tree);

internal sealed record GpuSqlCounterInput(int QueueIndex, IReadOnlyList<CounterEventRow> Rows, int[] ChildCounts);

internal sealed record GpuSqlResourceUse(int QueueIndex, uint EventIndex, uint ViewIndex, string? ApiObjectId, string? ResourceName, string? ViewType);

/// <summary>Pure row builders for the GPU SQL store: cached capture data in, positional rows in schema column order out.</summary>
internal static class GpuSqlPopulate
{
    private static object? Ns(ulong value) => value == GpuCaptureHandle.TimingNone ? null : (long)value;
    private static object? Ns(ulong? value) => value is ulong v && v != GpuCaptureHandle.TimingNone ? (long)v : null;
    private static object? Id(uint value) => value == uint.MaxValue ? null : (long)value;
    private static object Flag(bool value) => value ? 1L : 0L;

    public static IReadOnlyList<GpuSqlTableRows> Core(string handle, string path, string? vendor, string? adapterName, IReadOnlyList<GpuSqlQueueInput> queues, FrameTable frames)
    {
        var capture = new List<object?[]>
        {
            new object?[] { handle, path, (long)GpuSqlSchema.Version, (long)queues.Sum(q => q.Events.Length), (long)queues.Count, vendor, adapterName,
                (long)frames.Count, frames.PresentQueueIndex is int present ? (object?)(long)present : null, frames.Assignment },
        };
        var queueRows = new List<object?[]>();
        var eventRows = new List<object?[]>();
        var argRows = new List<object?[]>();
        var workRows = new List<object?[]>();
        foreach (GpuSqlQueueInput q in queues)
        {
            EventRecord[] events = q.Events;
            queueRows.Add([(long)q.QueueIndex, q.Name, q.Type, (long)events.Length]);
            long[] subtreeLast = SubtreeLast(events);
            int[] depths = Depths(events);
            for (uint i = 0; i < events.Length; i++)
            {
                EventRecord e = events[i];
                string kind = ToolKinds.Classify(e, q.ChildCounts[i] > 0);
                ApiCallDto? call = ApiCallParser.Parse(e.ApiCallData, e.Name);
                bool parsed = call is { Form: not "unparsed" };
                string markerPath = string.Join('/', EventNavigation.MarkerPath(events, i));
                eventRows.Add(
                [
                    (long)q.QueueIndex, (long)i, Id(e.GpuId), e.ParentIndex < events.Length && e.ParentIndex != i ? (object?)(long)e.ParentIndex : null,
                    e.Name, e.ApiCallData, (long)e.CommandListId, (long)e.Color, kind, Flag(EventInspection.IsWork(kind)), Flag(kind == "marker"),
                    parsed ? call!.Api : null, markerPath, markerPath.Length == 0 ? e.Name : markerPath + "/" + e.Name, (long)depths[i], (long)q.ChildCounts[i],
                    subtreeLast[i], frames.FrameOf(q.QueueIndex, i) is int frame ? (object?)(long)frame : null,
                ]);
                if (!parsed) continue;
                foreach (ApiArgumentDto argument in call!.Arguments)
                    argRows.Add([(long)q.QueueIndex, (long)i, (long)argument.Position, argument.Name, argument.Text, argument.Int64, argument.Double, argument.ApiObjectId, argument.ObjectKind]);
                if (call.WorkItemKind is not null)
                    workRows.Add([(long)q.QueueIndex, (long)i, call.Api, call.Form, call.WorkItemKind, call.WorkItems, call.InstanceCount]);
            }
        }
        List<object?[]> frameRows = frames.Frames.Select(f => new object?[]
        {
            (long)f.Index, frames.PresentQueueIndex is int present ? (object?)(long)present : null, (long)f.FirstEventIndex, (long)f.LastEventIndex,
            f.PresentEventIndex is uint presentEvent ? (object?)(long)presentEvent : null, Flag(f.Partial), Ns(f.WindowStartNs), Ns(f.WindowEndNs),
        }).ToList();
        return [new("capture", capture), new("queues", queueRows), new("events", eventRows), new("call_args", argRows), new("work_items", workRows), new("frames", frameRows)];
    }

    /// <summary>The largest index in each event's subtree, assuming children follow their parents (PIX's pre-order event lists).</summary>
    public static long[] SubtreeLast(EventRecord[] events)
    {
        var last = new long[events.Length];
        for (int i = 0; i < events.Length; i++) last[i] = i;
        for (int i = events.Length - 1; i >= 0; i--)
        {
            uint parent = events[i].ParentIndex;
            if (parent < i) last[parent] = Math.Max(last[parent], last[i]);
        }
        return last;
    }

    public static int[] Depths(EventRecord[] events)
    {
        var depths = new int[events.Length];
        for (int i = 0; i < events.Length; i++)
        {
            uint parent = events[i].ParentIndex;
            depths[i] = parent < i ? depths[parent] + 1 : parent < events.Length && parent != i ? EventNavigation.MarkerPath(events, (uint)i).Length : 0;
        }
        return depths;
    }

    public static IReadOnlyList<GpuSqlTableRows> Timing(IReadOnlyList<GpuSqlTimingInput> queues)
    {
        var timing = new List<object?[]>();
        var tree = new List<object?[]>();
        var totals = new List<object?[]>();
        foreach (GpuSqlTimingInput q in queues)
        {
            var seen = new HashSet<uint>();
            foreach (EventTimingRow r in q.Rows)
            {
                if (!seen.Add(r.Index)) continue;
                bool eop = r.EopStart != GpuCaptureHandle.TimingNone && r.EopDuration != GpuCaptureHandle.TimingNone;
                ulong? end = eop ? r.EopStart + r.EopDuration : null;
                ulong? exec = end is ulong e && r.TopStart != GpuCaptureHandle.TimingNone && r.TopStart <= e ? e - r.TopStart : null;
                timing.Add([(long)q.QueueIndex, (long)r.Index, Ns(r.TopStart), Ns(r.TopDuration), Ns(r.EopStart), Ns(r.EopDuration),
                    eop ? (object?)Metrics.Ms(r.EopDuration) : null, Ns(end), Ns(exec), exec is ulong x ? (object?)Metrics.Ms(x) : null]);
            }
            TimingTreeNode[] nodes = q.Tree.Nodes;
            foreach (TimingTreeNode n in nodes)
            {
                ulong? parentInclusive = n.ParentIndex is uint p && p < nodes.Length && p != n.Index && nodes[p].IsTimed ? nodes[p].InclusiveEopNs : null;
                bool timed = n.IsTimed;
                tree.Add(
                [
                    (long)q.QueueIndex, (long)n.Index, n.Semantics, Ns(n.MeasuredEopNs), timed ? (object?)(long)n.InclusiveEopNs : null, timed ? Metrics.Ms(n.InclusiveEopNs) : null,
                    timed ? (object?)(long)n.SelfEopNs : null, timed ? Metrics.Ms(n.SelfEopNs) : null, (long)n.ChildSumEopNs, Flag(n.ChildrenExceedMeasured), (long)n.ChildOverflowNs,
                    (long)n.UntimedChildren, Flag(n.Repaired), (long)n.TimedDescendants, (long)n.ChildCount, Ns(n.ExecutionNs),
                    timed ? Metrics.Percent(n.InclusiveEopNs, q.Tree.Totals.SpanNs) : null, timed && parentInclusive is ulong pi ? Metrics.Percent(n.InclusiveEopNs, pi) : null,
                ]);
            }
            QueueTotals t = q.Tree.Totals;
            totals.Add([(long)q.QueueIndex, (long)t.BusyNs, t.BusyMs, (long)t.SpanNs, t.SpanMs, (long)t.IdleNs, t.IdleMs, t.IdlePercent, (long)t.SumOfRootsNs, Flag(t.RootsOverlap),
                (long)t.TimedEvents, (long)t.UntimedEvents, (long)t.FirstEopStartNs, (long)t.LastEopEndNs, t.BusySource]);
        }
        return [new("timing", timing), new("timing_tree", tree), new("queue_totals", totals)];
    }

    public static IReadOnlyList<GpuSqlTableRows> Shaders(IEnumerable<ShaderOccurrence> occurrences, Func<EventRef, string?> psoKey, Func<EventRef, bool> complete)
    {
        var shaders = new List<object?[]>();
        var pipelines = new List<object?[]>();
        foreach (IGrouping<EventRef, ShaderOccurrence> e in occurrences.Where(o => o.Shader.ShaderRef is not null).GroupBy(o => o.Shader.ShaderRef!.EventRef))
        {
            foreach (ShaderOccurrence occurrence in e)
            {
                ShaderInfoDto s = occurrence.Shader;
                shaders.Add([(long)e.Key.QueueIndex, (long)e.Key.EventIndex, (long)s.ShaderRef!.ShaderIndex, ShaderIdentity.ShaderKey(s.Stage, s.Hash), s.Stage, s.Hash, s.Entry, s.Target,
                    (long)s.SizeBytes, string.Join(',', s.AvailableCode)]);
            }
            pipelines.Add([(long)e.Key.QueueIndex, (long)e.Key.EventIndex, psoKey(e.Key), Flag(complete(e.Key))]);
        }
        return [new("shaders", shaders), new("event_pipelines", pipelines)];
    }

    public static IReadOnlyList<GpuSqlTableRows> Counters(IReadOnlyList<CounterInfo> counters, IReadOnlyList<GpuSqlCounterInput> queues)
    {
        List<object?[]> info = counters.Select(c => new object?[]
        {
            (long)c.Id, c.Name, c.Description, c.DataType, c.Unit.Unit, c.Unit.UnitSource, c.Unit.UnitConfidence, c.Unit.AggregationHint, string.Join(',', c.Groups),
        }).ToList();
        var values = new List<object?[]>();
        foreach (GpuSqlCounterInput q in queues)
            foreach (CounterEventRow row in q.Rows)
            {
                if (!row.HasData) continue;
                uint index = row.Event.Index;
                string rowKind = index < q.ChildCounts.Length && ToolKinds.IsMarker(row.Event, q.ChildCounts[index] > 0) ? "marker" : "event";
                for (int i = 0; i < counters.Count && i < row.Values.Length; i++)
                    if (row.Values[i] is { } value)
                        values.Add([(long)q.QueueIndex, (long)index, (long)counters[i].Id, CounterNormalization.ToDouble(value), Convert.ToString(value, CultureInfo.InvariantCulture), rowKind]);
            }
        return [new("counter_info", info), new("counters", values)];
    }

    public static IReadOnlyList<GpuSqlTableRows> Resources(IEnumerable<ResourceSummaryDto> resources)
        => [new("resources", resources.GroupBy(r => r.ApiObjectId).Select(g => g.First()).Select(r => new object?[]
        {
            r.ApiObjectId, (long)r.Index, r.Name, r.Type, r.Dimension, (long)r.Width, (long)r.Height, (long)r.DepthOrArraySize, (long)r.MipLevels, r.Format, (long)r.SampleCount, r.Flags,
            r.EstimatedBytes is ulong bytes ? (long)bytes : (long?)null, r.EstimateMethod, r.HeapKind,
        }).ToList())];

    /// <summary>Coarse access class of a bound view type.</summary>
    public static string Access(string? viewType) => ResourceAccess.FromViewType(viewType);

    public static IReadOnlyList<GpuSqlTableRows> ResourceUses(IEnumerable<GpuSqlResourceUse> uses)
        => [new("resource_uses", uses.GroupBy(u => (u.QueueIndex, u.EventIndex, u.ViewIndex)).Select(g => g.First()).Select(u => new object?[]
        {
            (long)u.QueueIndex, (long)u.EventIndex, (long)u.ViewIndex, u.ApiObjectId, u.ResourceName, u.ViewType, Access(u.ViewType),
        }).ToList())];
}
