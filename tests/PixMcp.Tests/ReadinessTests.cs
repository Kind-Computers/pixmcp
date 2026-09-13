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
        Task<string> second = ToolHelpers.RunWhenReady(fixture.Session, fixture.Jobs, "pix_test", handle.Id, Preparation(handle.Id), h => new { n = 2 }, 0, CancellationToken.None);
        JsonElement pending = JsonDocument.Parse(await second.WaitAsync(TimeSpan.FromSeconds(1))).RootElement;
        Assert.True(pending.GetProperty("pending").GetBoolean());
        Assert.Equal(0, fixture.Worker.PendingCount);
        gate.Release.Set();
        Assert.Equal(1, JsonDocument.Parse(await first).RootElement.GetProperty("n").GetInt32());
        Assert.Single(fixture.Jobs.All);
        Assert.Equal(1, handle.Prepared);
    }

    [Fact]
    public async Task FailedPreparationSurfacesAsToolErrorAndIsRetriedNextTime()
    {
        using var fixture = new Fixture();
        FakeHandle handle = await fixture.Worker.Run(() => fixture.Session.Register(new FakeHandle { FailPreparation = true }));
        PixToolException error = await Assert.ThrowsAsync<PixToolException>(() => ToolHelpers.RunWhenReady(fixture.Session, fixture.Jobs, "pix_test", handle.Id, Preparation(handle.Id),
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
        McpException error = await Assert.ThrowsAsync<PixToolException>(() => ToolHelpers.RunWhenReady(fixture.Session, fixture.Jobs, "pix_test", "fake-9", Preparation("fake-9"),
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
        Task<string> query = ToolHelpers.RunWhenReady(fixture.Session, fixture.Jobs, "pix_test", handle.Id, Preparation(handle.Id), h => new { joined = true }, 0, CancellationToken.None);
        Assert.True(JsonDocument.Parse(await query.WaitAsync(TimeSpan.FromSeconds(1))).RootElement.GetProperty("pending").GetBoolean());
        gate.Release.Set();
        await explicitJob.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
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
        Assert.Equal(PixErrors.Codes.UnknownJob, Assert.Throws<PixToolException>(() => fixture.Jobs.Get("job-1")).Detail.Code);
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
        JsonElement schema = StructuredToolResults.CoreSchemaFor("pix_gpu_timing_events");
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

    [Theory]
    [InlineData("Triangle pass", uint.MaxValue)]
    [InlineData("<deprecated - use pix3.h instead> Frame", uint.MaxValue)]
    [InlineData("<deprecated - use pix3.h instead> Hello PixMcp!!!", 7u)]
    public void NativeMarkerLabelsMatchWithOrWithoutGpuIdentity(string name, uint gpuId)
    {
        var marker = new EventRecord(0, gpuId, uint.MaxValue, name, string.Empty, 0, 0);
        Assert.True(ToolHelpers.MatchesKind(marker, "marker"));
        Assert.False(ToolHelpers.MatchesKind(marker with { Name = "DrawIndexedInstanced", ApiCallData = "DrawIndexedInstanced(3, 1, 0, 0, 0)" }, "marker"));
        Assert.True(ToolHelpers.MatchesKind(marker with { Name = "PIXBeginEvent", ApiCallData = "some legacy marker text" }, "marker"));
    }

    [Theory]
    [InlineData("ExecuteIndirect", "", true)]
    [InlineData("ExecuteIndirect", "ExecuteIndirect(sig, 4, args, 0, null, 0)", true)]
    [InlineData("DrawInstanced", "DrawInstanced(3, 1, 0, 0)", true)]
    [InlineData("DispatchMesh", "DispatchMesh(1, 1, 1)", true)]
    [InlineData("CopyResource", "CopyResource(a, b)", false)]
    [InlineData("Frame", "", false)]
    public void WorkCoversDrawsDispatchesAndExecuteIndirect(string name, string api, bool expected)
    {
        var record = new EventRecord(0, 1, uint.MaxValue, name, api, 0, 0);
        Assert.Equal(expected, ToolHelpers.MatchesKind(record, "work"));
    }

    [Fact]
    public void ApiShapedNamesAreNeverMarkersAndLeafLabelsBecomeLabels()
    {
        var indirect = new EventRecord(0, 1, uint.MaxValue, "ExecuteIndirect", string.Empty, 0, 0);
        Assert.False(ToolHelpers.MatchesKind(indirect, "marker"));
        Assert.Equal("executeIndirect", ToolHelpers.Classify(indirect));
        var shaped = new EventRecord(0, 1, uint.MaxValue, "Foo(1,2)", string.Empty, 0, 0);
        Assert.False(ToolHelpers.MatchesKind(shaped, "marker"));
        Assert.Equal("other", ToolHelpers.Classify(shaped));
        var label = new EventRecord(0, 7, uint.MaxValue, "Hello PixMcp!!!", string.Empty, 0, 0);
        Assert.Equal("marker", ToolHelpers.Classify(label)); // no child information: stays a marker
        Assert.Equal("marker", ToolHelpers.Classify(label, hasChildren: true));
        Assert.Equal("label", ToolHelpers.Classify(label, hasChildren: false));
        Assert.True(ToolHelpers.MatchesKind(label, "label", hasChildren: false));
        Assert.False(ToolHelpers.MatchesKind(label, "marker", hasChildren: false));
        Assert.False(ToolHelpers.MatchesKind(label, "label"));
        var begin = new EventRecord(0, uint.MaxValue, uint.MaxValue, "Frame", string.Empty, 0, 0);
        Assert.Equal("marker", ToolHelpers.Classify(begin, hasChildren: false)); // no GPU id: a marker even without children
        Assert.Equal("clear", ToolHelpers.Classify(new EventRecord(0, 2, uint.MaxValue, "ClearRenderTargetView", "ClearRenderTargetView(rtv)", 0, 0)));
        Assert.Equal("resolve", ToolHelpers.Classify(new EventRecord(0, 2, uint.MaxValue, "ResolveSubresource", "", 0, 0)));
    }

    [Fact]
    public void UnknownKindsAreInvalidArgumentsThatNameTheReplacementVocabulary()
    {
        var record = new EventRecord(0, 1, uint.MaxValue, "Draw", "Draw", 0, 0);
        var error = Assert.Throws<PixToolException>(() => ToolHelpers.MatchesKind(record, "drawOrDispatch"));
        Assert.Equal("invalid_arguments", error.Detail.Code);
        Assert.Contains("work", error.Detail.Message);
        Assert.Throws<PixToolException>(() => ToolHelpers.ValidateKind("bogus"));
        ToolHelpers.ValidateKind(null);
        ToolHelpers.ValidateKind(" Work ");
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
