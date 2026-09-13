namespace PixMcp.Pix.Sql;

/// <summary>Named read-only queries over the GPU SQL store. Requires lists the families each reads; scoped queries honour the pre-bound scope parameters.</summary>
internal static class GpuQueryLibrary
{
    /// <summary>Keeps events of $scopeQueue between $scopeFirst and $scopeLast and events whose path starts with the $markerPathPrefix segments; both are optional.</summary>
    internal const string Scope = "($scopeQueue IS NULL OR (e.queue_index = $scopeQueue AND e.event_index BETWEEN $scopeFirst AND $scopeLast)) " +
        "AND ($markerPathPrefix IS NULL OR (e.path || '/') LIKE ($markerPathPrefix || '/%'))";

    private static readonly TimingQueryParam Limit = new("limit", "integer", 25, "Rows to return.");
    private const string ScopeCaveat = "scope and markerPathPrefix restrict the events; the prefix matches whole path segments, case-insensitively for ASCII.";
    private const string DerivedSum = "Sums of measured work rows (derivedSum), not PIX marker spans; top_passes reports PIX's own marker measurements.";

    public static readonly IReadOnlyList<NamedTimingQuery> All =
    [
        new("top_passes", "Markers with children ranked by inclusive replay time, with self time and semantics.",
            "SELECT e.queue_index, e.event_index, e.path, tt.semantics, tt.inclusive_ns, tt.inclusive_ms, tt.self_ns, tt.self_ms, tt.percent_of_queue_span, e.child_count " +
            "FROM events e JOIN timing_tree tt ON tt.queue_index = e.queue_index AND tt.event_index = e.event_index " +
            $"WHERE e.is_marker = 1 AND e.child_count > 0 AND tt.semantics <> 'untimed' AND {Scope} ORDER BY tt.inclusive_ns DESC LIMIT $limit",
            [Limit], ["core", "timing"], ["Nested passes overlap their ancestors: compare siblings, never an ancestor with its descendants.", ScopeCaveat],
            "Which passes take the most GPU time?"),
        new("cost_by_marker", "Work events grouped by their marker path with summed EOP time.",
            "SELECT e.marker_path, COUNT(*) AS work_events, COUNT(t.eop_ns) AS timed, SUM(t.eop_ns) AS eop_ns, SUM(t.eop_ns) / 1e6 AS eop_ms, MAX(t.eop_ns) AS max_eop_ns " +
            "FROM events e LEFT JOIN timing t ON t.queue_index = e.queue_index AND t.event_index = e.event_index " +
            $"WHERE e.is_work = 1 AND {Scope} GROUP BY e.marker_path ORDER BY eop_ns DESC LIMIT $limit",
            [Limit], ["core", "timing"], [DerivedSum, ScopeCaveat], "Where do draws and dispatches spend their time?"),
        new("cost_by_kind_per_queue", "Non-marker events per queue and kind with summed EOP time and its share of the queue span.",
            "SELECT e.queue_index, e.kind, COUNT(*) AS events, COUNT(t.eop_ns) AS timed, SUM(t.eop_ns) AS eop_ns, SUM(t.eop_ns) / 1e6 AS eop_ms, " +
            "ROUND(100.0 * SUM(t.eop_ns) / NULLIF(q.span_ns, 0), 2) AS percent_of_queue_span " +
            "FROM events e LEFT JOIN timing t ON t.queue_index = e.queue_index AND t.event_index = e.event_index LEFT JOIN queue_totals q ON q.queue_index = e.queue_index " +
            $"WHERE e.is_marker = 0 AND {Scope} GROUP BY e.queue_index, e.kind ORDER BY e.queue_index, eop_ns DESC LIMIT $limit",
            [Limit], ["core", "timing"], [DerivedSum, ScopeCaveat], "How is GPU time split between draws, dispatches, copies and clears?"),
        new("cost_by_shader", "Shaders by the summed EOP time of the work events that bind them.",
            "SELECT COALESCE(s.shader_key, 'occurrence:' || s.queue_index || ':' || s.event_index || ':' || s.shader_index) AS shader, s.stage, MIN(s.entry) AS entry, " +
            "COUNT(*) AS uses, SUM(t.eop_ns) AS eop_ns, SUM(t.eop_ns) / 1e6 AS eop_ms " +
            "FROM shaders s JOIN events e ON e.queue_index = s.queue_index AND e.event_index = s.event_index " +
            "LEFT JOIN timing t ON t.queue_index = s.queue_index AND t.event_index = s.event_index " +
            $"WHERE {Scope} GROUP BY shader, s.stage ORDER BY eop_ns DESC LIMIT $limit",
            [Limit], ["core", "shaders", "timing"], ["A work event counts once per shader it binds.", "Shaders without a hash stay individual occurrences.", ScopeCaveat],
            "Which shaders sit under the most GPU time?"),
        new("cost_by_pso", "Pipelines (bound shader sets) by summed EOP time.",
            "SELECT p.pso_key, COUNT(*) AS events, COUNT(t.eop_ns) AS timed, SUM(t.eop_ns) AS eop_ns, SUM(t.eop_ns) / 1e6 AS eop_ms, MAX(t.eop_ns) AS max_eop_ns " +
            "FROM event_pipelines p JOIN events e ON e.queue_index = p.queue_index AND e.event_index = p.event_index " +
            "LEFT JOIN timing t ON t.queue_index = p.queue_index AND t.event_index = p.event_index " +
            $"WHERE {Scope} GROUP BY p.pso_key ORDER BY eop_ns DESC LIMIT $limit",
            [Limit], ["core", "shaders", "timing"], ["pso_key null groups every event without a complete shader identity.", ScopeCaveat],
            "Which pipelines are the most expensive?"),
        new("dispatch_cost_per_workitem", "Dispatches with their thread groups, EOP time per thread group and a tiny flag below 64 thread groups.",
            "SELECT e.queue_index, e.event_index, e.name, e.marker_path, w.work_items AS thread_groups, t.eop_ns, t.eop_ms, " +
            "ROUND(t.eop_ns * 1.0 / NULLIF(w.work_items, 0), 1) AS ns_per_thread_group, CASE WHEN w.work_items < 64 THEN 1 ELSE 0 END AS tiny " +
            "FROM events e JOIN work_items w ON w.queue_index = e.queue_index AND w.event_index = e.event_index " +
            "LEFT JOIN timing t ON t.queue_index = e.queue_index AND t.event_index = e.event_index " +
            $"WHERE w.work_item_kind = 'threadGroups' AND {Scope} ORDER BY t.eop_ns DESC LIMIT $limit",
            [Limit], ["core", "timing"], ["Small dispatches are dominated by fixed per-dispatch overhead.", ScopeCaveat], "Are my dispatches too small?"),
        new("resource_flow", "Every bound use of one resource in event order with the access of the previous use.",
            "SELECT u.queue_index, u.event_index, e.name, e.marker_path, u.view_type, u.access, " +
            "LAG(u.access) OVER (ORDER BY u.queue_index, u.event_index, u.view_index) AS previous_access " +
            "FROM resource_uses u JOIN events e ON e.queue_index = u.queue_index AND e.event_index = u.event_index " +
            $"WHERE u.api_object_id = $apiObjectId COLLATE NOCASE AND {Scope} ORDER BY u.queue_index, u.event_index, u.view_index LIMIT $limit",
            [new("apiObjectId", "string", null, "Resource API object id (hex), as in resources.api_object_id.", Required: true), Limit], ["core", "resourceUses"],
            ["Access comes from the view type only; copies and barriers are not resource_uses rows.", ScopeCaveat], "When is this texture written and when is it read?"),
        new("barriers_per_pass", "Barrier calls per marker path.",
            $"SELECT e.marker_path, COUNT(*) AS barriers FROM events e WHERE e.kind = 'barrier' AND {Scope} GROUP BY e.marker_path ORDER BY barriers DESC LIMIT $limit",
            [Limit], ["core"], [ScopeCaveat], "Which passes issue the most resource barriers?"),
        new("counters_for_events", "Values of one counter per event with the event's EOP time and value per millisecond.",
            "SELECT c.queue_index, c.event_index, e.name, e.kind, c.row_kind, c.value, c.value_text, t.eop_ns, " +
            "CASE WHEN t.eop_ns > 0 THEN c.value / (t.eop_ns / 1e6) END AS value_per_ms " +
            "FROM counters c JOIN events e ON e.queue_index = c.queue_index AND e.event_index = c.event_index " +
            "LEFT JOIN timing t ON t.queue_index = c.queue_index AND t.event_index = c.event_index " +
            $"WHERE c.counter_id = $counterId AND {Scope} ORDER BY c.value DESC LIMIT $limit",
            [new("counterId", "integer", null, "Counter id from counter_info.", Required: true), Limit], ["core", "counters", "timing"],
            ["Marker rows (row_kind marker) are PIX's own measurement over the marker span in a separate playback round; never add event rows to them.", ScopeCaveat],
            "Which events have the highest value of this counter?"),
        new("counter_ratio", "The ratio of two counters per event row.",
            "SELECT n.queue_index, n.event_index, e.name, n.value AS numerator, d.value AS denominator, n.value / NULLIF(d.value, 0) AS ratio " +
            "FROM counters n JOIN counters d ON d.queue_index = n.queue_index AND d.event_index = n.event_index AND d.counter_id = $denominatorId " +
            "JOIN events e ON e.queue_index = n.queue_index AND e.event_index = n.event_index " +
            $"WHERE n.counter_id = $numeratorId AND n.row_kind = 'event' AND {Scope} ORDER BY ratio DESC LIMIT $limit",
            [new("numeratorId", "integer", null, "Numerator counter id.", Required: true), new("denominatorId", "integer", null, "Denominator counter id.", Required: true), Limit],
            ["core", "counters"], ["Rows where the denominator is 0 have a null ratio.", ScopeCaveat], "Which events have the worst ratio between two counters?"),
        new("frames_summary", "Frames with their event range, work events and replay-clock window.",
            "SELECT f.frame_index, f.queue_index, f.first_event_index, f.last_event_index, f.partial, " +
            "(SELECT COUNT(*) FROM events w WHERE w.frame_index = f.frame_index AND w.is_work = 1) AS work_events, " +
            "f.window_start_ns, f.window_end_ns, (f.window_end_ns - f.window_start_ns) / 1e6 AS window_ms FROM frames f ORDER BY f.frame_index LIMIT $limit",
            [Limit], ["core"], ["Frames are delimited by Present calls; replay-clock windows are not application frame times."], "How many frames does the capture hold?"),
        new("kinds_by_queue", "Event counts per queue and kind.",
            $"SELECT e.queue_index, e.kind, COUNT(*) AS events FROM events e WHERE {Scope} GROUP BY e.queue_index, e.kind ORDER BY e.queue_index, events DESC LIMIT $limit",
            [Limit], ["core"], [ScopeCaveat], "What does each queue do?"),
        new("untimed_events", "Work events without replay timing.",
            "SELECT e.queue_index, e.event_index, e.name, e.kind, e.marker_path FROM events e " +
            "LEFT JOIN timing t ON t.queue_index = e.queue_index AND t.event_index = e.event_index " +
            $"WHERE e.is_work = 1 AND t.eop_ns IS NULL AND {Scope} ORDER BY e.queue_index, e.event_index LIMIT $limit",
            [Limit], ["core", "timing"], [ScopeCaveat], "Which draws or dispatches have no timing?"),
        new("unbalanced_markers", "Measured markers whose children add up to more than PIX measured.",
            "SELECT e.queue_index, e.event_index, e.path, tt.measured_eop_ns, tt.child_sum_ns, tt.child_overflow_ns FROM events e " +
            "JOIN timing_tree tt ON tt.queue_index = e.queue_index AND tt.event_index = e.event_index " +
            $"WHERE tt.children_exceed_measured = 1 AND {Scope} ORDER BY tt.child_overflow_ns DESC LIMIT $limit",
            [Limit], ["core", "timing"], ["Overflowing children usually overlap in the pipeline; their EOP spans include each other's time.", ScopeCaveat],
            "Where do child timings exceed the marker's own measurement?"),
    ];

    public static NamedTimingQuery? Find(string name) => All.FirstOrDefault(q => q.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
}
