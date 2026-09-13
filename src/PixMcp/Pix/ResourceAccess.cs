using System.Globalization;
using System.Text.RegularExpressions;

namespace PixMcp.Pix;

/// <summary>One resource transition, UAV or aliasing barrier parsed from captured call text.</summary>
internal sealed record ParsedBarrier(string Type, ulong ResourceId, string? StateBefore, string? StateAfter, uint? Subresource);

/// <summary>full when every referenced resource was attributed to a barrier, partial when some were, none otherwise.</summary>
internal sealed record BarrierParse(string State, IReadOnlyList<ParsedBarrier> Barriers);

/// <summary>Read/write classification of resource uses and tolerant parsing of captured barrier arguments. Pure; never throws.</summary>
internal static class ResourceAccess
{
    public static readonly string[] Classes = ["read", "write", "readWrite", "copySrc", "copyDst", "barrier", "unknown"];
    public static readonly string[] UsedAs = ["any", "read", "write", "readWrite", "copySrc", "copyDst", "barrier", "renderTarget", "depthStencil", "uav"];
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    /// <summary>Access implied by a bound view type: render target, depth stencil and stream output write; unordered access reads and writes.</summary>
    public static string FromViewType(string? viewType) => viewType switch
    {
        "RENDER_TARGET_VIEW" or "DEPTH_STENCIL_VIEW" or "STREAM_OUTPUT_VIEW" => "write",
        "UNORDERED_ACCESS_VIEW" => "readWrite",
        "SHADER_RESOURCE_VIEW" or "CONSTANT_BUFFER_VIEW" or "VERTEX_BUFFER_VIEW" or "INDEX_BUFFER_VIEW" => "read",
        _ => "unknown",
    };

    /// <summary>
    /// Access implied by a captured API object argument: barrier calls transition, pDst*/pDest* elements receive copies,
    /// pSrc*/pSource* elements feed them, and ExecuteIndirect buffers are read.
    /// </summary>
    public static string FromArgument(string path, string apiName)
    {
        if (apiName.StartsWith("ResourceBarrier", StringComparison.Ordinal) || apiName == "Barrier") return "barrier";
        string[] segments = path.Split('/');
        if (segments.Any(s => s.StartsWith("pDst", StringComparison.OrdinalIgnoreCase) || s.StartsWith("pDest", StringComparison.OrdinalIgnoreCase))) return "copyDst";
        if (segments.Any(s => s.StartsWith("pSrc", StringComparison.OrdinalIgnoreCase) || s.StartsWith("pSource", StringComparison.OrdinalIgnoreCase))) return "copySrc";
        if (apiName.StartsWith("ExecuteIndirect", StringComparison.Ordinal) || apiName.StartsWith("Present", StringComparison.Ordinal)) return "read";
        if (apiName.Contains("UnorderedAccessView", StringComparison.Ordinal))
            return apiName.StartsWith("Clear", StringComparison.Ordinal) ? "write" : "readWrite";
        if (apiName.Contains("ShaderResourceView", StringComparison.Ordinal) || apiName.Contains("ConstantBufferView", StringComparison.Ordinal)
            || apiName.StartsWith("IASet", StringComparison.Ordinal)) return "read";
        if (apiName.StartsWith("OMSetRenderTargets", StringComparison.Ordinal) || apiName.StartsWith("ClearRenderTargetView", StringComparison.Ordinal)
            || apiName.StartsWith("ClearDepthStencilView", StringComparison.Ordinal)) return "write";
        return "unknown";
    }

    public static bool Reads(string access) => access is "read" or "readWrite" or "copySrc";

    public static bool Writes(string access) => access is "write" or "readWrite" or "copyDst";

