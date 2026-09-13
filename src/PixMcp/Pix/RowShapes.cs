using System.Text.Json;
using System.Text.Json.Nodes;
using PixMcp.Pix.Handles;

namespace PixMcp.Pix;

/// <summary>One pre-order row of a flattened timing tree.</summary>
public sealed record TimingTreeRow(int Depth, uint? ParentEventIndex, TimingBranchDto Node);

/// <summary>Column declarations for every tool that can return positional rows; declared once next to the DTOs they shape.</summary>
public static class RowShapes
{
    private static readonly Dictionary<string, RefRecipe> EventRefs = new() { ["eventRef"] = Shaping.EventRefRecipe };
    private static readonly Dictionary<string, string> MarkerPathNote = new() { ["markerPath"] = "ancestor marker names joined with '/'" };

    private static ColumnSpec<T> Col<T>(string name, string type, string description, bool brief, Func<T, object?> select, string? unit = null)
        => new(name, type, unit, description, brief, select);

    public static readonly RowShape<EventDto> Events = new(
    [
        Col<EventDto>("queueIndex", "integer", "Queue index", true, e => e.QueueIndex),
        Col<EventDto>("eventIndex", "integer", "Event index within the queue", true, e => e.Index),
        Col<EventDto>("name", "string", "Event name (API call or marker label)", true, e => e.Name),
        Col<EventDto>("parentIndex", "integer", "Parent event index; null at the top level", true, e => e.ParentIndex),
        Col<EventDto>("gpuId", "integer", "GPU id when the event carries GPU work", true, e => e.GpuId),
        Col<EventDto>("commandListId", "integer", "Command list id", false, e => e.CommandListId),
        Col<EventDto>("apiCallData", "string", "Captured API call text", false, e => e.ApiCallData),
        Col<EventDto>("color", "string", "Marker colour as 0xAARRGGBB", false, e => e.Color),
        Col<EventDto>("markerPath", "string", "Ancestor marker names joined with '/'", false, e => Shaping.JoinPath(e.MarkerPath)),
    ], EventRefs, MarkerPathNote, e => e with { ApiCallData = null, Color = null, MarkerPath = null });

    public static readonly RowShape<TimingEventDto> TimingEvents = new(
    [
        Col<TimingEventDto>("queueIndex", "integer", "Queue index", true, r => r.QueueIndex),
        Col<TimingEventDto>("eventIndex", "integer", "Event index within the queue", true, r => r.Index),
        Col<TimingEventDto>("name", "string", "Event name", true, r => r.Name),
        Col<TimingEventDto>("kind", "string", "Event kind", true, r => r.Kind),
        .. Shaping.Duration<TimingEventDto>("eop", "End-of-pipe duration (includes idle before the event)", true, r => r.Eop),
        .. Shaping.Duration<TimingEventDto>("exec", "TOP-to-EOP execution (overlaps neighbours)", false, r => r.Exec),
        Col<TimingEventDto>("markerPath", "string", "Ancestor marker names joined with '/'", false, r => Shaping.JoinPath(r.MarkerPath)),
        Col<TimingEventDto>("gpuId", "integer", "GPU id", false, r => r.GpuId),
        Col<TimingEventDto>("topStartNs", "integer", "Top-of-pipe start", false, r => r.TopStartNs, "ns"),
        Col<TimingEventDto>("topDurationNs", "integer", "Top-of-pipe duration", false, r => r.TopDurationNs, "ns"),
        Col<TimingEventDto>("eopStartNs", "integer", "End-of-pipe start", false, r => r.EopStartNs, "ns"),
        Col<TimingEventDto>("eopEndNs", "integer", "End-of-pipe end", false, r => r.EopEndNs, "ns"),
    ], EventRefs, Merge(Shaping.DurationNotes, MarkerPathNote),
    r => r with { MarkerPath = null, Exec = null, TopStartNs = null, TopDurationNs = null, EopStartNs = null, EopEndNs = null });

