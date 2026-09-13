using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PixMcp.Pix.Sql;

internal sealed record GpuSqlFamilyState(string Family, string State, long Rows, string? PopulatedAt, int? AnalysisGeneration, string? Detail);

internal sealed record GpuSqlTableRows(string Table, IReadOnlyList<object?[]> Rows);

/// <summary>
/// One GPU capture's private SQLite store in the session's private result directory: created with the full schema, written one
/// family per transaction (readers never see a half-written family), read through <see cref="GpuSqlDatabase"/> connections
/// under a lifecycle read lock, and deleted on dispose after interrupting live readers.
/// </summary>
internal sealed class GpuSqlStore : IDisposable
{
    private const int MaxQueryJobs = 32;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly ReaderWriterLockSlim _lifecycle = new(LockRecursionPolicy.SupportsRecursion);
    private readonly CancellationTokenSource _closing = new();
    private readonly ConcurrentDictionary<string, GpuSqlFamilyState> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Job> _queryJobs = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _queryOrder = new();
    private bool _disposed;

    internal GpuSqlStore(string handle, string path, long maxBytes)
    {
        Handle = handle;
        Path = System.IO.Path.GetFullPath(path);
        MaxBytes = maxBytes;
        foreach (string file in Files)
            if (File.Exists(file)) File.Delete(file);
        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        foreach (string statement in GpuSqlSchema.CreateStatements()) Execute(connection, transaction, statement);
        transaction.Commit();
    }

    public string Handle { get; }
    public string Path { get; }
    public long MaxBytes { get; }
    internal object JobsGate { get; } = new();
    internal Dictionary<string, Job> PopulateJobs { get; } = new(StringComparer.Ordinal);
    private string[] Files => [Path, Path + "-journal", Path + "-wal", Path + "-shm"];

    public long Bytes => Files.Where(File.Exists).Sum(file => new FileInfo(file).Length);

    public GpuSqlFamilyState State(string family) => _states.TryGetValue(family, out GpuSqlFamilyState? state) ? state : new(family, "empty", 0, null, null, null);