    private static readonly Regex ObjectElement = new("<(?<name>[A-Za-z_][A-Za-z0-9_]*)>obj#(?<id>[0-9]+)</\\k<name>>", RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex AnyTag = new("<(?<close>/?)(?<name>[A-Za-z_][A-Za-z0-9_]*)>", RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex LeafElement = new("<(?<name>[A-Za-z_][A-Za-z0-9_]*)>(?<value>[^<]*)</\\k<name>>", RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex Transition = new("<Transition>(?<body>.*?)</Transition>", RegexOptions.CultureInvariant | RegexOptions.Singleline, Timeout);
    private static readonly Regex Uav = new("<UAV>(?<body>.*?)</UAV>", RegexOptions.CultureInvariant | RegexOptions.Singleline, Timeout);
    private static readonly Regex Aliasing = new("<Aliasing>(?<body>.*?)</Aliasing>", RegexOptions.CultureInvariant | RegexOptions.Singleline, Timeout);

    /// <summary>Every element whose whole text is obj#N, with the path of enclosing elements below the call's root element.</summary>
    public static IReadOnlyList<(string Path, ulong ObjectId)> ArgumentPaths(string? apiCallData)
    {
        var result = new List<(string, ulong)>();
        if (string.IsNullOrEmpty(apiCallData) || !apiCallData.Contains("obj#", StringComparison.Ordinal)) return result;
        try
        {
            MatchCollection tags = AnyTag.Matches(apiCallData);
            var stack = new List<string>();
            int t = 0;
            foreach (Match match in ObjectElement.Matches(apiCallData))
            {
                for (; t < tags.Count && tags[t].Index < match.Index; t++)
                {
                    string name = tags[t].Groups["name"].Value;
                    if (tags[t].Groups["close"].Value.Length == 0) stack.Add(name);
                    else if (stack.LastIndexOf(name) is int at and >= 0) stack.RemoveRange(at, stack.Count - at);
                }
                if (ulong.TryParse(match.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id))
                    result.Add((string.Join("/", stack.Skip(1).Append(match.Groups["name"].Value)), id));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return [];
        }
        return result;
    }

    /// <summary>
    /// Transition (resource, subresource, states), UAV and aliasing barriers from element-form call text. Enhanced barriers
    /// and positional text are not parsed; their resources stay unattributed.
    /// </summary>
    public static BarrierParse ParseBarriers(string? apiCallData)
    {
        if (string.IsNullOrEmpty(apiCallData) || !apiCallData.StartsWith('<')) return new("none", []);
        var barriers = new List<ParsedBarrier>();
        try
        {
            foreach (Match match in Transition.Matches(apiCallData))
            {
                Dictionary<string, string> leaves = Leaves(match.Groups["body"].Value);
                if (ObjectId(leaves, "pResource") is ulong id)
                    barriers.Add(new("TRANSITION", id, State(leaves, "StateBefore"), State(leaves, "StateAfter"),
                        leaves.TryGetValue("Subresource", out string? sub) && uint.TryParse(sub, NumberStyles.None, CultureInfo.InvariantCulture, out uint s) ? s : null));
            }
            foreach (Match match in Uav.Matches(apiCallData))
                if (ObjectId(Leaves(match.Groups["body"].Value), "pResource") is ulong id) barriers.Add(new("UAV", id, null, null, null));
            foreach (Match match in Aliasing.Matches(apiCallData))
            {
                Dictionary<string, string> leaves = Leaves(match.Groups["body"].Value);
                if (ObjectId(leaves, "pOutputResource") is ulong output) barriers.Add(new("ALIASING", output, null, null, null));
                if (ObjectId(leaves, "pInputResource") is ulong input) barriers.Add(new("ALIASING", input, null, null, null));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return new("none", []);
        }
        int referenced = ArgumentPaths(apiCallData).Select(a => a.ObjectId).Distinct().Count();
        int attributed = barriers.Select(b => b.ResourceId).Distinct().Count();
        return new(barriers.Count == 0 ? "none" : attributed >= referenced ? "full" : "partial", barriers);
    }

    private static Dictionary<string, string> Leaves(string body)
    {
        var leaves = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match leaf in LeafElement.Matches(body)) leaves.TryAdd(leaf.Groups["name"].Value, leaf.Groups["value"].Value);
        return leaves;
    }

    private static ulong? ObjectId(Dictionary<string, string> leaves, string name)
        => leaves.TryGetValue(name, out string? value) && value.StartsWith("obj#", StringComparison.Ordinal)
            && ulong.TryParse(value.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) ? id : null;

    private static string? State(Dictionary<string, string> leaves, string name)
        => leaves.TryGetValue(name, out string? value) ? value.Replace("D3D12_RESOURCE_STATE_", "", StringComparison.Ordinal) : null;
}
