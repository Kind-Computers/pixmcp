using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using PixMcp.Pix;
using PixMcp.Pix.Handles;
using PixMcp.Tools;
using ToolHelpers = PixMcp.Tools.Tools;
using Xunit;

namespace PixMcp.Tests;

/// <summary>R04: job results that cannot be retained, evicted results, origins, stable codes and the preparation gate.</summary>
public sealed class JobLifecycleTests
{
    private static CallToolRequestParams Request(string tool, object arguments) => new()
    {
        Name = tool,
        Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(arguments))!,
    };

    [Fact]
    public async Task RetentionFailureKeepsTheJobSucceededAndOffersItsOrigin()
    {
        using var directory = new Temp();
        using var fixture = new Fixture(new ResultStore(256, 0, directory.Path));
        Job job;
        using (StructuredToolResults.WithRequest(Request("pix_test", new { handle = "gpu-1", limit = 5 })))
            job = fixture.Jobs.Start("test", "big result", _ => new string('x', 100 * 1024));
        await job.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        JobDto dto = job.ToDto();
        Assert.Equal("succeeded", dto.Status);
        Assert.Equal("retentionFailed", dto.ResultState);
        Assert.Null(dto.ResultRef);
        Assert.Equal(PixErrors.Codes.ResultCapacityExceeded, dto.ResultError!.Code);
        Assert.Equal("pix_test", dto.Origin!.Tool);
        Assert.Equal("pix_test", dto.NextCalls[0].Tool);
        Assert.Equal(5, ((JsonElement)dto.NextCalls[0].Arguments).GetProperty("limit").GetInt32());
        Assert.Equal("pix_info", dto.NextCalls[1].Tool);
        Assert.Equal(0, fixture.Session.Results.Summary().ResultCount);
    }

    [Fact]
    public async Task EvictedResultIsObservableThroughJobStatusWithTheOriginCall()
    {
        using var directory = new Temp();
        var time = new FakeTime();
        using var fixture = new Fixture(new ResultStore(0, 200, directory.Path, time));
        Job job;
        using (StructuredToolResults.WithRequest(Request("pix_gpu_events", new { handle = "gpu-1" })))
            job = fixture.Jobs.Start("test", "first", _ => new string('a', 60));
        await job.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal("available", job.ToDto().ResultState);
        // Inside the grace period the finished result is protected: pressure fails the newcomer instead.
        Assert.Equal(PixErrors.Codes.ResultCapacityExceeded, Assert.Throws<PixToolException>(() => fixture.Session.Results.Store(new string('b', 150))).Detail.Code);
        Assert.Equal("available", job.ToDto().ResultState);
        time.Advance(TimeSpan.FromSeconds(ResultStore.EvictionGraceSeconds + 1));
        fixture.Session.Results.Store(new string('b', 150));
        JobDto evicted = job.ToDto();
        Assert.Equal("evicted", evicted.ResultState);
        Assert.Null(evicted.ResultRef);
        Assert.Equal("succeeded", evicted.Status);
        Assert.Equal("pix_gpu_events", Assert.Single(evicted.NextCalls).Tool);
        Assert.Same(job, fixture.Jobs.Get(job.Id)); // the job is not forgotten by the eviction
    }

    [Fact]
    public async Task UnknownAndFinishedJobsUseStableCodesWithRecoveryCalls()
    {
        using var fixture = new Fixture();
        PixToolException unknown = Assert.Throws<PixToolException>(() => fixture.Jobs.Get("job-404"));
        Assert.Equal(PixErrors.Codes.UnknownJob, unknown.Detail.Code);
        Assert.Equal("pix_jobs", Assert.Single(unknown.Detail.NextCalls).Tool);
        Job job = fixture.Jobs.Start("test", "done", _ => new { ok = true });
        await job.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        PixToolException finished = Assert.Throws<PixToolException>(() => SessionTools.JobCancel(fixture.Jobs, job.Id));
        Assert.Equal(PixErrors.Codes.JobAlreadyFinished, finished.Detail.Code);
        Assert.Equal(new[] { "pix_job_status", "pix_result_read" }, finished.Detail.NextCalls.Select(c => c.Tool));
        Assert.Equal(PixErrors.Codes.JobAlreadyFinished, Assert.Throws<PixToolException>(() => fixture.Jobs.Cancel(job)).Detail.Code);
    }

    [Fact]
    public async Task FiftyOriginCarryingJobsStayUnderTheInlineBudget()
    {
        using var fixture = new Fixture();
        var jobs = new List<Job>();
        using (StructuredToolResults.WithRequest(Request("pix_gpu_timing_events", new { handle = "gpu-1", kind = "work", limit = 25 })))
            for (int i = 0; i < 50; i++) jobs.Add(fixture.Jobs.Start("test", $"job {i}", _ => new { i }));
        foreach (Job job in jobs) await job.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        string json = SessionTools.Jobs(fixture.Jobs);
        Assert.True(Encoding.UTF8.GetByteCount(json) < ResultStore.TargetBytes, $"pix_jobs is {Encoding.UTF8.GetByteCount(json)} bytes");
        JsonElement first = JsonDocument.Parse(json).RootElement.GetProperty("items")[0];
        Assert.Equal("pix_gpu_timing_events", first.GetProperty("origin").GetProperty("tool").GetString());
        Assert.Equal("available", first.GetProperty("resultState").GetString());
    }

    [Fact]
    public void OriginArgumentsAreCappedToHandlesAndShortScalars()
    {
        using (StructuredToolResults.WithRequest(Request("pix_gpu_sql", new { handle = "gpu-1", sql = new string('s', 4096), limit = 10 })))
        {
            ToolCallDto origin = StructuredToolResults.CurrentCall()!;
            Assert.Equal("pix_gpu_sql", origin.Tool);
            JsonElement arguments = (JsonElement)origin.Arguments;
            Assert.Equal("gpu-1", arguments.GetProperty("handle").GetString());
            Assert.Equal(10, arguments.GetProperty("limit").GetInt32());
            Assert.False(arguments.TryGetProperty("sql", out _));
            Assert.True(arguments.GetProperty("argumentsTruncated").GetBoolean());
        }
        Assert.Null(StructuredToolResults.CurrentCall());
    }

    [Fact]
    public async Task ParallelCallersStartExactlyOnePreparationJob()
    {
        using var fixture = new Fixture();
        using var gate = new Gate();
        FakeHandle handle = await fixture.Worker.Run(() => fixture.Session.Register(new FakeHandle { PrepareGate = gate }));
        Job[] started = new Job[32];
        Parallel.For(0, started.Length, i => started[i] = ToolHelpers.StartPreparation(fixture.Session, fixture.Jobs, handle.Id, Preparation(handle.Id)));
        Assert.All(started, job => Assert.Same(started[0], job));
        Assert.Single(fixture.Jobs.All);
        gate.Release.Set();
        await started[0].WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(1, handle.Prepared);
    }

    [Fact]
    public async Task RelatedRunningJobIsJoinedAndTheOwnPreparationStartsOnlyWhenStillNeeded()
    {
        using var fixture = new Fixture();
        using var gate = new Gate();
        FakeHandle handle = await fixture.Worker.Run(() => fixture.Session.Register(new FakeHandle { PrepareGate = gate }));
        // A "related" job that starts a different state; joining it must not queue a second replay while it runs.
        Job related = ToolHelpers.StartPreparation(fixture.Session, fixture.Jobs, handle.Id,
            new Preparation<FakeHandle>("related", "fake", "Related work", h => h.Related, (h, job) => { h.Prepare(job); h.Related = true; }));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Preparation<FakeHandle> joining = Preparation(handle.Id) with { JoinKeys = ["related"] };
        Task<string> query = ToolHelpers.RunWhenReady(fixture.Session, fixture.Jobs, "pix_test", handle.Id, joining, h => new { ok = true }, 5, CancellationToken.None);
        JsonElement pending = JsonDocument.Parse(await ToolHelpers.RunWhenReady(fixture.Session, fixture.Jobs, "pix_test", handle.Id, joining, h => new { }, 0, CancellationToken.None)).RootElement;
        Assert.Equal(related.Id, pending.GetProperty("jobId").GetString());
        Assert.Single(fixture.Jobs.All);
        // The related job sets Ready too, so the joining caller needs no job of its own.
        gate.Release.Set();
        Assert.True(JsonDocument.Parse(await query).RootElement.GetProperty("ok").GetBoolean());
        Assert.Single(fixture.Jobs.All);
        Assert.Equal(1, handle.Prepared);
    }

    [Fact]
    public async Task JoinedJobThatDoesNotCoverThePreparationIsFollowedByTheOwnJob()
    {
        using var fixture = new Fixture();
        using var gate = new Gate();
        FakeHandle handle = await fixture.Worker.Run(() => fixture.Session.Register(new FakeHandle()));
        Job related = ToolHelpers.StartPreparation(fixture.Session, fixture.Jobs, handle.Id,
            new Preparation<FakeHandle>("related", "fake", "Related work", h => h.Related, (h, job) => { gate.Block(); h.Related = true; }));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Preparation<FakeHandle> joining = Preparation(handle.Id) with { JoinKeys = ["related"] };
        Task<string> query = ToolHelpers.RunWhenReady(fixture.Session, fixture.Jobs, "pix_test", handle.Id, joining, h => new { ok = true }, 5, CancellationToken.None);
        gate.Release.Set();
        Assert.True(JsonDocument.Parse(await query).RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(2, fixture.Jobs.All.Count);
        Assert.Equal(1, handle.Prepared);
        Assert.Same(fixture.Jobs.All.Last(), handle.PreparationJobs["fake"]);
    }

    [Fact]
    public async Task ClosingTheHandleClearsItsPreparationsUnderTheGate()
    {
        using var fixture = new Fixture();
        FakeHandle handle = await fixture.Worker.Run(() => fixture.Session.Register(new FakeHandle { Ready = true }));
        await fixture.Worker.Run(() => fixture.Session.Close(handle.Id));
        Assert.Empty(handle.PreparationJobs);
        Assert.Contains("Unknown handle", Assert.ThrowsAny<Exception>(() => ToolHelpers.StartPreparation(fixture.Session, fixture.Jobs, handle.Id, Preparation(handle.Id))).Message);
        Assert.Empty(fixture.Jobs.All);
    }

    [Theory]
    [InlineData(0.0, false, 0.0)]
    [InlineData(0.0, true, 1.0)]
    [InlineData(0.4, true, 1.0)]
    [InlineData(2.5, true, 2.5)]
    [InlineData(2.5, false, 2.5)]
    public void AdmissionBudgetIsRaisedOnlyAfterAPreparation(double remaining, bool afterPreparation, double expected)
        => Assert.Equal(TimeSpan.FromSeconds(expected), ToolHelpers.AdmissionBudget(TimeSpan.FromSeconds(remaining), afterPreparation));

    [Fact]
    public async Task ReadyPreparationIsNotLostToABusyQueueWhenTheWaitIsExhausted()
    {
        using var fixture = new Fixture();
        using var gate = new Gate();
        FakeHandle handle = await fixture.Worker.Run(() => fixture.Session.Register(new FakeHandle { PrepareGate = gate }));
        Task<string> query = ToolHelpers.RunWhenReady(fixture.Session, fixture.Jobs, "pix_test", handle.Id, Preparation(handle.Id), h => new { ok = true }, 1.0, CancellationToken.None);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Occupy the worker right after the preparation finishes, for longer than the caller's remaining budget.
        Task blocker = fixture.Worker.Run(() => Thread.Sleep(300));
        await Task.Delay(800);
        gate.Release.Set();
        Assert.True(JsonDocument.Parse(await query.WaitAsync(TimeSpan.FromSeconds(10))).RootElement.GetProperty("ok").GetBoolean());
        await blocker;
    }

    [Fact]
    public void AnalysisSettingsConflictCarriesTheStopCall()
    {
        var options = new AnalysisOptions(1, 2, Microsoft.PIX.PIX_ANALYSIS_FLAGS.PIX_ANALYSIS_ENABLE_DEBUG_LAYER);
        PixToolException error = Assert.Throws<PixToolException>(() => options.ValidateRunningRequest(new(PowerState: 3), "gpu-1"));
        Assert.Equal(PixErrors.Codes.AnalysisSettingsConflict, error.Detail.Code);
        Assert.Equal("pix_gpu_analysis_stop", Assert.Single(error.Detail.NextCalls).Tool);
        Assert.Contains("pix_gpu_analysis_stop", error.Detail.Message);
    }

    private static Preparation<FakeHandle> Preparation(string handle)
        => new("fake", "fake", $"Prepare {handle}", h => h.Ready, (h, job) => h.Prepare(job));

    private sealed class FakeHandle : PixHandle
    {
        public FakeHandle() : base("fake") { }
        public override string Kind => "fake";
        public bool Ready { get; set; }
        public bool Related { get; set; }
        public int Prepared;
        public Gate? PrepareGate { get; init; }
        public void Prepare(Job job)
        {
            Interlocked.Increment(ref Prepared);
            PrepareGate?.Block();
            Ready = true;
        }
        public override void Close(List<string> warnings) { }
    }

    private sealed class Gate : IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public void Block()
        {
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test gate was not released.");
        }
        public void Dispose() => Release.Set();
    }

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Temp : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pixmcp-job-tests-" + Guid.NewGuid().ToString("N"));
        internal Temp() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class Fixture : IDisposable
    {
        public PixWorker Worker { get; } = new();
        public PixSession Session { get; }
        public JobManager Jobs { get; }
        public Fixture(ResultStore? results = null)
        {
            Session = new PixSession(Worker, NullLogger<PixSession>.Instance, results);
            Jobs = new JobManager(Worker, Session, () => null);
        }
        public void Dispose() { Session.Dispose(); Worker.Dispose(); }
    }
}
