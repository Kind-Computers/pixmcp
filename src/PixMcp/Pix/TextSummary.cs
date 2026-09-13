using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace PixMcp.Pix;

/// <summary>
/// The short text block sent instead of the full JSON when PIXMCP_TEXT_CONTENT=summary: the tool, the result's top-level keys,
/// paging totals, pending and deferred markers and the nextCalls tool names, at most <see cref="MaxBytes"/> UTF-8 bytes.
/// structuredContent keeps the full result; errors keep their full text.
/// </summary>
internal static class TextSummary
{
    public const int MaxBytes = 512;
    private const string Tail = "Full result in structuredContent.";
    private const int MaxScalarLength = 64;

    /// <summary>Replaces every text block of a successful result with the summary of its structuredContent.</summary>
    public static void Apply(CallToolResult result, string tool)
    {
        if (result.IsError == true || result.StructuredContent is not JsonElement value) return;
        string text = Build(tool, value);
        result.Content = result.Content.Select(c => c is TextContentBlock ? new TextContentBlock { Text = text } : c).ToList();
    }

    public static string Build(string tool, JsonElement value)
    {
        var facts = new List<string>();
        string[] keys = [];
        if (value.ValueKind == JsonValueKind.Object)
        {
            keys = value.EnumerateObject().Select(p => p.Name).ToArray();
            if (Flag(value, "deferred"))
                facts.Add($"deferred: read resultRef {Scalar(value, "resultRef")} ({Scalar(value, "bytes") ?? "?"} bytes) with pix_result_read");
            else if (Scalar(value, "resultRef") is string resultRef)
                facts.Add("resultRef " + resultRef);
            if (Flag(value, "pending"))
                facts.Add($"pending: pix_job_wait jobId {Scalar(value, "jobId")}, then repeat the call");
            string[] pendingSections = value.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.Object && Flag(p.Value, "pending")).Select(p => p.Name).ToArray();
            if (pendingSections.Length > 0) facts.Add("sections pending: " + string.Join(", ", pendingSections));
            if (Scalar(value, "status") is string status) facts.Add("status " + status);
            string paging = string.Join(", ", new[] { "total", "offset", "count", "nextOffset", "rowCount", "hasMore" }
                .Select(n => Scalar(value, n) is string s ? $"{n} {s}" : null).OfType<string>());
            if (paging.Length > 0) facts.Add(paging);
            if (value.TryGetProperty("nextCalls", out JsonElement calls) && calls.ValueKind == JsonValueKind.Array)
            {
                string[] tools = calls.EnumerateArray()
                    .Select(c => c.ValueKind == JsonValueKind.Object && c.TryGetProperty("tool", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null)
                    .OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
                if (tools.Length > 0) facts.Add("nextCalls: " + string.Join(", ", tools.Take(6)) + (tools.Length > 6 ? $" (+{tools.Length - 6})" : ""));
            }
        }
        string head = $"{Clip(tool)}: {(value.ValueKind == JsonValueKind.Object ? "object" : value.ValueKind.ToString().ToLowerInvariant())} result";

        string Compose(int shownKeys, IReadOnlyList<string> shownFacts)
        {
            var text = new StringBuilder(head);
            foreach (string fact in shownFacts) text.Append("; ").Append(fact);
            if (keys.Length > 0)
            {
                text.Append("; keys: ").Append(string.Join(", ", keys.Take(shownKeys)));
                if (shownKeys < keys.Length) text.Append(shownKeys == 0 ? "" : " ").Append($"(+{keys.Length - shownKeys} more)");
            }
            return text.Append(". ").Append(Tail).ToString();
        }

        for (int shown = Math.Min(keys.Length, 16); shown >= 0; shown--)
        {
            string text = Compose(shown, facts);
            if (Encoding.UTF8.GetByteCount(text) <= MaxBytes) return text;
        }
        for (int kept = facts.Count - 1; kept >= 0; kept--)
        {
            string text = Compose(0, facts.Take(kept).ToArray());
            if (Encoding.UTF8.GetByteCount(text) <= MaxBytes) return text;
        }
        return Tail;
    }

    private static bool Flag(JsonElement value, string name) => value.TryGetProperty(name, out JsonElement flag) && flag.ValueKind == JsonValueKind.True;

    private static string? Scalar(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out JsonElement scalar)) return null;
        return scalar.ValueKind switch
        {
            JsonValueKind.String => Clip(scalar.GetString()!),
            JsonValueKind.Number => scalar.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static string Clip(string text) => text.Length <= MaxScalarLength ? text : text[..(MaxScalarLength - 1)] + "…";
}
