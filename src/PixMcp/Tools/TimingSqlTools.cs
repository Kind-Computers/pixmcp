using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using PixMcp.Pix.Sql;

namespace PixMcp.Tools;

[McpServerToolType]
public static class TimingSqlTools
{
    public const string RangeModeDescription = "full (default): $start..$end spans the first reliable timestamp through the capture end; reliable: through the capture stop timestamp. startNs/endNs override either bound.";
    private const string ParamsDescription = "Named parameters as a JSON object: { \"name\": value } binds $name, @name or :name. Scalars only (string, number, boolean, null); pass arrays as JSON text and read them with json_each($name). Cannot set the pre-bound names.";

    /// <summary>Names pix_timing_sql binds itself; callers may not shadow them.</summary>
    public static readonly string[] PreboundNames = ["$start", "$end", "$reliableStart", "$reliableEnd", "$captureEnd", "$targetPid"];

    private static readonly string[] Notes =
    [
        "Timestamps are capture-clock nanoseconds; $start/$end are the selected window.",
        "Threads.Id, not the OS thread id, is what execution, marker and submission tables reference; ProcThreadId packs pid << 32 | tid.",
        "Constrain virtual tables (ContextSwitch, PixCpuExecution, PixCounters) by timestamp, core or thread so PixStorage can push the filter down.",
        "Nested PIX executions overlap their parents: sum siblings, never ancestors.",
    ];

