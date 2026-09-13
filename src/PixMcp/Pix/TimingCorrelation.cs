using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PixMcp.Pix.Handles;

namespace PixMcp.Pix;

/// <summary>A timed GPU marker path of a replayed capture: the first event carrying it, how often it occurs and its summed inclusive EOP time.</summary>
internal sealed record GpuMarkerPathSnapshot(IReadOnlyList<string> Segments, int QueueIndex, uint EventIndex, int Occurrences, ulong InclusiveEopNs, string Semantics);

internal sealed record GpuQueueSnapshot(int Index, string Name, string Type);

/// <summary>What pix_correlate needs from a GPU capture, copied off the PIX worker so the recorded side runs as an ordinary timing query.</summary>
internal sealed record GpuCorrelationSnapshot(string Handle, IReadOnlyList<GpuMarkerPathSnapshot> Paths, IReadOnlyList<GpuQueueSnapshot> Queues, int Markers, bool Truncated,
    ScopeDescriptionDto? Scope, ReplayProvenance Provenance)
{
    /// <summary>A content hash that keys the timing query job, so a changed GPU side never reuses an old correlation.</summary>
    public string Stamp() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Json.Serialize(this))))[..16];
}

/// <summary>Normalized keys of one marker path: the whole path and its leaf, with the normalizations each needed.</summary>
internal sealed record CorrelationKey(string FullKey, string LeafKey, IReadOnlyList<string> FullNormalizations, IReadOnlyList<string> LeafNormalizations);

internal sealed record CorrelationMatch(int GpuIndex, int RecordedIndex, string Method, IReadOnlyList<string> Normalizations);

/// <summary>
/// Name matching between replayed GPU marker paths and recorded PIX marker paths. Names are compared after trimming,
/// collapsing whitespace, ignoring case, removing the legacy PIX BeginEvent prefix and dropping a trailing number; the
/// full path is tried first, then a leaf name that is unique on both sides. A match is a name coincidence, not identity.
/// </summary>
internal static class TimingCorrelation
{
    public const string LegacyPixPrefix = "<deprecated - use pix3.h instead>";
    public const string Identity = "name-match, not identity; recorded occurrences are averaged";

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
    private static readonly Regex TrailingNumber = new(@"^(.*\D)[\s#:_-]*\d+$", RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromMilliseconds(200));

    public static (string Key, IReadOnlyList<string> Normalizations) NormalizeSegment(string name)
    {
        var applied = new List<string>(2);
        string value = name.Trim();
        if (value.StartsWith(LegacyPixPrefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value[LegacyPixPrefix.Length..].Trim();
            applied.Add("legacyPixPrefix");
        }
        value = Whitespace.Replace(value, " ");
        Match number = TrailingNumber.Match(value);
        if (number.Success)
        {
            string stem = number.Groups[1].Value.TrimEnd(' ', '#', ':', '_', '-');
            if (stem.Length > 0)
            {
                value = stem;
                applied.Add("trailingNumber");
            }
        }
        return (value.ToLowerInvariant(), applied);
    }

    public static CorrelationKey Key(IReadOnlyList<string> segments)
    {
        var keys = new List<string>(segments.Count);
        var applied = new SortedSet<string>(StringComparer.Ordinal);
        IReadOnlyList<string> leaf = [];
        foreach (string segment in segments)
        {
            (string key, IReadOnlyList<string> normalizations) = NormalizeSegment(segment);
            keys.Add(key);
            applied.UnionWith(normalizations);
            leaf = normalizations;
        }
        return new(string.Join("/", keys), keys.Count == 0 ? "" : keys[^1], applied.ToArray(), leaf);
    }

    public static (List<CorrelationMatch> Matches, List<int> UnmatchedGpu, List<int> UnmatchedRecorded) Match(IReadOnlyList<CorrelationKey> gpu, IReadOnlyList<CorrelationKey> recorded)
    {
        var byFullKey = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int r = 0; r < recorded.Count; r++) byFullKey.TryAdd(recorded[r].FullKey, r);
        Dictionary<string, int> recordedLeaves = recorded.GroupBy(k => k.LeafKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        Dictionary<string, int> gpuLeaves = gpu.GroupBy(k => k.LeafKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var matches = new List<CorrelationMatch>();
        var unmatchedGpu = new List<int>();
        var used = new HashSet<int>();
        for (int g = 0; g < gpu.Count; g++)
        {
            CorrelationKey key = gpu[g];
            if (key.LeafKey.Length > 0 && byFullKey.TryGetValue(key.FullKey, out int path))
                matches.Add(new(g, path, "pathMatch", key.FullNormalizations.Union(recorded[path].FullNormalizations).Order(StringComparer.Ordinal).ToArray()));
            else if (key.LeafKey.Length > 0 && gpuLeaves[key.LeafKey] == 1 && recordedLeaves.GetValueOrDefault(key.LeafKey) == 1)
            {
                int leaf = Enumerable.Range(0, recorded.Count).First(r => recorded[r].LeafKey == key.LeafKey);
                matches.Add(new(g, leaf, "nameMatch", key.LeafNormalizations.Union(recorded[leaf].LeafNormalizations).Order(StringComparer.Ordinal).ToArray()));
            }
            else
            {
                unmatchedGpu.Add(g);
                continue;
            }
            used.Add(matches[^1].RecordedIndex);
        }
        return (matches, unmatchedGpu, Enumerable.Range(0, recorded.Count).Where(r => !used.Contains(r)).ToList());
    }

    /// <summary>PIX GPU capture types (GRAPHICS, COMPUTE, COPY) and recorded ApiCommandQueue types (Direct, Compute, Copy) on one vocabulary.</summary>
    public static string NormalizeQueueType(string? type) => (type ?? "").Trim().ToLowerInvariant() switch
    {
        "graphics" or "direct" or "3d" => "graphics",
        var other => other,
    };

    /// <summary>Pairs each GPU capture queue with an unused recorded queue of the same type, preferring an equal name.</summary>
    public static List<CorrelationQueueMapDto> MapQueues(IReadOnlyList<(int Index, string Name, string Type)> gpu, IReadOnlyList<(string Id, string? Name, string? Type)> recorded)
    {
        var used = new HashSet<int>();
        var rows = new List<CorrelationQueueMapDto>();
        foreach (var queue in gpu)
        {
            string type = NormalizeQueueType(queue.Type);
            int pick = -1;
            string method = "none";
            for (int r = 0; r < recorded.Count && pick < 0; r++)
                if (!used.Contains(r) && NormalizeQueueType(recorded[r].Type) == type && string.Equals(recorded[r].Name?.Trim(), queue.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                    (pick, method) = (r, "typeAndName");
            for (int r = 0; r < recorded.Count && pick < 0; r++)
                if (!used.Contains(r) && NormalizeQueueType(recorded[r].Type) == type)
                    (pick, method) = (r, "type");
            if (pick >= 0) used.Add(pick);
            rows.Add(new CorrelationQueueMapDto(queue.Index, queue.Name, queue.Type, pick < 0 ? null : recorded[pick].Id, pick < 0 ? null : recorded[pick].Name,
                pick < 0 ? null : recorded[pick].Type, method, method switch { "typeAndName" => "medium", "type" => "low", _ => "none" }));
        }
        return rows;
    }
}
