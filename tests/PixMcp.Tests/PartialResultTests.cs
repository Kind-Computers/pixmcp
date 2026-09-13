using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PixMcp.Pix;
using PixMcp.Pix.Handles;
using PixMcp.Tools;
using ToolHelpers = PixMcp.Tools.Tools;
using Xunit;

namespace PixMcp.Tests;

/// <summary>Tools.RunWhenReadyOrPartial: metadata that needs no replay is answered immediately with a pending section.</summary>
public sealed class PartialResultTests
{
    private sealed record Metadata(string Handle, int Queues, bool FromWorker, object? Timing = null, IReadOnlyList<ToolCallDto>? NextCalls = null);

    private static Task<string> Call(Fixture fixture, FakeHandle handle, double waitSeconds, bool withMerge = true) =>
        ToolHelpers.RunWhenReadyOrPartial(fixture.Session, fixture.Jobs, "pix_test", handle.Id, Preparation(handle.Id),
            (h, onWorker) => new Metadata(h.Id, 2, onWorker),
            h => new Metadata(h.Id, 2, true, new { busyNs = 10 }),
            withMerge ? (partial, section) => partial is Metadata m ? m with { Timing = section, NextCalls = section.NextCalls } : null : null,
            waitSeconds, CancellationToken.None);

    [Fact]
    public async Task ReadyHandleAnswersTheFullQueryWithoutTouchingMetadata()
    {
        using var fixture = new Fixture();
        FakeHandle handle = await fixture.Worker.Run(() => fixture.Session.Register(new FakeHandle { Ready = true }));
        JsonElement root = JsonDocument.Parse(await Call(fixture, handle, 0)).RootElement;
        Assert.Equal(10, root.GetProperty("timing").GetProperty("busyNs").GetInt32());
        Assert.False(root.TryGetProperty("pending", out _));
        Assert.Empty(fixture.Jobs.All);
    }

    [Fact]
    public async Task UnreadyHandleReturnsMetadataWithAPendingSectionInsteadOfAWholePendingResponse()
    {
        using var fixture = new Fixture();
        using var gate = new Gate();
        FakeHandle handle = await fixture.Worker.Run(() => fixture.Session.Register(new FakeHandle { PrepareGate = gate }));
        JsonElement root = JsonDocument.Parse(await Call(fixture, handle, 0.05)).RootElement;
        Assert.False(StructuredToolResults.IsPending(root), "the top level must not be pending");
        Assert.Equal(2, root.GetProperty("queues").GetInt32());
        Assert.True(root.GetProperty("fromWorker").GetBoolean(), "the first caller computes metadata inside the admitted probe");
        JsonElement section = root.GetProperty("timing");
        Assert.True(section.GetProperty("pending").GetBoolean());
        Assert.Equal("pix_test", section.GetProperty("retry").GetString());
        Assert.Equal("running", section.GetProperty("job").GetProperty("status").GetString());
        string jobId = section.GetProperty("jobId").GetString()!;
        Assert.Same(fixture.Jobs.Get(jobId), handle.PreparationJobs["fake"]);
        JsonElement next = root.GetProperty("nextCalls");
        Assert.Equal("pix_job_wait", next[0].GetProperty("tool").GetString());
        Assert.Equal("job", next[0].GetProperty("cost").GetString());
        Assert.Equal("pix_test", next[1].GetProperty("tool").GetString());
        Assert.Equal("cached", next[1].GetProperty("cost").GetString());

        // A second caller while the job runs never queues a probe behind the replay: metadata comes from caches (onWorker=false).
        JsonElement joined = JsonDocument.Parse(await Call(fixture, handle, 0)).RootElement;
        Assert.False(joined.GetProperty("fromWorker").GetBoolean());
        Assert.Equal(jobId, joined.GetProperty("timing").GetProperty("jobId").GetString());
        Assert.Equal(1, handle.Prepared);

        gate.Release.Set();
        await fixture.Jobs.Get(jobId).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        JsonElement done = JsonDocument.Parse(await Call(fixture, handle, 0)).RootElement;
        Assert.Equal(10, done.GetProperty("timing").GetProperty("busyNs").GetInt32());
        Assert.Single(fixture.Jobs.All);
    }

    [Fact]
    public async Task WithoutAMergeTheWholeResponseIsPending()
    {
        using var fixture = new Fixture();
        using var gate = new Gate();
        FakeHandle handle = await fixture.Worker.Run(() => fixture.Session.Register(new FakeHandle { PrepareGate = gate }));
        JsonElement root = JsonDocument.Parse(await Call(fixture, handle, 0.05, withMerge: false)).RootElement;
        Assert.True(StructuredToolResults.IsPending(root));
        Assert.Equal("pix_test", root.GetProperty("retry").GetString());
        gate.Release.Set();
    }

    [Fact]
    public async Task PendingSectionValidatesAgainstTheOverviewAndInspectionSchemas()
    {
        using var fixture = new Fixture();
        using var gate = new Gate();
        FakeHandle handle = await fixture.Worker.Run(() => fixture.Session.Register(new FakeHandle { PrepareGate = gate }));
        string json = await Call(fixture, handle, 0.05);
        JsonElement section = JsonDocument.Parse(json).RootElement.GetProperty("timing");
        OutputSchemaTests.AssertMatches(section, StructuredToolResults.Export<PendingSectionDto>());

        var overview = new CaptureOverviewDto("gpu-1",
            [new QueueOverviewDto(0, "Graphics", "direct", 29, new Dictionary<string, int> { ["work"] = 4 }, null)],
            new Dictionary<string, CapabilityDto>(), null, null, [], [], [new("pix_job_wait", new { jobId = "job-1" }, "job")])
        { Timing = JsonSerializer.Deserialize<PendingSectionDto>(section.GetRawText(), Json.Options) };
        OutputSchemaTests.AssertMatches(JsonSerializer.SerializeToElement(overview, Json.Options), StructuredToolResults.SchemaFor("pix_gpu_overview"));

        PendingSectionDto pending = JsonSerializer.Deserialize<PendingSectionDto>(section.GetRawText(), Json.Options)!;
        var inspection = new EventInspectionDto(new EventRef("gpu-1", 0, 3), ["Frame"],
            new EventDto(0, 3, 7, 1, "DrawInstanced(3,1,0,0)", "DrawInstanced(3,1,0,0)", 1, null), pending, pending, pending, [])
        { Preparation = pending, NextCalls = pending.NextCalls };
        OutputSchemaTests.AssertMatches(JsonSerializer.SerializeToElement(inspection, Json.Options), StructuredToolResults.SchemaFor("pix_gpu_inspect_event"));
        gate.Release.Set();
    }

    private static Preparation<FakeHandle> Preparation(string handle)
        => new("fake", "fake", "Fake preparation", h => h.Ready, (h, job) => h.Prepare(job));

    private sealed class FakeHandle : PixHandle
    {
        public FakeHandle() : base("fake") { }
        public override string Kind => "fake";
        public bool Ready { get; set; }
        public int Prepared;
        public Gate? PrepareGate { get; init; }
        public void Prepare(Job job)
        {
            Prepared++;
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
