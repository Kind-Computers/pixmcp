using System.ComponentModel;
using System.Text.Json;

namespace PixMcp.Pix.Sql;

/// <summary>
/// One caller-supplied read-only statement and how to shape its rows. Params bind by name ($name, @name or :name in
/// the SQL); Prebound values (server-supplied, such as $reliableStart) bind only when the SQL declares them and cannot
/// be shadowed by Params.
/// </summary>
public sealed record SqlRequest(string Sql)
{
    public const int MaxSqlLength = 65536;
    public const int DefaultMaxRows = 200, MaxMaxRows = 5000;
    public const int DefaultMaxBytes = 131072, MinMaxBytes = 4096, MaxMaxBytes = 1048576;
    public const int DefaultMaxStringLength = 256, MinMaxStringLength = 16, MaxMaxStringLength = 65536;
    public const double DefaultTimeoutSeconds = 30;
    public const int BlobPreviewBytes = 4096;

    public IReadOnlyDictionary<string, JsonElement>? Params { get; init; }
    public IReadOnlyDictionary<string, object?>? Prebound { get; init; }
    public int MaxRows { get; init; } = DefaultMaxRows;
    public int MaxBytes { get; init; } = DefaultMaxBytes;
    public int MaxStringLength { get; init; } = DefaultMaxStringLength;
    public int Offset { get; init; }
    public bool CountTotal { get; init; }
    public bool Explain { get; init; }
    public bool IncludeBlobs { get; init; }
    public double TimeoutSeconds { get; init; } = DefaultTimeoutSeconds;
}

/// <summary>A result column: SQL name, declared type and affinity when the column traces to a table column, plus documentation when known.</summary>
public sealed record SqlColumnDto(
    [property: Description("Column name as the statement produced it.")] string Name,
    [property: Description("Declared type of the source table column; null for expressions.")] string? DeclaredType,
    [property: Description("SQLite affinity of the declared type (INTEGER, TEXT, BLOB, REAL, NUMERIC); null for expressions.")] string? Affinity,
    [property: Description("Unit when the schema documentation names one (ns for timestamps and durations).")] string? Unit = null,
    [property: Description("Schema documentation for the column, when known.")] string? Description = null);

/// <summary>A parameter the statement declared and the value bound to it.</summary>
public sealed record SqlBoundParameterDto(
    [property: Description("Parameter name as written in the SQL, including its $, @ or : prefix.")] string Name,
    [property: Description("Bound value.")] object? Value,
    [property: Description("params (caller) or prebound (server).")] string Source);

/// <summary>One EXPLAIN QUERY PLAN row.</summary>
public sealed record SqlPlanRowDto(long Id, long Parent, string Detail);

/// <summary>
/// Rows of one read-only statement: positional cells in column order, truncation state with an exact continuation,
/// the tables SQLite read (from the authorizer), bound parameters and the optional query plan.
/// </summary>
public sealed record SqlResultDto(
    string? Handle,
    [property: Description("Database the statement ran against (pixstorage for timing captures).")] string Source,
    [property: Description("The statement text that ran.")] string Sql,
    IReadOnlyList<SqlColumnDto> Columns,
    TableLegend Legend,
    [property: Description("Positional rows; integers beyond 2^53, NaN and infinities are strings; long text ends with an ellipsis; BLOBs are summaries or base64 previews.")] IReadOnlyList<object?[]> Rows,
    int RowCount,
    int Offset,
    [property: Description("More rows follow; repeat with offset = nextOffset (stable only with ORDER BY).")] bool HasMore,
    [property: Description("maxRows or maxBytes when hasMore.")] string? TruncationReason,
    [property: Description("Total rows the statement produces, when countTotal was requested.")] long? TotalRows,
    [property: Description("Cells whose text or BLOB was shortened.")] int TruncatedCells,
    double ElapsedMs,
    [property: Description("Tables and virtual tables SQLite read while preparing and running the statement.")] IReadOnlyList<string> ReferencedTables,
    IReadOnlyList<SqlBoundParameterDto> BoundParameters,
    IReadOnlyList<SqlPlanRowDto>? Plan,
    IReadOnlyList<string> Notes,
    IReadOnlyList<ToolCallDto> NextCalls)
{
    public int? NextOffset => HasMore ? Offset + RowCount : null;
    /// <summary>What the pre-bound parameters describe: the timing window and capture facts, or the GPU replay provenance.</summary>
    public object? Provenance { get; init; }
    /// <summary>The named library query that supplied the SQL, when one did.</summary>
    public string? Query { get; init; }
}
