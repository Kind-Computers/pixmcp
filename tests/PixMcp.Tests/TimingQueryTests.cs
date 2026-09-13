using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PixMcp.Pix.Handles;
using PixMcp.Tools;
using PixMcp.Pix;
using PixMcp.Pix.Sql;
using Xunit;

namespace PixMcp.Tests;

public sealed class TimingQueryTests
{
    [Fact]
    public void RecordedEventsUseHalfOpenOverlapAndKeepFullExecutionTiming()
    {
        using var fixture = new Fixture();
        using var database = fixture.Open();
        TimingEventsDto data = database.Events("timing-1", "cpu", 42, 7, null, "Frame", null, null, "start", 0, 25);
        Assert.Equal("100", data.Provenance.StartNs);
        Assert.Equal("500", data.Provenance.EndNs);
        Assert.Equal(3, data.Events.Total);
        RecordedTimingEventDto crossing = data.Events.Items.Single(e => e.BeginNs == "50");
        Assert.Equal("100", crossing.DurationNs);
        Assert.Equal("50", crossing.OverlapDurationNs);
        Assert.Equal("20", crossing.ExecutionNs);
        Assert.Equal("80", crossing.StallNs);
        Assert.Equal("available", crossing.ExecutionTimingState);
        Assert.Contains(data.Events.Items, e => e.BeginNs == "100" && e.EndNs == "100");
        Assert.DoesNotContain(data.Events.Items, e => e.BeginNs == "500");
        Assert.Equal(1, database.Events("timing-1", "gpuMarkers", 42, null, "1", null, null, null, "duration", 0, 25).Events.Total);
    }

    [Fact]
    public void EventPagesFiltersAndAmbiguousExecutionDataStayExact()
    {
        using var fixture = new Fixture();
        fixture.Execute("INSERT INTO PixCpuExecutionTimes VALUES(100,20,80,1,50,150)");
        using var database = fixture.Open();
        TimingEventsDto first = database.Events("timing-1", "cpu", 42, null, null, "Frame", null, null, "duration", 0, 1);
        TimingEventsDto second = database.Events("timing-1", "cpu", 42, null, null, "Frame", null, null, "duration", 1, 1);
        Assert.Equal(1, first.Events.NextOffset);
        Assert.Single(first.NextCalls);
        Assert.NotEqual(first.Events.Items[0].BeginNs, second.Events.Items[0].BeginNs);
        Assert.Equal("ambiguous", first.Events.Items[0].ExecutionTimingState);
        Assert.Null(first.Events.Items[0].ExecutionNs);
        Assert.Empty(database.Events("timing-1", "all", null, null, null, "' OR 1=1 --", null, null, "start", 0, 25).Events.Items);
        Assert.Equal("invalid_arguments", Assert.Throws<PixToolException>(() => database.Events("timing-1", "gpuMarkers", null, 7, null, null, null, null, "start", 0, 25)).Detail.Code);
    }

    [Fact]
    public void ExecutionTimingCannotAttributeIdenticalTuplesAcrossFilteredLanes()
    {
        using var fixture = new Fixture();
        fixture.Execute("INSERT INTO PixCpuExecution VALUES(50,150,0,2,1,0)");
        using var database = fixture.Open();
        foreach (uint pid in new uint[] { 42, 43 })
        {
            RecordedTimingEventDto row = database.Events("timing-1", "cpu", pid, 7, null, "Frame", null, null, "duration", 0, 1).Events.Items[0];
            Assert.Equal("ambiguous", row.ExecutionTimingState);
            Assert.Null(row.ExecutionNs);
            Assert.Null(row.StallNs);
        }
    }

    [Fact]
    public void ExecutionTimingMatchesEveryPageEventIdAndItsExactOccurrence()
    {
        using var fixture = new Fixture();
        fixture.Execute("""
            INSERT INTO PixCpuExecution VALUES(200,240,1,1,2,0),(300,330,0,1,2,0);
            INSERT INTO PixCpuExecutionTimes VALUES
                (40,31,9,1,200,240),(40,22,18,2,200,240),(30,12,18,2,300,330),
                (30,29,1,1,300,330),(40,39,1,99,200,240);
            """);
        using var database = fixture.Open();
        TimingEventsDto page = database.Events("timing-1", "cpu", 42, 7, null, null, 190, 340, "start", 0, 25);
        Assert.Equal(3, page.Events.Total);
        Assert.All(page.Events.Items, row => Assert.Equal("available", row.ExecutionTimingState));
        RecordedTimingEventDto firstId = Assert.Single(page.Events.Items, row => row.EventId == "1");
        Assert.Equal("31", firstId.ExecutionNs);
        Assert.Equal("9", firstId.StallNs);
        RecordedTimingEventDto sameInterval = Assert.Single(page.Events.Items, row => row.EventId == "2" && row.BeginNs == "200");
        Assert.Equal("22", sameInterval.ExecutionNs);
        Assert.Equal("18", sameInterval.StallNs);
        RecordedTimingEventDto repeatedId = Assert.Single(page.Events.Items, row => row.EventId == "2" && row.BeginNs == "300");
        Assert.Equal("12", repeatedId.ExecutionNs);
        Assert.Equal("18", repeatedId.StallNs);
    }

