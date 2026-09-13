using System.Globalization;

namespace PixMcp.Pix;

/// <summary>
/// Coordinates off-worker readers of a document's SQLite storage with the changes that rewrite it (save, symbol
/// resolution, close). Readers carry the generation and invalidation token they started with; a writer first
/// invalidates (cancelling that token interrupts running statements), then waits a bounded time for the write lock.
/// Readers poll for the read lock so a stale generation or a cancelled job never waits behind a long writer.
/// </summary>
internal sealed class DocumentGate
{
    public static readonly TimeSpan DefaultWriterTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReadPoll = TimeSpan.FromMilliseconds(100);

    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly object _sync = new();
    private readonly string _documentName;
    private CancellationTokenSource _source = new();
    private int _generation;

    public DocumentGate(string documentName = "timing capture") => _documentName = documentName;

    public int Generation { get { lock (_sync) return _generation; } }

    /// <summary>The current generation and the token that is cancelled when it ends.</summary>
    public (int Generation, CancellationToken Invalidation) Snapshot()
    {
        lock (_sync) return (_generation, _source.Token);
    }

    /// <summary>Ends the current generation and interrupts its readers; returns the new generation.</summary>
    public int Invalidate()
    {
        CancellationTokenSource previous;
        int generation;
        lock (_sync)
        {
            previous = _source;
            _source = new CancellationTokenSource();
            generation = ++_generation;
        }
        // Outside the lock: registrations call sqlite3_interrupt synchronously. Old sources are not disposed because a
        // reader may still be linking to their token; they own no timers or handles.
        previous.Cancel();
        return generation;
    }

    public void RequireGeneration(int generation)
    {
        if (generation != Generation)
            throw new PixToolException(PixErrors.Codes.TimingQueryInvalidated,
                $"The {_documentName} changed while this query was pending (save, symbol resolution or close). Repeat the query.", true);
    }

    /// <summary>Runs a reader of <paramref name="generation"/>; the lock is held on the calling thread for the duration of <paramref name="work"/>.</summary>
    public T Read<T>(int generation, CancellationToken cancellation, Func<T> work)
    {
        while (!_lock.TryEnterReadLock(ReadPoll))
        {
            cancellation.ThrowIfCancellationRequested();
            RequireGeneration(generation);
        }
        try
        {
            RequireGeneration(generation);
            cancellation.ThrowIfCancellationRequested();
            return work();
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Invalidates readers, then runs <paramref name="work"/> exclusively; timing_capture_busy when a reader does not stop in time.</summary>
    public T Write<T>(Func<T> work, TimeSpan? timeout = null)
    {
        TimeSpan wait = timeout ?? DefaultWriterTimeout;
        Invalidate();
        if (!_lock.TryEnterWriteLock(wait))
            throw new PixToolException(PixErrors.Codes.TimingCaptureBusy,
                $"A running query on this {_documentName} did not stop within {wait.TotalSeconds.ToString("g", CultureInfo.InvariantCulture)} s. Retry shortly.", true);
        try { return work(); }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>Like <see cref="Write{T}"/> but runs <paramref name="work"/> even when readers did not stop; returns whether it ran exclusively.</summary>
    public bool WriteOrForce(Action work, TimeSpan? timeout = null)
    {
        Invalidate();
        bool exclusive = _lock.TryEnterWriteLock(timeout ?? DefaultWriterTimeout);
        try { work(); }
        finally { if (exclusive) _lock.ExitWriteLock(); }
        return exclusive;
    }
}
