using System.ComponentModel;
using System.Globalization;
using PixMcp.Pix.Handles;
using ToolKinds = PixMcp.Tools.Tools;

namespace PixMcp.Pix;

/// <summary>Per-group counter aggregate of a rollup.</summary>
public sealed record RollupCounterDto(uint Id, string Name, string Unit,
    [property: Description("sum or avg over the group's rows, chosen from the counter's unit (percent-like counters average).")] string Aggregate,
    [property: Description("The aggregate; null when no row of the group has data.")] double? Value,
    int Samples, double? Min, double? Max, double? P50, double? P95,
    [property: Description("eventRows (the group's event rows) or pixMarkerRound (PIX's own marker measurement, markerDepth only).")] string ValueSource,
    [property: Description("Value divided by the group's summed EOP milliseconds when normalize=perMs and the aggregate is a sum.")] double? PerMs);

/// <summary>One group of a rollup.</summary>
public sealed record RollupRowDto(
    [property: Description("Group key: marker name, marker path joined with '/', shaderKey, psoKey, kind, queue index, command list id or API name; (no marker) and (incomplete identity) collect the rest.")] string Key,
    [property: Description("Rows in the group: events, or marker occurrences for markerDepth.")] int Count,
    [property: Description("Rows with a value for the metric.")] int Measured,
    [property: Description("Rows without a value for the metric.")] int Untimed,
    [property: Description("Summed metric; percentages divide by the denominators block.")] DurationDto? Sum,
    ulong? AvgNs, ulong? MinNs, ulong? MaxNs, ulong? P50Ns, ulong? P95Ns,
    [property: Description("measured (one PIX measurement), derivedSum (a sum of measurements), mixed, remainder or untimed.")] string Semantics,
    [property: Description("The group's largest row, for follow-up calls.")] EventRef? Representative,
    [property: Description("Structured key parts: marker path segments, shader stage and hash, or a pipeline's shader keys.")] object? KeyDetail)
{
    public IReadOnlyList<RollupCounterDto>? Counters { get; init; }
    public IReadOnlyList<ToolCallDto> NextCalls { get; init; } = [];
}

/// <summary>What the percentages of a rollup divide by.</summary>
public sealed record RollupDenominatorsDto(ulong QueueSpanNs, ulong QueueSumNs,
    [property: Description("queue N, or sum over queues [...] when several queues contribute.")] string Source);

public sealed record RollupDto(string Handle, string GroupBy, int? Depth, string Metric, string? Kind, int? QueueIndex, string SortBy, bool Descending,
    RollupDenominatorsDto Denominators, long Total, int Offset, int Count, int? NextOffset, IReadOnlyList<RollupRowDto> Items,
    [property: Description("The metric summed over the population with every row once; group sums exceed it when a row belongs to several groups.")] ulong PopulationNs,
    [property: Description("markerDepth without a scope: true when the groups add up to the queues' sum of roots.")] bool? Reconciles,
    string PercentileMethod, IReadOnlyList<string> Notes, IReadOnlyList<ToolCallDto> NextCalls)
{
    public ScopeDescriptionDto? Scope { get; init; }
    public object? Provenance { get; init; }
}

/// <summary>pix_gpu_events mode=count.</summary>
public sealed record EventCountDto(string Handle, int? QueueIndex, long Total)
{
    public ScopeDescriptionDto? Scope { get; init; }
}

public sealed record EventBucketDto(string Key, int Count, EventRef First);

/// <summary>pix_gpu_events mode=histogram: matching events counted per bucket, largest first.</summary>
public sealed record EventHistogramDto(string Handle, int? QueueIndex, string BucketBy, long Total, int BucketCount, IReadOnlyList<EventBucketDto> Buckets)
{
    public ScopeDescriptionDto? Scope { get; init; }
    public IReadOnlyList<ToolCallDto> NextCalls { get; init; } = [];
}

public sealed record PipelineShaderDto(string Stage, string? Hash, string? ShaderKey, string? Entry, ShaderRef? ShaderRef);

/// <summary>One pipeline (bound shader set) ranked by replay time or use count.</summary>
public sealed record PipelineRankDto(
    [property: Description("pso:STAGE:HASH;... over every bound shader; null when a hash is missing or a shader could not be read.")] string? PsoKey,
    [property: Description("psoKey (a pipeline identity) or occurrence (events without a complete identity, grouped together).")] string Identity,
    IReadOnlyList<PipelineShaderDto> Shaders, int UseCount, DurationDto? GpuTime, ulong? P50Ns, ulong? P95Ns, EventRef? Representative, IReadOnlyList<string>? MarkerPath)
{
    public IReadOnlyList<ToolCallDto> NextCalls { get; init; } = [];
}

