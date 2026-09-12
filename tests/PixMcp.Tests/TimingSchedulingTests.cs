using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PixMcp.Pix;
using Xunit;

namespace PixMcp.Tests;

public sealed class TimingSchedulingTests
{
    [Fact]
    public void SubmissionsSelectCpuTimestampAndPreserveGpuCompletionBeyondRange()
    {
        using var fixture = new Fixture(); using var db = fixture.Open();
        TimingSubmissionsDto result = db.Submissions("timing-1", 4, null, 42, null, "1", null, null, 0, 25);
        Assert.Equal(new[] { "2", "3", "7", "8", "4", "5" }, result.Submissions.Items.Select(r => r.SubmissionId));
        TimingSubmissionDto row = result.Submissions.Items[0];
        Assert.Equal("100", row.SubmitNs); Assert.Equal("120", row.GpuBeginNs); Assert.Equal("550", row.GpuEndNs);
        Assert.Equal("20", row.LatencyNs); Assert.Equal("430", row.GpuDurationNs);
        Assert.Equal("1", row.ThreadRowId); Assert.Equal((uint)42, row.ProcessId); Assert.Equal((uint)7, row.ThreadId);
        Assert.Equal("available", row.ThreadCorrelation.State);
        Assert.Contains(row.NextCalls, c => c.Tool == "pix_timing_thread_switches");
        foreach (ToolCallDto call in row.NextCalls.Where(c => c.Tool is "pix_timing_events" or "pix_timing_hotspots"))
        {
            JsonElement arguments = JsonSerializer.SerializeToElement(call.Arguments);
            Assert.Equal("100", arguments.GetProperty("startNs").GetString());
            Assert.Equal("300", arguments.GetProperty("endNs").GetString());
        }
        Assert.Equal(2, row.NextCalls.Count(c => c.Tool is "pix_timing_events" or "pix_timing_hotspots"));
        JsonElement switchArguments = JsonSerializer.SerializeToElement(row.NextCalls.Single(c => c.Tool == "pix_timing_thread_switches").Arguments);
        Assert.Equal("1", switchArguments.GetProperty("threadRowId").GetString());
        Assert.Equal("550", switchArguments.GetProperty("endNs").GetString());
        TimingSubmissionsDto exact = db.Submissions("timing-1", 4, row.SubmissionRef, null, null, null, null, null, 0, 25);
        Assert.Equal(row.SubmissionRef, Assert.Single(exact.Submissions.Items).SubmissionRef);
        Assert.Contains("no time filter", exact.Selection);
    }

    [Fact]
    public void ZeroMissingInconsistentTimingAndOrphanMetadataStayVisible()
    {
        using var fixture = new Fixture(); using var db = fixture.Open();
        var rows = db.Submissions("timing-1", 0, null, null, null, null, null, null, 0, 25).Submissions.Items.ToDictionary(r => r.SubmissionId);
        foreach (string id in new[] { "3", "4", "7" })
        {
            Assert.Equal("unavailable", rows[id].GpuTiming.State);
            Assert.NotNull(rows[id].GpuTiming.Reason); Assert.Null(rows[id].LatencyNs); Assert.Null(rows[id].GpuDurationNs);
        }
        Assert.Equal("200", rows["3"].GpuBeginNs); Assert.Equal("240", rows["4"].GpuBeginNs); Assert.Null(rows["7"].GpuBeginNs);
        Assert.Equal("999", rows["8"].ThreadRowId); Assert.Equal("unavailable", rows["8"].ThreadCorrelation.State);
        Assert.Null(rows["8"].ThreadId); Assert.Equal((uint)42, rows["8"].ProcessId);
        Assert.Single(rows["8"].NextCalls);
    }

