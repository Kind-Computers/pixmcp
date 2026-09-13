using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using SQLitePCL;

namespace PixMcp.Pix.Sql;

/// <summary>
/// Runs one caller-supplied read-only statement on a <see cref="ReadOnlySqlite"/> connection under
/// <see cref="SqlStatementGuard"/>: exactly one statement, SQLite's own read-only verdict, named parameters only,
/// a statement budget, and rows shaped for JSON (big integers and non-finite doubles as strings, long text and BLOBs
/// shortened) with an exact continuation. Runs on the caller's thread; never on the PIX worker.
/// </summary>
internal static class SqlQuery
{
    private const long MaxSafeInteger = 9007199254740991;
    private const string LimitParameter = "$__pixmcp_limit", OffsetParameter = "$__pixmcp_offset";

    public static SqlResultDto Execute(ReadOnlySqlite db, SqlRequest request, string source, string? handle = null)
    {
        Validate(request);
        return db.WithBudget(request.TimeoutSeconds, PixErrors.Codes.SqlTimeout, PixErrors.Codes.SqlInterrupted, "SQL statement",
            () => Run(db, request, source, handle));
    }

    public static void Validate(SqlRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Sql)) throw PixErrors.InvalidArguments("sql is required.");
        if (request.Sql.Length > SqlRequest.MaxSqlLength) throw PixErrors.InvalidArguments($"sql is {request.Sql.Length} characters; the limit is {SqlRequest.MaxSqlLength}.");
        if (request.MaxRows is < 1 or > SqlRequest.MaxMaxRows) throw PixErrors.InvalidArguments($"maxRows must be between 1 and {SqlRequest.MaxMaxRows}.");
        if (request.MaxBytes is < SqlRequest.MinMaxBytes or > SqlRequest.MaxMaxBytes) throw PixErrors.InvalidArguments($"maxBytes must be between {SqlRequest.MinMaxBytes} and {SqlRequest.MaxMaxBytes}.");
        if (request.MaxStringLength is < SqlRequest.MinMaxStringLength or > SqlRequest.MaxMaxStringLength) throw PixErrors.InvalidArguments($"maxStringLength must be between {SqlRequest.MinMaxStringLength} and {SqlRequest.MaxMaxStringLength}.");
        if (request.Offset < 0) throw PixErrors.InvalidArguments("offset must be nonnegative.");
        if (!double.IsFinite(request.TimeoutSeconds) || request.TimeoutSeconds <= 0 || request.TimeoutSeconds > 600) throw PixErrors.InvalidArguments("timeoutSeconds must be greater than 0 and at most 600.");
        if (request.Params is { } parameters && request.Prebound is { } prebound)
        {
            string[] shadowed = parameters.Keys.Select(Bare).Where(key => prebound.Keys.Any(p => Bare(p) == key)).ToArray();
            if (shadowed.Length > 0)
                throw new PixToolException(PixErrors.Codes.SqlInvalidParameter, $"params may not set server-bound parameters: {string.Join(", ", shadowed.Select(n => "$" + n))}. Use another name.");
        }
    }

    private static SqlResultDto Run(ReadOnlySqlite db, SqlRequest request, string source, string? handle)
    {
        var stopwatch = Stopwatch.StartNew();
        db.Check();
        sqlite3 connection = db.Handle;
        using SqlStatementGuard guard = SqlStatementGuard.Install(connection);
        var notes = new List<string>();

        string statementText;
        SqlColumnDto[] columns;
        string[] declared;
        using (sqlite3_stmt statement = Prepare(db, guard, request.Sql, out string? tail))
        {
            if (!IsIgnorable(tail))
                throw new PixToolException(PixErrors.Codes.SqlMultipleStatements, "Only one statement per call: remove the text after the first statement (comments are allowed).");
            if (raw.sqlite3_stmt_readonly(statement) == 0)
                throw new PixToolException(PixErrors.Codes.SqlNotReadOnly, "SQLite reports this statement would modify the database; only read-only statements run.");
            statementText = TrimStatement(tail is null ? request.Sql : request.Sql[..^tail.Length]);
            int count = raw.sqlite3_column_count(statement);
            columns = Enumerable.Range(0, count).Select(i =>
            {
                string? type = raw.sqlite3_column_decltype(statement, i).utf8_to_string();
                return new SqlColumnDto(raw.sqlite3_column_name(statement, i).utf8_to_string() ?? $"column{i}", type, Affinity(type));
            }).ToArray();
            declared = ParameterNames(statement);
        }

        Dictionary<string, (object? Value, string Source)> values = ResolveParameters(declared, request, notes, out List<SqlBoundParameterDto> bound);

        var rows = new List<object?[]>();
        bool hasMore = false;
        string? reason = null;
        int truncatedCells = 0;
        long bytes = 0;
        string wrapped = "SELECT * FROM (\n" + statementText + "\n) LIMIT " + LimitParameter + " OFFSET " + OffsetParameter;
        sqlite3_stmt? paged = TryPrepare(connection, wrapped);
        bool usePaging = paged is not null && raw.sqlite3_column_count(paged) == columns.Length;
        if (!usePaging && paged is not null) { paged.Dispose(); paged = null; }
        using (sqlite3_stmt run = paged ?? Prepare(db, guard, statementText, out _))
        {
            Bind(db, run, values);
            if (usePaging)
            {
                Check(db, connection, raw.sqlite3_bind_int64(run, raw.sqlite3_bind_parameter_index(run, LimitParameter), request.MaxRows + 1L));
                Check(db, connection, raw.sqlite3_bind_int64(run, raw.sqlite3_bind_parameter_index(run, OffsetParameter), request.Offset));
            }
            else
            {
                notes.Add("The statement could not be wrapped for LIMIT/OFFSET, so offset rows were stepped over.");
            }
            long skipped = 0, stepped = 0;
            while (true)
            {
                int rc = raw.sqlite3_step(run);
                if (rc == raw.SQLITE_DONE) break;
                if (rc != raw.SQLITE_ROW) throw StepFailure(db, guard, connection, rc);
                if ((++stepped & 0xFF) == 0) db.Check();
                if (!usePaging && skipped < request.Offset) { skipped++; continue; }
                if (rows.Count == request.MaxRows) { hasMore = true; reason = "maxRows"; break; }
                object?[] row = ReadRow(run, columns.Length, request, ref truncatedCells);
                int size = JsonSerializer.SerializeToUtf8Bytes(row, Json.Options).Length + 1;
                if (rows.Count > 0 && bytes + size > request.MaxBytes) { hasMore = true; reason = "maxBytes"; break; }
                if (rows.Count == 0 && size > request.MaxBytes) notes.Add($"The first row alone is {size} bytes, above maxBytes; it is returned whole.");
                bytes += size;
                rows.Add(row);
            }
        }

        long? total = null;
        if (request.CountTotal)
        {
            using sqlite3_stmt? counter = TryPrepare(connection, "SELECT COUNT(*) FROM (\n" + statementText + "\n)");
            if (counter is null) notes.Add("countTotal is unavailable for this statement form.");
            else
            {
                Bind(db, counter, values);
                int rc = raw.sqlite3_step(counter);
                if (rc != raw.SQLITE_ROW) throw StepFailure(db, guard, connection, rc);
                total = raw.sqlite3_column_int64(counter, 0);
            }
        }

        List<SqlPlanRowDto>? plan = null;
        if (request.Explain)
        {
            using sqlite3_stmt? explain = TryPrepare(connection, "EXPLAIN QUERY PLAN " + statementText);
            if (explain is null) notes.Add("EXPLAIN QUERY PLAN is unavailable for this statement form.");
            else
            {
                Bind(db, explain, values);
                plan = [];
                int rc;
                while ((rc = raw.sqlite3_step(explain)) == raw.SQLITE_ROW)
                    plan.Add(new(raw.sqlite3_column_int64(explain, 0), raw.sqlite3_column_int64(explain, 1), raw.sqlite3_column_text(explain, 3).utf8_to_string() ?? ""));
                if (rc != raw.SQLITE_DONE) throw StepFailure(db, guard, connection, rc);
            }
        }

        db.Check();
        var legend = new TableLegend("positional: each row lists one cell per entry of columns, in order",
            new Dictionary<string, RefRecipe>(), new Dictionary<string, string>());
        return new SqlResultDto(handle, source, statementText, columns, legend, rows, rows.Count, request.Offset, hasMore, reason, total, truncatedCells,
            Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3), guard.ReferencedTables, bound, plan, notes, []);
    }

    private static sqlite3_stmt Prepare(ReadOnlySqlite db, SqlStatementGuard guard, string sql, out string? tail)
    {
        int rc = raw.sqlite3_prepare_v2(db.Handle, sql, out sqlite3_stmt statement, out tail);
        if (rc != raw.SQLITE_OK)
        {
            statement?.Dispose();
            string message = raw.sqlite3_errmsg(db.Handle).utf8_to_string() ?? "unknown error";
            // SQLite refuses unsafe functions such as load_extension itself (SQLITE_ERROR "not authorized to use function: x") before the authorizer runs.
            if (rc == raw.SQLITE_AUTH || message.StartsWith("not authorized", StringComparison.OrdinalIgnoreCase))
                throw new PixToolException(PixErrors.Codes.SqlForbidden, $"Read-only SQL may not use {guard.Denied ?? DeniedFromMessage(message)} ({message}). Only SELECT statements over tables, views and non-file functions run.");
            if (rc == raw.SQLITE_INTERRUPT) throw db.Interrupted(message);
            throw new PixToolException(PixErrors.Codes.SqlSyntaxError, "SQLite could not prepare the statement: " + message);
        }
        if (statement is null || statement.IsInvalid)
        {
            statement?.Dispose();
            throw new PixToolException(PixErrors.Codes.SqlSyntaxError, "The SQL contains no statement.");
        }
        return statement;
    }

    /// <summary>Prepares a server-built variant (paging, count, plan); null when SQLite rejects that form.</summary>
    private static sqlite3_stmt? TryPrepare(sqlite3 connection, string sql)
    {
        int rc = raw.sqlite3_prepare_v2(connection, sql, out sqlite3_stmt statement);
        if (rc == raw.SQLITE_OK && statement is not null && !statement.IsInvalid) return statement;
        statement?.Dispose();
        return null;
    }

    private static PixToolException StepFailure(ReadOnlySqlite db, SqlStatementGuard guard, sqlite3 connection, int rc)
    {
        string message = raw.sqlite3_errmsg(connection).utf8_to_string() ?? "unknown error";
        if (rc == raw.SQLITE_INTERRUPT) return db.Interrupted(message);
        db.Check();
        if (rc == raw.SQLITE_AUTH)
            return new PixToolException(PixErrors.Codes.SqlForbidden, $"Read-only SQL may not use {guard.Denied ?? "this construct"} ({message}).");
        return new PixToolException(PixErrors.Codes.SqlExecutionError, "SQLite failed while running the statement: " + message);
    }

    private static void Check(ReadOnlySqlite db, sqlite3 connection, int rc)
    {
        if (rc != raw.SQLITE_OK)
            throw new PixToolException(PixErrors.Codes.SqlExecutionError, "SQLite could not bind a parameter: " + raw.sqlite3_errmsg(connection).utf8_to_string());
    }

    private static string[] ParameterNames(sqlite3_stmt statement)
    {
        int count = raw.sqlite3_bind_parameter_count(statement);
        var names = new string[count];
        for (int i = 1; i <= count; i++)
        {
            string? name = raw.sqlite3_bind_parameter_name(statement, i).utf8_to_string();
            if (string.IsNullOrEmpty(name) || name[0] == '?')
                throw new PixToolException(PixErrors.Codes.SqlInvalidParameter, "Positional ? parameters are not supported; name them ($name) and pass params { \"name\": value }.");
            names[i - 1] = name;
        }
        return names;
    }

    private static Dictionary<string, (object? Value, string Source)> ResolveParameters(string[] declared, SqlRequest request, List<string> notes, out List<SqlBoundParameterDto> bound)
    {
        var values = new Dictionary<string, (object?, string)>(StringComparer.Ordinal);
        var missing = new List<string>();
        bound = [];
        foreach (string name in declared)
        {
            string key = Bare(name);
            if (request.Prebound?.FirstOrDefault(p => Bare(p.Key) == key) is { Key: not null } server)
            {
                object? value = Scalar(server.Value);
                values[key] = (value, "prebound");
                bound.Add(new(name, value, "prebound"));
            }
            else if (request.Params?.FirstOrDefault(p => Bare(p.Key) == key) is { Key: not null } supplied)
            {
                object? value = FromJson(key, supplied.Value);
                values[key] = (value, "params");
                bound.Add(new(name, value, "params"));
            }
            else missing.Add(name);
        }
        if (missing.Count > 0)
            throw WithMissing(missing, new PixToolException(PixErrors.Codes.SqlMissingParameter,
                $"The statement declares {string.Join(", ", missing)} but params does not supply {(missing.Count == 1 ? "it" : "them")}. Pass params {{ {string.Join(", ", missing.Select(m => "\"" + Bare(m) + "\": value"))} }}."));
        string[] unused = (request.Params?.Keys ?? []).Where(k => !declared.Any(d => Bare(d) == Bare(k))).ToArray();
        if (unused.Length > 0) notes.Add("params not used by the statement: " + string.Join(", ", unused));
        return values;
    }

    /// <summary>Carries the missing parameter names (Exception.Data["missing"]) so tools can offer a params skeleton.</summary>
    private static PixToolException WithMissing(List<string> missing, PixToolException error)
    {
        error.Data["missing"] = missing.ToArray();
        return error;
    }

    private static void Bind(ReadOnlySqlite db, sqlite3_stmt statement, Dictionary<string, (object? Value, string Source)> values)
    {
        sqlite3 connection = db.Handle;
        int count = raw.sqlite3_bind_parameter_count(statement);
        for (int i = 1; i <= count; i++)
        {
            string? name = raw.sqlite3_bind_parameter_name(statement, i).utf8_to_string();
            if (name is null || name is LimitParameter or OffsetParameter) continue;
            if (!values.TryGetValue(Bare(name), out (object? Value, string Source) entry)) continue;
            int rc = entry.Value switch
            {
                null => raw.sqlite3_bind_null(statement, i),
                long l => raw.sqlite3_bind_int64(statement, i, l),
                double d => raw.sqlite3_bind_double(statement, i, d),
                string s => raw.sqlite3_bind_text(statement, i, s),
                _ => raw.sqlite3_bind_text(statement, i, Convert.ToString(entry.Value, CultureInfo.InvariantCulture) ?? ""),
            };
            Check(db, connection, rc);
        }
    }

    private static object? FromJson(string name, JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.True => 1L,
        JsonValueKind.False => 0L,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt64(out long integer) ? (object)integer : value.GetDouble(),
        _ => throw new PixToolException(PixErrors.Codes.SqlInvalidParameter,
            $"params.{name} is a JSON {value.ValueKind.ToString().ToLowerInvariant()}; bind scalars only. Pass the array or object as JSON text and read it with json_each(${name}) or json_extract(${name}, '$.field')."),
    };

    private static object? Scalar(object? value) => value switch
    {
        null => null,
        bool b => b ? 1L : 0L,
        int i => (long)i,
        uint u => (long)u,
        long l => l,
        ulong ul => ul <= long.MaxValue ? (long)ul : (double)ul,
        float f => (double)f,
        double d => d,
        string s => s,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private static object?[] ReadRow(sqlite3_stmt statement, int columns, SqlRequest request, ref int truncatedCells)
    {
        var row = new object?[columns];
        for (int i = 0; i < columns; i++)
        {
            switch (raw.sqlite3_column_type(statement, i))
            {
                case raw.SQLITE_INTEGER:
                    long integer = raw.sqlite3_column_int64(statement, i);
                    row[i] = integer is > MaxSafeInteger or < -MaxSafeInteger ? integer.ToString(CultureInfo.InvariantCulture) : integer;
                    break;
                case raw.SQLITE_FLOAT:
                    double real = raw.sqlite3_column_double(statement, i);
                    row[i] = double.IsFinite(real) ? real : real.ToString(CultureInfo.InvariantCulture);
                    break;
                case raw.SQLITE_TEXT:
                    string text = raw.sqlite3_column_text(statement, i).utf8_to_string() ?? "";
                    if (text.Length > request.MaxStringLength) { text = text[..(request.MaxStringLength - 1)] + "…"; truncatedCells++; }
                    row[i] = text;
                    break;
                case raw.SQLITE_BLOB:
                    ReadOnlySpan<byte> blob = raw.sqlite3_column_blob(statement, i);
                    if (request.IncludeBlobs)
                    {
                        bool cut = blob.Length > SqlRequest.BlobPreviewBytes;
                        if (cut) truncatedCells++;
                        row[i] = new Dictionary<string, object> { ["blobBytes"] = blob.Length, ["base64"] = Convert.ToBase64String(cut ? blob[..SqlRequest.BlobPreviewBytes] : blob), ["truncated"] = cut };
                    }
                    else row[i] = new Dictionary<string, object> { ["blobBytes"] = blob.Length };
                    break;
                default:
                    row[i] = null;
                    break;
            }
        }
        return row;
    }

    /// <summary>SQLite type-affinity rules applied to a declared type.</summary>
    public static string? Affinity(string? declaredType)
    {
        if (string.IsNullOrWhiteSpace(declaredType)) return null;
        string type = declaredType.ToUpperInvariant();
        if (type.Contains("INT", StringComparison.Ordinal)) return "INTEGER";
        if (type.Contains("CHAR", StringComparison.Ordinal) || type.Contains("CLOB", StringComparison.Ordinal) || type.Contains("TEXT", StringComparison.Ordinal)) return "TEXT";
        if (type.Contains("BLOB", StringComparison.Ordinal)) return "BLOB";
        if (type.Contains("REAL", StringComparison.Ordinal) || type.Contains("FLOA", StringComparison.Ordinal) || type.Contains("DOUB", StringComparison.Ordinal)) return "REAL";
        return "NUMERIC";
    }

    /// <summary>True when nothing but whitespace, semicolons and comments follows the first statement.</summary>
    public static bool IsIgnorable(string? tail)
    {
        if (string.IsNullOrEmpty(tail)) return true;
        int i = 0;
        while (i < tail.Length)
        {
            char c = tail[i];
            if (char.IsWhiteSpace(c) || c == ';') { i++; continue; }
            if (c == '-' && i + 1 < tail.Length && tail[i + 1] == '-')
            {
                int end = tail.IndexOf('\n', i);
                i = end < 0 ? tail.Length : end + 1;
                continue;
            }
            if (c == '/' && i + 1 < tail.Length && tail[i + 1] == '*')
            {
                int end = tail.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? tail.Length : end + 2;
                continue;
            }
            return false;
        }
        return true;
    }

    /// <summary>The statement without trailing whitespace and semicolons, so it can be embedded in a subquery.</summary>
    public static string TrimStatement(string sql)
    {
        string text = sql.TrimEnd();
        while (text.EndsWith(';')) text = text[..^1].TrimEnd();
        return text;
    }

    private static string DeniedFromMessage(string message)
    {
        const string function = "not authorized to use function: ";
        return message.StartsWith(function, StringComparison.OrdinalIgnoreCase) ? "FUNCTION " + message[function.Length..].Trim() : "this construct";
    }

    private static string Bare(string name) => name.Length > 0 && name[0] is '$' or '@' or ':' ? name[1..] : name;
}