public sealed record PipelinesDto(string Handle, string SortBy, long Total, int Offset, int Count, int? NextOffset, IReadOnlyList<PipelineRankDto> Items,
    RollupDenominatorsDto Denominators, IReadOnlyList<ToolCallDto> NextCalls)
{
    public ScopeDescriptionDto? Scope { get; init; }
    public object? Provenance { get; init; }
}

internal sealed record RollupQueueInput(int QueueIndex, EventRecord[] Events, int[] ChildCounts, TimingTreeResult Tree, IReadOnlyList<EventTimingRow> Rows);

internal sealed record RollupInputs(string Handle, IReadOnlyList<RollupQueueInput> Queues, Func<int, uint, bool> InScope, bool Unrestricted)
{
    public Func<EventRef, IReadOnlyList<string>>? ShaderKeys { get; init; }
    public Func<EventRef, string?>? PsoKey { get; init; }
    public IReadOnlyList<CounterInfo>? Counters { get; init; }
    public Func<int, uint, object?[]?>? CounterValues { get; init; }
}

internal sealed record RollupRequest(string GroupBy, int Depth = 1, string Metric = "eop", string? Kind = null, string SortBy = "sum", bool Descending = true,
    double? MinPercent = null, int MinCount = 1, string Normalize = "none");

internal sealed record RollupResult(IReadOnlyList<RollupRowDto> Rows, RollupDenominatorsDto Denominators, QueueTotals Totals, ulong PopulationNs, bool? Reconciles, IReadOnlyList<string> Notes);

/// <summary>Group keys shared by rollups and event histograms; pure over cached event metadata.</summary>
internal static class GroupKeys
{
    public const string NoMarker = "(no marker)", IncompleteIdentity = "(incomplete identity)";
    public static readonly string[] BucketBys = ["kind", "marker", "markerPath", "queue", "commandList", "api"];

    /// <summary>The name of the nearest enclosing marker.</summary>
    public static string Marker(EventRecord[] events, int[] childCounts, uint index)
    {
        var visited = new HashSet<uint> { index };
        for (uint parent = events[index].ParentIndex; parent < events.Length && visited.Add(parent); parent = events[parent].ParentIndex)
            if (ToolKinds.IsMarker(events[parent], childCounts[parent] > 0)) return events[parent].Name;
        return NoMarker;
    }

    public static string MarkerPath(EventRecord[] events, uint index)
    {
        string[] path = EventNavigation.MarkerPath(events, index);
        return path.Length == 0 ? NoMarker : string.Join('/', path);
    }

    public static string Of(string bucketBy, int queueIndex, EventRecord[] events, int[] childCounts, uint index) => bucketBy switch
    {
        "marker" => Marker(events, childCounts, index),
        "markerPath" => MarkerPath(events, index),
        "queue" => queueIndex.ToString(CultureInfo.InvariantCulture),
        "commandList" => events[index].CommandListId.ToString(CultureInfo.InvariantCulture),
        "api" => events[index].Name,
        _ => ToolKinds.Classify(events[index], childCounts[index] > 0),
    };

    /// <summary>Markers among each event and its ancestors; for a marker this is its marker depth (1 = top level).</summary>
    public static int[] MarkersAtOrAbove(EventRecord[] events, int[] childCounts)
    {
        var counts = new int[events.Length];
        var known = new bool[events.Length];
        for (uint i = 0; i < events.Length; i++)
        {
            if (known[i]) continue;
            var chain = new List<uint>();
            var seen = new HashSet<uint>();
            uint current = i;
            while (current < events.Length && !known[current] && seen.Add(current))
            {
                chain.Add(current);
                current = events[current].ParentIndex;
            }
            int above = current < events.Length && known[current] ? counts[current] : 0;
            for (int k = chain.Count - 1; k >= 0; k--)
            {
                uint node = chain[k];
                if (ToolKinds.IsMarker(events[node], childCounts[node] > 0)) above++;
                counts[node] = above;
                known[node] = true;
            }
        }
        return counts;
    }
}

/// <summary>
/// Pure aggregation over cached replay timing: groups work rows (or marker spans for markerDepth) and reports count, summed
/// metric with queue percentages, avg/min/max, nearest-rank p50/p95, semantics and a representative row per group.
/// </summary>
internal static class Rollups
{
    public static readonly string[] GroupBys = ["marker", "markerPath", "markerDepth", "shader", "psoKey", "kind", "queue", "commandList", "api"];
    public static readonly string[] MetricNames = ["eop", "self", "exec", "counter"];
    public static readonly string[] SortKeys = ["sum", "count", "avg", "p95", "max", "key"];
    public static readonly string[] Normalizations = ["none", "perMs"];
    public const string PercentileMethod = "nearest rank over the group's per-row values";
    public const string Remainder = "remainder";

