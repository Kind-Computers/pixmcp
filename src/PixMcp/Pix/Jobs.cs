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
    private string? _error;
    private ErrorDto? _errorDetail;
    private string? _resultRef;
    private readonly ResultStore? _results;
    private readonly string[] _owners;
    private DateTimeOffset? _startedAt, _finishedAt;
    private bool _cancellationRequested;
    private IPixCancellationToken? _pixToken;

    public Job(string id, string kind, string description, ResultStore? results = null, string? owner = null, IEnumerable<string>? owners = null)
    { Id = id; Kind = kind; Description = description; _results = results; _owners = owner is null ? (owners ?? []).ToArray() : [owner]; }
    public string Id { get; }
    public string Kind { get; }
    public string Description { get; }
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public JobStatus Status { get { lock (_lock) return _status; } }
    public float Progress { get { lock (_lock) return _progress; } }
    public string? ResultRef { get { lock (_lock) return _resultRef; } }
    public string? Error { get { lock (_lock) return _error; } }
    public ErrorDto? ErrorDetail { get { lock (_lock) return _errorDetail; } }
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
        try { Cancellation.Cancel(); }
        catch (ObjectDisposedException) when (IsFinished) { /* Completion/pruning won the cancellation race. */ }
        CancelNative(token);
    }

    private void CancelNative(IPixCancellationToken? token)
    {
        try { token?.Cancel(); }
        catch (Exception ex) { AddMessage("Native cancellation unavailable: " + PixErrors.Describe(ex)); }
    }

    internal void Succeed(object? result)
    {
        string? resultRef = result is null ? null : _results?.Store(result, jobId: Id, owners: _owners, operation: Kind);
        lock (_lock) { _resultRef = resultRef; _progress = 1; Finish(JobStatus.Succeeded); }
    }

    internal void Fail(Exception ex)
    {
        lock (_lock)
        {
            _error = PixErrors.Describe(ex);
            _errorDetail = PixErrors.ToDto(ex);
            // A cancellation request alone does not prove an operation stopped.
            bool cancelled = CancellationRequested && (ex is OperationCanceledException ||
                ex is ExternalException native && PixErrors.IsCancellationHResult(native.ErrorCode));
            Finish(cancelled ? JobStatus.Cancelled : JobStatus.Failed);
        }
    }

    private void Finish(JobStatus status)
    {
        _finishedAt = DateTimeOffset.UtcNow;
        _status = status;
        _pixToken = null;
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

    public JobDto ToDto()
    {
        JobDto snapshot;
        lock (_lock)
        {
            snapshot = new JobDto(Id, Kind, Description, _status.ToString().ToLowerInvariant(),
                _progress, CreatedAt, _startedAt, _finishedAt,
                _startedAt is null ? null : ((_finishedAt ?? DateTimeOffset.UtcNow) - _startedAt.Value).TotalSeconds,
                _messages.TakeLast(20).Select(m => m.Length > 500 ? m[..500] + "…" : m).ToArray(),
                _errorDetail, _resultRef, CancellationRequested,
                _resultRef is not null ? [ResultStore.ReadCall(_resultRef)] : !IsFinished
                    ? [new ToolCallDto("pix_job_wait", new { jobId = Id, timeoutSeconds = 2 })] : []);
        }
        // Never hold the job lock while checking retention; keep status/result identity atomic.
        return snapshot.ResultRef is not null && _results?.IsAvailable(snapshot.ResultRef) == false
            ? snapshot with { ResultRef = null, NextCalls = [] } : snapshot;
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
    /// <summary>Finished jobs kept for pix_job_status; older ones are forgotten so results and native tokens are not retained forever.</summary>
    public const int MaxFinishedJobs = 50;

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
        _session.Results.JobEvicted += id =>
        {
            if (_jobs.TryRemove(id, out Job? removed)) removed.Cancellation.Dispose();
        };
    }

    public IReadOnlyCollection<Job> All => _jobs.Values.OrderBy(j => j.CreatedAt).ToArray();
    /// <summary>The job currently executing on the PIX thread, if any (at most one, since the worker is single-threaded).</summary>
    public Job? Running => _worker.Snapshot().Operation is string operation
        ? _jobs.Values.FirstOrDefault(j => operation.StartsWith(j.Id + ": ", StringComparison.Ordinal)) : null;
    public Job Get(string jobId)
        => _jobs.TryGetValue(jobId, out Job? job) ? job : throw new McpException($"Unknown job '{jobId}'. Known jobs: {string.Join(", ", _jobs.Keys.OrderBy(k => k))}");
    public Job StartForHandle<T>(string kind, string description, string handleId, Func<Job, T, object?> work) where T : PixHandle
        => Start(kind, description, job => work(job, _session.Get<T>(handleId)), handleId);

    /// <summary>Runs work on the PIX worker. Success remains success when cancellation arrives too late.</summary>
    public Job Start(string kind, string description, Func<Job, object?> work, string? owner = null)
        => StartAfter(kind, description, null, work, owner);

    /// <summary>A managed prerequisite may await readiness without occupying the PIX worker.</summary>
    internal Job StartAfter(string kind, string description, Func<Job, Task>? prerequisite,
        Func<Job, object?> work, string? owner = null)
    {
        var job = new Job($"job-{Interlocked.Increment(ref _next)}", kind, description, _session.Results, owner, StructuredToolResults.CurrentOwners());
        _session.Results.RegisterJobOwners(job.Id, owner is null ? StructuredToolResults.CurrentOwners() : [owner]);
        _jobs[job.Id] = job;
        Prune();
        _ = Execute();
        return job;

        async Task Execute()
        {
            try
            {
                if (prerequisite is not null) await prerequisite(job).ConfigureAwait(false);
                object? result = await _worker.Run(() =>
                {
                    job.Begin();
                    try { job.AttachPixToken(_createNativeToken()); }
                    catch (Exception ex) { job.AddMessage("Native cancellation unavailable: " + PixErrors.Describe(ex)); }
                    job.ThrowIfCancellationRequested();
                    return work(job);
                }, job.Cancellation.Token, job.Id + ": " + description).ConfigureAwait(false);
                job.Succeed(result);
            }
            catch (Exception ex) { job.Fail(ex); }
            finally
            {
                try { _session.Results.MarkJobFinished(job.Id); Prune(); }
                finally { job.Completion.TrySetResult(true); }
            }
        }
    }

    /// <summary>Forgets the oldest finished jobs beyond <see cref="MaxFinishedJobs"/>; running and queued jobs are never removed.</summary>
    private void Prune()
    {
        Job[] finished = _jobs.Values.Where(j => j.IsFinished).OrderBy(j => j.FinishedAt ?? j.CreatedAt).ThenBy(j => j.CreatedAt).ToArray();
        for (int i = 0; i < finished.Length - MaxFinishedJobs; i++)
        {
            if (_session.Results.TryRemoveJob(finished[i].Id) && _jobs.TryRemove(finished[i].Id, out Job? removed))
            {
                removed.Cancellation.Dispose();
            }
        }
    }

    public void Cancel(Job job) => job.RequestCancellation();
    public async Task<object> WaitOrStatus(Job job, double waitSeconds, CancellationToken ct)
    {
        if (waitSeconds > 0 && !job.IsFinished)
        {
            try { await job.WaitAsync(TimeSpan.FromSeconds(waitSeconds), ct).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
        return job.ToDto();
    }
}
