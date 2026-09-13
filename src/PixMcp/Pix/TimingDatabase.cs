using System.Globalization;
using Microsoft.Data.Sqlite;
using PixMcp.Pix.Sql;

namespace PixMcp.Pix;

/// <summary>
/// One read-only SQLite connection over a timing capture's PixStorage database per query job. Jobs run off the PIX
/// worker behind the handle's document gate; no connection survives save, symbol resolution or close.
/// </summary>
internal sealed partial class TimingDatabase : ReadOnlySqlite
{
    internal TimingDatabase(string path, string? extensionPath, CancellationToken cancellation = default,
        double timeoutSeconds = 120, Action<SqliteConnection>? configureForTests = null, CancellationToken invalidation = default)
        : base(path, cancellation, invalidation, timeoutSeconds, configureForTests ?? (connection => LoadPixStorage(connection, extensionPath)))
    {
    }

    /// <summary>Loads the document's PixStorage extension (virtual tables and findstackid) before extensions are disabled.</summary>
    private static void LoadPixStorage(SqliteConnection connection, string? extensionPath)
    {
        if (string.IsNullOrWhiteSpace(extensionPath) || !File.Exists(extensionPath))
            throw new PixToolException(PixErrors.Codes.TimingSqlUnavailable, "The timing document's PixStorage SQLite extension is missing. Check the PIX installation.",
                nextCalls: [new("pix_info", new { })]);
        try { connection.LoadExtension(Path.GetFullPath(extensionPath), "sqlite3_batchexpand_init"); }
        catch (SqliteException ex) { throw new PixToolException(PixErrors.Codes.TimingSqlUnavailable, "Unable to load the document's PixStorage extension: " + ex.Message); }
    }

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

    internal (long start, long end, TimingRangeDto provenance) Range(long? start = null, long? end = null, string rangeMode = RangeModeFull)
    {
        Require("CaptureFacts", "Id", "Value");
        Dictionary<long, long> facts = Rows("SELECT Id,Value FROM CaptureFacts WHERE Id IN (2,3,24)", r => (r.GetInt64(0), r.GetInt64(1))).ToDictionary(x => x.Item1, x => x.Item2);
        if (!facts.TryGetValue(2, out long reliableStart))
            throw new PixToolException(PixErrors.Codes.TimingRangeUnavailable, "The capture has no first-reliable timestamp (CaptureFacts 2).");
        long? reliableEnd = facts.TryGetValue(24, out long stop) && stop > reliableStart ? stop : null;
        long? captureEnd = facts.TryGetValue(3, out long last) && last > reliableStart ? last : null;
        long defaultEnd = (rangeMode == RangeModeReliable ? reliableEnd ?? captureEnd : captureEnd ?? reliableEnd)
            ?? throw new PixToolException(PixErrors.Codes.TimingRangeUnavailable, "The capture has neither a stop (CaptureFacts 24) nor an end (CaptureFacts 3) timestamp.");
        long a = start ?? reliableStart, b = end ?? defaultEnd;
        if (a < 0 || b <= a) throw new PixToolException(PixErrors.Codes.InvalidArguments, "The selected time range requires 0 <= startNs < endNs.");
        string note = start is not null || end is not null
            ? "Explicit startNs/endNs select the window."
            : rangeMode == RangeModeReliable
                ? "rangeMode=reliable ends at the stop timestamp; data recorded after it is excluded."
                : "rangeMode=full runs through the capture end, including data recorded after the stop timestamp.";
        var provenance = new TimingRangeDto(Ns(a), Ns(b), Ns(reliableStart), Ns(reliableEnd ?? captureEnd ?? b))
        {
            RangeMode = rangeMode,
            CaptureEndNs = captureEnd is long captured ? Ns(captured) : null,
            Coverage = Coverage(a, b),
            Note = note,
        };
        return (a, b, provenance);
    }

    private readonly Dictionary<string, long> _coverageTotals = new(StringComparer.Ordinal);

    /// <summary>In-window versus total rows per recorded family; totals are computed once per connection.</summary>
    private TimingCoverageDto Coverage(long a, long b)
    {
        TimingCoverageCountDto? Family(string table, string begin, string? finish)
        {
            if (!(finish is null ? Has(table, begin) : Has(table, begin, finish))) return null;
            if (!_coverageTotals.TryGetValue(table, out long total)) _coverageTotals[table] = total = Count(Quote(table));
            string where = finish is null
                ? $" WHERE {begin} >= $start AND {begin} < $end"
                : $" WHERE {begin} < $end AND ({finish} > $start OR ({finish} = {begin} AND {begin} >= $start))";
            return new TimingCoverageCountDto(Count(Quote(table), where, ("$start", a), ("$end", b)), total);
        }
        return new TimingCoverageDto(Family("ContextSwitch", "Timestamp", null), Family("PixCpuExecution", "BeginTimestamp", "EndTimestamp"),
            Family("ApiQueueExecution", "BeginTimestamp", "EndTimestamp"), Family("GpuWorkRange", "BeginTimestamp", "EndTimestamp"));
    }
}
