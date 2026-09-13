using System.Text.Json;
using Microsoft.Data.Sqlite;
using PixMcp.Pix;
using PixMcp.Pix.Handles;
using Xunit;

namespace PixMcp.Tests;

/// <summary>pix_correlate: name normalization and matching, queue mapping, and the recorded side over a fixture with a synthetic GPU snapshot.</summary>
public sealed class TimingCorrelationTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("pixmcp-correlate-");
    private string Path => System.IO.Path.Combine(_directory.FullName, "pixstorage.sqlite");

    public TimingCorrelationTests() => Execute("""
        CREATE TABLE CaptureFacts(Id INTEGER PRIMARY KEY, Value INTEGER);
        INSERT INTO CaptureFacts VALUES(2, 0), (3, 1000), (4, 42), (24, 1000);
        CREATE TABLE Strings(Id INTEGER PRIMARY KEY, Value TEXT);
        INSERT INTO Strings VALUES(1, 'Frame'), (2, 'Shadow'), (3, 'Lighting'), (4, 'Post 2'), (5, 'Unmatched Cpu'), (6, 'Render'), (7, 'Graphics'), (8, 'Direct');
        CREATE TABLE Processes(Id INTEGER PRIMARY KEY, ProcessId INTEGER);
        INSERT INTO Processes VALUES(1, 42);
        CREATE TABLE Threads(Id INTEGER PRIMARY KEY, ProcThreadId INTEGER, ThreadNameId INTEGER, ProcessRowId INTEGER, StartTimestamp INTEGER, EndTimestamp INTEGER);
        INSERT INTO Threads VALUES(10, (42 << 32) | 7, 6, 1, 0, 1000);
        CREATE TABLE PixEventInfo(Id INTEGER PRIMARY KEY, NameId INTEGER);
        INSERT INTO PixEventInfo VALUES(1, 1), (2, 2), (3, 3), (4, 4), (5, 5);
        CREATE TABLE PixCpuExecution(BeginTimestamp INTEGER, EndTimestamp INTEGER, Level INTEGER, ThreadRowId INTEGER, EventId INTEGER, Color INTEGER);
        INSERT INTO PixCpuExecution VALUES(0, 100, 0, 10, 1, 0), (10, 40, 1, 10, 2, 0), (40, 90, 1, 10, 3, 0), (100, 200, 0, 10, 1, 0), (110, 130, 1, 10, 2, 0),
          (300, 310, 0, 10, 4, 0), (400, 450, 0, 10, 5, 0);
        CREATE TABLE ApiCommandQueue(Id INTEGER PRIMARY KEY, ProcessId INTEGER, NameId INTEGER, TypeId INTEGER);
        INSERT INTO ApiCommandQueue VALUES(1, 1, 7, 8);
        CREATE TABLE ApiQueueExecution(Id INTEGER PRIMARY KEY, ApiCommandQueueId INTEGER, ThreadId INTEGER, SubmitTimestamp INTEGER, BeginTimestamp INTEGER, EndTimestamp INTEGER);
        INSERT INTO ApiQueueExecution VALUES(1, 1, 10, 20, 25, 30), (2, 1, 10, 50, 55, 60), (3, 1, 10, 120, 125, 130), (4, 1, 10, 500, 505, 510);
        CREATE TABLE ContextSwitch(Core INTEGER, Timestamp INTEGER, FromProcThreadId INTEGER, ToProcThreadId INTEGER, ReadyThreadId INTEGER, FromThreadPriority INTEGER, ToThreadPriority INTEGER, FromThreadWaitReason INTEGER);
        INSERT INTO ContextSwitch VALUES(0, 0, 0, (42 << 32) | 7, 0, 0, 8, 0), (0, 60, (42 << 32) | 7, 0, 0, 8, 0, 6), (0, 80, 0, (42 << 32) | 7, 1, 0, 8, 0);
        CREATE TABLE ReadyThread(Id INTEGER PRIMARY KEY, Timestamp INTEGER, Core INTEGER, ReadyingThreadRowId INTEGER, AdjustReason INTEGER, AdjustIncrement INTEGER);
        INSERT INTO ReadyThread VALUES(1, 70, 0, 0, 0, 0);
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
    private static JsonElement Arguments(ToolCallDto call) => JsonSerializer.SerializeToElement(call.Arguments);
    private static CorrelationKey Key(params string[] segments) => TimingCorrelation.Key(segments);

    private static GpuCorrelationSnapshot Snapshot() => new("gpu-1",
        [
            new(["<deprecated - use pix3.h instead> Frame", "Lighting"], 0, 7, 2, 400, "measured"),
            new(["<deprecated - use pix3.h instead> Frame", "Shadow"], 0, 3, 2, 100, "measured"),
            new(["Frame", "Post 7"], 0, 9, 1, 50, "measured"),
            new(["GpuOnly"], 1, 2, 1, 10, "derivedSum"),
        ],
        [new(0, "Graphics", "GRAPHICS"), new(1, "Compute", "COMPUTE")], 6, false, null,
        new ReplayProvenance("replay", "2606.18", null, null, null, "semantics"));

    [Theory]
    [InlineData("<deprecated - use pix3.h instead> Frame", "frame", "legacyPixPrefix")]
    [InlineData("Shadow Cascade 3", "shadow cascade", "trailingNumber")]
    [InlineData("  Main \t Pass ", "main pass", "")]
    [InlineData("Pass#12", "pass", "trailingNumber")]
    [InlineData("42", "42", "")]
    public void SegmentsNormalize(string name, string key, string normalization)
    {
        var (actual, applied) = TimingCorrelation.NormalizeSegment(name);
        Assert.Equal(key, actual);
        Assert.Equal(normalization.Length == 0 ? Array.Empty<string>() : new[] { normalization }, applied);
    }

    [Fact]
    public void PathsMatchByFullPathThenUniqueLeafAndQueuesByTypeAndName()
    {
        CorrelationKey[] gpu = [Key("<deprecated - use pix3.h instead> Frame", "Shadow"), Key("Frame", "Lighting"), Key("Frame", "Post 1"), Key("Other", "Unique"), Key("A", "Dup"), Key("B", "Dup")];
        CorrelationKey[] recorded = [Key("Frame", "Shadow"), Key("Tick", "Lighting"), Key("Loop", "Post 2"), Key("X", "Solo")];
        var (matches, unmatchedGpu, unmatchedRecorded) = TimingCorrelation.Match(gpu, recorded);
        Assert.Equal(new[] { (0, 0, "pathMatch"), (1, 1, "nameMatch"), (2, 2, "nameMatch") }, matches.Select(m => (m.GpuIndex, m.RecordedIndex, m.Method)));
        Assert.Equal(new[] { "legacyPixPrefix" }, matches[0].Normalizations);
        Assert.Equal(new[] { "trailingNumber" }, matches[2].Normalizations);
        Assert.Equal(new[] { 3, 4, 5 }, unmatchedGpu);
        Assert.Equal(new[] { 3 }, unmatchedRecorded);

        List<CorrelationQueueMapDto> map = TimingCorrelation.MapQueues([(0, "Graphics Queue 0 (Main Graphics Queue)", "GRAPHICS"), (1, "Async Compute Queue", "COMPUTE"), (2, "Loader", "COPY")],
            [("1", "Async Compute Queue", "Compute"), ("2", "Main Graphics Queue", "Direct"), ("3", "Other Compute", "Compute")]);
        Assert.Equal(new[] { ("2", "typeAndName", "medium"), ("1", "typeAndName", "medium"), ((string?)null, "none", "none") }, map.Select(m => (m.RecordedQueueId, m.Method, m.Confidence)));
    }

    [Fact]
    public void RecordedSideReportsOccurrencesSubmissionsWaitsAndUnmatchedPaths()
    {
        using TimingDatabase db = Open();
        CorrelationDto result = db.Correlate("timing-1", Snapshot(), null, null, null, "full", 0, 25, next => new { offset = next });
        Assert.Equal(TimingCorrelation.Identity, result.Identity);
        CorrelationCountsDto counts = result.Counts;
        Assert.Equal((4, 6, 5, 7L, 3, 2, 1, 1, 2), (counts.GpuPaths, counts.GpuTimedMarkers, counts.RecordedPaths, counts.RecordedEvents, counts.Matched, counts.PathMatches, counts.NameMatches, counts.UnmatchedGpu, counts.UnmatchedRecorded));

        CorrelationRowDto lighting = result.Matches.Items[0];
        Assert.Equal(("<deprecated - use pix3.h instead> Frame/Lighting", "Frame/Lighting", "cpu", "pathMatch", "medium"), (lighting.GpuPath, lighting.RecordedPath, lighting.RecordedDomain, lighting.Method, lighting.Confidence));
        Assert.Equal(new[] { "legacyPixPrefix" }, lighting.Normalizations);
        Assert.Equal((1L, 50L, (long?)1, (long?)10, (long?)10, (double?)0.25), (lighting.RecordedOccurrences, lighting.RecordedInclusiveNs, lighting.SubmissionsInOccurrences, lighting.BlockedNs, lighting.ReadyNs, lighting.RatioRecordedToReplay));
        Assert.Equal((2, 400UL), (lighting.GpuOccurrences, lighting.GpuInclusiveEopNs));
        Assert.Equal(new[] { "threadRowId 10" }, lighting.RecordedLanes);
        Assert.Equal(new EventRef("gpu-1", 0, 7), lighting.GpuEvent);
        Assert.Contains(lighting.NextCalls, c => c.Tool == "pix_timing_tree" && Arguments(c).GetProperty("parentPath").GetString() == "Frame/Lighting");
        Assert.Contains(lighting.NextCalls, c => c.Tool == "pix_gpu_inspect_event");

        CorrelationRowDto shadow = result.Matches.Items[1];
        Assert.Equal((2L, 25L, (long?)2, (long?)0, (long?)0, (double?)0.5), (shadow.RecordedOccurrences, shadow.RecordedOccurrence.AvgNs, shadow.SubmissionsInOccurrences, shadow.BlockedNs, shadow.ReadyNs, shadow.RatioRecordedToReplay));
        CorrelationRowDto post = result.Matches.Items[2];
        Assert.Equal(("Post 2", "nameMatch", "low", (long?)0, (double?)0.2), (post.RecordedPath, post.Method, post.Confidence, post.SubmissionsInOccurrences, post.RatioRecordedToReplay));
        Assert.Equal(new[] { "trailingNumber" }, post.Normalizations);

        Assert.Equal(new[] { "GpuOnly" }, result.UnmatchedGpu.Select(u => u.Path));
        Assert.Equal(new EventRef("gpu-1", 1, 2), result.UnmatchedGpu[0].GpuEvent);
        Assert.Equal(new[] { "Frame", "Unmatched Cpu" }, result.UnmatchedRecorded.Select(u => u.Path));
        Assert.Equal(("1", "typeAndName", "none"), (result.QueueMap[0].RecordedQueueId, result.QueueMap[0].Method, result.QueueMap[1].Method));

        CorrelationDto paged = db.Correlate("timing-1", Snapshot(), null, null, null, "full", 0, 2, next => new { offset = next });
        Assert.Equal((3L, (int?)2), (paged.Matches.Total, paged.Matches.NextOffset));
        Assert.Contains(paged.NextCalls, c => c.Tool == "pix_correlate" && Arguments(c).GetProperty("offset").GetInt32() == 2);

        GpuCorrelationSnapshot unrelated = Snapshot() with { Paths = [new(["Nothing"], 0, 1, 1, 5, "measured")] };
        CorrelationDto none = db.Correlate("timing-1", unrelated, null, null, null, "full", 0, 25);
        Assert.Equal(0, none.Counts.Matched);
        Assert.Contains(none.NextCalls, c => c.Tool == "pix_gpu_timing_tree");
        Assert.Contains(none.NextCalls, c => c.Tool == "pix_timing_tree");
        Assert.NotEqual(Snapshot().Stamp(), unrelated.Stamp());
    }
}
