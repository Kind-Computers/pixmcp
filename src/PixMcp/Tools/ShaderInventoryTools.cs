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
    [McpServerTool(Name = "pix_gpu_shaders", ReadOnly = true), Description("Discover shaders across a capture, grouped by available hash and stage with representative shader references and event-use counts. Missing hashes remain individual occurrences. Builds one shared metadata index as a job; filters and subsequent pages reuse it.")]
    public static Task<string> Shaders(PixSession session, JobManager jobs, string handle,
        string? stage = null, string? entryContains = null, string? hash = null,
        [Description(EventScope.Description)] EventRef? scope = null,
        int offset = 0, int limit = Paging.DefaultLimit,
        [Description(Tools.ReadyWaitDescription)] double waitSeconds = Tools.DefaultReadyWaitSeconds,
        CancellationToken cancellationToken = default)
    {
        ReferenceValidation.Page(offset, limit);
        session.Get<GpuCaptureHandle>(handle);
        EventScope.ResolveQueue(session, handle, null, scope);
        return Tools.RunWhenReady(session, jobs, "pix_gpu_shaders", handle, GpuCaptureHandle.ShaderIndexPreparation(handle), h =>
        {
            ShaderIndex index = h.ShaderIndex!;
            IReadOnlyList<ShaderInventoryItemDto> rows = index.Inventory(stage, entryContains, hash,
                candidate => EventScope.Contains(h, candidate, scope), scope);
            ShaderInventoryItemDto[] items = rows.Skip(offset).Take(limit).ToArray();
            int? next = offset + (long)items.Length < rows.Count ? offset + items.Length : null;
            return new ShaderInventoryDto(handle, rows.Count, offset, items.Length, next, items, index.Coverage,
                next.HasValue ? [new("pix_gpu_shaders", new { handle, stage, entryContains, hash, scope, offset = next.Value, limit })] : []);
        }, waitSeconds, cancellationToken);
    }

    [McpServerTool(Name = "pix_gpu_shader_uses", ReadOnly = true), Description("Find events using the selected shader by hash and stage in the same capture. A missing hash only matches the exact shader occurrence. Includes marker paths and all matching shader slots at each event; uses the shared capture shader index.")]
    public static Task<string> Uses(PixSession session, JobManager jobs, ShaderRef shaderRef,
        [Description(EventScope.Description)] EventRef? scope = null,
        int offset = 0, int limit = Paging.DefaultLimit,
        [Description(Tools.ReadyWaitDescription)] double waitSeconds = Tools.DefaultReadyWaitSeconds,
        CancellationToken cancellationToken = default)
    {
        ReferenceValidation.Shader(session, shaderRef);
        ReferenceValidation.Page(offset, limit);
        string handle = shaderRef.EventRef.Handle;
        EventScope.ResolveQueue(session, handle, null, scope);
        return Tools.RunWhenReady(session, jobs, "pix_gpu_shader_uses", handle, GpuCaptureHandle.ShaderIndexPreparation(handle), h =>
        {
            ShaderIndex index = h.ShaderIndex!;
            var (method, rows) = index.Uses(shaderRef, candidate => EventScope.Contains(h, candidate, scope));
            ShaderUseEventDto[] items = rows.Skip(offset).Take(limit).ToArray();
            int? next = offset + (long)items.Length < rows.Count ? offset + items.Length : null;
            return new ShaderUsesDto(shaderRef, method, rows.Count, offset, items.Length, next, items, index.Coverage,
                next.HasValue ? [new("pix_gpu_shader_uses", new { shaderRef, scope, offset = next.Value, limit })] : []);
        }, waitSeconds, cancellationToken);
    }

    internal static ShaderIndex BuildIndex(GpuCaptureHandle h, Job job)
    {
        job.AddMessage("Reading bound shader identities across draw and dispatch events...");
        var occurrences = new List<ShaderOccurrence>();
        var coverage = new List<object>();
        foreach (QueueEntry queue in h.Queues)
        {
            EventRecord[] events = h.AllEvents(queue.Index);
            foreach (EventRecord record in events)
            {
                job.ThrowIfCancellationRequested();
                if (!Tools.MatchesKind(record, "drawOrDispatch")) continue;
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
                        catch (Exception ex) { coverage.Add(new { eventRef, shaderIndex = slot, unavailable = true, reason = PixErrors.Describe(ex) }); }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { coverage.Add(new { eventRef, unavailable = true, reason = PixErrors.Describe(ex) }); }
            }
        }
        job.ThrowIfCancellationRequested();
        return new(occurrences, coverage);
    }
}