    [Fact]
    public void CounterMetadataAndExactSamplePagesPreserveUnitsAndBoundaries()
    {
        using var fixture = new Fixture();
        using var database = fixture.Open();
        TimingCounterDto counter = Assert.Single(database.Counters("timing-1", 42, "Frame Number", 0, 25).Counters.Items);
        Assert.Equal(new[] { "Fixture", "Counters" }, counter.GroupPath);
        Assert.Equal("count", counter.Units);
        TimingCounterSamplesDto first = database.CounterSamples("timing-1", counter.CounterId, null, null, 0, 1);
        TimingCounterSamplesDto second = database.CounterSamples("timing-1", counter.CounterId, null, null, 1, 1);
        Assert.Equal(2, first.Samples.Total);
        Assert.Equal("100", first.Samples.Items[0].TimestampNs);
        Assert.Equal(1d, first.Samples.Items[0].Value);
        Assert.Equal("499", second.Samples.Items[0].TimestampNs);
        Assert.Null(second.Samples.NextOffset);
        Assert.Equal("timing_counter_not_found", Assert.Throws<PixToolException>(() => database.CounterSamples("timing-1", "99", null, null, 0, 25)).Detail.Code);
    }

    [Fact]
    public void SampleHotspotsCountRecursionOnceAndTreePreservesCallerOrder()
    {
        using var fixture = new Fixture();
        using var database = fixture.Open();
        TimingSampleAnalysisDto profile = database.Samples("timing-1", 42, 7, null, null);
        Assert.Equal(3, profile.Coverage.TotalSamples);
        Assert.Equal(2, profile.Coverage.SamplesWithStacks);
        Assert.Equal(1, profile.Coverage.SamplesWithoutStacks);
        TimingHotspotDto inner = profile.Hotspots.Single(h => h.Function.Function == "FixtureTimingInner");
        Assert.Equal(2, inner.InclusiveSamples);
        Assert.Equal(2, inner.ExclusiveSamples);
        Assert.Equal(200d / 3, inner.InclusivePercent, 8);
        Assert.Equal("fixture.cpp", inner.Function.SourceFile);
        Assert.Equal(17, inner.Function.SourceLine);
        TimingCallNodeDto caller = Assert.Single(profile.Nodes, n => n.ParentNodeId == "root");
        Assert.Equal("FixtureTimingOuter", caller.Function!.Function);
        Assert.Equal(2, caller.InclusiveSamples);
        Assert.Equal(3, profile.Nodes.Single(n => n.NodeId == "root").InclusiveSamples);
        Assert.Equal(3, profile.Nodes.Count(n => n.Function?.Function == "FixtureTimingInner"));
    }

    [Fact]
    public void CpuSamplesResolveTheirThreadAtTheExactRecordedTimestamp()
    {
        using var fixture = new Fixture();
        // A second thread sampled at the same timestamp has no recorded stack.
        fixture.Execute("INSERT INTO CpuSample VALUES(3,100,180388626440)");
        using var database = fixture.Open();
        TimingSampleAnalysisDto recorded = database.Samples("timing-1", 42, 7, null, null);
        TimingSampleAnalysisDto missing = database.Samples("timing-1", 42, 8, null, null);
        Assert.Equal(2, recorded.Coverage.SamplesWithStacks);
        Assert.Equal(1, missing.Coverage.TotalSamples);
        Assert.Equal(0, missing.Coverage.SamplesWithStacks);
        Assert.Empty(missing.Hotspots);
    }

