using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using PixMcp.Pix.Handles;

namespace PixMcp.Tools;

public enum InspectionSection { Timing, Pipeline, Shaders, Bindings }

[McpServerToolType]
public static class InspectionTools
{
    [McpServerTool(Name = "pix_gpu_inspect_event", ReadOnly = true), Description("Inspect an event with marker context, replay timing, shader identities, pipeline summary, resource bindings and actual root constants. Default sections are Timing, Pipeline, Shaders and Bindings. Expensive preparation runs as a shared job. Use includeDetails for decoded PSO/root-signature details; page views and bindings independently.")]
    public static Task<string> InspectEvent(PixSession session, JobManager jobs, EventRef eventRef,
        InspectionSection[]? sections = null, bool includeDetails = false,
        int viewOffset = 0, int viewLimit = 10, int bindingOffset = 0, int bindingLimit = 10,
        [Description(Tools.ReadyWaitDescription)] double waitSeconds = Tools.DefaultReadyWaitSeconds,
        CancellationToken cancellationToken = default)
    {
        ReferenceValidation.Event(session, eventRef);
        if (viewOffset < 0 || bindingOffset < 0 || viewLimit is < 1 or > 1000 || bindingLimit is < 1 or > 1000)
            throw new McpException("Offsets must be nonnegative and limits must be between 1 and 1000.");
        sections ??= Enum.GetValues<InspectionSection>();
        if (sections.Any(s => !Enum.IsDefined(s))) throw new McpException("sections contains an unknown inspection section.");
        if (sections.Length == 0)
            return Tools.Run(session, "pix_gpu_inspect_event", () =>
            {
                GpuCaptureHandle h = session.Get<GpuCaptureHandle>(eventRef.Handle);
                EventDto record = h.DescribeEvent(eventRef.QueueIndex, h.Event(eventRef.QueueIndex, eventRef.EventIndex));
                return new EventInspectionDto(eventRef, record.MarkerPath ?? [], record, null, null, null, []);
            }, cancellationToken);
        bool timing = sections.Contains(InspectionSection.Timing);
        bool bindings = sections.Contains(InspectionSection.Bindings);
        bool pipeline = sections.Contains(InspectionSection.Pipeline);
        bool shaders = sections.Contains(InspectionSection.Shaders);
        string key = "inspection:" + timing + ":" + bindings;
        var preparation = new Preparation<GpuCaptureHandle>(key, "event-inspection", $"Prepare event inspection for {eventRef.Handle}",
            h => h.AnalysisStarted && (!timing || h.Timing is not null) &&
                (!bindings || h.AccessedResourcesGathered || h.AccessedResourcesUnavailable is not null),
            (h, job) =>
            {
                h.EventInfo(eventRef.QueueIndex, eventRef.EventIndex);
                h.EnsureAnalysisStarted(job);
                if (timing) CountersTools.CollectTiming(h, job);
                if (bindings) PrepareOptionalBindings(() => h.EnsureAccessedResources(job));
            });
        return Tools.RunWhenReady(session, jobs, "pix_gpu_inspect_event", eventRef.Handle, preparation, h =>
        {
            EventRecord record = h.Event(eventRef.QueueIndex, eventRef.EventIndex);
            var coverage = new List<object>();
            object? eventTiming = null;
            if (timing)
            {
                EventTimingRow? row = h.TimingRowsByQueue.GetValueOrDefault(eventRef.QueueIndex)?.FirstOrDefault(r => r.Index == eventRef.EventIndex);
                eventTiming = row is null ? new { unavailable = true, reason = "No replay timing row exists for this event." }
                    : new { topStartNs = AvailableTime(row.TopStart), topDurationNs = AvailableTime(row.TopDuration),
                        eopStartNs = AvailableTime(row.EopStart), eopDurationNs = AvailableTime(row.EopDuration),
                        measured = true, derived = false, provenance = h.Provenance() };
            }
            PipelineStateDto? pipelineState = null;
            if (pipeline || shaders)
            {
                try { pipelineState = PipelineTools.QueryPipelineState(h, eventRef, pipeline && includeDetails, pipeline && includeDetails, shaders); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { coverage.Add(PixErrors.Unavailable("pipeline", ex)); }
            }
            EventResourcesDto? resourceBindings = null;
            if (bindings)
            {
                try
                {
                    if (h.AccessedResourcesUnavailable is Exception unavailable) throw unavailable;
                    resourceBindings = ResourceTools.QueryEventResources(h, eventRef, null, bindingOffset, bindingLimit, viewOffset, viewLimit);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { coverage.Add(PixErrors.Unavailable("bindings", ex)); }
            }
            var nextCalls = new List<ToolCallDto>();
            if (!includeDetails && pipelineState is not null)
                nextCalls.Add(new("pix_gpu_pipeline_state", new { eventRef, includeSubobjects = true, includeRootSignature = true }));
            ShaderInfoDto? firstShader = (pipelineState?.Shaders as IEnumerable<object>)?.OfType<ShaderInfoDto>()
                .FirstOrDefault(s => s.ShaderRef is not null && s.AvailableCode.Any(c => c is "HLSL" or "IL" or "ISA"));
            if (firstShader is not null)
                nextCalls.Add(new("pix_gpu_shader_code", new { shaderRef = firstShader.ShaderRef,
                    codeType = firstShader.AvailableCode.First(c => c is "HLSL" or "IL" or "ISA") }));
            ResourceRef? firstResource = (resourceBindings?.Resources.FirstOrDefault()?.Resource as ResourceDetailsDto)?.ResourceRef;
            if (firstResource is not null) nextCalls.Add(new("pix_gpu_resource_uses", new { resourceRef = firstResource }));
            return new EventInspectionDto(eventRef, EventNavigation.MarkerPath(h.AllEvents(eventRef.QueueIndex), eventRef.EventIndex),
                h.DescribeEvent(eventRef.QueueIndex, record), eventTiming, pipelineState, resourceBindings, coverage)
                { NextCalls = nextCalls };
        }, waitSeconds, cancellationToken);
    }

    internal static ulong? AvailableTime(ulong value) => value == GpuCaptureHandle.TimingNone ? null : value;

    internal static void PrepareOptionalBindings(Action prepare)
    {
        try { prepare(); }
        catch (Exception ex) when (PixErrors.ToDto(ex).Code == "unsupported_feature") { }
    }
}
