using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using Xunit;

namespace PixMcp.Tests;

/// <summary>Every tool parameter and every property of a parameter record carries a description the schema can show.</summary>
public sealed class ToolDocumentationTests
{
    private static readonly Type[] Infrastructure = [typeof(PixSession), typeof(JobManager), typeof(CancellationToken)];

    internal static IEnumerable<MethodInfo> ToolMethods() => typeof(PixMcp.Tools.Tools).Assembly.GetTypes()
        .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
        .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
        .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null);

    private static IEnumerable<Type> ParameterRecordTypes()
    {
        var seen = new HashSet<Type>();
        var pending = new Queue<Type>(ToolMethods().SelectMany(m => m.GetParameters()).Select(p => Unwrap(p.ParameterType)));
        while (pending.Count > 0)
        {
            Type type = pending.Dequeue();
            if (!IsRecordLike(type) || !seen.Add(type)) continue;
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)) pending.Enqueue(Unwrap(property.PropertyType));
        }
        return seen;
    }

    private static Type Unwrap(Type type)
    {
        if (type.IsArray) return Unwrap(type.GetElementType()!);
        Type? nullable = Nullable.GetUnderlyingType(type);
        if (nullable is not null) return Unwrap(nullable);
        if (type.IsGenericType && typeof(System.Collections.IEnumerable).IsAssignableFrom(type)) return Unwrap(type.GetGenericArguments()[0]);
        return type;
    }

    private static bool IsRecordLike(Type type)
        => type.Namespace?.StartsWith("PixMcp", StringComparison.Ordinal) == true && !type.IsEnum && !type.IsPrimitive && type != typeof(string)
           && !Infrastructure.Contains(type) && !typeof(PixMcp.Pix.Handles.PixHandle).IsAssignableFrom(type);

    private static bool Described(ICustomAttributeProvider provider)
        => provider.GetCustomAttributes(typeof(DescriptionAttribute), false).OfType<DescriptionAttribute>().Any(d => !string.IsNullOrWhiteSpace(d.Description));

    [Fact]
    public void EveryToolParameterIsDescribed()
    {
        var missing = new List<string>();
        foreach (MethodInfo method in ToolMethods())
        {
            string tool = method.GetCustomAttribute<McpServerToolAttribute>()!.Name!;
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                if (Infrastructure.Contains(parameter.ParameterType) || parameter.ParameterType.Name.StartsWith("IProgress", StringComparison.Ordinal)) continue;
                if (!Described(parameter)) missing.Add($"{tool}.{parameter.Name}");
            }
        }
        Assert.Empty(missing);
    }

    [Fact]
    public void EveryPropertyOfAParameterRecordIsDescribed()
    {
        var missing = new List<string>();
        foreach (Type type in ParameterRecordTypes())
        {
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.Name == "EqualityContract") continue;
                // Positional record parameters carry [property: Description(...)]; plain properties carry it directly.
                if (!Described(property)) missing.Add($"{type.Name}.{property.Name}");
            }
        }
        Assert.Empty(missing);
    }

    [Fact]
    public void DescriptionsOfNonTrivialDefaultsMentionTheDefault()
    {
        var missing = new List<string>();
        foreach (MethodInfo method in ToolMethods())
        {
            string tool = method.GetCustomAttribute<McpServerToolAttribute>()!.Name!;
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                if (!parameter.HasDefaultValue || parameter.DefaultValue is null or false or 0 or 0.0 or "" || Infrastructure.Contains(parameter.ParameterType)) continue;
                if (parameter.DefaultValue is bool or int or double or string or Enum && Described(parameter)
                    && !parameter.GetCustomAttribute<DescriptionAttribute>()!.Description.Contains("default", StringComparison.OrdinalIgnoreCase))
                    missing.Add($"{tool}.{parameter.Name} = {parameter.DefaultValue}");
            }
        }
        Assert.Empty(missing);
    }

    [Fact]
    public void DumpOffendersForTheMaintainer()
    {
        string? target = Environment.GetEnvironmentVariable("PIXMCP_DUMP_OFFENDERS");
        if (target is null) return;
        var lines = new List<string>();
        foreach (MethodInfo method in ToolMethods())
        {
            string tool = method.GetCustomAttribute<McpServerToolAttribute>()!.Name!;
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                if (Infrastructure.Contains(parameter.ParameterType)) continue;
                if (!Described(parameter)) lines.Add($"PARAM {method.DeclaringType!.Name} {tool} {parameter.Name} {parameter.ParameterType.Name} default={parameter.DefaultValue}");
                else if (parameter.HasDefaultValue && parameter.DefaultValue is not (null or false or 0 or 0.0 or "") && parameter.DefaultValue is bool or int or double or string or Enum
                    && !parameter.GetCustomAttribute<DescriptionAttribute>()!.Description.Contains("default", StringComparison.OrdinalIgnoreCase))
                    lines.Add($"DEFAULT {method.DeclaringType!.Name} {tool} {parameter.Name} = {parameter.DefaultValue}");
            }
        }
        foreach (Type type in ParameterRecordTypes())
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (property.Name != "EqualityContract" && !Described(property)) lines.Add($"PROP {type.FullName} {property.Name} {property.PropertyType.Name}");
        File.WriteAllLines(target, lines);
    }
}
