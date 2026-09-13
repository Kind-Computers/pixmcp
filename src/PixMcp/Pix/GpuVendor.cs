using System.Text.Json.Serialization;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PixMcp.Pix;

public enum GpuVendor { Unknown, Nvidia, Amd, Intel, Qualcomm, Warp }

/// <summary>
/// Which GPU vendor a piece of data came from and how that was decided. <c>source</c> is captureFileInfo (the capture's
/// GPU vendor id or device name), captureQueueAdapter (queue adapter names), replayAdapter (the analysis adapter),
/// shaderProfilingAdapter (a Shader Explorer target) or none. <c>assumedDefault</c> marks "PIX picks the adapter and we
/// assume it is the first one".
/// </summary>
public sealed record VendorIdentity([property: JsonIgnore] GpuVendor Vendor, string? AdapterName, string Source, bool AssumedDefault = false, string? Note = null)
{
    public static readonly VendorIdentity None = new(GpuVendor.Unknown, null, "none");
    /// <summary>The vendor as the wire name (lowercase: nvidia, amd, intel, qualcomm, warp, unknown), the same spelling queue rows use.</summary>
    [JsonPropertyName("vendor"), JsonPropertyOrder(-1)]
    public string VendorName => GpuVendors.Name(Vendor);
}

/// <summary>Vendor detection from the strings PIX exposes. Ordered rules; WARP is tested first so "Microsoft Basic Render Driver" never reads as a GPU vendor.</summary>
public static class GpuVendors
{
    private static readonly (GpuVendor Vendor, Regex Pattern)[] NameRules =
    [
        (GpuVendor.Warp, new(@"\bWARP\b|Basic Render Driver|Microsoft Basic", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        (GpuVendor.Nvidia, new(@"\bNVIDIA\b|\bGeForce\b|\bQuadro\b|\bTesla\b|\bRTX\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        (GpuVendor.Amd, new(@"\bAMD\b|\bRadeon\b|\bATI\b|\bFirePro\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        (GpuVendor.Intel, new(@"\bIntel\b|\bArc\b|\bIris\b|\bUHD Graphics\b|\bXe\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        (GpuVendor.Qualcomm, new(@"\bQualcomm\b|\bAdreno\b|\bSnapdragon\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
    ];

    /// <summary>Vendor from an adapter or device name; Unknown for null, empty or unrecognised names.</summary>
    public static GpuVendor FromAdapterName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return GpuVendor.Unknown;
        foreach ((GpuVendor vendor, Regex pattern) in NameRules)
            if (pattern.IsMatch(name)) return vendor;
        return GpuVendor.Unknown;
    }

    /// <summary>Vendor from a Shader Explorer vendor string ("NVIDIA", "AMD", "Intel").</summary>
    public static GpuVendor FromVendorName(string? name) => FromAdapterName(name);

    /// <summary>Vendor from a PCI vendor id ("10DE", "0x1002", 32902).</summary>
    public static GpuVendor FromVendorId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return GpuVendor.Unknown;
        string text = id.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        if (!uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value)) return GpuVendor.Unknown;
        return value switch
        {
            0x10DE => GpuVendor.Nvidia,
            0x1002 or 0x1022 => GpuVendor.Amd,
            0x8086 => GpuVendor.Intel,
            0x5143 or 0x4D4F4351 => GpuVendor.Qualcomm,
            0x1414 => GpuVendor.Warp,
            _ => GpuVendor.Unknown,
        };
    }

    /// <summary>The vendor of a set of adapter names: one vendor when they agree, Unknown with a note when they disagree.</summary>
    public static VendorIdentity FromAdapterNames(IEnumerable<string?> names, string source)
    {
        string[] distinct = names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length == 0) return VendorIdentity.None;
        GpuVendor[] vendors = distinct.Select(FromAdapterName).Where(v => v != GpuVendor.Unknown).Distinct().ToArray();
        if (vendors.Length == 1) return new(vendors[0], distinct.Length == 1 ? distinct[0] : string.Join(" | ", distinct), source);
        if (vendors.Length == 0) return new(GpuVendor.Unknown, distinct[0], source, Note: "No known vendor name in " + string.Join(", ", distinct));
        return new(GpuVendor.Unknown, string.Join(" | ", distinct), source, Note: "Adapter names disagree on the vendor: " + string.Join(", ", distinct));
    }

    public static string Name(GpuVendor vendor) => vendor.ToString().ToLowerInvariant();
}
