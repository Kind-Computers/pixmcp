using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PixMcp.Pix;

public sealed record ResultStoreSummary(long MemoryBytes, long MemoryLimitBytes, long DiskBytes,
    long DiskLimitBytes, int ResultCount, int ActiveLeases, long Evictions);
public sealed record ResultExportDto(string ResultRef, string Pointer, string OutPath, long Bytes);

/// <summary>Immutable serialized snapshots with bounded retention. Reads are independent of PIX.</summary>
public sealed partial class ResultStore : IDisposable
{
    /// <summary>Inline response target: larger managed results become snapshots (PIXMCP_INLINE_RESULT_BYTES).</summary>
    public static int TargetBytes => ServerOptions.Current.InlineResultBytes;
    public const int MaxTransientResults = 50;
    /// <summary>Origins of expired results kept for result_expired recovery calls.</summary>
    public const int MaxExpiredOrigins = 256;
    private const string IdAlphabet = "0123456789abcdefghjkmnpqrstvwxyz";
    /// <summary>A finished job's result cannot be evicted for storage pressure until this long after it finished, so pix_job_wait can hand it over.</summary>
    public const int EvictionGraceSeconds = 30;
    private sealed class Snapshot(string id, byte[]? bytes, string? path, long size, HashSet<string> owners, string? jobId, long sequence, ToolCallDto? origin)
    {
        internal readonly string Id = id;
        internal readonly byte[]? Bytes = bytes;
        internal readonly string? Path = path;
        internal readonly long Size = size;
        internal readonly HashSet<string> Owners = owners;
        internal readonly string? JobId = jobId;
        /// <summary>Creation order (ids are opaque).</summary>
        internal readonly long Sequence = sequence;
        /// <summary>The tool call that produced the result, offered when it expires.</summary>
        internal readonly ToolCallDto? Origin = origin;
        internal int Leases;
        internal bool Removed;
    }
    private readonly object _gate = new();
    private readonly Dictionary<string, Snapshot> _snapshots = new();
    private readonly Dictionary<string, string[]> _jobOwners = new();
    private readonly Dictionary<string, (long Order, DateTimeOffset At)> _finishedJobs = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly HashSet<string> _closedOwners = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingEvictions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _leasedJobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToolCallDto> _expiredOrigins = new(StringComparer.Ordinal);
    private readonly Queue<string> _expiredOrder = new();
    private readonly string _directory;
    private FileStream? _ownership;
    private readonly long _memoryLimit, _diskLimit;
    private long _memoryBytes, _diskBytes, _evictions, _finishedOrder, _next;
    private int _leases;
    private bool _disposed;

