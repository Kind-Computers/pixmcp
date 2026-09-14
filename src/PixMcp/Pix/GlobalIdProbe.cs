using System.Globalization;
using System.Text;
using PixMcp.Pix.Handles;

namespace PixMcp.Pix;

/// <summary>One row of pixtool's save-event-list CSV. pixtool labels the first column "Queue ID", but it holds the event index.</summary>
internal readonly record struct EventListRow(uint Index, string Name, uint? GlobalId);

/// <summary>Whether pixtool Global IDs equal this capture's native GPU ids: verified, mismatch or inconclusive.</summary>
public sealed record GlobalIdMappingDto(string State, int Compared, int? QueueIndex, string? FirstMismatch, string Method);

/// <summary>
/// Checks that pixtool's Global IDs are the native GPU ids before a tool selects events or ranges by them. pixtool lists only one queue, so the
/// list is matched to the queue whose event names agree at the same indices.
/// </summary>
internal static class GlobalIdProbe
{
    public const string Verified = "verified", Mismatch = "mismatch", Inconclusive = "inconclusive";
    public const int CompareRows = 64, MinimumComparable = 8;
    private const string Method = "save-event-list: up to 64 rows compared with native GPU ids";
    private static readonly char Bom = (char)0xFEFF, Lf = (char)10, Cr = (char)13;

    /// <summary>Parses the CSV (UTF-8 BOM, a space after each comma, RFC 4180 quotes). Null when the header lacks the index, Name or Global ID column.</summary>
    public static IReadOnlyList<EventListRow>? Parse(string text)
    {
        List<List<string>> records = ReadRecords(text.TrimStart(Bom));
        if (records.Count == 0) return null;
        List<string> header = records[0];
        int index = header.FindIndex(h => h is "Queue ID" or "Index");
        int name = header.FindIndex(h => h == "Name");
        int global = header.FindIndex(h => h == "Global ID");
        if (index < 0 || name < 0 || global < 0) return null;
        var rows = new List<EventListRow>();
        foreach (List<string> raw in records.Skip(1))
        {
            List<string> record = raw;
            // pixtool does not always quote names; extra commas belong to the Name column when it precedes Global ID.
            int extra = record.Count - header.Count;
            if (extra > 0 && name < global && index < name)
                record = [.. record.Take(name), string.Join(", ", record.Skip(name).Take(extra + 1)), .. record.Skip(name + extra + 1)];
            if (record.Count <= Math.Max(index, Math.Max(name, global))) continue;
            if (!uint.TryParse(record[index], NumberStyles.None, CultureInfo.InvariantCulture, out uint eventIndex)) continue;
            uint? id = uint.TryParse(record[global], NumberStyles.None, CultureInfo.InvariantCulture, out uint parsed) ? parsed : null;
            rows.Add(new(eventIndex, record[name], id));
        }
        return rows;
    }

    /// <summary>
    /// Compares up to 64 rows that carry a Global ID with the cached events of the queue the list describes. Verified needs at least eight
    /// comparable rows, all with equal GPU id and name; the first difference is a mismatch.
    /// </summary>
    public static GlobalIdMappingDto Verify(IReadOnlyList<EventListRow>? rows, IReadOnlyList<EventRecord[]> queues)
    {
        if (rows is null) return new(Inconclusive, 0, null, "the event list has no index, Name and Global ID columns", Method);
        EventListRow[] sample = rows.Take(CompareRows).ToArray();
        int? queue = null;
        int best = 0;
        for (int q = 0; q < queues.Count; q++)
        {
            EventRecord[] events = queues[q];
            int matches = sample.Count(r => r.Index < events.Length && events[r.Index].Name == r.Name);
            if (matches > best)
            {
                best = matches;
                queue = q;
            }
        }
        if (queue is not int chosen || best * 2 < sample.Length)
            return new(Inconclusive, 0, null, "no queue's event names match the event list", Method);
        EventRecord[] listed = queues[chosen];
        int compared = 0;
        foreach (EventListRow row in sample)
        {
            if (row.GlobalId is not uint id || row.Index >= listed.Length) continue;
            compared++;
            EventRecord e = listed[row.Index];
            if (e.GpuId != id || e.Name != row.Name)
            {
                string native = e.GpuId == uint.MaxValue ? "none" : e.GpuId.ToString(CultureInfo.InvariantCulture);
                return new(Mismatch, compared, chosen, $"event {row.Index} '{row.Name}': pixtool Global ID {id}, native GPU id {native} ('{e.Name}')", Method);
            }
        }
        return compared >= MinimumComparable
            ? new(Verified, compared, chosen, null, Method)
            : new(Inconclusive, compared, chosen, $"only {compared} comparable rows (at least {MinimumComparable} are needed)", Method);
    }

    /// <summary>Verifies the event list pixtool wrote and keeps a verified or mismatch verdict on the capture; an inconclusive one is checked again next time.</summary>
    public static GlobalIdMappingDto Record(GpuCaptureHandle capture, string csvPath, Action<string> diagnostic)
    {
        GlobalIdMappingDto mapping;
        try
        {
            mapping = File.Exists(csvPath)
                ? Verify(Parse(File.ReadAllText(csvPath, Encoding.UTF8)), capture.Queues.Select(q => capture.AllEvents(q.Index)).ToArray())
                : new(Inconclusive, 0, null, "pixtool did not write the event list", Method);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            mapping = new(Inconclusive, 0, null, "the event list could not be read: " + ex.Message, Method);
        }
        if (mapping.State != Inconclusive) capture.GlobalIdMapping = mapping;
        diagnostic($"Global ID check: {mapping.State} ({mapping.Compared} events compared).");
        return mapping;
    }

    public static PixToolException MismatchError(GlobalIdMappingDto mapping, params ToolCallDto[] nextCalls)
        => new(PixErrors.Codes.GlobalIdMismatch,
            $"pixtool Global IDs do not match this capture's GPU ids ({mapping.FirstMismatch}), so exact-event selection is disabled for this capture.", false, nextCalls);

    /// <summary>RFC 4180 records. Unquoted fields are trimmed (pixtool writes a space after each comma); quoted fields keep their content.</summary>
    private static List<List<string>> ReadRecords(string text)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        bool quoted = false, wasQuoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c != '"') field.Append(c);
                else if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else quoted = false;
            }
            else if (c == '"' && !wasQuoted && field.ToString().Trim().Length == 0)
            {
                field.Clear();
                quoted = wasQuoted = true;
            }
            else if (c == ',') record.Add(Finish());
            else if (c == Lf || c == Cr)
            {
                if (c == Cr && i + 1 < text.Length && text[i + 1] == Lf) i++;
                record.Add(Finish());
                if (record.Count > 1 || record[0].Length > 0) records.Add(record);
                record = new List<string>();
            }
            else if (!wasQuoted || !char.IsWhiteSpace(c)) field.Append(c);
        }
        if (field.Length > 0 || record.Count > 0)
        {
            record.Add(Finish());
            records.Add(record);
        }
        return records;

        string Finish()
        {
            string value = wasQuoted ? field.ToString() : field.ToString().Trim();
            field.Clear();
            wasQuoted = false;
            return value;
        }
    }
}
