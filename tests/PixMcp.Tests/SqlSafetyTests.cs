using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PixMcp.Pix;
using PixMcp.Pix.Sql;
using SQLitePCL;
using Xunit;

namespace PixMcp.Tests;

public sealed class SqlSafetyTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("pixmcp-sql-safety-");
    private string Path => System.IO.Path.Combine(_directory.FullName, "fixture.sqlite");

    public SqlSafetyTests()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE Strings(Id INTEGER PRIMARY KEY, Value TEXT);
            CREATE TABLE Events(Id INTEGER PRIMARY KEY, ThreadId INTEGER, BeginTimestamp INTEGER, EndTimestamp INTEGER);
            CREATE TABLE Numbers(Id INTEGER PRIMARY KEY, Big INTEGER, Data BLOB);
            CREATE VIEW NamedEvents AS SELECT e.Id, s.Value AS Name FROM Events e JOIN Strings s ON s.Id = e.Id;
            WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 10)
            INSERT INTO Strings SELECT x, 'name-' || x FROM n;
            WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 10)
            INSERT INTO Events SELECT x, 7, x * 100, x * 100 + 50 FROM n;
            INSERT INTO Numbers VALUES(1, 9007199254740993, zeroblob(5000));
            """;
        command.ExecuteNonQuery();
    }

    public void Dispose() => _directory.Delete(recursive: true);

    private TestDatabase Open(CancellationToken cancellation = default, CancellationToken invalidation = default) => new(Path, cancellation, invalidation);

    private SqlResultDto Run(string sql, Func<SqlRequest, SqlRequest>? configure = null)
    {
        using TestDatabase db = Open();
        return SqlQuery.Execute(db, (configure ?? (r => r))(new SqlRequest(sql)), "fixture");
    }

    private PixToolException Fails(string sql, Func<SqlRequest, SqlRequest>? configure = null)
        => Assert.Throws<PixToolException>(() => Run(sql, configure));

    [Theory]
    [InlineData("INSERT INTO Strings VALUES(99, 'x')", "sql_forbidden")]
    [InlineData("UPDATE Strings SET Value = 'y'", "sql_forbidden")]
    [InlineData("DELETE FROM Strings", "sql_forbidden")]
    [InlineData("CREATE TABLE Extra(x)", "sql_forbidden")]
    [InlineData("DROP TABLE Strings", "sql_forbidden")]
    [InlineData("ATTACH DATABASE 'other.db' AS other", "sql_forbidden")]
    [InlineData("PRAGMA table_info(Strings)", "sql_forbidden")]
    [InlineData("BEGIN", "sql_forbidden")]
    [InlineData("VACUUM", "sql_not_read_only")]
    [InlineData("SELECT load_extension('evil')", "sql_forbidden")]
    [InlineData("SELECT readfile('C:/Windows/win.ini')", "sql_syntax_error")]
    public void ForbiddenStatementsNeverRunAndLeaveTheFileUnchanged(string sql, string codes)
    {
        byte[] before = SHA256.HashData(File.ReadAllBytes(Path));
        PixToolException error = Fails(sql);
        Assert.True(codes.Split('|').Contains(error.Detail.Code), error.Detail.Code + ": " + error.Detail.Message);
        Assert.False(error.Detail.Retryable);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(Path)));
        Assert.False(File.Exists(Path + "-wal"));
        Assert.Equal(10L, Run("SELECT count(*) FROM Strings").Rows[0][0]);
    }

    [Fact]
    public void DeniedActionIsNamed()
    {
        string insert = Fails("INSERT INTO Strings VALUES(99, 'x')").Detail.Message;
        Assert.True(insert.Contains("INSERT Strings", StringComparison.Ordinal), insert);
        string extension = Fails("SELECT load_extension('evil')").Detail.Message;
        Assert.True(extension.Contains("FUNCTION load_extension", StringComparison.Ordinal), extension);
    }

    [Theory]
    [InlineData("SELECT 1; SELECT 2")]
    [InlineData("SELECT 1; DELETE FROM Strings")]
    public void OnlyOneStatementRuns(string sql)
        => Assert.Equal("sql_multiple_statements", Fails(sql).Detail.Code);

    [Theory]
    [InlineData("SELECT 1;")]
    [InlineData("SELECT 1; -- done")]
    [InlineData("SELECT 1 /* inline */ ; /* after */")]
    [InlineData("SELECT 1 -- trailing comment without newline")]
    public void TrailingCommentsAndSemicolonsAreAllowed(string sql)
    {
        SqlResultDto result = Run(sql);
        Assert.Equal(1L, Assert.Single(result.Rows)[0]);
        Assert.False(result.HasMore);
    }

    [Fact]
    public void RecursiveCtesWindowFunctionsJsonAndDocumentFunctionsRun()
    {
        Assert.Equal(15L, Run("WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 5) SELECT sum(x) FROM n").Rows[0][0]);
        SqlResultDto running = Run("SELECT Id, sum(Id) OVER (ORDER BY Id) AS total FROM Strings ORDER BY Id LIMIT 3");
        Assert.Equal(new object?[] { 1L, 3L, 6L }, running.Rows.Select(r => r[1]));
        SqlResultDto json = Run("SELECT count(*) FROM json_each($ids)", r => r with { Params = Params(new { ids = "[1,2,3]" }) });
        Assert.Equal(3L, json.Rows[0][0]);
        Assert.Equal(1L, Run("SELECT FindStackId(7, 100)").Rows[0][0]);
    }

    [Fact]
    public void ReferencedTablesListWhatSqliteRead()
    {
        Assert.Equal(new[] { "Events", "Strings" }, Run("SELECT s.Value FROM Strings s JOIN Events e ON e.Id = s.Id ORDER BY s.Id").ReferencedTables);
        IReadOnlyList<string> view = Run("SELECT Name FROM NamedEvents").ReferencedTables;
        Assert.Contains("NamedEvents", view);
        Assert.Contains("Strings", view);
        Assert.Empty(Run("SELECT 1").ReferencedTables);
    }

    [Fact]
    public void RowWindowsCarryAnExactContinuation()
    {
        SqlResultDto first = Run("SELECT Id FROM Strings ORDER BY Id", r => r with { MaxRows = 4 });
        Assert.Equal(4, first.RowCount);
        Assert.True(first.HasMore);
        Assert.Equal("maxRows", first.TruncationReason);
        Assert.Equal(4, first.NextOffset);
        SqlResultDto last = Run("SELECT Id FROM Strings ORDER BY Id", r => r with { MaxRows = 4, Offset = 8 });
        Assert.Equal(new object?[] { 9L, 10L }, last.Rows.Select(row => row[0]));
        Assert.False(last.HasMore);
        Assert.Null(last.NextOffset);

        SqlResultDto bytes = Run("SELECT Id, printf('%.2000c', 'x') FROM Strings ORDER BY Id", r => r with { MaxBytes = 4096, MaxStringLength = 4096 });
        Assert.Equal("maxBytes", bytes.TruncationReason);
        Assert.InRange(bytes.RowCount, 1, 2);

        SqlResultDto cut = Run("SELECT Value FROM Strings WHERE Id = 10", r => r with { MaxStringLength = 16 });
        Assert.Equal("name-10", cut.Rows[0][0]);
        SqlResultDto longText = Run("SELECT printf('%.40c', 'y')", r => r with { MaxStringLength = 16 });
        Assert.Equal(new string('y', 15) + "…", longText.Rows[0][0]);
        Assert.Equal(1, longText.TruncatedCells);
    }

    [Fact]
    public void ColumnsCarryDeclaredTypesAndAffinity()
    {
        SqlResultDto result = Run("SELECT Id, Value, Id + 1 AS next FROM Strings LIMIT 1");
        Assert.Equal(new[] { "Id", "Value", "next" }, result.Columns.Select(c => c.Name));
        Assert.Equal(new[] { "INTEGER", "TEXT", null }, result.Columns.Select(c => c.DeclaredType));
        Assert.Equal(new[] { "INTEGER", "TEXT", null }, result.Columns.Select(c => c.Affinity));
        Assert.Equal("NUMERIC", SqlQuery.Affinity("DECIMAL(10,5)"));
        Assert.Equal("REAL", SqlQuery.Affinity("double precision"));
        Assert.Equal("BLOB", SqlQuery.Affinity("blob"));
    }

    [Fact]
    public void CellsKeepPrecisionAndSummariseBlobs()
    {
        SqlResultDto numbers = Run("SELECT Big, 1e999, -1e999, 0.5 FROM Numbers");
        Assert.Equal("9007199254740993", numbers.Rows[0][0]);
        Assert.Equal("Infinity", numbers.Rows[0][1]);
        Assert.Equal("-Infinity", numbers.Rows[0][2]);
        Assert.Equal(0.5, numbers.Rows[0][3]);

        JsonElement summary = JsonSerializer.SerializeToElement(Run("SELECT Data FROM Numbers").Rows[0][0]);
        Assert.Equal(5000, summary.GetProperty("blobBytes").GetInt32());
        Assert.False(summary.TryGetProperty("base64", out _));

        SqlResultDto included = Run("SELECT Data FROM Numbers", r => r with { IncludeBlobs = true });
        JsonElement preview = JsonSerializer.SerializeToElement(included.Rows[0][0]);
        Assert.Equal(4096, Convert.FromBase64String(preview.GetProperty("base64").GetString()!).Length);
        Assert.True(preview.GetProperty("truncated").GetBoolean());
        Assert.Equal(1, included.TruncatedCells);
    }

    private const string CteBomb = "WITH RECURSIVE x(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM x) SELECT count(*) FROM x";

    [Fact]
    public void StatementBudgetTimesOutWithSqlTimeout()
    {
        PixToolException error = Fails(CteBomb, r => r with { TimeoutSeconds = 0.05 });
        Assert.Equal("sql_timeout", error.Detail.Code);
        Assert.True(error.Detail.Retryable);
        Assert.Contains("0.05", error.Detail.Message);
    }

    [Fact]
    public async Task CancellationInvalidationAndExternalInterruptAreDistinct()
    {
        using (var cancel = new CancellationTokenSource())
        using (TestDatabase db = Open(cancel.Token))
        {
            cancel.CancelAfter(TimeSpan.FromMilliseconds(50));
            Assert.ThrowsAny<OperationCanceledException>(() => SqlQuery.Execute(db, new SqlRequest(CteBomb), "fixture"));
        }
        using (var invalidate = new CancellationTokenSource())
        using (TestDatabase db = Open(invalidation: invalidate.Token))
        {
            invalidate.CancelAfter(TimeSpan.FromMilliseconds(50));
            PixToolException error = Assert.Throws<PixToolException>(() => SqlQuery.Execute(db, new SqlRequest(CteBomb), "fixture"));
            Assert.Equal("timing_query_invalidated", error.Detail.Code);
            Assert.True(error.Detail.Retryable);
        }
        using (TestDatabase db = Open())
        {
            Task<PixToolException> running = Task.Run(() => Assert.Throws<PixToolException>(() => SqlQuery.Execute(db, new SqlRequest(CteBomb), "fixture")));
            while (!running.IsCompleted)
            {
                db.Interrupt();
                await Task.Delay(20);
            }
            PixToolException error = await running;
            Assert.Equal("sql_interrupted", error.Detail.Code);
            Assert.True(error.Detail.Retryable);
        }
    }

    [Theory]
    [InlineData("$id")]
    [InlineData("@id")]
    [InlineData(":id")]
    public void ParametersBindByName(string placeholder)
    {
        SqlResultDto result = Run($"SELECT Value FROM Strings WHERE Id = {placeholder}", r => r with { Params = Params(new { id = 3 }) });
        Assert.Equal("name-3", Assert.Single(result.Rows)[0]);
        SqlBoundParameterDto bound = Assert.Single(result.BoundParameters);
        Assert.Equal(placeholder, bound.Name);
        Assert.Equal(3L, bound.Value);
        Assert.Equal("params", bound.Source);
    }

    [Fact]
    public void PreboundParametersBindOnlyWhenDeclaredAndCannotBeShadowed()
    {
        var prebound = new Dictionary<string, object?> { ["$reliableStart"] = 100L, ["$captureEnd"] = 900L };
        SqlResultDto result = Run("SELECT count(*) FROM Events WHERE BeginTimestamp >= $reliableStart", r => r with { Prebound = prebound });
        Assert.Equal(10L, result.Rows[0][0]);
        SqlBoundParameterDto bound = Assert.Single(result.BoundParameters);
        Assert.Equal(("$reliableStart", 100L, "prebound"), (bound.Name, bound.Value, bound.Source));

        PixToolException shadow = Fails("SELECT 1", r => r with { Prebound = prebound, Params = Params(new { reliableStart = 5 }) });
        Assert.Equal("sql_invalid_parameter", shadow.Detail.Code);
        Assert.Contains("$reliableStart", shadow.Detail.Message);
    }

    [Fact]
    public void ParameterMistakesHaveStableCodes()
    {
        PixToolException missing = Fails("SELECT $a, $b");
        Assert.Equal("sql_missing_parameter", missing.Detail.Code);
        Assert.Contains("$a, $b", missing.Detail.Message);
        PixToolException array = Fails("SELECT $ids", r => r with { Params = Params(new { ids = new[] { 1, 2 } }) });
        Assert.Equal("sql_invalid_parameter", array.Detail.Code);
        Assert.Contains("json_each", array.Detail.Message);
        Assert.Equal("sql_invalid_parameter", Fails("SELECT ?").Detail.Code);
        SqlResultDto unused = Run("SELECT 1", r => r with { Params = Params(new { extra = true }) });
        Assert.Contains(unused.Notes, note => note.Contains("extra"));
        Assert.Equal(1L, Run("SELECT $flag", r => r with { Params = Params(new { flag = true }) }).Rows[0][0]);
    }

    [Fact]
    public void ExplainAndCountTotalUseTheSameBindings()
    {
        SqlResultDto result = Run("SELECT Id FROM Strings WHERE Id > $min ORDER BY Id", r => r with { Params = Params(new { min = 4 }), MaxRows = 2, CountTotal = true, Explain = true });
        Assert.Equal(6L, result.TotalRows);
        Assert.Equal(2, result.RowCount);
        Assert.NotNull(result.Plan);
        Assert.Contains(result.Plan!, row => row.Detail.Contains("Strings", StringComparison.Ordinal));
    }

    [Fact]
    public void PrepareAndExecutionFailuresAreDistinct()
    {
        Assert.Equal("sql_syntax_error", Fails("SELEC 1").Detail.Code);
        PixToolException table = Fails("SELECT * FROM Missing");
        Assert.Equal("sql_syntax_error", table.Detail.Code);
        Assert.Contains("no such table", table.Detail.Message);
        Assert.Equal("sql_syntax_error", Fails("-- only a comment").Detail.Code);
        Assert.Equal("sql_execution_error", Fails("SELECT json_extract('not json', '$.a')").Detail.Code);
    }

    [Theory]
    [InlineData(0, 131072, 256, 0, 30.0)]
    [InlineData(5001, 131072, 256, 0, 30.0)]
    [InlineData(200, 100, 256, 0, 30.0)]
    [InlineData(200, 131072, 8, 0, 30.0)]
    [InlineData(200, 131072, 256, -1, 30.0)]
    [InlineData(200, 131072, 256, 0, 0.0)]
    [InlineData(200, 131072, 256, 0, 601.0)]
    public void RequestBoundsAreValidatedBeforeTouchingTheDatabase(int maxRows, int maxBytes, int maxStringLength, int offset, double timeout)
    {
        var request = new SqlRequest("SELECT 1") { MaxRows = maxRows, MaxBytes = maxBytes, MaxStringLength = maxStringLength, Offset = offset, TimeoutSeconds = timeout };
        Assert.Equal("invalid_arguments", Assert.Throws<PixToolException>(() => SqlQuery.Validate(request)).Detail.Code);
        Assert.Equal("invalid_arguments", Fails("   ").Detail.Code);
        Assert.Equal("invalid_arguments", Fails("SELECT '" + new string('x', SqlRequest.MaxSqlLength) + "'").Detail.Code);
    }

    [Fact]
    public void TrustedQueriesWorkAgainOnceTheGuardIsRemoved()
    {
        using TestDatabase db = Open();
        Assert.Equal("sql_forbidden", Assert.Throws<PixToolException>(() => SqlQuery.Execute(db, new SqlRequest("PRAGMA table_info(Strings)"), "fixture")).Detail.Code);
        SqlQuery.Execute(db, new SqlRequest("SELECT 1"), "fixture");
        Assert.Equal(new[] { "Id", "Value" }, db.TableInfo("Strings"));
        Assert.Equal(10, raw.sqlite3_limit(db.Handle, raw.SQLITE_LIMIT_ATTACHED, -1));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("  ;; \n", true)]
    [InlineData(" -- note\n /* block */ ;", true)]
    [InlineData(" /* unterminated", true)]
    [InlineData(" SELECT 2", false)]
    [InlineData(" -- note\nSELECT 2", false)]
    public void TailScannerOnlyIgnoresCommentsAndSeparators(string? tail, bool ignorable)
        => Assert.Equal(ignorable, SqlQuery.IsIgnorable(tail));

    private static IReadOnlyDictionary<string, JsonElement> Params(object values)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(values))!;

    private sealed class TestDatabase(string path, CancellationToken cancellation = default, CancellationToken invalidation = default)
        : ReadOnlySqlite(path, cancellation, invalidation, 120, connection =>
            connection.CreateFunction<long, long, long>("FindStackId", (thread, timestamp) => thread == 7 && timestamp == 100 ? 1 : 0))
    {
        public List<string> TableInfo(string table) => Rows("PRAGMA table_info(\"" + table + "\")", r => r.GetString(1));
    }
}
