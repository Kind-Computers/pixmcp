using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using PixMcp.Pix;
using PixMcp.Pix.Handles;
using PixMcp.Tools;
using ToolHelpers = PixMcp.Tools.Tools;
using Xunit;

namespace PixMcp.Tests;

/// <summary>ToolHelpers.RunWhenReady: query tools prepare expensive state in a job instead of blocking the PIX thread.</summary>
public sealed class ReadinessTests
{
    [Fact]
    public async Task ReadyHandleAnswersDirectlyWithoutStartingAJob()
    {
        using var fixture = new Fixture();
        FakeHandle handle = await fixture.Worker.Run(() => fixture.Session.Register(new FakeHandle { Ready = true }));
        string json = await ToolHelpers.RunWhenReady(fixture.Session, fixture.Jobs, "pix_test", handle.Id, Preparation(handle.Id),
            h => new { answer = 42 }, waitSeconds: 0, CancellationToken.None);
        Assert.Equal(42, JsonDocument.Parse(json).RootElement.GetProperty("answer").GetInt32());
        Assert.Empty(fixture.Jobs.All);
        Assert.Equal(0, handle.Prepared);
    }

    [Fact]
    public async Task UnreadyHandleReturnsPendingWhenTheWaitElapsesAndAnswersOncePrepared()
    {
        using var fixture = new Fixture();
        using var gate = new Gate();
        FakeHandle handle = await fixture.Worker.Run(() => fixture.Session.Register(new FakeHandle { PrepareGate = gate }));
        string pendingJson = await ToolHelpers.RunWhenReady(fixture.Session, fixture.Jobs, "pix_test", handle.Id, Preparation(handle.Id),
            h => new { answer = 42 }, waitSeconds: 0.05, CancellationToken.None);
        JsonElement pending = JsonDocument.Parse(pendingJson).RootElement;
        Assert.True(pending.GetProperty("pending").GetBoolean());
        Assert.Equal("pix_test", pending.GetProperty("retry").GetString());
        string jobId = pending.GetProperty("jobId").GetString()!;
        Assert.Equal("running", pending.GetProperty("job").GetProperty("status").GetString());
        Assert.True(StructuredToolResults.IsPending(pending));
        Assert.Same(fixture.Jobs.Get(jobId), handle.PreparationJobs["fake"]);

        gate.Release.Set();
        await fixture.Jobs.Get(jobId).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        string json = await ToolHelpers.RunWhenReady(fixture.Session, fixture.Jobs, "pix_test", handle.Id, Preparation(handle.Id),
            h => new { answer = 42 }, waitSeconds: 0, CancellationToken.None);
        Assert.Equal(42, JsonDocument.Parse(json).RootElement.GetProperty("answer").GetInt32());
        Assert.Equal(1, handle.Prepared);
        Assert.Single(fixture.Jobs.All);
    }

    [Fact]
    public async Task ConcurrentCallersJoinTheRunningPreparationJob()
    {
        using var fixture = new Fixture();
        using var gate = new Gate();
        FakeHandle handle = await fixture.Worker.Run(() => fixture.Session.Register(new FakeHandle { PrepareGate = gate }));
        Task<string> first = ToolHelpers.RunWhenReady(fixture.Session, fixture.Jobs, "pix_test", handle.Id, Preparation(handle.Id), h => new { n = 1 }, 5, CancellationToken.None);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<string> second = ToolHelpers.RunWhenReady(fixture.Session, fixture.Jobs, "pix_test", handle.Id, Preparation(handle.Id), h => new { n = 2 }, 5, CancellationToken.None);
        // The second caller's readiness probe queues behind the running preparation; release once it is enqueued.
        while (fixture.Worker.PendingCount == 0) await Task.Delay(10);
        gate.Release.Set();
        Assert.Equal(1, JsonDocument.Parse(await first).RootElement.GetProperty("n").GetInt32());
        Assert.Equal(2, JsonDocument.Parse(await second).RootElement.GetProperty("n").GetInt32());
        Assert.Single(fixture.Jobs.All);
        Assert.Equal(1, handle.Prepared);
    }

