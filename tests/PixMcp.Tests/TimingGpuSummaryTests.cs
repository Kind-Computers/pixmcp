using System.Text;
using Microsoft.Data.Sqlite;
using PixMcp.Pix;
using Xunit;

namespace PixMcp.Tests;

/// <summary>pix_timing_gpu_summary over a fixture with overlapping, clipped and invalid submissions, hardware ranges and VSync markers.</summary>
public sealed class TimingGpuSummaryTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("pixmcp-gpu-summary-");
    private string Path => System.IO.Path.Combine(_directory.FullName, "pixstorage.sqlite");

    public TimingGpuSummaryTests() => Execute("""
        CREATE TABLE CaptureFacts(Id INTEGER PRIMARY KEY, Value INTEGER);
        INSERT INTO CaptureFacts VALUES(2, 0), (3, 100000000), (4, 42), (24, 50000000);
        CREATE TABLE Strings(Id INTEGER PRIMARY KEY, Value TEXT);
        INSERT INTO Strings VALUES(1, 'Graphics'), (2, 'Direct'), (3, 'Compute'), (4, 'Render'), (5, 'Worker'), (6, 'VSync'), (7, 'Monitor 1'), (8, '3D'), (9, 'Adapter');
        CREATE TABLE Processes(Id INTEGER PRIMARY KEY, ProcessId INTEGER);
        INSERT INTO Processes VALUES(1, 42), (2, 43);
        CREATE TABLE Threads(Id INTEGER PRIMARY KEY, ProcThreadId INTEGER, ThreadNameId INTEGER, ProcessRowId INTEGER, StartTimestamp INTEGER, EndTimestamp INTEGER);
        INSERT INTO Threads VALUES(10, (42 << 32) | 7, 4, 1, 0, 100000000), (11, (42 << 32) | 8, 5, 1, 0, 100000000);
        CREATE TABLE ApiCommandQueue(Id INTEGER PRIMARY KEY, TypeId INTEGER, ProcessId INTEGER, NameId INTEGER);
        INSERT INTO ApiCommandQueue VALUES(1, 2, 1, 1), (2, 3, 1, 3), (3, 2, 2, 1);
        CREATE TABLE ApiQueueExecution(Id INTEGER PRIMARY KEY, ApiCommandQueueId INTEGER, ThreadId INTEGER, SubmitTimestamp INTEGER, BeginTimestamp INTEGER, EndTimestamp INTEGER);
        INSERT INTO ApiQueueExecution VALUES
          (1, 1, 10, 90, 100, 300), (2, 1, 10, 150, 200, 400), (3, 1, 11, 580, 600, 700),
          (4, 1, 10, 800, 800, 800), (5, 1, 10, 900, 850, 950), (6, 1, 10, 950, NULL, NULL),
          (7, 1, 10, 9990, 9995, 12000), (8, 1, 10, 20000, 20010, 20020),
          (9, 2, 11, 100, 110, 130), (10, 3, 10, 100, 110, 120);
        CREATE TABLE HardwareAdapter(Id INTEGER PRIMARY KEY, NameId INTEGER);
        INSERT INTO HardwareAdapter VALUES(1, 9);
        CREATE TABLE HardwareCommandQueue(Id INTEGER PRIMARY KEY, AdapterId INTEGER, NameId INTEGER);
        INSERT INTO HardwareCommandQueue VALUES(45, 1, 8);
        CREATE TABLE GpuWorkRange(Id INTEGER PRIMARY KEY, HardwareQueueId INTEGER, ApiCommandQueueId INTEGER, BeginTimestamp INTEGER, EndTimestamp INTEGER, OverlapLevel INTEGER, Work BLOB);
        INSERT INTO GpuWorkRange VALUES(1, 45, 1, 100, 400, 0, x'00'), (2, 45, 1, 300, 500, 1, x'00'), (3, 45, 1, 9900, 10100, 0, x'00');
        CREATE TABLE CustomDataTypeInfo(Id INTEGER PRIMARY KEY, NameId INTEGER);
        INSERT INTO CustomDataTypeInfo VALUES(1, 7);
        CREATE TABLE CustomMarkerInfo(Id INTEGER PRIMARY KEY, DataTypeId INTEGER, NameId INTEGER, Count INTEGER);
        INSERT INTO CustomMarkerInfo VALUES(1, 1, 6, 3);
        CREATE TABLE CustomMarker(Id INTEGER PRIMARY KEY, MarkerInfoId INTEGER, Color INTEGER, Timestamp INTEGER);
        INSERT INTO CustomMarker VALUES(1, 1, 0, 1000000), (2, 1, 0, 17666666), (3, 1, 0, 34333333);
        CREATE TABLE CpuGpuExecutionMap(CpuMarkerId INTEGER, GpuEventId INTEGER);
        CREATE TABLE ApiMarkerGpuWorkMap(ApiMarkerId INTEGER, GpuWorkId INTEGER);
        """);

    public void Dispose() => _directory.Delete(recursive: true);

    private void Execute(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private TimingDatabase Open() => new(Path, null, configureForTests: _ => { });

    [Fact]
    public void QueueTotalsUnionOverlapsClipToTheWindowAndCountInvalidSubmissions()
    {
        using TimingDatabase db = Open();
        TimingGpuSummaryDto summary = db.GpuSummary("timing-1", 3, null, null, 0, 10000);
        Assert.Equal((uint)42, summary.ProcessId);
        Assert.Equal(new[] { "1", "2" }, summary.Queues.Select(q => q.QueueId));

        TimingGpuQueueRollupDto graphics = summary.Queues[0];
        Assert.Equal(("Graphics", "Direct", 7L, 4L), (graphics.Name, graphics.Type, graphics.Submissions, graphics.ValidSubmissions));
        Assert.Equal(new Dictionary<string, long> { ["inconsistentTimestamps"] = 1, ["missingTimestamps"] = 1, ["zeroDuration"] = 1 }, graphics.InvalidReasons);

        RecordedLaneTotalsDto totals = graphics.Totals!;
        Assert.Equal((405UL, 9900UL, 9495UL, 505UL), (totals.Busy.Ns, totals.Span.Ns, totals.Idle.Ns, totals.SumOfIntervals.Ns));
        Assert.True(totals.IntervalsOverlap);
        Assert.Equal(("100", "10000"), (totals.FirstStartNs, totals.LastEndNs));
        Assert.Equal(4.05, totals.BusyPercentOfWindow);
        Assert.Equal(4.09, totals.Busy.PercentOfQueueSpan);
        Assert.Equal(80.2, totals.Busy.PercentOfQueueSum);

        Assert.Equal((4L, 21L, 10L, 50L, 50L), (graphics.SubmitLatency!.Count, graphics.SubmitLatency.AvgNs, graphics.SubmitLatency.P50Ns, graphics.SubmitLatency.P95Ns, graphics.SubmitLatency.MaxNs));
        Assert.Equal((626L, 200L, 2005L, 2005L), (graphics.Execution!.AvgNs, graphics.Execution.P50Ns, graphics.Execution.P95Ns, graphics.Execution.MaxNs));

        TimingGpuThreadDto render = graphics.TopSubmittingThreads[0];
        Assert.Equal(("10", (uint?)42, (uint?)7, "Render", 6L, 3L), (render.ThreadRowId, render.ProcessId, render.ThreadId, render.Name, render.Submissions, render.ValidSubmissions));
        Assert.Equal(22L, render.SubmitLatency!.AvgNs);
        Assert.Equal(("11", 1L), (graphics.TopSubmittingThreads[1].ThreadRowId, graphics.TopSubmittingThreads[1].Submissions));

        Assert.Equal(new[] { 2005L, 200L, 200L, 100L }, graphics.LongestExecutions.Select(e => e.DurationNs));
        Assert.Equal(("9990", "9995", 5L), (graphics.LongestExecutions[0].SubmitNs, graphics.LongestExecutions[0].BeginNs, graphics.LongestExecutions[0].LatencyNs));
        Assert.Equal(TimingSubmissionReferences.Create("timing-1", 3, 7), graphics.LongestExecutions[0].SubmissionRef);
        Assert.Equal(TimingSubmissionReferences.Create("timing-1", 3, 1), graphics.LongestExecutions[1].SubmissionRef);

        RecordedLaneTotalsDto compute = summary.Queues[1].Totals!;
        Assert.Equal((20UL, 20UL, 0UL, 20UL, false), (compute.Busy.Ns, compute.Span.Ns, compute.Idle.Ns, compute.SumOfIntervals.Ns, compute.IntervalsOverlap));

        TimingHardwareQueueRollupDto hardware = Assert.Single(summary.HardwareQueues!);
        Assert.Equal(("45", "3D", "Adapter", 3L, 1L), (hardware.HardwareQueueId, hardware.Name, hardware.AdapterName, hardware.WorkRanges, hardware.MaxOverlapLevel));
        Assert.Equal((500UL, 9900UL, 600UL), (hardware.Totals!.Busy.Ns, hardware.Totals.Span.Ns, hardware.Totals.SumOfIntervals.Ns));

        Assert.Empty(summary.Vsync!);
        Assert.Equal(("empty", 0L, 0L), (summary.CpuGpuCausality.State, summary.CpuGpuCausality.CpuGpuExecutionMapRows, summary.CpuGpuCausality.ApiMarkerGpuWorkMapRows));
        Assert.Null(summary.CpuGpuCausality.GpuMarkerRows);
        Assert.Contains(summary.NextCalls, c => c.Tool == "pix_timing_submissions");
        Assert.Contains(summary.NextCalls, c => c.Tool == "pix_timing_thread_switches");
        Assert.Contains(summary.NextCalls, c => c.Tool == "pix_timing_sql");
    }

    [Fact]
    public void FullWindowReportsVsyncAndFiltersSelectProcessesAndQueues()
    {
        using (TimingDatabase db = Open())
        {
            TimingGpuSummaryDto full = db.GpuSummary("timing-1", 0, null, null, null, null);
            Assert.Equal("full", full.Provenance.RangeMode);
            Assert.Equal(8, full.Queues.Single(q => q.QueueId == "1").Submissions);
            TimingVsyncMonitorDto monitor = Assert.Single(full.Vsync!);
            Assert.Equal(("Monitor 1", 2L, 16.667, 60.0), (monitor.Monitor, monitor.Intervals, monitor.AvgMs, monitor.AvgHz));

            Assert.Equal(new[] { "3" }, db.GpuSummary("timing-1", 0, 43, null, null, null).Queues.Select(q => q.QueueId));
            Assert.Equal(new[] { "2" }, db.GpuSummary("timing-1", 0, null, "2", null, null).Queues.Select(q => q.QueueId));
            Assert.Equal("invalid_reference", Assert.Throws<PixToolException>(() => db.GpuSummary("timing-1", 0, null, "3", null, null)).Detail.Code);
            Assert.Equal("invalid_arguments", Assert.Throws<PixToolException>(() => db.GpuSummary("timing-1", 0, null, null, null, null, limit: 0)).Detail.Code);
            Assert.Single(db.GpuSummary("timing-1", 0, null, "1", 0, 10000, limit: 1).Queues[0].LongestExecutions);
        }

        Execute("INSERT INTO CpuGpuExecutionMap VALUES(1, 2)");
        using (TimingDatabase linked = Open())
            Assert.Equal("available", linked.GpuSummary("timing-1", 0, null, null, null, null).CpuGpuCausality.State);

        Execute("DROP TABLE CpuGpuExecutionMap; DROP TABLE ApiMarkerGpuWorkMap; DROP TABLE GpuWorkRange");
        using (TimingDatabase bare = Open())
        {
            TimingGpuSummaryDto summary = bare.GpuSummary("timing-1", 0, null, null, null, null);
            Assert.Equal("unsupported", summary.CpuGpuCausality.State);
            Assert.Null(summary.HardwareQueues);
            Assert.Contains(summary.Unavailable, u => u.StartsWith("hardwareQueues", StringComparison.Ordinal));
        }

        Execute("DROP TABLE ApiQueueExecution");
        using TimingDatabase unsupported = Open();
        Assert.Equal("timing_schema_unsupported", Assert.Throws<PixToolException>(() => unsupported.GpuSummary("timing-1", 0, null, null, null, null)).Detail.Code);
    }

    [Fact]
    public void RecordedIntervalArithmetic()
    {
        Assert.Equal([(0L, 10L), (20L, 40L)], RecordedTiming.Merge([(20L, 30L), (0L, 5L), (5L, 10L), (25L, 40L), (50L, 50L)]));
        Assert.Equal(30, RecordedTiming.UnionLength([(20L, 30L), (0L, 5L), (5L, 10L), (25L, 40L)]));
        List<(long, long)> merged = RecordedTiming.Merge([(0L, 10L), (20L, 30L)]);
        Assert.Equal(10, RecordedTiming.CoveredLength(merged, 5, 25));
        Assert.Equal(0, RecordedTiming.CoveredLength(merged, 10, 20));
        Assert.Equal(20, RecordedTiming.CoveredLength(merged, -5, 100));
        Assert.Equal(50, RecordedTiming.NearestRank([10L, 20L, 30L, 40L, 50L], 95));
        Assert.Equal(30, RecordedTiming.NearestRank([10L, 20L, 30L, 40L, 50L], 50));
        Assert.Null(RecordedTiming.Stats([]));
        Assert.Null(RecordedTiming.Totals([(10L, 20L)], 20, 30));
        Assert.Null(RecordedTiming.Percent(1, 0));
    }

    [SkippableFact]
    public void NativeCaptureSummarizesBothQueues()
    {
        string? capture = TestArtifacts.TimingCapture;
        Skip.If(capture is null || PixDiscovery.InstallDir is null, "Set PIX_TEST_TIMING_CAPTURE to a timing capture.");
        using var db = new TimingDatabase(capture!, System.IO.Path.Combine(PixDiscovery.InstallDir!, "pixstorage.dll"));
        TimingGpuSummaryDto summary = db.GpuSummary("timing-native", 0, null, null, null, null);
        Assert.Equal(2, summary.Queues.Count);
        Assert.All(summary.Queues, q => Assert.Equal(404, q.Submissions));
        TimingGpuQueueRollupDto compute = summary.Queues.Single(q => q.Type == "Compute"), direct = summary.Queues.Single(q => q.Type == "Direct");
        Assert.Equal(404, compute.ValidSubmissions);
        Assert.Equal(263, direct.ValidSubmissions);
        Assert.Equal(141, direct.InvalidReasons["zeroDuration"]);
        Assert.All(summary.Queues, q => Assert.True(q.Totals!.Busy.Ns < q.Totals.Span.Ns));
        Assert.All(summary.Queues, q => Assert.True(q.TopSubmittingThreads[0].Submissions >= 403));
        Assert.Contains(summary.Vsync!, m => m.Intervals == 589);
        Assert.Equal("empty", summary.CpuGpuCausality.State);
        Assert.Equal(0, summary.CpuGpuCausality.GpuMarkerRows);
        int bytes = Encoding.UTF8.GetByteCount(Json.Serialize(summary));
        Assert.True(bytes < 12000, $"gpu summary is {bytes} bytes");
    }
}
