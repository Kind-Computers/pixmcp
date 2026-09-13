using System.ComponentModel;

namespace PixMcp.Pix.Sql;

/// <summary>One family of the GPU SQL store.</summary>
public sealed record GpuSqlFamilyStateDto(string Family,
    [property: Description("empty or ready.")] string State,
    long Rows, string? PopulatedAt, int? AnalysisGeneration,
    [property: Description("True when a replay family was populated under an earlier GPU analysis; its rows still answer queries, and populate with force=true refreshes them.")] bool Stale,
    [property: Description("What populating costs: query (no replay) or replay.")] string Cost,
    string Requires, string? Detail);

public sealed record GpuSqlColumnDto(string Name, string Type, string? Unit, string Description);

public sealed record GpuSqlTableDto(string Name, string Family, string Description, IReadOnlyList<GpuSqlColumnDto>? Columns);

public sealed record GpuSqlViewDto(string Name, string Description, IReadOnlyList<string> Families, string? Sql);

public sealed record GpuSqlStoreDto(bool Exists, long Bytes, long MaxBytes, int SchemaVersion);

/// <summary>pix_gpu_sql_tables: store state, families, documented tables and views, named queries and pre-bound parameters.</summary>
public sealed record GpuSqlTablesDto(string Handle, string Detail, GpuSqlStoreDto Store, IReadOnlyList<GpuSqlFamilyStateDto> Families, IReadOnlyList<GpuSqlTableDto> Tables,
    IReadOnlyList<GpuSqlViewDto> Views, IReadOnlyList<TimingNamedQueryDto> NamedQueries, IReadOnlyList<string> PreboundParameters, IReadOnlyList<string> Notes,
    IReadOnlyList<ToolCallDto> NextCalls);

/// <summary>The result of a populate job.</summary>
public sealed record GpuSqlPopulateDto(string Handle, IReadOnlyList<GpuSqlFamilyStateDto> Families, IReadOnlyList<string> Skipped, long StoreBytes, IReadOnlyList<ToolCallDto> NextCalls);

/// <summary>The result of an export job.</summary>
public sealed record GpuSqlExportDto(string Handle, string OutPath, string Format, long Rows, bool Truncated, IReadOnlyList<string> Columns, long Bytes);