    /// <summary>The kind filter a grouping uses when the caller passes none: work for marker, shader and pipeline groupings; every non-marker event otherwise.</summary>
    public static string? DefaultKind(string groupBy) => groupBy is "marker" or "markerPath" or "shader" or "psoKey" ? "work" : null;

    private sealed record Sample(int Queue, uint Index, string Key, ulong? Ns, string Semantics, object?[]? CounterRow, bool MarkerRound);

    public static RollupResult Compute(RollupRequest request, RollupInputs inputs)
    {
        string? kind = request.GroupBy == "markerDepth" ? null : request.Kind ?? DefaultKind(request.GroupBy);
        var samples = new List<Sample>();
        ulong population = 0;
        foreach (RollupQueueInput q in inputs.Queues)
        {
            EventRecord[] events = q.Events;
            TimingTreeNode[] nodes = q.Tree.Nodes;
            if (request.GroupBy == "markerDepth")
            {
                int[] depths = GroupKeys.MarkersAtOrAbove(events, q.ChildCounts);
                for (uint i = 0; i < events.Length; i++)
                {
                    if (q.ChildCounts[i] == 0 || !ToolKinds.IsMarker(events[i], true) || depths[i] != request.Depth || !inputs.InScope(q.QueueIndex, i)) continue;
                    TimingTreeNode node = nodes[i];
                    ulong? ns = !node.IsTimed ? null : request.Metric switch { "self" => node.SelfEopNs, "exec" => node.ExecutionNs, _ => node.InclusiveEopNs };
                    samples.Add(new(q.QueueIndex, i, string.Join('/', [.. EventNavigation.MarkerPath(events, i), events[i].Name]), ns, node.Semantics,
                        inputs.CounterValues?.Invoke(q.QueueIndex, i), true));
                    if (ns is ulong value) population += value;
                }
                continue;
            }

            var rows = new Dictionary<uint, EventTimingRow>();
            foreach (EventTimingRow r in q.Rows)
                if (r.Index < events.Length && r.EopStart != GpuCaptureHandle.TimingNone && r.EopDuration != GpuCaptureHandle.TimingNone) rows.TryAdd(r.Index, r);
            for (uint i = 0; i < events.Length; i++)
            {
                EventRecord e = events[i];
                bool hasChildren = q.ChildCounts[i] > 0;
                if (ToolKinds.IsMarker(e, hasChildren) || !ToolKinds.MatchesKind(e, kind, hasChildren) || !inputs.InScope(q.QueueIndex, i)) continue;
                rows.TryGetValue(i, out EventTimingRow? row);
                ulong? ns = row is null ? null : request.Metric == "exec" ? Exec(row) : row.EopDuration;
                object?[]? counters = inputs.CounterValues?.Invoke(q.QueueIndex, i);
                var eventRef = new EventRef(inputs.Handle, q.QueueIndex, i);
                IReadOnlyList<string> keys = request.GroupBy switch
                {
                    "shader" => inputs.ShaderKeys?.Invoke(eventRef) is { Count: > 0 } shaderKeys ? shaderKeys : [GroupKeys.IncompleteIdentity],
                    "psoKey" => [inputs.PsoKey?.Invoke(eventRef) ?? GroupKeys.IncompleteIdentity],
                    _ => [GroupKeys.Of(request.GroupBy, q.QueueIndex, events, q.ChildCounts, i)],
                };
                foreach (string key in keys) samples.Add(new(q.QueueIndex, i, key, ns, TimingSemantics.Measured, counters, false));
                if (ns is ulong value) population += value;
            }
        }

        QueueTotals totals = CombineTotals(inputs.Queues.Select(q => q.Tree.Totals).ToArray());
        var denominators = new RollupDenominatorsDto(totals.SpanNs, totals.SumOfRootsNs, inputs.Queues.Count == 1
            ? $"queue {inputs.Queues[0].QueueIndex}" : $"sum over queues [{string.Join(", ", inputs.Queues.Select(q => q.QueueIndex))}]");
        var groups = new List<RollupRowDto>();
        foreach (IGrouping<string, Sample> group in samples.GroupBy(s => s.Key, StringComparer.Ordinal))
        {
            Sample[] members = group.ToArray();
            ulong[] values = members.Where(s => s.Ns.HasValue).Select(s => s.Ns!.Value).Order().ToArray();
            ulong sum = 0;
            foreach (ulong value in values) sum += value;
            Sample representative = members.Where(s => s.Ns.HasValue).OrderByDescending(s => s.Ns).ThenBy(s => s.Queue).ThenBy(s => s.Index).FirstOrDefault() ?? members[0];
            string semantics = values.Length == 0 ? TimingSemantics.Untimed
                : request.GroupBy == "markerDepth" ? MarkerSemantics(members)
                : values.Length == 1 ? TimingSemantics.Measured : TimingSemantics.DerivedSum;
            var eventRef = new EventRef(inputs.Handle, representative.Queue, representative.Index);
            groups.Add(new RollupRowDto(group.Key, members.Length, values.Length, members.Length - values.Length,
                values.Length == 0 ? null : Metrics.Duration(sum, totals),
                values.Length == 0 ? null : sum / (ulong)values.Length, values.Length == 0 ? null : values[0], values.Length == 0 ? null : values[^1],
                Percentile(values, 50), Percentile(values, 95), semantics, eventRef, KeyDetail(request.GroupBy, group.Key, representative, inputs))
            {
                Counters = inputs.Counters?.Select((counter, column) => Aggregate(counter, column, members, sum, request.Normalize)).ToArray(),
                NextCalls = RowCalls(request.GroupBy, inputs.Handle, group.Key, eventRef),
            });
        }

        var notes = new List<string>();
        bool? reconciles = null;
        if (request.GroupBy == "markerDepth")
        {
            notes.Add("markerDepth groups PIX's own marker spans (measured, or summed from children when PIX has no span) by path.");
            if (inputs.Unrestricted && request.Metric is "eop" or "counter")
            {
                ulong roots = totals.SumOfRootsNs;
                reconciles = population <= roots;
                if (roots > population)
                {
                    ulong remainder = roots - population;
                    groups.Add(new RollupRowDto(GroupKeys.NoMarker, 0, 0, 0, Metrics.Duration(remainder, totals), null, null, null, null, null, Remainder, null, null));
                    notes.Add($"(no marker) is the queues' sum of roots minus the depth-{request.Depth} marker spans: top-level events and the self time of shallower markers.");
                }
                else if (roots < population) notes.Add($"The depth-{request.Depth} marker spans exceed the queues' sum of roots (overlapping or repaired markers), so no remainder row is added.");
            }
        }
        else notes.Add("Group sums add measured event rows (derivedSum); they are not PIX marker spans. Use groupBy=markerDepth for PIX's own marker measurements.");
        if (request.GroupBy == "shader") notes.Add("A work event counts once per bound shader, so group sums add up to more than populationNs.");
        if (request.Metric == "exec") notes.Add("exec windows (TOP start to EOP end) of neighbouring events overlap, so their sums double count pipeline time.");
        if (request.Metric == "counter") notes.Add("Counter aggregates combine event rows; PIX's marker rows are separate playback rounds and are never summed into markers (markerDepth reports each marker's own round).");

        IEnumerable<RollupRowDto> kept = groups.Where(r => r.Semantics == Remainder || r.Count >= request.MinCount)
            .Where(r => request.MinPercent is not double min || (r.Sum?.PercentOfQueueSpan ?? 0) >= min);
        Func<RollupRowDto, double> metric = request.SortBy switch
        {
            "count" => r => r.Count,
            "avg" => r => r.AvgNs ?? 0,
            "p95" => r => r.P95Ns ?? 0,
            "max" => r => r.MaxNs ?? 0,
            _ => r => r.Sum?.Ns ?? 0,
        };
        RollupRowDto[] sorted = request.SortBy == "key"
            ? (request.Descending ? kept.OrderByDescending(r => r.Key, StringComparer.Ordinal) : kept.OrderBy(r => r.Key, StringComparer.Ordinal)).ToArray()
            : (request.Descending ? kept.OrderByDescending(metric) : kept.OrderBy(metric)).ThenBy(r => r.Key, StringComparer.Ordinal).ToArray();
        RollupRowDto[] ranked = sorted.Select((r, i) => r.Sum is null ? r : r with { Sum = r.Sum with { Rank = i + 1 } }).ToArray();
        return new RollupResult(ranked, denominators, totals, population, reconciles, notes);
    }

