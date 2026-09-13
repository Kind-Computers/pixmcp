using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using PixMcp.Pix.Handles;

namespace PixMcp.Pix;

public sealed record PresetMatch(uint Id, string Name, string MatchedBy);
/// <summary>How a preset resolved against a capture's counter list: matches in pattern order, patterns nothing matched, and the cap.</summary>
public sealed record PresetResolution(string Preset, string Vendor, string Confidence, IReadOnlyList<PresetMatch> Matches,
    IReadOnlyList<string> UnmatchedPatterns, bool Capped, IReadOnlyList<uint> OmittedIds, string Description)
{
    public uint[] Ids => Matches.Select(m => m.Id).ToArray();
}

/// <summary>
/// Named counter groups per vendor, loaded from the embedded counter-presets.json. Patterns are case-insensitive globs
/// over counter names; a vendor block carries the confidence of its vocabulary (verified on hardware, or transcribed).
/// </summary>
public static class CounterPresets
{
    public const int DefaultCap = 16;
    private sealed record VendorBlock(string Vendor, string Confidence, string[] Patterns);
    private sealed record Preset(string Name, string Description, VendorBlock[] Vendors);
    private static readonly Lazy<Preset[]> Presets = new(Load);

    public static IReadOnlyList<string> Names => Presets.Value.Select(p => p.Name).ToArray();
    public static string Description(string preset) => Find(preset)?.Description ?? "";
    /// <summary>Attribute-safe description; CounterPresetsTests keeps it in sync with the embedded preset names.</summary>
    public const string NamesDescription = "Named counter group (utilization, aluUtilization, perStageAlu, occupancy, stalls, cache, memoryBandwidth, fixedFunction, pipelineStatistics, depthOcclusion); resolved for the capture's vendor.";

    private static Preset[] Load()
    {
        using Stream stream = typeof(CounterPresets).Assembly.GetManifestResourceStream("PixMcp.Resources.counter-presets.json")
            ?? throw new InvalidOperationException("counter-presets.json is not embedded.");
        using JsonDocument document = JsonDocument.Parse(stream);
        var presets = new List<Preset>();
        foreach (JsonProperty preset in document.RootElement.GetProperty("presets").EnumerateObject())
        {
            var vendors = new List<VendorBlock>();
            foreach (JsonProperty vendor in preset.Value.GetProperty("vendors").EnumerateObject())
                vendors.Add(new(vendor.Name, vendor.Value.GetProperty("confidence").GetString()!,
                    vendor.Value.GetProperty("patterns").EnumerateArray().Select(p => p.GetString()!).ToArray()));
            presets.Add(new(preset.Name, preset.Value.GetProperty("description").GetString() ?? "", vendors.ToArray()));
        }
        return presets.ToArray();
    }

    private static Preset? Find(string preset) => Presets.Value.FirstOrDefault(p => p.Name.Equals(preset?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolves a preset for <paramref name="vendor"/>: matches are listed in pattern order, capped at <paramref name="cap"/>
    /// ids. An unknown vendor tries every block as an unverified union. Returns null for an unknown preset name.
    /// </summary>
    public static PresetResolution? Resolve(GpuVendor vendor, string preset, IReadOnlyList<CounterInfo> counters, int cap = DefaultCap)
    {
        Preset? definition = Find(preset);
        if (definition is null) return null;
        string vendorName = GpuVendors.Name(vendor);
        VendorBlock[] blocks = vendor == GpuVendor.Unknown
            ? definition.Vendors
            : definition.Vendors.Where(b => b.Vendor.Equals(vendorName, StringComparison.OrdinalIgnoreCase) || b.Vendor == "any").ToArray();
        string confidence = vendor == GpuVendor.Unknown ? "unverified" : blocks.Length == 0 ? "none" : blocks.Min(b => Rank(b.Confidence)) switch { 0 => "verified", 1 => "transcribed", _ => "unverified" };
        var matches = new List<PresetMatch>(); var unmatched = new List<string>(); var seen = new HashSet<uint>();
        foreach (VendorBlock block in blocks)
            foreach (string pattern in block.Patterns)
            {
                var regex = new Regex("^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                bool any = false;
                foreach (CounterInfo counter in counters)
                    if (regex.IsMatch(counter.Name) && seen.Add(counter.Id)) { matches.Add(new(counter.Id, counter.Name, pattern)); any = true; }
                if (!any) unmatched.Add(pattern);
            }
        bool capped = matches.Count > cap;
        var omitted = capped ? matches.Skip(cap).Select(m => m.Id).ToArray() : [];
        return new(definition.Name, vendorName, confidence, capped ? matches.Take(cap).ToArray() : matches.ToArray(), unmatched, capped, omitted, definition.Description);
    }

    private static int Rank(string confidence) => confidence switch { "verified" => 0, "transcribed" => 1, _ => 2 };
}