    [Theory]
    [InlineData("UPDATE Threads SET StartTimestamp=NULL WHERE Id=1")]
    [InlineData("UPDATE Threads SET EndTimestamp=NULL WHERE Id=1")]
    [InlineData("UPDATE Threads SET StartTimestamp=150 WHERE Id=1")]
    [InlineData("UPDATE Threads SET EndTimestamp=100 WHERE Id=1")]
    [InlineData("ALTER TABLE Threads DROP COLUMN StartTimestamp; ALTER TABLE Threads DROP COLUMN EndTimestamp")]
    public void CpuFollowupsRequireKnownLifetimeContainingSubmission(string mutation)
    {
        using var fixture = new Fixture(); fixture.Execute(mutation); using var db = fixture.Open();
        TimingSubmissionDto row = Assert.Single(db.Submissions("timing-1", 0, null, null, null, null, 100, 101, 0, 25).Submissions.Items);
        Assert.Equal("20", row.LatencyNs); Assert.Equal("550", row.GpuEndNs);
        Assert.DoesNotContain(row.NextCalls, c => c.Tool is "pix_timing_events" or "pix_timing_hotspots");
    }

    [Fact]
    public void SubmissionIdsPagingAndFiltersDoNotDependOnMarkerDefinitions()
    {
        using var fixture = new Fixture();
        fixture.Execute("INSERT INTO ApiQueueExecution VALUES(9,1,1,100,120,550)");
        using var db = fixture.Open();
        TimingSubmissionDto first = Assert.Single(db.Submissions("timing-1", 0, null, 42, 7, "1", 100, 101, 0, 1).Submissions.Items);
        TimingSubmissionDto second = Assert.Single(db.Submissions("timing-1", 0, null, 42, 7, "1", 100, 101, 1, 1).Submissions.Items);
        Assert.Equal("2", first.SubmissionId); Assert.Equal("9", second.SubmissionId); Assert.NotEqual(first.SubmissionRef, second.SubmissionRef);
        Assert.Empty(db.Submissions("timing-1", 0, null, 43, null, null, null, null, 0, 25).Submissions.Items);
        Assert.Empty(db.Submissions("timing-1", 0, null, null, null, "2", null, null, 0, 25).Submissions.Items);
    }

    [Fact]
    public void SubmissionReferencesAreCaptureAndGenerationScoped()
    {
        using var fixture = new Fixture(); using var db = fixture.Open();
        string reference = TimingSubmissionReferences.Create("timing-1", 3, 2);
        Assert.Equal("result_expired", Assert.Throws<PixToolException>(() => db.Submissions("timing-1", 4, reference, null, null, null, null, null, 0, 25)).Detail.Code);
        Assert.Equal("result_expired", Assert.Throws<PixToolException>(() => db.Submissions("timing-2", 3, reference, null, null, null, null, null, 0, 25)).Detail.Code);
        Assert.Equal("invalid_arguments", Assert.Throws<PixToolException>(() => db.Submissions("timing-1", 3, reference, 42, null, null, null, null, 0, 25)).Detail.Code);
        Assert.Equal("invalid_reference", Assert.Throws<PixToolException>(() => db.Submissions("timing-1", 3, "bad", null, null, null, null, null, 0, 25)).Detail.Code);
        Assert.Equal("invalid_reference", Assert.Throws<PixToolException>(() => db.Submissions("timing-1", 3, TimingSubmissionReferences.Create("timing-1", 3, 999), null, null, null, null, null, 0, 25)).Detail.Code);
    }

    [Fact]
    public void SwitchesRespectThreadLifetimeAndPreserveWaitAndStackEvidence()
    {
        using var fixture = new Fixture(); using var db = fixture.Open();
        TimingThreadSwitchesDto result = db.ThreadSwitches("timing-1", "1", null, null, 0, 25);
        Assert.Equal(new[] { "100", "150", "200", "250" }, result.Switches.Items.Select(r => r.TimestampNs));
        TimingThreadSwitchDto first = result.Switches.Items[0];
        Assert.Equal("switchOut", first.Direction); Assert.Equal(6, first.WaitReasonCode);
        Assert.Equal((uint)43, first.PeerProcessId); Assert.Equal((uint)9, first.PeerThreadId);
        Assert.Equal("available", first.Stack.State); Assert.Equal("partial", first.Stack.SymbolState);
        Assert.Equal("SwitchCaller", first.Stack.Frames[0].Function); Assert.Equal("0xFFFF", first.Stack.Frames[1].Address);
        Assert.Equal("leafToCaller", first.Stack.StackOrder);
        Assert.Equal("switchIn", result.Switches.Items[1].Direction); Assert.Null(result.Switches.Items[1].WaitReasonCode);
        Assert.Equal("not_applicable", result.Switches.Items[1].Stack.State);
        Assert.Equal("missing", result.Switches.Items[2].Stack.State);
        Assert.Equal("invalid", result.Switches.Items[3].Stack.State);
        Assert.All(fixture.StackLookups, call => Assert.Equal(7L, call.thread));
        Assert.Contains((7L, 100L), fixture.StackLookups); Assert.DoesNotContain((7L, 150L), fixture.StackLookups);
        TimingThreadSwitchDto reused = Assert.Single(db.ThreadSwitches("timing-1", "2", null, null, 0, 25).Switches.Items);
        Assert.Equal("300", reused.TimestampNs);
        TimingThreadSwitchDto otherProcess = Assert.Single(db.ThreadSwitches("timing-1", "3", null, null, 0, 25).Switches.Items);
        Assert.Equal("175", otherProcess.TimestampNs);
    }

