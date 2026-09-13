using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using PixMcp.Pix.Handles;

namespace PixMcp.Tools;

/// <summary>Sections of pix_gpu_inspect_event.</summary>
public enum InspectionSection { timing, pipeline, shaders, bindings, targets, counters, occupancy, hf, hints }

[McpServerToolType]
public static class InspectionTools
{
    internal static readonly InspectionSection[] DefaultSections =
        [InspectionSection.timing, InspectionSection.pipeline, InspectionSection.shaders, InspectionSection.bindings, InspectionSection.targets, InspectionSection.hints];

    /// <summary>Preparation key for sections that read cached data only.</summary>
    internal const string CachedOnly = "inspection:cached";

    [McpServerTool(Name = "pix_gpu_inspect_event", Title = "Inspect event", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Replays the capture on the local GPU if analysis is not started. Inspect one event: marker context, kind, the API call parsed into named arguments and work items, replay timing ranked within its queue and parent with sibling statistics and pipelining, pipeline summary, shader identities, a resource binding summary with actual root constants (pix_gpu_event_resources returns full resource details), bound render targets with pixel counts, cached counter values, and hints about what is unusual. Default sections: timing, pipeline, shaders, bindings, targets, hints. Replay preparation (analysis, timing, accessed resources) is one shared job; on a cold capture the event, kind and parameters return at once with pending sections. Use includeDetails for decoded PSO/root-signature details; page views and bindings independently.")]
    public static Task<string> InspectEvent(PixSession session, JobManager jobs, [Description("Event reference { handle, queueIndex, eventIndex } as returned by pix_gpu_events or pix_gpu_overview.")] EventRef eventRef,
        [Description("Sections to return: timing, pipeline, shaders, bindings, targets, counters, occupancy, hf, hints (default timing, pipeline, shaders, bindings, targets and hints). counters, occupancy and hf read data already collected on the handle (PIX per-event occupancy points, high-frequency window statistics) and never collect.")] InspectionSection[]? sections = null,
        [Description("Include pipeline subobjects and the root signature (default false; pix_gpu_pipeline_state returns them on demand).")] bool includeDetails = false,
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
        sections ??= DefaultSections;
        if (sections.Any(s => !Enum.IsDefined(s))) throw PixErrors.InvalidArguments($"sections contains an unknown inspection section. Valid sections: {sectionNames}.");
        if (sections.Length == 0) throw PixErrors.InvalidArguments($"sections must name at least one of: {sectionNames}.",
            [new("pix_gpu_inspect_event", new { eventRef, sections = DefaultSections.Select(s => s.ToString()).ToArray() })]);
        InspectionSection[] selected = sections.Distinct().ToArray();
        bool Want(InspectionSection section) => selected.Contains(section);
        int q = eventRef.QueueIndex;
        uint i = eventRef.EventIndex;
        Preparation<GpuCaptureHandle> preparation = PreparationFor(selected) switch
        {
            PreparationKeys.Inspection => InspectionPreparation(eventRef.Handle, Want(InspectionSection.timing) || Want(InspectionSection.hints), Want(InspectionSection.bindings)),
            PreparationKeys.Analysis => GpuCaptureHandle.AnalysisPreparation(eventRef.Handle),
            _ => new(CachedOnly, "event-inspection", $"Read cached inspection data for {eventRef.Handle}", _ => true, (_, _) => { }),
        };
        return Tools.RunWhenReadyOrPartial(session, jobs, "pix_gpu_inspect_event", eventRef.Handle, preparation,
            (h, onWorker) =>
            {
                EventRecord[]? events = onWorker ? h.AllEvents(q) : h.CachedEvents(q);
                if (events is null || i >= events.Length) return null;
                int[] children = onWorker ? h.ChildCounts(q) : EventNavigation.ChildCounts(events);
                EventRecord record = events[i];
                string[] path = EventNavigation.MarkerPath(events, i);
                return new EventInspectionDto(eventRef, path, record.ToDto(q) with { EventRef = eventRef, MarkerPath = path },
                    Tools.Classify(record, children[i] > 0), ApiCallParser.Parse(record.ApiCallData, record.Name), null, null, null, []);
            },
            h => RowShapes.Finish(Inspect(h), shaping),
            (partial, section) =>
            {
                if (partial is not EventInspectionDto metadata) return null;
                var pending = new InspectionPendingDto(true, section.JobId);
                object? Pending(bool wanted) => wanted ? pending : null;
                return RowShapes.Finish(metadata with
                {
                    Timing = Pending(Want(InspectionSection.timing)),
                    Pipeline = Pending(Want(InspectionSection.pipeline) || Want(InspectionSection.shaders)),
                    Bindings = Pending(Want(InspectionSection.bindings)),
                    Targets = Pending(Want(InspectionSection.targets)),
                    Counters = Pending(Want(InspectionSection.counters)),
                    Occupancy = Pending(Want(InspectionSection.occupancy)),
                    Hf = Pending(Want(InspectionSection.hf)),
                    Hints = Pending(Want(InspectionSection.hints)),
                    Preparation = section,
                    Coverage = selected.Select(s => (object)new { section = s.ToString(), pending = true, jobId = section.JobId }).ToArray(),
                    NextCalls = section.NextCalls,
                }, shaping);
            }, waitSeconds, cancellationToken);

        EventInspectionDto Inspect(GpuCaptureHandle h)
        {
            EventRecord[] events = h.AllEvents(q);
            int[] children = h.ChildCounts(q);
            EventRecord record = events[i];
            bool hasChildren = children[i] > 0;
            string kind = Tools.Classify(record, hasChildren);
            ApiCallDto? call = ApiCallParser.Parse(record.ApiCallData, record.Name);
            uint? parentIndex = record.ParentIndex < events.Length && record.ParentIndex != i ? record.ParentIndex : null;
            var coverage = new List<object>();

            EventTimingInspectionDto? timingDto = h.Timing is not null
                && (Want(InspectionSection.timing) || Want(InspectionSection.hints) || Want(InspectionSection.targets) || Want(InspectionSection.counters))
                ? EventInspection.Timing(new EventInspectionInput(h.Id, q, events, children, h.TimingRowsByQueue.GetValueOrDefault(q, []), h.TimingTreeFor(q), call), i)
                : null;

            PipelineStateDto? pipelineState = null;
            if (Want(InspectionSection.pipeline) || Want(InspectionSection.shaders))
            {
                bool details = Want(InspectionSection.pipeline) && includeDetails;
                try { pipelineState = PipelineTools.QueryPipelineState(h, eventRef, details, details, Want(InspectionSection.shaders)); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { coverage.Add(PixErrors.Unavailable("pipeline", ex)); }
            }

            EventResourcesDto? resourceBindings = null;
            if (Want(InspectionSection.bindings))
            {
                try
                {
                    if (h.AccessedResourcesUnavailable is Exception unavailable) throw unavailable;
                    resourceBindings = ResourceTools.QueryEventResources(h, eventRef, null, bindingOffset, bindingLimit, viewOffset, viewLimit);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { coverage.Add(PixErrors.Unavailable("bindings", ex)); }
            }

            EventTargetsDto? targetsDto = null;
            if (Want(InspectionSection.targets))
            {
                targetsDto = kind is "marker" or "label"
                    ? new EventTargetsDto("notApplicable", [], $"A {kind} binds no targets of its own; inspect a work event inside it.")
                    : ResourceTools.QueryBoundTargets(h, eventRef);
                if (timingDto?.Eop is DurationDto eop && targetsDto.Targets.Count > 0)
                    targetsDto = targetsDto with { Targets = targetsDto.Targets.Select(t => EventInspection.WithCost(t, eop)).ToArray() };
            }

            EventCountersDto? countersDto = null;
            if (Want(InspectionSection.counters))
            {
                var sets = new List<CounterSetSnapshot>();
                foreach ((string key, CounterCollectionCache cache) in h.CounterCollections.OrderBy(c => c.Key, StringComparer.Ordinal).Take(EventInspection.MaxCounterSets))
                {
                    try
                    {
                        CounterEventRow? row = CountersTools.CachedCounterRows(h, cache, q).FirstOrDefault(r => r.Event.Index == i);
                        sets.Add(new(key, cache.Source, cache.Counters, row?.Values, row?.HasData ?? false));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { coverage.Add(PixErrors.Unavailable("counters", ex)); }
                }
                countersDto = EventInspection.Counters(eventRef, sets, h.CounterCollections.Count, Tools.IsMarker(record, hasChildren) ? "marker" : "event", timingDto?.Eop);
            }

            var nextCalls = new List<ToolCallDto>();
            if (!includeDetails && pipelineState is not null)
                nextCalls.Add(new("pix_gpu_pipeline_state", new { eventRef, includeSubobjects = true, includeRootSignature = true }));
            ShaderInfoDto? firstShader = (pipelineState?.Shaders as IEnumerable<object>)?.OfType<ShaderInfoDto>()
                .FirstOrDefault(s => s.ShaderRef is not null && s.AvailableCode.Any(c => c is "HLSL" or "IL" or "ISA"));
            if (firstShader is not null)
                nextCalls.Add(new("pix_gpu_shader_code", new { shaderRef = firstShader.ShaderRef,
                    codeType = firstShader.AvailableCode.First(c => c is "HLSL" or "IL" or "ISA") }));
            ResourceRef? resource = targetsDto?.Targets.Where(t => t.ResourceRef is not null).MaxBy(t => t.PixelCount)?.ResourceRef
                ?? (resourceBindings?.Resources.FirstOrDefault()?.Resource as ResourceDetailsDto)?.ResourceRef;
            if (resource is not null) nextCalls.Add(new("pix_gpu_resource_uses", new { resourceRef = resource }));
            if (resourceBindings is not null) nextCalls.Add(new("pix_gpu_event_resources", new { eventRef }));
            if (timingDto is not null && parentIndex is uint parent)
                nextCalls.Add(new("pix_gpu_timing_tree", new { handle = h.Id, scope = new EventRef(h.Id, q, parent), sortBy = "inclusive" }));

            return new EventInspectionDto(eventRef, EventNavigation.MarkerPath(events, i), h.DescribeEvent(q, record), kind, call,
                Want(InspectionSection.timing) ? timingDto : null, pipelineState is null ? null : WithoutEvent(pipelineState),
                resourceBindings is null ? null : SummarizeBindings(resourceBindings), coverage)
            {
                Targets = targetsDto,
                Counters = countersDto,
                Occupancy = Want(InspectionSection.occupancy) ? CountersTools.EventOccupancySection(h, eventRef) : null,
                Hf = Want(InspectionSection.hf) ? CountersTools.EventHfSection(h, eventRef) : null,
                Hints = Want(InspectionSection.hints) ? InspectionHints.Evaluate(eventRef, kind, parentIndex, timingDto, call, targetsDto) : null,
                Provenance = Want(InspectionSection.timing) && timingDto is not null ? h.Provenance() : null,
                NextCalls = nextCalls,
            };
        }
    }

    /// <summary>
    /// What the sections need prepared: <see cref="PreparationKeys.Inspection"/> (analysis, timing and accessed resources in
    /// one shared job) for timing, bindings and hints; <see cref="PreparationKeys.Analysis"/> for pipeline, shaders and
    /// targets; <see cref="CachedOnly"/> when only cached data (counters) or pointers (occupancy, hf) are asked for.
    /// </summary>
    internal static string PreparationFor(IReadOnlyCollection<InspectionSection> sections)
        => sections.Any(s => s is InspectionSection.timing or InspectionSection.bindings or InspectionSection.hints) ? PreparationKeys.Inspection
            : sections.Any(s => s is InspectionSection.pipeline or InspectionSection.shaders or InspectionSection.targets) ? PreparationKeys.Analysis
            : CachedOnly;

    /// <summary>
    /// The one inspection preparation: whoever starts it prepares analysis, timing and accessed resources, so callers with
    /// different sections share the job; readiness only checks what the caller's sections need.
    /// </summary>
    internal static Preparation<GpuCaptureHandle> InspectionPreparation(string handle, bool needTiming, bool needBindings)
        => new(PreparationKeys.Inspection, "event-inspection", $"Prepare event inspection (analysis, timing, accessed resources) for {handle}",
            h => h.AnalysisStarted && (!needTiming || h.Timing is not null) && (!needBindings || h.AccessedResourcesGathered || h.AccessedResourcesUnavailable is not null),
            (h, job) =>
            {
                h.EnsureAnalysisStarted(job);
                if (h.Timing is null) CountersTools.CollectTiming(h, job);
                PrepareOptionalBindings(() => h.EnsureAccessedResources(job));
            })
        { JoinKeys = [PreparationKeys.Analysis, PreparationKeys.Timing, PreparationKeys.AccessedResources] };

    /// <summary>A nested section without the event block the inspection already carries at its top level.</summary>
    internal static JsonObject WithoutEvent(object section)
    {
        JsonObject node = JsonSerializer.SerializeToNode(section, Json.Options)!.AsObject();
        node.Remove("eventRef");
        node.Remove("markerPath");
        node.Remove("event");
        return node;
    }

    /// <summary>
    /// The inspection's binding summary: each resource keeps its reference, name, type and size; views drop the resource
    /// their group already names and empty continuations. pix_gpu_event_resources returns heap, state and castable formats.
    /// </summary>
    internal static JsonObject SummarizeBindings(EventResourcesDto bindings)
    {
        JsonObject node = WithoutEvent(bindings);
        static void RemoveEmptyCalls(JsonObject o)
        {
            if (o["nextCalls"] is JsonArray { Count: 0 }) o.Remove("nextCalls");
        }
        foreach (JsonNode? group in node["resources"] as JsonArray ?? new JsonArray())
        {
            if (group?["resource"] is JsonObject resource)
            {
                foreach (string key in new[] { "barrierStateType", "clearValue", "initialState", "initialLayout", "castableFormats", "heap", "views", "nextCalls" })
                    resource.Remove(key);
                if (resource["desc"] is JsonObject desc)
                    foreach (string key in new[] { "alignment", "layout", "sampleQuality" }) desc.Remove(key);
            }
            foreach (JsonNode? view in group?["views"] as JsonArray ?? new JsonArray())
                if (view is JsonObject o)
                {
                    o.Remove("resource");
                    RemoveEmptyCalls(o);
                }
        }
        foreach (JsonNode? view in node["otherViews"] as JsonArray ?? new JsonArray())
            if (view is JsonObject o) RemoveEmptyCalls(o);
        RemoveEmptyCalls(node);
        return node;
    }

    internal static void PrepareOptionalBindings(Action prepare)
    {
        try { prepare(); }
        catch (Exception ex) when (PixErrors.ToDto(ex).Code == "unsupported_feature") { }
    }
}
