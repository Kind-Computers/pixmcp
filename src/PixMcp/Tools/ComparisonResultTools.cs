using System.ComponentModel;
using ModelContextProtocol.Server;
using PixMcp.Pix;

namespace PixMcp.Tools;

[McpServerToolType]
public static class ComparisonResultTools
{
    [McpServerTool(Name = "pix_gpu_compare_changes", ReadOnly = true), Description("Filters, sorts and pages a completed GPU comparison using its fullResultRef without replay. Nonzero thresholds are inclusive absolute magnitudes and combine with AND; default zero thresholds retain structural-only changes. Missing numeric values sort last. Oversized fields retain exact result-read pointers.")]
    public static Task<string> Changes(PixSession session, string fullResultRef,
        [Description("all, regressions, or improvements.")] string direction = "all",
        decimal minDeltaNs = 0, double minDeltaPercent = 0,
        [Description("absoluteDeltaNs, deltaNs, deltaPercent, or event.")] string sortBy = "absoluteDeltaNs",
        bool descending = true, int offset = 0, int limit = 25, CancellationToken cancellationToken = default)
        => PixErrors.Guard("pix_gpu_compare_changes", async () => Tools.Serialize(await Task.Run(
            () => ComparisonResultQuery.Read(session.Results, fullResultRef, direction, minDeltaNs, minDeltaPercent, sortBy, descending, offset, limit, cancellationToken),
            cancellationToken).ConfigureAwait(false), "pix_gpu_compare_changes"));
}
