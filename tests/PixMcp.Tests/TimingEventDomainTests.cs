using System.Text.Json;
using Microsoft.Data.Sqlite;
using PixMcp.Pix;
using Xunit;

namespace PixMcp.Tests;

public sealed class TimingEventDomainTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("pixmcp-timing-domains-");
    private string Path => System.IO.Path.Combine(_directory.FullName, "pixstorage.sqlite");

    public TimingEventDomainTests()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        // Real PixStorage column lists (2606.18); PixCpuExecution/PixCpuMarker/PixCpuExecutionTimes are plain stand-ins for the
        // virtual tables, with CpuExecutionRowId as an ordinary column so the row-id lookup path runs.
        command.CommandText = """
            CREATE TABLE CaptureFacts(Id INTEGER PRIMARY KEY, Value INTEGER);
            CREATE TABLE Strings(Id INTEGER PRIMARY KEY, Value TEXT);
            CREATE TABLE Processes(Id INTEGER PRIMARY KEY, ProcessId INTEGER, ImageNameId INTEGER, StartTimestamp INTEGER, EndTimestamp INTEGER);
            CREATE TABLE Threads(Id INTEGER PRIMARY KEY, ProcThreadId INTEGER, ThreadNameId INTEGER, MaxPixEventLevel INTEGER, StartTimestamp INTEGER, EndTimestamp INTEGER, PixEventCount INTEGER, ContextSwitchCount INTEGER, ProcessRowId INTEGER, SampleCount INTEGER, MarkerCount INTEGER, ApiMarkerCount INTEGER, ApiObjectEventCount INTEGER);
            CREATE TABLE PixEventInfo(Id INTEGER PRIMARY KEY, NameId INTEGER, Count INTEGER, GpuCount INTEGER, MinCpuLevel INTEGER, MinGpuLevel INTEGER);
            CREATE TABLE PixCpuExecution(BeginTimestamp INTEGER, EndTimestamp INTEGER, Level INTEGER, ThreadRowId INTEGER, EventId INTEGER, Color INTEGER);
            CREATE TABLE PixCpuExecutionTimes(Duration INTEGER, Execution INTEGER, Stall INTEGER, EventId INTEGER, BeginTimestamp INTEGER, EndTimestamp INTEGER, CpuExecutionRowId INTEGER);
            CREATE TABLE PixMarkerInfo(Id INTEGER PRIMARY KEY, NameId INTEGER, CpuCount INTEGER, GpuCount INTEGER);
            CREATE TABLE PixCpuMarker(InfoId INTEGER, Timestamp INTEGER, ThreadId INTEGER, Color INTEGER);
            CREATE TABLE ApiCommandQueue(Id INTEGER PRIMARY KEY, TypeId INTEGER, ProcessId INTEGER, HardwareAdapterId INTEGER, NameId INTEGER, BeginTimestamp INTEGER, EndTimestamp INTEGER, ExecutionCount INTEGER, WorkCount INTEGER, MaxExecutionLevel INTEGER, MaxWorkLevel INTEGER, MarkerCount INTEGER, ApiExecutionCount INTEGER, CommandListCount INTEGER);
            CREATE TABLE ApiQueueExecution(Id INTEGER PRIMARY KEY, ApiCommandQueueId INTEGER, ThreadId INTEGER, SubmitTimestamp INTEGER, BeginTimestamp INTEGER, EndTimestamp INTEGER, FrameToken INTEGER);
            CREATE TABLE ApiQueueExecutionCommandList(Id INTEGER PRIMARY KEY, ApiQueueExecutionId INTEGER, BeginTimestamp INTEGER, EndTimestamp INTEGER, Count INTEGER, CommandLists BLOB);
            CREATE TABLE HardwareAdapter(Id INTEGER PRIMARY KEY, NameId INTEGER);
            CREATE TABLE HardwareCommandQueue(Id INTEGER PRIMARY KEY, AdapterId INTEGER, NameId INTEGER);
            CREATE TABLE GpuWorkRange(Id INTEGER PRIMARY KEY, HardwareQueueId INTEGER, ApiCommandQueueId INTEGER, BeginTimestamp INTEGER, EndTimestamp INTEGER, OverlapLevel INTEGER, Work BLOB);

            INSERT INTO CaptureFacts VALUES(2, 100), (3, 2000), (4, 4242), (24, 1000);
            INSERT INTO Strings VALUES(1, 'game.exe'), (2, 'Render'), (3, 'Main Graphics Queue'), (4, 'Direct'), (5, 'Frame'), (6, 'Tick'), (7, '3D'), (8, 'COPY'), (9, 'Test Adapter');
            INSERT INTO Processes VALUES(1, 4242, 1, 0, 2000);
            INSERT INTO Threads VALUES(10, (4242 << 32) | 7, 2, 1, 0, 2000, 3, 0, 1, 0, 2, 0, 0);
            INSERT INTO PixEventInfo VALUES(1, 5, 3, 0, 0, 0);
            INSERT INTO PixCpuExecution VALUES(200, 300, 0, 10, 1, 7), (400, 500, 0, 10, 1, 7), (1500, 1600, 0, 10, 1, 7);
            INSERT INTO PixCpuExecutionTimes VALUES(100, 60, 40, 1, 200, 300, 1), (100, 50, 40, 1, 400, 500, 2);
            INSERT INTO PixMarkerInfo VALUES(1, 6, 2, 0);
            INSERT INTO PixCpuMarker VALUES(1, 250, 10, 4292654100.0), (1, 1200, 10, 1.0);
            INSERT INTO ApiCommandQueue VALUES(1, 4, 1, 1, 3, 0, 2000, 3, 0, 0, 0, 0, 3, 3);
            INSERT INTO ApiQueueExecution VALUES(1, 1, 10, 150, 200, 260, 0), (2, 1, 10, 330, 320, 380, 0), (3, 1, 10, 1100, 1150, 1180, 0);
            INSERT INTO ApiQueueExecutionCommandList VALUES(1, 1, 200, 260, 2, x'00'), (2, 2, 320, 380, 1, x'00');
            INSERT INTO HardwareAdapter VALUES(1, 9);
            INSERT INTO HardwareCommandQueue VALUES(45, 1, 7), (46, 1, 8);
            INSERT INTO GpuWorkRange VALUES(1, 45, 1, 190, 900, 0, x'00'), (2, 45, 1, 200, 400, 1, x'00'), (3, 46, NULL, 1200, 1300, 0, x'00');
            """;
        command.ExecuteNonQuery();
    }

    public void Dispose() => _directory.Delete(recursive: true);

    private TimingDatabase Open() => new(Path, null, configureForTests: _ => { });

    private static TimingEventsDto Page(TimingDatabase db, string domain, string rangeMode = "full", uint? processId = null, uint? threadId = null,
        string? queueName = null, string? hardwareQueueId = null)
        => db.Events("timing-1", domain, processId, threadId, null, null, null, null, "start", 0, 25, rangeMode, queueName, hardwareQueueId, generation: 3);

    [Fact]
    public void SubmissionsCarrySubmitLatencyCommandListsAndReferences()
    {
        using TimingDatabase db = Open();
        TimingEventsDto full = Page(db, "gpuSubmissions");
        Assert.Equal(3, full.Events.Total);
        RecordedTimingEventDto first = full.Events.Items[0];
        Assert.Equal(("apiSubmission", "ExecuteCommandLists", "150", "50", 2L), (first.Source, first.Name, first.SubmitNs, first.SubmitLatencyNs, first.CommandListCount));
        Assert.Equal(("Render", "Main Graphics Queue", 7u, 4242u), (first.ThreadName, first.QueueName, first.ThreadId!.Value, first.ProcessId!.Value));
        Assert.Equal(1L, TimingSubmissionReferences.Parse(first.SubmissionRef!, "timing-1", 3));
        RecordedTimingEventDto second = full.Events.Items[1];
        Assert.Null(second.SubmitLatencyNs);
        Assert.Equal(1L, second.CommandListCount);
        Assert.Null(full.Events.Items[2].CommandListCount);
        Assert.Equal("not_applicable", full.CpuExecutionTiming.State);

        Assert.Equal(2, Page(db, "gpuSubmissions", rangeMode: "reliable").Events.Total);
        Assert.Equal(3, Page(db, "gpuSubmissions", queueName: "graphics").Events.Total);
        Assert.Equal(0, Page(db, "gpuSubmissions", queueName: "compute").Events.Total);
        OutputSchemaTests.AssertMatches(JsonSerializer.SerializeToElement(full, Json.Options), StructuredToolResults.SchemaFor("pix_timing_events"));
    }

    [Fact]
    public void HardwareRangesAndCpuMarkersAreTheirOwnDomains()
    {
        using TimingDatabase db = Open();
        TimingEventsDto hardware = Page(db, "gpuHardware");
        Assert.Equal(3, hardware.Events.Total);
        Assert.Equal(new[] { "3D", "3D", "COPY" }, hardware.Events.Items.Select(r => r.HardwareQueueName));
        Assert.Equal(1, hardware.Events.Items.Single(r => r.EventId == "2").OverlapLevel);
        Assert.Null(hardware.Events.Items.Single(r => r.EventId == "3").ProcessId);
        Assert.Equal("46", Assert.Single(Page(db, "gpuHardware", hardwareQueueId: "46").Events.Items).HardwareQueueId);
        Assert.Equal(2, Page(db, "gpuHardware", processId: 4242).Events.Total);
        Assert.Equal(2, Page(db, "gpuHardware", rangeMode: "reliable").Events.Total);

        TimingEventsDto markers = Page(db, "cpuMarkers");
        Assert.Equal(2, markers.Events.Total);
        Assert.All(markers.Events.Items, r => Assert.Equal((r.BeginNs, "0", "Tick", "Render"), (r.EndNs, r.DurationNs, r.Name, r.ThreadName)));
        Assert.Equal(4292654100L, markers.Events.Items[0].Color);
        Assert.Equal(1, Page(db, "cpuMarkers", threadId: 7, rangeMode: "reliable").Events.Total);
    }

    [Fact]
    public void CpuExecutionTimingUsesOneRowIdLookupPerEvent()
    {
        using TimingDatabase db = Open();
        TimingEventsDto cpu = Page(db, "cpu");
        Assert.Equal(3, cpu.Events.Total);
        Assert.All(cpu.Events.Items, r => Assert.Equal("rowId", r.ExecutionTimingMethod));
        RecordedTimingEventDto exact = cpu.Events.Items[0];
        Assert.Equal(("available", "60", "40"), (exact.ExecutionTimingState, exact.ExecutionNs, exact.StallNs));
        RecordedTimingEventDto mismatch = cpu.Events.Items[1];
        Assert.Equal("inconsistent", mismatch.ExecutionTimingState);
        Assert.Null(mismatch.ExecutionNs);
        Assert.Equal("unavailable", cpu.Events.Items[2].ExecutionTimingState);
        Assert.Contains("CpuExecutionRowId", cpu.CpuExecutionTiming.Reason);
        Assert.Contains("1 event(s)", cpu.CpuExecutionTiming.Reason);
    }

    [Fact]
    public void AllListsEverySourceWithItsRowsAndAbsentFamilies()
    {
        using TimingDatabase db = Open();
        TimingEventsDto all = Page(db, "all");
        Assert.Equal(11, all.Events.Total);
        Assert.Equal(11, all.Events.Items.Count);
        Assert.Equal(new[] { ("cpu", "included", 3L), ("cpuMarkers", "included", 2L), ("gpuMarkers", "absent", 0L), ("gpuSubmissions", "included", 3L), ("gpuHardware", "included", 3L) },
            all.Sources!.Select(s => (s.Domain, s.State, s.Rows)));
        Assert.Equal(all.Events.Total, all.Sources!.Sum(s => s.Rows));
        Assert.Equal(new[] { "apiSubmission", "hardwareQueue", "pixCpuEvent", "pixCpuMarker" }, all.Events.Items.Select(r => r.Source!).Distinct().OrderBy(s => s, StringComparer.Ordinal));
    }

    [Fact]
    public void DomainsAndFiltersAreValidated()
    {
        using TimingDatabase db = Open();
        PixToolException absent = Assert.Throws<PixToolException>(() => Page(db, "gpuMarkers"));
        Assert.Equal("timing_schema_unsupported", absent.Detail.Code);
        Assert.Contains(absent.Detail.NextCalls, c => c.Tool == "pix_timing_events");

        PixToolException removed = Assert.Throws<PixToolException>(() => Page(db, "gpu"));
        Assert.Equal("invalid_arguments", removed.Detail.Code);
        Assert.Contains("gpuSubmissions", removed.Detail.Message);
        Assert.Equal("invalid_arguments", Assert.Throws<PixToolException>(() => Page(db, "gpuHardware", threadId: 7)).Detail.Code);
        Assert.Equal("invalid_arguments", Assert.Throws<PixToolException>(() => Page(db, "cpu", hardwareQueueId: "45")).Detail.Code);
        Assert.Equal("invalid_arguments", Assert.Throws<PixToolException>(() => Page(db, "cpuMarkers", queueName: "graphics")).Detail.Code);
        Assert.Equal(TimingDatabase.EventDomains, StructuredToolResults.InputSchemaFor("pix_timing_events",
            JsonSerializer.SerializeToElement(new { type = "object", properties = new { domain = new { type = "string" } } })).GetProperty("properties").GetProperty("domain").GetProperty("enum").EnumerateArray().Select(v => v.GetString()));
    }

    [Fact]
    public void OverviewReportsFamiliesThreadLifetimesQueuesAndHardwareQueues()
    {
        using TimingDatabase db = Open();
        TimingOverviewDto overview = db.Overview("timing-1", null, 0, 25);
        Assert.Equal(("available", 3L), (overview.Capabilities["gpuSubmissions"].State, overview.Capabilities["gpuSubmissions"].Rows!.Value));
        Assert.Equal(3L, overview.Capabilities["gpuHardware"].Rows);
        Assert.Equal(2L, overview.Capabilities["cpuMarkers"].Rows);
        Assert.Equal(3L, overview.Capabilities["cpuEvents"].Rows);
        Assert.Equal("unsupported", overview.Capabilities["gpuMarkers"].State);
        Assert.False(overview.Capabilities.ContainsKey("gpuEvents"));
        Assert.False(overview.Capabilities.ContainsKey("submissions"));

        TimingThreadDto thread = Assert.Single(overview.Threads.Items);
        Assert.Equal(("0", "2000", 3L, 0L, 2L), (thread.StartNs, thread.EndNs, thread.PixEventCount!.Value, thread.ContextSwitchCount!.Value, thread.MarkerCount!.Value));
        TimingQueueDto queue = Assert.Single(overview.Queues.Items);
        Assert.Equal(("Test Adapter", "0", "2000", 3L, 3L, 0L), (queue.AdapterName, queue.BeginNs, queue.EndNs, queue.ApiExecutionCount!.Value, queue.CommandListCount!.Value, queue.MaxWorkLevel!.Value));
        Assert.Equal(2, overview.HardwareQueues!.Total);
        Assert.Equal(new[] { ("45", "3D", 2L, "190", "900"), ("46", "COPY", 1L, "1200", "1300") },
            overview.HardwareQueues.Items.Select(h => (h.HardwareQueueId, h.Name!, h.WorkRanges, h.FirstNs!, h.LastNs!)));
        Assert.All(overview.HardwareQueues.Items, h => Assert.Equal("Test Adapter", h.AdapterName));

        // A call an insight already offers is not repeated at the top level, so look in both places.
        ToolCallDto[] insightCalls = overview.Sections!.Insights.SelectMany(i => i.NextCalls).ToArray();
        ToolCallDto[] offered = [.. overview.NextCalls, .. insightCalls];
        Assert.Contains(offered, c => c.Tool == "pix_timing_events" && JsonSerializer.SerializeToElement(c.Arguments).GetProperty("domain").GetString() == "gpuSubmissions");
        Assert.Contains(offered, c => c.Tool == "pix_timing_events" && JsonSerializer.SerializeToElement(c.Arguments).GetProperty("domain").GetString() == "gpuHardware");
        Assert.Contains(overview.NextCalls, c => c.Tool == "pix_timing_schema");
        var insightKeys = insightCalls.Select(c => c.Tool + Json.Serialize(c.Arguments)).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(overview.NextCalls, c => insightKeys.Contains(c.Tool + Json.Serialize(c.Arguments)));
        OutputSchemaTests.AssertMatches(JsonSerializer.SerializeToElement(overview, Json.Options), StructuredToolResults.SchemaFor("pix_timing_overview"));
    }

    [Fact]
    public void ReliableWindowWarnsWhenItTruncatesAndMissingTablesAreListed()
    {
        using TimingDatabase db = Open();
        TimingOverviewSectionsDto reliable = db.Overview("timing-1", null, 0, 5, "reliable").Sections!;
        TimingInsightDto truncates = reliable.Insights[0];
        Assert.Equal(("window_truncates_data", "warning"), (truncates.Id, truncates.Severity));
        Assert.Equal((2L, 3L), ((long)truncates.Evidence["gpuSubmissionsInRange"]!, (long)truncates.Evidence["gpuSubmissionsTotal"]!));
        Assert.Contains(truncates.NextCalls, c => c.Tool == "pix_timing_overview");
        Assert.Contains(reliable.Unavailable, u => u.StartsWith("frames_vsync:", StringComparison.Ordinal));
        Assert.Contains(reliable.Unavailable, u => u.StartsWith("capture:", StringComparison.Ordinal));
        Assert.Null(reliable.Frames);
        Assert.Contains(reliable.Insights, i => i.Id == "frames_unavailable");
        Assert.NotNull(reliable.Gpu);
        Assert.DoesNotContain(db.Overview("timing-1", null, 0, 5).Sections!.Insights, i => i.Id == "window_truncates_data");
    }

    [SkippableFact]
    public void NativeCaptureExposesRealGpuLanesMarkersAndRowIdTiming()
    {
        string? capture = TestArtifacts.TimingCapture;
        Skip.If(capture is null || PixDiscovery.InstallDir is null, "Set PIX_TEST_TIMING_CAPTURE to a timing capture.");
        using var db = new TimingDatabase(capture!, System.IO.Path.Combine(PixDiscovery.InstallDir!, "pixstorage.dll"));

        TimingEventsDto submissions = Page(db, "gpuSubmissions");
        Assert.Equal(808, submissions.Events.Total);
        Assert.All(submissions.Events.Items, r => { Assert.NotNull(r.SubmitNs); Assert.Equal(1L, r.CommandListCount); Assert.NotNull(r.SubmissionRef); });
        Assert.Equal(722, Page(db, "gpuSubmissions", rangeMode: "reliable").Events.Total);

        TimingEventsDto hardware = Page(db, "gpuHardware");
        Assert.Equal(15, hardware.Events.Total);
        Assert.All(hardware.Events.Items, r => Assert.Equal("3D", r.HardwareQueueName));

        TimingEventsDto markers = Page(db, "cpuMarkers");
        Assert.Equal(434, markers.Events.Total);
        Assert.All(markers.Events.Items, r => { Assert.Equal(r.BeginNs, r.EndNs); Assert.False(string.IsNullOrEmpty(r.Name)); });

        TimingEventsDto cpu = Page(db, "cpu");
        Assert.NotEmpty(cpu.Events.Items);
        Assert.All(cpu.Events.Items, r =>
        {
            Assert.Equal(("rowId", "available"), (r.ExecutionTimingMethod, r.ExecutionTimingState));
            Assert.Equal(long.Parse(r.DurationNs), long.Parse(r.ExecutionNs!) + long.Parse(r.StallNs!));
        });

        TimingEventsDto all = Page(db, "all");
        Assert.Equal(all.Sources!.Sum(s => s.Rows), all.Events.Total);
        TimingEventSourceDto gpuMarkers = all.Sources.Single(s => s.Domain == "gpuMarkers");
        Assert.Equal(("included", 0L), (gpuMarkers.State, gpuMarkers.Rows));

        TimingOverviewDto overview = db.Overview("timing-native", null, 0, 25);
        Assert.Equal(("available", 808L), (overview.Capabilities["gpuSubmissions"].State, overview.Capabilities["gpuSubmissions"].Rows!.Value));
        Assert.Equal(15L, overview.Capabilities["gpuHardware"].Rows);
        Assert.Equal(434L, overview.Capabilities["cpuMarkers"].Rows);
        Assert.Equal("empty", overview.Capabilities["gpuMarkers"].State);
        Assert.Contains(overview.HardwareQueues!.Items, h => h.Name == "3D" && h.WorkRanges == 15);
        Assert.All(overview.Queues.Items, q => { Assert.False(string.IsNullOrEmpty(q.AdapterName)); Assert.NotNull(q.CommandListCount); });
        Assert.Contains(overview.Threads.Items, t => t.StartNs is not null && t.PixEventCount > 0);
    }
}
