using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace PixMcp.Pix.Sql;

/// <summary>
/// One private, read-only SQLite connection with query_only, extensions disabled after configuration, a progress
/// handler that enforces a wall-clock budget, and interruption on cancellation or invalidation. Subclasses bind it to
/// a document (recorded timing captures today, the GPU SQL store later) and choose the error codes their callers see.
/// </summary>
internal abstract class ReadOnlySqlite : IDisposable
{
    protected readonly SqliteConnection Connection;
    private readonly CancellationToken _cancellation;
    private readonly CancellationToken _invalidation;
    private readonly CancellationTokenRegistration _registration;
    private readonly CancellationTokenRegistration _invalidationRegistration;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly double _timeoutSeconds;
    private readonly ProgressCallback _progress;
    private readonly Dictionary<string, HashSet<string>> _columns = new(StringComparer.Ordinal);
    private StatementBudget? _budget;
    private bool _disposed;

    /// <summary>A narrower budget for one statement, with the codes its timeout and interruption report.</summary>
    private sealed record StatementBudget(double EndSeconds, double Seconds, string TimeoutCode, string InterruptedCode, string Subject);

    protected ReadOnlySqlite(string path, CancellationToken cancellation, CancellationToken invalidation, double timeoutSeconds, Action<SqliteConnection> configure)
    {
        if (!double.IsFinite(timeoutSeconds) || timeoutSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        cancellation.ThrowIfCancellationRequested();
        _cancellation = cancellation;
        _invalidation = invalidation;
        _timeoutSeconds = timeoutSeconds;
        Connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(path), Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private, Pooling = false, DefaultTimeout = 1,
        }.ToString());
        _progress = _ => _cancellation.IsCancellationRequested || _invalidation.IsCancellationRequested || BudgetExceeded() ? 1 : 0;
        try
        {
            Connection.Open();
            configure(Connection);
            Connection.EnableExtensions(false);
            using (SqliteCommand command = Connection.CreateCommand())
            {
                command.CommandText = "PRAGMA query_only=ON";
                command.ExecuteNonQuery();
            }
            NativeProgress(Connection.Handle!.DangerousGetHandle(), 1000, _progress, IntPtr.Zero);
            _registration = cancellation.Register(Interrupt);
            _invalidationRegistration = invalidation.Register(Interrupt);
        }
        catch { Connection.Dispose(); throw; }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ProgressCallback(IntPtr state);
    // Microsoft.Data.Sqlite's selected bundle supplies e_sqlite3; the managed progress hook allocates per call.
    [DllImport("e_sqlite3", EntryPoint = "sqlite3_progress_handler", CallingConvention = CallingConvention.Cdecl)]
    private static extern void NativeProgress(IntPtr connection, int instructions, ProgressCallback? callback, IntPtr state);

    /// <summary>Code and message for a query that outlived the connection budget.</summary>
    protected virtual string TimeoutCode => PixErrors.Codes.TimingQueryTimeout;
    protected virtual string TimeoutMessage(double seconds)
        => $"The timing query exceeded its {seconds.ToString("g", CultureInfo.InvariantCulture)}-second execution budget. Select a narrower time or process range.";
    protected virtual string InterruptedCode => PixErrors.Codes.TimingQueryInterrupted;
    protected virtual string InvalidatedCode => PixErrors.Codes.TimingQueryInvalidated;
    protected virtual string InvalidatedMessage => "The timing capture changed while this query was running (save, symbol resolution or close). Repeat the query.";
    protected virtual string BusyCode => PixErrors.Codes.TimingCaptureBusy;
    protected virtual string InvalidDocumentCode => PixErrors.Codes.TimingCaptureInvalid;
    protected virtual string SchemaUnsupportedCode => PixErrors.Codes.TimingSchemaUnsupported;
    /// <summary>What the database is called in messages ("timing capture").</summary>
    protected virtual string DocumentName => "timing capture";

    internal sqlite3 Handle => Connection.Handle ?? throw new ObjectDisposedException(GetType().Name);
    internal string SqliteVersion => raw.sqlite3_libversion().utf8_to_string();
    internal TimeSpan Elapsed => _elapsed.Elapsed;

    /// <summary>Stops the statement running on this connection; the caller sees the interruption code.</summary>
    internal void Interrupt()
    {
        if (_disposed) return;
        try { raw.sqlite3_interrupt(Connection.Handle!); }
        catch (ObjectDisposedException) { }
        catch (NullReferenceException) { }
    }

    private bool BudgetExceeded()
    {
        double elapsed = _elapsed.Elapsed.TotalSeconds;
        return elapsed >= _timeoutSeconds || (_budget is { } budget && elapsed >= budget.EndSeconds);
    }