    private static ulong? Exec(EventTimingRow row)
    {
        ulong end = row.EopStart + row.EopDuration;
        return row.TopStart != GpuCaptureHandle.TimingNone && row.TopStart <= end ? end - row.TopStart : null;
    }

    private static ulong? Percentile(ulong[] sorted, int percent) => sorted.Length == 0 ? null : sorted[(percent * sorted.Length + 99) / 100 - 1];

    private static string MarkerSemantics(Sample[] members)
    {
        string[] timed = members.Where(m => m.Ns.HasValue).Select(m => m.Semantics).Distinct().ToArray();
        if (members.Length == 1) return members[0].Semantics;
        if (timed.All(s => s == TimingSemantics.Measured)) return TimingSemantics.DerivedSum;
        return timed.Length == 1 && timed[0] == TimingSemantics.DerivedSum ? TimingSemantics.DerivedSum : TimingSemantics.Mixed;
    }

    private static object? KeyDetail(string groupBy, string key, Sample representative, RollupInputs inputs)
    {
        EventRecord[] Events() => inputs.Queues.First(q => q.QueueIndex == representative.Queue).Events;
        return groupBy switch
        {
            "markerPath" when key != GroupKeys.NoMarker => EventNavigation.MarkerPath(Events(), representative.Index),
            "markerDepth" => (string[])[.. EventNavigation.MarkerPath(Events(), representative.Index), Events()[representative.Index].Name],
            "shader" when ShaderIdentity.TryParseShaderKey(key, out string stage, out string hash) => new { stage, hash },
            "psoKey" when key.StartsWith("pso:", StringComparison.Ordinal) => ShaderIdentity.ShaderKeysOf(key),
            _ => null,
        };
    }

