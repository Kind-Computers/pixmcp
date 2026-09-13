using System.ComponentModel;
using PixMcp.Pix.Sql;

namespace PixMcp.Pix;

/// <summary>How many schema objects the PixStorage database exposes.</summary>
public sealed record TimingSchemaCensusDto(int BaseTables, int VirtualTables, int Views, int Indexes, int Functions);

/// <summary>One column: PRAGMA table_xinfo facts plus documentation. Hidden virtual-table columns are constraint inputs (filter with = to push down).</summary>
public sealed record TimingSchemaColumnDto(
    string Name, string? Type, bool PrimaryKey, bool NotNull, bool Hidden,
    [property: Description("constraintInput for hidden virtual-table columns (usable in WHERE/JOIN, not returned by SELECT *).")] string? Role = null,
    [property: Description("Unit from the documentation overlay (ns = capture-clock nanoseconds).")] string? Unit = null,
    string? Description = null);

/// <summary>A table, view or PixStorage virtual table.</summary>
public sealed record TimingSchemaTableDto(
    string Name,
    [property: Description("table, view or virtual.")] string Kind,
    [property: Description("The documentation overlay describes this object.")] bool Known,
    [property: Description("For virtual tables, the base table PixStorage decodes rows from, when documented.")] string? BackingTable,
    long? Rows,
    [property: Description("exact, timedOut (2 s budget) or notRequested (pass includeRowCounts).")] string RowCountState,
    string? Purpose,
    IReadOnlyList<TimingSchemaColumnDto>? Columns);

public sealed record TimingSchemaIndexDto(string Name, bool Unique, IReadOnlyList<string> Columns);
public sealed record TimingSchemaForeignKeyDto(string From, string Table, string? To);

/// <summary>Everything about one object: DDL, indexes, declared foreign keys, documented joins and caveats, optional sample rows.</summary>
public sealed record TimingSchemaTableDetailDto(
    TimingSchemaTableDto Table,
    string? Ddl,
    IReadOnlyList<TimingSchemaIndexDto> Indexes,
    IReadOnlyList<TimingSchemaForeignKeyDto> ForeignKeys,
    [property: Description("Documented join recipes, observed on the verified PIX build.")] IReadOnlyList<string> Joins,
    IReadOnlyList<string> Caveats,
    SqlResultDto? SampleRows);

public sealed record TimingSchemaFunctionDto(string Name, int Arguments, string? Description);
public sealed record TimingCaptureFactDto(long Id, string Value, string? Meaning);

public sealed record TimingQueryParamDto(string Name, string Type, object? Default, string Description);

/// <summary>A library query: its parameters and whether this capture has what it needs; requires, caveats and SQL are included when the query is requested by name.</summary>
public sealed record TimingNamedQueryDto(
    string Name, string Description, IReadOnlyList<TimingQueryParamDto> Params,
    [property: Description("table:column pairs the query needs; listed when the query is requested by name.")] IReadOnlyList<string> Requires,
    [property: Description("available when every required table:column exists in this capture, else unsupported.")] string RequiresState,
    IReadOnlyList<string> Caveats, string? ExampleQuestion,
    [property: Description("The SQL body; included only when the query was requested by name.")] string? Sql = null);

/// <summary>The PixStorage schema of a timing capture with documentation, capability probes, capture facts and the values pix_timing_sql pre-binds.</summary>
public sealed record TimingSchemaDto(
    string Handle,
    string SqliteVersion,
    TimingSchemaCensusDto Census,
    PageResult<TimingSchemaTableDto> Tables,
    TimingSchemaTableDetailDto? Table,
    IReadOnlyList<TimingSchemaFunctionDto> Functions,
    IReadOnlyList<TimingCaptureFactDto> CaptureFacts,
    [property: Description("Parameters pix_timing_sql binds when a statement declares them.")] IReadOnlyList<SqlBoundParameterDto> BoundParameters,
    IReadOnlyDictionary<string, TimingCapabilityDto> Capabilities,
    IReadOnlyList<TimingNamedQueryDto>? NamedQueries,
    TimingRangeDto Provenance,
    IReadOnlyList<string> Notes,
    IReadOnlyList<ToolCallDto> NextCalls);