    [Fact]
    public void SwitchPaginationRangeAndDuplicateTimestampOrderingAreStable()
    {
        using var fixture = new Fixture(); fixture.Execute("INSERT INTO ContextSwitch VALUES(0,100,180388626439,0,15)");
        using var db = fixture.Open();
        TimingThreadSwitchDto first = Assert.Single(db.ThreadSwitches("timing-1", "1", 100, 101, 0, 1).Switches.Items);
        TimingThreadSwitchDto second = Assert.Single(db.ThreadSwitches("timing-1", "1", 100, 101, 1, 1).Switches.Items);
        Assert.Equal(0, first.Core); Assert.Equal(1, second.Core);
        Assert.Empty(db.ThreadSwitches("timing-1", "1", 300, 400, 0, 25).Switches.Items);
        Assert.Equal("invalid_reference", Assert.Throws<PixToolException>(() => db.ThreadSwitches("timing-1", "999", null, null, 0, 25)).Detail.Code);
    }

    [Fact]
    public void AbsentSymbolsDoNotDiscardRecordedSwitchStackAddresses()
    {
        using var fixture = new Fixture(); fixture.Execute("DROP TABLE FunctionInformation");
        using var db = fixture.Open();
        TimingRecordedStackDto stack = db.ThreadSwitches("timing-1", "1", 100, 101, 0, 25).Switches.Items[0].Stack;
        Assert.Equal("available", stack.State); Assert.Equal("unresolved", stack.SymbolState);
        Assert.Equal("0x1012", stack.Frames[0].Address); Assert.Null(stack.Frames[0].Function);
    }

    [Fact]
    public void MissingStackSchemaAndInvalidThreadLifetimeAreExplicit()
    {
        using var fixture = new Fixture(); fixture.Execute("DROP TABLE Stacks");
        using (var db = fixture.Open()) Assert.Equal("unsupported", db.ThreadSwitches("timing-1", "1", 100, 101, 0, 25).Switches.Items[0].Stack.State);
        fixture.Execute("UPDATE Threads SET StartTimestamp=NULL WHERE Id=1");
        using var invalid = fixture.Open();
        Assert.Equal("timing_thread_lifetime_unavailable", Assert.Throws<PixToolException>(() => invalid.ThreadSwitches("timing-1", "1", null, null, 0, 25)).Detail.Code);
    }

    [Fact]
    public void IdleStacksAreNotAttributedAcrossCoresThroughSharedThreadZero()
    {
        using var fixture = new Fixture();
        fixture.Execute("INSERT INTO Processes VALUES(3,0); INSERT INTO Threads VALUES(4,0,3,1,0,0,500); INSERT INTO ContextSwitch VALUES(3,100,0,180388626439,25)");
        using var db = fixture.Open();
        TimingThreadSwitchDto idle = Assert.Single(db.ThreadSwitches("timing-1", "4", 100, 101, 0, 25).Switches.Items);
        Assert.Equal("unavailable", idle.Stack.State); Assert.Empty(fixture.StackLookups);
    }

