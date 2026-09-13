using System.ComponentModel;
using Microsoft.PIX;
using Microsoft.PIX.Extension;
using Microsoft.PIX.Extension.GpuCapture.Resources;
using Microsoft.PIX.Internal;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using PixMcp.Pix.Handles;

namespace PixMcp.Tools;

[McpServerToolType]
public static class ShaderInventoryTools
{
    [McpServerTool(Name = "pix_gpu_shaders", Title = "Shader inventory", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Replays the capture on the local GPU if analysis is not started. Discover shaders across a capture, grouped by available hash and stage with representative shader references and event-use counts. Missing hashes remain individual occurrences. Builds one shared metadata index as a job; filters and subsequent pages reuse it.")]
    public static Task<string> Shaders(PixSession session, JobManager jobs, [Description("GPU capture handle (from pix_gpu_open).")] string handle,
        [Description("Only shaders of this stage (VS, PS, CS, ...).")] string? stage = null, [Description("Only shaders whose entry point contains this text.")] string? entryContains = null, [Description("Only the shader with this hash.")] string? hash = null,
        [Description(EventScope.Description)] EventRef? scope = null,
        [Description(EventScope.PrefixDescription)] string? markerPathPrefix = null,
        [Description("First item to return (default 0).")] int offset = 0, [Description("Maximum items to return (default 25, max 1000).")] int limit = Paging.DefaultLimit,
        [Description(Tools.ReadyWaitDescription)] double waitSeconds = Tools.DefaultReadyWaitSeconds,
        [Description(Shaping.FormatDescription)] string format = "objects",
        [Description(Shaping.BriefDescription)] bool brief = false,
        [Description(Shaping.TopNDescription)] int? topN = null,
        [Description(Shaping.MaxStringLengthDescription)] int? maxStringLength = null,
        [Description("stage (default: stage, entry, hash), entry, hash, useCount or gpuTime (summed replay EOP of the events using the shader; collects timing first).")] string sortBy = "stage",
        CancellationToken cancellationToken = default)
    {
        ReferenceValidation.Page(offset, limit);
        session.Get<GpuCaptureHandle>(handle);
        ScopeSelection selection = EventScope.Resolve(session, handle, null, scope, markerPathPrefix);
        ShapingOptions shaping = Shaping.Options(format, brief, topN, maxStringLength, offset);
        string sort = RollupTools.Canonical(sortBy, ShaderIndex.SortKeys, "sortBy");
        Preparation<GpuCaptureHandle> preparation = sort == "gpuTime"
            ? Tools.Combine(handle, [GpuCaptureHandle.ShaderIndexPreparation(handle), CountersTools.TimingPreparation(handle)])
            : GpuCaptureHandle.ShaderIndexPreparation(handle);
        return Tools.RunWhenReady(session, jobs, "pix_gpu_shaders", handle, preparation, h =>
        {
            ShaderIndex index = h.ShaderIndex!;
            IReadOnlyList<ShaderInventoryItemDto> rows = index.Inventory(stage, entryContains, hash,
                candidate => selection.Contains(h, candidate), scope, sort, h.Timing is null ? null : EopLookup(h));
            (int o, int l) = Shaping.Window(shaping, offset, limit);
            ShaderInventoryItemDto[] items = rows.Skip(o).Take(l).ToArray();
            ToolCallDto Call(int at, int? strings) => new("pix_gpu_shaders", new { handle, stage, entryContains, hash, scope, markerPathPrefix, offset = at, limit = l, format, brief, maxStringLength = strings, sortBy = sort });
            if (shaping.Table)
                return Shaping.Apply(items, rows.Count, o, l, shaping, RowShapes.Shaders, handle, new { coverage = index.Coverage, scope = selection.DescribeOrNull(h) },
                    next => Call(next, maxStringLength), () => Call(o, Shaping.FullStringLength));
            if (shaping.Brief) items = items.Select(RowShapes.Shaders.Brief!).ToArray();
            int? next = !shaping.TopN.HasValue && o + (long)items.Length < rows.Count ? o + items.Length : null;
            return RowShapes.Finish(new ShaderInventoryDto(handle, rows.Count, o, items.Length, next, items, index.Coverage,
                next.HasValue ? [Call(next.Value, maxStringLength)] : []) { Scope = selection.DescribeOrNull(h) }, shaping);
        }, waitSeconds, cancellationToken);
    }

    [McpServerTool(Name = "pix_gpu_shader_uses", Title = "Shader uses", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Replays the capture on the local GPU if analysis is not started. Find events using the selected shader by hash and stage in the same capture. A missing hash only matches the exact shader occurrence. Includes marker paths and all matching shader slots at each event; uses the shared capture shader index.")]
    public static Task<string> Uses(PixSession session, JobManager jobs,
        [Description("Shader reference { eventRef, shaderIndex } as returned by pix_gpu_pipeline_state or pix_gpu_shaders. Exactly one of shaderRef or shaderKey.")] ShaderRef? shaderRef = null,
        [Description(EventScope.Description)] EventRef? scope = null,
        [Description(EventScope.PrefixDescription)] string? markerPathPrefix = null,
        [Description("First item to return (default 0).")] int offset = 0, [Description("Maximum items to return (default 25, max 1000).")] int limit = Paging.DefaultLimit,
        [Description(Tools.ReadyWaitDescription)] double waitSeconds = Tools.DefaultReadyWaitSeconds,
        [Description(ShaderIdentity.KeyDescription + " Exactly one of shaderRef or shaderKey; shaderKey needs handle.")] string? shaderKey = null,
        [Description("GPU capture handle for shaderKey.")] string? handle = null,
        CancellationToken cancellationToken = default)
    {
        if ((shaderRef is null) == (shaderKey is null)) throw PixErrors.InvalidArguments("Pass exactly one of shaderRef or shaderKey.");
        string? canonicalKey = null;
        if (shaderKey is not null)
        {
            if (handle is null) throw PixErrors.InvalidArguments("shaderKey needs handle.", [new("pix_handles", new { })]);
            if (!ShaderIdentity.TryParseShaderKey(shaderKey, out string keyStage, out string keyHash))
                throw PixErrors.InvalidArguments("shaderKey must look like hash:STAGE:HASH.", [new("pix_gpu_shaders", new { handle })]);
            canonicalKey = ShaderIdentity.ShaderKey(keyStage, keyHash)!;
            session.Get<GpuCaptureHandle>(handle);
        }
        else ReferenceValidation.Shader(session, shaderRef!);
        ReferenceValidation.Page(offset, limit);
        string capture = shaderRef?.EventRef.Handle ?? handle!;
        ScopeSelection selection = EventScope.Resolve(session, capture, null, scope, markerPathPrefix);
        return Tools.RunWhenReady(session, jobs, "pix_gpu_shader_uses", capture, GpuCaptureHandle.ShaderIndexPreparation(capture), h =>
        {
            ShaderIndex index = h.ShaderIndex!;
            ShaderRef reference = shaderRef ?? index.FirstReference(canonicalKey!)
                ?? throw PixErrors.InvalidReference($"No indexed shader in {capture} has key {canonicalKey}.", new ToolCallDto("pix_gpu_shaders", new { handle = capture }, CostHints.Cached));
            var (method, rows) = index.Uses(reference, candidate => selection.Contains(h, candidate));
            ShaderUseEventDto[] items = rows.Skip(offset).Take(limit).ToArray();
            int? next = offset + (long)items.Length < rows.Count ? offset + items.Length : null;
            ShaderInfoDto? identity = index.ShadersAt(reference.EventRef).FirstOrDefault(s => s.ShaderRef == reference);
            return new ShaderUsesDto(reference, method, rows.Count, offset, items.Length, next, items, index.Coverage,
                next.HasValue ? [new("pix_gpu_shader_uses", new { shaderRef, shaderKey = canonicalKey, handle = canonicalKey is null ? null : capture, scope, markerPathPrefix, offset = next.Value, limit })] : [])
            { Scope = selection.DescribeOrNull(h), ShaderKey = canonicalKey ?? (identity is null ? null : ShaderIdentity.ShaderKey(identity.Stage, identity.Hash)) };
        }, waitSeconds, cancellationToken);
    }

    /// <summary>EOP nanoseconds per event from the collected timing rows.</summary>
    internal static Func<EventRef, ulong?> EopLookup(GpuCaptureHandle h)
    {
        var byQueue = h.TimingRowsByQueue.ToDictionary(kv => kv.Key, kv => kv.Value.Where(r => r.EopDuration != GpuCaptureHandle.TimingNone)
            .GroupBy(r => r.Index).ToDictionary(g => g.Key, g => g.First().EopDuration));
        return e => byQueue.TryGetValue(e.QueueIndex, out Dictionary<uint, ulong>? rows) && rows.TryGetValue(e.EventIndex, out ulong ns) ? ns : null;
    }

    internal static ShaderIndex BuildIndex(GpuCaptureHandle h, Job job)
    {
        job.AddMessage("Reading bound shader identities across draw and dispatch events...");
        var occurrences = new List<ShaderOccurrence>();
        var coverage = new List<object>();
        var incomplete = new HashSet<EventRef>();
        foreach (QueueEntry queue in h.Queues)
        {
            EventRecord[] events = h.AllEvents(queue.Index);
            foreach (EventRecord record in events)
            {
                job.ThrowIfCancellationRequested();
                if (!Tools.MatchesKind(record, "work")) continue;
                var eventRef = new EventRef(h.Id, queue.Index, record.Index);
                try
                {
                    IPixCollection shaders = PixApiExtensionsGpuCaptureResources.GetShaders(PipelineTools.ReadProgram(h, eventRef));
                    for (ulong slot = 0; slot < shaders.GetCount(); slot++)
                    {
                        job.ThrowIfCancellationRequested();
                        try
                        {
                            var shader = shaders.Get<IPixShader>(slot);
                            ShaderInfoDto identity = PipelineTools.ShaderDto(checked((int)slot), shader, eventRef);
                            occurrences.Add(new(identity, EventNavigation.MarkerPath(events, record.Index)));
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { incomplete.Add(eventRef); coverage.Add(new { eventRef, shaderIndex = slot, unavailable = true, reason = PixErrors.Describe(ex) }); }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { incomplete.Add(eventRef); coverage.Add(new { eventRef, unavailable = true, reason = PixErrors.Describe(ex) }); }
            }
        }
        job.ThrowIfCancellationRequested();
        return new(occurrences, coverage, incomplete);
    }
}