    /// <summary>Budgets and the storage root default to <see cref="ServerOptions.Current"/>.</summary>
    public ResultStore(long? memoryLimitBytes = null, long? diskLimitBytes = null, string? tempDirectory = null, TimeProvider? time = null)
    {
        ServerOptions options = ServerOptions.Current;
        _time = time ?? TimeProvider.System;
        _memoryLimit = memoryLimitBytes ?? options.ResultMemoryBytes;
        _diskLimit = diskLimitBytes ?? options.ResultDiskBytes;
        if (_memoryLimit < 0 || _diskLimit < 0 || (_memoryLimit == 0 && _diskLimit == 0))
            throw new ArgumentOutOfRangeException(nameof(memoryLimitBytes), "At least one result budget must be positive; budgets cannot be negative.");
        string root = System.IO.Path.GetFullPath(System.IO.Path.Combine(tempDirectory ?? options.ResultDirectory ?? System.IO.Path.GetTempPath(), "pixmcp-results"));
        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The private result storage root cannot be a reparse point.");
        ReclaimAbandoned(root);
        _directory = System.IO.Path.Combine(root, "session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _ownership = new FileStream(System.IO.Path.Combine(_directory, ".owner"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Delete);
        _ownership.Write(Encoding.UTF8.GetBytes("pixmcp-results-v1")); _ownership.Flush();
    }
    /// <summary>Opaque result ids: "r-" plus 10 Crockford base32 characters from 50 random bits, never guessable from a neighbour.</summary>
    private string NewId()
    {
        Span<byte> random = stackalloc byte[8];
        while (true)
        {
            RandomNumberGenerator.Fill(random);
            ulong bits = BitConverter.ToUInt64(random) & ((1UL << 50) - 1);
            var id = new char[12]; id[0] = 'r'; id[1] = '-';
            for (int i = 11; i >= 2; i--) { id[i] = IdAlphabet[(int)(bits & 31)]; bits >>= 5; }
            string candidate = new(id);
            if (!_snapshots.ContainsKey(candidate) && !_expiredOrigins.ContainsKey(candidate)) return candidate;
        }
    }
    /// <summary>True for paths inside the server's private result storage root (never a valid user output path).</summary>
    internal bool IsPrivatePath(string fullPath)
        => fullPath.StartsWith(System.IO.Path.GetDirectoryName(_directory)! + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public event Action<string>? JobEvicted;
    public bool IsAvailable(string resultRef) { lock (_gate) return !_disposed && _snapshots.ContainsKey(resultRef); }
    public ResultStoreSummary Summary() { lock (_gate) return new(_memoryBytes, _memoryLimit, _diskBytes, _diskLimit, _snapshots.Count, _leases, _evictions); }
    internal void RegisterJobOwners(string jobId, IEnumerable<string> owners) { lock (_gate) { ThrowIfDisposed(); _jobOwners[jobId] = owners.Select(NormalizeOwner).Distinct(StringComparer.Ordinal).ToArray(); } }
    public void MarkJobFinished(string jobId) { lock (_gate) if (!_disposed) _finishedJobs.TryAdd(jobId, (++_finishedOrder, _time.GetUtcNow())); }
    public bool CanRemoveJob(string jobId) { lock (_gate) return !_leasedJobs.ContainsKey(jobId); }
    public bool TryRemoveJob(string jobId)
    {
        lock (_gate)
        {
            if (!CanRemoveJob(jobId)) return false;
            RemoveJobLocked(jobId);
            return true;
        }
    }
    public void RemoveJob(string jobId) => TryRemoveJob(jobId);

    public string Store(object? value, string? owner = null, string? jobId = null, IEnumerable<string>? owners = null, string? operation = null, ToolCallDto? origin = null)
        => Retain(writer => JsonSerializer.Serialize(writer, value, Json.Options), owner is null ? owners : [owner], jobId, null, operation, origin);
    public string StoreElement(JsonElement value, IEnumerable<string>? owners = null, string? jobId = null, string? operation = null, ToolCallDto? origin = null)
        => Retain(value.WriteTo, owners, jobId, OpenedHandle(value, operation), operation, origin);

    private string Retain(Action<Utf8JsonWriter> serialize, IEnumerable<string>? owners, string? jobId, string? openedHandle, string? operation = null, ToolCallDto? origin = null)
    {
        origin ??= StructuredToolResults.CurrentCall();
        var ownerSet = new HashSet<string>((owners ?? StructuredToolResults.CurrentOwners()).Select(NormalizeOwner), StringComparer.Ordinal);
        if (openedHandle is not null) ownerSet.Add(NormalizeOwner(openedHandle));
        try
        {
            using var staging = new StagingStream(this, jobId);
            lock (_gate)
            {
                ThrowIfDisposed();
                if (jobId is not null && _jobOwners.TryGetValue(jobId, out string[]? jobOwners)) ownerSet.UnionWith(jobOwners);
                ValidateOwners(ownerSet);
            }
            using (var writer = new Utf8JsonWriter(staging)) { serialize(writer); writer.Flush(); }
            if (HandleChild(operation) is string child)
            {
                using Stream stream = staging.OpenRead();
                var json = new StoredJson(stream);
                try
                {
                    string pointer = child.Length == 0 ? "/handle" : "/" + child + "/handle";
                    StoredJson.Node node = json.Locate(pointer);
                    if (node.Kind == JsonValueKind.String) ownerSet.Add(NormalizeOwner(json.Element(node, 1024, "", pointer).GetString()!));
                }
                catch (PixToolException ex) when (ex.Detail.Code == "invalid_pointer") { }
            }
            lock (_gate)
            {
                ThrowIfDisposed(); ValidateOwners(ownerSet);
                if (jobId is null)
                    while (_snapshots.Values.Count(s => s.JobId is null) >= MaxTransientResults)
                    {
                        Snapshot? oldest = _snapshots.Values.Where(s => s.JobId is null && s.Leases == 0).OrderBy(s => s.Sequence).FirstOrDefault();
                        if (oldest is null) throw Capacity();
                        RemoveLocked(oldest, eviction: true);
                    }
                string id = NewId();
                _snapshots.Add(id, staging.Commit(id, ownerSet, jobId, ++_next, origin));
                return id;
            }
        }
        finally { NotifyEvictions(); }
    }
    private static string? OpenedHandle(JsonElement value, string? operation)
    {
        string? child = HandleChild(operation);
        if (child is null || value.ValueKind != JsonValueKind.Object) return null;
        if (child.Length > 0 && !value.TryGetProperty(child, out value)) return null;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty("handle", out JsonElement handle)
            && handle.ValueKind == JsonValueKind.String ? handle.GetString() : null;
    }
    private static string? HandleChild(string? operation) => operation switch
        {
            "gpu-capture" or "pix_device_take_gpu_capture" => "gpuCapture",
            "timing-capture-stop" or "pix_device_timing_capture_stop" => "timingCapture",
            "pix_gpu_open" or "pix_timing_open" or "pix_dump_open" or "pix_device_connect" => "",
            _ => null,
        };
    // Match PixSession's handle lookup without changing case-sensitive handle identity.
    private static string NormalizeOwner(string owner) => owner.Trim();

    private void ValidateOwners(HashSet<string> owners)
    {
        if (owners.Overlaps(_closedOwners)) throw new PixToolException(PixErrors.Codes.ResultExpired, "An owning capture closed before this result could be retained. Reopen the capture and repeat the query.");
    }
    public void InvalidateOwner(string owner)
    {
        owner = NormalizeOwner(owner);
        lock (_gate)
        {
            _closedOwners.Add(owner);
            foreach (Snapshot snapshot in _snapshots.Values.Where(s => s.Owners.Contains(owner)).ToArray()) RemoveLocked(snapshot);
        }
    }
    private void RemoveJobLocked(string jobId)
    {
        _jobOwners.Remove(jobId); _finishedJobs.Remove(jobId);
        foreach (Snapshot snapshot in _snapshots.Values.Where(s => s.JobId == jobId).ToArray()) RemoveLocked(snapshot);
    }
    private void RemoveLocked(Snapshot snapshot, bool eviction = false)
    {
        _snapshots.Remove(snapshot.Id); snapshot.Removed = true;
        if (eviction) _evictions++;
        if (snapshot.Origin is not null)
        {
            _expiredOrigins[snapshot.Id] = snapshot.Origin; _expiredOrder.Enqueue(snapshot.Id);
            while (_expiredOrder.Count > MaxExpiredOrigins) _expiredOrigins.Remove(_expiredOrder.Dequeue());
        }
        if (snapshot.Leases == 0) ReleaseStorage(snapshot);
    }
    private void ReleaseStorage(Snapshot snapshot)
    {
        if (snapshot.Path is null) _memoryBytes -= snapshot.Size;
        else { File.Delete(snapshot.Path); _diskBytes -= snapshot.Size; }
        CleanDirectory();
    }
    private bool EvictOne(string? protectedJob)
    {
        Snapshot? transient = _snapshots.Values.Where(s => s.JobId is null && s.Leases == 0).OrderBy(s => s.Sequence).FirstOrDefault();
        if (transient is not null) { RemoveLocked(transient, eviction: true); return true; }
        // Only finished jobs past the eviction grace qualify; a result that pix_job_wait may be about to hand over is never taken.
        DateTimeOffset now = _time.GetUtcNow();
        string? job = _snapshots.Values.Where(s => s.JobId is not null && s.JobId != protectedJob && OutOfGrace(s.JobId, now))
            .OrderBy(s => _finishedJobs[s.JobId!].Order).ThenBy(s => s.Sequence).Select(s => s.JobId!).Distinct().FirstOrDefault(CanRemoveJob);
        if (job is null) return false;
        _evictions += _snapshots.Values.Count(s => s.JobId == job);
        RemoveJobLocked(job); _pendingEvictions.Add(job); return true;
    }
    private bool OutOfGrace(string jobId, DateTimeOffset now)
        => _finishedJobs.TryGetValue(jobId, out (long Order, DateTimeOffset At) finished) && now - finished.At >= TimeSpan.FromSeconds(EvictionGraceSeconds);
    private bool MakeDiskRoom(long bytes, string? protectedJob)
    {
        if (bytes > _diskLimit) return false;
        while (_diskBytes > _diskLimit - bytes) if (!EvictOne(protectedJob)) return false;
        return true;
    }
    private void NotifyEvictions()
    {
        string[] jobs; lock (_gate) { jobs = _pendingEvictions.ToArray(); _pendingEvictions.Clear(); }
        foreach (string job in jobs) JobEvicted?.Invoke(job);
    }
    private PixToolException Capacity() => PixErrors.ResultCapacity(StructuredToolResults.CurrentCall(),
        new(_memoryBytes, _memoryLimit, _diskBytes, _diskLimit, _snapshots.Count, _leases, _evictions));
    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(ResultStore)); }
    private void CleanDirectory()
    {
        if (!_disposed || _diskBytes != 0 || _leases != 0 || _ownership is null) return;
        File.Delete(System.IO.Path.Combine(_directory, ".owner"));
        _ownership.Dispose(); _ownership = null;
        if (Directory.Exists(_directory)) Directory.Delete(_directory);
    }

