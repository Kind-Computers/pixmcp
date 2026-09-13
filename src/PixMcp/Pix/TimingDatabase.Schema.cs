using System.Globalization;
using Microsoft.Data.Sqlite;
using PixMcp.Pix.Sql;

namespace PixMcp.Pix;

/// <summary>What pix_timing_schema returns; one instance per call.</summary>
internal sealed record TimingSchemaOptions(string? Table = null, string? NameContains = null, string? Query = null, bool IncludeColumns = true,
    bool IncludeRowCounts = false, bool IncludeSampleRows = false, bool IncludeQueries = true, int Offset = 0, int Limit = Paging.DefaultLimit);

/// <summary>Values pix_timing_sql pre-binds and the window they describe.</summary>
internal sealed record TimingSqlBindings(IReadOnlyDictionary<string, object?> Values, TimingRangeDto Range, IReadOnlyList<SqlBoundParameterDto> Parameters);

internal sealed partial class TimingDatabase
{
    public const string RangeModeFull = "full", RangeModeReliable = "reliable";
    public static readonly string[] RangeModes = [RangeModeFull, RangeModeReliable];
    private const double RowCountBudgetSeconds = 2;

    /// <summary>Modules and functions every e_sqlite3 connection has; anything else on a capture connection came from PixStorage.</summary>
    private static readonly Lazy<(HashSet<string> Modules, HashSet<string> Functions)> Stock = new(() =>
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using SqliteCommand modules = connection.CreateCommand();
        modules.CommandText = "SELECT name FROM pragma_module_list";
        var moduleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (SqliteDataReader reader = modules.ExecuteReader()) while (reader.Read()) moduleNames.Add(reader.GetString(0));
        using SqliteCommand functions = connection.CreateCommand();
        functions.CommandText = "SELECT name, narg FROM pragma_function_list";
        var functionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (SqliteDataReader reader = functions.ExecuteReader()) while (reader.Read()) functionNames.Add(reader.GetString(0) + "/" + reader.GetInt64(1));
        return (moduleNames, functionNames);
    });

    internal static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    internal static string NormalizeRangeMode(string? rangeMode)
    {
        string mode = string.IsNullOrWhiteSpace(rangeMode) ? RangeModeFull : rangeMode.Trim().ToLowerInvariant();
        return RangeModes.Contains(mode) ? mode : throw PixErrors.InvalidArguments($"rangeMode must be one of {string.Join(", ", RangeModes)} (got '{rangeMode}').");
    }

    /// <summary>
    /// $reliableStart (fact 2), $reliableEnd (fact 24), $captureEnd (fact 3), $targetPid (fact 4) and the selected
    /// [$start, $end) window: full ends at the capture end, reliable at the stop timestamp; explicit bounds win.
    /// </summary>
    internal TimingSqlBindings SqlBindings(long? start, long? end, string rangeMode)
    {
        Require("CaptureFacts", "Id", "Value");
        Dictionary<long, long> facts = Rows("SELECT Id,Value FROM CaptureFacts WHERE Id IN (2,3,4,24)", r => (r.GetInt64(0), r.GetInt64(1))).ToDictionary(x => x.Item1, x => x.Item2);
        if (!facts.TryGetValue(2, out long reliableStart))
            throw new PixToolException(PixErrors.Codes.TimingRangeUnavailable, "The capture has no first-reliable timestamp (CaptureFacts 2).");
        long? reliableEnd = facts.TryGetValue(24, out long stop) && stop > reliableStart ? stop : null;
        long? captureEnd = facts.TryGetValue(3, out long last) && last > reliableStart ? last : null;
        long defaultEnd = (rangeMode == RangeModeReliable ? reliableEnd ?? captureEnd : captureEnd ?? reliableEnd)
            ?? throw new PixToolException(PixErrors.Codes.TimingRangeUnavailable, "The capture has neither a stop (CaptureFacts 24) nor an end (CaptureFacts 3) timestamp.");
        long a = start ?? reliableStart, b = end ?? defaultEnd;
        if (a < 0 || b <= a) throw PixErrors.InvalidArguments("The selected time range requires 0 <= startNs < endNs.");
        var values = new Dictionary<string, object?>(StringComparer.Ordinal) { ["$reliableStart"] = reliableStart, ["$start"] = a, ["$end"] = b };
        if (reliableEnd is long re) values["$reliableEnd"] = re;
        if (captureEnd is long ce) values["$captureEnd"] = ce;
        if (facts.TryGetValue(4, out long pid)) values["$targetPid"] = pid;
        var parameters = values.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => new SqlBoundParameterDto(v.Key, v.Value, "prebound")).ToArray();
        var range = new TimingRangeDto(Ns(a), Ns(b), Ns(reliableStart), Ns(reliableEnd ?? b)) { RangeMode = rangeMode, CaptureEndNs = captureEnd is long end2 ? Ns(end2) : null };
        return new TimingSqlBindings(values, range, parameters);
    }

    internal TimingSchemaDto Schema(string handle, TimingSchemaOptions options, TimingSqlBindings bindings, IReadOnlyList<NamedTimingQuery> library)
        => Guard(() =>
        {
            var (stockModules, stockFunctions) = Stock.Value;
            List<string> virtualTables = Rows("SELECT name FROM pragma_module_list", r => r.GetString(0))
                .Where(m => !stockModules.Contains(m)).OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
            var master = Rows("SELECT type, name, sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' OR type = 'index' ORDER BY name",
                r => (Type: r.GetString(0), Name: r.GetString(1), Sql: r.IsDBNull(2) ? null : r.GetString(2)));
            var objects = virtualTables.Select(v => (Name: v, Kind: "virtual", Sql: (string?)null))
                .Concat(master.Where(m => m.Type == "view").Select(m => (m.Name, Kind: "view", m.Sql)))
                .Concat(master.Where(m => m.Type == "table").Select(m => (m.Name, Kind: "table", m.Sql)))
                .ToList();
            var functions = Rows("SELECT DISTINCT name, narg FROM pragma_function_list", r => (Name: r.GetString(0), Arguments: (int)r.GetInt64(1)))
                .Where(f => !stockFunctions.Contains(f.Name + "/" + f.Arguments))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => new TimingSchemaFunctionDto(f.Name, f.Arguments, PixStorageDocs.Function(f.Name, f.Arguments))).ToArray();
            var census = new TimingSchemaCensusDto(objects.Count(o => o.Kind == "table"), virtualTables.Count, objects.Count(o => o.Kind == "view"),
                master.Count(m => m.Type == "index"), functions.Length);

            // With table the listing is that object alone; otherwise nameContains filters it.
            var filtered = objects.Where(o => !string.IsNullOrWhiteSpace(options.Table)
                ? o.Name.Equals(options.Table.Trim(), StringComparison.OrdinalIgnoreCase)
                : string.IsNullOrEmpty(options.NameContains) || o.Name.Contains(options.NameContains, StringComparison.OrdinalIgnoreCase)).ToList();
            var page = filtered.Skip(options.Offset).Take(options.Limit)
                .Select(o => Describe(o.Name, o.Kind, options.IncludeColumns, options.IncludeRowCounts)).ToArray();
            PageResult<TimingSchemaTableDto> tables = Paging.Page(page, filtered.Count, options.Offset, options.Limit);

            TimingSchemaTableDetailDto? detail = null;
            if (!string.IsNullOrWhiteSpace(options.Table))
            {
                var match = objects.FirstOrDefault(o => o.Name.Equals(options.Table.Trim(), StringComparison.OrdinalIgnoreCase));
                if (match.Name is null)
                    throw PixErrors.InvalidArguments($"Unknown table '{options.Table}'. List tables with nameContains to find the exact name.",
                        [new ToolCallDto("pix_timing_schema", new { handle, nameContains = options.Table, includeColumns = false }, CostHints.Query)]);
                detail = Detail(handle, match.Name, match.Kind, match.Sql, options.IncludeSampleRows);
            }

            var facts = Has("CaptureFacts", "Id", "Value")
                ? Rows("SELECT Id, Value FROM CaptureFacts ORDER BY Id", r => new TimingCaptureFactDto(r.GetInt64(0), Convert.ToString(r.GetValue(1), CultureInfo.InvariantCulture) ?? "", PixStorageDocs.Fact(r.GetInt64(0)))).ToArray()
                : [];

            IReadOnlyList<TimingNamedQueryDto>? namedQueries = null;
            if (options.IncludeQueries || options.Query is not null)
            {
                if (options.Query is not null && !library.Any(q => q.Name == options.Query))
                    throw PixErrors.InvalidArguments($"Unknown named query '{options.Query}'. Queries: {string.Join(", ", library.Select(q => q.Name))}.");
                namedQueries = library.Where(q => options.Query is null || q.Name == options.Query)
                    .Select(q => q.Describe(RequirementsMet(q.Requires), detailed: q.Name == options.Query)).ToArray();
            }

            var notes = new List<string>(PixStorageDocs.Notes) { $"Documentation observed on {PixStorageDocs.ObservedOn}; row counts and plans vary per capture." };
            return new TimingSchemaDto(handle, SqliteVersion, census, tables, detail, functions, facts, bindings.Parameters,
                Capabilities(objects.Select(o => o.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)), namedQueries, bindings.Range, notes,
                SchemaNextCalls(handle, virtualTables, library));
        });

    private TimingSchemaTableDto Describe(string name, string kind, bool includeColumns, bool includeRowCounts)
    {
        PixStorageTableDoc? doc = PixStorageDocs.For(name);
        IReadOnlyList<TimingSchemaColumnDto>? columns = includeColumns ? Columns(name, doc) : null;
        long? rows = null;
        string state = "notRequested";
        if (includeRowCounts)
        {
            try
            {
                rows = WithBudget(RowCountBudgetSeconds, PixErrors.Codes.SqlTimeout, PixErrors.Codes.SqlInterrupted, "row count", () => Count(Quote(name)));
                state = "exact";
            }
            catch (PixToolException ex) when (ex.Detail.Code == PixErrors.Codes.SqlTimeout) { state = "timedOut"; }
        }
        return new TimingSchemaTableDto(name, kind, doc is not null, doc?.BackingTable, rows, state, doc?.Purpose, columns);
    }

    private IReadOnlyList<TimingSchemaColumnDto> Columns(string name, PixStorageTableDoc? doc)
        => Rows("PRAGMA table_xinfo(" + Quote(name) + ")", r =>
        {
            string column = r.GetString(1);
            bool hidden = !r.IsDBNull(6) && r.GetInt64(6) == 1;
            PixStorageColumnDoc? columnDoc = doc?.Columns.GetValueOrDefault(column);
            return new TimingSchemaColumnDto(column, r.IsDBNull(2) || r.GetString(2).Length == 0 ? null : r.GetString(2), r.GetInt64(5) > 0, r.GetInt64(3) != 0, hidden,
                hidden ? "constraintInput" : null, columnDoc?.Unit, columnDoc?.Description);
        });

    private TimingSchemaTableDetailDto Detail(string handle, string name, string kind, string? ddl, bool includeSampleRows)
    {
        PixStorageTableDoc? doc = PixStorageDocs.For(name);
        var indexes = kind == "table"
            ? Rows("PRAGMA index_list(" + Quote(name) + ")", r => (Name: r.GetString(1), Unique: r.GetInt64(2) != 0)).ToArray()
                .Select(i => new TimingSchemaIndexDto(i.Name, i.Unique, Rows("PRAGMA index_info(" + Quote(i.Name) + ")", r => r.IsDBNull(2) ? "(expression)" : r.GetString(2)))).ToArray()
            : [];
        var foreignKeys = kind == "table"
            ? Rows("PRAGMA foreign_key_list(" + Quote(name) + ")", r => new TimingSchemaForeignKeyDto(r.GetString(3), r.GetString(2), r.IsDBNull(4) ? null : r.GetString(4))).ToArray()
            : [];
        SqlResultDto? sample = includeSampleRows
            ? SqlQuery.Execute(this, new SqlRequest("SELECT * FROM " + Quote(name) + " LIMIT 3") { MaxRows = 3, MaxBytes = 16384, MaxStringLength = 128, TimeoutSeconds = 5 }, "pixstorage", handle)
            : null;
        string? text = ddl ?? (kind == "virtual" ? $"-- eponymous virtual table provided by the PixStorage extension{(doc?.BackingTable is string backing ? "; rows decode " + backing : "")}" : null);
        return new TimingSchemaTableDetailDto(Describe(name, kind, includeColumns: true, includeRowCounts: false), text, indexes, foreignKeys,
            doc?.Joins ?? [], doc?.Caveats ?? [], sample);
    }

    /// <summary>"Table:Column" pairs (or bare table names) that must exist for a library query to prepare.</summary>
    internal bool RequirementsMet(IReadOnlyList<string> requires)
        => requires.All(requirement =>
        {
            string[] parts = requirement.Split(':', 2);
            return parts.Length == 1 ? Has(parts[0]) || HasVirtual(parts[0]) : Has(parts[0], parts[1]);
        });

    private bool HasVirtual(string module)
        => Count("pragma_module_list", " WHERE name = $name COLLATE NOCASE", ("$name", module)) > 0 && !Stock.Value.Modules.Contains(module);

    private IReadOnlyDictionary<string, TimingCapabilityDto> Capabilities(HashSet<string> objects)
    {
        TimingCapabilityDto Probe(string table, string? where = null, string? emptyReason = null)
        {
            if (!objects.Contains(table)) return new("unsupported", $"{table} is absent from this capture.");
            bool any = Rows("SELECT EXISTS(SELECT 1 FROM " + Quote(table) + (where is null ? "" : " WHERE " + where) + ")", r => r.GetInt64(0) == 1)[0];
            return any ? new("available") : new("empty", emptyReason ?? $"{table} has no rows in this capture.");
        }
        var capabilities = new Dictionary<string, TimingCapabilityDto>(StringComparer.Ordinal)
        {
            ["cpuEvents"] = Probe("PixCpuExecution"),
            ["cpuMarkers"] = Probe("PixCpuMarker"),
            ["gpuMarkers"] = Probe("PixGpuExecution", emptyReason: "No GPU-side PIX markers were recorded (PixGpuExecution is empty); GPU work is in ApiQueueExecution and GpuWorkRange."),
            ["gpuSubmissions"] = Probe("ApiQueueExecution"),
            ["gpuHardware"] = Probe("GpuWorkRange"),
            ["contextSwitches"] = Probe("ContextSwitch"),
            ["readyThread"] = Probe("ReadyThread"),
            ["samples"] = Probe("CpuSample"),
            ["counters"] = Probe("PixCounters"),
            ["fileIo"] = Probe("FileIORange"),
            ["memory"] = Probe("MemoryEventRanges"),
            ["presentFrames"] = objects.Contains("GpuFrame") && Has("GpuFrame", "PresentCallTime")
                ? Probe("GpuFrame", "PresentCallTime IS NOT NULL", "GpuFrame has no presented frames (API-taken timing captures leave it empty).") : new("unsupported", "GpuFrame is absent."),
            ["symbols"] = Probe("FunctionInformation", emptyReason: "No symbols are resolved; run pix_timing_resolve_symbols."),
        };
        if (objects.Contains("CustomMarker") && Has("CustomMarkerInfo", "Id", "NameId") && Has("Strings", "Id", "Value"))
            capabilities["vsync"] = Probe("CustomMarker", "MarkerInfoId IN (SELECT i.Id FROM CustomMarkerInfo i JOIN Strings s ON s.Id = i.NameId WHERE s.Value LIKE '%VSync%')",
                "No VSync custom markers were recorded.");
        else capabilities["vsync"] = new("unsupported", "CustomMarker/CustomMarkerInfo are absent.");
        if (Has("PhysicalCores", "Id", "EfficiencyClass"))
        {
            long classes = Rows("SELECT COUNT(DISTINCT EfficiencyClass) FROM PhysicalCores", r => r.GetInt64(0))[0];
            capabilities["heterogeneousCores"] = classes > 1 ? new("available") : new("empty", "Every physical core reports the same EfficiencyClass.");
        }
        else capabilities["heterogeneousCores"] = new("unsupported", "PhysicalCores.EfficiencyClass is absent.");
        return capabilities;
    }

    private IReadOnlyList<ToolCallDto> SchemaNextCalls(string handle, IReadOnlyList<string> virtualTables, IReadOnlyList<NamedTimingQuery> library)
    {
        var calls = new List<ToolCallDto>();
        foreach (string name in new[] { "capture_facts", "gpu_busy_per_queue" })
            if (library.FirstOrDefault(q => q.Name == name) is { } query && RequirementsMet(query.Requires))
                calls.Add(new ToolCallDto("pix_timing_sql", new { handle, query = name }, CostHints.Query));
        if (virtualTables.Contains("ContextSwitch", StringComparer.OrdinalIgnoreCase))
            calls.Add(new ToolCallDto("pix_timing_sql", new { handle, sql = "SELECT COUNT(*) AS switches FROM ContextSwitch WHERE Timestamp >= $start AND Timestamp < $end" }, CostHints.Query));
        calls.Add(new ToolCallDto("pix_timing_schema", new { handle, table = virtualTables.FirstOrDefault() ?? "Threads", includeSampleRows = true }, CostHints.Query));
        return calls;
    }
}
