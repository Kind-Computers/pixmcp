using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace PixMcp.Pix;

/// <summary>One read-only SQLite connection per worker job. No connection survives save, symbol resolution or close.</summary>
internal sealed partial class TimingDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly CancellationToken _cancellation;
    private readonly CancellationTokenRegistration _registration;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly double _timeoutSeconds;
    private readonly ProgressCallback _progress;
    private readonly Dictionary<string, HashSet<string>> _columns = new(StringComparer.Ordinal);

    internal TimingDatabase(string path, string? extensionPath, CancellationToken cancellation = default,
        double timeoutSeconds = 120, Action<SqliteConnection>? configureForTests = null)
    {
        if (!double.IsFinite(timeoutSeconds) || timeoutSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        cancellation.ThrowIfCancellationRequested();
        _cancellation = cancellation;
        _timeoutSeconds = timeoutSeconds;
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(path), Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private, Pooling = false, DefaultTimeout = 1,
        }.ToString());
        _progress = _ => _cancellation.IsCancellationRequested || _elapsed.Elapsed.TotalSeconds >= _timeoutSeconds ? 1 : 0;
        try
        {
            _connection.Open();
            if (configureForTests is null)
            {
                if (string.IsNullOrWhiteSpace(extensionPath) || !File.Exists(extensionPath))
                    throw new PixToolException(PixErrors.Codes.TimingSqlUnavailable, "The timing document's PixStorage SQLite extension is missing. Check the PIX installation.",
                        nextCalls: [new("pix_info", new { })]);
                try { _connection.LoadExtension(Path.GetFullPath(extensionPath), "sqlite3_batchexpand_init"); }
                catch (SqliteException ex) { throw new PixToolException(PixErrors.Codes.TimingSqlUnavailable, "Unable to load the document's PixStorage extension: " + ex.Message); }
            }
            else configureForTests(_connection);
            _connection.EnableExtensions(false);
            using (SqliteCommand command = _connection.CreateCommand())
            {
                command.CommandText = "PRAGMA query_only=ON";
                command.ExecuteNonQuery();
            }
            NativeProgress(_connection.Handle!.DangerousGetHandle(), 1000, _progress, IntPtr.Zero);
            _registration = cancellation.Register(() => SQLitePCL.raw.sqlite3_interrupt(_connection.Handle!));
        }
        catch { _connection.Dispose(); throw; }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ProgressCallback(IntPtr state);
    // Microsoft.Data.Sqlite's selected bundle supplies e_sqlite3; SQLitePCLRaw doesn't expose this hook.
    [DllImport("e_sqlite3", EntryPoint = "sqlite3_progress_handler", CallingConvention = CallingConvention.Cdecl)]
    private static extern void NativeProgress(IntPtr connection, int instructions, ProgressCallback? callback, IntPtr state);

    private void Check()
    {
        _cancellation.ThrowIfCancellationRequested();
        if (_elapsed.Elapsed.TotalSeconds >= _timeoutSeconds)
            throw new PixToolException(PixErrors.Codes.TimingQueryTimeout, $"The timing query exceeded its {_timeoutSeconds:g}-second execution budget. Select a narrower time or process range.", true);
    }

    internal T Guard<T>(Func<T> work)
    {
        try { Check(); T result = work(); Check(); return result; }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SQLitePCL.raw.SQLITE_INTERRUPT)
        {
            Check();
            throw new PixToolException(PixErrors.Codes.TimingQueryInterrupted, "SQLite interrupted the timing query: " + ex.Message, true);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is SQLitePCL.raw.SQLITE_BUSY or SQLitePCL.raw.SQLITE_LOCKED)
        { throw new PixToolException(PixErrors.Codes.TimingCaptureBusy, "The timing capture is locked by another writer. Retry after it finishes.", true); }
        catch (SqliteException ex) when (ex.SqliteErrorCode is SQLitePCL.raw.SQLITE_CORRUPT or SQLitePCL.raw.SQLITE_NOTADB)
        { throw new PixToolException(PixErrors.Codes.TimingCaptureInvalid, "The timing capture is not a readable SQLite timing database: " + ex.Message); }
    }

    private SqliteCommand Command(string sql, IEnumerable<(string name, object? value)>? parameters = null)
    {
        Check();
        SqliteCommand command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters ?? []) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private List<T> Rows<T>(string sql, Func<SqliteDataReader, T> project, params (string name, object? value)[] parameters)
    {
        using SqliteCommand command = Command(sql, parameters);
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read()) { Check(); rows.Add(project(reader)); }
        return rows;
    }

    private long Count(string table, string where = "", params (string name, object? value)[] parameters)
    {
        using SqliteCommand command = Command("SELECT COUNT(*) FROM " + table + where, parameters);
        return (long)command.ExecuteScalar()!;
    }

    private bool Has(string table, params string[] required)
    {
        if (!_columns.TryGetValue(table, out HashSet<string>? columns))
        {
            try { columns = Rows("PRAGMA table_info(\"" + table + "\")", r => r.GetString(1)).ToHashSet(StringComparer.OrdinalIgnoreCase); }
            catch (SqliteException ex) when (ex.SqliteErrorCode == SQLitePCL.raw.SQLITE_ERROR) { columns = []; }
            _columns.Add(table, columns);
        }
        return columns.Count > 0 && required.All(columns.Contains);
    }

    private void Require(string table, params string[] columns)
    {
        if (!Has(table, columns))
            throw new PixToolException(PixErrors.Codes.TimingSchemaUnsupported, $"This timing capture does not expose the required {table} columns ({string.Join(", ", columns)}). Other timing sections may still be available.");
    }

    private bool HasFunction(string name, int arguments)
        => Count("pragma_function_list", " WHERE lower(name)=lower($name) AND (narg=$arguments OR narg=-1)", ("$name", name), ("$arguments", arguments)) > 0;

    internal static string Ns(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Id(SqliteDataReader r, int i) => Ns(r.GetInt64(i));
    private static long Number(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : r.GetInt64(i);
    private static string? Text(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    internal static long? ParseNs(string? value, string name)
        => value is null ? null : long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed)
            ? parsed : throw new PixToolException(PixErrors.Codes.InvalidArguments, $"{name} must be a nonnegative decimal nanosecond timestamp within Int64 range.");
    internal static long ParseId(string value, string name)
        => ParseNs(value, name) ?? throw new PixToolException(PixErrors.Codes.InvalidArguments, name + " is required.");
    internal static void ValidatePage(int offset, int limit)
    {
        if (offset < 0 || limit is < 1 or > 1000) throw new PixToolException(PixErrors.Codes.InvalidArguments, "offset must be nonnegative and limit must be between 1 and 1000.");
    }

    internal (long start, long end, TimingRangeDto provenance) Range(long? start = null, long? end = null)
    {
        Require("CaptureFacts", "Id", "Value");
        Dictionary<long, long> facts = Rows("SELECT Id,Value FROM CaptureFacts WHERE Id IN (2,24)", r => (r.GetInt64(0), r.GetInt64(1))).ToDictionary(x => x.Item1, x => x.Item2);
        if (!facts.TryGetValue(2, out long reliableStart) || !facts.TryGetValue(24, out long reliableEnd) || reliableEnd <= reliableStart)
            throw new PixToolException(PixErrors.Codes.TimingRangeUnavailable, "The capture has no valid first-reliable/stop timestamp interval.");
        long a = start ?? reliableStart, b = end ?? reliableEnd;
        if (a < 0 || b <= a) throw new PixToolException(PixErrors.Codes.InvalidArguments, "The selected time range requires 0 <= startNs < endNs.");
        return (a, b, new(Ns(a), Ns(b), Ns(reliableStart), Ns(reliableEnd)));
    }

    public void Dispose()
    {
        _registration.Dispose();
        if (_connection.State == System.Data.ConnectionState.Open)
            NativeProgress(_connection.Handle!.DangerousGetHandle(), 0, null, IntPtr.Zero);
        _connection.Dispose();
        GC.KeepAlive(_progress);
    }
}