    /// <summary>Leases survive owner invalidation; new readers cannot acquire an expired reference.</summary>
    internal sealed class Lease : IDisposable
    {
        private readonly ResultStore _store;
        private readonly Snapshot _snapshot;
        private bool _released;
        private Lease(ResultStore store, Snapshot snapshot) { _store = store; _snapshot = snapshot; }
        internal Stream Open() => _snapshot.Bytes is not null ? new MemoryStream(_snapshot.Bytes, writable: false)
            : new FileStream(_snapshot.Path!, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        public void Dispose()
        {
            lock (_store._gate)
            {
                if (_released) return;
                _released = true; _snapshot.Leases--; _store._leases--;
                if (_snapshot.JobId is string job)
                {
                    if (_store._leasedJobs[job] == 1) _store._leasedJobs.Remove(job);
                    else _store._leasedJobs[job]--;
                }
                if (_snapshot.Removed && _snapshot.Leases == 0) _store.ReleaseStorage(_snapshot);
            }
        }
        internal static Lease Create(ResultStore store, string reference)
        {
            lock (store._gate)
            {
                if (store._disposed || !store._snapshots.TryGetValue(reference, out Snapshot? snapshot))
                    throw PixErrors.ResultExpired(reference, store._expiredOrigins.GetValueOrDefault(reference));
                snapshot.Leases++; store._leases++;
                if (snapshot.JobId is string job) store._leasedJobs[job] = store._leasedJobs.GetValueOrDefault(job) + 1;
                return new(store, snapshot);
            }
        }
    }
    internal Lease Acquire(string resultRef) => Lease.Create(this, resultRef);
    public static ToolCallDto ReadCall(string resultRef, string pointer = "", int offset = 0, int limit = 25, string? mode = null,
        IReadOnlyList<string>? fields = null, IReadOnlyList<WhereClause>? where = null)
        => new("pix_result_read", new { resultRef, pointer, offset, limit, mode, fields, where }, CostHints.Cached);
    /// <summary>The calls a deferred result offers: its outline first, then the first page of values.</summary>
    public static ToolCallDto[] DeferredCalls(string resultRef) => [ReadCall(resultRef, mode: "outline"), ReadCall(resultRef)];
    public ResultReadDto Read(string resultRef, string pointer = "", int offset = 0, int limit = 25, CancellationToken cancellationToken = default)
        => (ResultReadDto)Query(resultRef, pointer, offset, limit, "values", null, null, cancellationToken);

