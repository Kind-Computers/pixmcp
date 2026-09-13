using System.Security.Cryptography;
using System.Text;

namespace PixMcp.Pix.Handles;

public sealed partial class TimingCaptureHandle
{
    private readonly object _queryGate = new();
    private readonly Dictionary<string, Job> _queryJobs = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _queryOrder = new();
    private readonly HashSet<string> _profiles = new(StringComparer.Ordinal);

    /// <summary>Readers (query jobs off the PIX worker) versus writers (save, symbol resolution, close) of the PixStorage file.</summary>
    internal DocumentGate DocumentGate { get; } = new("timing capture");

    internal Job QueryJob(JobManager jobs, ResultStore results, string tool, object arguments, Func<TimingDatabase, object> query, out int queryGeneration)
        => QueryJob(jobs, results, tool, arguments, (database, _) => query(database), out queryGeneration);

    /// <summary>
    /// Finds or starts the managed job for these arguments. The job runs off the PIX worker: it takes the document's
    /// read lock for its generation, opens a private read-only connection whose statements a writer interrupts, and
    /// releases the lock before its result is stored.
    /// </summary>
    internal Job QueryJob(JobManager jobs, ResultStore results, string tool, object arguments, Func<TimingDatabase, int, object> query, out int queryGeneration,
        IReadOnlyCollection<string>? owners = null)
    {
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Json.Serialize(arguments))));
        lock (_queryGate)
        {
            (int generation, CancellationToken invalidation) = DocumentGate.Snapshot();
            queryGeneration = generation;
            if (_queryJobs.TryGetValue(key, out Job? current))
            {
                if (!current.IsFinished || current.Status == JobStatus.Succeeded && current.ResultRef is string result && results.IsAvailable(result)) return current;
                _queryJobs.Remove(key);
                _queryOrder.Remove(key);
            }
            Job job = jobs.StartManaged("timing-query", $"{tool} on {Id}",
                j => Task.FromResult<object?>(RunQuery(j, generation, invalidation, query)), owner: owners is null ? Id : null, ownerIds: owners);
            _queryJobs.Add(key, job); _queryOrder.AddLast(key);
            // Only metadata and result references are retained; query data stays in ResultStore.
            int attempts = _queryOrder.Count;
            while (_queryJobs.Count > 32 && attempts-- > 0)
            {
                string old = _queryOrder.First!.Value;
                _queryOrder.RemoveFirst();
                if (!_queryJobs.TryGetValue(old, out Job? previous)) continue;
                if (previous.IsFinished) _queryJobs.Remove(old); else _queryOrder.AddLast(old);
            }
            return job;
        }
    }

    private object RunQuery(Job job, int generation, CancellationToken invalidation, Func<TimingDatabase, int, object> query)
        => DocumentGate.Read(generation, job.Cancellation.Token, () =>
        {
            job.AddMessage("Reading recorded timing data through PixStorage SQLite (off the PIX worker).");
            using var database = new TimingDatabase(CapturePath, PixStoragePath, job.Cancellation.Token, invalidation: invalidation);
            return query(database, generation);
        });

    /// <summary>Runs a document change exclusively: running queries are interrupted and later ones see the new generation.</summary>
    internal T WithDocumentWriter<T>(Func<T> work)
        => DocumentGate.Write(() =>
        {
            ClearQueryCaches();
            return work();
        });

    internal void RequireQueryGeneration(int generation) => DocumentGate.RequireGeneration(generation);

    internal void RememberProfile(string resultRef, int generation)
    {
        lock (_queryGate)
        {
            RequireQueryGeneration(generation);
            _profiles.Add(resultRef);
            // At most the currently retained query jobs can expose a live profile.
            if (_profiles.Count > 64)
                _profiles.RemoveWhere(id => !_queryJobs.Values.Any(j => j.ResultRef == id));
        }
    }

    internal bool OwnsProfile(string resultRef) { lock (_queryGate) return _profiles.Contains(resultRef); }

    /// <summary>Ends the current generation (interrupting its running queries) and forgets cached jobs and profiles.</summary>
    internal void InvalidateQueries()
    {
        DocumentGate.Invalidate();
        ClearQueryCaches();
    }

    private void ClearQueryCaches()
    {
        lock (_queryGate)
        {
            _queryJobs.Clear(); _queryOrder.Clear(); _profiles.Clear();
        }
    }

    /// <summary>Called by writers after the document moved; the writer already ended the generation.</summary>
    internal void RefreshCapturePath()
    {
        CapturePath = Interop.W(Document.GetCapturePath());
        ClearQueryCaches();
    }
}
