using System.Text;
using System.Text.Json;

namespace PixMcp.Pix;

public sealed record ComparisonChangesDto(string FullResultRef, int Total, int Offset, int Count,
    int? NextOffset, IReadOnlyList<JsonElement> Items, IReadOnlyList<ToolCallDto> NextCalls);

/// <summary>Filters detached comparison rows, keeping only the requested sorted prefix in memory.</summary>
internal static class ComparisonResultQuery
{
    public static readonly string[] Directions = ["all", "regressions", "improvements", "structural"];
    public static readonly string[] SortKeys = ["absoluteDeltaNs", "deltaNs", "deltaPercent", "event"];
    public static readonly string[] Confidences = ["low", "medium", "high"];

    private sealed record Row(int Index, StoredJson.Node Node, EventRef Baseline, EventRef Candidate, decimal? DeltaNs, double? DeltaPercent);

    internal static ComparisonChangesDto Read(ResultStore store, string fullResultRef, string direction = "all",
        decimal minDeltaNs = 0, double minDeltaPercent = 0, string sortBy = "absoluteDeltaNs", bool descending = true,
        int offset = 0, int limit = 25, CancellationToken cancellationToken = default, string? markerPathPrefix = null, string? kind = null, int? queueIndex = null,
        string? section = null, bool excludeBelowNoise = false, string? minConfidence = null)
    {
        if (!Directions.Contains(direction) || !SortKeys.Contains(sortBy)
            || minDeltaNs < 0 || !double.IsFinite(minDeltaPercent) || minDeltaPercent < 0 || offset < 0 || limit is < 1 or > 1000
            || (minConfidence is not null && !Confidences.Contains(minConfidence)) || queueIndex < 0)
            throw new PixToolException(PixErrors.Codes.InvalidArguments,
                "Use direction all/regressions/improvements/structural, sortBy absoluteDeltaNs/deltaNs/deltaPercent/event, minConfidence low/medium/high, nonnegative thresholds/offset/queueIndex, and limit 1 through 1000.");
        string[]? prefix = string.IsNullOrWhiteSpace(markerPathPrefix) ? null : markerPathPrefix.Trim().TrimEnd('/').Split('/');
        int minRank = minConfidence is null ? -1 : Array.IndexOf(Confidences, minConfidence);
        cancellationToken.ThrowIfCancellationRequested();
        using ResultStore.Lease lease = store.Acquire(fullResultRef);
        using Stream stream = lease.Open();
        var json = new StoredJson(stream, cancellationToken);
        StoredJson.Node items;
        try { items = json.Locate("/items"); }
        catch (PixToolException ex) when (ex.Detail.Code == "invalid_pointer")
        { throw new PixToolException(PixErrors.Codes.InvalidArguments, "fullResultRef must identify the complete result returned by pix_gpu_compare."); }
        if (items.Kind != JsonValueKind.Array) throw new PixToolException(PixErrors.Codes.InvalidArguments, "The comparison's items value must be an array.");
        var comparer = Comparer<Row>.Create((a, b) => Compare(a, b, sortBy, descending));
        var selected = new PriorityQueue<Row, Row>(Comparer<Row>.Create((a, b) => comparer.Compare(b, a)));
        long keep = (long)offset + limit; int total = 0, index = 0;
        foreach (var entry in json.Children(items))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Value.Kind != JsonValueKind.Object) throw new PixToolException(PixErrors.Codes.InvalidArguments, "The result contains a value that is not a comparison row.");
            var properties = json.Children(entry.Value).ToDictionary(p => p.Key, p => p.Value);
            int rowIndex = index;
            JsonElement Property(string name)
                => properties.TryGetValue(name, out var node) ? json.Element(node, ResultStore.TargetBytes, fullResultRef, $"/items/{rowIndex}/{name}") : default;
            EventRef baseline = Reference(Property("baseline")); EventRef candidate = Reference(Property("candidate"));
            JsonElement ns = Property("deltaNs"), percent = Property("deltaPercent");
            decimal? deltaNs = ns.ValueKind == JsonValueKind.Number ? ns.GetDecimal() : null;
            double? deltaPercent = percent.ValueKind == JsonValueKind.Number ? percent.GetDouble() : null;
            var row = new Row(index++, entry.Value, baseline, candidate, deltaNs, deltaPercent);
            if (direction == "regressions" && !(deltaNs > 0) || direction == "improvements" && !(deltaNs < 0)) continue;
            if (direction == "structural" && (deltaNs is not null && deltaNs != 0 || !FieldSections().Any())) continue;
            if (minDeltaNs > 0 && (!deltaNs.HasValue || Math.Abs(deltaNs.Value) < minDeltaNs)) continue;
            if (minDeltaPercent > 0 && (!deltaPercent.HasValue || Math.Abs(deltaPercent.Value) < minDeltaPercent)) continue;
            if (prefix is not null && !PathMatches(Property("markerPath"), prefix)) continue;
            if (kind is not null && Property("kind") is { ValueKind: JsonValueKind.String } rowKind && !string.Equals(rowKind.GetString(), kind, StringComparison.OrdinalIgnoreCase)) continue;
            if (queueIndex.HasValue && baseline.QueueIndex != queueIndex.Value && candidate.QueueIndex != queueIndex.Value) continue;
            if (section is not null && !FieldSections().Any(s => s.Equals(section, StringComparison.OrdinalIgnoreCase))) continue;
            if (excludeBelowNoise && Property("belowNoiseFloor").ValueKind == JsonValueKind.True) continue;
            if (minRank >= 0 && Property("confidence") is { ValueKind: JsonValueKind.String } confidence
                && Array.IndexOf(Confidences, confidence.GetString()) is int rank and >= 0 && rank < minRank) continue;
            total++;
            if (selected.Count < keep) selected.Enqueue(row, row);
            else if (comparer.Compare(row, selected.Peek()) < 0) { selected.Dequeue(); selected.Enqueue(row, row); }

