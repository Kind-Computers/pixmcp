using System.Text.Json;
using System.Text.Json.Nodes;

namespace PixMcp.Pix;

/// <summary>How a paged tool should present its rows.</summary>
/// <param name="Format">objects (the typed DTO rows) or table (positional rows with a legend).</param>
/// <param name="Brief">Only the columns that identify and rank a row.</param>
/// <param name="TopN">Return the first N rows of the sorted set with no continuation; requires offset 0.</param>
/// <param name="MaxStringLength">Strings longer than this are cut and counted in truncatedStrings.</param>
public sealed record ShapingOptions(string Format = "objects", bool Brief = false, int? TopN = null, int MaxStringLength = 200)
{
    public bool Table => Format == "table";
}

public sealed record ColumnDto(string Name, string Type, string? Unit, string Description);

/// <summary>How to rebuild a reference object from positional columns; "$handle" is the response's handle, "$col:x" the named column.</summary>
public sealed record RefRecipe(string Kind, IReadOnlyDictionary<string, string> From);

public sealed record TableLegend(string RowFormat, IReadOnlyDictionary<string, RefRecipe> Refs, IReadOnlyDictionary<string, string> Notes);

/// <summary>Positional rows plus the columns and legend that make them self-describing.</summary>
public sealed record TableDto(string? Handle, IReadOnlyList<ColumnDto> Columns, TableLegend Legend, IReadOnlyList<object?[]> Rows,
    long Total, int Offset, int Count, int? NextOffset, bool TruncatedRows, int TruncatedStrings, object? Extra,
    IReadOnlyList<ToolCallDto> NextCalls);

/// <summary>One column of a shaped row: how it is named, typed and selected from the DTO.</summary>
public sealed record ColumnSpec<T>(string Name, string Type, string? Unit, string Description, bool Brief, Func<T, object?> Select);

/// <summary>Everything a paged tool declares once per row type: its columns, how refs are rebuilt, and how to trim a row for brief output.</summary>
public sealed record RowShape<T>(IReadOnlyList<ColumnSpec<T>> Columns, IReadOnlyDictionary<string, RefRecipe> Refs,
    IReadOnlyDictionary<string, string> Notes, Func<T, T>? Brief = null);

public static class Shaping
{
    public const string FormatDescription = "objects (default: typed rows) or table (positional rows with columns and a legend; several times smaller).";
    public const string BriefDescription = "Only identifying and ranking fields (default false).";
    public const string TopNDescription = "Return only the first N rows of the sorted set and no continuation (1..1000); offset must be 0.";
    public const string MaxStringLengthDescription = "Cut strings longer than this many characters (default 200, 16..4096); truncatedStrings counts them and a continuation with maxStringLength=4096 is offered.";

    public static readonly string[] Formats = { "objects", "table" };
    public const int MinStringLength = 16, MaxStringLengthLimit = 4096, DefaultStringLength = 200, FullStringLength = 4096;

    public static readonly RefRecipe EventRefRecipe = new("EventRef", new Dictionary<string, string>
    {
        ["handle"] = "$handle", ["queueIndex"] = "$col:queueIndex", ["eventIndex"] = "$col:eventIndex",
    });
    public static readonly RefRecipe ShaderRefRecipe = new("ShaderRef", new Dictionary<string, string>
    {
        ["eventRef.handle"] = "$handle", ["eventRef.queueIndex"] = "$col:queueIndex", ["eventRef.eventIndex"] = "$col:eventIndex", ["shaderIndex"] = "$col:shaderIndex",
    });
    public static readonly RefRecipe ResourceRefRecipe = new("ResourceRef", new Dictionary<string, string>
    {
        ["handle"] = "$handle", ["apiObjectId"] = "$col:apiObjectId",
    });

    /// <summary>Validates the shaping parameters a tool received; invalid combinations are invalid_arguments.</summary>
    public static ShapingOptions Options(string? format, bool brief, int? topN, int? maxStringLength, int offset)
    {
        string f = string.IsNullOrWhiteSpace(format) ? "objects" : format.Trim().ToLowerInvariant();
        if (!Formats.Contains(f))
            throw new PixToolException(PixErrors.Codes.InvalidArguments, $"format must be one of: {string.Join(", ", Formats)}.");
        if (topN is < 1 or > Paging.MaxLimit)
            throw new PixToolException(PixErrors.Codes.InvalidArguments, $"topN must be between 1 and {Paging.MaxLimit}.");
        if (topN.HasValue && offset != 0)
            throw new PixToolException(PixErrors.Codes.InvalidArguments, "topN returns the first rows of the sorted set; pass offset=0 or use offset/limit paging instead.");
        int max = maxStringLength ?? DefaultStringLength;
        if (max is < MinStringLength or > MaxStringLengthLimit)
            throw new PixToolException(PixErrors.Codes.InvalidArguments, $"maxStringLength must be between {MinStringLength} and {MaxStringLengthLimit}.");
        return new(f, brief, topN, max);
    }

    /// <summary>The effective page window: topN replaces offset/limit.</summary>
    public static (int Offset, int Limit) Window(ShapingOptions options, int offset, int limit)
        => options.TopN.HasValue ? (0, options.TopN.Value) : Paging.Normalize(offset, limit);

