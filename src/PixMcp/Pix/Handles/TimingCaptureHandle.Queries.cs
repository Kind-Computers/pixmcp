using System.Security.Cryptography;
using System.Text;

namespace PixMcp.Pix.Handles;

public sealed partial class TimingCaptureHandle
{
    private readonly object _queryGate = new();
    private readonly Dictionary<string, Job> _queryJobs = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _queryOrder = new();
    private readonly HashSet<string> _profiles = new(StringComparer.Ordinal);
    private int _queryGeneration;

    internal Job QueryJob(JobManager jobs, ResultStore results, string tool, object arguments, Func<TimingDatabase, object> query, out int queryGeneration)
        => QueryJob(jobs, results, tool, arguments, (database, _) => query(database), out queryGeneration);

    internal Job QueryJob(JobManager jobs, ResultStore results, string tool, object arguments, Func<TimingDatabase, int, object> query, out int queryGeneration)
    {
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Json.Serialize(arguments))));
        lock (_queryGate)
        {
            queryGeneration = _queryGeneration;
            if (_queryJobs.TryGetValue(key, out Job? current))
            {
                if (!current.IsFinished || current.Status == JobStatus.Succeeded && current.ResultRef is string result && results.IsAvailable(result)) return current;
                _queryJobs.Remove(key);
                _queryOrder.Remove(key);
            }
            int generation = _queryGeneration;
            Job job = jobs.StartForHandle<TimingCaptureHandle>("timing-query", $"{tool} on {Id}", Id, (j, handle) =>
            {
                RequireQueryGeneration(generation);
                j.AddMessage("Reading recorded timing data through PixStorage SQLite.");
                using var database = new TimingDatabase(handle.CapturePath, handle.PixStoragePath, j.Cancellation.Token);
                return query(database, generation);
            });
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

    internal void RequireQueryGeneration(int generation)
    {
        lock (_queryGate)
            if (generation != _queryGeneration)
                throw new PixToolException("timing_query_invalidated", "The timing capture changed while this query was pending. Repeat the query.", true);
    }

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

    internal void InvalidateQueries()
    {
        lock (_queryGate)
        {
            _queryGeneration++;
            _queryJobs.Clear(); _queryOrder.Clear(); _profiles.Clear();
        }
    }

    internal void RefreshCapturePath()
    {
        CapturePath = Interop.W(Document.GetCapturePath());
        InvalidateQueries();
    }
}