    public static RowShape<CounterValueRowDto> Counters(IReadOnlyList<CounterInfo> counters, IReadOnlyList<CounterRatioSpec>? derived = null, bool normalized = false, bool timing = false)
    {
        var columns = new List<ColumnSpec<CounterValueRowDto>>
        {
            Col<CounterValueRowDto>("queueIndex", "integer", "Queue index", true, r => r.EventRef.QueueIndex),
            Col<CounterValueRowDto>("eventIndex", "integer", "Event index within the queue", true, r => r.Index),
            Col<CounterValueRowDto>("name", "string", "Event name", true, r => r.Name),
            Col<CounterValueRowDto>("gpuId", "integer", "GPU id", false, r => r.GpuId),
            Col<CounterValueRowDto>("markerPath", "string", "Ancestor marker names joined with '/'", false, r => Shaping.JoinPath(r.MarkerPath)),
            Col<CounterValueRowDto>("rowKind", "string", "marker (PIX's own measurement over the marker span, a separate playback round) or event (leaf)", true, r => r.RowKind),
            Col<CounterValueRowDto>("descendantDataEvents", "integer", "Leaf rows with data under this marker; null for leaves", false, r => r.DescendantDataEvents),
        };
        if (timing) columns.AddRange(Shaping.Duration<CounterValueRowDto>("eop", "The event's inclusive replay EOP time", true, r => r.Eop));
        foreach (CounterInfo counter in counters)
        {
            string id = counter.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            columns.Add(Col<CounterValueRowDto>(id, "number", counter.Name, true, r => r.Values.GetValueOrDefault(id)));
            if (normalized) columns.Add(Col<CounterValueRowDto>(id + ".normalized", "number", counter.Name + " divided by the normalization divisor", true, r => r.Normalized?.GetValueOrDefault(id)));
        }
        foreach (CounterRatioSpec spec in derived ?? [])
            columns.Add(Col<CounterValueRowDto>("derived." + spec.Name, "number", $"counter {spec.NumeratorId} / counter {spec.DenominatorId}", true, r => r.Derived?.GetValueOrDefault(spec.Name)));
        if (normalized) columns.Add(Col<CounterValueRowDto>("normalizeReason", "string", "Why the row has no normalized values", false, r => r.NormalizeReason));
        var notes = new Dictionary<string, string>(MarkerPathNote)
        {
            ["counters"] = "columns named by counter id; extra.counters lists names, data types and units",
            ["rowKind"] = "marker rows are PIX's own measurement over the marker span (separate playback rounds), never the sum of the event rows below them",
        };
        if (timing) foreach (var note in Shaping.DurationNotes) notes[note.Key] = note.Value;
        return new(columns, EventRefs, notes, r => r with { MarkerPath = null, Exec = null });
    }

    public static readonly RowShape<RollupRowDto> Rollup = new(
    [
        Col<RollupRowDto>("key", "string", "Group key", true, r => r.Key),
        Col<RollupRowDto>("count", "integer", "Rows in the group", true, r => r.Count),
        Col<RollupRowDto>("measured", "integer", "Rows with a value for the metric", false, r => r.Measured),
        Col<RollupRowDto>("untimed", "integer", "Rows without a value for the metric", false, r => r.Untimed),
        .. Shaping.Duration<RollupRowDto>("sum", "Summed metric", true, r => r.Sum),
        Col<RollupRowDto>("avgNs", "integer", "Average per measured row", false, r => r.AvgNs, "ns"),
        Col<RollupRowDto>("minNs", "integer", "Smallest row", false, r => r.MinNs, "ns"),
        Col<RollupRowDto>("maxNs", "integer", "Largest row", true, r => r.MaxNs, "ns"),
        Col<RollupRowDto>("p50Ns", "integer", "Nearest-rank median", false, r => r.P50Ns, "ns"),
        Col<RollupRowDto>("p95Ns", "integer", "Nearest-rank 95th percentile", true, r => r.P95Ns, "ns"),
        Col<RollupRowDto>("semantics", "string", "measured, derivedSum, mixed, remainder or untimed", true, r => r.Semantics),
        Col<RollupRowDto>("queueIndex", "integer", "Queue of the group's largest row", false, r => r.Representative?.QueueIndex),
        Col<RollupRowDto>("eventIndex", "integer", "Event index of the group's largest row", false, r => r.Representative?.EventIndex),
    ], EventRefs, Merge(Shaping.DurationNotes, new Dictionary<string, string> { ["representative"] = "queueIndex and eventIndex name the group's largest row; counters need format=objects" }),
    r => r with { KeyDetail = null, NextCalls = [], Counters = null });

