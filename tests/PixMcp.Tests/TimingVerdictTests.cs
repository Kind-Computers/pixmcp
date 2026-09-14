using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using PixMcp.Pix;
using Xunit;

namespace PixMcp.Tests;

/// <summary>
/// pix_timing_verdict over five 100 ns VSync frames built to hit one rule each: cpuBound, gpuBound, syncBound, contended and
/// waitBound, with ready events that split waits into blocked and ready time.
/// </summary>
public sealed class TimingVerdictTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("pixmcp-verdict-");
    private string Path => System.IO.Path.Combine(_directory.FullName, "pixstorage.sqlite");

    public TimingVerdictTests() => Execute("""
        CREATE TABLE CaptureFacts(Id INTEGER PRIMARY KEY, Value INTEGER);
        INSERT INTO CaptureFacts VALUES(2, 0), (3, 1000), (4, 42), (24, 1000);
        CREATE TABLE Strings(Id INTEGER PRIMARY KEY, Value TEXT);
        INSERT INTO Strings VALUES(1, 'Graphics'), (2, 'Render'), (3, 'VSync'), (4, 'Monitor'), (5, 'Frame'), (6, 'Tick');
        CREATE TABLE Processes(Id INTEGER PRIMARY KEY, ProcessId INTEGER);
        INSERT INTO Processes VALUES(1, 42);
        CREATE TABLE Threads(Id INTEGER PRIMARY KEY, ProcThreadId INTEGER, ThreadNameId INTEGER, ProcessRowId INTEGER, StartTimestamp INTEGER, EndTimestamp INTEGER, PixEventCount INTEGER);
        INSERT INTO Threads VALUES(10, (42 << 32) | 7, 2, 1, 0, 1000, 0), (11, (42 << 32) | 8, NULL, 1, 0, 1000, 0);
        CREATE TABLE ApiCommandQueue(Id INTEGER PRIMARY KEY, ProcessId INTEGER, NameId INTEGER, TypeId INTEGER);
        INSERT INTO ApiCommandQueue VALUES(1, 1, 1, NULL);
        CREATE TABLE ApiQueueExecution(Id INTEGER PRIMARY KEY, ApiCommandQueueId INTEGER, ThreadId INTEGER, SubmitTimestamp INTEGER, BeginTimestamp INTEGER, EndTimestamp INTEGER);
        INSERT INTO ApiQueueExecution VALUES(1, 1, 10, 5, 10, 20), (2, 1, 10, 100, 100, 195), (3, 1, 10, 200, 200, 270), (4, 1, 10, 310, 320, 320);
        CREATE TABLE ContextSwitch(Core INTEGER, Timestamp INTEGER, FromProcThreadId INTEGER, ToProcThreadId INTEGER, ReadyThreadId INTEGER, FromThreadPriority INTEGER, ToThreadPriority INTEGER, FromThreadWaitReason INTEGER);
        INSERT INTO ContextSwitch VALUES
          (0, 0, 0, (42 << 32) | 7, 0, 0, 8, 0), (0, 100, (42 << 32) | 7, 0, 0, 8, 0, 6), (0, 195, 0, (42 << 32) | 7, 1, 0, 8, 0),
          (0, 210, (42 << 32) | 7, 0, 0, 8, 0, 6), (0, 260, 0, (42 << 32) | 7, 0, 0, 8, 0), (0, 300, (42 << 32) | 7, 0, 0, 8, 0, 32),
          (0, 340, 0, (42 << 32) | 7, 0, 0, 8, 0), (0, 400, (42 << 32) | 7, 0, 0, 8, 0, 6), (0, 460, 0, (42 << 32) | 7, 2, 0, 8, 0);
        CREATE TABLE ReadyThread(Id INTEGER PRIMARY KEY, Timestamp INTEGER, Core INTEGER, ReadyingThreadRowId INTEGER, AdjustReason INTEGER, AdjustIncrement INTEGER);
        INSERT INTO ReadyThread VALUES(1, 190, 0, 11, 0, 0), (2, 450, 0, 11, 0, 0);
        CREATE TABLE CustomDataTypeInfo(Id INTEGER PRIMARY KEY, NameId INTEGER);
        INSERT INTO CustomDataTypeInfo VALUES(1, 4);
        CREATE TABLE CustomMarkerInfo(Id INTEGER PRIMARY KEY, DataTypeId INTEGER, NameId INTEGER, Count INTEGER);
        INSERT INTO CustomMarkerInfo VALUES(1, 1, 3, 6);
        CREATE TABLE CustomMarker(Id INTEGER PRIMARY KEY, MarkerInfoId INTEGER, Color INTEGER, Timestamp INTEGER);
        INSERT INTO CustomMarker VALUES(1, 1, 0, 0), (2, 1, 0, 100), (3, 1, 0, 200), (4, 1, 0, 300), (5, 1, 0, 400), (6, 1, 0, 500);
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

    private static TimingVerdictDto Verdict(TimingDatabase db, string source = "auto", string? marker = null, string? thread = null, int maxFrames = 600, int limit = 25)
        => db.Verdict("timing-1", null, source, marker, thread, null, null, maxFrames, 0, limit);

    private static JsonElement Arguments(ToolCallDto call) => JsonSerializer.SerializeToElement(call.Arguments);
    private static string Code(Action action) => Assert.Throws<PixToolException>(action).Detail.Code;

    [Fact]
    public void FramesClassifyByGpuBusyAndRenderThreadStates()
    {
        using TimingDatabase db = Open();
        TimingVerdictDto v = Verdict(db);
        Assert.Equal(("vsync", 5L, 5, false), (v.Frames.Source, v.Frames.Available, v.Frames.Analyzed, v.Frames.Truncated));
        Assert.StartsWith("auto: vsync", v.Frames.Selection);
        Assert.Equal(("10", 7u, "Render", 4L, "mostSubmissions"), (v.RenderThread.ThreadRowId, v.RenderThread.ThreadId, v.RenderThread.Name, v.RenderThread.Submissions, v.RenderThread.Selection));
        Assert.Equal(new[] { "cpuBound", "gpuBound", "syncBound", "contended", "waitBound" }, v.PerFrame.Items.Select(f => f.Verdict));

        TimingFrameVerdictDto gpuFrame = v.PerFrame.Items[1];
        Assert.Equal((95.0, 5.0, 90.0, 5.0, 0.0, 1), (gpuFrame.GpuBusyPercent, gpuFrame.OnCpuPercent!.Value, gpuFrame.BlockedPercent!.Value, gpuFrame.ReadyPercent!.Value, gpuFrame.UnknownPercent, gpuFrame.Submissions));
        TimingFrameVerdictDto syncFrame = v.PerFrame.Items[2];
        Assert.Equal((70.0, 50.0, 50.0, 0.0), (syncFrame.GpuBusyPercent, syncFrame.OnCpuPercent!.Value, syncFrame.BlockedPercent!.Value, syncFrame.ReadyPercent!.Value));
        TimingFrameVerdictDto contended = v.PerFrame.Items[3];
        Assert.Equal((60.0, 40.0, 1, (double?)null), (contended.OnCpuPercent!.Value, contended.ReadyPercent!.Value, contended.Submissions, contended.MaxSubmitLatencyMs));
        TimingFrameVerdictDto waitFrame = v.PerFrame.Items[4];
        Assert.Equal((0.0, 40.0, 50.0, 10.0), (waitFrame.GpuBusyPercent, waitFrame.OnCpuPercent!.Value, waitFrame.BlockedPercent!.Value, waitFrame.ReadyPercent!.Value));

        Assert.Equal(("gpuBound", 20.0, "low"), (v.Summary.DominantVerdict, v.Summary.DominantPercent, v.Summary.Confidence));
        Assert.Equal((35.0, 51.0, 38.0, 11.0, 0.0), (v.Summary.GpuBusyPercent!.Value, v.Summary.OnCpuPercent!.Value, v.Summary.BlockedPercent!.Value, v.Summary.ReadyPercent!.Value, v.Summary.UnknownPercent!.Value));
        Assert.Equal((2L, 5L, 10L), (v.Summary.ReadyLatency!.Count, v.Summary.ReadyLatency.P50Ns, v.Summary.ReadyLatency.MaxNs));
        Assert.Equal((5L, 100L), (v.Summary.FrameDuration!.Count, v.Summary.FrameDuration.MaxNs));

        Assert.Equal(("blocked", (int?)6, "UserRequest", 190L, 3L), (v.WaitsByReason[0].State, v.WaitsByReason[0].Code, v.WaitsByReason[0].ProbableName, v.WaitsByReason[0].Ns, v.WaitsByReason[0].Waits));
        Assert.Equal(("readyNotRunning", (int?)32, "WrPreempted", 40L), (v.WaitsByReason[1].State, v.WaitsByReason[1].Code, v.WaitsByReason[1].ProbableName, v.WaitsByReason[1].Ns));
        Assert.Equal(("readyNotRunning", (int?)6, 15L, 2L), (v.WaitsByReason[2].State, v.WaitsByReason[2].Code, v.WaitsByReason[2].Ns, v.WaitsByReason[2].Waits));

        Assert.Equal(("1", "Graphics", 35.0), (v.GpuQueues[0].QueueId, v.GpuQueues[0].Name, v.GpuQueues[0].BusyPercent!.Value));
        Assert.Equal(("available", 9L, 2L, 4L, 3L, 100.0), (v.Coverage.Scheduling, v.Coverage.SwitchEvents, v.Coverage.ReadyLinks, v.Coverage.RenderThreadSubmissions, v.Coverage.GpuIntervals, v.Coverage.KnownStatePercent!.Value));
        Assert.Equal("unavailable", v.Vram.State);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, v.WorstFrames.Select(f => f.Index));
        Assert.True(v.Rules.Heuristic);
        Assert.Equal(new[] { 30, 31, 32, 33, 38 }, v.Rules.RunnableWaitReasons);
        Assert.Equal(VerdictRules.Rules.Select(r => r.Verdict), v.Rules.Rules.Select(r => r.Verdict));

        JsonElement hotspots = Arguments(Assert.Single(v.NextCalls, c => c.Tool == "pix_timing_hotspots"));
        Assert.Equal(("0", "100", 7u), (hotspots.GetProperty("startNs").GetString(), hotspots.GetProperty("endNs").GetString(), hotspots.GetProperty("threadId").GetUInt32()));
        JsonElement switches = Arguments(Assert.Single(v.NextCalls, c => c.Tool == "pix_timing_thread_switches"));
        Assert.Equal(("10", "100", "200"), (switches.GetProperty("threadRowId").GetString(), switches.GetProperty("startNs").GetString(), switches.GetProperty("endNs").GetString()));
        Assert.Contains(v.NextCalls, c => c.Tool == "pix_timing_tree");
        Assert.Contains(v.NextCalls, c => c.Tool == "pix_timing_gpu_summary");
    }

    [Fact]
    public void FrameSourcesPagingTruncationAndErrors()
    {
        using (TimingDatabase db = Open())
        {
            TimingVerdictDto paged = Verdict(db, "vsync", limit: 2);
            Assert.Equal((5L, (int?)2, 2), (paged.PerFrame.Total, paged.PerFrame.NextOffset, paged.PerFrame.Items.Count));
            Assert.Contains(paged.NextCalls, c => c.Tool == "pix_timing_verdict" && Arguments(c).GetProperty("offset").GetInt32() == 2);

            TimingVerdictDto truncated = Verdict(db, "vsync", maxFrames: 2);
            Assert.Equal((2, true, "200", 5L), (truncated.Frames.Analyzed, truncated.Frames.Truncated, truncated.Frames.NextStartNs, truncated.Frames.Available));
            Assert.Contains(truncated.NextCalls, c => c.Tool == "pix_timing_verdict" && Arguments(c).GetProperty("startNs").GetString() == "200");

            TimingVerdictDto submissions = Verdict(db, "submission");
            Assert.Equal(("submission", 3, 3L, "5"), (submissions.Frames.Source, submissions.Frames.Analyzed, submissions.Frames.Available, submissions.PerFrame.Items[0].StartNs));

            TimingVerdictDto quiet = Verdict(db, "vsync", thread: "11");
            Assert.Equal(("explicit", 0L, "unknown", "low"), (quiet.RenderThread.Selection, quiet.RenderThread.Submissions, quiet.Summary.DominantVerdict, quiet.Summary.Confidence));
            Assert.Equal((100.0, "gpuBound"), (quiet.PerFrame.Items[0].UnknownPercent, quiet.PerFrame.Items[1].Verdict));

            Assert.Equal("invalid_arguments", Code(() => Verdict(db, "bogus")));
            Assert.Equal("invalid_arguments", Code(() => Verdict(db, "vsync", maxFrames: 1)));
            Assert.Equal("invalid_arguments", Code(() => Verdict(db, "vsync", marker: "Frame")));
            Assert.Equal("invalid_reference", Code(() => Verdict(db, "vsync", thread: "99")));
            Assert.Equal("timing_schema_unsupported", Code(() => Verdict(db, "present")));
            Assert.Equal("timing_schema_unsupported", Code(() => Verdict(db, "cpuMarker")));
        }

        Execute("""
            CREATE TABLE PixEventInfo(Id INTEGER PRIMARY KEY, NameId INTEGER);
            INSERT INTO PixEventInfo VALUES(1, 5), (2, 6);
            CREATE TABLE PixCpuExecution(BeginTimestamp INTEGER, EndTimestamp INTEGER, Level INTEGER, ThreadRowId INTEGER, EventId INTEGER, Color INTEGER);
            INSERT INTO PixCpuExecution VALUES(0, 90, 0, 10, 1, 0), (10, 20, 1, 10, 2, 0), (100, 190, 0, 10, 1, 0), (200, 290, 0, 10, 1, 0), (0, 1000, 0, 11, 2, 0);
            """);
        using (TimingDatabase marked = Open())
        {
            TimingVerdictDto cpu = Verdict(marked);
            Assert.Equal(("cpuMarker", 3, 3L, "Frame (level 0)"), (cpu.Frames.Source, cpu.Frames.Analyzed, cpu.Frames.Available, cpu.Frames.Detail));
            Assert.Equal(new[] { 100L, 100L, 90L }, cpu.PerFrame.Items.Select(f => f.FrameNs));
            Assert.Equal(3, Verdict(marked, "cpuMarker", marker: "Frame").Frames.Analyzed);
            Assert.Equal("timing_schema_unsupported", Code(() => Verdict(marked, "cpuMarker", marker: "Tick")));
        }

        Execute("""
            CREATE TABLE GpuFrame(Id INTEGER PRIMARY KEY, PresentCallTime INTEGER, PresentReturnTime INTEGER, VSyncTime INTEGER, GPUBusyDuration INTEGER);
            INSERT INTO GpuFrame VALUES(1, 0, 5, 60, 0), (2, 100, 105, 110, 0), (3, 200, 205, 210, 0), (4, NULL, NULL, NULL, NULL);
            """);
        using TimingDatabase presented = Open();
        TimingVerdictDto present = Verdict(presented);
        Assert.Equal(("present", 2, 2L), (present.Frames.Source, present.Frames.Analyzed, present.Frames.Available));
        Assert.Equal(new[] { "presentBound", "gpuBound" }, present.PerFrame.Items.Select(f => f.Verdict));
    }

    [Fact]
    public void MissingContextSwitchesLeaveStatesUnknown()
    {
        Execute("DROP TABLE ContextSwitch");
        using TimingDatabase db = Open();
        TimingVerdictDto blind = Verdict(db, "vsync");
        Assert.Equal("unavailable", blind.Coverage.Scheduling);
        Assert.Null(blind.PerFrame.Items[0].OnCpuPercent);
        Assert.Null(blind.Summary.OnCpuPercent);
        Assert.Equal(("unknown", "gpuBound"), (blind.PerFrame.Items[0].Verdict, blind.PerFrame.Items[1].Verdict));
    }

    [Fact]
    public void VerdictUsesTrailingReadinessWithoutCountingFutureSwitches()
    {
        Execute("UPDATE ContextSwitch SET Timestamp=220 WHERE Timestamp=195;");
        using TimingDatabase db = Open();
        TimingVerdictDto verdict = Verdict(db, "vsync", maxFrames: 2);
        TimingFrameVerdictDto last = verdict.PerFrame.Items[1];
        Assert.Equal((0.0, 90.0, 10.0), (last.OnCpuPercent!.Value, last.BlockedPercent!.Value, last.ReadyPercent!.Value));
        Assert.Equal((2L, 0L), (verdict.Coverage.SwitchEvents, verdict.Coverage.ReadyLinks));
        Assert.Null(verdict.Summary.ReadyLatency);
    }

    [Theory]
    [InlineData(null, 20, 0)]
    [InlineData(70L, 10, 10)]
    [InlineData(90L, 20, 0)]
    public void TrailingReadinessOnlySplitsTheClosingWait(long? readyAt, long blocked, long ready)
    {
        RecordedThreadStates states = RecordedThreadStates.Build([new(0, true), new(60, false, 6)], 80, readyAt);
        RecordedStateTotals totals = states.Integrate(0, 100);
        Assert.Equal((60L, blocked, ready), (totals.OnCpuNs, totals.BlockedNs, totals.ReadyNs));
        Assert.All(states.Segments, s => Assert.True(s.End <= 80));
        RecordedStateTotals running = RecordedThreadStates.Build([new(0, true)], 80, readyAt).Integrate(0, 100);
        Assert.Equal((80L, 0L, 0L), (running.OnCpuNs, running.BlockedNs, running.ReadyNs));
    }

    [Fact]
    public void ThreadStatesSplitWaitsAtTheReadyEvent()
    {
        RecordedThreadStates states = RecordedThreadStates.Build(
        [
            new(0, true), new(10, false, 6), new(30, true, null, 25), new(40, false, 32), new(50, true), new(60, false, 6), new(70, true, null, 55), new(80, true),
        ], 100);
        Assert.Equal(
            [(0L, 10L, RecordedThreadState.OnCpu), (10L, 25L, RecordedThreadState.Blocked), (25L, 30L, RecordedThreadState.Ready), (30L, 40L, RecordedThreadState.OnCpu),
             (40L, 50L, RecordedThreadState.Ready), (50L, 60L, RecordedThreadState.OnCpu), (60L, 70L, RecordedThreadState.Ready), (70L, 100L, RecordedThreadState.OnCpu)],
            states.Segments.Select(s => (s.Start, s.End, s.State)));
        RecordedStateTotals window = states.Integrate(5, 45);
        Assert.Equal((15L, 15L, 10L), (window.OnCpuNs, window.BlockedNs, window.ReadyNs));
        Assert.Equal((15L, 1L), window.ByReason[(RecordedThreadState.Blocked, 6)]);
        Assert.Equal("WrAlertByThreadId", WaitReasons.ProbableName(37));
        Assert.Null(WaitReasons.ProbableName(99));
        Assert.Equal("cpuBound", VerdictRules.Classify(10, 85, 5, 0, 0));
        Assert.Equal("balanced", VerdictRules.Classify(50, 50, 10, 10, 0));
    }

    [SkippableFact]
    public void NativeCaptureFramesAreCpuBound()
    {
        string? capture = TestArtifacts.TimingCapture;
        Skip.If(capture is null || PixDiscovery.InstallDir is null, "Set PIX_TEST_TIMING_CAPTURE to a timing capture.");
        using var db = new TimingDatabase(capture!, System.IO.Path.Combine(PixDiscovery.InstallDir!, "pixstorage.dll"));
        var timer = Stopwatch.StartNew();
        TimingVerdictDto verdict = db.Verdict("timing-native", null, "auto", null, null, null, null, 600, 0, 5);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), $"verdict took {timer.Elapsed}");
        Assert.Equal(("cpuMarker", 434, "10067"), (verdict.Frames.Source, verdict.Frames.Analyzed, verdict.RenderThread.ThreadRowId));
        Assert.Contains("Fixture Frame", verdict.Frames.Detail);
        Assert.Equal("cpuBound", verdict.Summary.DominantVerdict);
        Assert.True(verdict.Summary.OnCpuPercent > 90, $"onCpu {verdict.Summary.OnCpuPercent}");
        Assert.All(verdict.WorstFrames.Concat(verdict.PerFrame.Items), f => Assert.InRange(f.GpuBusyPercent, 0.0, 100.0));
        TimingVerdictDto vsync = db.Verdict("timing-native", null, "vsync", null, null, null, null, 600, 0, 10);
        Assert.Equal((589, "cpuBound"), (vsync.Frames.Analyzed, vsync.Summary.DominantVerdict));
        string json = Json.Serialize(verdict);
        int bytes = Encoding.UTF8.GetByteCount(json);
        string breakdown = string.Join(", ", JsonNode.Parse(json)!.AsObject().Select(p => $"{p.Key}={Encoding.UTF8.GetByteCount(p.Value?.ToJsonString() ?? "null")}"));
        Assert.True(bytes < 8192, $"verdict is {bytes} bytes: {breakdown}");
    }
}