    [Fact]
    public void SampleSymbolsRespectProcessImageLifetimeAndKeepUnknownAddresses()
    {
        using var fixture = new Fixture();
        using (var db = fixture.Open())
        {
            TimingSampleAnalysisDto profile = db.Samples("timing-1", 43, 7, null, null);
            Assert.Equal("OtherProcessFunction", Assert.Single(profile.Hotspots).Function.Function);
        }
        fixture.Execute("UPDATE Images SET UnloadTimestamp=150 WHERE OSProcessId=42; DELETE FROM FunctionInformation WHERE ModuleId=2;");
        using (var db = fixture.Open())
        {
            TimingSampleAnalysisDto profile = db.Samples("timing-1", 42, 7, null, null);
            Assert.Equal(1, profile.Coverage.SamplesWithUnresolvedFrames);
            Assert.Contains(profile.Hotspots, h => h.Function.SymbolState == "module_unknown" && h.Function.Address == "0x1015");
            TimingSampleAnalysisDto other = db.Samples("timing-1", 43, 7, null, null);
            Assert.Equal("unresolved", Assert.Single(other.Hotspots).Function.SymbolState);
        }
    }

    [Fact]
    public void MissingAndMalformedStacksRemainVisibleInCoverage()
    {
        using var fixture = new Fixture();
        fixture.Execute("UPDATE Stacks SET NumFrames=999 WHERE Id=1");
        using (var db = fixture.Open())
        {
            TimingSampleAnalysisDto profile = db.Samples("timing-1", 42, 7, null, null);
            Assert.Equal(1, profile.Coverage.InvalidStackSamples);
            Assert.Equal(1, profile.Coverage.SamplesWithStacks);
            Assert.Equal(3, profile.Coverage.TotalSamples);
        }
        fixture.Execute("DROP TABLE StackEvents; DROP TABLE Stacks; DROP TABLE FunctionInformation");
        using (var db = fixture.Open())
        {
            TimingSampleAnalysisDto profile = db.Samples("timing-1", 42, 7, null, null);
            Assert.Equal(3, profile.Coverage.SamplesWithoutStacks);
            Assert.Equal("unsupported", profile.Coverage.StackState);
            Assert.Equal("unsupported", profile.Coverage.SymbolState);
            Assert.Empty(profile.Hotspots);
        }
    }

