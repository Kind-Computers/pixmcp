using System.Text.Json;
using Microsoft.Data.Sqlite;
using PixMcp.Pix;
using Xunit;

namespace PixMcp.Tests;

/// <summary>pix_timing_tree over Frame(0..100){Shadow, Lighting{Sun}} and Frame(100..200){Shadow, Late(150..220)} on one thread plus a GPU marker lane.</summary>
public sealed class TimingMarkerTreeTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("pixmcp-marker-tree-");
    private string Path => System.IO.Path.Combine(_directory.FullName, "pixstorage.sqlite");

    public TimingMarkerTreeTests() => Execute("""
        CREATE TABLE CaptureFacts(Id INTEGER PRIMARY KEY, Value INTEGER);
        INSERT INTO CaptureFacts VALUES(2, 0), (3, 1000), (4, 42), (24, 500);
        CREATE TABLE Strings(Id INTEGER PRIMARY KEY, Value TEXT);
        INSERT INTO Strings VALUES(1, 'Frame'), (2, 'Shadow'), (3, 'Lighting'), (4, 'Sun'), (5, 'Late'), (6, 'Render'), (7, 'Graphics'), (8, 'GpuPass');
        CREATE TABLE Processes(Id INTEGER PRIMARY KEY, ProcessId INTEGER);
        INSERT INTO Processes VALUES(1, 42);
        CREATE TABLE Threads(Id INTEGER PRIMARY KEY, ProcThreadId INTEGER, ThreadNameId INTEGER, ProcessRowId INTEGER);
        INSERT INTO Threads VALUES(10, (42 << 32) | 7, 6, 1), (11, (42 << 32) | 8, NULL, 1);
        CREATE TABLE PixEventInfo(Id INTEGER PRIMARY KEY, NameId INTEGER);
        INSERT INTO PixEventInfo VALUES(1, 1), (2, 2), (3, 3), (4, 4), (5, 5), (6, 8);
        CREATE TABLE PixCpuExecution(BeginTimestamp INTEGER, EndTimestamp INTEGER, Level INTEGER, ThreadRowId INTEGER, EventId INTEGER, Color INTEGER);
        INSERT INTO PixCpuExecution(rowid, BeginTimestamp, EndTimestamp, Level, ThreadRowId, EventId, Color) VALUES
          (1, 0, 100, 0, 10, 1, 0), (2, 10, 40, 1, 10, 2, 0), (3, 40, 90, 1, 10, 3, 0), (4, 45, 60, 2, 10, 4, 0),
          (5, 100, 200, 0, 10, 1, 0), (6, 110, 130, 1, 10, 2, 0), (7, 150, 220, 1, 10, 5, 0),
          (8, 0, 50, 0, 11, 1, 0);
        CREATE TABLE PixCpuExecutionTimes(Duration INTEGER, Execution INTEGER, Stall INTEGER, EventId INTEGER, BeginTimestamp INTEGER, EndTimestamp INTEGER, CpuExecutionRowId INTEGER);
        INSERT INTO PixCpuExecutionTimes VALUES(100, 80, 20, 1, 0, 100, 1), (100, 90, 10, 1, 100, 200, 5), (15, 10, 6, 4, 45, 60, 4);
        CREATE TABLE ApiCommandQueue(Id INTEGER PRIMARY KEY, ProcessId INTEGER, NameId INTEGER);
        INSERT INTO ApiCommandQueue VALUES(1, 1, 7);
        CREATE TABLE PixGpuExecution(BeginTimestamp INTEGER, EndTimestamp INTEGER, Level INTEGER, ApiCommandQueueId INTEGER, EventId INTEGER, Color INTEGER);
        INSERT INTO PixGpuExecution VALUES(300, 340, 0, 1, 6, 0), (360, 380, 0, 1, 6, 0);
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

    private static RecordedMarkerTreeDto Thread(TimingDatabase db, string sortBy = "inclusive", int depth = 2, long? minSelfNs = null, string? parentPath = null,
        long? start = null, long? end = null, int limit = 25, bool includeExecution = true)
        => db.MarkerTree("timing-1", "10", null, parentPath, depth, sortBy, minSelfNs, start, end, 0, limit, includeExecution);

    private static string[] Paths(RecordedMarkerTreeDto tree) => tree.Nodes.Items.Select(n => n.Path).ToArray();
    private static JsonElement Arguments(ToolCallDto call) => JsonSerializer.SerializeToElement(call.Arguments);
    private static string Code(Action action) => Assert.Throws<PixToolException>(action).Detail.Code;

    [Fact]
    public void PathsAggregateOccurrencesWithClippedSelfTimeAndOverflow()
    {
        using TimingDatabase db = Open();
        RecordedMarkerTreeDto tree = Thread(db);
        Assert.Equal(("cpu", "thread", "Render", (uint?)42, (uint?)7), (tree.Domain, tree.Lane.Kind, tree.Lane.Name, tree.Lane.ProcessId, tree.Lane.ThreadId));
        Assert.Equal((7L, 2L, 0L, 1L), (tree.Lane.Events, tree.Lane.Roots, tree.Lane.Orphans, tree.Lane.MalformedNestings));
        RecordedLaneTotalsDto lane = tree.Lane.Totals!;
        Assert.Equal((200UL, 200UL, 0UL, 200UL, false), (lane.Busy.Ns, lane.Span.Ns, lane.Idle.Ns, lane.SumOfIntervals.Ns, lane.IntervalsOverlap));
        Assert.Equal(new[] { "Frame", "Frame/Late", "Frame/Shadow", "Frame/Lighting" }, Paths(tree));

        RecordedTreeNodeDto frame = tree.Nodes.Items[0];
        Assert.Equal((1, 2L, 200UL, 50UL, 170L, true, 20L), (frame.Depth, frame.Occurrences, frame.Inclusive.Ns, frame.Self.Ns, frame.ChildSumNs, frame.ChildrenExceedMeasured, frame.ChildOverflowNs));
        Assert.Equal((100.0, 100.0, (double?)null, (int?)1), (frame.Inclusive.PercentOfQueueSpan!.Value, frame.Inclusive.PercentOfQueueSum!.Value, frame.Inclusive.PercentOfParent, frame.Inclusive.Rank));
        Assert.Equal((170L, 30L, "available"), (frame.ExecutionNs!.Value, frame.StallNs!.Value, frame.ExecutionTiming));
        Assert.Equal((2L, 100L, 100L), (frame.OccurrenceDuration.Count, frame.OccurrenceDuration.P50Ns, frame.OccurrenceDuration.MaxNs));
        Assert.Equal(("0", "100", 3), (frame.SlowestStartNs, frame.SlowestEndNs, frame.ChildPaths));

        RecordedTreeNodeDto late = tree.Nodes.Items[1];
        Assert.Equal((2, 70UL, 70UL, 35.0, "unavailable"), (late.Depth, late.Inclusive.Ns, late.Self.Ns, late.Inclusive.PercentOfParent!.Value, late.ExecutionTiming));
        RecordedTreeNodeDto lighting = tree.Nodes.Items[3];
        Assert.Equal((50UL, 35UL, 1, 3), (lighting.Inclusive.Ns, lighting.Self.Ns, lighting.ChildPaths, lighting.Inclusive.Rank!.Value));
        Assert.Contains("1 occurrence", tree.ExecutionTiming.Reason);

        Assert.Contains(tree.NextCalls, c => c.Tool == "pix_timing_tree" && Arguments(c).GetProperty("parentPath").GetString() == "Frame/Lighting");
        JsonElement hotspots = Arguments(Assert.Single(tree.NextCalls, c => c.Tool == "pix_timing_hotspots"));
        Assert.Equal(("0", "100", 7u), (hotspots.GetProperty("startNs").GetString(), hotspots.GetProperty("endNs").GetString(), hotspots.GetProperty("threadId").GetUInt32()));
        Assert.Contains(tree.NextCalls, c => c.Tool == "pix_timing_thread_switches");
    }

    [Fact]
    public void SortingFilteringParentPathsWindowsAndLanes()
    {
        using TimingDatabase db = Open();
        Assert.Equal(new[] { "Frame", "Frame/Late", "Frame/Lighting", "Frame/Shadow" }, Paths(Thread(db, sortBy: "name")));
        Assert.Equal(new[] { "Frame", "Frame/Shadow", "Frame/Late", "Frame/Lighting" }, Paths(Thread(db, sortBy: "occurrences")));
        Assert.Equal(new[] { "Frame", "Frame/Late", "Frame/Shadow", "Frame/Lighting" }, Paths(Thread(db, sortBy: "SELF")));
        Assert.Equal(new[] { "Frame", "Frame/Late", "Frame/Shadow" }, Paths(Thread(db, minSelfNs: 40)));
        Assert.Equal(new[] { "Frame", "Frame/Late", "Frame/Shadow", "Frame/Lighting", "Frame/Lighting/Sun" }, Paths(Thread(db, depth: 3)));

        RecordedTreeNodeDto sun = Assert.Single(Thread(db, depth: 1, parentPath: "Frame/Lighting").Nodes.Items);
        Assert.Equal(("Frame/Lighting/Sun", 3, 30.0, "unavailable"), (sun.Path, sun.Depth, sun.Inclusive.PercentOfParent!.Value, sun.ExecutionTiming));

        RecordedTreeNodeDto early = Thread(db, depth: 1, start: 0, end: 150).Nodes.Items[0];
        Assert.Equal((150UL, 50UL, 170L), (early.Inclusive.Ns, early.Self.Ns, early.ExecutionNs!.Value));

        RecordedMarkerTreeDto paged = Thread(db, limit: 2);
        Assert.Equal((4L, (int?)2), (paged.Nodes.Total, paged.Nodes.NextOffset));
        Assert.Contains(paged.NextCalls, c => c.Tool == "pix_timing_tree" && Arguments(c).TryGetProperty("offset", out JsonElement o) && o.GetInt32() == 2);

        RecordedMarkerTreeDto skipped = Thread(db, depth: 1, includeExecution: false);
        Assert.Equal(("skipped", "skipped"), (skipped.ExecutionTiming.State, skipped.Nodes.Items[0].ExecutionTiming));
        Assert.Null(skipped.Nodes.Items[0].ExecutionNs);

        RecordedMarkerTreeDto other = db.MarkerTree("timing-1", "11", null, null, 2, "inclusive", null, null, null, 0, 25, true);
        Assert.Equal(("Frame", 1L, (string?)null), (other.Nodes.Items[0].Name, other.Nodes.Items[0].Occurrences, other.Lane.Name));

        RecordedMarkerTreeDto gpu = db.MarkerTree("timing-1", null, "1", null, 2, "inclusive", null, null, null, 0, 25, true);
        RecordedTreeNodeDto pass = Assert.Single(gpu.Nodes.Items);
        Assert.Equal(("gpuMarkers", "queue", "Graphics", (uint?)42, "GpuPass", 2L, 60UL, "notApplicable"),
            (gpu.Domain, gpu.Lane.Kind, gpu.Lane.Name, gpu.Lane.ProcessId, pass.Name, pass.Occurrences, pass.Inclusive.Ns, pass.ExecutionTiming));
        Assert.Equal("notApplicable", gpu.ExecutionTiming.State);
        Assert.Contains(gpu.NextCalls, c => c.Tool == "pix_timing_submissions");

        Assert.Equal("invalid_arguments", Code(() => db.MarkerTree("timing-1", "10", "1", null, 2, "inclusive", null, null, null, 0, 25, true)));
        Assert.Equal("invalid_arguments", Code(() => db.MarkerTree("timing-1", null, null, null, 2, "inclusive", null, null, null, 0, 25, true)));
        Assert.Equal("invalid_arguments", Code(() => Thread(db, depth: 9)));
        Assert.Equal("invalid_arguments", Code(() => Thread(db, sortBy: "bogus")));
        Assert.Equal("invalid_arguments", Code(() => Thread(db, minSelfNs: -1)));
        Assert.Equal("invalid_reference", Code(() => db.MarkerTree("timing-1", "99", null, null, 2, "inclusive", null, null, null, 0, 25, true)));
        Assert.Equal("invalid_reference", Code(() => Thread(db, parentPath: "Frame/Nope")));
    }

    [Fact]
    public void MissingRowIdLookupIsUnsupportedAndOrphansAreCounted()
    {
        Execute("ALTER TABLE PixCpuExecutionTimes DROP COLUMN CpuExecutionRowId");
        using (TimingDatabase db = Open())
            Assert.Equal("unsupported", db.MarkerTree("timing-1", "11", null, null, 1, "inclusive", null, null, null, 0, 25, true).ExecutionTiming.State);

        RecordedMarkerTree tree = RecordedMarkerTree.Build([new(1, 0, 10, 0, "A"), new(2, 20, 30, 2, "Deep"), new(3, 40, 50, 0, "A")], 0, 100);
        Assert.Equal((1L, 3), (tree.Orphans, tree.RootIntervals.Count));
        Assert.Equal(2, tree.Find("A")!.Occurrences);
        Assert.Equal(1, tree.Find("Deep")!.Depth);
        RecordedMarkerTree closing = RecordedMarkerTree.Build([new(1, 0, 10, 0, "P"), new(2, 10, 10, 1, "C"), new(3, 10, 20, 1, "Next")], 0, 100);
        Assert.Equal((1L, 0L), (closing.Orphans, closing.MalformedNestings)); // "Next" is level 1 but starts after P closed.
        Assert.Equal(2, closing.Find("P/C")!.Depth);
        Assert.Equal(1, closing.Find("Next")!.Depth);

        Execute("DROP TABLE PixCpuExecution");
        using TimingDatabase missing = Open();
        Assert.Equal("timing_schema_unsupported", Code(() => Thread(missing)));
    }

    [SkippableFact]
    public void NativeRenderThreadTreeNestsFrameWork()
    {
        string? capture = TestArtifacts.TimingCapture;
        Skip.If(capture is null || PixDiscovery.InstallDir is null, "Set PIX_TEST_TIMING_CAPTURE to a timing capture.");
        using var db = new TimingDatabase(capture!, System.IO.Path.Combine(PixDiscovery.InstallDir!, "pixstorage.dll"));
        RecordedMarkerTreeDto tree = db.MarkerTree("timing-native", "10067", null, null, 2, "inclusive", null, null, null, 0, 25, true);
        Assert.Equal((868L, 434L, 0L, 0L), (tree.Lane.Events, (long)tree.Lane.Roots, tree.Lane.Orphans, tree.Lane.MalformedNestings));
        Assert.Equal("available", tree.ExecutionTiming.State);
        RecordedTreeNodeDto frame = tree.Nodes.Items[0];
        Assert.Equal(("Fixture Frame", 434L, "available"), (frame.Name, frame.Occurrences, frame.ExecutionTiming));
        Assert.Equal((long)frame.Inclusive.Ns, frame.ExecutionNs!.Value + frame.StallNs!.Value);
        Assert.True(frame.Self.Ns > 0 && frame.Self.Ns < frame.Inclusive.Ns);
        RecordedTreeNodeDto work = Assert.Single(tree.Nodes.Items, n => n.Depth == 2);
        Assert.Equal(("Fixture Frame/Fixture CPU Work", 434L), (work.Path, work.Occurrences));
        RecordedMarkerTreeDto queue = db.MarkerTree("timing-native", null, "2", null, 2, "inclusive", null, null, null, 0, 25, true);
        Assert.Empty(queue.Nodes.Items);
        Assert.Contains(queue.NextCalls, c => c.Tool == "pix_timing_gpu_summary");
    }
}