    public static readonly RowShape<BubbleDto> Bubbles = new(
    [
        Col<BubbleDto>("queueIndex", "integer", "Queue of the gap", true, b => b.QueueIndex),
        Col<BubbleDto>("startNs", "integer", "Gap start on the replay clock", true, b => b.StartNs, "ns"),
        .. Shaping.Duration<BubbleDto>("duration", "Gap length", true, b => b.Duration),
        Col<BubbleDto>("beforeEventIndex", "integer", "Leaf event whose window ends the busy stretch before the gap", false, b => b.Before.EventIndex),
        Col<BubbleDto>("beforeName", "string", "Name of that event", false, b => b.BeforeName),
        Col<BubbleDto>("eventIndex", "integer", "Leaf event that starts after the gap", true, b => b.After.EventIndex),
        Col<BubbleDto>("afterName", "string", "Name of that event", true, b => b.AfterName),
        Col<BubbleDto>("afterMarkerPath", "string", "Ancestor markers of the event after the gap, joined with '/'", false, b => b.AfterMarkerPath),
        Col<BubbleDto>("primaryCause", "string", "present, queueWait, queueSignal, barrier, commandListBoundary or unknown", true, b => b.PrimaryCause),
        Col<BubbleDto>("causes", "string", "Every cause found, comma separated in precedence order", false, b => string.Join(",", b.Causes)),
        Col<BubbleDto>("evidenceEventIndex", "integer", "Event that shows the primary cause", false, b => b.Evidence?.EventIndex),
        Col<BubbleDto>("commandListChanged", "boolean", "A different command list appears across the gap", false, b => b.CommandListChanged),
        Col<BubbleDto>("otherQueueBusyPercent", "number", "Percent of the gap another timed queue was busy", true, b => b.OtherQueueBusyPercent),
    ], EventRefs, Merge(Shaping.DurationNotes, new Dictionary<string, string> { ["eventRef"] = "queueIndex and eventIndex name the event after the gap" }));

    private static readonly Dictionary<string, RefRecipe> ShaderRefs = new() { ["shaderRef"] = Shaping.ShaderRefRecipe, ["eventRef"] = Shaping.EventRefRecipe };

    public static readonly RowShape<ShaderInventoryItemDto> Shaders = new(
    [
        Col<ShaderInventoryItemDto>("stage", "string", "Shader stage", true, i => i.Shader.Stage),
        Col<ShaderInventoryItemDto>("hash", "string", "Shader hash when PIX exposes one", true, i => i.Shader.Hash),
        Col<ShaderInventoryItemDto>("useCount", "integer", "Events using this shader", true, i => i.UseCount),
        Col<ShaderInventoryItemDto>("gpuTimeMs", "number", "Summed replay EOP of the events using the shader; null until timing is collected", true, i => i.GpuTimeMs, "ms"),
        Col<ShaderInventoryItemDto>("shaderKey", "string", "Stable identity hash:STAGE:HASH; null when PIX exposes no hash", false, i => i.ShaderKey),
        Col<ShaderInventoryItemDto>("queueIndex", "integer", "Representative event queue", true, i => i.Shader.ShaderRef?.EventRef.QueueIndex),
        Col<ShaderInventoryItemDto>("eventIndex", "integer", "Representative event index", true, i => i.Shader.ShaderRef?.EventRef.EventIndex),
        Col<ShaderInventoryItemDto>("shaderIndex", "integer", "Shader slot at the representative event", true, i => i.Shader.ShaderRef?.ShaderIndex),
        Col<ShaderInventoryItemDto>("entry", "string", "Entry point", false, i => i.Shader.Entry),
        Col<ShaderInventoryItemDto>("target", "string", "Compile target", false, i => i.Shader.Target),
        Col<ShaderInventoryItemDto>("sizeBytes", "integer", "Bytecode size", false, i => i.Shader.SizeBytes, "bytes"),
        Col<ShaderInventoryItemDto>("availableCode", "string", "Code kinds PIX can show, comma-separated", false, i => string.Join(',', i.Shader.AvailableCode)),
    ], ShaderRefs, new Dictionary<string, string> { ["identity"] = "a missing hash means the row is one occurrence, not an identity" },
    i => i with { NextCalls = [], Shader = i.Shader with { Defines = null, Flags = null } });