    /// <summary>A values window, an outline, or a projected array page (<see cref="ResultReadDto"/> or <see cref="ResultOutlineDto"/>).</summary>
    public object Query(string resultRef, string pointer, int offset, int limit, string? mode,
        IReadOnlyList<string>? fields, IReadOnlyList<WhereClause>? where, CancellationToken cancellationToken = default)
    {
        if (offset < 0 || limit is < 1 or > 1000) throw PixErrors.InvalidArguments("offset must be nonnegative and limit must be between 1 and 1000.");
        string shape = string.IsNullOrWhiteSpace(mode) ? "values" : mode.Trim().ToLowerInvariant();
        if (shape is not ("values" or "outline")) throw PixErrors.InvalidArguments("mode must be values or outline.");
        ResultProjection.Validate(fields, where);
        cancellationToken.ThrowIfCancellationRequested();
        using Lease lease = Acquire(resultRef); using Stream stream = lease.Open();
        var json = new StoredJson(stream, cancellationToken, resultRef);
        StoredJson.Node node = json.Locate(pointer);
        if (shape == "outline") return json.Outline(node, resultRef, pointer, offset, limit);
        if (fields is not null || where is not null) return json.Project(node, resultRef, pointer, offset, limit, fields, where);
        return json.ReadNode(node, resultRef, pointer, offset, limit);
    }
    public JsonElement ReadElement(string resultRef, string pointer = "", int? maxBytes = null, CancellationToken cancellationToken = default)
    {
        int budget = maxBytes ?? TargetBytes;
        if (budget < 1) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        cancellationToken.ThrowIfCancellationRequested();
        using Lease lease = Acquire(resultRef); using Stream stream = lease.Open();
        var json = new StoredJson(stream, cancellationToken, resultRef);
        return json.Element(json.Locate(pointer), budget, resultRef, pointer);
    }
    public IEnumerable<JsonElement> EnumerateArray(string resultRef, string pointer, int? maxItemBytes = null, CancellationToken cancellationToken = default)
    {
        int budget = maxItemBytes ?? TargetBytes;
        if (budget < 1) throw new ArgumentOutOfRangeException(nameof(maxItemBytes));
        cancellationToken.ThrowIfCancellationRequested();
        using Lease lease = Acquire(resultRef); using Stream stream = lease.Open();
        var json = new StoredJson(stream, cancellationToken, resultRef); StoredJson.Node root = json.Locate(pointer);
        if (root.Kind != JsonValueKind.Array) throw new PixToolException(PixErrors.Codes.InvalidPointer, "The selected result value must be an array.");
        foreach (var child in json.Children(root)) yield return json.Element(child.Value, budget, resultRef, pointer + "/" + child.Key);
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (Snapshot snapshot in _snapshots.Values.ToArray()) RemoveLocked(snapshot);
            _jobOwners.Clear(); _finishedJobs.Clear(); CleanDirectory();
        }
    }
    internal static JsonElement Resolve(JsonElement value, string pointer)
    {
        foreach (string token in StoredJson.PointerTokens(pointer))
        {
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(token, out JsonElement property)) value = property;
            else if (value.ValueKind == JsonValueKind.Array && StoredJson.ArrayIndex(token, out int index) && index < value.GetArrayLength()) value = value[index];
            else throw StoredJson.InvalidPointer(pointer);
        }
        return value;
    }
}
