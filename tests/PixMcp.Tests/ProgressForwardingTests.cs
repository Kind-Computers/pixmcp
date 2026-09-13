using ModelContextProtocol;
using PixMcp.Pix;
using Xunit;

namespace PixMcp.Tests;

public sealed class ProgressForwardingTests
{
    private static ProgressNotificationValue Value(float progress, string? message = null) => new() { Progress = progress, Total = 100, Message = message };

    [Fact]
    public void ThrottleSendsAtMostOneReportPerIntervalAndFlushesTheFinalValue()
    {
        var time = new ManualTime();
        var sink = new Collector();
        var throttle = new ProgressThrottle(sink, time, TimeSpan.FromMilliseconds(250));
        int built = 0;
        for (int i = 0; i < 10; i++)
        {
            int step = i;
            throttle.Offer(() => { built++; return Value(step * 10, "step"); });
        }
        Assert.Single(sink.Values);
        Assert.Equal(1, built);
        time.Advance(TimeSpan.FromMilliseconds(300));
        throttle.Offer(() => Value(95, "later"));
        throttle.Flush(Value(100, "done"));
        throttle.Flush(Value(100, "done"));
        Assert.Equal(new float[] { 0, 95, 100 }, sink.Values.Select(v => v.Progress));
    }

    [Fact]
    public async Task WaitingOnAJobForwardsProgressOnlyInsideAProgressScope()
    {
        var job = new Job("job-1", "test", "Collect timing");
        job.Begin();
        var sink = new Collector();
        Task waiting;
        using (ProgressForwarding.Scope(sink))
            waiting = job.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        job.SetProgress(0.5f);
        job.AddMessage("replaying");
        job.Succeed(null);
        job.Completion.TrySetResult(true); // JobManager completes the wait after Succeed.
        await waiting;

        IReadOnlyList<ProgressNotificationValue> values = sink.Values;
        Assert.True(values.Count >= 2, string.Join(" | ", values.Select(v => $"{v.Progress} {v.Message}")));
        Assert.Equal(0, values[0].Progress);
        Assert.StartsWith("job-1 running: Collect timing", values[0].Message);
        Assert.Equal(100, values[^1].Progress);
        Assert.StartsWith("job-1 succeeded: Collect timing", values[^1].Message);
        Assert.Equal(values.Select(v => v.Progress).Order(), values.Select(v => v.Progress));

        var outside = new Job("job-2", "test", "Other");
        outside.Begin();
        Task plain = outside.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        outside.SetProgress(0.3f);
        outside.Succeed(null);
        outside.Completion.TrySetResult(true);
        await plain;
        Assert.Equal(values.Count, sink.Values.Count);
    }

    [Fact]
    public async Task ATimedOutWaitStillReportsAndRethrows()
    {
        var job = new Job("job-3", "test", "Slow replay");
        job.Begin();
        var sink = new Collector();
        using (ProgressForwarding.Scope(sink))
            await Assert.ThrowsAsync<TimeoutException>(() => job.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None));
        Assert.NotEmpty(sink.Values);
        Assert.Contains("running", sink.Values[^1].Message);
    }

    [Fact]
    public async Task ClientProgressKeepsOrderNeverDecreasesAndSwallowsSendFailures()
    {
        var sent = new List<(float, string?)>();
        var client = new ClientProgress(async value =>
        {
            await Task.Yield();
            lock (sent) sent.Add((value.Progress, value.Message));
        });
        client.Report(Value(50, "a"));
        client.Report(Value(20, "b"));
        client.Report(Value(70, "c"));
        await client.DrainAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new (float, string?)[] { (50, "a"), (50, "b"), (70, "c") }, sent);

        var failing = new ClientProgress(_ => throw new InvalidOperationException("transport closed"));
        failing.Report(Value(10));
        await failing.DrainAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class Collector : IProgress<ProgressNotificationValue>
    {
        private readonly List<ProgressNotificationValue> _values = new();
        public IReadOnlyList<ProgressNotificationValue> Values { get { lock (_values) return _values.ToArray(); } }
        public void Report(ProgressNotificationValue value) { lock (_values) _values.Add(value); }
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _now = 1_000_000;
        public override long GetTimestamp() => _now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan by) => _now += by.Ticks;
    }
}
