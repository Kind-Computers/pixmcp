using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PixMcp.Pix.Sql;

/// <summary>A private read-only connection to a GPU SQL store, with SQL error codes; closing the store interrupts it.</summary>
internal sealed class GpuSqlDatabase : ReadOnlySqlite
{
    internal GpuSqlDatabase(string path, CancellationToken cancellation, CancellationToken closing, double timeoutSeconds)
        : base(path, cancellation, closing, timeoutSeconds, _ => { })
    {
    }

    protected override string TimeoutCode => PixErrors.Codes.SqlTimeout;
    protected override string TimeoutMessage(double seconds)
        => $"The GPU SQL query exceeded its {seconds.ToString("g", CultureInfo.InvariantCulture)}-second budget. Add indexed predicates or LIMIT, or raise timeoutSeconds.";
    protected override string InterruptedCode => PixErrors.Codes.SqlInterrupted;
    protected override string InvalidatedCode => PixErrors.Codes.SqlStoreClosed;
    protected override string InvalidatedMessage => "The GPU capture handle closed while this query was running.";
    protected override string BusyCode => PixErrors.Codes.SqlStoreBusy;
    protected override string InvalidDocumentCode => PixErrors.Codes.SqlStoreClosed;
    protected override string SchemaUnsupportedCode => PixErrors.Codes.SqlTablesNotPopulated;
    protected override string DocumentName => "GPU SQL store";

    /// <summary>One integer cell of a server-built statement, or null.</summary>
    internal long? Scalar(string sql, params (string name, object? value)[] parameters)
        => Guard(() =>
        {
            using SqliteCommand command = Command(sql, parameters);
            object? value = command.ExecuteScalar();
            return value is null or DBNull ? (long?)null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        });
}
