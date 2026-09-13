using System.Text.Json;
using System.Text.Json.Nodes;
using PixMcp.Pix.Handles;

namespace PixMcp.Pix;

/// <summary>
/// Best-effort cost of a suggested call: <c>cached</c> (answers from memory), <c>query</c> (reads capture metadata or
/// SQLite), <c>replay</c> (starts a GPU replay or collection), <c>pixtool</c> (spawns pixtool), <c>job</c> (waits on a job).
/// Advisory only; readiness flags are read off the worker and can lag one job transition.
/// </summary>
public static class CostHints
{
    public const string Cached = "cached", Query = "query", Replay = "replay", PixTool = "pixtool", Job = "job";

    private static readonly HashSet<string> CachedTools = new(StringComparer.Ordinal)
    {
        "pix_result_read", "pix_job_status", "pix_jobs", "pix_handles", "pix_info", "pix_gpu_compare_changes", "pix_log",
        "pix_gpu_queues", "pix_gpu_info", "pix_gpu_analysis_status", "pix_gpu_sql_tables", "pix_gpu_preview_image", "pix_gpu_preview_bytes",
    };
    private static readonly HashSet<string> JobTools = new(StringComparer.Ordinal)
    {
        "pix_job_wait", "pix_job_cancel", "pix_gpu_compare", "pix_gpu_drpix_run", "pix_gpu_bottleneck", "pix_gpu_counters_prepare", "pix_gpu_counters_prepare",
        "pix_gpu_timing_prepare", "pix_gpu_timing_prepare", "pix_gpu_analysis_start", "pix_gpu_shader_profile",
        "pix_gpu_shader_static_profile", "pix_timing_resolve_symbols", "pix_device_take_gpu_capture",
        "pix_device_timing_capture_start", "pix_device_timing_capture_stop", "pix_capture_upgrade", "pix_csv_compare", "pix_gpu_sql_populate", "pix_gpu_sql_export",
    };
    private static readonly HashSet<string> PixToolTools = new(StringComparer.Ordinal)
    {
        "pix_gpu_preview", "pix_gpu_screenshot", "pix_gpu_export_cpp", "pix_gpu_subcapture",
    };
    private static readonly HashSet<string> TimingTools = new(StringComparer.Ordinal)
    {
        "pix_gpu_timing_events", "pix_gpu_timing_tree", "pix_gpu_overview", "pix_gpu_queue_overlap", "pix_gpu_bubbles", "pix_gpu_rollup", "pix_gpu_pipelines", "pix_correlate",
    };
    private static readonly HashSet<string> AnalysisTools = new(StringComparer.Ordinal)
    {
        "pix_gpu_pipeline_state", "pix_gpu_inspect_event", "pix_gpu_event_resources", "pix_gpu_shader_code", "pix_gpu_shader_search",
        "pix_gpu_shader_diagnostics", "pix_gpu_drpix_experiments", "pix_gpu_counters_list", "pix_gpu_resource_uses", "pix_gpu_shaders",
        "pix_gpu_shader_uses", "pix_gpu_occupancy", "pix_gpu_hf_counters", "pix_gpu_counters_read", "pix_gpu_counters_read",
    };

    /// <summary>The static cost of a tool, without looking at handle state.</summary>
    public static string Default(string tool)
    {
        if (CachedTools.Contains(tool)) return Cached;
        if (JobTools.Contains(tool)) return Job;
        if (PixToolTools.Contains(tool)) return PixTool;
        if (TimingTools.Contains(tool) || AnalysisTools.Contains(tool)) return Replay;
        return Query;
    }

    /// <summary>The cost given the handle's current readiness (cached when the preparation the tool needs already exists).</summary>
    public static string For(string tool, PixSession? session, string? handle)
    {
        string fallback = Default(tool);
        if (fallback != Replay || session is null || handle is null) return fallback;
        if (session.TryGet<GpuCaptureHandle>(handle) is not GpuCaptureHandle h) return fallback;
        if (TimingTools.Contains(tool)) return h.Timing is not null ? Cached : Replay;
        return tool switch
        {
            "pix_gpu_counters_read" or "pix_gpu_counters_read" => h.CounterCollections.Count > 0 ? Query : Replay,
            "pix_gpu_occupancy" => h.OccupancyData is not null ? Cached : Replay,
            "pix_gpu_hf_counters" => h.HighFrequencyCollections.Count > 0 || h.HighFrequencyCatalog is not null ? Cached : Replay,
            "pix_gpu_shaders" or "pix_gpu_shader_uses" => h.ShaderIndex is not null ? Cached : Replay,
            _ => h.AnalysisStarted ? Query : Replay,
        };
    }

    /// <summary>Sets <c>cost</c> on every nextCalls entry (at any depth) that lacks one. Returns true when something changed.</summary>
    public static bool Annotate(JsonNode? root, PixSession? session)
    {
        bool changed = false;
        Visit(root, ref changed, session, 0);
        return changed;
    }

    private static void Visit(JsonNode? node, ref bool changed, PixSession? session, int depth)
    {
        if (depth > 24) return;
        switch (node)
        {
            case JsonObject obj:
                foreach (KeyValuePair<string, JsonNode?> property in obj.ToArray())
                {
                    if (property.Key == "nextCalls" && property.Value is JsonArray calls)
                    {
                        foreach (JsonNode? call in calls)
                        {
                            if (call is not JsonObject c || c["cost"] is not null || c["tool"] is not JsonValue toolValue || !toolValue.TryGetValue(out string? tool)) continue;
                            string? handle = c["arguments"] is JsonObject args ? HandleOf(args) : null;
                            c["cost"] = For(tool, session, handle);
                            changed = true;
                        }
                    }
                    else Visit(property.Value, ref changed, session, depth + 1);
                }
                break;
            case JsonArray array:
                foreach (JsonNode? child in array) Visit(child, ref changed, session, depth + 1);
                break;
        }
    }

    private static string? HandleOf(JsonObject arguments)
    {
        foreach (KeyValuePair<string, JsonNode?> property in arguments)
        {
            if ((property.Key == "handle" || property.Key.EndsWith("Handle", StringComparison.Ordinal)) && property.Value is JsonValue v && v.TryGetValue(out string? text))
                return text;
            if (property.Value is JsonObject nested && HandleOf(nested) is string inner) return inner;
        }
        return null;
    }
}
