using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using PixMcp.Pix.Handles;

namespace PixMcp.Tools;

public enum ComparisonSection { timings, shaders, pipeline, resources }

[McpServerToolType]
public static partial class InvestigationTools
{
    [McpServerTool(Name = "pix_gpu_overview", Title = "GPU capture overview", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Replays the capture on the local GPU if analysis is not started. Start a GPU investigation here: capture facts (event total, adapter vendor, frames delimited by Present calls), per-queue event kinds and replay totals (busy, span, idle), ranked top passes (inclusive and self time, semantics, child overflow, work counts) and top work events (draw, dispatch, executeIndirect with EOP and execution time and captured call text), an EOP histogram of work events, per-frame busy time with percentiles on multi-frame captures, and targeted next calls. Queues, kinds and frames answer immediately while the timing replay runs (timing.pending). A scope or frame selection whose event caches are not ready returns the existing pending job.")]
    public static Task<string> Overview(PixSession session, JobManager jobs, [Description("GPU capture handle (from pix_gpu_open).")] string handle,
        [Description("Collect replay timing for totals, passes, work events, the histogram and frames (default true); false answers from metadata only.")] bool includeTiming = true,
        [Description("Top passes and top work events to rank (default 10, max 1000).")] int limit = 10,
        [Description(Tools.ReadyWaitDescription)] double waitSeconds = Tools.DefaultReadyWaitSeconds,
        [Description(EventScope.Description + " Restricts topPasses, topDraws and the histogram.")] EventRef? scope = null,
        [Description(EventScope.PrefixDescription)] string? markerPathPrefix = null,
        [Description("Restrict topPasses, topDraws and the histogram to one frame (0-based; frames end at Present calls, see capture.frames); default: the whole capture.")] int? frameIndex = null,
        [Description("Evaluate the insight rules (default true): findings with evidence, implication and follow-up calls.")] bool includeInsights = true,
        [Description(Shaping.FormatDescription + " table turns topPasses and topDraws into positional tables.")] string format = "objects",
        [Description(Shaping.BriefDescription + " Trims marker paths and drops execution durations, call text, capability reasons, denominators and zero kind counts.")] bool brief = false,
        [Description(Shaping.MaxStringLengthDescription)] int? maxStringLength = null,
        [Description(Tools.IncludeProvenanceDescription)] bool includeProvenance = false,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 1000) throw new PixToolException(PixErrors.Codes.InvalidArguments, "limit must be between 1 and 1000.");
        if (frameIndex is < 0) throw PixErrors.InvalidArguments("frameIndex must be nonnegative.");
        ScopeSelection selection = EventScope.Resolve(session, handle, null, scope, markerPathPrefix);
        ShapingOptions shaping = Shaping.Options(format, brief, null, maxStringLength, 0);
        if (!includeTiming)
            return Tools.Run(session, "pix_gpu_overview", () => Present(QueryOverview(session.Get<GpuCaptureHandle>(handle), false, limit, selection, frameIndex: frameIndex)!, shaping), cancellationToken);
        return Tools.RunWhenReadyOrPartial(session, jobs, "pix_gpu_overview", handle, CountersTools.TimingPreparation(handle),
            (h, onWorker) => QueryOverview(h, false, limit, selection, onWorker, frameIndex),
            h => Present(QueryOverview(h, true, limit, selection, frameIndex: frameIndex, includeInsights: includeInsights)!, shaping),
            (partial, section) => partial is CaptureOverviewDto metadata ? Present(metadata with { Timing = section, NextCalls = section.NextCalls }, shaping) : null,
            waitSeconds, cancellationToken);
    }

    /// <summary>Applies brief (marker paths trimmed; execution durations, call text, capability reasons, denominators, zero kinds and empty buckets dropped), table sections and string truncation.</summary>
    internal static object Present(CaptureOverviewDto overview, ShapingOptions shaping)
    {
        if (shaping.Brief)
        {
            static IReadOnlyList<string> Tail(IReadOnlyList<string> path) => path.Count > 2 ? path.Skip(path.Count - 2).ToArray() : path;
            overview = overview with
            {
                Capabilities = overview.Capabilities.ToDictionary(c => c.Key, c => new CapabilityDto(c.Value.State)),
                Denominators = null,
                Queues = overview.Queues.Select(q => q with { Kinds = q.Kinds.Where(k => k.Value > 0).ToDictionary(k => k.Key, k => k.Value) }).ToArray(),
                Histogram = overview.Histogram is null ? null : overview.Histogram with { Buckets = overview.Histogram.Buckets.Where(b => b.Count > 0).ToArray() },
                TopPasses = overview.TopPasses.Select(p => p with { MarkerPath = Tail(p.MarkerPath) }).ToArray(),
                TopDraws = overview.TopDraws.Select(d => d with { MarkerPath = Tail(d.MarkerPath), Exec = null, Parameters = null }).ToArray(),
                Overlap = overview.Overlap is null ? null : overview.Overlap with { Queues = null },
            };
        }
        if (!shaping.Table) return RowShapes.Finish(overview, shaping);
        System.Text.Json.Nodes.JsonObject node = JsonSerializer.SerializeToNode(overview, Json.Options)!.AsObject();
        node["topPasses"] = JsonSerializer.SerializeToNode(Shaping.Apply(overview.TopPasses, overview.TopPasses.Count, 0, Math.Max(1, overview.TopPasses.Count), shaping,
            RowShapes.OverviewPasses, overview.Handle, null, null, null), Json.Options);
        node["topDraws"] = JsonSerializer.SerializeToNode(Shaping.Apply(overview.TopDraws, overview.TopDraws.Count, 0, Math.Max(1, overview.TopDraws.Count), shaping,
            RowShapes.OverviewWork, overview.Handle, null, null, null), Json.Options);
        return RowShapes.Finish(node, shaping);
    }

    /// <summary>
    /// Gathers the overview inputs from the handle. With <paramref name="onWorker"/> false (a caller joining a running timing job)
    /// only cached event arrays are read; returns null when a scope or frame needs events that are not cached yet.
    /// </summary>
    internal static CaptureOverviewDto? QueryOverview(GpuCaptureHandle h, bool timing, int limit, ScopeSelection? selection = null, bool onWorker = true, int? frameIndex = null,
        bool includeInsights = true)
    {
        ScopeSelection scope = selection ?? new ScopeSelection(h.Id, null, null, null, null);
        var queues = new List<OverviewQueueInput>();
        foreach (QueueEntry queue in h.Queues)
        {
            EventRecord[]? events = onWorker ? h.AllEvents(queue.Index) : h.CachedEvents(queue.Index);
            int[]? children = events is null ? null : onWorker ? h.ChildCounts(queue.Index) : EventNavigation.ChildCounts(events);
            queues.Add(new(queue.Index, queue.Name, Json.EnumName(queue.Type), queue.EventCount, events, children,
                timing && events is not null ? h.TimingTreeFor(queue.Index) : null, timing ? h.TimingRowsByQueue.GetValueOrDefault(queue.Index, []) : null));
        }
        // Registry notes stay in pix_gpu_info: they grow with every observed vendor behaviour and would crowd the Level 0 answer.
        IReadOnlyDictionary<string, CapabilityDto> capabilities = h.CapabilitiesSnapshot().ToDictionary(c => c.Key, c => c.Value with { Notes = null });
        return OverviewBuilder.BuildScoped(new OverviewInputs(h.Id, h.Path, h.CachedCaptureVendor, queues, capabilities, timing ? h.Provenance() : null, timing,
            (_, _) => true, null, h.Experiments is not null), new OverviewOptions(limit, frameIndex, includeInsights), scope);
    }
}