    public static readonly RowShape<ResourceSummaryDto> Resources = new(
    [
        Col<ResourceSummaryDto>("index", "integer", "Resource index in the capture", true, r => r.Index),
        Col<ResourceSummaryDto>("apiObjectId", "string", "API object id (hex)", true, r => r.ApiObjectId),
        Col<ResourceSummaryDto>("name", "string", "Debug name", true, r => r.Name),
        Col<ResourceSummaryDto>("type", "string", "COMMITTED, PLACED or RESERVED", false, r => r.Type),
        Col<ResourceSummaryDto>("dimension", "string", "BUFFER or texture dimension", true, r => r.Dimension),
        Col<ResourceSummaryDto>("width", "integer", "Width in texels (bytes for buffers)", true, r => r.Width),
        Col<ResourceSummaryDto>("height", "integer", "Height in texels", true, r => r.Height),
        Col<ResourceSummaryDto>("depthOrArraySize", "integer", "Depth or array size", false, r => r.DepthOrArraySize),
        Col<ResourceSummaryDto>("mipLevels", "integer", "Mip levels", false, r => r.MipLevels),
        Col<ResourceSummaryDto>("format", "string", "DXGI format", true, r => r.Format),
        Col<ResourceSummaryDto>("sampleCount", "integer", "MSAA sample count", false, r => r.SampleCount),
        Col<ResourceSummaryDto>("flags", "string", "D3D12 resource flags", false, r => r.Flags),
        Col<ResourceSummaryDto>("estimatedBytes", "integer", "Estimated size from the description", true, r => r.EstimatedBytes, "bytes"),
        Col<ResourceSummaryDto>("heapKind", "string", "committed, placed, reserved or unknown", false, r => r.HeapKind),
        Col<ResourceSummaryDto>("trafficBytes", "integer", "Estimated bytes times the events that read or write it (resource-use index only)", false, r => r.TrafficBytes, "bytes"),
    ], new Dictionary<string, RefRecipe> { ["resourceRef"] = Shaping.ResourceRefRecipe }, new Dictionary<string, string>(), null);

    public static readonly RowShape<RecordedTimingEventDto> RecordedTimingEvents = new(
    [
        Col<RecordedTimingEventDto>("eventId", "string", "Marker definition id within the timing capture", true, r => r.EventId),
        Col<RecordedTimingEventDto>("domain", "string", "cpu, cpuMarkers, gpuMarkers, gpuSubmissions or gpuHardware", true, r => r.Domain),
        Col<RecordedTimingEventDto>("source", "string", "pixCpuEvent, pixCpuMarker, pixGpuMarker, apiSubmission or hardwareQueue", false, r => r.Source),
        Col<RecordedTimingEventDto>("name", "string", "Event name", true, r => r.Name),
        Col<RecordedTimingEventDto>("beginNs", "string", "Begin timestamp (decimal string)", true, r => r.BeginNs, "ns"),
        Col<RecordedTimingEventDto>("endNs", "string", "Exclusive end timestamp", false, r => r.EndNs, "ns"),
        Col<RecordedTimingEventDto>("durationNs", "string", "Original duration", true, r => r.DurationNs, "ns"),
        Col<RecordedTimingEventDto>("overlapDurationNs", "string", "Overlap with the selected interval", false, r => r.OverlapDurationNs, "ns"),
        Col<RecordedTimingEventDto>("level", "integer", "Nesting level", false, r => r.Level),
        Col<RecordedTimingEventDto>("processId", "integer", "OS process id", false, r => r.ProcessId),
        Col<RecordedTimingEventDto>("threadId", "integer", "OS thread id", true, r => r.ThreadId),
        Col<RecordedTimingEventDto>("threadName", "string", "Thread name", false, r => r.ThreadName),
        Col<RecordedTimingEventDto>("queueId", "string", "Recorded queue id", true, r => r.QueueId),
        Col<RecordedTimingEventDto>("queueName", "string", "Recorded queue name", false, r => r.QueueName),
        Col<RecordedTimingEventDto>("submitNs", "string", "CPU submit timestamp of a gpuSubmissions row", false, r => r.SubmitNs, "ns"),
        Col<RecordedTimingEventDto>("submitLatencyNs", "string", "GPU begin minus CPU submit (null when begin precedes submit)", true, r => r.SubmitLatencyNs, "ns"),
        Col<RecordedTimingEventDto>("commandListCount", "integer", "Command lists in the submission", false, r => r.CommandListCount),
        Col<RecordedTimingEventDto>("submissionRef", "string", "Reference for pix_timing_submissions", false, r => r.SubmissionRef),
        Col<RecordedTimingEventDto>("hardwareQueueName", "string", "Hardware queue of a gpuHardware range", false, r => r.HardwareQueueName),
        Col<RecordedTimingEventDto>("executionNs", "string", "Recorded CPU execution time", false, r => r.ExecutionNs, "ns"),
        Col<RecordedTimingEventDto>("stallNs", "string", "Recorded CPU stall time", false, r => r.StallNs, "ns"),
        Col<RecordedTimingEventDto>("executionTimingState", "string", "available, ambiguous, inconsistent, unavailable or unsupported", false, r => r.ExecutionTimingState),
        Col<RecordedTimingEventDto>("executionTimingMethod", "string", "rowId (CpuExecutionRowId lookup) or tupleMatch", false, r => r.ExecutionTimingMethod),
    ], new Dictionary<string, RefRecipe>(), new Dictionary<string, string> { ["timestamps"] = "decimal nanosecond strings on the capture clock; endNs is exclusive; nested events overlap" },
    r => r with { ThreadName = null, QueueName = null, ExecutionNs = null, StallNs = null, ExecutionTimingState = null });