    [Fact]
    public async Task FailedPreparationSurfacesAsToolErrorAndIsRetriedNextTime()
    {
        using var fixture = new Fixture();
        FakeHandle handle = await fixture.Worker.Run(() => fixture.Session.Register(new FakeHandle { FailPreparation = true }));
        McpException error = await Assert.ThrowsAsync<McpException>(() => ToolHelpers.RunWhenReady(fixture.Session, fixture.Jobs, "pix_test", handle.Id, Preparation(handle.Id),
            h => new { }, 5, CancellationToken.None));
        Assert.Contains("failed", error.Message);
        Assert.Contains("replay broke", error.Message);

        handle.FailPreparation = false;
        string json = await ToolHelpers.RunWhenReady(fixture.Session, fixture.Jobs, "pix_test", handle.Id, Preparation(handle.Id), h => new { ok = true }, 5, CancellationToken.None);
        Assert.True(JsonDocument.Parse(json).RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(2, fixture.Jobs.All.Count);
        Assert.Equal(2, handle.Prepared);
    }

    [Fact]
    public async Task UnknownHandleFailsBeforeAnyJobStarts()
    {
        using var fixture = new Fixture();
        McpException error = await Assert.ThrowsAsync<McpException>(() => ToolHelpers.RunWhenReady(fixture.Session, fixture.Jobs, "pix_test", "fake-9", Preparation("fake-9"),
            h => new { }, 5, CancellationToken.None));
        Assert.Contains("Unknown handle", error.Message);
        Assert.Empty(fixture.Jobs.All);
    }

    [Fact]
    public async Task ExplicitJobIsJoinedByLaterQueries()
    {
        using var fixture = new Fixture();
        using var gate = new Gate();
        FakeHandle handle = await fixture.Worker.Run(() => fixture.Session.Register(new FakeHandle { PrepareGate = gate }));
        Job explicitJob = ToolHelpers.StartPreparation(fixture.Session, fixture.Jobs, handle.Id, Preparation(handle.Id));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<string> query = ToolHelpers.RunWhenReady(fixture.Session, fixture.Jobs, "pix_test", handle.Id, Preparation(handle.Id), h => new { joined = true }, 5, CancellationToken.None);
        while (fixture.Worker.PendingCount == 0) await Task.Delay(10);
        gate.Release.Set();
        Assert.True(JsonDocument.Parse(await query).RootElement.GetProperty("joined").GetBoolean());
        Assert.Same(explicitJob, Assert.Single(fixture.Jobs.All));
    }

    [Fact]
    public async Task FinishedJobsArePrunedButRunningOnesAreKept()
    {
        using var fixture = new Fixture();
        for (int i = 0; i < JobManager.MaxFinishedJobs + 5; i++)
        {
            Job job = fixture.Jobs.Start("test", $"job {i}", _ => i);
            await job.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
        using var gate = new Gate();
        Job running = fixture.Jobs.Start("test", "running", _ => { gate.Block(); return 0; });
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Job trigger = fixture.Jobs.Start("test", "trigger prune", _ => 0);
        Assert.Contains(running, fixture.Jobs.All);
        Assert.Equal(JobManager.MaxFinishedJobs + 2, fixture.Jobs.All.Count);
        Assert.Throws<McpException>(() => fixture.Jobs.Get("job-1"));
        Assert.Same(running, fixture.Jobs.Running);
        gate.Release.Set();
        await trigger.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    [Fact]
    public async Task CancelledRequestIsSkippedAtDequeueAndReportedAsCancellation()
    {
        using var fixture = new Fixture();
        using var gate = new Gate();
        Task blocker = fixture.Worker.Run(gate.Block);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        bool executed = false;
        Task<string> queued = ToolHelpers.Run(fixture.Session, "pix_test", () => executed = true, cancellation.Token);
        cancellation.Cancel();
        gate.Release.Set();
        await blocker;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.False(executed);
    }

    [Fact]
    public void PendingSchemasDescribeBothShapes()
    {
        JsonElement schema = StructuredToolResults.SchemaFor("pix_gpu_timing_events");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        JsonElement[] alternatives = schema.GetProperty("anyOf").EnumerateArray().ToArray();
        Assert.Equal(2, alternatives.Length);
        Assert.True(alternatives[0].GetProperty("properties").TryGetProperty("items", out _));
        JsonElement pending = alternatives[1].GetProperty("properties");
        Assert.Equal("boolean", pending.GetProperty("pending").GetProperty("type").GetString());
        Assert.True(pending.GetProperty("job").GetProperty("properties").TryGetProperty("jobId", out _));
        Assert.Equal("object", StructuredToolResults.SchemaFor("pix_gpu_pipeline_state").GetProperty("type").GetString());
    }

    [Fact]
    public void KindFilterMatchesApiCallDataLikeEventListing()
    {
        // pix_gpu_timing_events rows carry ApiCallData so "draw" matches the same events as pix_gpu_events.
        var marker = new EventRecord(0, 1, uint.MaxValue, "Triangle", "DrawInstanced(3, 1, 0, 0)", 0, 0);
        Assert.True(ToolHelpers.MatchesKind(marker, "draw"));
        Assert.False(ToolHelpers.MatchesKind(marker with { ApiCallData = string.Empty }, "draw"));
    }

    [Fact]
    public void ServerVersionComesFromTheAssembly()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+", ServerHost.Version);
        Assert.DoesNotContain("+", ServerHost.Version);
    }

    private static Preparation<FakeHandle> Preparation(string handle)
        => new("fake", "fake", $"Prepare {handle}", h => h.Ready, (h, job) => h.Prepare(job));

    private sealed class FakeHandle : PixHandle
    {
        public FakeHandle() : base("fake") { }
        public override string Kind => "fake";
        public bool Ready { get; set; }
        public int Prepared;
        public bool FailPreparation { get; set; }
        public Gate? PrepareGate { get; init; }
        public void Prepare(Job job)
        {
            Prepared++;
            PrepareGate?.Block();
            if (FailPreparation) throw new InvalidOperationException("replay broke");
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

    private sealed class Fixture : IDisposable
    {
        public PixWorker Worker { get; } = new();
        public PixSession Session { get; }
        public JobManager Jobs { get; }
        public Fixture()
        {
            Session = new PixSession(Worker, NullLogger<PixSession>.Instance);
            Jobs = new JobManager(Worker, Session, () => null);
        }
        public void Dispose() { Session.Dispose(); Worker.Dispose(); }
    }
}
