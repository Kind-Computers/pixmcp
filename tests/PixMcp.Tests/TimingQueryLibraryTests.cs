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
    private readonly TimingFixture _fixture = new();
    private static readonly object Continuation = new { handle = "timing-1", query = "fixture" };

    public void Dispose() => _fixture.Dispose();

    private TimingDatabase Open() => _fixture.Open();

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
