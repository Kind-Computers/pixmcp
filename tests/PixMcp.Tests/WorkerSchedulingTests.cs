using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.PIX;
using PixMcp.Pix;
using PixMcp.Pix.Handles;
using PixMcp.Tools;
using Xunit;

namespace PixMcp.Tests;

public sealed class WorkerSchedulingTests
{
    [Fact]
    public async Task OrdinaryNativeCallReportsBusyAndQueuedCancellationCompletesBeforeIt()
    {
        using var worker = new PixWorker();
        using var session = new PixSession(worker, NullLogger<PixSession>.Instance);
        var jobs = new JobManager(worker, session, () => null);
        using var gate = new Gate();
        Task active = worker.Run(gate.Block, operation: "pix_gpu_open");
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        JsonElement info = JsonSerializer.Deserialize<JsonElement>(await SessionTools.Info(session, jobs));
        Assert.True(info.GetProperty("worker").GetProperty("busy").GetBoolean());
        Assert.Equal("pix_gpu_open", info.GetProperty("worker").GetProperty("operation").GetString());
        using var cancellation = new CancellationTokenSource();
        bool executed = false;
        Task queued = worker.Run(() => executed = true, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.False(executed);
        Assert.Equal(0, worker.PendingCount);
        Assert.False(active.IsCompleted);
        gate.Release.Set();
        await active;
        Assert.False(worker.Snapshot().Busy);
    }

    [Fact]
    public async Task AdmissionDeadlineRemovesUnstartedWorkButDoesNotAbortStartedWork()
    {
        using var worker = new PixWorker();
        using var gate = new Gate();
        Task active = worker.Run(gate.Block);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        bool executed = false;
        PixToolException error = await Assert.ThrowsAsync<PixToolException>(() =>
            worker.RunWithAdmission(() => executed = true, TimeSpan.FromMilliseconds(25), default, "queued"));
        Assert.Equal("worker_busy", error.Detail.Code);
        Assert.Equal(0, worker.PendingCount);
        Assert.False(executed);
        gate.Release.Set();
        await active;
        using var second = new Gate();
        Task<int> started = worker.RunWithAdmission(() => { second.Block(); return 42; }, TimeSpan.Zero, default, "started");
        await second.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(started.IsCompleted);
        second.Release.Set();
        Assert.Equal(42, await started);
    }

    [Fact]
    public async Task BusyPreparationQueryHasExactRecoveryWithoutQueueingReplay()
    {
        using var worker = new PixWorker();
        using var session = new PixSession(worker, NullLogger<PixSession>.Instance);
        var jobs = new JobManager(worker, session, () => null);
        FakeHandle handle = session.Register(new FakeHandle());
        using var gate = new Gate();
        Task active = worker.Run(gate.Block);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var preparation = new Preparation<FakeHandle>("fake", "fake", "Prepare fake", _ => true, (_, _) => throw new Exception("must not prepare"));
        PixToolException error = await Assert.ThrowsAsync<PixToolException>(() => PixMcp.Tools.Tools.RunWhenReady(session, jobs,
            "pix_fake", handle.Id, preparation, _ => new { ok = true }, 0, default));
        Assert.Equal("worker_busy", error.Detail.Code);
        Assert.True(error.Detail.Retryable);
        Assert.Equal("pix_fake", Assert.Single(error.Detail.NextCalls).Tool);
        Assert.Empty(jobs.All);
        Assert.Equal(0, worker.PendingCount);
        gate.Release.Set();
        await active;
    }

    [Fact]
    public async Task QueuedJobsCancelImmediatelyAndCompletedBatchPrunesWithoutTriggerJob()
    {
        using var worker = new PixWorker();
        using var session = new PixSession(worker, NullLogger<PixSession>.Instance);
        var jobs = new JobManager(worker, session, () => null);
        using var gate = new Gate();
        Task active = worker.Run(gate.Block);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Job cancelled = jobs.Start("test", "cancel queued", _ => throw new Exception("must not run"));
        jobs.Cancel(cancelled);
        await cancelled.WaitAsync(TimeSpan.FromSeconds(1), default);
        Assert.Equal(JobStatus.Cancelled, cancelled.Status);
        var batch = Enumerable.Range(0, 60).Select(i => jobs.Start("test", i.ToString(), _ => new { i })).ToArray();
        gate.Release.Set();
        await active;
        await Task.WhenAll(batch.Select(j => j.WaitAsync(TimeSpan.FromSeconds(10), default)));
        Assert.Equal(JobManager.MaxFinishedJobs, jobs.All.Count);
    }

    [Fact]
    public async Task ManagedCaptureReadinessWaitDoesNotOccupyWorker()
    {
        using var worker = new PixWorker();
        using var session = new PixSession(worker, NullLogger<PixSession>.Instance);
        var jobs = new JobManager(worker, session, () => null);
        var target = new CaptureTarget(42, PIX_PROCESS_UNSUPPORTED_REASON.PIX_PROCESS_UNSUPPORTED_REASON_NOT_USING_D3D12);
        Job job = jobs.StartAfter("capture", "ready", j => target.WaitReadyAsync(TimeSpan.FromSeconds(5), j.Cancellation.Token), _ => "captured");
        Assert.Equal(JobStatus.Queued, job.Status);
        Assert.Equal(17, await worker.Run(() => 17).WaitAsync(TimeSpan.FromSeconds(1)));
        target.Update(PIX_PROCESS_UNSUPPORTED_REASON.PIX_PROCESS_UNSUPPORTED_REASON_NONE);
        await job.WaitAsync(TimeSpan.FromSeconds(5), default);
        Assert.Equal(JobStatus.Succeeded, job.Status);
    }

    [Fact]
    public async Task DisposeFailsQueuedWorkWithServerShuttingDownAndLetsTheActiveCallFinish()
    {
        var worker = new PixWorker();
        using var gate = new Gate();
        Task<int> active = worker.Run(() => { gate.Block(); return 7; });
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<int> queued = worker.Run(() => 1, operation: "pix_gpu_events");
        Task queuedTask = worker.Run(() => { }, operation: "pix_close");
        Task dispose = Task.Run(worker.Dispose);
        PixToolException error = await Assert.ThrowsAsync<PixToolException>(() => queued.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(PixErrors.Codes.ServerShuttingDown, error.Detail.Code);
        Assert.Contains("pix_gpu_events", error.Detail.Message);
        await Assert.ThrowsAsync<PixToolException>(() => queuedTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, worker.PendingCount);
        gate.Release.Set();
        Assert.Equal(7, await active);
        await dispose.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(PixErrors.Codes.ServerShuttingDown, (await Assert.ThrowsAsync<PixToolException>(() => worker.Run(() => 1))).Detail.Code);
    }

    [Fact]
    public async Task EnqueueAndCancelRacesLeaveNothingQueued()
    {
        using var worker = new PixWorker();
        using var gate = new Gate();
        Task active = worker.Run(gate.Block);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var tasks = new List<Task>();
        Parallel.For(0, 500, _ =>
        {
            using var cancellation = new CancellationTokenSource();
            Task queued = worker.Run(() => 1, cancellation.Token);
            cancellation.Cancel();
            lock (tasks) tasks.Add(queued);
        });
        await Task.WhenAll(tasks.Select(async t => { try { await t; } catch (OperationCanceledException) { } }));
        Assert.Equal(0, worker.PendingCount);
        gate.Release.Set();
        await active;
    }

    private sealed class FakeHandle() : PixHandle("fake")
    {
        public override string Kind => "fake";
        public override void Close(List<string> warnings) { }
    }
    private sealed class Gate : IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public void Block() { Entered.TrySetResult(); Release.Wait(); }
        public void Dispose() => Release.Set();
    }
}
