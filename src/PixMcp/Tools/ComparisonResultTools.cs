using System.ComponentModel;
using ModelContextProtocol.Server;
using PixMcp.Pix;

namespace PixMcp.Tools;

[McpServerToolType]
public static class ComparisonResultTools
{
    [McpServerTool(Name = "pix_gpu_compare_changes", Title = "Filter comparison changes", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Filters, sorts and pages a completed GPU comparison using its fullResultRef without replay. Nonzero thresholds are inclusive absolute magnitudes and combine with AND; default zero thresholds retain structural-only changes. Missing numeric values sort last. Oversized fields retain exact result-read pointers.")]
    public static Task<string> Changes(PixSession session, [Description("The fullResultRef returned by pix_gpu_compare (the complete comparison snapshot).")] string fullResultRef,
        [Description("all, regressions, or improvements. Default: all.")] string direction = "all",
        [Description("Only changes whose absolute EOP delta is at least this many nanoseconds (default 0).")] decimal minDeltaNs = 0, [Description("Only changes whose absolute delta is at least this percent of the baseline (default 0).")] double minDeltaPercent = 0,
        [Description("absoluteDeltaNs, deltaNs, deltaPercent, or event. Default: absoluteDeltaNs.")] string sortBy = "absoluteDeltaNs",
        [Description("Sort descending (default true).")] bool descending = true, [Description("First item to return (default 0).")] int offset = 0, [Description("Maximum items to return (default 25, max 1000).")] int limit = 25, CancellationToken cancellationToken = default)
        => PixErrors.Guard("pix_gpu_compare_changes", async () => Tools.Serialize(await Task.Run(
            () => ComparisonResultQuery.Read(session.Results, fullResultRef, direction, minDeltaNs, minDeltaPercent, sortBy, descending, offset, limit, cancellationToken),
            cancellationToken).ConfigureAwait(false), "pix_gpu_compare_changes"));
}