    [Fact]
    public void SchedulingQueriesRemainReadOnlyAndMissingSchemasAreReported()
    {
        using var fixture = new Fixture(); byte[] before = SHA256.HashData(File.ReadAllBytes(fixture.Path));
        using (var db = fixture.Open())
        {
            db.Submissions("timing-1", 0, null, null, null, null, null, null, 0, 25);
            db.ThreadSwitches("timing-1", "1", null, null, 0, 25);
        }
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(fixture.Path)));
        fixture.Execute("DROP TABLE ApiQueueExecution; DROP TABLE ContextSwitch");
        using var unsupported = fixture.Open();
        Assert.Equal("timing_schema_unsupported", Assert.Throws<PixToolException>(() => unsupported.Submissions("timing-1", 0, null, null, null, null, null, null, 0, 25)).Detail.Code);
        Assert.Equal("timing_schema_unsupported", Assert.Throws<PixToolException>(() => unsupported.ThreadSwitches("timing-1", "1", null, null, 0, 25)).Detail.Code);
    }

    [SkippableTheory]
    [InlineData("timing-validation")]
    [InlineData("timing-cpu-launch")]
    [InlineData("tutorial-validation")]
    public void ExistingNativeFixturesMatchSubmissionStorageAndExposeSwitches(string fixtureName)
    {
        string? root = FindRoot();
        string? path = root is null ? null : System.IO.Path.Combine(root, "tests", "artifacts", fixtureName, "timing.wpix");
        Skip.If(path is null || !File.Exists(path) || PixDiscovery.InstallDir is null, "Provide the existing native timing fixture and PIX installation.");
        using var input = File.OpenRead(path!); byte[] before = SHA256.HashData(input); input.Close();
        using (var db = new TimingDatabase(path!, System.IO.Path.Combine(PixDiscovery.InstallDir!, "pixstorage.dll")))
        {
            TimingSubmissionsDto result = db.Submissions("native", 0, null, null, null, null, null, null, 0, 1000);
            TimingSubmissionDto row = result.Submissions.Items.First(r => r.GpuTiming.State == "available" && r.ThreadCorrelation.State == "available");
            using var direct = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            direct.Open(); using var command = direct.CreateCommand();
            command.CommandText = "SELECT SubmitTimestamp,BeginTimestamp,EndTimestamp FROM ApiQueueExecution WHERE Id=$id";
            command.Parameters.AddWithValue("$id", long.Parse(row.SubmissionId)); using var reader = command.ExecuteReader(); Assert.True(reader.Read());
            Assert.Equal(reader.GetInt64(0).ToString(), row.SubmitNs); Assert.Equal(reader.GetInt64(1).ToString(), row.GpuBeginNs);
            Assert.Equal(reader.GetInt64(2).ToString(), row.GpuEndNs);
            Assert.Equal((reader.GetInt64(1) - reader.GetInt64(0)).ToString(), row.LatencyNs);
            Assert.Equal((reader.GetInt64(2) - reader.GetInt64(1)).ToString(), row.GpuDurationNs);
            TimingThreadSwitchesDto switches = db.ThreadSwitches("native", row.ThreadRowId!, null, null, 0, 1000);
            Assert.NotEmpty(switches.Switches.Items);
            Assert.Contains(switches.Switches.Items, s => s.Direction == "switchOut" && s.WaitReasonCode.HasValue);
            reader.Close(); direct.LoadExtension(System.IO.Path.Combine(PixDiscovery.InstallDir!, "pixstorage.dll"), "sqlite3_batchexpand_init");
            direct.EnableExtensions(false);
            foreach (TimingThreadSwitchDto transition in switches.Switches.Items.Where(s => s.Direction == "switchOut").Take(20))
            {
                using var stackQuery = direct.CreateCommand();
                stackQuery.CommandText = "SELECT FindStackId($thread,$time)";
                stackQuery.Parameters.AddWithValue("$thread", row.ThreadId!.Value);
                stackQuery.Parameters.AddWithValue("$time", long.Parse(transition.TimestampNs));
                long stackId = Convert.ToInt64(stackQuery.ExecuteScalar());
                if (stackId == 0) Assert.Equal("missing", transition.Stack.State);
                else Assert.Contains(transition.Stack.State, new[] { "available", "invalid" });
            }
        }
        using var after = File.OpenRead(path!); Assert.Equal(before, SHA256.HashData(after));
    }

    private static string? FindRoot()
    {
        for (DirectoryInfo? current = new(AppContext.BaseDirectory); current is not null; current = current.Parent)
            if (Directory.Exists(System.IO.Path.Combine(current.FullName, ".git"))) return current.FullName;
        return null;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("pixmcp-scheduling-");
        internal string Path => System.IO.Path.Combine(_directory.FullName, "timing.sqlite");
        internal List<(long thread, long timestamp)> StackLookups { get; } = [];
        internal Fixture()
        {
            Execute("""
                CREATE TABLE CaptureFacts(Id INTEGER PRIMARY KEY,Value INTEGER);
                INSERT INTO CaptureFacts VALUES(2,100),(24,500);
                CREATE TABLE Strings(Id INTEGER PRIMARY KEY,Value TEXT);
                INSERT INTO Strings VALUES(1,'Main'),(2,'Graphics'),(3,'DIRECT'),(4,'fixture.exe');
                CREATE TABLE Processes(Id INTEGER PRIMARY KEY,ProcessId INTEGER);
                INSERT INTO Processes VALUES(1,42),(2,43);
                CREATE TABLE Threads(Id INTEGER PRIMARY KEY,ProcThreadId INTEGER,ProcessRowId INTEGER,ThreadNameId INTEGER,SampleCount INTEGER,StartTimestamp INTEGER,EndTimestamp INTEGER);
                INSERT INTO Threads VALUES(1,180388626439,1,1,0,50,300),(2,180388626439,1,1,0,300,500),(3,184683593735,2,1,0,100,500);
                CREATE TABLE ApiCommandQueue(Id INTEGER PRIMARY KEY,ProcessId INTEGER,NameId INTEGER,TypeId INTEGER);
                INSERT INTO ApiCommandQueue VALUES(1,1,2,3);
                CREATE TABLE ApiQueueExecution(Id INTEGER PRIMARY KEY,ApiCommandQueueId INTEGER,ThreadId INTEGER,SubmitTimestamp INTEGER,BeginTimestamp INTEGER,EndTimestamp INTEGER);
                INSERT INTO ApiQueueExecution VALUES(1,1,1,99,110,120),(2,1,1,100,120,550),(3,1,1,200,200,200),(4,1,1,250,240,260),(5,1,2,499,510,530),(6,1,2,500,510,520),(7,1,1,210,NULL,NULL),(8,1,999,220,230,240);
                CREATE TABLE ContextSwitch(Core INTEGER,Timestamp INTEGER,FromProcThreadId INTEGER,ToProcThreadId INTEGER,FromThreadWaitReason INTEGER);
                INSERT INTO ContextSwitch VALUES(1,99,180388626439,0,0),(1,100,180388626439,184683593737,6),(2,150,184683593737,180388626439,27),(2,200,180388626439,0,15),(2,250,180388626439,0,32),(2,300,180388626439,0,15),(0,175,184683593735,0,6);
                CREATE TABLE Stacks(Id INTEGER PRIMARY KEY,NumFrames INTEGER,Addresses BLOB);
                INSERT INTO Stacks VALUES(1,2,X'1210000000000000FFFF000000000000'),(2,1,X'00');
                CREATE TABLE StackEvents(OSThreadId INTEGER,StartTimestamp INTEGER,EndTimestamp INTEGER,StackEventData BLOB);
                CREATE TABLE Images(Id INTEGER PRIMARY KEY,OSProcessId INTEGER,PELoadAddress INTEGER,LoadSize INTEGER,LoadTimestamp INTEGER,UnloadTimestamp INTEGER,FilePathId INTEGER,ModuleId INTEGER);
                INSERT INTO Images VALUES(1,42,4096,4096,0,500,4,1);
                CREATE TABLE FunctionInformation(Id INTEGER PRIMARY KEY,ModuleId INTEGER,Offset INTEGER,Size INTEGER,DecoratedNameId INTEGER);
                INSERT INTO FunctionInformation VALUES(1,1,16,16,1);
                CREATE TABLE SymbolStrings(Id INTEGER PRIMARY KEY,Value TEXT);
                INSERT INTO SymbolStrings VALUES(1,'SwitchCaller');
                """);
        }
        internal TimingDatabase Open() => new(Path, null, configureForTests: c => c.CreateFunction<long, long, long>("FindStackId", (thread, time) =>
        {
            StackLookups.Add((thread, time));
            return thread != 7 ? 0 : time == 100 ? 1 : time == 250 ? 2 : 0;
        }));
        internal void Execute(string sql)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString());
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
        public void Dispose() => _directory.Delete(true);
    }
}