    [McpServerTool(Name = "pix_timing_sql", Title = "Query timing capture with SQL", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Runs one read-only SQL statement, or a named library query, against the timing capture's PixStorage SQLite database off the PIX worker and returns positional rows. Call pix_timing_schema first for tables, documented joins, named queries and the pre-bound parameters ($start, $end, $reliableStart, $reliableEnd, $captureEnd, $targetPid). Writes, PRAGMA, ATTACH and file functions are refused (sql_forbidden). Page with offset; pages are stable only with ORDER BY.")]
    public static Task<string> Sql(
        PixSession session,
        JobManager jobs,
        [Description("Timing capture handle (from pix_timing_open).")] string handle,
        [Description("One read-only statement (SELECT, WITH ... SELECT, VALUES). Exactly one of sql or query.")] string? sql = null,
        [Description("Name of a library query listed by pix_timing_schema (namedQueries). Exactly one of sql or query.")] string? query = null,
        [Description(ParamsDescription)] Dictionary<string, JsonElement>? @params = null,
        [Description(RangeModeDescription)] string rangeMode = TimingDatabase.RangeModeFull,
        [Description("Window start in capture nanoseconds (decimal string); binds $start. Default: the first reliable timestamp.")] string? startNs = null,
        [Description("Window end in capture nanoseconds (decimal string, exclusive); binds $end. Default: per rangeMode.")] string? endNs = null,
        [Description("Maximum rows (default 200, max 5000).")] int maxRows = SqlRequest.DefaultMaxRows,
        [Description("Maximum serialized row bytes (default 131072, 4096 to 1048576).")] int maxBytes = SqlRequest.DefaultMaxBytes,
        [Description("Text cells longer than this end with an ellipsis (default 256, 16 to 65536).")] int maxStringLength = SqlRequest.DefaultMaxStringLength,
        [Description("Rows to skip (default 0). Stable only when the statement has ORDER BY.")] int offset = 0,
        [Description("Also count every row the statement produces (default false; runs it a second time).")] bool countTotal = false,
        [Description("Also return EXPLAIN QUERY PLAN rows (default false).")] bool explain = false,
        [Description("Return base64 of the first 4096 bytes of BLOB cells instead of only their size (default false).")] bool includeBlobs = false,
        [Description("Statement budget in seconds (default 30, max 120); sql_timeout when exceeded.")] double timeoutSeconds = SqlRequest.DefaultTimeoutSeconds,
        [Description(TimingQueryTools.WaitDescription)] double waitSeconds = 2,
        [Description(Tools.IncludeProvenanceDescription)] bool includeProvenance = false,
        CancellationToken cancellationToken = default)
    {
        if ((sql is null) == (query is null))
            throw PixErrors.InvalidArguments("Pass exactly one of sql or query.", [SchemaCall(handle)]);
        if (!double.IsFinite(timeoutSeconds) || timeoutSeconds <= 0 || timeoutSeconds > 120)
            throw PixErrors.InvalidArguments("timeoutSeconds must be greater than 0 and at most 120.");
        string mode = TimingDatabase.NormalizeRangeMode(rangeMode);
        long? start = TimingDatabase.ParseNs(startNs, nameof(startNs)), end = TimingDatabase.ParseNs(endNs, nameof(endNs));
        NamedTimingQuery? named = null;
        if (query is not null)
            named = TimingQueryLibrary.Find(query) ?? throw PixErrors.InvalidArguments(
                $"Unknown named query '{query}'. Queries: {string.Join(", ", TimingQueryLibrary.All.Select(q => q.Name))}.", [SchemaCall(handle)]);
        IReadOnlyDictionary<string, JsonElement>? parameters = named is null ? @params : named.BindParams(@params);
        var request = new SqlRequest(named?.Sql ?? sql!)
        {
            Params = parameters, Prebound = PreboundNames.ToDictionary(n => n, _ => (object?)null), MaxRows = maxRows, MaxBytes = maxBytes,
            MaxStringLength = maxStringLength, Offset = offset, CountTotal = countTotal, Explain = explain, IncludeBlobs = includeBlobs, TimeoutSeconds = timeoutSeconds,
        };
        SqlQuery.Validate(request);
        object continuation = new { handle, sql, query, @params, rangeMode = mode, startNs, endNs, maxRows, maxBytes, maxStringLength, countTotal, explain, includeBlobs, timeoutSeconds };
        return TimingQueryTools.Query(session, jobs, "pix_timing_sql", handle,
            new { query = "sql", statement = request.Sql, parameters, mode, start, end, maxRows, maxBytes, maxStringLength, offset, countTotal, explain, includeBlobs, timeoutSeconds },
            database => Run(database, handle, request, named, start, end, mode, continuation), waitSeconds, cancellationToken);
    }

    [McpServerTool(Name = "pix_timing_schema", Title = "Timing capture SQL schema", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Describes the timing capture's PixStorage SQLite schema for pix_timing_sql: tables and PixStorage virtual tables with columns (hidden constraint columns marked), documented units, joins and caveats, custom functions, capture facts, capability probes, the pre-bound parameters and the named query library. Pass table for its DDL, indexes, joins and optional sample rows, or query for a named query's SQL. Runs off the PIX worker.")]
    public static Task<string> Schema(
        PixSession session,
        JobManager jobs,
        [Description("Timing capture handle (from pix_timing_open).")] string handle,
        [Description("Describe this table, view or virtual table in detail (case-insensitive).")] string? table = null,
        [Description("Only list objects whose name contains this text (case-insensitive).")] string? nameContains = null,
        [Description("Include the SQL of this named query.")] string? query = null,
        [Description("List each object's columns (default true).")] bool includeColumns = true,
        [Description("Count rows per listed object with a 2-second budget each (default false; virtual tables can be slow).")] bool includeRowCounts = false,
        [Description("With table: include three sample rows (default false).")] bool includeSampleRows = false,
        [Description("List the named query library with each query's requirements state (default true).")] bool includeQueries = true,
        [Description(RangeModeDescription)] string rangeMode = TimingDatabase.RangeModeFull,
        [Description("First object to list (default 0).")] int offset = 0,
        [Description("Maximum objects to list (default 25, max 1000). With table, the listing is that object alone.")] int limit = Paging.DefaultLimit,
        [Description(TimingQueryTools.WaitDescription)] double waitSeconds = 2,
        [Description(Tools.IncludeProvenanceDescription)] bool includeProvenance = false,
        CancellationToken cancellationToken = default)
    {
        TimingDatabase.ValidatePage(offset, limit);
        string mode = TimingDatabase.NormalizeRangeMode(rangeMode);
        var options = new TimingSchemaOptions(table, nameContains, query, includeColumns, includeRowCounts, includeSampleRows, includeQueries, offset, limit);
        return TimingQueryTools.Query(session, jobs, "pix_timing_schema", handle,
            new { query = "schema", table, nameContains, namedQuery = query, includeColumns, includeRowCounts, includeSampleRows, includeQueries, mode, offset, limit },
            database => database.Schema(handle, options, database.SqlBindings(null, null, mode), TimingQueryLibrary.All), waitSeconds, cancellationToken);
    }

    internal static SqlResultDto Run(TimingDatabase database, string handle, SqlRequest request, NamedTimingQuery? named, long? start, long? end, string mode, object continuation)
    {
        if (named is not null && !database.RequirementsMet(named.Requires))
            throw new PixToolException(PixErrors.Codes.TimingSchemaUnsupported,
                $"This capture lacks what named query '{named.Name}' needs ({string.Join(", ", named.Requires)}).", nextCalls: [SchemaCall(handle)]);
        TimingSqlBindings bindings = database.SqlBindings(start, end, mode);
        SqlResultDto result;
        try
        {
            result = SqlQuery.Execute(database, request with { Prebound = bindings.Values }, "pixstorage", handle);
        }
        catch (PixToolException ex) when (ex.Detail.Code.StartsWith("sql_", StringComparison.Ordinal) && ex.Detail.NextCalls.Count == 0)
        {
            throw new PixToolException(ex.Detail with { NextCalls = ErrorCalls(handle, ex, continuation) });
        }
        var notes = new List<string>(Notes);
        if (named is not null) notes.AddRange(named.Caveats);
        notes.AddRange(result.Notes);
        var calls = new List<ToolCallDto>();
        if (result.NextOffset is int next)
            calls.Add(new ToolCallDto("pix_timing_sql", Merge(continuation, "offset", next), CostHints.Query));
        if (result.ReferencedTables.FirstOrDefault(t => PixStorageDocs.For(t) is not null) is string documented)
            calls.Add(new ToolCallDto("pix_timing_schema", new { handle, table = documented, includeColumns = true }, CostHints.Query));
        return result with
        {
            Columns = Document(result.Columns, result.ReferencedTables),
            Notes = notes,
            NextCalls = calls,
            Provenance = bindings.Range,
            Query = named?.Name,
        };
    }

    /// <summary>Column units and descriptions from the overlay when exactly one referenced table documents a column of that name.</summary>
    private static IReadOnlyList<SqlColumnDto> Document(IReadOnlyList<SqlColumnDto> columns, IReadOnlyList<string> tables)
    {
        PixStorageTableDoc[] docs = tables.Select(PixStorageDocs.For).OfType<PixStorageTableDoc>().ToArray();
        return columns.Select(column =>
        {
            PixStorageColumnDoc[] matches = docs.Select(d => d.Columns.GetValueOrDefault(column.Name)).OfType<PixStorageColumnDoc>().Distinct().ToArray();
            return matches.Length == 1 ? column with { Unit = matches[0].Unit, Description = matches[0].Description } : column;
        }).ToArray();
    }

    private static IReadOnlyList<ToolCallDto> ErrorCalls(string handle, PixToolException error, object continuation)
    {
        var calls = new List<ToolCallDto> { SchemaCall(handle) };
        if (error.Detail.Code == PixErrors.Codes.SqlMissingParameter && error.Data["missing"] is string[] missing)
        {
            JsonElement existing = JsonSerializer.SerializeToElement(continuation, Json.Options);
            var skeleton = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (existing.TryGetProperty("params", out JsonElement supplied) && supplied.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty property in supplied.EnumerateObject()) skeleton[property.Name] = property.Value;
            foreach (string name in missing) skeleton[name.TrimStart('$', '@', ':')] = null;
            calls.Add(new ToolCallDto("pix_timing_sql", Merge(continuation, "params", skeleton), CostHints.Query));
        }
        return calls;
    }

    private static ToolCallDto SchemaCall(string handle) => new("pix_timing_schema", new { handle, includeColumns = false }, CostHints.Query);

    private static Dictionary<string, object?> Merge(object arguments, string name, object? value)
    {
        var merged = JsonSerializer.SerializeToElement(arguments, Json.Options).EnumerateObject()
            .Where(p => p.Value.ValueKind != JsonValueKind.Null)
            .ToDictionary(p => p.Name, p => (object?)p.Value, StringComparer.Ordinal);
        merged[name] = value;
        return merged;
    }
}
