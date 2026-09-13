using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PixMcp.Pix;
using PixMcp.Pix.Sql;
using PixMcp.Tools;
using Xunit;

namespace PixMcp.Tests;

public sealed class TimingSqlTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("pixmcp-timing-sql-tools-");
    private string Path => System.IO.Path.Combine(_directory.FullName, "pixstorage.sqlite");
    private static readonly object Continuation = new { handle = "timing-1", sql = "SELECT 1" };

    public TimingSqlTests()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        // Real PixStorage DDL (observed on 2606.18) for the tables the named queries touch; virtual tables are plain stand-ins.
        command.CommandText = """
            CREATE TABLE CaptureFacts(Id INTEGER PRIMARY KEY, Value INTEGER);
            CREATE TABLE Strings(Id INTEGER PRIMARY KEY, Value TEXT);
            CREATE TABLE Processes(Id INTEGER PRIMARY KEY, ProcessId INTEGER, ImageNameId INTEGER, StartTimestamp INTEGER, EndTimestamp INTEGER);
            CREATE TABLE Threads(Id INTEGER PRIMARY KEY, ProcThreadId INTEGER, ThreadNameId INTEGER, MaxPixEventLevel INTEGER, StartTimestamp INTEGER, EndTimestamp INTEGER, PixEventCount INTEGER, ContextSwitchCount INTEGER, ProcessRowId INTEGER, SampleCount INTEGER, MarkerCount INTEGER, ApiMarkerCount INTEGER, ApiObjectEventCount INTEGER);
            CREATE TABLE ApiCommandQueue(Id INTEGER PRIMARY KEY, TypeId INTEGER, ProcessId INTEGER, HardwareAdapterId INTEGER, NameId INTEGER, BeginTimestamp INTEGER, EndTimestamp INTEGER, ExecutionCount INTEGER, WorkCount INTEGER, MaxExecutionLevel INTEGER, MaxWorkLevel INTEGER, MarkerCount INTEGER, ApiExecutionCount INTEGER, CommandListCount INTEGER);
            CREATE TABLE ApiQueueExecution(Id INTEGER PRIMARY KEY, ApiCommandQueueId INTEGER, ThreadId INTEGER, SubmitTimestamp INTEGER, BeginTimestamp INTEGER, EndTimestamp INTEGER, FrameToken INTEGER);
            CREATE TABLE CustomDataTypeInfo(Id INTEGER PRIMARY KEY, NameId INTEGER, SubNameId INTEGER, TypeDefinitionId INTEGER, EventCount INTEGER, MarkerCount INTEGER);
            CREATE TABLE CustomMarkerInfo(Id INTEGER PRIMARY KEY, DataTypeId INTEGER, NameId INTEGER, Count INTEGER);
            CREATE TABLE CustomMarker(Id INTEGER PRIMARY KEY, MarkerInfoId INTEGER, Color INTEGER, Timestamp INTEGER);
            CREATE TABLE PhysicalCores(Id INTEGER PRIMARY KEY, EfficiencyClass INTEGER);
            CREATE TABLE Cores(Id INTEGER PRIMARY KEY, StartingThreadId INTEGER, ContextSwitchCount INTEGER, MaxPixEventLevel INTEGER, SampleCount INTEGER, PhysicalCoreId INTEGER);
            CREATE TABLE ContextSwitch(Core INTEGER, Timestamp INTEGER, FromProcThreadId INTEGER, ToProcThreadId INTEGER, ReadyThreadId INTEGER, FromThreadPriority INTEGER, ToThreadPriority INTEGER, FromThreadWaitReason INTEGER);
            CREATE TABLE DroppedData(Id INTEGER PRIMARY KEY, LaneId INTEGER, Type INTEGER, Count INTEGER, BeginTimestamp INTEGER, EndTimestamp INTEGER);
            CREATE TABLE TruncatedData(Id INTEGER PRIMARY KEY, Count INTEGER);
            CREATE TABLE CaptureStats(Id INTEGER PRIMARY KEY, Timestamp INTEGER, EventCount INTEGER, ByteCount INTEGER, LostEtwEventCount INTEGER, LostEtwBufferCount INTEGER);
            CREATE TABLE PixCpuExecutionTimes(Duration INTEGER, Execution INTEGER, Stall INTEGER, EventId INTEGER, BeginTimestamp INTEGER, EndTimestamp INTEGER);

            INSERT INTO CaptureFacts VALUES(2, 100), (3, 1000), (4, 4242), (5, 8), (24, 800);
            INSERT INTO Strings VALUES(1, 'Main Graphics Queue'), (2, 'Direct'), (3, 'Async Compute Queue'), (4, 'Compute'), (5, 'game.exe'), (6, 'Render'), (7, 'VSync'), (8, 'Monitor #{}1');
            INSERT INTO Processes VALUES(1, 4242, 5, 0, 1000), (2, 99, 5, 0, 1000);
            INSERT INTO Threads VALUES(10, (4242 << 32) | 7, 6, 1, 0, 1000, 5, 3, 1, 50, 1, 0, 0);
            INSERT INTO ApiCommandQueue VALUES(1, 2, 1, 1, 1, 0, 1000, 3, 0, 0, 0, 0, 3, 3), (2, 4, 1, 1, 3, 0, 1000, 1, 0, 0, 0, 0, 1, 1), (3, 2, 2, 1, 1, 0, 1000, 1, 0, 0, 0, 0, 1, 1);
            INSERT INTO ApiQueueExecution VALUES(1, 1, 10, 150, 200, 300, 0), (2, 1, 10, 240, 250, 350, 0), (3, 1, 10, 400, 390, 450, 0), (4, 2, 10, 500, 510, 530, 0), (5, 3, 10, 100, 200, 900, 0);
            INSERT INTO CustomDataTypeInfo VALUES(1, 8, 1, 0, 0, 3);
            INSERT INTO CustomMarkerInfo VALUES(1, 1, 7, 3);
            INSERT INTO CustomMarker VALUES(1, 1, 0, 100), (2, 1, 0, 150), (3, 1, 0, 200);
            INSERT INTO PhysicalCores VALUES(0, 0), (1, 1);
            INSERT INTO Cores VALUES(0, 0, 0, 0, 0, 0), (1, 0, 0, 0, 0, 1);
            INSERT INTO ContextSwitch VALUES(0, 150, (4242 << 32) | 7, 0, 0, 8, 0, 6), (0, 160, (4242 << 32) | 7, 0, 0, 8, 0, 6), (1, 170, (4242 << 32) | 7, 0, 0, 8, 0, 32), (1, 2000, (4242 << 32) | 7, 0, 0, 8, 0, 6);
            INSERT INTO DroppedData VALUES(1, 1, 3, 5, 100, 200), (2, 1, 7, 2, 300, 310);
            INSERT INTO TruncatedData VALUES(1, 2);
            INSERT INTO CaptureStats VALUES(1, 500, 100, 1000, 4, 1);
            INSERT INTO PixCpuExecutionTimes VALUES(10, 7, 3, 1, 200, 210);
            """;
        command.ExecuteNonQuery();
    }

    public void Dispose() => _directory.Delete(recursive: true);

    private TimingDatabase Open() => new(Path, null, configureForTests: c =>
        c.CreateFunction<long, long, long>("FindStackId", (thread, timestamp) => 0));

    private SqlResultDto Run(string sql, Func<SqlRequest, SqlRequest>? configure = null, NamedTimingQuery? named = null, string mode = "full", long? start = null, long? end = null)
    {
        using TimingDatabase db = Open();
        var request = (configure ?? (r => r))(new SqlRequest(named?.Sql ?? sql) { Params = named?.BindParams(null) });
        return TimingSqlTools.Run(db, "timing-1", request, named, start, end, mode, Continuation);
    }

    private static object? Cell(SqlResultDto result, int row, string column)
        => result.Rows[row][result.Columns.Select((c, i) => (c, i)).Single(x => x.c.Name == column).i];

    [Fact]
    public void BindingsFollowRangeModeAndExplicitBounds()
    {
        using TimingDatabase db = Open();
        TimingSqlBindings full = db.SqlBindings(null, null, "full");
        Assert.Equal((100L, 1000L), ((long)full.Values["$start"]!, (long)full.Values["$end"]!));
        Assert.Equal(4242L, full.Values["$targetPid"]);
        Assert.Equal(800L, full.Values["$reliableEnd"]);
        Assert.Equal(1000L, full.Values["$captureEnd"]);
        Assert.Equal("full", full.Range.RangeMode);
        Assert.Equal("1000", full.Range.CaptureEndNs);
        Assert.All(full.Parameters, p => Assert.Equal("prebound", p.Source));

        Assert.Equal(800L, db.SqlBindings(null, null, "reliable").Values["$end"]);
        TimingSqlBindings explicitWindow = db.SqlBindings(300, 400, "reliable");
        Assert.Equal((300L, 400L), ((long)explicitWindow.Values["$start"]!, (long)explicitWindow.Values["$end"]!));
        Assert.Equal("invalid_arguments", Assert.Throws<PixToolException>(() => db.SqlBindings(500, 400, "full")).Detail.Code);
        Assert.Equal("invalid_arguments", Assert.Throws<PixToolException>(() => TimingDatabase.NormalizeRangeMode("everything")).Detail.Code);
        Assert.Equal("full", TimingDatabase.NormalizeRangeMode(null));
    }

    [Fact]
    public void TimingWindowsReportTheirEndNoteAndCoverage()
    {
        using TimingDatabase db = Open();
        var (fullStart, fullEnd, full) = db.Range(null, null, "full");
        Assert.Equal((100L, 1000L), (fullStart, fullEnd));
        Assert.Equal(("full", "1000", "800"), (full.RangeMode, full.CaptureEndNs, full.ReliableEndNs));
        Assert.Equal(new TimingCoverageCountDto(3, 4), full.Coverage!.ContextSwitches);
        Assert.Equal(new TimingCoverageCountDto(5, 5), full.Coverage.GpuSubmissions);
        Assert.Null(full.Coverage.CpuEvents);
        Assert.Null(full.Coverage.GpuHardware);
        Assert.Contains("rangeMode=full", full.Note);

        var (_, reliableEnd, reliable) = db.Range(null, null, "reliable");
        Assert.Equal(800L, reliableEnd);
        Assert.Contains("excluded", reliable.Note);

        var (_, _, narrow) = db.Range(155, 165, "full");
        Assert.Equal(new TimingCoverageCountDto(1, 4), narrow.Coverage!.ContextSwitches);
        Assert.Equal(new TimingCoverageCountDto(0, 5), narrow.Coverage.GpuSubmissions);
        Assert.StartsWith("Explicit", narrow.Note);
    }

    [Fact]
    public void StatementsBindTheWindowAndReturnDocumentedColumns()
    {
        SqlResultDto result = Run("SELECT Id, SubmitTimestamp, BeginTimestamp - SubmitTimestamp AS latency FROM ApiQueueExecution WHERE BeginTimestamp >= $start AND EndTimestamp <= $end ORDER BY Id", r => r with { MaxRows = 2 });
        Assert.Equal(new object?[] { 1L, 2L }, result.Rows.Select(r => r[0]));
        Assert.Equal("ns", result.Columns.Single(c => c.Name == "SubmitTimestamp").Unit);
        Assert.Null(result.Columns.Single(c => c.Name == "latency").Unit);
        Assert.Equal(new[] { "$end", "$start" }, result.BoundParameters.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal("full", Assert.IsType<TimingRangeDto>(result.Provenance).RangeMode);
        Assert.Contains(result.Notes, n => n.Contains("Threads.Id", StringComparison.Ordinal));

        ToolCallDto next = result.NextCalls[0];
        Assert.Equal("pix_timing_sql", next.Tool);
        JsonElement arguments = JsonSerializer.SerializeToElement(next.Arguments, Json.Options);
        Assert.Equal(2, arguments.GetProperty("offset").GetInt32());
        Assert.Equal("SELECT 1", arguments.GetProperty("sql").GetString());
        Assert.Contains(result.NextCalls, c => c.Tool == "pix_timing_schema");
        OutputSchemaTests.AssertMatches(JsonSerializer.SerializeToElement(result, Json.Options), StructuredToolResults.SchemaFor("pix_timing_sql"));
    }

    [Fact]
    public void SqlErrorsCarryRecoveryCalls()
    {
        PixToolException forbidden = Assert.Throws<PixToolException>(() => Run("DROP TABLE Strings"));
        Assert.Equal("sql_forbidden", forbidden.Detail.Code);
        Assert.Equal("pix_timing_schema", forbidden.Detail.NextCalls[0].Tool);

        PixToolException missing = Assert.Throws<PixToolException>(() => Run("SELECT * FROM Threads WHERE Id = $thread AND ProcessRowId = $process"));
        Assert.Equal("sql_missing_parameter", missing.Detail.Code);
        ToolCallDto retry = Assert.Single(missing.Detail.NextCalls, c => c.Tool == "pix_timing_sql");
        JsonElement skeleton = JsonSerializer.SerializeToElement(retry.Arguments, Json.Options).GetProperty("params");
        Assert.Equal(JsonValueKind.Null, skeleton.GetProperty("thread").ValueKind);
        Assert.Equal(JsonValueKind.Null, skeleton.GetProperty("process").ValueKind);
    }

    [Fact]
    public void EveryNamedQueryPreparesAndGpuBusyCountsOverlapOnce()
    {
        // This fixture covers a subset of PixStorage; queries whose tables it lacks must refuse cleanly instead of failing to prepare.
        using (TimingDatabase schema = Open())
            foreach (NamedTimingQuery query in TimingQueryLibrary.All.Where(q => q.Params.All(p => !p.Required)))
            {
                if (schema.RequirementsMet(query.Requires)) Assert.Equal(query.Name, Run("", named: query).Query);
                else Assert.Equal("timing_schema_unsupported", Assert.Throws<PixToolException>(() => Run("", named: query)).Detail.Code);
            }

        SqlResultDto busy = Run("", named: TimingQueryLibrary.Find("gpu_busy_per_queue"));
        Assert.Equal(2, busy.RowCount);
        Assert.Equal("Main Graphics Queue", Cell(busy, 0, "queueName"));
        Assert.Equal(3L, Cell(busy, 0, "submissions"));
        Assert.Equal(260L, Cell(busy, 0, "sumExecutionNs"));
        Assert.Equal(210L, Cell(busy, 0, "busyNs"));
        Assert.Equal(250L, Cell(busy, 0, "spanNs"));
        Assert.Equal(900L, Cell(busy, 0, "windowNs"));
        Assert.Equal(30.0, Convert.ToDouble(Cell(busy, 0, "avgSubmitLatencyNs")));
        Assert.Equal(50L, Cell(busy, 0, "maxSubmitLatencyNs"));
        Assert.Equal(20L, Cell(busy, 1, "busyNs"));
        Assert.Contains(busy.Notes, n => n.Contains("Processes row id", StringComparison.Ordinal));

        SqlResultDto switches = Run("", named: TimingQueryLibrary.Find("context_switches_per_thread"));
        Assert.Equal(new[] { (7L, 6L, 2L), (7L, 32L, 1L) }, switches.Rows.Select(r => ((long)r[0]!, (long)r[1]!, (long)r[2]!)));

        SqlResultDto dropped = Run("", named: TimingQueryLibrary.Find("dropped_data"));
        Assert.Contains(dropped.Rows, r => (string)r[0]! == "dropped" && (long)r[1]! == 3 && (long)r[3]! == 5);
        Assert.Contains(dropped.Rows, r => (string)r[0]! == "lostEtwEvents" && (long)r[3]! == 4);

        SqlResultDto threads = Run("", named: TimingQueryLibrary.Find("thread_summary"));
        Assert.Equal((10L, 7L, "Render"), ((long)Cell(threads, 0, "threadRowId")!, (long)Cell(threads, 0, "threadId")!, (string)Cell(threads, 0, "threadName")!));

        SqlResultDto otherProcess = Run("", r => r with { Params = TimingQueryLibrary.Find("gpu_busy_per_queue")!.BindParams(new Dictionary<string, JsonElement> { ["pid"] = JsonSerializer.SerializeToElement(99) }) },
            TimingQueryLibrary.Find("gpu_busy_per_queue"));
        Assert.Equal(700L, Cell(otherProcess, 0, "busyNs"));
    }

    [Fact]
    public void NamedQueryParametersAreDeclared()
    {
        NamedTimingQuery query = TimingQueryLibrary.Find("gpu_busy_per_queue")!;
        PixToolException error = Assert.Throws<PixToolException>(() => query.BindParams(new Dictionary<string, JsonElement> { ["bogus"] = JsonSerializer.SerializeToElement(1) }));
        Assert.Equal("invalid_arguments", error.Detail.Code);
        Assert.Contains("pid", error.Detail.Message);
        Assert.Equal(JsonValueKind.Null, query.BindParams(null)["pid"].ValueKind);
        Assert.Equal(TimingQueryLibrary.All.Count, TimingQueryLibrary.All.Select(q => q.Name).Distinct().Count());
    }

    [Fact]
    public void SchemaDescribesObjectsFactsCapabilitiesAndQueries()
    {
        using TimingDatabase db = Open();
        TimingSchemaDto schema = db.Schema("timing-1", new TimingSchemaOptions(IncludeRowCounts: true), db.SqlBindings(null, null, "full"), TimingQueryLibrary.All);
        Assert.Equal(new TimingSchemaCensusDto(16, 0, 0, 0, 1), schema.Census);
        // SQLite reports function names in lower case.
        Assert.Equal("findstackid", Assert.Single(schema.Functions).Name);
        TimingSchemaTableDto times = schema.Tables.Items.Single(t => t.Name == "PixCpuExecutionTimes");
        Assert.Equal("table", times.Kind);
        Assert.All(times.Columns!, c => Assert.False(c.Hidden));
        TimingSchemaTableDto facts = schema.Tables.Items.Single(t => t.Name == "CaptureFacts");
        Assert.True(facts.Known);
        Assert.Equal((5L, "exact"), (facts.Rows!.Value, facts.RowCountState));
        Assert.Equal("ns", schema.Tables.Items.Single(t => t.Name == "ApiQueueExecution").Columns!.Single(c => c.Name == "BeginTimestamp").Unit);

        Assert.Contains(schema.CaptureFacts, f => f.Id == 4 && f.Value == "4242" && f.Meaning!.Contains("Target process", StringComparison.Ordinal));
        Assert.NotNull(schema.CaptureFacts.Single(f => f.Id == 5).Meaning);
        Assert.Contains(schema.BoundParameters, p => p.Name == "$targetPid" && Equals(p.Value, 4242L));

        Assert.Equal("available", schema.Capabilities["gpuSubmissions"].State);
        Assert.Equal("available", schema.Capabilities["vsync"].State);
        Assert.Equal("available", schema.Capabilities["heterogeneousCores"].State);
        Assert.Equal("available", schema.Capabilities["contextSwitches"].State);
        Assert.Equal("unsupported", schema.Capabilities["gpuHardware"].State);
        Assert.Equal("unsupported", schema.Capabilities["gpuMarkers"].State);
        Assert.Equal("unsupported", schema.Capabilities["presentFrames"].State);
        // The listing reports each query against this partial fixture's tables and stays compact.
        Assert.All(schema.NamedQueries!, q => Assert.Equal(db.RequirementsMet(TimingQueryLibrary.Find(q.Name)!.Requires) ? "available" : "unsupported", q.RequiresState));
        Assert.Equal("available", schema.NamedQueries!.Single(q => q.Name == "gpu_busy_per_queue").RequiresState);
        Assert.Equal("unsupported", schema.NamedQueries!.Single(q => q.Name == "gpu_hardware_queues").RequiresState);
        Assert.All(schema.NamedQueries!, q => { Assert.Empty(q.Caveats); Assert.Empty(q.Requires); });
        Assert.All(schema.NamedQueries!, q => Assert.Null(q.Sql));
        Assert.Contains(schema.NextCalls, c => c.Tool == "pix_timing_sql");
        OutputSchemaTests.AssertMatches(JsonSerializer.SerializeToElement(schema, Json.Options), StructuredToolResults.SchemaFor("pix_timing_schema"));

        TimingSchemaDto detail = db.Schema("timing-1", new TimingSchemaOptions(Table: "apiqueueexecution", IncludeSampleRows: true, Query: "gpu_busy_per_queue", IncludeColumns: false),
            db.SqlBindings(null, null, "full"), TimingQueryLibrary.All);
        Assert.StartsWith("CREATE TABLE ApiQueueExecution", detail.Table!.Ddl);
        Assert.NotEmpty(detail.Table.Joins);
        Assert.Equal(3, detail.Table.SampleRows!.RowCount);
        Assert.Equal("ApiQueueExecution", Assert.Single(detail.Tables.Items).Name);
        Assert.Null(detail.Tables.Items[0].Columns);
        TimingNamedQueryDto requested = Assert.Single(detail.NamedQueries!);
        Assert.Contains("ApiQueueExecution", requested.Sql);
        Assert.NotEmpty(requested.Caveats);
        Assert.NotEmpty(requested.Requires);

        Assert.Equal("invalid_arguments", Assert.Throws<PixToolException>(() =>
            db.Schema("timing-1", new TimingSchemaOptions(Table: "Nope"), db.SqlBindings(null, null, "full"), TimingQueryLibrary.All)).Detail.Code);
        Assert.Equal("invalid_arguments", Assert.Throws<PixToolException>(() =>
            db.Schema("timing-1", new TimingSchemaOptions(Query: "nope"), db.SqlBindings(null, null, "full"), TimingQueryLibrary.All)).Detail.Code);
    }

    [Fact]
    public void DocumentationOverlayCoversVirtualTablesFactsAndFunctions()
    {
        foreach (string module in new[] { "ContextSwitch", "CpuMemoryEvent", "FileEvents", "PixCounters", "PixCpuExecution", "PixCpuExecutionTimes", "PixCpuMarker", "PixGpuExecution" })
        {
            PixStorageTableDoc doc = Assert.IsType<PixStorageTableDoc>(PixStorageDocs.For(module));
            Assert.False(string.IsNullOrEmpty(doc.BackingTable), module);
            Assert.NotNull(PixStorageDocs.For(doc.BackingTable!));
        }
        foreach (long fact in new long[] { 2, 3, 4, 5, 24 }) Assert.NotNull(PixStorageDocs.Fact(fact));
        Assert.NotNull(PixStorageDocs.Function("FindStackId", 2));
        Assert.Contains("2606.18", PixStorageDocs.ObservedOn);
    }

    [Fact]
    public void ToolArgumentsAreValidatedBeforeAnyQuery()
    {
        using var worker = new PixWorker();
        using var session = new PixSession(worker, NullLogger<PixSession>.Instance);
        using var jobs = new JobManager(worker, session, () => null);
        // Argument validation must throw before a query job starts, so the exception is synchronous.
        static PixToolException Fails(Func<Task<string>> call)
        {
            try { call(); }
            catch (PixToolException ex) { return ex; }
            throw new Xunit.Sdk.XunitException("Expected a synchronous PixToolException.");
        }

        Assert.Equal("invalid_arguments", Fails(() => TimingSqlTools.Sql(session, jobs, "timing-1")).Detail.Code);
        Assert.Equal("invalid_arguments", Fails(() => TimingSqlTools.Sql(session, jobs, "timing-1", sql: "SELECT 1", query: "capture_facts")).Detail.Code);
        PixToolException unknown = Fails(() => TimingSqlTools.Sql(session, jobs, "timing-1", query: "nope"));
        Assert.Equal("pix_timing_schema", unknown.Detail.NextCalls[0].Tool);
        Assert.Equal("invalid_arguments", Fails(() => TimingSqlTools.Sql(session, jobs, "timing-1", sql: "SELECT 1", timeoutSeconds: 121)).Detail.Code);
        Assert.Equal("invalid_arguments", Fails(() => TimingSqlTools.Sql(session, jobs, "timing-1", sql: "SELECT 1", rangeMode: "all")).Detail.Code);
        Assert.Equal("invalid_arguments", Fails(() => TimingSqlTools.Sql(session, jobs, "timing-1", sql: "SELECT 1", maxRows: 0)).Detail.Code);
        var shadow = new Dictionary<string, JsonElement> { ["start"] = JsonSerializer.SerializeToElement(5) };
        Assert.Equal("sql_invalid_parameter", Fails(() => TimingSqlTools.Sql(session, jobs, "timing-1", sql: "SELECT $start", @params: shadow)).Detail.Code);
        Assert.Equal("invalid_arguments", Fails(() => TimingSqlTools.Sql(session, jobs, "timing-1", query: "capture_facts", @params: shadow)).Detail.Code);
        Assert.Equal("invalid_arguments", Fails(() => TimingSqlTools.Schema(session, jobs, "timing-1", limit: 0)).Detail.Code);
    }

    [SkippableFact]
    public async Task NativeSchemaAndSqlMatchTheObservedPixStorage()
    {
        string? source = TestArtifacts.TimingCapture;
        Skip.If(source is null || !File.Exists(source) || PixDiscovery.InstallDir is null, "Set PIX_TEST_TIMING_CAPTURE to a timing capture.");
        string path = System.IO.Path.Combine(_directory.FullName, "capture.wpix");
        File.Copy(source!, path);
        using var worker = new PixWorker();
        using var session = new PixSession(worker, NullLogger<PixSession>.Instance);
        using var jobs = new JobManager(worker, session, () => null);
        string handle = JsonSerializer.Deserialize<JsonElement>(await TimingCaptureTools.Open(session, path)).GetProperty("handle").GetString()!;
        try
        {
            JsonElement schema = JsonSerializer.Deserialize<JsonElement>(await TimingSqlTools.Schema(session, jobs, handle, includeColumns: false, includeQueries: false, limit: 5, waitSeconds: 60));
            JsonElement census = schema.GetProperty("census");
            Assert.Equal((105, 8, 41), (census.GetProperty("baseTables").GetInt32(), census.GetProperty("virtualTables").GetInt32(), census.GetProperty("indexes").GetInt32()));
            Assert.Contains(schema.GetProperty("functions").EnumerateArray(), f => f.GetProperty("name").GetString() == "findstackid" && f.GetProperty("arguments").GetInt32() == 2);
            JsonElement capabilities = schema.GetProperty("capabilities");
            Assert.Equal("available", capabilities.GetProperty("gpuSubmissions").GetProperty("state").GetString());
            Assert.Equal("empty", capabilities.GetProperty("gpuMarkers").GetProperty("state").GetString());
            Assert.Equal("available", capabilities.GetProperty("vsync").GetProperty("state").GetString());
            string targetPid = schema.GetProperty("captureFacts").EnumerateArray().Single(f => f.GetProperty("id").GetInt64() == 4).GetProperty("value").GetString()!;
            Assert.Contains(schema.GetProperty("boundParameters").EnumerateArray(), p => p.GetProperty("name").GetString() == "$targetPid" && p.GetProperty("value").GetRawText() == targetPid);

            JsonElement times = JsonSerializer.Deserialize<JsonElement>(await TimingSqlTools.Schema(session, jobs, handle, table: "PixCpuExecutionTimes", waitSeconds: 60))
                .GetProperty("table").GetProperty("table");
            JsonElement hidden = times.GetProperty("columns").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "CpuExecutionRowId");
            Assert.True(hidden.GetProperty("hidden").GetBoolean());
            Assert.Equal("constraintInput", hidden.GetProperty("role").GetString());
            Assert.Equal("virtual", times.GetProperty("kind").GetString());

            JsonElement busy = JsonSerializer.Deserialize<JsonElement>(await TimingSqlTools.Sql(session, jobs, handle, query: "gpu_busy_per_queue", waitSeconds: 60));
            string[] names = busy.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()!).ToArray();
            Assert.Equal(2, busy.GetProperty("rowCount").GetInt32());
            foreach (JsonElement row in busy.GetProperty("rows").EnumerateArray())
            {
                Assert.Equal(404, row[Array.IndexOf(names, "submissions")].GetInt32());
                Assert.True(row[Array.IndexOf(names, "busyNs")].GetInt64() <= row[Array.IndexOf(names, "spanNs")].GetInt64());
            }

            JsonElement switches = JsonSerializer.Deserialize<JsonElement>(await TimingSqlTools.Sql(session, jobs, handle,
                sql: "SELECT COUNT(*) AS switches FROM ContextSwitch WHERE Timestamp >= $start AND Timestamp < $end", explain: true, waitSeconds: 60));
            Assert.True(switches.GetProperty("rows")[0][0].GetInt64() > 0);
            Assert.Contains("ContextSwitch", switches.GetProperty("referencedTables").EnumerateArray().Select(t => t.GetString()));
            Assert.Contains(switches.GetProperty("plan").EnumerateArray(), p => p.GetProperty("detail").GetString()!.Contains("VIRTUAL TABLE INDEX", StringComparison.Ordinal));

            PixToolException forbidden = await Assert.ThrowsAsync<PixToolException>(() => TimingSqlTools.Sql(session, jobs, handle, sql: "PRAGMA table_xinfo(ContextSwitch)", waitSeconds: 60));
            Assert.Equal("sql_forbidden", forbidden.Detail.Code);
            Assert.Equal("pix_timing_schema", forbidden.Detail.NextCalls[0].Tool);

            JsonElement stack = JsonSerializer.Deserialize<JsonElement>(await TimingSqlTools.Sql(session, jobs, handle, sql: "SELECT typeof(findstackid(0, 0)) AS kind", waitSeconds: 60));
            Assert.Equal(1, stack.GetProperty("rowCount").GetInt32());

            // The stop timestamp (fact 24) ends recording long before the capture end (fact 3) in this capture.
            JsonElement full = JsonSerializer.Deserialize<JsonElement>(await TimingQueryTools.Overview(session, jobs, handle, waitSeconds: 60)).GetProperty("provenance");
            Assert.Equal(("full", "23334906100", "23334906100"), (full.GetProperty("rangeMode").GetString(), full.GetProperty("endNs").GetString(), full.GetProperty("captureEndNs").GetString()));
            JsonElement fullCoverage = full.GetProperty("coverage");
            Assert.Equal((629260L, 634626L), (fullCoverage.GetProperty("contextSwitches").GetProperty("inRange").GetInt64(), fullCoverage.GetProperty("contextSwitches").GetProperty("total").GetInt64()));
            Assert.Equal(808L, fullCoverage.GetProperty("gpuSubmissions").GetProperty("inRange").GetInt64());
            Assert.Equal(868L, fullCoverage.GetProperty("cpuEvents").GetProperty("inRange").GetInt64());

            JsonElement reliable = JsonSerializer.Deserialize<JsonElement>(await TimingQueryTools.Overview(session, jobs, handle, rangeMode: "reliable", waitSeconds: 60)).GetProperty("provenance");
            Assert.Equal(("reliable", "3454905300"), (reliable.GetProperty("rangeMode").GetString(), reliable.GetProperty("endNs").GetString()));
            JsonElement reliableCoverage = reliable.GetProperty("coverage");
            Assert.Equal(125503L, reliableCoverage.GetProperty("contextSwitches").GetProperty("inRange").GetInt64());
            Assert.Equal((722L, 808L), (reliableCoverage.GetProperty("gpuSubmissions").GetProperty("inRange").GetInt64(), reliableCoverage.GetProperty("gpuSubmissions").GetProperty("total").GetInt64()));
            Assert.Equal(782L, reliableCoverage.GetProperty("cpuEvents").GetProperty("inRange").GetInt64());
        }
        finally { await session.Run(() => session.Close(handle)); }
    }
}
