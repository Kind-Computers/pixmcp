using SQLitePCL;

namespace PixMcp.Pix.Sql;

/// <summary>
/// Installs SQLite limits and an authorizer on a connection for the lifetime of one caller-supplied statement
/// (prepare and step, because a schema re-prepare consults the authorizer again). Only SELECT, READ, RECURSIVE and
/// functions outside the deny list are allowed; every other action is denied and remembered for the error message.
/// READ actions record the tables and views the statement touches. Dispose removes the authorizer and restores the limits.
/// </summary>
internal sealed class SqlStatementGuard : IDisposable
{
    /// <summary>Functions that reach the file system, load code or alter tokenizers; denied even if an extension registered them.</summary>
    public static readonly IReadOnlySet<string> DeniedFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "load_extension", "readfile", "writefile", "edit", "fts3_tokenizer", "zipfile", "zipfile_cds", "sqlar_compress", "sqlar_uncompress", "fsdir", "lsdir",
    };

    /// <summary>Limits applied while the statement exists (id, value); the SQL length leaves room for the paging wrapper.</summary>
    public static readonly IReadOnlyList<(int Id, int Value)> Limits =
    [
        (raw.SQLITE_LIMIT_SQL_LENGTH, SqlRequest.MaxSqlLength + 1024),
        (raw.SQLITE_LIMIT_COMPOUND_SELECT, 32),
        (raw.SQLITE_LIMIT_EXPR_DEPTH, 256),
        (raw.SQLITE_LIMIT_VARIABLE_NUMBER, 64),
        (raw.SQLITE_LIMIT_ATTACHED, 0),
        (raw.SQLITE_LIMIT_LIKE_PATTERN_LENGTH, 2000),
        (raw.SQLITE_LIMIT_TRIGGER_DEPTH, 0),
        (raw.SQLITE_LIMIT_VDBE_OP, 250000),
    ];

    private static readonly string[] ActionNames =
    [
        "COPY", "CREATE INDEX", "CREATE TABLE", "CREATE TEMP INDEX", "CREATE TEMP TABLE", "CREATE TEMP TRIGGER", "CREATE TEMP VIEW",
        "CREATE TRIGGER", "CREATE VIEW", "DELETE", "DROP INDEX", "DROP TABLE", "DROP TEMP INDEX", "DROP TEMP TABLE", "DROP TEMP TRIGGER",
        "DROP TEMP VIEW", "DROP TRIGGER", "DROP VIEW", "INSERT", "PRAGMA", "READ", "SELECT", "TRANSACTION", "UPDATE", "ATTACH", "DETACH",
        "ALTER TABLE", "REINDEX", "ANALYZE", "CREATE VIRTUAL TABLE", "DROP VIRTUAL TABLE", "FUNCTION", "SAVEPOINT", "RECURSIVE",
    ];

    private const int Select = 21, Read = 20, Function = 31, Recursive = 33;

    private readonly sqlite3 _db;
    private readonly (int Id, int Previous)[] _previous;
    // Rooted for the statement's lifetime: SQLite calls back into it during prepare and step.
    private readonly strdelegate_authorizer _authorizer;
    private readonly SortedSet<string> _tables = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    private SqlStatementGuard(sqlite3 db)
    {
        _db = db;
        _previous = Limits.Select(l => (l.Id, raw.sqlite3_limit(db, l.Id, l.Value))).ToArray();
        _authorizer = Authorize;
        raw.sqlite3_set_authorizer(db, _authorizer, null);
    }

    public static SqlStatementGuard Install(sqlite3 db) => new(db);

    /// <summary>Tables, virtual tables and views read so far (sqlite_stat* excluded), sorted.</summary>
    public IReadOnlyList<string> ReferencedTables => _tables.ToArray();
    /// <summary>The first denied action, such as "INSERT Strings" or "FUNCTION load_extension".</summary>
    public string? Denied { get; private set; }

    public static string ActionName(int action) => action >= 0 && action < ActionNames.Length ? ActionNames[action] : "ACTION " + action;

    private int Authorize(object userData, int action, string param0, string param1, string dbName, string innerMostTriggerOrView)
    {
        switch (action)
        {
            case Select:
            case Recursive:
                return raw.SQLITE_OK;
            case Read:
                if (!string.IsNullOrEmpty(param0) && !param0.StartsWith("sqlite_stat", StringComparison.OrdinalIgnoreCase)) _tables.Add(param0);
                if (!string.IsNullOrEmpty(innerMostTriggerOrView)) _tables.Add(innerMostTriggerOrView);
                return raw.SQLITE_OK;
            case Function when string.IsNullOrEmpty(param1) || !DeniedFunctions.Contains(param1):
                return raw.SQLITE_OK;
            default:
                Denied ??= string.Join(' ', new[] { ActionName(action), action == Function ? param1 : param0, action == Function ? null : param1 }
                    .Where(part => !string.IsNullOrEmpty(part)));
                return raw.SQLITE_DENY;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        raw.sqlite3_set_authorizer(_db, (strdelegate_authorizer)null!, null);
        foreach ((int id, int previous) in _previous) raw.sqlite3_limit(_db, id, previous);
        GC.KeepAlive(_authorizer);
    }
}
