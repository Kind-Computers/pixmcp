using System.Text.Json;

namespace PixMcp.Pix;

/// <summary>One documented limitation or behaviour, scoped to a feature, vendor and PIX version range.</summary>
public sealed record CompatibilityNote(string Id, string Feature, string Vendor, string? PixMin, string? PixMax, string Severity,
    string CapabilityState, string Text, string Source, string? VerifiedOn);

/// <summary>
/// Machine-readable known limitations (embedded compatibility-notes.json). Tools attach the notes for their feature and
/// the capture's vendor; capability snapshots take their default state from here when nothing probed the feature.
/// </summary>
public static class CompatibilityNotes
{
    private static readonly Lazy<CompatibilityNote[]> Notes = new(Load);

    public static IReadOnlyList<CompatibilityNote> All => Notes.Value;

    private static CompatibilityNote[] Load()
    {
        using Stream stream = typeof(CompatibilityNotes).Assembly.GetManifestResourceStream("PixMcp.Resources.compatibility-notes.json")
            ?? throw new InvalidOperationException("compatibility-notes.json is not embedded.");
        using JsonDocument document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("notes").EnumerateArray().Select(n => new CompatibilityNote(
            n.GetProperty("id").GetString()!, n.GetProperty("feature").GetString()!, n.GetProperty("vendor").GetString()!,
            Text(n, "pixMin"), Text(n, "pixMax"), n.GetProperty("severity").GetString()!, n.GetProperty("capabilityState").GetString()!,
            n.GetProperty("text").GetString()!, n.GetProperty("source").GetString()!, Text(n, "verifiedOn"))).ToArray();
    }

    private static string? Text(JsonElement node, string name) => node.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Notes for a feature that apply to <paramref name="vendor"/> (or any vendor) on <paramref name="pixVersion"/> (YYMM.DD; null = no version filter).</summary>
    public static IReadOnlyList<CompatibilityNote> For(string feature, GpuVendor vendor, string? pixVersion = null)
    {
        string vendorName = GpuVendors.Name(vendor);
        return Notes.Value.Where(n => n.Feature.Equals(feature, StringComparison.OrdinalIgnoreCase)
            && (n.Vendor == "any" || n.Vendor.Equals(vendorName, StringComparison.OrdinalIgnoreCase))
            && InRange(n, pixVersion)).ToArray();
    }

    public static IReadOnlyList<string> Texts(string feature, GpuVendor vendor, string? pixVersion = null)
        => For(feature, vendor, pixVersion).Select(n => n.Text).ToArray();

    /// <summary>The capability state the registry knows for a feature and vendor when nothing probed it: the most specific (vendor over any) note's state, else null.</summary>
    public static string? CapabilityState(string feature, GpuVendor vendor, string? pixVersion = null)
    {
        IReadOnlyList<CompatibilityNote> notes = For(feature, vendor, pixVersion);
        CompatibilityNote? note = notes.FirstOrDefault(n => n.Vendor != "any") ?? notes.FirstOrDefault();
        return note?.CapabilityState is "unknown" or null ? null : note.CapabilityState;
    }

    private static bool InRange(CompatibilityNote note, string? pixVersion)
    {
        if (pixVersion is null) return true;
        int[]? version = PixDiscovery.ParseVersion(pixVersion);
        if (version is null) return true;
        if (note.PixMin is not null && PixDiscovery.ParseVersion(note.PixMin) is int[] min && PixDiscovery.CompareVersions(version, min) < 0) return false;
        if (note.PixMax is not null && PixDiscovery.ParseVersion(note.PixMax) is int[] max && PixDiscovery.CompareVersions(version, max) > 0) return false;
        return true;
    }
}
