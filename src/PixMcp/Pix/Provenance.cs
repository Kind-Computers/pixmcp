using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace PixMcp.Pix;

/// <summary>Replaces a provenance block the same handle already returned; the earlier response still holds the full block.</summary>
public sealed record ProvenanceStubDto(string ProvenanceRef, bool Unchanged);

/// <summary>
/// Provenance blocks (replay adapter, flags, timing range) repeat verbatim on every response of a handle. The first
/// response per handle carries the full block plus a fingerprint; later ones carry a stub until the block changes
/// or the caller passes includeProvenance=true.
/// </summary>
public static class ProvenanceDedup
{
    public static readonly string[] Keys = { "provenance", "baselineProvenance", "candidateProvenance" };
    private const int MaxDepth = 4;

    public static string Fingerprint(JsonNode block)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(block.ToJsonString()))).Substring(0, 8).ToLowerInvariant();

    /// <summary>
    /// Walks <paramref name="root"/>; for each provenance-named object property resolves its owner handle through
    /// <paramref name="ownerFor"/>, compares with <paramref name="last"/> and either stubs the block or records the new
    /// fingerprint through <paramref name="record"/>. Returns true when the document changed.
    /// </summary>
    public static bool Apply(JsonNode? root, bool includeProvenance, Func<string, string?> ownerFor,
        Func<string, string, string?> last, Action<string, string, string> record)
    {
        bool changed = false;
        Visit(root, 0, ref changed, includeProvenance, ownerFor, last, record);
        return changed;
    }

    private static void Visit(JsonNode? node, int depth, ref bool changed, bool include, Func<string, string?> ownerFor,
        Func<string, string, string?> last, Action<string, string, string> record)
    {
        if (depth > MaxDepth) return;
        switch (node)
        {
            case JsonObject obj:
                foreach (KeyValuePair<string, JsonNode?> property in obj.ToArray())
                {
                    if (Keys.Contains(property.Key) && property.Value is JsonObject block && block["provenanceRef"] is null)
                    {
                        string? owner = ownerFor(property.Key);
                        if (owner is null) continue;
                        string fingerprint = Fingerprint(block);
                        string? previous = last(owner, property.Key);
                        if (!include && previous == fingerprint)
                        {
                            obj[property.Key] = new JsonObject { ["provenanceRef"] = owner + "#" + fingerprint, ["unchanged"] = true };
                            changed = true;
                            continue;
                        }
                        block["fingerprint"] = fingerprint;
                        block["changed"] = previous is not null && previous != fingerprint;
                        record(owner, property.Key, fingerprint);
                        changed = true;
                    }
                    else Visit(property.Value, depth + 1, ref changed, include, ownerFor, last, record);
                }
                break;
            case JsonArray array:
                foreach (JsonNode? child in array) Visit(child, depth + 1, ref changed, include, ownerFor, last, record);
                break;
        }
    }
}