    [Fact]
    public void QueriesAreReadOnlyAndCancellationInterruptsSqliteExecution()
    {
        using var fixture = new Fixture();
        byte[] before = SHA256.HashData(File.ReadAllBytes(fixture.Path));
        using (var db = fixture.Open())
        {
            db.Overview("timing-1", null, 0, 25);
            db.Samples("timing-1", null, null, null, null);
        }
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(fixture.Path)));
        Assert.False(File.Exists(fixture.Path + "-wal"));
        fixture.Execute("DROP TABLE CpuSample; CREATE VIEW CpuSample AS WITH RECURSIVE x(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM x WHERE n<1000000000) SELECT 0 Core,n Timestamp,180388626439 ProcThreadId FROM x;");
        using var cancel = new CancellationTokenSource();
        using var database = fixture.Open(cancel.Token);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(50));
        Assert.ThrowsAny<OperationCanceledException>(() => database.Overview("timing-1", null, 0, 25));
        // Closing the interrupted connection releases the capture, and a new query is still usable.
        database.Dispose();
        using var next = fixture.Open();
        Assert.Equal(1, next.Counters("timing-1", 42, null, 0, 25).Counters.Total);
    }

    [Fact]
    public void QueryBudgetInterruptsExpensiveSqlWithoutCancellation()
    {
        using var fixture = new Fixture();
        fixture.Execute("DROP TABLE CpuSample; CREATE VIEW CpuSample AS WITH RECURSIVE x(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM x WHERE n<1000000000) SELECT 0 Core,n Timestamp,180388626439 ProcThreadId FROM x;");
        using var database = fixture.Open(timeoutSeconds: 0.05);
        Assert.Equal("timing_query_timeout", Assert.Throws<PixToolException>(() => database.Overview("timing-1", null, 0, 25)).Detail.Code);
    }

    [Fact]
    public void MissingSchemasAndInvalidArgumentsUseStableErrors()
    {
        using var fixture = new Fixture();
        fixture.Execute("DROP TABLE PixGpuExecution");
        using var database = fixture.Open();
        Assert.Equal("timing_schema_unsupported", Assert.Throws<PixToolException>(() => database.Events("timing-1", "gpuMarkers", null, null, null, null, null, null, "start", 0, 25)).Detail.Code);
        Assert.Equal("unsupported", database.Overview("timing-1", null, 0, 25).Capabilities["gpuMarkers"].State);
        Assert.Equal("invalid_arguments", Assert.Throws<PixToolException>(() => database.Range(200, 100)).Detail.Code);
        Assert.Throws<PixToolException>(() => TimingDatabase.ParseNs("9223372036854775808", "startNs"));
        Assert.Throws<PixToolException>(() => TimingDatabase.ParseNs("-1", "startNs"));
        Assert.Equal(9007199254740993L, TimingDatabase.ParseNs("9007199254740993", "startNs"));
        Assert.Equal("0xFFFFFFFFFFFFFFFF", "0x" + TimingDatabase.DecodeStack(1, Enumerable.Repeat((byte)255, 8).ToArray())![0].ToString("X"));
    }

    [SkippableFact]
    public void NativeRecordedFixtureProvidesQueryableCountersAndSampleCoverage()
    {
        string? capture = TestArtifacts.TimingCapture;
        if (capture is null)
        {
            string existing = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pixmcp_timing_test.wpix");
            if (File.Exists(existing)) capture = existing;
        }
        Skip.If(capture is null || !File.Exists(capture) || PixDiscovery.InstallDir is null, "Provide PIX_TEST_TIMING_CAPTURE or an existing pixmcp_timing_test.wpix fixture.");
        byte[] before = SHA256.HashData(File.ReadAllBytes(capture!));
        using (var db = new TimingDatabase(capture!, System.IO.Path.Combine(PixDiscovery.InstallDir!, "pixstorage.dll")))
        {
            TimingOverviewDto overview = db.Overview("timing-native", null, 0, 25);
            Assert.Equal("recordedTimingCapture", overview.Provenance.Source);
            Assert.True(overview.SampleCount > 0);
            TimingCountersDto counters = db.Counters("timing-native", null, null, 0, 25);
            Assert.NotEmpty(counters.Counters.Items);
            TimingSampleAnalysisDto samples = db.Samples("timing-native", null, null, null, null);
            Assert.True(samples.Coverage.TotalSamples > 0);
            Assert.True(samples.Coverage.SamplesWithStacks > 0);
            Assert.Equal(samples.Coverage.TotalSamples, samples.Coverage.SamplesWithStacks + samples.Coverage.SamplesWithoutStacks);
        }
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(capture!)));
    }

    [SkippableFact]
    public void NamedNativeFixtureResolvesExpectedFunctionLeafAndCpuEvents()
    {
        string? capture = TestArtifacts.TimingCapture;
        Skip.If(capture is null || PixDiscovery.InstallDir is null, "Set PIX_TEST_TIMING_CAPTURE to the generated named/PDB timing fixture.");
        using var db = new TimingDatabase(capture!, System.IO.Path.Combine(PixDiscovery.InstallDir!, "pixstorage.dll"));
        TimingEventsDto events = db.Events("timing-native", "cpu", null, null, null, "Fixture CPU Work", null, null, "start", 0, 25);
        Assert.NotEmpty(events.Events.Items);
        uint pid = events.Events.Items[0].ProcessId ?? throw new Xunit.Sdk.XunitException("CPU event rows carry their process id.");
        TimingSampleAnalysisDto samples = db.Samples("timing-native", pid, null, null, null);
        Assert.True(samples.Coverage.SamplesWithStacks > 0);
        Assert.Contains(samples.Hotspots, h => h.Function.Function?.Contains("FixtureTimingInner", StringComparison.Ordinal) == true && h.ExclusiveSamples > 0);
        Assert.Contains(samples.Hotspots, h => h.Function.Function?.Contains("FixtureTimingOuter", StringComparison.Ordinal) == true && h.InclusiveSamples > 0);
        // Verify decoded native stack direction, not just that both symbols occurred.
        // These noinline fixture functions deliberately retain their caller frames.
        var nodes = samples.Nodes.ToDictionary(n => n.NodeId);
        Assert.Contains(samples.Nodes, inner =>
            HasFunction(inner, "FixtureTimingInner") && inner.ExclusiveSamples > 0 &&
            inner.ParentNodeId is string middleId && nodes.TryGetValue(middleId, out TimingCallNodeDto? middle) &&
            HasFunction(middle, "FixtureTimingMiddle") && middle.InclusiveSamples >= inner.InclusiveSamples &&
            middle.ParentNodeId is string outerId && nodes.TryGetValue(outerId, out TimingCallNodeDto? outer) &&
            HasFunction(outer, "FixtureTimingOuter") && outer.InclusiveSamples >= middle.InclusiveSamples);
        Assert.Contains(db.Counters("timing-native", pid, "Fixture Frame Number", 0, 25).Counters.Items, c => c.Name == "Fixture Frame Number");

        static bool HasFunction(TimingCallNodeDto node, string name)
            => node.Function?.Function?.Contains(name, StringComparison.Ordinal) == true;
    }

    [SkippableFact]
    public async Task NativeToolQueriesShareSnapshotsAndInvalidateProfilesAfterSave()
    {
        string source = TestArtifacts.TimingCapture
            ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pixmcp_timing_test.wpix");
        Skip.If(!File.Exists(source) || PixDiscovery.InstallDir is null, "Provide an existing timing capture.");
        DirectoryInfo directory = Directory.CreateTempSubdirectory("pixmcp-timing-lifecycle-");
        try
        {
            string path = System.IO.Path.Combine(directory.FullName, "capture.wpix");
            File.Copy(source, path);
            using var worker = new PixWorker();
            using var session = new PixSession(worker, NullLogger<PixSession>.Instance);
            var jobs = new JobManager(worker, session, () => null);
            JsonElement opened = JsonSerializer.Deserialize<JsonElement>(await TimingCaptureTools.Open(session, path));
            string handle = opened.GetProperty("handle").GetString()!;
            using (var release = new ManualResetEventSlim())
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task blocker = worker.Run(() => { entered.SetResult(); release.Wait(TimeSpan.FromSeconds(10)); });
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                try
                {
                    // Recorded-timing queries run off the PIX worker: a busy worker does not delay them.
                    JsonElement overview = JsonSerializer.Deserialize<JsonElement>(await TimingQueryTools.Overview(session, jobs, handle, waitSeconds: 8).WaitAsync(TimeSpan.FromSeconds(9)));
                    Assert.False(overview.TryGetProperty("pending", out _));
                    Assert.False(blocker.IsCompleted);
                    Assert.Single(jobs.All);
                }
                finally { release.Set(); await blocker; }
            }
            string first = await TimingQueryTools.Overview(session, jobs, handle, waitSeconds: 10);
            string again = await TimingQueryTools.Overview(session, jobs, handle, waitSeconds: 10);
            Assert.Equal(first, again);
            Assert.Single(jobs.All);
            JsonElement hotspots = JsonSerializer.Deserialize<JsonElement>(await TimingQueryTools.Hotspots(session, jobs, handle, limit: 1, waitSeconds: 10));
            string profile = hotspots.GetProperty("profileRef").GetString()!;
            JsonElement tree = JsonSerializer.Deserialize<JsonElement>(await TimingQueryTools.Calltree(session, jobs, handle, profileRef: profile, limit: 1));
            Assert.Equal(profile, tree.GetProperty("profileRef").GetString());
            Assert.Equal(2, jobs.All.Count);
            OutputSchemaTests.AssertMatches(hotspots, StructuredToolResults.SchemaFor("pix_timing_hotspots"));
            OutputSchemaTests.AssertMatches(tree, StructuredToolResults.SchemaFor("pix_timing_calltree"));
            await TimingCaptureTools.Save(session, handle);
            PixToolException error = await Assert.ThrowsAsync<PixToolException>(() => TimingQueryTools.Calltree(session, jobs, handle, profileRef: profile));
            Assert.Equal("result_expired", error.Detail.Code);
            string updated = await TimingQueryTools.Hotspots(session, jobs, handle, limit: 1, waitSeconds: 10);
            Assert.NotEqual(profile, JsonSerializer.Deserialize<JsonElement>(updated).GetProperty("profileRef").GetString());
            // Serialization runs off the PIX worker: saving may invalidate the native query
            // while its immutable result is still being written. It must not revive a profile.
            using (var release = new ManualResetEventSlim())
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                TimingCaptureHandle capture = session.Get<TimingCaptureHandle>(handle);
                Job slow = capture.QueryJob(jobs, session.Results, "test", new { query = "blocked-serialization" },
                    _ => new BlockingTimingResult(entered, release), out int generation);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                try { await TimingCaptureTools.Save(session, handle).WaitAsync(TimeSpan.FromSeconds(10)); }
                finally { release.Set(); }
                await slow.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
                Assert.Equal(JobStatus.Succeeded, slow.Status);
                Assert.Equal("timing_query_invalidated", Assert.Throws<PixToolException>(() => capture.RememberProfile(slow.ResultRef!, generation)).Detail.Code);
                Assert.False(capture.OwnsProfile(slow.ResultRef!));
            }
            await session.Run(() => session.Close(handle));
            Assert.False(session.Results.IsAvailable(profile));
        }
        finally { directory.Delete(true); }
    }

    [SkippableFact]
    public async Task NativeSqlRunsOverPixStorageAndWritersInterruptRunningQueries()
    {
        string? source = TestArtifacts.TimingCapture;
        Skip.If(source is null || !File.Exists(source) || PixDiscovery.InstallDir is null, "Set PIX_TEST_TIMING_CAPTURE to a timing capture.");
        DirectoryInfo directory = Directory.CreateTempSubdirectory("pixmcp-timing-sql-native-");
        try
        {
            string path = System.IO.Path.Combine(directory.FullName, "capture.wpix");
            File.Copy(source!, path);
            using var worker = new PixWorker();
            using var session = new PixSession(worker, NullLogger<PixSession>.Instance);
            using var jobs = new JobManager(worker, session, () => null);
            string handle = JsonSerializer.Deserialize<JsonElement>(await TimingCaptureTools.Open(session, path)).GetProperty("handle").GetString()!;
            TimingCaptureHandle capture = session.Get<TimingCaptureHandle>(handle);

            SqlResultDto? executions = null, switches = null, stack = null;
            PixToolException? pragma = null;
            Job sql = capture.QueryJob(jobs, session.Results, "test", new { query = "native-sql" }, db =>
            {
                executions = SqlQuery.Execute(db, new SqlRequest("SELECT COUNT(*) FROM PixCpuExecution"), "pixstorage", handle);
                switches = SqlQuery.Execute(db, new SqlRequest("SELECT * FROM ContextSwitch LIMIT 5") { Explain = true }, "pixstorage", handle);
                stack = SqlQuery.Execute(db, new SqlRequest("SELECT typeof(findstackid(0, 0))"), "pixstorage", handle);
                try { SqlQuery.Execute(db, new SqlRequest("PRAGMA table_xinfo(ContextSwitch)"), "pixstorage", handle); }
                catch (PixToolException ex) { pragma = ex; }
                return new { done = true };
            }, out _);
            await sql.WaitAsync(TimeSpan.FromSeconds(120), CancellationToken.None);
            Assert.True(sql.Status == JobStatus.Succeeded, sql.Error);
            Assert.True(Convert.ToInt64(executions!.Rows[0][0]) > 0);
            Assert.Contains("PixCpuExecution", executions.ReferencedTables);
            Assert.Equal(5, switches!.RowCount);
            Assert.Contains("ContextSwitch", switches.ReferencedTables);
            Assert.NotEmpty(switches.Plan!);
            Assert.Equal(1, stack!.RowCount);
            Assert.Equal("sql_forbidden", pragma?.Detail.Code);

            const string bomb = "WITH RECURSIVE x(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM x) SELECT count(*) FROM x";
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Job running = capture.QueryJob(jobs, session.Results, "test", new { query = "bomb-save" }, db =>
            {
                started.TrySetResult();
                return SqlQuery.Execute(db, new SqlRequest(bomb) { TimeoutSeconds = 120 }, "pixstorage", handle);
            }, out _);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Task.Delay(200);
            await TimingCaptureTools.Save(session, handle).WaitAsync(TimeSpan.FromSeconds(10));
            await running.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.Equal(JobStatus.Failed, running.Status);
            Assert.Equal("timing_query_invalidated", running.ErrorDetail?.Code);

            var closingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Job closing = capture.QueryJob(jobs, session.Results, "test", new { query = "bomb-close" }, db =>
            {
                closingStarted.TrySetResult();
                return SqlQuery.Execute(db, new SqlRequest(bomb) { TimeoutSeconds = 120 }, "pixstorage", handle);
            }, out _);
            await closingStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Task.Delay(200);
            await session.Run(() => session.Close(handle)).WaitAsync(TimeSpan.FromSeconds(15));
            await closing.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.Equal("timing_query_invalidated", closing.ErrorDetail?.Code);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public void SamplesFilterAndCountByCoreEfficiencyClass()
    {
        using (var plain = new Fixture())
        using (var database = plain.Open())
        {
            Assert.Null(database.Samples("timing-1", 42, 7, null, null).Coverage.SamplesByEfficiencyClass);
            Assert.Equal("timing_schema_unsupported", Assert.Throws<PixToolException>(() => database.Samples("timing-1", 42, 7, null, null, efficiencyClass: 0)).Detail.Code);
        }

        using var fixture = new Fixture();
        // Core 0 is a performance core (class 1); cores 1 and 2 are efficiency cores (class 0).
        fixture.Execute("CREATE TABLE PhysicalCores(Id INTEGER PRIMARY KEY, EfficiencyClass INTEGER); INSERT INTO PhysicalCores VALUES(0,1),(1,0),(2,0);" +
            "CREATE TABLE Cores(Id INTEGER PRIMARY KEY, StartingThreadId INTEGER, ContextSwitchCount INTEGER, MaxPixEventLevel INTEGER, SampleCount INTEGER, PhysicalCoreId INTEGER);" +
            "INSERT INTO Cores VALUES(0,0,0,0,0,0),(1,0,0,0,0,1),(2,0,0,0,0,2);");
        using var db = fixture.Open();
        TimingSampleAnalysisDto every = db.Samples("timing-1", 42, 7, null, null);
        Assert.Equal(3, every.Coverage.TotalSamples);
        Assert.Equal(new Dictionary<string, long> { ["0"] = 1, ["1"] = 2 }, every.Coverage.SamplesByEfficiencyClass);
        Assert.NotEmpty(every.Hotspots);
        Assert.All(every.Hotspots, h => Assert.Equal(h.InclusiveSamples, h.InclusiveByEfficiencyClass!.Values.Sum()));

        TimingSampleAnalysisDto performance = db.Samples("timing-1", 42, 7, null, null, efficiencyClass: 1);
        Assert.Equal((2L, 1L), (performance.Coverage.TotalSamples, performance.Selection.EfficiencyClass!.Value));
        Assert.Equal(new Dictionary<string, long> { ["1"] = 2 }, performance.Coverage.SamplesByEfficiencyClass);
        Assert.Equal(1, db.Samples("timing-1", 42, 7, null, null, efficiencyClass: 0).Coverage.TotalSamples);

        PixToolException unknown = Assert.Throws<PixToolException>(() => db.Samples("timing-1", 42, 7, null, null, efficiencyClass: 5));
        Assert.Equal("invalid_arguments", unknown.Detail.Code);
        Assert.Contains("0, 1", unknown.Detail.Message);
    }

    private sealed class BlockingTimingResult(TaskCompletionSource entered, ManualResetEventSlim release)
    {
        public int Value
        {
            get { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException(); return 1; }
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("pixmcp-timing-sql-");
        private readonly Dictionary<(long thread, long timestamp), long> _stackIds = new();
        public string Path => System.IO.Path.Combine(_directory.FullName, "timing.sqlite");
        public Fixture()
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE CaptureFacts(Id INTEGER PRIMARY KEY,Value INTEGER);
                INSERT INTO CaptureFacts VALUES(2,100),(24,500);
                CREATE TABLE Strings(Id INTEGER PRIMARY KEY,Value TEXT);
                INSERT INTO Strings VALUES(1,'fixture.exe'),(2,'other.exe'),(3,'Main'),(4,'Frame'),(5,'GPU Frame'),(6,'Graphics'),(7,'DIRECT'),(8,'Fixture Frame Number'),(9,'Recorded frame index'),(10,'count'),(11,'Fixture'),(12,'Counters');
                CREATE TABLE Processes(Id INTEGER PRIMARY KEY,ProcessId INTEGER,ImageNameId INTEGER);
                INSERT INTO Processes VALUES(1,42,1),(2,43,2);
                CREATE TABLE Threads(Id INTEGER PRIMARY KEY,ProcThreadId INTEGER,ThreadNameId INTEGER,ProcessRowId INTEGER,SampleCount INTEGER);
                INSERT INTO Threads VALUES(1,180388626439,3,1,3),(2,184683593735,3,2,1);
                CREATE TABLE PixEventInfo(Id INTEGER PRIMARY KEY,NameId INTEGER);
                INSERT INTO PixEventInfo VALUES(1,4),(2,5);
                CREATE TABLE PixCpuExecution(BeginTimestamp INTEGER,EndTimestamp INTEGER,Level INTEGER,ThreadRowId INTEGER,EventId INTEGER,Color INTEGER);
                INSERT INTO PixCpuExecution VALUES(50,150,0,1,1,0),(100,100,0,1,1,0),(200,240,0,1,1,0),(500,600,0,1,1,0),(50,100,0,1,1,0);
                CREATE TABLE PixCpuExecutionTimes(Duration INTEGER,Execution INTEGER,Stall INTEGER,EventId INTEGER,BeginTimestamp INTEGER,EndTimestamp INTEGER);
                INSERT INTO PixCpuExecutionTimes VALUES(100,20,80,1,50,150);
                CREATE TABLE ApiCommandQueue(Id INTEGER PRIMARY KEY,ProcessId INTEGER,NameId INTEGER,TypeId INTEGER);
                INSERT INTO ApiCommandQueue VALUES(1,1,6,7);
                CREATE TABLE PixGpuExecution(BeginTimestamp INTEGER,EndTimestamp INTEGER,Level INTEGER,ApiCommandQueueId INTEGER,EventId INTEGER,Color INTEGER);
                INSERT INTO PixGpuExecution VALUES(100,400,0,1,2,0);
                CREATE TABLE PixCounterGroup(Id INTEGER PRIMARY KEY,ParentGroupId INTEGER,NameId INTEGER);
                INSERT INTO PixCounterGroup VALUES(1,NULL,11),(2,1,12);
                CREATE TABLE PixCounterInfo(Id INTEGER PRIMARY KEY,GroupId INTEGER,ProcessId INTEGER,NameId INTEGER,DescriptionId INTEGER,UnitsId INTEGER);
                INSERT INTO PixCounterInfo VALUES(1,2,1,8,9,10);
                CREATE TABLE PixCounters(CounterId INTEGER,Timestamp INTEGER,Value REAL);
                INSERT INTO PixCounters VALUES(1,99,0),(1,100,1),(1,499,2),(1,500,3);
                CREATE TABLE CpuSample(Core INTEGER,Timestamp INTEGER,ProcThreadId INTEGER);
                INSERT INTO CpuSample VALUES(0,100,180388626439),(1,200,180388626439),(0,300,180388626439),(2,400,184683593735),(0,500,180388626439);
                CREATE TABLE Stacks(Id INTEGER PRIMARY KEY,NumFrames INTEGER,Addresses BLOB);
                CREATE TABLE StackEvents(Id INTEGER PRIMARY KEY,OSThreadId INTEGER,StartTimestamp INTEGER,EndTimestamp INTEGER,StackEventData BLOB,EventCount INTEGER);
                CREATE TABLE Images(Id INTEGER PRIMARY KEY,OSProcessId INTEGER,PELoadAddress INTEGER,LoadSize INTEGER,LoadTimestamp INTEGER,UnloadTimestamp INTEGER,FilePathId INTEGER,ModuleId INTEGER);
                INSERT INTO Images VALUES(1,42,4096,4096,0,500,1,1),(2,43,4096,4096,0,500,2,2);
                CREATE TABLE FunctionInformation(Id INTEGER PRIMARY KEY,ModuleId INTEGER,Offset INTEGER,Size INTEGER,DecoratedNameId INTEGER);
                INSERT INTO FunctionInformation VALUES(1,1,16,16,1),(2,1,32,16,2),(3,1,48,16,3),(4,2,16,16,4);
                CREATE TABLE SymbolStrings(Id INTEGER PRIMARY KEY,Value TEXT);
                INSERT INTO SymbolStrings VALUES(1,'FixtureTimingInner'),(2,'FixtureTimingMiddle'),(3,'FixtureTimingOuter'),(4,'OtherProcessFunction'),(5,'fixture.cpp');
                CREATE TABLE SourceFile(Id INTEGER PRIMARY KEY,SourceFileNameId INTEGER);
                INSERT INTO SourceFile VALUES(1,5);
                CREATE TABLE SourceLine(Id INTEGER PRIMARY KEY,ModuleId INTEGER,SourceFileId INTEGER,Offset INTEGER,Length INTEGER,LineStart INTEGER);
                INSERT INTO SourceLine VALUES(1,1,1,16,16,17);
                """;
            command.ExecuteNonQuery();
            AddStack(connection, 1, 100, [0x1012, 0x1022, 0x1032]);
            AddStack(connection, 2, 200, [0x1015, 0x1025, 0x1018, 0x1035]);
            AddStack(connection, 3, 400, [0x1014]);
        }

        public TimingDatabase Open(CancellationToken token = default, double timeoutSeconds = 120) => new(Path, null, token, timeoutSeconds, configureForTests: c =>
            c.CreateFunction<long, long, long>("FindStackId", (thread, timestamp) => _stackIds.GetValueOrDefault((thread, timestamp))));

        public void Execute(string sql)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString());
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }

        private void AddStack(SqliteConnection connection, int id, long timestamp, ulong[] addresses)
        {
            _stackIds.Add((7, timestamp), id);
            byte[] bytes = new byte[addresses.Length * 8], sample = new byte[16];
            for (int i = 0; i < addresses.Length; i++) BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(i * 8), addresses[i]);
            BinaryPrimitives.WriteInt64LittleEndian(sample, timestamp); BinaryPrimitives.WriteInt64LittleEndian(sample.AsSpan(8), id);
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO Stacks VALUES($id,$frames,$addresses); INSERT INTO StackEvents VALUES($id,7,$time,$time,$sample,1)";
            command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$frames", addresses.Length);
            command.Parameters.AddWithValue("$addresses", bytes); command.Parameters.AddWithValue("$time", timestamp); command.Parameters.AddWithValue("$sample", sample);
            command.ExecuteNonQuery();
        }

        public void Dispose() => _directory.Delete(true);
    }
}