            IEnumerable<string> FieldSections()
            {
                if (!properties.TryGetValue("fields", out StoredJson.Node fields) || fields.Kind != JsonValueKind.Array) yield break;
                int f = 0;
                foreach (var field in json.Children(fields))
                {
                    if (field.Value.Kind == JsonValueKind.Object)
                        foreach (var p in json.Children(field.Value))
                            if (p.Key == "section" && p.Value.Kind == JsonValueKind.String)
                                yield return json.Element(p.Value, 4096, fullResultRef, $"/items/{rowIndex}/fields/{f}/section").GetString() ?? "";
                    f++;
                }
            }
        }
        var result = new List<JsonElement>(); int used = 0;
        cancellationToken.ThrowIfCancellationRequested();
        foreach (Row row in selected.UnorderedItems.Select(e => e.Element).Order(comparer).Skip(offset))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string pointer = "/items/" + row.Index;
            ResultReadDto page = json.ReadNode(row.Node, fullResultRef, pointer, 0, 1000);
            object? value = page.NextOffset.HasValue ? new { deferred = true, pointer, kind = "object", nextCalls = new[] { ResultStore.ReadCall(fullResultRef, pointer) } } : page.Value;
            JsonElement element = JsonSerializer.SerializeToElement(value, Json.Options);
            int bytes = Encoding.UTF8.GetByteCount(element.GetRawText());
            if (result.Count > 0 && used + bytes > ResultStore.TargetBytes - 4096) break;
            result.Add(element); used += bytes;
        }
        int? next = (long)offset + result.Count < total ? offset + result.Count : null;
        cancellationToken.ThrowIfCancellationRequested();
        return new(fullResultRef, total, offset, result.Count, next, result, next.HasValue
            ? [new("pix_gpu_compare_changes", new { fullResultRef, direction, minDeltaNs, minDeltaPercent, sortBy, descending, offset = next.Value, limit,
                markerPathPrefix, kind, queueIndex, section, excludeBelowNoise, minConfidence })] : []);
    }

    /// <summary>The row's markerPath starts with every prefix segment (case-insensitive); rows without a path never match.</summary>
    private static bool PathMatches(JsonElement path, string[] prefix)
    {
        if (path.ValueKind != JsonValueKind.Array || path.GetArrayLength() < prefix.Length) return false;
        int i = 0;
        foreach (JsonElement segment in path.EnumerateArray())
        {
            if (i == prefix.Length) break;
            if (!string.Equals(segment.GetString(), prefix[i], StringComparison.OrdinalIgnoreCase)) return false;
            i++;
        }
        return true;
    }

    private static EventRef Reference(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new PixToolException(PixErrors.Codes.InvalidArguments, "The result lacks comparison event references.");
        try { return value.Deserialize<EventRef>(Json.Options) ?? throw new JsonException(); }
        catch (JsonException) { throw new PixToolException(PixErrors.Codes.InvalidArguments, "The result contains an invalid comparison event reference."); }
    }

    private static int Compare(Row a, Row b, string sortBy, bool descending)
    {
        int key;
        if (sortBy == "event") key = EventOrder(a, b);
        else
        {
            bool hasA = sortBy == "deltaPercent" ? a.DeltaPercent.HasValue : a.DeltaNs.HasValue;
            bool hasB = sortBy == "deltaPercent" ? b.DeltaPercent.HasValue : b.DeltaNs.HasValue;
            if (hasA != hasB) return hasA ? -1 : 1;
            key = !hasA ? 0 : sortBy switch
            {
                "absoluteDeltaNs" => Math.Abs(a.DeltaNs!.Value).CompareTo(Math.Abs(b.DeltaNs!.Value)),
                "deltaNs" => a.DeltaNs!.Value.CompareTo(b.DeltaNs!.Value),
                _ => a.DeltaPercent!.Value.CompareTo(b.DeltaPercent!.Value),
            };
        }
        if (key != 0) return descending ? -key : key;
        key = EventOrder(a, b); return key != 0 ? key : a.Index.CompareTo(b.Index);
    }

    private static int EventOrder(Row a, Row b)
    {
        int key = a.Baseline.QueueIndex.CompareTo(b.Baseline.QueueIndex);
        if (key == 0) key = a.Baseline.EventIndex.CompareTo(b.Baseline.EventIndex);
        if (key == 0) key = a.Candidate.QueueIndex.CompareTo(b.Candidate.QueueIndex);
        if (key == 0) key = a.Candidate.EventIndex.CompareTo(b.Candidate.EventIndex);
        return key;
    }
}