    public bool IsReady(string family) => State(family).State == "ready";

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Private, Pooling = false, DefaultTimeout = 5,
        }.ToString());
        connection.Open();
        Execute(connection, null, "PRAGMA journal_mode=DELETE");
        Execute(connection, null, "PRAGMA synchronous=OFF");
        return connection;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Replaces a family's tables in one transaction; over the byte cap the transaction rolls back with sql_capacity_exceeded.</summary>
    public GpuSqlFamilyState Write(string family, IReadOnlyList<GpuSqlTableRows> tables, int analysisGeneration, string? detail, CancellationToken cancellation)
        => Read(_ =>
        {
            _writer.Wait(cancellation);
            try
            {
                using SqliteConnection connection = Open();
                using SqliteTransaction transaction = connection.BeginTransaction();
                long rows = 0;
                foreach (GpuSqlTable table in GpuSqlSchema.Tables.Where(t => t.Family == family))
                {
                    Execute(connection, transaction, "DELETE FROM " + table.Name);
                    IReadOnlyList<object?[]> data = tables.FirstOrDefault(t => t.Table == table.Name)?.Rows ?? [];
                    if (data.Count == 0) continue;
                    using SqliteCommand insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText = table.Insert;
                    var parameters = new SqliteParameter[table.Columns.Count];
                    for (int i = 0; i < parameters.Length; i++)
                    {
                        parameters[i] = insert.CreateParameter();
                        parameters[i].ParameterName = "$p" + i.ToString(CultureInfo.InvariantCulture);
                        insert.Parameters.Add(parameters[i]);
                    }
                    foreach (object?[] row in data)
                    {
                        if (row.Length != parameters.Length)
                            throw new InvalidOperationException($"A {table.Name} row has {row.Length} cells; the table has {parameters.Length} columns.");
                        cancellation.ThrowIfCancellationRequested();
                        for (int i = 0; i < row.Length; i++) parameters[i].Value = row[i] ?? DBNull.Value;
                        insert.ExecuteNonQuery();
                    }
                    rows += data.Count;
                }
                string populatedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                using (SqliteCommand status = connection.CreateCommand())
                {
                    status.Transaction = transaction;
                    status.CommandText = $"INSERT OR REPLACE INTO {GpuSqlSchema.StatusTable} (family, state, rows, populated_at, analysis_generation, detail) VALUES ($family, 'ready', $rows, $at, $generation, $detail)";
                    status.Parameters.AddWithValue("$family", family);
                    status.Parameters.AddWithValue("$rows", rows);
                    status.Parameters.AddWithValue("$at", populatedAt);
                    status.Parameters.AddWithValue("$generation", (long)analysisGeneration);
                    status.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
                    status.ExecuteNonQuery();
                }
                long bytes;
                using (SqliteCommand size = connection.CreateCommand())
                {
                    size.Transaction = transaction;
                    size.CommandText = "SELECT page_count * page_size FROM pragma_page_count(), pragma_page_size()";
                    bytes = Convert.ToInt64(size.ExecuteScalar(), CultureInfo.InvariantCulture);
                }
                if (bytes > MaxBytes)
                {
                    transaction.Rollback();
                    throw new PixToolException(PixErrors.Codes.SqlCapacityExceeded,
                        $"Populating {family} would grow the GPU SQL store to {bytes} bytes, above {ServerOptions.GpuSqlMaxVariable} ({MaxBytes}). Populate fewer families or raise the cap.",
                        false, [new ToolCallDto("pix_gpu_sql_tables", new { handle = Handle }, CostHints.Cached), new ToolCallDto("pix_info", new { }, CostHints.Cached)]);
                }
                transaction.Commit();
                var state = new GpuSqlFamilyState(family, "ready", rows, populatedAt, analysisGeneration, detail);
                _states[family] = state;
                return state;
            }
            finally { _writer.Release(); }
        });

    /// <summary>Runs work under the lifecycle read lock with the closing token; a closed store fails with sql_store_closed.</summary>
    public T Read<T>(Func<CancellationToken, T> work)
    {
        while (!_lifecycle.TryEnterReadLock(100))
            if (_closing.IsCancellationRequested) throw Closed();
        try
        {
            if (_disposed || _closing.IsCancellationRequested) throw Closed();
            return work(_closing.Token);
        }
        finally { _lifecycle.ExitReadLock(); }
    }

    /// <summary>The cached or running query job for identical arguments, else a new one from <paramref name="start"/>.</summary>
    internal Job QueryJob(string key, ResultStore results, Func<Job> start)
    {
        lock (JobsGate)
        {
            if (_queryJobs.TryGetValue(key, out Job? current))
            {
                if (!current.IsFinished || current.Status == JobStatus.Succeeded && current.ResultRef is string result && results.IsAvailable(result)) return current;
                _queryJobs.Remove(key);
                _queryOrder.Remove(key);
            }
            Job job = start();
            _queryJobs[key] = job;
            _queryOrder.AddLast(key);
            for (LinkedListNode<string>? node = _queryOrder.First; node is not null && _queryJobs.Count > MaxQueryJobs;)
            {
                LinkedListNode<string>? next = node.Next;
                if (_queryJobs.TryGetValue(node.Value, out Job? old) && old.IsFinished)
                {
                    _queryJobs.Remove(node.Value);
                    _queryOrder.Remove(node);
                }
                node = next;
            }
            return job;
        }
    }

    private static PixToolException Closed()
        => new(PixErrors.Codes.SqlStoreClosed, "The GPU SQL store closed with its capture handle. Reopen the capture and populate again.", false,
            [new ToolCallDto("pix_handles", new { }, CostHints.Cached)]);

    public void Dispose()
    {
        if (_disposed) return;
        _closing.Cancel();
        bool exclusive = _lifecycle.TryEnterWriteLock(TimeSpan.FromSeconds(5));
        try
        {
            _disposed = true;
            foreach (string file in Files)
            {
                try { if (File.Exists(file)) File.Delete(file); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        finally { if (exclusive) _lifecycle.ExitWriteLock(); }
    }
}
