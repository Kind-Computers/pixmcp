using System.Text.Json;

namespace PixMcp.Pix.Sql;

internal sealed record PixStorageColumnDoc(string? Unit, string? Description);

internal sealed record PixStorageTableDoc(string Name, string? Family, string Purpose, string? BackingTable,
    IReadOnlyDictionary<string, PixStorageColumnDoc> Columns, IReadOnlyList<string> Joins, IReadOnlyList<string> Caveats);

/// <summary>
/// Documentation overlay for the PixStorage schema (embedded Resources/pixstorage-docs.json): purpose, units, joins and
/// caveats per table, capture-fact meanings and custom functions. Everything is phrased as observed on the verified
/// PIX build; enum-like integer codes are recorded as observed, never decoded.
/// </summary>
internal static class PixStorageDocs
{
    private sealed record Catalog(string ObservedOn, IReadOnlyDictionary<string, PixStorageTableDoc> Tables,
        IReadOnlyDictionary<long, string> Facts, IReadOnlyDictionary<string, string> Functions, IReadOnlyList<string> Notes);

    private static readonly Lazy<Catalog> Docs = new(Load);

    public static string ObservedOn => Docs.Value.ObservedOn;
    public static IReadOnlyCollection<PixStorageTableDoc> Tables => Docs.Value.Tables.Values.ToArray();
    public static IReadOnlyList<string> Notes => Docs.Value.Notes;

    public static PixStorageTableDoc? For(string table) => Docs.Value.Tables.GetValueOrDefault(table);
    public static string? Fact(long id) => Docs.Value.Facts.GetValueOrDefault(id);
    public static string? Function(string name, int arguments) => Docs.Value.Functions.GetValueOrDefault(name.ToLowerInvariant() + "/" + arguments);

    private static Catalog Load()
    {
        using Stream stream = typeof(PixStorageDocs).Assembly.GetManifestResourceStream("PixMcp.Resources.pixstorage-docs.json")
            ?? throw new InvalidOperationException("pixstorage-docs.json is not embedded.");
        using JsonDocument document = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        JsonElement root = document.RootElement;
        var tables = new Dictionary<string, PixStorageTableDoc>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty table in root.GetProperty("tables").EnumerateObject())
        {
            JsonElement value = table.Value;
            var columns = new Dictionary<string, PixStorageColumnDoc>(StringComparer.OrdinalIgnoreCase);
            if (value.TryGetProperty("columns", out JsonElement columnDocs))
                foreach (JsonProperty column in columnDocs.EnumerateObject())
                    columns[column.Name] = column.Value.ValueKind == JsonValueKind.String
                        ? new PixStorageColumnDoc(null, column.Value.GetString())
                        : new PixStorageColumnDoc(Text(column.Value, "unit"), Text(column.Value, "description"));
            tables[table.Name] = new PixStorageTableDoc(table.Name, Text(value, "family"), Text(value, "purpose") ?? "", Text(value, "backingTable"),
                columns, Strings(value, "joins"), Strings(value, "caveats"));
        }
        var facts = root.GetProperty("facts").EnumerateObject().ToDictionary(p => long.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture), p => p.Value.GetString()!);
        var functions = root.GetProperty("functions").EnumerateObject().ToDictionary(p => p.Name.ToLowerInvariant(), p => p.Value.GetString()!, StringComparer.Ordinal);
        return new Catalog(Text(root, "observedOn") ?? "", tables, facts, functions, Strings(root, "notes"));
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static IReadOnlyList<string> Strings(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(v => v.GetString()!).ToArray() : [];
}
