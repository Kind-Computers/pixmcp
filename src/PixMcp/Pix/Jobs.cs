using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.PIX;
using Microsoft.PIX.Extension;
using ModelContextProtocol;
using PixMcp.Pix.Handles;
using Windows.Win32.Foundation;

namespace PixMcp.Pix;

public enum JobStatus { Queued, Running, Succeeded, Failed, Cancelled }

/// <summary>A long-running PIX operation and its thread-safe progress/status snapshot.</summary>
public sealed class Job
{
    private readonly object _lock = new();
    private readonly List<string> _messages = new();
    private JobStatus _status = JobStatus.Queued;
    private float _progress;
    private object? _result;
    private string? _error;
    private DateTimeOffset? _startedAt, _finishedAt;
    private bool _cancellationRequested;
    private IPixCancellationToken? _pixToken;

    public Job(string id, string kind, string description) { Id = id; Kind = kind; Description = description; }
    public string Id { get; }
    public string Kind { get; }
    public string Description { get; }
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public JobStatus Status { get { lock (_lock) return _status; } }
    public float Progress { get { lock (_lock) return _progress; } }
    public object? Result { get { lock (_lock) return _result; } }
    public string? Error { get { lock (_lock) return _error; } }
    public DateTimeOffset? StartedAt { get { lock (_lock) return _startedAt; } }
    public DateTimeOffset? FinishedAt { get { lock (_lock) return _finishedAt; } }
    public CancellationTokenSource Cancellation { get; } = new();
    public bool CancellationRequested { get { lock (_lock) return _cancellationRequested || Cancellation.IsCancellationRequested; } }
    public IPixCancellationToken? PixToken { get { lock (_lock) return _pixToken; } }
    internal TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool IsFinished => Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled;
    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) => Completion.Task.WaitAsync(timeout, ct);

    public void ThrowIfCancellationRequested()
    {
        if (CancellationRequested) throw new OperationCanceledException(Cancellation.Token);
    }

    internal void Begin()
    {
        lock (_lock)
        {
            ThrowIfCancellationRequested();
            _status = JobStatus.Running;
            _startedAt = DateTimeOffset.UtcNow;
        }
    }

    internal void AttachPixToken(IPixCancellationToken? token)
    {
        bool cancel;
        lock (_lock) { _pixToken = token; cancel = CancellationRequested; }
        // Covers cancellation arriving before, during, or after native token creation.
        if (cancel) CancelNative(token);
    }

    internal void RequestCancellation()
    {
        IPixCancellationToken? token;
        lock (_lock)
        {
            if (IsFinished) throw new McpException($"Job {Id} already finished with status {_status}.");
            if (_cancellationRequested) return;
            _cancellationRequested = true;
            token = _pixToken;
            AddMessage("Cancellation requested.");
        }
        // Never hold the status lock across callbacks or a native call.
        Cancellation.Cancel();
        CancelNative(token);
    }

    private void CancelNative(IPixCancellationToken? token)
    {
        try { token?.Cancel(); }
        catch (Exception ex) { AddMessage("Native cancellation unavailable: " + PixErrors.Describe(ex)); }
    }

    internal void Succeed(object? result)
    {
        lock (_lock) { _result = result; _progress = 1; Finish(JobStatus.Succeeded); }
    }

    internal void Fail(Exception ex)
    {
        lock (_lock)
        {
            _error = PixErrors.Describe(ex);
            // A cancellation request alone does not prove an operation stopped.
            bool cancelled = CancellationRequested && (ex is OperationCanceledException ||
                ex is ExternalException native && native.ErrorCode is unchecked((int)0x80004004) or unchecked((int)0x800704C7));
            Finish(cancelled ? JobStatus.Cancelled : JobStatus.Failed);
        }
    }

    private void Finish(JobStatus status)
    {
        _finishedAt = DateTimeOffset.UtcNow;
        _status = status;
        Completion.TrySetResult(true);
    }

    public void SetProgress(float progress)
    {
        lock (_lock)
        {
            if (!IsFinished && float.IsFinite(progress)) _progress = Math.Clamp(progress, 0, 1);
        }
    }

    public void AddMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        lock (_lock) { _messages.Add(message); if (_messages.Count > 200) _messages.RemoveAt(0); }
    }

    public IReadOnlyList<string> Messages { get { lock (_lock) return _messages.ToArray(); } }

    public JobDto ToDto(bool includeResult)
    {
        lock (_lock)
        {
            return new JobDto(Id, Kind, Description, _status.ToString().ToLowerInvariant(),
                _progress, CreatedAt, _startedAt, _finishedAt,
                _startedAt is null ? null : ((_finishedAt ?? DateTimeOffset.UtcNow) - _startedAt.Value).TotalSeconds,
                _messages.TakeLast(20).ToArray(), _error, includeResult ? _result : null, CancellationRequested);
        }
    }

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
    private readonly Func<IPixCancellationToken?> _createNativeToken;
    private int _next;

    public JobManager(PixWorker worker, PixSession session)
        : this(worker, session, () => session.Factory.CreateCancellationToken()) { }
    internal JobManager(PixWorker worker, PixSession session, Func<IPixCancellationToken?> createNativeToken)
    {
        _worker = worker; _session = session; _createNativeToken = createNativeToken;
    }

    public IReadOnlyCollection<Job> All => _jobs.Values.OrderBy(j => j.CreatedAt).ToArray();
    public Job Get(string jobId)
        => _jobs.TryGetValue(jobId, out Job? job) ? job : throw new McpException($"Unknown job '{jobId}'. Known jobs: {string.Join(", ", _jobs.Keys)}");
    public Job StartForHandle<T>(string kind, string description, string handleId, Func<Job, T, object?> work) where T : PixHandle
        => Start(kind, description, job => work(job, _session.Get<T>(handleId)));

    /// <summary>Runs work on the PIX worker. Success remains success when cancellation arrives too late.</summary>
    public Job Start(string kind, string description, Func<Job, object?> work)
    {
        var job = new Job($"job-{Interlocked.Increment(ref _next)}", kind, description);
        _jobs[job.Id] = job;
        try
        {
            _ = _worker.Run(() =>
            {
                try
                {
                    job.Begin();
                    try { job.AttachPixToken(_createNativeToken()); }
                    catch (Exception ex) { job.AddMessage("Native cancellation unavailable: " + PixErrors.Describe(ex)); }
                    job.ThrowIfCancellationRequested();
                    job.Succeed(work(job));
                }
                catch (Exception ex) { job.Fail(ex); }
            });
        }
        catch (Exception ex) { job.Fail(ex); }
        return job;
    }

    public void Cancel(Job job) => job.RequestCancellation();
    public async Task<object> WaitOrStatus(Job job, double waitSeconds, CancellationToken ct)
    {
        if (waitSeconds > 0 && !job.IsFinished)
        {
            try { await job.WaitAsync(TimeSpan.FromSeconds(waitSeconds), ct).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
        return job.ToDto(includeResult: true);
    }
}
