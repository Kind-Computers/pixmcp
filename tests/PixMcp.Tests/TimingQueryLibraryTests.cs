using System.Text.Json;
using Microsoft.Data.Sqlite;
using PixMcp.Pix;
using PixMcp.Pix.Sql;
using PixMcp.Tools;
using Xunit;

namespace PixMcp.Tests;

/// <summary>Every named timing query against a fixture that carries every table the library reads, with hand-computed aggregates.</summary>
public sealed class TimingQueryLibraryTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("pixmcp-timing-library-");
    private string Path => System.IO.Path.Combine(_directory.FullName, "pixstorage.sqlite");
    private static readonly object Continuation = new { handle = "timing-1", query = "fixture" };

    public TimingQueryLibraryTests()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        // Column lists as observed on PIX 2606.18; virtual tables (ContextSwitch, PixCpuExecution, PixCpuExecutionTimes, PixCpuMarker,
        // PixCounters, FileEvents, CpuMemoryEvent) are plain stand-ins.
        command.CommandText = """
            CREATE TABLE CaptureFacts(Id INTEGER PRIMARY KEY, Value INTEGER);
            CREATE TABLE CaptureData(Id INTEGER PRIMARY KEY, NameId INTEGER, ValueId INTEGER);
            CREATE TABLE Strings(Id INTEGER PRIMARY KEY, Value TEXT);
            CREATE TABLE Processes(Id INTEGER PRIMARY KEY, ProcessId INTEGER, ImageNameId INTEGER, StartTimestamp INTEGER, EndTimestamp INTEGER);
            CREATE TABLE Threads(Id INTEGER PRIMARY KEY, ProcThreadId INTEGER, ThreadNameId INTEGER, MaxPixEventLevel INTEGER, StartTimestamp INTEGER, EndTimestamp INTEGER, PixEventCount INTEGER, ContextSwitchCount INTEGER, ProcessRowId INTEGER, SampleCount INTEGER, MarkerCount INTEGER, ApiMarkerCount INTEGER, ApiObjectEventCount INTEGER);
            CREATE TABLE Cores(Id INTEGER PRIMARY KEY, StartingThreadId INTEGER, ContextSwitchCount INTEGER, MaxPixEventLevel INTEGER, SampleCount INTEGER, PhysicalCoreId INTEGER);
            CREATE TABLE PhysicalCores(Id INTEGER PRIMARY KEY, EfficiencyClass INTEGER);
            CREATE TABLE ApiCommandQueue(Id INTEGER PRIMARY KEY, TypeId INTEGER, ProcessId INTEGER, HardwareAdapterId INTEGER, NameId INTEGER, BeginTimestamp INTEGER, EndTimestamp INTEGER, ExecutionCount INTEGER, WorkCount INTEGER, MaxExecutionLevel INTEGER, MaxWorkLevel INTEGER, MarkerCount INTEGER, ApiExecutionCount INTEGER, CommandListCount INTEGER);
            CREATE TABLE ApiQueueExecution(Id INTEGER PRIMARY KEY, ApiCommandQueueId INTEGER, ThreadId INTEGER, SubmitTimestamp INTEGER, BeginTimestamp INTEGER, EndTimestamp INTEGER, FrameToken INTEGER);
            CREATE TABLE HardwareAdapter(Id INTEGER PRIMARY KEY, NameId INTEGER);
            CREATE TABLE HardwareCommandQueue(Id INTEGER PRIMARY KEY, AdapterId INTEGER, NameId INTEGER);
            CREATE TABLE GpuWorkRange(Id INTEGER PRIMARY KEY, HardwareQueueId INTEGER, ApiCommandQueueId INTEGER, BeginTimestamp INTEGER, EndTimestamp INTEGER, OverlapLevel INTEGER, Work BLOB);
            CREATE TABLE ContextSwitch(Core INTEGER, Timestamp INTEGER, FromProcThreadId INTEGER, ToProcThreadId INTEGER, ReadyThreadId INTEGER, FromThreadPriority INTEGER, ToThreadPriority INTEGER, FromThreadWaitReason INTEGER);
            CREATE TABLE ReadyThread(Id INTEGER PRIMARY KEY, Timestamp INTEGER, Core INTEGER, ReadyingThreadRowId INTEGER, AdjustReason INTEGER, AdjustIncrement INTEGER);
            CREATE TABLE PixEventInfo(Id INTEGER PRIMARY KEY, NameId INTEGER, Count INTEGER, GpuCount INTEGER, MinCpuLevel INTEGER, MinGpuLevel INTEGER);
            CREATE TABLE PixCpuExecution(BeginTimestamp INTEGER, EndTimestamp INTEGER, Level INTEGER, ThreadRowId INTEGER, EventId INTEGER, Color INTEGER);
            CREATE TABLE PixCpuExecutionTimes(Duration INTEGER, Execution INTEGER, Stall INTEGER, EventId INTEGER, BeginTimestamp INTEGER, EndTimestamp INTEGER);
            CREATE TABLE PixMarkerInfo(Id INTEGER PRIMARY KEY, NameId INTEGER, CpuCount INTEGER, GpuCount INTEGER);
            CREATE TABLE PixCpuMarker(InfoId INTEGER, Timestamp INTEGER, ThreadId INTEGER, Color INTEGER);
            CREATE TABLE CustomDataTypeInfo(Id INTEGER PRIMARY KEY, NameId INTEGER, SubNameId INTEGER, TypeDefinitionId INTEGER, EventCount INTEGER, MarkerCount INTEGER);
            CREATE TABLE CustomMarkerInfo(Id INTEGER PRIMARY KEY, DataTypeId INTEGER, NameId INTEGER, Count INTEGER);
            CREATE TABLE CustomMarker(Id INTEGER PRIMARY KEY, MarkerInfoId INTEGER, Color INTEGER, Timestamp INTEGER);
            CREATE TABLE GpuFrame(Id INTEGER PRIMARY KEY, PresentCallTime INTEGER, PresentReturnTime INTEGER, VSyncTime INTEGER, GPUBusyDuration INTEGER);
            CREATE TABLE PixCounterGroup(Id INTEGER PRIMARY KEY, ParentGroupId INTEGER, NameId INTEGER, DescriptionId INTEGER, DefinitionId INTEGER);
            CREATE TABLE PixCounterInfo(Id INTEGER PRIMARY KEY, GroupId INTEGER, ProcessId INTEGER, NameId INTEGER, DescriptionId INTEGER, DefinitionId INTEGER, UnitsId INTEGER, Flags INTEGER, MinValue REAL, MaxValue REAL);
            CREATE TABLE PixCounters(CounterId INTEGER, Timestamp INTEGER, Value REAL);
            CREATE TABLE Modules(Id INTEGER PRIMARY KEY, PEPathId INTEGER, PETimestamp INTEGER, PESize INTEGER, PDBPathId INTEGER, PDBGuid BLOB, PDBAge INTEGER);
            CREATE TABLE Images(Id INTEGER PRIMARY KEY, OSProcessId INTEGER, PELoadAddress INTEGER, LoadSize INTEGER, LoadTimestamp INTEGER, UnloadTimestamp INTEGER, FilePathId INTEGER, ModuleId INTEGER, XMemFlags INTEGER);
            CREATE TABLE FunctionInformation(Id INTEGER PRIMARY KEY, ModuleId INTEGER, Offset INTEGER, Size INTEGER, DecoratedNameId INTEGER, MethodToken INTEGER);
            CREATE TABLE DroppedData(Id INTEGER PRIMARY KEY, LaneId INTEGER, Type INTEGER, Count INTEGER, BeginTimestamp INTEGER, EndTimestamp INTEGER);
            CREATE TABLE TruncatedData(Id INTEGER PRIMARY KEY, Count INTEGER);
            CREATE TABLE CaptureStats(Id INTEGER PRIMARY KEY, Timestamp INTEGER, EventCount INTEGER, ByteCount INTEGER, LostEtwEventCount INTEGER, LostEtwBufferCount INTEGER);
            CREATE TABLE FileEvents(BeginTimestamp INTEGER, EndTimestamp INTEGER, DeviceId INTEGER, FileId INTEGER, ProcThreadId INTEGER, Offset INTEGER, Size INTEGER, TypeId INTEGER, Type TEXT, Flags0 INTEGER, Flags1 INTEGER, Status INTEGER);
            CREATE TABLE CpuMemoryEvent(OSProcessId INTEGER, AllocatorId INTEGER, Timestamp INTEGER, BaseAddress INTEGER, Size INTEGER, Flags INTEGER, OSThreadId INTEGER, IsFree INTEGER);

            INSERT INTO CaptureFacts VALUES(2, 100), (3, 10000), (4, 4242), (5, 4), (24, 5000);
            INSERT INTO CaptureData VALUES(1, 23, 1);
            INSERT INTO Strings VALUES(1, 'game.exe'), (2, 'Render'), (3, 'Worker'), (4, 'Main Graphics Queue'), (5, 'Direct'), (6, 'Frame'), (7, 'Tick'), (8, '3D'),
              (9, 'Test Adapter'), (10, 'VSync'), (11, 'Monitor #{}1'), (12, 'GPU Memory (Adapter #1)'), (13, 'Local Budget'), (14, 'Local Usage'), (15, 'MB'),
              (16, 'Custom counters'), (17, 'Frame Number'), (18, 'game.exe'), (19, 'game.pdb'), (20, 'other.dll'), (21, 'Non-Local Budget'), (22, 'Non-Local Usage'), (23, 'Target Process Name');
            INSERT INTO Processes VALUES(1, 4242, 1, 0, 10000);
            INSERT INTO Threads VALUES(10, (4242 << 32) | 7, 2, 1, 0, 10000, 2, 6, 1, 5, 2, 0, 0), (11, (4242 << 32) | 8, 3, 0, 0, 10000, 0, 2, 1, 1, 0, 0, 0);
            INSERT INTO Cores VALUES(0, 0, 10, 0, 4, 0), (1, 0, 6, 0, 2, 1), (2, 0, 4, 0, 1, 2), (3, 0, 2, 0, 0, 2);
            INSERT INTO PhysicalCores VALUES(0, 1), (1, 1), (2, 0);
            INSERT INTO ApiCommandQueue VALUES(1, 5, 1, 1, 4, 0, 10000, 4, 0, 0, 0, 0, 4, 4);
            INSERT INTO ApiQueueExecution VALUES(1, 1, 10, 150, 200, 300, 0), (2, 1, 10, 400, 500, 600, 0), (3, 1, 11, 700, 700, 900, 0), (4, 1, 10, 1000, 990, 1100, 0);
            INSERT INTO HardwareAdapter VALUES(1, 9);
            INSERT INTO HardwareCommandQueue VALUES(45, 1, 8);
            INSERT INTO GpuWorkRange VALUES(1, 45, 1, 150, 950, 0, x'00'), (2, 45, 1, 300, 400, 1, x'00');
            INSERT INTO ContextSwitch VALUES
              (0, 1000, 0, (4242 << 32) | 7, 1, 0, 8, 0), (0, 1500, (4242 << 32) | 7, 0, 0, 8, 0, 6),
              (0, 2500, 0, (4242 << 32) | 7, 2, 0, 8, 0), (0, 2600, (4242 << 32) | 7, 0, 0, 8, 0, 32),
              (1, 2650, 0, (4242 << 32) | 7, 0, 0, 8, 0), (1, 3000, (4242 << 32) | 7, 0, 0, 8, 0, 6),
              (1, 3600, 0, (4242 << 32) | 7, 3, 0, 8, 0), (1, 20000, (4242 << 32) | 7, 0, 0, 8, 0, 6);
            INSERT INTO ReadyThread VALUES(1, 990, 0, 11, 0, 0), (2, 2400, 0, 11, 0, 0), (3, 3580, 1, 11, 0, 0);
            INSERT INTO PixEventInfo VALUES(1, 6, 2, 0, 0, 0);
            INSERT INTO PixCpuExecution VALUES(200, 400, 0, 10, 1, 0), (600, 700, 0, 10, 1, 0);
            INSERT INTO PixCpuExecutionTimes VALUES(200, 150, 50, 1, 200, 400), (100, 100, 0, 1, 600, 700);
            INSERT INTO PixMarkerInfo VALUES(1, 7, 3, 0);
            INSERT INTO PixCpuMarker VALUES(1, 300, 10, 0), (1, 400, 10, 0), (1, 6000, 11, 0);
            INSERT INTO CustomDataTypeInfo VALUES(1, 11, 9, 0, 0, 5);
            INSERT INTO CustomMarkerInfo VALUES(1, 1, 10, 5);
            INSERT INTO CustomMarker VALUES(1, 1, 0, 1000), (2, 1, 0, 2000), (3, 1, 0, 3000), (4, 1, 0, 4000), (5, 1, 0, 8000);
            INSERT INTO GpuFrame VALUES(1, 1000, 1010, 1500, 7), (2, NULL, NULL, NULL, NULL);
            INSERT INTO PixCounterGroup VALUES(1, NULL, 12, 0, 0), (2, NULL, 16, 0, 0);
            INSERT INTO PixCounterInfo VALUES(1, 1, 1, 13, 0, 0, 15, 0, 0, 0), (2, 1, 1, 14, 0, 0, 15, 0, 0, 0), (3, 2, 1, 17, 0, 0, 0, 0, 0, 0), (4, 1, 1, 21, 0, 0, 15, 0, 0, 0), (5, 1, 1, 22, 0, 0, 15, 0, 0, 0);
            INSERT INTO PixCounters VALUES(1, 50, 1000.0), (1, 5000, 800.0), (2, 60, 100.0), (2, 3000, 900.0), (2, 9000, 300.0),
              (3, 150, 1), (3, 1150, 2), (3, 2150, 3), (3, 3150, 4), (4, 50, 500.0), (5, 60, 50.0);
            INSERT INTO Modules VALUES(1, 18, 0, 0, 19, x'00', 1), (2, 20, 0, 0, NULL, NULL, 0);
            INSERT INTO Images VALUES(1, 4242, 4096, 100, 10, 0, 18, 1, 0), (2, 4242, 8192, 100, 20, 0, 20, 2, 0);
            INSERT INTO FunctionInformation VALUES(1, 1, 0, 10, 1, 0), (2, 1, 10, 10, 2, 0);
            INSERT INTO DroppedData VALUES(1, 7, 3, 4, 100, 200);
            INSERT INTO TruncatedData VALUES(1, 0);
            INSERT INTO CaptureStats VALUES(1, 500, 10, 100, 0, 0);
            INSERT INTO FileEvents VALUES(100, 150, 1, 1, (4242 << 32) | 7, 0, 4096, 1, 'Read', 0, 0, 0), (200, 400, 1, 1, (4242 << 32) | 7, 0, 1024, 2, 'Write', 0, 0, 0), (300, 310, 1, 1, (4242 << 32) | 7, 0, 2048, 1, 'Read', 0, 0, 0);
            INSERT INTO CpuMemoryEvent VALUES(4242, 1, 200, 4096, 100, 0, 7, 0), (4242, 1, 300, 8192, 50, 0, 7, 0), (4242, 1, 400, 4096, 100, 0, 7, 1);
            """;
        command.ExecuteNonQuery();
    }

    public void Dispose() => _directory.Delete(recursive: true);

    private TimingDatabase Open() => new(Path, null, configureForTests: _ => { });

    private static SqlResultDto Query(TimingDatabase db, string name, object? parameters = null, string mode = "full")
    {
        NamedTimingQuery query = TimingQueryLibrary.Find(name) ?? throw new Xunit.Sdk.XunitException("Unknown query " + name);
        var supplied = parameters is null ? null : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(parameters));
        var request = new SqlRequest(query.Sql) { Params = query.BindParams(supplied) };
        return TimingSqlTools.Run(db, "timing-1", request, query, null, null, mode, Continuation);
    }

    private static object? Cell(SqlResultDto result, int row, string column)
        => result.Rows[row][result.Columns.Select((c, i) => (c, i)).Single(x => x.c.Name == column).i];

    private static double Number(SqlResultDto result, int row, string column) => Convert.ToDouble(Cell(result, row, column));

    [Fact]
    public void EveryLibraryQueryPreparesOnTheFullSchema()
    {
        using TimingDatabase db = Open();
        foreach (NamedTimingQuery query in TimingQueryLibrary.All)
        {
            Assert.True(db.RequirementsMet(query.Requires), query.Name + " requirements");
            SqlResultDto result = Query(db, query.Name, query.Name == "counters_bucketed" ? new { counterId = 3 } : null);
            Assert.Equal(query.Name, result.Query);
            Assert.Contains(result.Notes, note => query.Caveats.Contains(note));
        }
        Assert.Equal(TimingQueryLibrary.All.Count, TimingQueryLibrary.All.Select(q => q.Name).Distinct().Count());
    }

    [Fact]
    public void RequiredParametersAreEnforced()
    {
        NamedTimingQuery bucketed = TimingQueryLibrary.Find("counters_bucketed")!;
        PixToolException missing = Assert.Throws<PixToolException>(() => bucketed.BindParams(null));
        Assert.Equal("invalid_arguments", missing.Detail.Code);
        Assert.Contains("counterId", missing.Detail.Message);
        Assert.Equal(1000000000L, bucketed.BindParams(new Dictionary<string, JsonElement> { ["counterId"] = JsonSerializer.SerializeToElement(3) })["bucketNs"].GetInt64());
        Assert.Contains("Required", bucketed.Describe(true, false).Params.Single(p => p.Name == "counterId").Description);
    }

    [Fact]
    public void GpuQueriesMatchHandComputedValues()
    {
        using TimingDatabase db = Open();
        SqlResultDto busy = Query(db, "gpu_busy_per_queue");
        Assert.Equal((4.0, 510.0, 510.0, 900.0, 50.0, 100.0), (Number(busy, 0, "submissions"), Number(busy, 0, "sumExecutionNs"), Number(busy, 0, "busyNs"), Number(busy, 0, "spanNs"),
            Number(busy, 0, "avgSubmitLatencyNs"), Number(busy, 0, "maxSubmitLatencyNs")));

        SqlResultDto latency = Query(db, "submit_latency_per_thread");
        Assert.Equal(2, latency.RowCount);
        Assert.Equal(("Render", 2.0, 75.0, 100.0, 100.0), ((string)Cell(latency, 0, "threadName")!, Number(latency, 0, "submissions"), Number(latency, 0, "avgLatencyNs"),
            Number(latency, 0, "p95LatencyNs"), Number(latency, 0, "maxLatencyNs")));
        Assert.Equal((1.0, 0.0), (Number(latency, 1, "submissions"), Number(latency, 1, "p95LatencyNs")));

        SqlResultDto hardware = Query(db, "gpu_hardware_queues");
        Assert.Equal(("3D", "Test Adapter", 2.0, 150.0, 950.0, 1.0), ((string)Cell(hardware, 0, "name")!, (string)Cell(hardware, 0, "adapter")!, Number(hardware, 0, "ranges"),
            Number(hardware, 0, "firstNs"), Number(hardware, 0, "lastNs"), Number(hardware, 0, "maxOverlapLevel")));

        SqlResultDto present = Query(db, "frames_present");
        Assert.Equal(1, present.RowCount);
        Assert.Equal((10.0, 500.0), (Number(present, 0, "presentDurationNs"), Number(present, 0, "presentToVsyncNs")));

        SqlResultDto vsync = Query(db, "frames_vsync");
        Assert.Equal(("Monitor #{}1", 4.0, 1750.0, 1000.0, 1000.0, 4000.0, 4000.0), ((string)Cell(vsync, 0, "monitor")!, Number(vsync, 0, "intervals"), Number(vsync, 0, "avgIntervalNs"),
            Number(vsync, 0, "minIntervalNs"), Number(vsync, 0, "p50IntervalNs"), Number(vsync, 0, "p95IntervalNs"), Number(vsync, 0, "maxIntervalNs")));
    }

    [Fact]
    public void SchedulingQueriesPairSwitchesAndReadyEvents()
    {
        using TimingDatabase db = Open();
        SqlResultDto waits = Query(db, "context_switch_waits", new { tid = 7 });
        Assert.Equal(2, waits.RowCount);
        Assert.Equal((7.0, 6.0, 2.0, 1600.0, 1000.0), (Number(waits, 0, "threadId"), Number(waits, 0, "waitReason"), Number(waits, 0, "waits"), Number(waits, 0, "waitedNs"), Number(waits, 0, "maxWaitNs")));
        Assert.Equal((32.0, 1.0, 50.0), (Number(waits, 1, "waitReason"), Number(waits, 1, "waits"), Number(waits, 1, "waitedNs")));

        SqlResultDto ready = Query(db, "ready_thread_latency", new { tid = 7 });
        Assert.Equal((3.0, 43.0, 100.0, 100.0), (Number(ready, 0, "readies"), Number(ready, 0, "avgLatencyNs"), Number(ready, 0, "p95LatencyNs"), Number(ready, 0, "maxLatencyNs")));

        SqlResultDto switches = Query(db, "context_switches_per_thread");
        Assert.Equal(new[] { (7.0, 6.0, 2.0), (7.0, 32.0, 1.0) }, switches.Rows.Select(r => (Convert.ToDouble(r[0]), Convert.ToDouble(r[1]), Convert.ToDouble(r[2]))));

        SqlResultDto cores = Query(db, "core_efficiency");
        Assert.Equal(new[] { (1.0, 2.0, 2.0, 6.0, 16.0), (0.0, 2.0, 1.0, 1.0, 6.0) },
            cores.Rows.Select(r => (Convert.ToDouble(r[0]), Convert.ToDouble(r[1]), Convert.ToDouble(r[2]), Convert.ToDouble(r[3]), Convert.ToDouble(r[4]))));
    }

    [Fact]
    public void CpuCounterSymbolAndIoQueriesMatchHandComputedValues()
    {
        using TimingDatabase db = Open();
        SqlResultDto rollup = Query(db, "cpu_execution_rollup");
        Assert.Equal(("Frame", 2.0, 300.0, 150.0, 200.0, 250.0, 50.0), ((string)Cell(rollup, 0, "event")!, Number(rollup, 0, "occurrences"), Number(rollup, 0, "inclusiveNs"),
            Number(rollup, 0, "avgNs"), Number(rollup, 0, "maxNs"), Number(rollup, 0, "executionNs"), Number(rollup, 0, "stallNs")));

        SqlResultDto markers = Query(db, "cpu_markers");
        Assert.Equal(new[] { ("Render", 2.0), ("Worker", 1.0) }, markers.Rows.Select((_, i) => ((string)Cell(markers, i, "threadName")!, Number(markers, i, "markers"))));

        SqlResultDto vram = Query(db, "vram_budget");
        Assert.Equal(("local", 800.0, 800.0, 900.0, 300.0, 112.5), ((string)Cell(vram, 0, "pool")!, Number(vram, 0, "lastBudgetMb"), Number(vram, 0, "minBudgetMb"),
            Number(vram, 0, "peakUsageMb"), Number(vram, 0, "lastUsageMb"), Number(vram, 0, "peakPercentOfBudget")));
        Assert.Equal(("nonLocal", 500.0, 50.0, 10.0), ((string)Cell(vram, 1, "pool")!, Number(vram, 1, "lastBudgetMb"), Number(vram, 1, "peakUsageMb"), Number(vram, 1, "peakPercentOfBudget")));

        SqlResultDto buckets = Query(db, "counters_bucketed", new { counterId = 3, bucketNs = 1000 });
        Assert.Equal(new[] { 100.0, 1100.0, 2100.0, 3100.0 }, buckets.Rows.Select((_, i) => Number(buckets, i, "bucketStartNs")));

        SqlResultDto modules = Query(db, "module_symbols");
        Assert.Equal(new[] { ("game.exe", 2.0, "resolved"), ("other.dll", 0.0, "unresolved") },
            modules.Rows.Select((_, i) => ((string)Cell(modules, i, "module")!, Number(modules, i, "functions"), (string)Cell(modules, i, "symbolState")!)));

        SqlResultDto files = Query(db, "file_io_summary");
        Assert.Equal(new[] { ("Write", 1.0, 1024.0, 200.0), ("Read", 2.0, 6144.0, 60.0) },
            files.Rows.Select((_, i) => ((string)Cell(files, i, "operation")!, Number(files, i, "operations"), Number(files, i, "bytes"), Number(files, i, "durationNs"))));

        SqlResultDto memory = Query(db, "memory_summary");
        Assert.Equal(new[] { ("allocation", 2.0, 150.0), ("free", 1.0, 100.0) },
            memory.Rows.Select((_, i) => ((string)Cell(memory, i, "kind")!, Number(memory, i, "events"), Number(memory, i, "bytes"))));

        Assert.Equal(new[] { "Render", "Worker" }, Query(db, "thread_summary").Rows.Select(r => (string)r[2]!));
    }

    [Fact]
    public void OverviewSectionsSummariseTheLibraryAndDeriveInsights()
    {
        using TimingDatabase db = Open();
        TimingOverviewDto overview = db.Overview("timing-1", null, 0, 5);
        TimingOverviewSectionsDto sections = overview.Sections!;
        Assert.Empty(sections.Unavailable);
        Assert.Equal("game.exe", sections.Capture!.Data["Target Process Name"]);
        Assert.Equal(("lossy", 4L, 0L, 0L), (sections.DataQuality!.State, sections.DataQuality.DroppedEvents, sections.DataQuality.TruncatedEvents, sections.DataQuality.LostEtwEvents));
        Assert.Equal(4L, sections.DataQuality.DroppedByType["3"]);
        TimingGpuQueueSummaryDto queue = Assert.Single(sections.Gpu!);
        Assert.Equal(("Main Graphics Queue", 4L, 510L, 5.15, 900L), (queue.Name, queue.Submissions, queue.BusyNs, queue.BusyPercentOfWindow!.Value, queue.SpanNs));
        Assert.Equal(("CustomMarker:VSync", "Monitor #{}1", 4L, 0.004), (sections.Frames!.Source, sections.Frames.Monitor, sections.Frames.Intervals, sections.Frames.MaxMs));
        Assert.Equal((4L, 3L, true), (sections.Cores!.LogicalCores, sections.Cores.PhysicalCores, sections.Cores.Heterogeneous));
        Assert.Equal(new long[] { 1, 0 }, sections.Cores.EfficiencyClasses);
        Assert.Equal((2L, 1L), (sections.Modules!.Modules, sections.Modules.Resolved));
        Assert.Equal(new[] { "other.dll" }, sections.Modules.UnresolvedExamples);
        Assert.Equal(112.5, sections.Vram!.Single(p => p.Pool == "local").PeakPercentOfBudget);

        Assert.Equal(new[] { "vram_over_budget", "dropped_data", "gpu_idle_high", "no_gpu_markers", "hybrid_cores_present" }, sections.Insights.Select(i => i.Id));
        Assert.Equal("warning", sections.Insights[0].Severity);
        Assert.All(sections.Insights, insight =>
        {
            Assert.NotEmpty(insight.NextCalls);
            Assert.False(string.IsNullOrEmpty(insight.Implication));
            Assert.NotEmpty(insight.Summary);
        });
        Assert.Equal(112.5, sections.Insights[0].Evidence["peakPercentOfBudget"]);
        Assert.Contains(sections.Insights[0].NextCalls, c => c.Tool == "pix_timing_sql" && JsonSerializer.SerializeToElement(c.Arguments).GetProperty("query").GetString() == "vram_budget");
        OutputSchemaTests.AssertMatches(JsonSerializer.SerializeToElement(overview, Json.Options), StructuredToolResults.SchemaFor("pix_timing_overview"));
    }

    [SkippableFact]
    public void NativeCaptureRunsEveryLibraryQuery()
    {
        string? capture = TestArtifacts.TimingCapture;
        Skip.If(capture is null || PixDiscovery.InstallDir is null, "Set PIX_TEST_TIMING_CAPTURE to a timing capture.");
        using var db = new TimingDatabase(capture!, System.IO.Path.Combine(PixDiscovery.InstallDir!, "pixstorage.dll"));
        var results = new Dictionary<string, SqlResultDto>(StringComparer.Ordinal);
        foreach (NamedTimingQuery query in TimingQueryLibrary.All)
        {
            if (!db.RequirementsMet(query.Requires)) continue;
            results[query.Name] = Query(db, query.Name, query.Name == "counters_bucketed" ? new { counterId = 1 } : null);
        }
        Assert.True(results.Count >= 18, "most library queries run on a real capture: " + string.Join(", ", results.Keys));

        SqlResultDto vsync = results["frames_vsync"];
        Assert.Equal(589.0, Number(vsync, 0, "intervals"));
        Assert.InRange(Number(vsync, 0, "avgIntervalNs"), 5_000_000, 7_000_000);
        Assert.Equal(807.0, Number(results["submit_latency_per_thread"], 0, "submissions"));
        Assert.Equal(1, results["core_efficiency"].RowCount);
        Assert.Equal("resolved", Cell(results["module_symbols"], 0, "symbolState"));
        Assert.Equal(434.0, Number(results["cpu_markers"], 0, "markers"));
        Assert.All(results["cpu_execution_rollup"].Rows.Select((_, i) => Number(results["cpu_execution_rollup"], i, "occurrences")), n => Assert.Equal(434.0, n));
        Assert.Equal(("3D", 15.0), ((string)Cell(results["gpu_hardware_queues"], 0, "name")!, Number(results["gpu_hardware_queues"], 0, "ranges")));
        Assert.Equal(0, results["frames_present"].RowCount);
        Assert.InRange(Number(results["vram_budget"], 0, "peakPercentOfBudget"), 0, 100);
        Assert.NotEmpty(results["context_switch_waits"].Rows);
        Assert.NotEmpty(results["ready_thread_latency"].Rows);

        TimingOverviewDto nativeOverview = db.Overview("timing-native", null, 0, 5);
        int overviewBytes = Json.Serialize(nativeOverview).Length;
        Assert.True(overviewBytes < 8192, $"The default timing overview should stay under 8 KB; it is {overviewBytes} bytes.");
        TimingOverviewSectionsDto sections = nativeOverview.Sections!;
        Assert.Empty(sections.Unavailable);
        Assert.False(string.IsNullOrEmpty(sections.Capture!.Data.GetValueOrDefault("Target Process Name")));
        Assert.Equal(("lossy", 8603L), (sections.DataQuality!.State, sections.DataQuality.DroppedEvents));
        Assert.Equal(2, sections.Gpu!.Count);
        Assert.All(sections.Gpu, q => Assert.Equal(404L, q.Submissions));
        Assert.Equal(589L, sections.Frames!.Intervals);
        Assert.InRange(sections.Frames.AvgMs, 5.0, 7.0);
        Assert.Equal((32L, false), (sections.Cores!.LogicalCores, sections.Cores.Heterogeneous));
        Assert.True(sections.Modules!.Resolved >= 1);
        Assert.NotEmpty(sections.Vram!);
        string[] ids = sections.Insights.Select(i => i.Id).ToArray();
        Assert.Contains("dropped_data", ids);
        Assert.Contains("no_gpu_markers", ids);
        Assert.Contains("gpu_idle_high", ids);
        Assert.Contains("submit_latency_high", ids);
        Assert.InRange(ids.Length, 1, TimingDatabase.MaxInsights);

        // This capture's CPU has one efficiency class: filtering by it keeps every sample and no per-class counts are reported.
        TimingSampleAnalysisDto everyCore = db.Samples("timing-native", 61052, null, null, null);
        TimingSampleAnalysisDto classZero = db.Samples("timing-native", 61052, null, null, null, efficiencyClass: 0);
        Assert.Equal(everyCore.Coverage.TotalSamples, classZero.Coverage.TotalSamples);
        Assert.Null(everyCore.Coverage.SamplesByEfficiencyClass);
        Assert.All(everyCore.Hotspots, h => Assert.Null(h.InclusiveByEfficiencyClass));
        Assert.Equal("invalid_arguments", Assert.Throws<PixToolException>(() => db.Samples("timing-native", 61052, null, null, null, efficiencyClass: 1)).Detail.Code);
    }
}
