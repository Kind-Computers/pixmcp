using System.Collections.Concurrent;

namespace PixMcp.Pix;

/// <summary>
/// Single dedicated thread that owns every PIX API call. The PIX API is nano-COM with no
/// apartment marshalling and the samples are strictly single-threaded, so all document,
/// analysis and device access is serialized here.
/// </summary>
public sealed class PixWorker : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    public PixWorker()
    {
        _thread = new Thread(Loop) { Name = "PixWorker", IsBackground = true };
        _thread.Start();
    }

    public bool IsOnWorkerThread => Thread.CurrentThread == _thread;

    /// <summary>Number of work items waiting behind the one currently executing.</summary>
    public int PendingCount => _queue.Count;

    /// <summary>
    /// Queues work for the PIX thread. A cancellation requested before the item is dequeued skips it
    /// entirely (the caller has gone away); once running, PIX calls are not interruptible.
    /// </summary>
    public Task<T> Run<T>(Func<T> work, CancellationToken cancellationToken = default)
    {
        if (IsOnWorkerThread)
        {
            try { return Task.FromResult(work()); }
            catch (Exception ex) { return Task.FromException<T>(ex); }
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                tcs.TrySetCanceled(cancellationToken);
                return;
            }
            try { tcs.TrySetResult(work()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    public Task Run(Action work, CancellationToken cancellationToken = default)
        => Run(() => { work(); return true; }, cancellationToken);

    private void Loop()
    {
        foreach (Action work in _queue.GetConsumingEnumerable())
        {
            // Every queued item completes its own TaskCompletionSource inside a try/catch; this guard
            // only exists so that a misbehaving item can never take the PIX thread down with it.
            try { work(); }
            catch { }
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        if (!IsOnWorkerThread)
        {
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }
}