    public static readonly RowShape<TimingHotspotDto> Hotspots = new(
    [
        Col<TimingHotspotDto>("function", "string", "Function name, or the address when unresolved", true, h => h.Function.Function ?? h.Function.Address),
        Col<TimingHotspotDto>("module", "string", "Module name", true, h => h.Function.Module),
        Col<TimingHotspotDto>("inclusiveSamples", "integer", "Samples with the function anywhere on the stack", true, h => h.InclusiveSamples),
        Col<TimingHotspotDto>("exclusiveSamples", "integer", "Samples with the function as the leaf", true, h => h.ExclusiveSamples),
        Col<TimingHotspotDto>("inclusivePercent", "number", "Inclusive share of the selected samples", true, h => h.InclusivePercent, "percent"),
        Col<TimingHotspotDto>("exclusivePercent", "number", "Exclusive share of the selected samples", true, h => h.ExclusivePercent, "percent"),
        Col<TimingHotspotDto>("address", "string", "Sampled address", false, h => h.Function.Address),
        Col<TimingHotspotDto>("key", "string", "Stable function key for pix_timing_calltree", false, h => h.Function.Key),
        Col<TimingHotspotDto>("symbolState", "string", "resolved or unresolved", false, h => h.Function.SymbolState),
        Col<TimingHotspotDto>("sourceFile", "string", "Source file when symbols resolve", false, h => h.Function.SourceFile),
        Col<TimingHotspotDto>("sourceLine", "integer", "Source line when symbols resolve", false, h => h.Function.SourceLine),
    ], new Dictionary<string, RefRecipe>(), new Dictionary<string, string> { ["samples"] = "statistical sample counts, not exact CPU time" }, null);

    public static readonly RowShape<TimingTreeRow> TimingTree = new(
    [
        Col<TimingTreeRow>("eventIndex", "integer", "Event index within the queue", true, r => r.Node.Index),
        Col<TimingTreeRow>("parentEventIndex", "integer", "Parent event index; null for first-level rows", true, r => r.ParentEventIndex),
        Col<TimingTreeRow>("depth", "integer", "Depth below the first level (0 = first level)", true, r => r.Depth),
        Col<TimingTreeRow>("name", "string", "Event name", true, r => r.Node.Name),
        Col<TimingTreeRow>("semantics", "string", "measured, derivedSum, mixed or untimed", true, r => r.Node.Semantics),
        .. Shaping.Duration<TimingTreeRow>("inclusive", "Inclusive EOP time", true, r => r.Node.Inclusive),
        Col<TimingTreeRow>("self.ns", "integer", "Self time (inclusive minus children, clamped at 0)", true, r => r.Node.Self.Ns, "ns"),
        Col<TimingTreeRow>("self.ms", "number", "Self time in milliseconds", true, r => r.Node.Self.Ms, "ms"),
        Col<TimingTreeRow>("hasOwnTiming", "boolean", "PIX measured this event itself", false, r => r.Node.HasOwnTiming),
        Col<TimingTreeRow>("childCount", "integer", "Direct children", true, r => r.Node.ChildCount),
        Col<TimingTreeRow>("childrenTruncated", "boolean", "More children exist than were expanded", false, r => r.Node.ChildrenTruncated),
        Col<TimingTreeRow>("childrenExceedMeasured", "boolean", "Children sum to more than the measured span", false, r => r.Node.ChildrenExceedMeasured),
        Col<TimingTreeRow>("untimedChildren", "integer", "Direct children with no timing", false, r => r.Node.UntimedChildren),
    ], EventRefs, Shaping.DurationNotes, null);

