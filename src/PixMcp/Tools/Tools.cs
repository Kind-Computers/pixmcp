using ModelContextProtocol;
using PixMcp.Pix;
using PixMcp.Pix.Handles;

namespace PixMcp.Tools;

/// <summary>Shared plumbing for tool implementations.</summary>
internal static class Tools
{
    /// <summary>Runs <paramref name="work"/> on the PIX worker thread and serializes the result; PIX errors become McpExceptions.</summary>
    public static Task<string> Run(PixSession session, string context, Func<object?> work)
        => PixErrors.Guard(context, async () => Json.Serialize(await session.Run(work).ConfigureAwait(false)));

    public static string RequireFile(string path, string what)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new McpException($"A {what} path is required.");
        }
        string full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw new McpException($"{what} not found: {full}");
        }
        return full;
    }

    public static bool Contains(string haystack, string? needle)
        => string.IsNullOrEmpty(needle) || haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string[]> KindPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["draw"] = new[] { "Draw" },
        ["dispatch"] = new[] { "Dispatch" },
        ["drawOrDispatch"] = new[] { "Draw", "Dispatch" },
        ["executeIndirect"] = new[] { "ExecuteIndirect" },
        ["copy"] = new[] { "Copy" },
        ["clear"] = new[] { "Clear" },
        ["barrier"] = new[] { "ResourceBarrier", "Barrier" },
        ["present"] = new[] { "Present" },
        ["marker"] = new[] { "PIXBeginEvent", "BeginEvent", "PIXSetMarker", "SetMarker" },
    };

    public const string KindDescription = "Optional event kind filter: draw, dispatch, drawOrDispatch, executeIndirect, copy, clear, barrier, present, marker (matched against the event name prefix).";

    public static bool MatchesKind(EventRecord e, string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return true;
        }
        if (!KindPrefixes.TryGetValue(kind.Trim(), out string[]? prefixes))
        {
            throw new McpException($"Unknown kind '{kind}'. Valid kinds: {string.Join(", ", KindPrefixes.Keys)}.");
        }
        foreach (string prefix in prefixes)
        {
            if (e.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || e.ApiCallData.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public static IEnumerable<EventRecord> FilterEvents(
        IEnumerable<EventRecord> events,
        string? nameContains,
        string? nameStartsWith,
        string? apiCallContains,
        string? kind,
        uint? parentIndex,
        uint? gpuIdMin,
        uint? gpuIdMax)
    {
        foreach (EventRecord e in events)
        {
            if (!Contains(e.Name, nameContains)) continue;
            if (!string.IsNullOrEmpty(nameStartsWith) && !e.Name.StartsWith(nameStartsWith, StringComparison.OrdinalIgnoreCase)) continue;
            if (!Contains(e.ApiCallData, apiCallContains)) continue;
            if (!MatchesKind(e, kind)) continue;
            if (parentIndex.HasValue && e.ParentIndex != parentIndex.Value) continue;
            if (gpuIdMin.HasValue && (e.GpuId == uint.MaxValue || e.GpuId < gpuIdMin.Value)) continue;
            if (gpuIdMax.HasValue && (e.GpuId == uint.MaxValue || e.GpuId > gpuIdMax.Value)) continue;
            yield return e;
        }
    }

    public static bool HasEventFilter(string? nameContains, string? nameStartsWith, string? apiCallContains, string? kind, uint? parentIndex, uint? gpuIdMin, uint? gpuIdMax)
        => !string.IsNullOrEmpty(nameContains) || !string.IsNullOrEmpty(nameStartsWith) || !string.IsNullOrEmpty(apiCallContains)
           || !string.IsNullOrEmpty(kind) || parentIndex.HasValue || gpuIdMin.HasValue || gpuIdMax.HasValue;

    /// <summary>Parses "0x1234" or "1234".</summary>
    public static ulong ParseId(string value, string what)
    {
        string v = value.Trim();
        if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && ulong.TryParse(v[2..], System.Globalization.NumberStyles.HexNumber, null, out ulong hex))
        {
            return hex;
        }
        if (ulong.TryParse(v, out ulong dec))
        {
            return dec;
        }
        throw new McpException($"Invalid {what} '{value}'; expected a decimal or 0x-prefixed hex number.");
    }

    /// <summary>Matches an enum member by full name or by suffix (case-insensitive), e.g. "ENABLE_DEBUG_LAYER".</summary>
    public static T ParseEnum<T>(string value) where T : struct, Enum
    {
        foreach (T member in Enum.GetValues<T>())
        {
            string name = member.ToString();
            if (name.Equals(value, StringComparison.OrdinalIgnoreCase) || name.EndsWith("_" + value, StringComparison.OrdinalIgnoreCase)
                || Json.EnumName(member).Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                return member;
            }
        }
        throw new McpException($"Unknown {typeof(T).Name} value '{value}'. Valid values: {string.Join(", ", Enum.GetValues<T>().Select(m => Json.EnumName(m)))}.");
    }
}