    private static RollupCounterDto Aggregate(CounterInfo counter, int column, Sample[] members, ulong sumNs, string normalize)
    {
        double[] values = members.Select(m => m.CounterRow is { } row && column < row.Length ? ToDouble(row[column]) : null)
            .Where(v => v.HasValue).Select(v => v!.Value).Order().ToArray();
        UnitGuess unit = counter.Unit;
        string aggregate = unit.AggregationHint == "sum" ? "sum" : "avg";
        double? value = values.Length == 0 ? null : aggregate == "sum" ? values.Sum() : Math.Round(values.Average(), 6);
        double? perMs = normalize == "perMs" && aggregate == "sum" && value is double total && sumNs > 0 ? Math.Round(total / (sumNs / 1e6), 6) : null;
        double? At(int percent) => values.Length == 0 ? null : values[(percent * values.Length + 99) / 100 - 1];
        return new RollupCounterDto(counter.Id, counter.Name, unit.Unit, aggregate, value, values.Length,
            values.Length == 0 ? null : values[0], values.Length == 0 ? null : values[^1], At(50), At(95),
            members.Any(m => m.MarkerRound) ? "pixMarkerRound" : "eventRows", perMs);
    }

    private static double? ToDouble(object? value) => CounterNormalization.ToDouble(value);

    private static IReadOnlyList<ToolCallDto> RowCalls(string groupBy, string handle, string key, EventRef representative)
    {
        ToolCallDto? call = groupBy switch
        {
            "markerDepth" => new("pix_gpu_timing_tree", new { handle, scope = representative, sortBy = "self" }),
            "shader" when key.StartsWith("hash:", StringComparison.Ordinal) => new("pix_gpu_shader_uses", new { handle, shaderKey = key }),
            "psoKey" => new("pix_gpu_pipeline_state", new { eventRef = representative }),
            "kind" when ToolKinds.Kinds.Contains(key) => new("pix_gpu_timing_events", new { handle, kind = key, sortBy = "eopDuration" }),
            "queue" => new("pix_gpu_timing_tree", new { handle, queueIndex = representative.QueueIndex }),
            _ => new("pix_gpu_inspect_event", new { eventRef = representative, sections = new[] { "timing" } }),
        };
        return call is not null && ToolRegistry.Accepts(call) ? [call] : [];
    }

    private static QueueTotals CombineTotals(IReadOnlyList<QueueTotals> all)
    {
        if (all.Count == 1) return all[0];
        if (all.Count == 0) return new(-1, 0, 0, 0, 0, false, 0, 0, 0, 0, "eopOnly");
        ulong Sum(Func<QueueTotals, ulong> select)
        {
            ulong total = 0;
            foreach (QueueTotals t in all) total += select(t);
            return total;
        }
        return new(-1, Sum(t => t.BusyNs), Sum(t => t.SpanNs), Sum(t => t.IdleNs), Sum(t => t.SumOfRootsNs), all.Any(t => t.RootsOverlap),
            all.Sum(t => t.TimedEvents), all.Sum(t => t.UntimedEvents), all.Min(t => t.FirstEopStartNs), all.Max(t => t.LastEopEndNs), "sumOverQueues");
    }
}
