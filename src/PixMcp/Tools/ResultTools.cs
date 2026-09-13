using System.ComponentModel;
using ModelContextProtocol.Server;
using PixMcp.Pix;

namespace PixMcp.Tools;

[McpServerToolType]
public static class ResultTools
{
    [McpServerTool(Name = "pix_result_read", Title = "Read result window", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Reads a bounded window from an immutable result snapshot without using the PIX worker. A JSON pointer selects nested data; offset/limit page array entries, object properties, or UTF-16 string characters. mode=outline returns the shape (kinds, counts, bytes, key samples) without values, so a large result is explored in one call; fields/where select and filter array elements server-side. String windows preserve surrogate pairs (limit 1 may return two code units); follow nextOffset. Large children include exact retrieval calls. Transient results retain the latest 50; job results last until pruning; closing an owning capture expires its results.")]
    public static string Read(PixSession session, [Description("Result reference (r-...) from a deferred response or a finished job.")] string resultRef,
        [Description("RFC 6901 JSON pointer; empty selects the complete result.")] string pointer = "",
        [Description("Zero-based item/property/character offset.")] int offset = 0,
        [Description("Maximum items/properties/characters (default 25, maximum 1000).")] int limit = 25,
        [Description("values (default) returns contents; outline returns each child's kind, count, bytes and key sample without contents.")] string mode = "values",
        [Description(ResultProjection.FieldsDescription)] string[]? fields = null,
        [Description(ResultProjection.WhereDescription)] WhereClause[]? where = null,
        CancellationToken cancellationToken = default)
        => Tools.Serialize(session.Results.Query(resultRef, pointer, offset, limit, mode, fields, where, cancellationToken), "pix_result_read");

    [McpServerTool(Name = "pix_result_export", Title = "Export result to file", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description("Exports the complete JSON subtree selected by a result reference and JSON pointer to a file without replaying or using the PIX worker. Streams spilled results, writes atomically, and refuses to overwrite an existing file unless overwrite=true.")]
    public static Task<string> Export(PixSession session, [Description("Result reference (r-...) from a deferred response or a finished job.")] string resultRef, [Description("Destination file path; the parent directory must exist.")] string outPath,
        [Description("RFC 6901 JSON pointer; empty exports the complete result.")] string pointer = "",
        [Description("Replace an existing file (default false: file_exists).")] bool overwrite = false, CancellationToken cancellationToken = default)
        => PixErrors.Guard("pix_result_export", async () => Tools.Serialize(await Task.Run(
            () => session.Results.Export(resultRef, outPath, pointer, overwrite, cancellationToken), cancellationToken).ConfigureAwait(false), "pix_result_export"));
}
