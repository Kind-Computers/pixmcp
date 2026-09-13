namespace PixMcp.Pix.Sql;

internal sealed record GpuSqlColumn(string Name, string Type, string? Unit, string Description);

internal sealed record GpuSqlTable(string Name, string Family, string Description, IReadOnlyList<GpuSqlColumn> Columns, IReadOnlyList<string> Key)
{
    public string Ddl => $"CREATE TABLE IF NOT EXISTS {Name} ({string.Join(", ", Columns.Select(c => $"{c.Name} {c.Type}"))}, PRIMARY KEY ({string.Join(", ", Key)}))";
    public string Insert => $"INSERT INTO {Name} ({string.Join(", ", Columns.Select(c => c.Name))}) VALUES ({string.Join(", ", Columns.Select((_, i) => "$p" + i))})";
}

internal sealed record GpuSqlView(string Name, string Description, IReadOnlyList<string> Families, string Sql)
{
    public string Ddl => $"CREATE VIEW IF NOT EXISTS {Name} AS {Sql}";
}

/// <summary>Tables populated together, what populating them costs and what it needs.</summary>
internal sealed record GpuSqlFamily(string Name, string Description, string Cost, string Requires);

/// <summary>The GPU SQL store's schema: families, documented tables, views and the status table. Single source for DDL and docs.</summary>
internal static class GpuSqlSchema
{
    public const int Version = 1;
    public const string StatusTable = "table_status";

    public static readonly IReadOnlyList<GpuSqlFamily> Families =
    [
        new("core", "Capture, queues, events with kinds, marker paths and frames, parsed call arguments and work items.", CostHints.Query, "Capture metadata only; no replay."),
        new("timing", "Per-event replay timing, the rolled-up timing tree and per-queue totals.", CostHints.Replay, "GPU analysis and timing collection."),
        new("shaders", "Bound shader identities per work event and per-event pipeline keys.", CostHints.Replay, "GPU analysis and the shader index."),
        new("counters", "The counter catalog entries and per-event values of one counter set.", CostHints.Replay, "GPU analysis and a counter collection (counterIds or preset)."),
        new("resources", "Every D3D12 resource with its description.", CostHints.Query, "Capture metadata only; no replay."),
        new("resourceUses", "Resource views bound at each work event with a coarse access class.", CostHints.Replay, "GPU analysis and accessed resources; one views read per work event."),
        new("psos", "Pipeline state per work event as JSON.", CostHints.Replay, "GPU analysis; one pipeline read per work event."),
    ];

    public static readonly string[] FamilyOrder = Families.Select(f => f.Name).ToArray();

    /// <summary>What tables=all populates: the families that are cheap or needed by most questions.</summary>
    public static readonly string[] AllFamilies = ["core", "timing", "shaders", "resources"];

    /// <summary>Families this build does not materialise, with the tool that answers instead.</summary>
    public static string? UnavailableReason(string family) => family.Trim().ToLowerInvariant() switch
    {
        "occupancy" => "Occupancy series are not materialised in the GPU SQL store yet; use pix_gpu_occupancy.",
        "hf" => "High-frequency counter samples are not materialised in the GPU SQL store yet; use pix_gpu_hf_counters.",
        "drpix" => "Dr. PIX runs are not retained for the GPU SQL store yet; use pix_gpu_drpix_run.",
        _ => null,
    };

    private static GpuSqlColumn C(string name, string type, string description, string? unit = null) => new(name, type, unit, description);
    private static readonly GpuSqlColumn Queue = C("queue_index", "INTEGER", "Queue index; with event_index it builds an EventRef { handle, queueIndex, eventIndex }.");
    private static readonly GpuSqlColumn Event = C("event_index", "INTEGER", "Event index within the queue.");

