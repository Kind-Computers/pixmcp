using ModelContextProtocol;
using PixMcp.Pix;
using Xunit;

namespace PixMcp.Tests;

public sealed class JobSnapshotConcurrencyTests
{
    [Fact]
    public async Task SuccessfulStatusNeverLosesItsResultDuringConcurrentCompletion()
    {
        using var store = new ResultStore();
        Job[] jobs = Enumerable.Range(0, 256).Select(i => new Job("job-" + i, "test", "Concurrent completion", store)).ToArray();
        using var start = new ManualResetEventSlim();
        Task completing = Task.Run(() => { start.Wait(); Parallel.ForEach(jobs, job => job.Succeed(new { value = job.Id })); });
        Task reading = Task.Run(() =>
        {
            start.Set();
            do
            {
                foreach (Job job in jobs)
                {
                    JobDto snapshot = job.ToDto();
                    if (snapshot.Status != "succeeded") continue;
                    Assert.NotNull(snapshot.ResultRef);
                    Assert.True(store.IsAvailable(snapshot.ResultRef));
                    Assert.Equal("pix_result_read", Assert.Single(snapshot.NextCalls).Tool);
                }
            } while (!completing.IsCompleted);
        });
        await Task.WhenAll(completing, reading);
        Assert.All(jobs, job => Assert.NotNull(job.ToDto().ResultRef));
    }

    [Fact]
    public void CancellationRacingCompletionAndRetirementDoesNotExposeDisposedSource()
    {
        Parallel.For(0, 256, _ =>
        {
            var job = new Job(Guid.NewGuid().ToString(), "test", "Retirement race");
            Parallel.Invoke(
                () => { job.Succeed(null); job.Cancellation.Dispose(); },
                () =>
                {
                    try { job.RequestCancellation(); }
                    catch (McpException) when (job.IsFinished) { /* Completion won admission to cancellation. */ }
                });
            Assert.Equal("succeeded", job.ToDto().Status);
        });
    }
}