    /// <summary>
    /// Shapes one already-sorted page. <paramref name="page"/> holds the rows of the window, <paramref name="total"/> the
    /// full count. <paramref name="continuation"/> builds the call for a later offset; topN suppresses it.
    /// <paramref name="fullStrings"/> builds the same call with maxStringLength raised, offered when strings were cut.
    /// </summary>
    public static object Apply<T>(IReadOnlyList<T> page, long total, int offset, int limit, ShapingOptions options, RowShape<T> shape,
        string? handle, object? extra, Func<int, ToolCallDto>? continuation, Func<ToolCallDto>? fullStrings)
    {
        int? nextOffset = !options.TopN.HasValue && offset + (long)page.Count < total ? checked(offset + page.Count) : null;
        if (options.Table)
        {
            IReadOnlyList<ColumnSpec<T>> columns = options.Brief ? shape.Columns.Where(c => c.Brief).ToArray() : shape.Columns;
            int truncated = 0;
            var rows = new List<object?[]>(page.Count);
            foreach (T item in page)
            {
                var cells = new object?[columns.Count];
                for (int i = 0; i < columns.Count; i++)
                {
                    object? cell = columns[i].Select(item);
                    if (cell is string text && text.Length > options.MaxStringLength) { cell = Cut(text, options.MaxStringLength); truncated++; }
                    cells[i] = cell;
                }
                rows.Add(cells);
            }
            var next = new List<ToolCallDto>();
            if (nextOffset.HasValue && continuation is not null) next.Add(continuation(nextOffset.Value));
            if (truncated > 0 && fullStrings is not null) next.Add(fullStrings());
            var notes = new Dictionary<string, string>(shape.Notes) { ["integerEncoding"] = "integers above 2^53 are decimal strings" };
            if (options.Brief) notes["brief"] = "only identifying and ranking columns are included";
            return new TableDto(handle, columns.Select(c => new ColumnDto(c.Name, c.Type, c.Unit, c.Description)).ToArray(),
                new TableLegend("positional; columns[i] describes rows[*][i]; null cells are present", shape.Refs, notes),
                rows, total, offset, page.Count, nextOffset, total > page.Count, truncated, extra, next);
        }

        IReadOnlyList<T> items = options.Brief && shape.Brief is not null ? page.Select(shape.Brief).ToArray() : page;
        var result = new PageResult<T>(total, offset, items.Count, nextOffset, items, extra);
        JsonObject node = JsonSerializer.SerializeToNode(result, Json.Options)!.AsObject();
        int cut = 0;
        if (node["items"] is JsonNode itemsNode) TruncateStrings(itemsNode, options.MaxStringLength, ref cut);
        if (cut == 0 && !options.Brief && !options.TopN.HasValue) return result;
        JsonObject extraNode = node["extra"] as JsonObject ?? new JsonObject();
        if (options.Brief) extraNode["brief"] = true;
        if (options.TopN.HasValue) extraNode["topN"] = options.TopN.Value;
        if (cut > 0)
        {
            extraNode["truncatedStrings"] = cut;
            if (fullStrings is not null) extraNode["fullStrings"] = JsonSerializer.SerializeToNode(fullStrings(), Json.Options);
        }
        node["extra"] = extraNode;
        return node;
    }

    /// <summary>Cuts every string value below <paramref name="node"/> that is longer than <paramref name="max"/>, counting the cuts.</summary>
    public static void TruncateStrings(JsonNode? node, int max, ref int truncated)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (KeyValuePair<string, JsonNode?> property in obj.ToArray())
                {
                    if (property.Value is JsonValue value && value.TryGetValue(out string? text))
                    {
                        if (text.Length > max) { obj[property.Key] = Cut(text, max); truncated++; }
                    }
                    else TruncateStrings(property.Value, max, ref truncated);
                }
                break;
            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue value && value.TryGetValue(out string? text))
                    {
                        if (text.Length > max) { array[i] = Cut(text, max); truncated++; }
                    }
                    else TruncateStrings(array[i], max, ref truncated);
                }
                break;
        }
    }

    public static string Cut(string text, int max) => text.Length <= max ? text : string.Concat(text.AsSpan(0, max - 1), "…");

    public static string JoinPath(IReadOnlyList<string>? path) => path is null ? "" : string.Join('/', path);

    /// <summary>The DurationDto columns of a field, flattened to dotted names.</summary>
    public static IEnumerable<ColumnSpec<T>> Duration<T>(string prefix, string description, bool brief, Func<T, DurationDto?> select) =>
    [
        new($"{prefix}.ns", "integer", "ns", description + " in nanoseconds", brief, r => select(r)?.Ns),
        new($"{prefix}.ms", "number", "ms", description + " in milliseconds", brief, r => select(r)?.Ms),
        new($"{prefix}.percentOfQueueSpan", "number", "percent", description + " as a share of the queue span", brief, r => select(r)?.PercentOfQueueSpan),
        new($"{prefix}.percentOfQueueSum", "number", "percent", description + " as a share of the sum of roots", false, r => select(r)?.PercentOfQueueSum),
        new($"{prefix}.percentOfParent", "number", "percent", description + " as a share of the parent", false, r => select(r)?.PercentOfParent),
        new($"{prefix}.rank", "integer", null, "1-based rank in the producing order", brief, r => select(r)?.Rank),
    ];

    public static IReadOnlyDictionary<string, string> DurationNotes => new Dictionary<string, string>
    {
        ["percentOfQueueSpan"] = Metrics.Denominators.PercentOfQueueSpan,
        ["percentOfQueueSum"] = Metrics.Denominators.PercentOfQueueSum,
        ["percentOfParent"] = Metrics.Denominators.PercentOfParent,
    };
}