    public static readonly IReadOnlyList<GpuSqlTable> Tables =
    [
        new("capture", "core", "One row: the capture and how it was materialised.",
        [
            C("handle", "TEXT", "GPU capture handle."), C("path", "TEXT", "Capture file path."), C("schema_version", "INTEGER", "GPU SQL schema version."),
            C("event_total", "INTEGER", "Events over every queue."), C("queue_count", "INTEGER", "Queues."), C("vendor", "TEXT", "Capture vendor."),
            C("adapter_name", "TEXT", "Capture adapter name."), C("frame_count", "INTEGER", "Frames delimited by Present calls."),
            C("present_queue_index", "INTEGER", "Queue with the most Present calls; null without Present."), C("frame_assignment", "TEXT", "present, indexOnly or none."),
        ], ["handle"]),
        new("queues", "core", "Command queues.", [Queue, C("name", "TEXT", "Queue name."), C("type", "TEXT", "Queue type."), C("event_count", "INTEGER", "Events on the queue.")], ["queue_index"]),
        new("events", "core", "Every event of every queue.",
        [
            Queue, Event, C("gpu_id", "INTEGER", "GPU id; null for events without GPU work."), C("parent_index", "INTEGER", "Parent event index; null at the top level."),
            C("name", "TEXT", "Event name."), C("api_call_data", "TEXT", "Captured call text."), C("command_list_id", "INTEGER", "Command list id."), C("color", "INTEGER", "Marker colour."),
            C("kind", "TEXT", "draw, dispatch, executeIndirect, copy, clear, resolve, barrier, present, marker, label or other."),
            C("is_work", "INTEGER", "1 for draw, dispatch and executeIndirect."), C("is_marker", "INTEGER", "1 for markers."),
            C("api", "TEXT", "Parsed API name; null when the call text does not parse."), C("marker_path", "TEXT", "Ancestor names joined with '/'; empty at the top level."),
            C("path", "TEXT", "marker_path plus the event's own name; $markerPathPrefix matches its leading segments."), C("depth", "INTEGER", "Ancestors above the event."),
            C("child_count", "INTEGER", "Direct children."), C("subtree_last", "INTEGER", "Largest event index in the event's subtree (the event itself when it has no children)."),
            C("frame_index", "INTEGER", "Frame the event belongs to; null when its queue cannot be split."),
        ], ["queue_index", "event_index"]),
        new("call_args", "core", "Arguments of each parsed API call.",
        [
            Queue, Event, C("position", "INTEGER", "0-based position; -1 for the element form's this object."), C("name", "TEXT", "Argument name."), C("text", "TEXT", "Argument text."),
            C("int64", "INTEGER", "Integer value when the text is one."), C("real", "REAL", "Floating value when the text is one."),
            C("api_object_id", "TEXT", "API object id (hex) of an obj#N argument; builds a ResourceRef when it names a resource."), C("object_kind", "TEXT", "Interface named before obj#N."),
        ], ["queue_index", "event_index", "position"]),
        new("work_items", "core", "Work launched by each parsed draw, dispatch or ExecuteIndirect call.",
        [
            Queue, Event, C("api", "TEXT", "API name."), C("form", "TEXT", "element or positional."),
            C("work_item_kind", "TEXT", "vertices, indices, threadGroups or maxCommands."), C("work_items", "INTEGER", "Vertices or indices times instances, thread groups X*Y*Z, or MaxCommandCount."),
            C("instance_count", "INTEGER", "Instances of a draw."),
        ], ["queue_index", "event_index"]),
        new("frames", "core", "Frames delimited by Present calls on the presenting queue.",
        [
            C("frame_index", "INTEGER", "0-based frame."), Queue, C("first_event_index", "INTEGER", "First event of the frame on the presenting queue."),
            C("last_event_index", "INTEGER", "Last event of the frame on the presenting queue."), C("present_event_index", "INTEGER", "The Present that ends the frame; null for a trailing partial frame."),
            C("partial", "INTEGER", "1 when no Present ends the frame."), C("window_start_ns", "INTEGER", "Replay-clock window start.", "ns"), C("window_end_ns", "INTEGER", "Replay-clock window end.", "ns"),
        ], ["frame_index"]),
        new("timing", "timing", "Per-event replay timing rows.",
        [
            Queue, Event, C("top_start_ns", "INTEGER", "Top-of-pipe start.", "ns"), C("top_duration_ns", "INTEGER", "Top-of-pipe duration.", "ns"),
            C("eop_start_ns", "INTEGER", "End-of-pipe start.", "ns"), C("eop_ns", "INTEGER", "End-of-pipe duration; includes idle before the event.", "ns"),
            C("eop_ms", "REAL", "eop_ns in milliseconds.", "ms"), C("eop_end_ns", "INTEGER", "End-of-pipe end.", "ns"),
            C("exec_ns", "INTEGER", "TOP start to EOP end; overlaps neighbouring events.", "ns"), C("exec_ms", "REAL", "exec_ns in milliseconds.", "ms"),
        ], ["queue_index", "event_index"]),
        new("timing_tree", "timing", "The replay timing tree: measured events taken as they are, unmeasured markers summed from their children.",
        [
            Queue, Event, C("semantics", "TEXT", "measured, derivedSum, mixed or untimed."), C("measured_eop_ns", "INTEGER", "PIX's own measurement; null for derived markers.", "ns"),
            C("inclusive_ns", "INTEGER", "Inclusive EOP time; null when untimed.", "ns"), C("inclusive_ms", "REAL", "inclusive_ns in milliseconds.", "ms"),
            C("self_ns", "INTEGER", "Inclusive minus children.", "ns"), C("self_ms", "REAL", "self_ns in milliseconds.", "ms"), C("child_sum_ns", "INTEGER", "Sum of the children's inclusive time.", "ns"),
            C("children_exceed_measured", "INTEGER", "1 when the children add up to more than PIX measured."), C("child_overflow_ns", "INTEGER", "How far the children exceed the measurement.", "ns"),
            C("untimed_children", "INTEGER", "Children without timing."), C("repaired", "INTEGER", "1 when a cyclic parent link was cut."), C("timed_descendants", "INTEGER", "Timed events below."),
            C("child_count", "INTEGER", "Direct children."), C("execution_ns", "INTEGER", "TOP start to EOP end of a measured event.", "ns"),
            C("percent_of_queue_span", "REAL", "inclusive_ns as a percent of the queue span.", "percent"), C("percent_of_parent", "REAL", "inclusive_ns as a percent of the parent's inclusive time.", "percent"),
        ], ["queue_index", "event_index"]),
        new("queue_totals", "timing", "Per-queue replay totals that percentages divide by.",
        [
            Queue, C("busy_ns", "INTEGER", "Union of TOP-to-EOP windows of timed leaf events.", "ns"), C("busy_ms", "REAL", "busy_ns in milliseconds.", "ms"),
            C("span_ns", "INTEGER", "First EOP start to last EOP end.", "ns"), C("span_ms", "REAL", "span_ns in milliseconds.", "ms"), C("idle_ns", "INTEGER", "span_ns minus busy_ns.", "ns"),
            C("idle_ms", "REAL", "idle_ns in milliseconds.", "ms"), C("idle_percent", "REAL", "idle_ns as a percent of span_ns.", "percent"),
            C("sum_of_roots_ns", "INTEGER", "Sum of top-level inclusive values.", "ns"), C("roots_overlap", "INTEGER", "1 when sum_of_roots_ns exceeds span_ns."),
            C("timed_events", "INTEGER", "Events with timing."), C("untimed_events", "INTEGER", "Events without timing."),
            C("first_eop_start_ns", "INTEGER", "First EOP start.", "ns"), C("last_eop_end_ns", "INTEGER", "Last EOP end.", "ns"), C("busy_source", "TEXT", "topEop or eopOnly."),
        ], ["queue_index"]),
        new("shaders", "shaders", "Shaders bound at each work event, one row per slot.",
        [
            Queue, Event, C("shader_index", "INTEGER", "Slot; with queue_index and event_index it builds a ShaderRef { eventRef, shaderIndex }."),
            C("shader_key", "TEXT", "hash:STAGE:HASH; null when PIX exposes no hash (the row is then one occurrence)."), C("stage", "TEXT", "Shader stage."),
            C("hash", "TEXT", "Shader hash."), C("entry", "TEXT", "Entry point."), C("target", "TEXT", "Compile target."), C("size_bytes", "INTEGER", "Bytecode size.", "bytes"),
            C("available_code", "TEXT", "Code kinds PIX can show, comma-separated."),
        ], ["queue_index", "event_index", "shader_index"]),
        new("event_pipelines", "shaders", "The pipeline key of each indexed work event.",
        [Queue, Event, C("pso_key", "TEXT", "pso:STAGE:HASH;...; null when a hash is missing or a shader could not be read."), C("complete", "INTEGER", "1 when every bound shader was read.")],
        ["queue_index", "event_index"]),
        new("counter_info", "counters", "Counters of the populated set.",
        [
            C("counter_id", "INTEGER", "Counter id."), C("name", "TEXT", "Counter name."), C("description", "TEXT", "Counter description."), C("data_type", "TEXT", "PIX data type."),
            C("unit", "TEXT", "Inferred unit."), C("unit_source", "TEXT", "Where the unit came from."), C("unit_confidence", "TEXT", "high, medium, low or none."),
            C("aggregation_hint", "TEXT", "sum, avg or none."), C("groups", "TEXT", "Counter groups, comma-separated."),
        ], ["counter_id"]),
        new("counters", "counters", "Counter values per event, one row per counter.",
        [
            Queue, Event, C("counter_id", "INTEGER", "Counter id."), C("value", "REAL", "Numeric value; null when not numeric."), C("value_text", "TEXT", "Value as text (exact for large integers)."),
            C("row_kind", "TEXT", "marker (PIX's own measurement over the marker span in a separate playback round; never add event rows to it) or event."),
        ], ["queue_index", "event_index", "counter_id"]),
        new("resources", "resources", "D3D12 resources.",
        [
            C("api_object_id", "TEXT", "API object id (hex); builds a ResourceRef { handle, apiObjectId }."), C("resource_index", "INTEGER", "Index in the capture."), C("name", "TEXT", "Debug name."),
            C("type", "TEXT", "COMMITTED, PLACED or RESERVED."), C("dimension", "TEXT", "BUFFER or texture dimension."), C("width", "INTEGER", "Width in texels (bytes for buffers)."),
            C("height", "INTEGER", "Height in texels."), C("depth_or_array_size", "INTEGER", "Depth or array size."), C("mip_levels", "INTEGER", "Mip levels."),
            C("format", "TEXT", "DXGI format."), C("sample_count", "INTEGER", "MSAA samples."), C("flags", "TEXT", "Resource flags."),
            C("estimated_bytes", "INTEGER", "Estimated size from the description (buffer width, or texels over mips, array slices and samples); null for unsized formats.", "bytes"),
            C("estimate_method", "TEXT", "bufferWidth, dimsMipsArraySamples, blockCompressed or unknownFormat."), C("heap_kind", "TEXT", "committed, placed, reserved or unknown."),
        ], ["api_object_id"]),
        new("resource_uses", "resourceUses", "Resource views bound at each work event.",
        [
            Queue, Event, C("view_index", "INTEGER", "View index in the event's view collection."), C("api_object_id", "TEXT", "Resource of the view; null for views without a resource."),
            C("resource_name", "TEXT", "Resource debug name."), C("view_type", "TEXT", "View type such as RENDER_TARGET_VIEW or SHADER_RESOURCE_VIEW."),
            C("access", "TEXT", "Coarse access from the view type: write (render target, depth stencil), readWrite (unordered access), read (shader resource, constant, vertex and index buffers) or unknown."),
        ], ["queue_index", "event_index", "view_index"]),
        new("pipeline_states", "psos", "Pipeline state per work event.",
        [Queue, Event, C("program_type", "TEXT", "Program type such as GRAPHICS or COMPUTE."), C("pso_key", "TEXT", "Pipeline key of the bound shaders; null when incomplete."),
            C("state_json", "TEXT", "Pipeline state as JSON; read fields with json_extract.")],
        ["queue_index", "event_index"]),
        new(StatusTable, "meta", "Populate state per family.",
        [
            C("family", "TEXT", "Family name."), C("state", "TEXT", "ready."), C("rows", "INTEGER", "Rows written."), C("populated_at", "TEXT", "UTC time of the populate."),
            C("analysis_generation", "INTEGER", "GPU analysis generation the rows came from; rows of replay families are stale after the analysis restarts."),
            C("detail", "TEXT", "Family detail (the counter ids for counters)."),
        ], ["family"]),
    ];

