using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PixMcp.Pix;
using PixMcp.Pix.Handles;
using PixMcp.Tools;
using Xunit;

namespace PixMcp.Tests;

public sealed class ResourceOwnershipTests
{
    // Handle summaries are diagnostics: they must answer while the PIX worker is busy with a replay,
    // so every Summary() implementation reads only immutable state or thread-safe snapshots.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResourceSummariesAnswerWhileTheWorkerIsBusy(bool individual)
    {
        using var worker = new PixWorker();
        using var session = new PixSession(worker, NullLogger<PixSession>.Instance);
        SnapshotHandle handle = await session.Run(() => session.Register(new SnapshotHandle()));

        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task blocker = worker.Run(() => { entered.SetResult(); release.Wait(TimeSpan.FromSeconds(10)); });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        string json = individual ? PixResources.Handle(session, handle.Id) : PixResources.Handles(session);
        string tool = SessionTools.Handles(session);
        release.Set();
        await blocker;

        using JsonDocument result = JsonDocument.Parse(json);
        JsonElement summary = individual ? result.RootElement : Assert.Single(result.RootElement.EnumerateArray());
        Assert.Equal(handle.Id, summary.GetProperty("handle").GetString());
        Assert.False(handle.SummarizedOnWorker);
        Assert.Contains(handle.Id, tool);
    }

    [Fact]
    public async Task ClosingStillRunsOnTheOwningWorker()
    {
        using var worker = new PixWorker();
        using var session = new PixSession(worker, NullLogger<PixSession>.Instance);
        SnapshotHandle handle = await session.Run(() => session.Register(new SnapshotHandle()));
        await session.Run(() => session.Close(handle.Id));
        Assert.True(handle.ClosedOnWorker);
        Assert.Empty(session.Handles);
    }

    private sealed class SnapshotHandle : PixHandle
    {
        private readonly int _workerThreadId = -1;
        public SnapshotHandle() : base("test-resource") { }
        public override string Kind => "test";
        public bool SummarizedOnWorker { get; private set; }
        public bool ClosedOnWorker { get; private set; }

        public override object Summary()
        {
            SummarizedOnWorker |= Thread.CurrentThread.Name == "PixWorker";
            return new { handle = Id };
        }

        public override void Close(List<string> warnings) => ClosedOnWorker = Thread.CurrentThread.Name == "PixWorker";
    }
}
