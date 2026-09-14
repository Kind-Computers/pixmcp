using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PixMcp.Pix.StaticProfiling;

internal sealed record StaticTargetMatch(ShaderTargetDto? Target, IReadOnlyList<ShaderTargetDto> Candidates, string? Problem);

/// <summary>
/// Target naming and resolution for static shader profiling. A target resolves by id, exact adapter name, or a vendor, family,
/// architecture or name fragment that selects exactly one family (whose lowest-id adapter is used).
/// </summary>
internal static class StaticTargets
{
    public static readonly string[] Vendors = ["amd", "intel"];
    public const string StabilityNote = "Target ids come from PIX and can change between PIX releases; pass an architecture, family or adapter name to stay stable.";
    private static readonly Regex AmdArchitecture = new(@"\((RDNA[0-9.]+)\)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private const string ExampleHlsl =
        "RWStructuredBuffer<float> Output : register(u0);\n[RootSignature(\"UAV(u0)\")]\n[numthreads(8, 8, 1)]\n" +
        "void main(uint3 id : SV_DispatchThreadID)\n{\n    float sum = 0;\n    for (int i = 0; i < 4; i++) sum += sin(id.x * i);\n    Output[id.x] = sum;\n}\n";

    /// <summary>Intel Xe architecture from the codename, AMD RDNA generation from the family, or null.</summary>
    public static string? Architecture(string name, string family)
    {
        string text = name + " " + family;
        if (text.Contains("Battlemage", StringComparison.OrdinalIgnoreCase)) return "Xe2-HPG";
        if (text.Contains("Lunar Lake", StringComparison.OrdinalIgnoreCase)) return "Xe2-LPG";
        if (text.Contains("Panther Lake", StringComparison.OrdinalIgnoreCase)) return "Xe3-LPG";
        Match match = AmdArchitecture.Match(family);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>True for hashes a vendor reports as a placeholder (empty, or one leading byte followed by zeros).</summary>
    public static bool IsPlaceholderHash(byte[]? bytes) => bytes is null || bytes.Length == 0 || bytes.Skip(1).All(b => b == 0);

    public static string? DocumentationLink(string? link)
        => link is not null && (link.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || link.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) ? link : null;

    public static ShaderTargetDto[] Filter(IEnumerable<ShaderTargetDto> targets, string? vendor, string? nameContains)
        => targets.Where(t => vendor is null || t.Vendor.Equals(vendor.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(t => string.IsNullOrWhiteSpace(nameContains) || Mentions(t, nameContains.Trim()))
            .ToArray();

    private static bool Mentions(ShaderTargetDto target, string text)
        => target.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || target.Family.Contains(text, StringComparison.OrdinalIgnoreCase)
            || (target.Architecture?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false);

    public static StaticTargetMatch Resolve(IReadOnlyList<ShaderTargetDto> targets, string spec)
    {
        string text = (spec ?? "").Trim();
        IReadOnlyList<ShaderTargetDto> families = Representatives(targets);
        if (text.Length == 0) return new(null, families, "target is required.");
        if (uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out uint id))
            return targets.FirstOrDefault(t => t.Id == id) is { } byId ? new(byId, [], null) : new(null, families, $"No shader target has id {id}.");
        ShaderTargetDto[] exact = targets.Where(t => t.Name.Equals(text, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (exact.Length > 0)
        {
            IReadOnlyList<ShaderTargetDto> exactFamilies = Representatives(exact);
            return exactFamilies.Count == 1
                ? new(exact.MinBy(t => t.Id), [], null)
                : new(null, exactFamilies, $"'{text}' names adapters in {exactFamilies.Count} families; pass an id, family or architecture.");
        }
        ShaderTargetDto[] matching = targets.Where(t => t.Vendor.Equals(text, StringComparison.OrdinalIgnoreCase) || Mentions(t, text)).ToArray();
        if (matching.Length == 0) return new(null, families, $"No shader target matches '{text}'; static profiling targets AMD and Intel only.");
        IReadOnlyList<ShaderTargetDto> groups = Representatives(matching);
        return groups.Count == 1 ? new(groups[0], [], null) : new(null, groups, $"'{text}' matches {groups.Count} target families; name one.");
    }

    /// <summary>The lowest-id adapter of each (vendor, family), in id order.</summary>
    public static IReadOnlyList<ShaderTargetDto> Representatives(IEnumerable<ShaderTargetDto> targets)
        => targets.GroupBy(t => (t.Vendor, t.Family)).Select(g => g.MinBy(t => t.Id)!).OrderBy(t => t.Id).ToArray();

    /// <summary>The shortest stable spelling that resolves to the representative's family.</summary>
    public static string ExampleTarget(ShaderTargetDto representative, IReadOnlyList<ShaderTargetDto> all)
    {
        string? familyToken = representative.Family.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        foreach (string? candidate in new[] { representative.Architecture, familyToken, representative.Family, representative.Name })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (Resolve(all, candidate).Target is { } resolved && resolved.Vendor == representative.Vendor && resolved.Family == representative.Family) return candidate;
        }
        return representative.TargetRef;
    }

    public static ShaderTargetsExtraDto Extra(IReadOnlyList<ShaderTargetDto> all, string? installDir, string? pixVersion,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? knownIssues = null)
    {
        IReadOnlyList<ShaderTargetDto> representatives = Representatives(all);
        ShaderTargetFamilyDto[] families = representatives
            .Select(r => new ShaderTargetFamilyDto(r.Vendor, r.Family, r.Architecture, all.Count(t => t.Vendor == r.Vendor && t.Family == r.Family), ExampleTarget(r, all)))
            .ToArray();
        ToolCallDto[] examples = Vendors.Select(vendor => families.FirstOrDefault(f => f.Vendor == vendor)).OfType<ShaderTargetFamilyDto>()
            .Select(f => new ToolCallDto("pix_gpu_shader_static_profile",
                new { target = f.ExampleTarget, sources = new[] { new { target = "cs_6_0", hlsl = ExampleHlsl } }, waitSeconds = 60 }, CostHints.Job))
            .ToArray();
        return new(all.Select(t => t.Vendor).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), families, OfflineCompilers(installDir), pixVersion, StabilityNote,
            knownIssues ?? new Dictionary<string, IReadOnlyList<string>>(), examples);
    }

    /// <summary>The vendor compiler DLLs PIX ships under OfflineShaderCompilers, with file versions.</summary>
    public static IReadOnlyList<OfflineCompilerDto> OfflineCompilers(string? installDir)
    {
        if (installDir is null) return [];
        string root = Path.Combine(installDir, "OfflineShaderCompilers");
        if (!Directory.Exists(root)) return [];
        try
        {
            return Directory.EnumerateFiles(root, "*.dll", SearchOption.AllDirectories)
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(file => new OfflineCompilerDto(Path.GetFileName(Path.GetDirectoryName(file)!).ToLowerInvariant(), Path.GetFileName(file), FileVersionInfo.GetVersionInfo(file).FileVersion))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}