    /// <summary>Throws for invalidation, cancellation, or an exhausted connection or statement budget.</summary>
    internal void Check()
    {
        if (_invalidation.IsCancellationRequested) throw new PixToolException(InvalidatedCode, InvalidatedMessage, true);
        _cancellation.ThrowIfCancellationRequested();
        double elapsed = _elapsed.Elapsed.TotalSeconds;
        if (_budget is { } budget && elapsed >= budget.EndSeconds && budget.EndSeconds <= _timeoutSeconds)
            throw new PixToolException(budget.TimeoutCode,
                $"The {budget.Subject} exceeded its {budget.Seconds.ToString("g", CultureInfo.InvariantCulture)}-second budget. Narrow it (a time window, LIMIT, indexed predicates) or raise timeoutSeconds.", true);
        if (elapsed >= _timeoutSeconds) throw new PixToolException(TimeoutCode, TimeoutMessage(_timeoutSeconds), true);
    }

    /// <summary>After SQLite reported SQLITE_INTERRUPT: the explaining condition if there is one, else the interruption code.</summary>
    internal PixToolException Interrupted(string detail)
    {
        Check();
        return new PixToolException(_budget?.InterruptedCode ?? InterruptedCode, "SQLite interrupted the query: " + detail, true);
    }

    /// <summary>Runs <paramref name="work"/> with a statement budget (capped by the connection budget) and its own codes.</summary>
    internal T WithBudget<T>(double seconds, string timeoutCode, string interruptedCode, string subject, Func<T> work)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        StatementBudget? previous = _budget;
        _budget = new(_elapsed.Elapsed.TotalSeconds + seconds, seconds, timeoutCode, interruptedCode, subject);
        try { return work(); }
        finally { _budget = previous; }
    }

    internal T Guard<T>(Func<T> work)
    {
        try { Check(); T result = work(); Check(); return result; }
        catch (SqliteException ex) when (ex.SqliteErrorCode == raw.SQLITE_INTERRUPT)
        {
            throw Interrupted(ex.Message);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is raw.SQLITE_BUSY or raw.SQLITE_LOCKED)
        { throw new PixToolException(BusyCode, $"The {DocumentName} is locked by another writer. Retry after it finishes.", true); }
        catch (SqliteException ex) when (ex.SqliteErrorCode is raw.SQLITE_CORRUPT or raw.SQLITE_NOTADB)
        { throw new PixToolException(InvalidDocumentCode, $"The {DocumentName} is not a readable SQLite database: " + ex.Message); }
    }

    protected SqliteCommand Command(string sql, IEnumerable<(string name, object? value)>? parameters = null)
    {
        Check();
        SqliteCommand command = Connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters ?? []) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    protected List<T> Rows<T>(string sql, Func<SqliteDataReader, T> project, params (string name, object? value)[] parameters)
    {
        using SqliteCommand command = Command(sql, parameters);
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read()) { Check(); rows.Add(project(reader)); }
        return rows;
    }

    protected long Count(string table, string where = "", params (string name, object? value)[] parameters)
    {
        using SqliteCommand command = Command("SELECT COUNT(*) FROM " + table + where, parameters);
        return (long)command.ExecuteScalar()!;
    }

    protected bool Has(string table, params string[] required)
    {
        if (!_columns.TryGetValue(table, out HashSet<string>? columns))
        {
            // table_xinfo also lists hidden virtual-table columns (constraint inputs such as CpuExecutionRowId).
            try { columns = Rows("PRAGMA table_xinfo(\"" + table + "\")", r => r.GetString(1)).ToHashSet(StringComparer.OrdinalIgnoreCase); }
            catch (SqliteException ex) when (ex.SqliteErrorCode == raw.SQLITE_ERROR) { columns = []; }
            _columns.Add(table, columns);
        }
        return columns.Count > 0 && required.All(columns.Contains);
    }

    protected void Require(string table, params string[] columns)
    {
        if (!Has(table, columns))
            throw new PixToolException(SchemaUnsupportedCode, $"This {DocumentName} does not expose the required {table} columns ({string.Join(", ", columns)}). Other sections may still be available.");
    }

    protected bool HasFunction(string name, int arguments)
        => Count("pragma_function_list", " WHERE lower(name)=lower($name) AND (narg=$arguments OR narg=-1)", ("$name", name), ("$arguments", arguments)) > 0;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _registration.Dispose();
        _invalidationRegistration.Dispose();
        if (Connection.State == System.Data.ConnectionState.Open)
            NativeProgress(Connection.Handle!.DangerousGetHandle(), 0, null, IntPtr.Zero);
        Connection.Dispose();
        GC.KeepAlive(_progress);
    }
}
