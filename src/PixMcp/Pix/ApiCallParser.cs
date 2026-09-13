using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PixMcp.Pix;

/// <summary>One argument of a captured API call.</summary>
public sealed record ApiArgumentDto(string Name,
    [property: Description("0-based argument position; -1 for the element form's <this> object.")] int Position,
    [property: Description("Argument text as captured (entities decoded; nested elements kept as XML).")] string Text,
    long? Int64 = null, double? Double = null,
    [property: Description("API object id as 0x-prefixed hex when the argument is an obj#N reference.")] string? ApiObjectId = null,
    [property: Description("Interface named before obj#N (for example ID3D12GraphicsCommandList).")] string? ObjectKind = null);

/// <summary>A captured API call parsed from PIX apiCallData: its arguments and, for work calls, the work items it launches.</summary>
public sealed record ApiCallDto(string Api,
    [property: Description("element (XML form), positional (Name(a, b)) or unparsed.")] string Form,
    IReadOnlyList<ApiArgumentDto> Arguments,
    [property: Description("vertices, indices, threadGroups or maxCommands; null for other calls.")] string? WorkItemKind = null,
    [property: Description("Vertices or indices times instances, thread groups X*Y*Z, or ExecuteIndirect's MaxCommandCount.")] long? WorkItems = null,
    long? InstanceCount = null,
    [property: Description("Interface of the element form's <this> object.")] string? Interface = null,
    string? Note = null);