    public static readonly RowShape<OverviewPassDto> OverviewPasses = new(
    [
        Col<OverviewPassDto>("queueIndex", "integer", "Queue index", true, p => p.EventRef.QueueIndex),
        Col<OverviewPassDto>("eventIndex", "integer", "Event index within the queue", true, p => p.EventRef.EventIndex),
        Col<OverviewPassDto>("name", "string", "Marker name", true, p => p.Name),
        Col<OverviewPassDto>("semantics", "string", "measured, derivedSum or mixed", true, p => p.Semantics),
        .. Shaping.Duration<OverviewPassDto>("inclusive", "Inclusive EOP time", true, p => p.Inclusive),
        Col<OverviewPassDto>("self.ns", "integer", "Self time (inclusive minus timed children)", true, p => p.Self.Ns, "ns"),
        Col<OverviewPassDto>("self.ms", "number", "Self time in milliseconds", true, p => p.Self.Ms, "ms"),
        Col<OverviewPassDto>("childCount", "integer", "Direct children", false, p => p.ChildCount),
        Col<OverviewPassDto>("workCount", "integer", "Draw, dispatch and executeIndirect events in the subtree", true, p => p.WorkCount),
        Col<OverviewPassDto>("childrenExceedMeasured", "boolean", "Children sum to more than the measured span", false, p => p.ChildrenExceedMeasured),
        Col<OverviewPassDto>("childOverflowNs", "integer", "Child time beyond the measured span", false, p => p.ChildOverflowNs, "ns"),
        Col<OverviewPassDto>("markerPath", "string", "Ancestor marker names joined with '/'", false, p => Shaping.JoinPath(p.MarkerPath)),
    ], EventRefs, Merge(Shaping.DurationNotes, MarkerPathNote), null);

    public static readonly RowShape<OverviewWorkDto> OverviewWork = new(
    [
        Col<OverviewWorkDto>("queueIndex", "integer", "Queue index", true, w => w.EventRef.QueueIndex),
        Col<OverviewWorkDto>("eventIndex", "integer", "Event index within the queue", true, w => w.EventRef.EventIndex),
        Col<OverviewWorkDto>("name", "string", "Event name", true, w => w.Name),
        Col<OverviewWorkDto>("kind", "string", "draw, dispatch or executeIndirect", true, w => w.Kind),
        .. Shaping.Duration<OverviewWorkDto>("eop", "End-of-pipe duration (includes idle before the event)", true, w => w.Eop),
        .. Shaping.Duration<OverviewWorkDto>("exec", "TOP-to-EOP execution (overlaps neighbours)", false, w => w.Exec),
        Col<OverviewWorkDto>("markerPath", "string", "Ancestor marker names joined with '/'", false, w => Shaping.JoinPath(w.MarkerPath)),
        Col<OverviewWorkDto>("parameters.raw", "string", "Captured API call text", false, w => w.Parameters?.Raw),
    ], EventRefs, Merge(Shaping.DurationNotes, MarkerPathNote), null);

    /// <summary>Flattens an expanded timing tree into pre-order rows.</summary>
    public static List<TimingTreeRow> Flatten(IReadOnlyList<TimingBranchDto> branches, uint? parent = null, int depth = 0, List<TimingTreeRow>? into = null)
    {
        into ??= new List<TimingTreeRow>();
        foreach (TimingBranchDto branch in branches)
        {
            into.Add(new(depth, parent, branch));
            Flatten(branch.Children, branch.Index, depth + 1, into);
        }
        return into;
    }

    /// <summary>Applies string truncation to any DTO after it is built; returns the DTO itself when nothing was cut.</summary>
    public static object Finish(object dto, ShapingOptions options)
    {
        if (options.MaxStringLength >= Shaping.FullStringLength) return dto;
        JsonNode? node = JsonSerializer.SerializeToNode(dto, Json.Options);
        int cut = 0;
        Shaping.TruncateStrings(node, options.MaxStringLength, ref cut);
        if (cut == 0 || node is not JsonObject obj) return dto;
        obj["truncatedStrings"] = cut;
        return obj;
    }

    private static Dictionary<string, string> Merge(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
    {
        var merged = new Dictionary<string, string>(a);
        foreach (var pair in b) merged[pair.Key] = pair.Value;
        return merged;
    }
}
