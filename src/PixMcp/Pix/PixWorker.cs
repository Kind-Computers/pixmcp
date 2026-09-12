namespace PixMcp.Pix;

public sealed record WorkerSnapshot(bool Busy, int QueuedCalls, string? Operation,
    DateTimeOffset? StartedAt, double? ElapsedSeconds);

/// <summary>Owns every native PIX call. Cancellation can remove queued work, never abort running native code.</summary>
public sealed class PixWorker : IDisposable
{
    private readonly object _gate = new();
    private readonly LinkedList<WorkItem> _queue = new();
    private readonly Thread _thread;
    private WorkItem? _active;
    private bool _stopping;

    public PixWorker()
    {
        _thread = new Thread(Loop) { Name = "PixWorker", IsBackground = true };
        _thread.Start();
    }

    public bool IsOnWorkerThread => Thread.CurrentThread == _thread;
    public int PendingCount { get { lock (_gate) return _queue.Count; } }
    public WorkerSnapshot Snapshot()
    {
        lock (_gate) return new(_active is not null || _queue.Count > 0, _queue.Count,
            _active?.Operation, _active?.StartedAt,
            _active?.StartedAt is DateTimeOffset start ? (DateTimeOffset.UtcNow - start).TotalSeconds : null);
    }

    public Task<T> Run<T>(Func<T> work, CancellationToken cancellationToken = default, string? operation = null)
    {
        if (IsOnWorkerThread) return Inline(work, cancellationToken);
        return Enqueue(work, cancellationToken, operation, onlyIfIdle: false).Completion.Task;
    }

    public Task Run(Action work, CancellationToken cancellationToken = default, string? operation = null)
        => Run(() => { work(); return true; }, cancellationToken, operation);

    /// <summary>Bounds only admission to the worker. A running operation is awaited to its actual outcome.</summary>
    internal async Task<T> RunWithAdmission<T>(Func<T> work, TimeSpan queueWait,
        CancellationToken cancellationToken, string operation)
    {
        if (IsOnWorkerThread) return await Inline(work, cancellationToken).ConfigureAwait(false);
        WorkItem<T> item = Enqueue(work, cancellationToken, operation, onlyIfIdle: queueWait <= TimeSpan.Zero);
        if (queueWait > TimeSpan.Zero && !item.Completion.Task.IsCompleted)
        {
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task deadline = Task.Delay(queueWait, timer.Token);
            await Task.WhenAny(item.Started.Task, item.Completion.Task, deadline).ConfigureAwait(false);
            if (deadline.IsCompleted && !cancellationToken.IsCancellationRequested)
                CancelQueued(item, null, Busy());
            await timer.CancelAsync().ConfigureAwait(false);
        }
        return await item.Completion.Task.ConfigureAwait(false);
    }

    private static Task<T> Inline<T>(Func<T> work, CancellationToken token)
    {
        try { token.ThrowIfCancellationRequested(); return Task.FromResult(work()); }
        catch (OperationCanceledException ex) { return Task.FromCanceled<T>(ex.CancellationToken.IsCancellationRequested ? ex.CancellationToken : new CancellationToken(true)); }
        catch (Exception ex) { return Task.FromException<T>(ex); }
    }

    private WorkItem<T> Enqueue<T>(Func<T> work, CancellationToken token, string? operation, bool onlyIfIdle)
    {
        token.ThrowIfCancellationRequested();
        var item = new WorkItem<T>(work, operation ?? "PIX operation");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (onlyIfIdle && (_active is not null || _queue.Count > 0)) throw Busy();
            item.Node = _queue.AddLast(item);
            item.Cancellation = token.Register(() => CancelQueued(item, token, null));
            Monitor.PulseAll(_gate);
        }
        return item;
    }

    private void CancelQueued(WorkItem item, CancellationToken? token, Exception? error)
    {
        lock (_gate)
        {
            if (item.Node is null) return; // Execution won the race; preserve its actual outcome.
            _queue.Remove(item.Node);
            item.Node = null;
            item.Cancel(token, error);
            item.Cancellation.Unregister();
            Monitor.PulseAll(_gate);
        }
    }

    private static PixToolException Busy() => new("worker_busy", "The PIX worker is occupied. Retry when the active operation finishes.", true);

    private void Loop()
    {
        while (true)
        {
            WorkItem item;
            lock (_gate)
            {
                while (_queue.Count == 0 && !_stopping) Monitor.Wait(_gate);
                if (_queue.Count == 0) return;
                item = _queue.First!.Value;
                _queue.RemoveFirst();
                item.Node = null;
                _active = item;
                item.StartedAt = DateTimeOffset.UtcNow;
                item.Started.TrySetResult();
            }
            item.Execute();
            lock (_gate) _active = null;
            item.Cancellation.Dispose();
            // Clear the active snapshot before waking callers that may immediately submit another request.
            item.Complete();
        }
    }

    public void Dispose()
    {
        lock (_gate) { _stopping = true; Monitor.PulseAll(_gate); }
        if (!IsOnWorkerThread) _thread.Join(TimeSpan.FromSeconds(5));
    }

    private abstract class WorkItem(string operation)
    {
        public string Operation { get; } = operation;
        public DateTimeOffset? StartedAt;
        public LinkedListNode<WorkItem>? Node;
        public CancellationTokenRegistration Cancellation;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public abstract void Execute();
        public abstract void Complete();
        public abstract void Cancel(CancellationToken? token, Exception? error);
    }

    private sealed class WorkItem<T>(Func<T> work, string operation) : WorkItem(operation)
    {
        public TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private T? _result;
        private Exception? _error;
        public override void Execute() { try { _result = work(); } catch (Exception ex) { _error = ex; } }
        public override void Complete()
        {
            if (_error is OperationCanceledException ex) Completion.TrySetCanceled(ex.CancellationToken);
            else if (_error is not null) Completion.TrySetException(_error);
            else Completion.TrySetResult(_result!);
        }
        public override void Cancel(CancellationToken? token, Exception? error)
        {
            if (error is not null) Completion.TrySetException(error);
            else Completion.TrySetCanceled(token ?? default);
        }
    }
}
