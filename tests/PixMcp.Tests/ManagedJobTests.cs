using Microsoft.Extensions.Logging.Abstractions;
using PixMcp.Pix;
using Xunit;

namespace PixMcp.Tests;

public sealed class ManagedJobTests
{
    [Fact]
    public async Task ManagedJobsFinishDuringNativeWorkWithoutCreatingNativeToken()
    {
        using var worker = new PixWorker();
        using var session = new PixSession(worker, NullLogger<PixSession>.Instance);
        using var jobs = new JobManager(worker, session, () => throw new Exception("Must not create a native token"));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task active = worker.Run(() => { entered.SetResult(); release.Wait(); });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Job job = jobs.StartManaged("csv", "independent", _ => Task.FromResult<object?>(new { complete = true }));
            await job.WaitAsync(TimeSpan.FromSeconds(5), default);
            Assert.Equal(JobStatus.Succeeded, job.Status);
            Assert.Empty(job.Messages);
            Assert.Null(job.PixToken);
            Assert.NotNull(job.ResultRef);
            Assert.True(session.Results.ReadElement(job.ResultRef!).GetProperty("complete").GetBoolean());
            Assert.False(active.IsCompleted);
            Assert.False(session.FactoryCreated);
        }
        finally { release.Set(); await active; }
    }

    [Fact]
    public async Task CancellationAndDisposalDrainManagedJobsBeforeResultStoreCloses()
    {
        using var worker = new PixWorker();
        using var session = new PixSession(worker, NullLogger<PixSession>.Instance);
        var jobs = new JobManager(worker, session, () => throw new Exception("Must not create a native token"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool cleaned = false;
        Job job = jobs.StartManaged("csv", "cancel", async j =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, j.Cancellation.Token); return null; }
            finally { cleaned = true; }
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Run(jobs.Dispose).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(cleaned);
        Assert.Equal(JobStatus.Cancelled, job.Status);
        Assert.Throws<ObjectDisposedException>(() => jobs.StartManaged("csv", "after disposal", _ => Task.FromResult<object?>(null)));
        jobs.Dispose();
    }

    [Fact]
    public async Task ImmediatelyCancelledManagedWorkHasNoRetainedResult()
    {
        using var worker = new PixWorker();
        using var session = new PixSession(worker, NullLogger<PixSession>.Instance);
        using var jobs = new JobManager(worker, session, () => null);
        Job job = jobs.StartManaged("csv", "cancel immediately", async j =>
        {
            await Task.Delay(Timeout.Infinite, j.Cancellation.Token);
            return new { unexpected = true };
        });
        jobs.Cancel(job);
        await job.WaitAsync(TimeSpan.FromSeconds(5), default);
        Assert.Equal(JobStatus.Cancelled, job.Status);
        Assert.Null(job.ResultRef);
    }
}
