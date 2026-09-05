using System.Collections.Concurrent;
using Microsoft.PIX;
using Microsoft.PIX.Extension;
using ModelContextProtocol;
using Windows.Win32.Foundation;

namespace PixMcp.Pix;

public enum JobStatus { Queued, Running, Succeeded, Failed, Cancelled }

/// <summary>A long-running PIX operation (analysis start, Dr. PIX run, symbol resolution, capture).</summary>
public sealed class Job
{
    private readonly object _lock = new();
    private readonly List<string> _messages = new();

    public Job(string id, string kind, string description)
    {
        Id = id;
        Kind = kind;
        Description = description;
    }

    public string Id { get; }
    public string Kind { get; }
    public string Description { get; }
    public JobStatus Status { get; internal set; } = JobStatus.Queued;
    public float Progress { get; private set; }
    public object? Result { get; internal set; }
    public string? Error { get; internal set; }
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; internal set; }
    public DateTimeOffset? FinishedAt { get; internal set; }
    public CancellationTokenSource Cancellation { get; } = new();
    public IPixCancellationToken? PixToken { get; internal set; }
    internal TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) => Completion.Task.WaitAsync(timeout, ct);

    public bool IsFinished => Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled;

    public void SetProgress(float progress)
    {
        Progress = Math.Clamp(progress, 0, 1);
    }

    public void AddMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }
        lock (_lock)
        {
            _messages.Add(message);
            if (_messages.Count > 200)
            {
                _messages.RemoveAt(0);
            }
        }
    }

    public IReadOnlyList<string> Messages
    {
        get { lock (_lock) { return _messages.ToArray(); } }
    }

    public object ToDto(bool includeResult) => new
    {
        jobId = Id,
        kind = Kind,
        description = Description,
        status = Status.ToString().ToLowerInvariant(),
        progress = Progress,
        createdAt = CreatedAt,
        startedAt = StartedAt,
        finishedAt = FinishedAt,
        elapsedSeconds = StartedAt is null ? (double?)null : ((FinishedAt ?? DateTimeOffset.UtcNow) - StartedAt.Value).TotalSeconds,
        messages = Messages.TakeLast(20).ToArray(),
        error = Error,
        result = includeResult ? Result : null,
    };

    /// <summary>IPixProgressNotifications adapter that reports into this job.</summary>
    public ProgressSink Sink => new(this);

    public sealed class ProgressSink : IPixGpuCaptureAnalysisNotifications, IPixProgressNotifications
    {
        private readonly Job _job;
        public ProgressSink(Job job) => _job = job;
        public void OnProgress(float progress) => _job.SetProgress(progress);
        public void OnStatus(PCWSTR status) => _job.AddMessage(Interop.W(status));
        public void OnIncompatibleDevice(PCWSTR message) => _job.AddMessage("Incompatible device: " + Interop.W(message));
    }
}

public sealed class JobManager
{
    private readonly ConcurrentDictionary<string, Job> _jobs = new();
    private readonly PixWorker _worker;
    private readonly PixSession _session;
    private int _next;

    public JobManager(PixWorker worker, PixSession session)
    {
        _worker = worker;
        _session = session;
    }

    public IReadOnlyCollection<Job> All => _jobs.Values.OrderBy(j => j.CreatedAt).ToArray();

    public Job Get(string jobId)
        => _jobs.TryGetValue(jobId, out Job? job) ? job : throw new McpException($"Unknown job '{jobId}'. Known jobs: {string.Join(", ", _jobs.Keys)}");

    /// <summary>
    /// Queues <paramref name="work"/> on the PIX worker thread. The work receives the job (for
    /// progress/messages) and must return the result object. A PIX cancellation token is created
    /// up front so pix_job_cancel can interrupt cooperative PIX operations.
    /// </summary>
    public Job Start(string kind, string description, Func<Job, object?> work)
    {
        string id = $"job-{Interlocked.Increment(ref _next)}";
        var job = new Job(id, kind, description);
        _jobs[id] = job;

        _ = _worker.Run(() =>
        {
            job.Status = JobStatus.Running;
            job.StartedAt = DateTimeOffset.UtcNow;
            try
            {
                try { job.PixToken = _session.Factory.CreateCancellationToken(); }
                catch { /* cancellation is best-effort */ }

                if (job.Cancellation.IsCancellationRequested)
                {
                    job.Status = JobStatus.Cancelled;
                    job.Error = "Cancelled before it started.";
                }
                else
                {
                    job.Result = work(job);
                    job.Status = job.Cancellation.IsCancellationRequested ? JobStatus.Cancelled : JobStatus.Succeeded;
                    job.SetProgress(1);
                }
            }
            catch (Exception ex)
            {
                job.Status = job.Cancellation.IsCancellationRequested ? JobStatus.Cancelled : JobStatus.Failed;
                job.Error = PixErrors.Describe(ex);
            }
            finally
            {
                job.FinishedAt = DateTimeOffset.UtcNow;
                job.Completion.TrySetResult(true);
            }
        });

        return job;
    }

    public void Cancel(Job job)
    {
        job.Cancellation.Cancel();
        try { job.PixToken?.Cancel(); } catch { }
        job.AddMessage("Cancellation requested.");
    }

    /// <summary>Waits up to <paramref name="waitSeconds"/> and returns the job DTO (with result when finished).</summary>
    public async Task<object> WaitOrStatus(Job job, double waitSeconds, CancellationToken ct)
    {
        if (waitSeconds > 0 && !job.IsFinished)
        {
            try { await job.WaitAsync(TimeSpan.FromSeconds(waitSeconds), ct).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
        return job.ToDto(includeResult: job.IsFinished);
    }
}