/// <summary>
/// Parses PIX apiCallData in its XML element form (<c>&lt;DrawInstanced&gt;&lt;VertexCountPerInstance&gt;3&lt;/...&gt;</c>, as PIX
/// 2606.18 records it) or a positional form (<c>DrawInstanced(3, 1, 0, 0)</c>, names from a D3D12 table). Pure and total: it
/// never throws, and text in neither form comes back as <c>unparsed</c>.
/// </summary>
public static class ApiCallParser
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);
    private static readonly Regex Positional = new(@"^\s*(?<api>[A-Za-z_][A-Za-z0-9_:]*)\s*\((?<args>.*)\)\s*$", RegexOptions.CultureInvariant | RegexOptions.Singleline, Timeout);
    private static readonly Regex ObjectReference = new(@"^(?:(?<kind>[A-Za-z_][A-Za-z0-9_]*)\s+)?obj#(?<id>[0-9]+)$", RegexOptions.CultureInvariant, Timeout);

    /// <summary>D3D12 argument names for calls whose captured text is positional.</summary>
    private static readonly Dictionary<string, string[]> PositionalNames = new(StringComparer.Ordinal)
    {
        ["DrawInstanced"] = ["VertexCountPerInstance", "InstanceCount", "StartVertexLocation", "StartInstanceLocation"],
        ["DrawIndexedInstanced"] = ["IndexCountPerInstance", "InstanceCount", "StartIndexLocation", "BaseVertexLocation", "StartInstanceLocation"],
        ["Dispatch"] = ["ThreadGroupCountX", "ThreadGroupCountY", "ThreadGroupCountZ"],
        ["DispatchMesh"] = ["ThreadGroupCountX", "ThreadGroupCountY", "ThreadGroupCountZ"],
        ["ExecuteIndirect"] = ["pCommandSignature", "MaxCommandCount", "pArgumentBuffer", "ArgumentBufferOffset", "pCountBuffer", "CountBufferOffset"],
    };

    /// <summary>The parsed call; null when there is no call text.</summary>
    public static ApiCallDto? Parse(string? apiCallData, string? eventName = null)
    {
        if (string.IsNullOrWhiteSpace(apiCallData)) return null;
        string text = apiCallData.Trim();
        try
        {
            if (text.StartsWith('<')) return FromElement(text) ?? Unparsed(eventName);
            Match positional = Positional.Match(text);
            return positional.Success ? FromPositional(positional.Groups["api"].Value, positional.Groups["args"].Value) : Unparsed(eventName);
        }
        catch (Exception)
        {
            return Unparsed(eventName);
        }
    }

    private static ApiCallDto? FromElement(string text)
    {
        XElement root;
        try { root = XElement.Parse(text); }
        catch (Exception) { return null; }
        var arguments = new List<ApiArgumentDto>();
        string? interfaceName = null;
        int position = 0;
        foreach (XElement child in root.Elements())
        {
            string name = child.Name.LocalName;
            string value = child.HasElements ? string.Concat(child.Nodes().Select(n => n.ToString(SaveOptions.DisableFormatting))) : child.Value;
            ApiArgumentDto argument = Argument(name, name == "this" ? -1 : position++, value);
            if (name == "this") interfaceName = argument.ObjectKind;
            arguments.Add(argument);
        }
        return WithWork(new ApiCallDto(root.Name.LocalName, "element", arguments, Interface: interfaceName));
    }

    private static ApiCallDto FromPositional(string api, string args)
    {
        int scope = api.LastIndexOf("::", StringComparison.Ordinal);
        string name = scope >= 0 ? api[(scope + 2)..] : api;
        PositionalNames.TryGetValue(name, out string[]? names);
        var arguments = SplitTopLevel(args).Select((part, i) =>
            Argument(names is not null && i < names.Length ? names[i] : "arg" + i.ToString(CultureInfo.InvariantCulture), i, part)).ToList();
        return WithWork(new ApiCallDto(name, "positional", arguments));
    }

    private static List<string> SplitTopLevel(string args)
    {
        var parts = new List<string>();
        if (string.IsNullOrWhiteSpace(args)) return parts;
        int depth = 0, start = 0;
        for (int i = 0; i < args.Length; i++)
        {
            char c = args[i];
            if (c is '(' or '{' or '[') depth++;
            else if (c is ')' or '}' or ']') depth = Math.Max(0, depth - 1);
            else if (c == ',' && depth == 0)
            {
                parts.Add(args[start..i].Trim());
                start = i + 1;
            }
        }
        parts.Add(args[start..].Trim());
        return parts;
    }

    private static ApiArgumentDto Argument(string name, int position, string text)
    {
        string decoded = WebUtility.HtmlDecode(text).Trim();
        Match reference = ObjectReference.Match(decoded);
        if (reference.Success && ulong.TryParse(reference.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id))
            return new(name, position, decoded, ApiObjectId: Interop.Hex(id), ObjectKind: reference.Groups["kind"].Success ? reference.Groups["kind"].Value : null);
        long? integer = long.TryParse(decoded, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long parsed) ? parsed
            : decoded.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(decoded.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long hex) ? hex
            : null;
        double? number = integer is null && double.TryParse(decoded, NumberStyles.Float, CultureInfo.InvariantCulture, out double real) && double.IsFinite(real) ? real : null;
        return new(name, position, decoded, integer, number);
    }

    private static ApiCallDto WithWork(ApiCallDto call)
    {
        long? Value(string name) => call.Arguments.FirstOrDefault(a => a.Name == name)?.Int64;
        long? instances = Value("InstanceCount");
        return call.Api switch
        {
            "DrawInstanced" when Value("VertexCountPerInstance") is long vertices
                => call with { WorkItemKind = "vertices", WorkItems = Multiply(vertices, instances ?? 1), InstanceCount = instances },
            "DrawIndexedInstanced" when Value("IndexCountPerInstance") is long indices
                => call with { WorkItemKind = "indices", WorkItems = Multiply(indices, instances ?? 1), InstanceCount = instances },
            "Dispatch" or "DispatchMesh" when Value("ThreadGroupCountX") is long x && Value("ThreadGroupCountY") is long y && Value("ThreadGroupCountZ") is long z
                => call with { WorkItemKind = "threadGroups", WorkItems = Multiply(Multiply(x, y), z) },
            "ExecuteIndirect" when Value("MaxCommandCount") is long commands => call with { WorkItemKind = "maxCommands", WorkItems = commands },
            _ => call,
        };
    }

    private static long Multiply(long a, long b)
    {
        try { return checked(a * b); }
        catch (OverflowException) { return long.MaxValue; }
    }

    private static ApiCallDto Unparsed(string? eventName)
        => new(eventName ?? "", "unparsed", [], Note: "The call text is neither the XML element form nor Name(arguments).");
}
