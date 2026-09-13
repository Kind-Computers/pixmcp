using System.ComponentModel;
using ModelContextProtocol.Server;
using PixMcp.Pix;

namespace PixMcp.Tools;

[McpServerToolType]
public static class ComparisonResultTools
{
    [McpServerTool(Name = "pix_gpu_compare_changes", Title = "Filter comparison changes", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Filters, sorts and pages a completed GPU comparison using its fullResultRef without replay. Filters combine with AND: direction (all, regressions, improvements, or structural for field changes without a timing delta), inclusive absolute thresholds, markerPathPrefix, kind, queueIndex, section, excludeBelowNoise and minConfidence; rows without kind, confidence or noise fields pass those filters. Default zero thresholds retain structural-only changes. Missing numeric values sort last. Oversized fields retain exact result-read pointers.")]
    public static Task<string> Changes(PixSession session,
        [Description("The fullResultRef returned by pix_gpu_compare (the complete comparison snapshot).")] string fullResultRef,
        [Description("all (default), regressions, improvements or structural (field changes without a timing delta).")] string direction = "all",
        [Description("Only changes whose absolute EOP delta is at least this many nanoseconds (default 0).")] decimal minDeltaNs = 0,
        [Description("Only changes whose absolute delta is at least this percent of the baseline (default 0).")] double minDeltaPercent = 0,
        [Description("absoluteDeltaNs (default), deltaNs, deltaPercent or event.")] string sortBy = "absoluteDeltaNs",
        [Description("Sort descending (default true).")] bool descending = true,
        [Description("First item to return (default 0).")] int offset = 0,
        [Description("Maximum items to return (default 25, max 1000).")] int limit = 25,
        [Description("Only changes whose marker path starts with these '/'-separated segments (case-insensitive).")] string? markerPathPrefix = null,
        [Description("Only changes of this event kind, such as draw or dispatch.")] string? kind = null,
        [Description("Only changes on this queue index (baseline or candidate side).")] int? queueIndex = null,
        [Description("Only changes with a field change in this section: shaders, pipeline, resources or rootConstants.")] string? section = null,
        [Description("Drop changes within the noise floor measured by pix_gpu_compare repeats (default false).")] bool excludeBelowNoise = false,
        [Description("Only changes whose match confidence is at least low, medium or high.")] string? minConfidence = null,
        CancellationToken cancellationToken = default)
        => PixErrors.Guard("pix_gpu_compare_changes", async () => Tools.Serialize(await Task.Run(
            () => ComparisonResultQuery.Read(session.Results, fullResultRef, direction, minDeltaNs, minDeltaPercent, sortBy, descending, offset, limit, cancellationToken,
                markerPathPrefix, kind, queueIndex, section, excludeBelowNoise, minConfidence),
            cancellationToken).ConfigureAwait(false), "pix_gpu_compare_changes"));
}
