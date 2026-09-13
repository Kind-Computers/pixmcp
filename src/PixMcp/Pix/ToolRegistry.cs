using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace PixMcp.Pix;

/// <summary>
/// The tools this server registers and their parameter names, reflected once. Cross-feature nextCalls pass through
/// <see cref="Accepts"/> so a suggestion never names a tool or argument that does not exist.
/// </summary>
internal static class ToolRegistry
{
    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlySet<string>>> Registered = new(() =>
        typeof(ToolRegistry).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Select(m => (Attribute: m.GetCustomAttribute<McpServerToolAttribute>(), Method: m))
            .Where(x => x.Attribute?.Name is not null)
            .ToDictionary(x => x.Attribute!.Name!, x => (IReadOnlySet<string>)x.Method.GetParameters().Where(p => p.Name is not null).Select(p => p.Name!).ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal));

    private static readonly Lazy<IReadOnlyList<string>> Names = new(() => Registered.Value.Keys.Order(StringComparer.Ordinal).ToArray());

    /// <summary>Every registered tool name, sorted, whatever PIXMCP_TOOLSETS enables.</summary>
    public static IReadOnlyList<string> AllNames => Names.Value;

    /// <summary>True when the tool is registered, enabled by PIXMCP_TOOLSETS and takes every named parameter.</summary>
    public static bool Has(string tool, params string[] parameters)
        => Registered.Value.TryGetValue(tool, out IReadOnlySet<string>? names) && Toolsets.Enabled(tool) && parameters.All(names.Contains);

    /// <summary>True when the call names a registered tool and every non-null argument is one of its parameters.</summary>
    public static bool Accepts(ToolCallDto call)
    {
        if (!Registered.Value.TryGetValue(call.Tool, out IReadOnlySet<string>? names) || !Toolsets.Enabled(call.Tool)) return false;
        JsonElement arguments = JsonSerializer.SerializeToElement(call.Arguments, Json.Options);
        return arguments.ValueKind != JsonValueKind.Object
            || arguments.EnumerateObject().All(p => p.Value.ValueKind == JsonValueKind.Null || names.Contains(p.Name));
    }
}