    public static readonly IReadOnlyList<GpuSqlView> Views =
    [
        new("v_events_timed", "Timed events with EOP, execution and timing-tree semantics.", ["core", "timing"],
            "SELECT e.queue_index, e.event_index, e.name, e.kind, e.path, t.eop_ns, t.eop_ms, t.exec_ns, t.exec_ms, tt.semantics, tt.inclusive_ns, tt.inclusive_ms, tt.self_ns, tt.self_ms " +
            "FROM events e JOIN timing_tree tt ON tt.queue_index = e.queue_index AND tt.event_index = e.event_index " +
            "LEFT JOIN timing t ON t.queue_index = e.queue_index AND t.event_index = e.event_index WHERE tt.semantics <> 'untimed'"),
        new("v_work", "Draw, dispatch and ExecuteIndirect events with timing and work items.", ["core", "timing"],
            "SELECT e.queue_index, e.event_index, e.name, e.kind, e.marker_path, e.frame_index, t.eop_ns, t.eop_ms, t.exec_ns, t.exec_ms, w.work_item_kind, w.work_items, w.instance_count " +
            "FROM events e LEFT JOIN timing t ON t.queue_index = e.queue_index AND t.event_index = e.event_index " +
            "LEFT JOIN work_items w ON w.queue_index = e.queue_index AND w.event_index = e.event_index WHERE e.is_work = 1"),
        new("v_passes", "Markers with children and their inclusive and self time.", ["core", "timing"],
            "SELECT e.queue_index, e.event_index, e.name, e.path, e.depth, e.child_count, tt.semantics, tt.inclusive_ns, tt.inclusive_ms, tt.self_ns, tt.self_ms, " +
            "tt.percent_of_queue_span, tt.children_exceed_measured FROM events e JOIN timing_tree tt ON tt.queue_index = e.queue_index AND tt.event_index = e.event_index " +
            "WHERE e.is_marker = 1 AND e.child_count > 0"),
        new("v_shader_cost", "Summed EOP time of the work events using each shader key.", ["shaders", "timing"],
            "SELECT s.shader_key, s.stage, s.hash, MIN(s.entry) AS entry, COUNT(*) AS uses, SUM(t.eop_ns) AS eop_ns, SUM(t.eop_ns) / 1e6 AS eop_ms " +
            "FROM shaders s LEFT JOIN timing t ON t.queue_index = s.queue_index AND t.event_index = s.event_index WHERE s.shader_key IS NOT NULL GROUP BY s.shader_key"),
        new("v_counters_joined", "Event counter rows with names, units and the event's EOP time; marker rows are excluded.", ["counters", "core", "timing"],
            "SELECT c.queue_index, c.event_index, e.name, e.kind, c.counter_id, i.name AS counter_name, i.unit, c.value, t.eop_ns, " +
            "CASE WHEN t.eop_ns > 0 THEN c.value / t.eop_ns END AS value_per_ns FROM counters c JOIN counter_info i ON i.counter_id = c.counter_id " +
            "JOIN events e ON e.queue_index = c.queue_index AND e.event_index = c.event_index " +
            "LEFT JOIN timing t ON t.queue_index = c.queue_index AND t.event_index = c.event_index WHERE c.row_kind = 'event'"),
    ];

    public static GpuSqlTable? Table(string name) => Tables.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static GpuSqlFamily? Family(string name) => Families.FirstOrDefault(f => f.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The families a statement needs, from the tables SQLite reports it reads; the status table and unknown names need none.</summary>
    public static IReadOnlyList<string> FamiliesOf(IEnumerable<string> referencedTables)
        => referencedTables.Select(Table).OfType<GpuSqlTable>().Select(t => t.Family).Where(f => f != "meta").Distinct()
            .OrderBy(f => Array.IndexOf(FamilyOrder, f)).ToArray();

    public static IEnumerable<string> CreateStatements() => Tables.Select(t => t.Ddl).Concat(Views.Select(v => v.Ddl));
}
