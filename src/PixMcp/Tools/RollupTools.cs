using System.ComponentModel;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using PixMcp.Pix.Handles;

namespace PixMcp.Tools;

[McpServerToolType]
public static class RollupTools
{
    public static readonly string[] PipelineSortKeys = ["gpuTime", "useCount"];

    [McpServerTool(Name = "pix_gpu_rollup", Title = "GPU time rollup", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Replays the capture on the local GPU if analysis is not started. Aggregates replay timing, and optionally counters, in one call: groups by marker, marker path, marker depth, shader, pipeline (psoKey), kind, queue, command list or API name and returns count, summed EOP with queue percentages, avg/min/max, p50/p95, measured and untimed counts, semantics and a representative event per group. markerDepth groups PIX's own marker spans and adds a (no marker) remainder so the rows add up to the queues' sum of roots; the other groupings sum measured event rows (derivedSum), never PIX's marker rounds.")]
    public static Task<string> Rollup(PixSession session, JobManager jobs,
        [Description("GPU capture handle (from pix_gpu_open).")] string handle,
        [Description("Group by marker (nearest enclosing marker name), markerPath, markerDepth (marker spans at a depth), shader (shaderKey; a work event counts once per bound shader), psoKey (bound shader set), kind, queue, commandList or api (default marker).")] string groupBy = "marker",
        [Description("Marker depth for groupBy=markerDepth: 1 is top-level markers (default 1).")] int depth = 1,
        [Description("eop (default), self (marker self time; equals eop for event rows), exec (TOP start to EOP end) or counter (adds per-group counter aggregates; needs counterIds or preset).")] string metric = "eop",
        [Description("Counter ids for metric=counter (from pix_gpu_counters_list).")] uint[]? counterIds = null,
        [Description(CounterPresets.NamesDescription + " For metric=counter, instead of counterIds.")] string? preset = null,
        [Description("Only this queue; omit for all queues.")] int? queueIndex = null,
        [Description(EventScope.Description)] EventRef? scope = null,
        [Description(EventScope.PrefixDescription)] string? markerPathPrefix = null,
        [Description(Tools.KindDescription + " When omitted: work for marker, markerPath, shader and psoKey groupings, every non-marker event otherwise.")] string? kind = null,
        [Description("none (default) or perMs: divide summed counters by the group's summed EOP milliseconds.")] string normalize = "none",
        [Description("sum (default), count, avg, p95, max or key.")] string sortBy = "sum",
        [Description("Largest first, or reverse key order (default true).")] bool descending = true,
        [Description("Only groups whose summed EOP is at least this percent of the queue span.")] double? minPercent = null,
        [Description("Only groups with at least this many rows (default 1).")] int minCount = 1,
        [Description("First group to return (default 0).")] int offset = 0,
        [Description("Maximum groups to return (default 25, max 1000).")] int limit = Paging.DefaultLimit,
        [Description(Shaping.FormatDescription)] string format = "objects",
        [Description(Shaping.BriefDescription + " Drops key details and per-row follow-up calls.")] bool brief = false,
        [Description(Shaping.MaxStringLengthDescription)] int? maxStringLength = null,
        [Description(Tools.ReadyWaitDescription)] double waitSeconds = Tools.DefaultReadyWaitSeconds,
        [Description(Tools.IncludeProvenanceDescription)] bool includeProvenance = false,
        CancellationToken cancellationToken = default)
    {
        string group = Canonical(groupBy, Rollups.GroupBys, "groupBy");
        string metricName = Canonical(metric, Rollups.MetricNames, "metric");
        string sort = Canonical(sortBy, Rollups.SortKeys, "sortBy");
        string normalization = Canonical(normalize, Rollups.Normalizations, "normalize");
        if (depth is < 1 or > 16) throw PixErrors.InvalidArguments("depth must be between 1 and 16.");
        if (minCount < 1) throw PixErrors.InvalidArguments("minCount must be at least 1.");
        if (minPercent is < 0 or > 1000 || minPercent is double p && !double.IsFinite(p)) throw PixErrors.InvalidArguments("minPercent must be between 0 and 1000.");
        ReferenceValidation.Page(offset, limit);
        Tools.ValidateKind(kind);
        ShapingOptions shaping = Shaping.Options(format, brief, null, maxStringLength, offset);
        GpuCaptureHandle capture = session.Get<GpuCaptureHandle>(handle);
        if (queueIndex.HasValue) capture.Queue(queueIndex.Value);
        if (metricName == "counter")
        {
            if (preset is not null && counterIds is { Length: > 0 }) throw PixErrors.InvalidArguments("Pass counterIds or preset, not both.");
            if (preset is null && counterIds is not { Length: > 0 })
                throw PixErrors.InvalidArguments("metric=counter needs counterIds or preset.", [new("pix_gpu_counters_list", new { handle })]);
            if (preset is not null)
                return CountersTools.WithPreset(session, jobs, "pix_gpu_rollup", handle, preset, waitSeconds, cancellationToken,
                    ids => Rollup(session, jobs, handle, group, depth, metricName, ids, null, queueIndex, scope, markerPathPrefix, kind, normalization, sort, descending,
                        minPercent, minCount, offset, limit, format, brief, maxStringLength, waitSeconds, includeProvenance, cancellationToken));
        }
        else if (preset is not null || counterIds is { Length: > 0 }) throw PixErrors.InvalidArguments("counterIds and preset apply only to metric=counter.");
        uint[]? ids = metricName == "counter" ? CountersTools.NormalizeCounterIds(counterIds) : null;
        queueIndex ??= scope?.QueueIndex;
        ScopeSelection selection = EventScope.Resolve(session, handle, queueIndex, scope, markerPathPrefix);

        var parts = new List<Preparation<GpuCaptureHandle>>();
        if (ids is not null) parts.Add(CountersTools.CounterSetPreparation(handle, ids));
        parts.Add(CountersTools.TimingPreparation(handle));
        if (group is "shader" or "psoKey") parts.Add(GpuCaptureHandle.ShaderIndexPreparation(handle));
        return Tools.RunWhenReady(session, jobs, "pix_gpu_rollup", handle, Tools.Combine(handle, parts), h =>
        {
            RollupResult result = Rollups.Compute(new RollupRequest(group, depth, metricName, kind, sort, descending, minPercent, minCount, normalization),
                Inputs(h, queueIndex, selection, ids));
            (int o, int l) = Shaping.Window(shaping, offset, limit);
            RollupRowDto[] page = result.Rows.Skip(o).Take(l).ToArray();
            ToolCallDto Call(int at, int? strings) => new("pix_gpu_rollup", new { handle, groupBy = group, depth, metric = metricName, counterIds = ids, queueIndex, scope,
                markerPathPrefix, kind, normalize = normalization, sortBy = sort, descending, minPercent, minCount, offset = at, limit = l, format, brief, maxStringLength = strings });
            ScopeDescriptionDto? described = selection.DescribeOrNull(h);
            int? shownDepth = group == "markerDepth" ? depth : null;
            if (shaping.Table)
                return Shaping.Apply(page, result.Rows.Count, o, l, shaping, RowShapes.Rollup, handle,
                    new { groupBy = group, depth = shownDepth, metric = metricName, denominators = result.Denominators, populationNs = result.PopulationNs,
                        reconciles = result.Reconciles, notes = result.Notes, scope = described, provenance = h.Provenance() },
                    next => Call(next, maxStringLength), () => Call(o, Shaping.FullStringLength));
            if (shaping.Brief) page = page.Select(r => r with { KeyDetail = null, NextCalls = [] }).ToArray();
            int? nextOffset = o + (long)page.Length < result.Rows.Count ? o + page.Length : null;
            return RowShapes.Finish(new RollupDto(handle, group, shownDepth, metricName, kind ?? Rollups.DefaultKind(group), queueIndex, sort, descending,
                result.Denominators, result.Rows.Count, o, page.Length, nextOffset, page, result.PopulationNs, result.Reconciles, Rollups.PercentileMethod, result.Notes,
                nextOffset.HasValue ? [Call(nextOffset.Value, maxStringLength)] : [])
            { Scope = described, Provenance = h.Provenance() }, shaping);
        }, waitSeconds, cancellationToken);
    }

    [McpServerTool(Name = "pix_gpu_pipelines", Title = "Pipelines by GPU time", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Replays the capture on the local GPU if analysis is not started. Ranks pipelines (psoKey: the stage and hash of every bound shader) by summed replay EOP time or use count across work events, with their shaders, p50/p95 and a representative event. Events whose shaders lack a hash or could not be read group under psoKey null with identity occurrence, which is not a pipeline identity.")]
    public static Task<string> Pipelines(PixSession session, JobManager jobs,
        [Description("GPU capture handle (from pix_gpu_open).")] string handle,
        [Description("gpuTime (summed EOP, default) or useCount.")] string sortBy = "gpuTime",
        [Description("Only this queue; omit for all queues.")] int? queueIndex = null,
        [Description(EventScope.Description)] EventRef? scope = null,
        [Description(EventScope.PrefixDescription)] string? markerPathPrefix = null,
        [Description("First pipeline to return (default 0).")] int offset = 0,
        [Description("Maximum pipelines to return (default 25, max 1000).")] int limit = Paging.DefaultLimit,
        [Description(Tools.ReadyWaitDescription)] double waitSeconds = Tools.DefaultReadyWaitSeconds,
        [Description(Shaping.MaxStringLengthDescription)] int? maxStringLength = null,
        [Description(Tools.IncludeProvenanceDescription)] bool includeProvenance = false,
        CancellationToken cancellationToken = default)
    {
        string sort = Canonical(sortBy, PipelineSortKeys, "sortBy");
        ReferenceValidation.Page(offset, limit);
        ShapingOptions shaping = Shaping.Options("objects", false, null, maxStringLength, offset);
        GpuCaptureHandle capture = session.Get<GpuCaptureHandle>(handle);
        if (queueIndex.HasValue) capture.Queue(queueIndex.Value);
        queueIndex ??= scope?.QueueIndex;
        ScopeSelection selection = EventScope.Resolve(session, handle, queueIndex, scope, markerPathPrefix);
        Preparation<GpuCaptureHandle> preparation = Tools.Combine(handle, [CountersTools.TimingPreparation(handle), GpuCaptureHandle.ShaderIndexPreparation(handle)]);
        return Tools.RunWhenReady(session, jobs, "pix_gpu_pipelines", handle, preparation, h =>
        {
            RollupResult result = Rollups.Compute(new RollupRequest("psoKey", SortBy: sort == "useCount" ? "count" : "sum"), Inputs(h, queueIndex, selection, null));
            ShaderIndex index = h.ShaderIndex!;
            PipelineRankDto[] ranked = result.Rows.Select(row =>
            {
                bool identity = row.Key != GroupKeys.IncompleteIdentity;
                IReadOnlyList<ShaderInfoDto> shaders = row.Representative is null ? [] : index.ShadersAt(row.Representative);
                return new PipelineRankDto(identity ? row.Key : null, identity ? "psoKey" : "occurrence",
                    shaders.Select(s => new PipelineShaderDto(s.Stage, s.Hash, ShaderIdentity.ShaderKey(s.Stage, s.Hash), s.Entry, s.ShaderRef)).ToArray(),
                    row.Count, row.Sum, row.P50Ns, row.P95Ns, row.Representative,
                    row.Representative is null ? null : EventNavigation.MarkerPath(h.AllEvents(row.Representative.QueueIndex), row.Representative.EventIndex))
                {
                    NextCalls = row.Representative is null ? [] : [new("pix_gpu_pipeline_state", new { eventRef = row.Representative })],
                };
            }).ToArray();
            (int o, int l) = Paging.Normalize(offset, limit);
            PipelineRankDto[] page = ranked.Skip(o).Take(l).ToArray();
            int? nextOffset = o + (long)page.Length < ranked.Length ? o + page.Length : null;
            return RowShapes.Finish(new PipelinesDto(handle, sort, ranked.Length, o, page.Length, nextOffset, page, result.Denominators,
                nextOffset.HasValue ? [new("pix_gpu_pipelines", new { handle, sortBy = sort, queueIndex, scope, markerPathPrefix, offset = nextOffset.Value, limit = l })] : [])
            { Scope = selection.DescribeOrNull(h), Provenance = h.Provenance() }, shaping);
        }, waitSeconds, cancellationToken);
    }

    /// <summary>The canonical spelling of a case-insensitive choice, or invalid_arguments listing the choices.</summary>
    internal static string Canonical(string? value, IReadOnlyList<string> allowed, string name)
        => allowed.FirstOrDefault(a => a.Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw PixErrors.InvalidArguments($"{name} must be one of: {string.Join(", ", allowed)}.");

    /// <summary>Rollup inputs from the handle's caches (PIX worker; timing, and the shader index or counters when asked, are prepared).</summary>
    internal static RollupInputs Inputs(GpuCaptureHandle h, int? queueIndex, ScopeSelection selection, uint[]? ids)
    {
        var queues = new List<RollupQueueInput>();
        foreach (QueueEntry queue in h.Queues)
        {
            if (queueIndex.HasValue && queue.Index != queueIndex.Value) continue;
            queues.Add(new(queue.Index, h.AllEvents(queue.Index), h.ChildCounts(queue.Index), h.TimingTreeFor(queue.Index), h.TimingRowsByQueue.GetValueOrDefault(queue.Index, [])));
        }
        ShaderIndex? shaders = h.ShaderIndex;
        CounterCollectionCache? counters = ids is null ? null : CountersTools.CounterSet(h, ids);
        Dictionary<int, object?[]?[]>? values = null;
        if (counters is not null)
        {
            values = new();
            foreach (RollupQueueInput q in queues)
            {
                var byIndex = new object?[]?[q.Events.Length];
                foreach (CounterEventRow row in CountersTools.CachedCounterRows(h, counters, q.QueueIndex))
                    if (row.HasData && row.Event.Index < byIndex.Length) byIndex[row.Event.Index] = row.Values;
                values[q.QueueIndex] = byIndex;
            }
        }
        return new RollupInputs(h.Id, queues, (q, i) => selection.Contains(h, q, i), selection.IsUnrestricted)
        {
            ShaderKeys = shaders is null ? null : new Func<EventRef, IReadOnlyList<string>>(shaders.ShaderKeysAt),
            PsoKey = shaders is null ? null : new Func<EventRef, string?>(shaders.PsoKeyOf),
            Counters = counters?.Counters,
            CounterValues = values is null ? null : new Func<int, uint, object?[]?>((q, i) => values.TryGetValue(q, out object?[]?[]? byIndex) && i < byIndex.Length ? byIndex[i] : null),
        };
    }
}
