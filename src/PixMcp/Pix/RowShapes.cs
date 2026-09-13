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

    public static RowShape<CounterValueRowDto> Counters(IReadOnlyList<CounterInfo> counters)
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
        foreach (CounterInfo counter in counters)
        {
            string id = counter.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            columns.Add(Col<CounterValueRowDto>(id, "number", counter.Name, true, r => r.Values.GetValueOrDefault(id)));
        }
        var notes = new Dictionary<string, string>(MarkerPathNote)
        {
            ["counters"] = "columns named by counter id; extra.counters lists names and data types",
            ["rowKind"] = "marker rows are PIX's own measurement over the marker span (separate playback rounds), never the sum of the event rows below them",
        };
        return new(columns, EventRefs, notes, r => r with { MarkerPath = null });
    }

    private static readonly Dictionary<string, RefRecipe> ShaderRefs = new() { ["shaderRef"] = Shaping.ShaderRefRecipe, ["eventRef"] = Shaping.EventRefRecipe };

    public static readonly RowShape<ShaderInventoryItemDto> Shaders = new(
    [
        Col<ShaderInventoryItemDto>("stage", "string", "Shader stage", true, i => i.Shader.Stage),
        Col<ShaderInventoryItemDto>("hash", "string", "Shader hash when PIX exposes one", true, i => i.Shader.Hash),
        Col<ShaderInventoryItemDto>("useCount", "integer", "Events using this shader", true, i => i.UseCount),
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
    ], new Dictionary<string, RefRecipe> { ["resourceRef"] = Shaping.ResourceRefRecipe }, new Dictionary<string, string>(), null);

    public static readonly RowShape<RecordedTimingEventDto> RecordedTimingEvents = new(
    [
        Col<RecordedTimingEventDto>("eventId", "string", "Marker definition id within the timing capture", true, r => r.EventId),
        Col<RecordedTimingEventDto>("domain", "string", "cpu or gpu lane family", true, r => r.Domain),
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
        Col<RecordedTimingEventDto>("executionNs", "string", "Recorded CPU execution time", false, r => r.ExecutionNs, "ns"),
        Col<RecordedTimingEventDto>("stallNs", "string", "Recorded CPU stall time", false, r => r.StallNs, "ns"),
        Col<RecordedTimingEventDto>("executionTimingState", "string", "available, ambiguous or unavailable", false, r => r.ExecutionTimingState),
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
