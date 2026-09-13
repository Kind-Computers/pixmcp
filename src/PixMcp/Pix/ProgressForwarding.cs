using ModelContextProtocol;

namespace PixMcp.Pix;

/// <summary>
/// Forwards job progress to the client while a tool call waits on a job. The call filter scopes the request's progress sink when
/// the client sent a progress token; every <see cref="Job.WaitAsync"/> inside that call then reports the job's progress (0..100)
/// and latest status message at most four times a second, with a final report when the wait ends. Clients without a token keep
/// polling pix_job_status.
/// </summary>
internal static class ProgressForwarding
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(250);
    private const int MaxMessageLength = 200;
    private static readonly AsyncLocal<IProgress<ProgressNotificationValue>?> Target = new();

    /// <summary>The progress sink of the tool call being served, or null when the client sent no progress token.</summary>
    public static IProgress<ProgressNotificationValue>? Current => Target.Value;

    public static IDisposable Scope(IProgress<ProgressNotificationValue>? progress)
    {
        IProgress<ProgressNotificationValue>? previous = Target.Value;
        Target.Value = progress;
        return new Restore(previous);
    }

    /// <summary>Awaits <paramref name="wait"/> while reporting <paramref name="job"/>'s progress; the wait's outcome (including a timeout) is rethrown.</summary>
    public static async Task Forward(Job job, Task wait, IProgress<ProgressNotificationValue> target, TimeProvider? time = null)
    {
        var throttle = new ProgressThrottle(target, time ?? TimeProvider.System, MinInterval);
        void OnProgressed(Job changed) => throttle.Offer(() => Value(changed));
        job.Progressed += OnProgressed;
        try
        {
            throttle.Offer(() => Value(job));
            await wait.ConfigureAwait(false);
        }
        finally
        {
            job.Progressed -= OnProgressed;
            throttle.Flush(Value(job));
        }
    }

    /// <summary>The job's progress on a 0..100 scale (100 once it succeeded) with its id, status, description and latest message.</summary>
    internal static ProgressNotificationValue Value(Job job)
    {
        JobStatus status = job.Status;
        string? last = job.LastMessage;
        string message = $"{job.Id} {status.ToString().ToLowerInvariant()}: {job.Description}" + (last is null ? "" : $" ({last})");
        if (message.Length > MaxMessageLength) message = message[..(MaxMessageLength - 1)] + "…";
        float progress = status == JobStatus.Succeeded ? 100 : MathF.Round(job.Progress * 100, 1);
        return new ProgressNotificationValue { Progress = progress, Total = 100, Message = message };
    }

    private sealed class Restore(IProgress<ProgressNotificationValue>? previous) : IDisposable
    {
        public void Dispose() => Target.Value = previous;
    }
}

/// <summary>Sends at most one report per interval; a report offered too early is kept and superseded by later ones or the final flush.</summary>
internal sealed class ProgressThrottle(IProgress<ProgressNotificationValue> target, TimeProvider time, TimeSpan interval)
{
    private readonly object _lock = new();
    private long _lastSentAt;
    private bool _sentAny;
    private (float Progress, string? Message) _lastSent;

    /// <summary>Sends the value now when the interval has elapsed (the value is only built then), otherwise drops it.</summary>
    public void Offer(Func<ProgressNotificationValue> value)
    {
        lock (_lock)
        {
            long now = time.GetTimestamp();
            if (_sentAny && time.GetElapsedTime(_lastSentAt, now) < interval) return;
            Send(value(), now);
        }
    }

    /// <summary>Sends the final value unless it repeats the last report.</summary>
    public void Flush(ProgressNotificationValue final)
    {
        lock (_lock)
        {
            if (_sentAny && _lastSent == (final.Progress, final.Message)) return;
            Send(final, time.GetTimestamp());
        }
    }

    private void Send(ProgressNotificationValue value, long now)
    {
        if (_sentAny && _lastSent == (value.Progress, value.Message)) return;
        _sentAny = true;
        _lastSentAt = now;
        _lastSent = (value.Progress, value.Message);
        target.Report(value);
    }
}

/// <summary>
/// The progress sink of one request: sends notifications in report order and never lets the progress value decrease (a call that
/// waits on two jobs keeps the higher value with the newer message).
/// </summary>
internal sealed class ClientProgress(Func<ProgressNotificationValue, Task> send) : IProgress<ProgressNotificationValue>
{
    private readonly object _lock = new();
    private Task _tail = Task.CompletedTask;
    private float _highest;

    public void Report(ProgressNotificationValue value)
    {
        lock (_lock)
        {
            _highest = Math.Max(_highest, value.Progress);
            var next = new ProgressNotificationValue { Progress = _highest, Total = value.Total, Message = value.Message };
            _tail = _tail.ContinueWith(async _ =>
            {
                try { await send(next).ConfigureAwait(false); }
                catch (Exception) { /* A dropped notification must never fail the call; polling still works. */ }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
    }

    /// <summary>Waits, bounded, for the notifications already reported so none is written after the tool result.</summary>
    public async Task DrainAsync(TimeSpan timeout)
    {
        Task tail;
        lock (_lock) tail = _tail;
        try { await tail.WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }
}
