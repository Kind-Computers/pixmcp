using System.ComponentModel;
using ModelContextProtocol.Server;
using PixMcp.Pix;

namespace PixMcp.Tools;

[McpServerToolType]
public static class ResultTools
{
    [McpServerTool(Name = "pix_result_read", ReadOnly = true), Description("Reads a bounded window from an immutable result snapshot without using the PIX worker. A JSON pointer selects nested data; offset/limit page array entries, object properties, or UTF-16 string characters. String windows preserve surrogate pairs (limit 1 may return two code units); follow nextOffset. Large children include exact retrieval calls. Transient results retain the latest 50; job results last until pruning; closing an owning capture expires its results.")]
    public static string Read(PixSession session, string resultRef,
        [Description("RFC 6901 JSON pointer; empty selects the complete result.")] string pointer = "",
        [Description("Zero-based item/property/character offset.")] int offset = 0,
        [Description("Maximum items/properties/characters (default 25, maximum 1000).")] int limit = 25,
        CancellationToken cancellationToken = default)
        => Tools.Serialize(session.Results.Read(resultRef, pointer, offset, limit, cancellationToken), "pix_result_read");

    [McpServerTool(Name = "pix_result_export"), Description("Exports the complete JSON subtree selected by a result reference and JSON pointer to a file without replaying or using the PIX worker. Streams spilled results, writes atomically, and refuses to overwrite an existing file unless overwrite=true.")]
    public static Task<string> Export(PixSession session, string resultRef, string outPath,
        [Description("RFC 6901 JSON pointer; empty exports the complete result.")] string pointer = "",
        bool overwrite = false, CancellationToken cancellationToken = default)
        => PixErrors.Guard("pix_result_export", async () => Tools.Serialize(await Task.Run(
            () => session.Results.Export(resultRef, outPath, pointer, overwrite, cancellationToken), cancellationToken).ConfigureAwait(false), "pix_result_export"));
}
