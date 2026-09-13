using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using PixMcp.Pix.Handles;

namespace PixMcp.Tools;

public enum InspectionSection { timing, pipeline, shaders, bindings }

[McpServerToolType]
public static class InspectionTools
{
    [McpServerTool(Name = "pix_gpu_inspect_event", Title = "Inspect event", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Replays the capture on the local GPU if analysis is not started. Inspect an event with marker context, replay timing, shader identities, pipeline summary, resource bindings and actual root constants. Default sections are Timing, Pipeline, Shaders and Bindings. Expensive preparation runs as a shared job. Use includeDetails for decoded PSO/root-signature details; page views and bindings independently.")]
    public static Task<string> InspectEvent(PixSession session, JobManager jobs, [Description("Event reference { handle, queueIndex, eventIndex } as returned by pix_gpu_events or pix_gpu_overview.")] EventRef eventRef,
        [Description("Sections to return: timing, pipeline, shaders, bindings (default all four).")] InspectionSection[]? sections = null, [Description("Include pipeline subobjects and the root signature (default false; pix_gpu_pipeline_state returns them on demand).")] bool includeDetails = false,
        [Description("First resource view to return (default 0).")] int viewOffset = 0, [Description("Maximum resource views per binding (default 10, max 1000).")] int viewLimit = 10, [Description("First binding to return (default 0).")] int bindingOffset = 0, [Description("Maximum bindings to return (default 10, max 1000).")] int bindingLimit = 10,
        [Description(Tools.ReadyWaitDescription)] double waitSeconds = Tools.DefaultReadyWaitSeconds,
        [Description(Shaping.MaxStringLengthDescription)] int? maxStringLength = null,
        [Description(Tools.IncludeProvenanceDescription)] bool includeProvenance = false,
        CancellationToken cancellationToken = default)
    {
        ReferenceValidation.Event(session, eventRef);
        ShapingOptions shaping = Shaping.Options("objects", false, null, maxStringLength, 0);
        if (viewOffset < 0 || bindingOffset < 0 || viewLimit is < 1 or > 1000 || bindingLimit is < 1 or > 1000)
            throw PixErrors.InvalidArguments("Offsets must be nonnegative and limits must be between 1 and 1000.");
        string sectionNames = string.Join(", ", Enum.GetNames<InspectionSection>());
        sections ??= Enum.GetValues<InspectionSection>();
        if (sections.Any(s => !Enum.IsDefined(s))) throw PixErrors.InvalidArguments($"sections contains an unknown inspection section. Valid sections: {sectionNames}.");
        if (sections.Length == 0) throw PixErrors.InvalidArguments($"sections must name at least one of: {sectionNames}.",
            [new("pix_gpu_inspect_event", new { eventRef, sections = Enum.GetNames<InspectionSection>() })]);
        bool timing = sections.Contains(InspectionSection.timing);
        bool bindings = sections.Contains(InspectionSection.bindings);
        bool pipeline = sections.Contains(InspectionSection.pipeline);
        bool shaders = sections.Contains(InspectionSection.shaders);
        // One key per set of needed state; every related running job (analysis, timing, resources, another inspection
        // variant) is joined first, then the missing state is prepared.
        string key = (timing, bindings) switch
        {
            (true, true) => PreparationKeys.InspectionTimingBindings,
            (true, false) => PreparationKeys.InspectionTiming,
            (false, true) => PreparationKeys.InspectionBindings,
            _ => PreparationKeys.Inspection,
        };
        string[] joinKeys = new[] { PreparationKeys.Analysis, PreparationKeys.Timing, PreparationKeys.AccessedResources, PreparationKeys.Inspection,
            PreparationKeys.InspectionTiming, PreparationKeys.InspectionBindings, PreparationKeys.InspectionTimingBindings }.Where(k => k != key).ToArray();
        var preparation = new Preparation<GpuCaptureHandle>(key, "event-inspection", $"Prepare event inspection ({key}) for {eventRef.Handle}",
            h => h.AnalysisStarted && (!timing || h.Timing is not null) &&
                (!bindings || h.AccessedResourcesGathered || h.AccessedResourcesUnavailable is not null),
            (h, job) =>
            {
                h.EventInfo(eventRef.QueueIndex, eventRef.EventIndex);
                h.EnsureAnalysisStarted(job);
                if (timing) CountersTools.CollectTiming(h, job);
                if (bindings) PrepareOptionalBindings(() => h.EnsureAccessedResources(job));
            }) { JoinKeys = joinKeys };
        return Tools.RunWhenReadyOrPartial(session, jobs, "pix_gpu_inspect_event", eventRef.Handle, preparation,
            (h, onWorker) =>
            {
                EventRecord[]? events = onWorker ? h.AllEvents(eventRef.QueueIndex) : h.CachedEvents(eventRef.QueueIndex);
                if (events is null || eventRef.EventIndex >= events.Length) return null;
                string[] path = EventNavigation.MarkerPath(events, eventRef.EventIndex);
                return new EventInspectionDto(eventRef, path, events[eventRef.EventIndex].ToDto(eventRef.QueueIndex) with { EventRef = eventRef, MarkerPath = path }, null, null, null, []);
            },
            h => RowShapes.Finish(Inspect(h), shaping),
            (partial, section) => partial is not EventInspectionDto metadata ? null : metadata with
            {
                Timing = timing ? section : null,
                Pipeline = pipeline || shaders ? section : null,
                Bindings = bindings ? section : null,
                Preparation = section,
                Coverage = new[] { (name: "timing", wanted: timing), (name: "pipeline", wanted: pipeline || shaders), (name: "bindings", wanted: bindings) }
                    .Where(x => x.wanted).Select(x => (object)new { section = x.name, pending = true, jobId = section.JobId }).ToArray(),
                NextCalls = section.NextCalls,
            }, waitSeconds, cancellationToken);

        EventInspectionDto Inspect(GpuCaptureHandle h)
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
        }
    }

    internal static ulong? AvailableTime(ulong value) => value == GpuCaptureHandle.TimingNone ? null : value;

    internal static void PrepareOptionalBindings(Action prepare)
    {
        try { prepare(); }
        catch (Exception ex) when (PixErrors.ToDto(ex).Code == "unsupported_feature") { }
    }
}
