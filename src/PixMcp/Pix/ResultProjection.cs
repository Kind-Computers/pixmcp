using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PixMcp.Pix;

/// <summary>One filter over an array element: <c>field</c> is a name or relative pointer (<c>eventRef/eventIndex</c>).</summary>
public sealed record WhereClause(
    [property: Description("Field name or relative pointer inside each array element (e.g. eventRef/eventIndex).")] string Field,
    [property: Description("Comparison: eq, ne, gt, ge, lt, le, in, contains, startsWith or exists.")] string Op,
    [property: Description("Value to compare with (an array for in; a boolean for exists; omitted for exists=true).")] JsonElement? Value = null);

/// <summary>What a projected read did: the fields and clauses applied, rows scanned, rows matched, rows too large to evaluate.</summary>
public sealed record ProjectionDto(IReadOnlyList<string>? Fields, IReadOnlyList<WhereClause>? Where, int Scanned, int Matched, int Unevaluated);

/// <summary>Server-side field selection and filtering over the elements of a retained array (pix_result_read fields/where).</summary>
public static class ResultProjection
{
    public const int MaxFields = 32, MaxClauses = 8;
    public static readonly string[] Ops = { "eq", "ne", "gt", "ge", "lt", "le", "in", "contains", "startsWith", "exists" };
    public const string FieldsDescription = "Only these fields of each array element are returned (names or relative pointers such as eventRef/eventIndex; max 32). Applies to array values.";
    public const string WhereDescription = "Filters array elements: [{ field, op, value }] with op eq, ne, gt, ge, lt, le, in (value is an array), contains, startsWith or exists; clauses are ANDed (max 8). total becomes the matched count; every element is evaluated (O(n)).";

    /// <summary>Rejects malformed projections before any result is opened.</summary>
    public static void Validate(IReadOnlyList<string>? fields, IReadOnlyList<WhereClause>? where)
    {
        if (fields is { Count: > MaxFields }) throw PixErrors.InvalidArguments($"fields lists at most {MaxFields} entries.");
        if (fields is not null && fields.Any(string.IsNullOrWhiteSpace)) throw PixErrors.InvalidArguments("fields entries must be non-empty names or relative pointers.");
        if (where is { Count: > MaxClauses }) throw PixErrors.InvalidArguments($"where lists at most {MaxClauses} clauses.");
        foreach (WhereClause clause in where ?? [])
        {
            if (string.IsNullOrWhiteSpace(clause.Field)) throw PixErrors.InvalidArguments("where clauses need a field.");
            if (!Ops.Contains(clause.Op)) throw PixErrors.InvalidArguments($"where op '{clause.Op}' is not one of: {string.Join(", ", Ops)}.");
            if (clause.Op == "in" && clause.Value?.ValueKind != JsonValueKind.Array) throw PixErrors.InvalidArguments("where op 'in' needs an array value.");
            if (clause.Op is not "exists" and not "in" && clause.Value is null) throw PixErrors.InvalidArguments($"where op '{clause.Op}' needs a value.");
        }
    }

    /// <summary>True when every clause holds for <paramref name="element"/>.</summary>
    public static bool Matches(JsonElement element, IReadOnlyList<WhereClause> where)
    {
        foreach (WhereClause clause in where)
        {
            bool present = TryResolve(element, clause.Field, out JsonElement actual) && actual.ValueKind != JsonValueKind.Null;
            bool holds = clause.Op switch
            {
                "exists" => present == (clause.Value is null || clause.Value.Value.ValueKind != JsonValueKind.False),
                "ne" => !present || !Equal(actual, clause.Value!.Value),
                _ when !present => false,
                "eq" => Equal(actual, clause.Value!.Value),
                "gt" => Compare(actual, clause.Value!.Value) is int c && c > 0,
                "ge" => Compare(actual, clause.Value!.Value) is int c && c >= 0,
                "lt" => Compare(actual, clause.Value!.Value) is int c && c < 0,
                "le" => Compare(actual, clause.Value!.Value) is int c && c <= 0,
                "in" => clause.Value!.Value.EnumerateArray().Any(candidate => Equal(actual, candidate)),
                "contains" => actual.ValueKind == JsonValueKind.String && clause.Value!.Value.ValueKind == JsonValueKind.String
                    ? actual.GetString()!.Contains(clause.Value.Value.GetString()!, StringComparison.OrdinalIgnoreCase)
                    : actual.ValueKind == JsonValueKind.Array && actual.EnumerateArray().Any(item => Equal(item, clause.Value!.Value)),
                "startsWith" => actual.ValueKind == JsonValueKind.String && clause.Value!.Value.ValueKind == JsonValueKind.String
                    && actual.GetString()!.StartsWith(clause.Value.Value.GetString()!, StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
            if (!holds) return false;
        }
        return true;
    }

    /// <summary>The requested fields of one element as an object keyed by the field spec; missing fields are omitted.</summary>
    public static JsonObject Select(JsonElement element, IReadOnlyList<string> fields)
    {
        var projected = new JsonObject();
        foreach (string field in fields)
            if (TryResolve(element, field, out JsonElement value)) projected[field] = JsonNode.Parse(value.GetRawText());
        return projected;
    }

    /// <summary>Resolves a name or relative pointer (with or without a leading '/') inside an element.</summary>
    public static bool TryResolve(JsonElement element, string field, out JsonElement value)
    {
        value = element;
        foreach (string token in field.TrimStart('/').Split('/'))
        {
            string key = token.Replace("~1", "/").Replace("~0", "~");
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out JsonElement property)) value = property;
            else if (value.ValueKind == JsonValueKind.Array && StoredJson.ArrayIndex(key, out int index) && index < value.GetArrayLength()) value = value[index];
            else return false;
        }
        return true;
    }

    private static bool Equal(JsonElement a, JsonElement b)
    {
        if (a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number) return a.GetDouble() == b.GetDouble();
        if (a.ValueKind == JsonValueKind.String && b.ValueKind == JsonValueKind.String) return string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal);
        return JsonElement.DeepEquals(a, b);
    }

    private static int? Compare(JsonElement a, JsonElement b)
    {
        if (a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number) return a.GetDouble().CompareTo(b.GetDouble());
        if (a.ValueKind == JsonValueKind.String && b.ValueKind == JsonValueKind.String) return string.CompareOrdinal(a.GetString(), b.GetString());
        if (a.ValueKind == JsonValueKind.String && b.ValueKind == JsonValueKind.Number && double.TryParse(a.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            return parsed.CompareTo(b.GetDouble()); // integers above 2^53 are retained as decimal strings
        return null;
    }
}
