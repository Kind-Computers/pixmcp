using System.Reflection;
using Xunit;

namespace PixMcp.Tests;

/// <summary>Maintainer aid: dumps the managed PIX signatures the server binds against when PIXMCP_DUMP_PIX_API names a file.</summary>
public sealed class ReflectionDumpTests
{
    [Fact]
    public void DumpPixApiSignaturesForTheMaintainer()
    {
        string? target = Environment.GetEnvironmentVariable("PIXMCP_DUMP_PIX_API");
        if (target is null) return;
        Assembly pix = typeof(Microsoft.PIX.IPixGpuCaptureTiming).Assembly;
        var lines = new List<string>();
        foreach (string name in new[] { "Microsoft.PIX.IPixGpuCaptureTiming", "Microsoft.PIX.IPixGpuCaptureCounterData", "Microsoft.PIX.IPixGpuCaptureQueueInfo",
            "Microsoft.PIX.Extension.PixApiExtensionsGpuCaptureTiming", "Microsoft.PIX.Extension.PixApiExtensionsGpuCaptureCounters", "Microsoft.PIX.PIX_EVENT_TIMING" })
        {
            Type? type = pix.GetType(name) ?? pix.GetTypes().FirstOrDefault(t => t.FullName!.EndsWith(name.Split('.')[^1], StringComparison.Ordinal));
            lines.Add("== " + (type?.FullName ?? name + " (not found)"));
            if (type is null) continue;
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                lines.Add("  " + method.ReturnType.Name + " " + method.Name + "(" + string.Join(", ", method.GetParameters().Select(p => (p.IsOut ? "out " : "") + p.ParameterType.Name + " " + p.Name)) + ")");
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                lines.Add("  field " + field.FieldType.Name + " " + field.Name);
        }
        File.WriteAllLines(target, lines);
    }

    /// <summary>Maintainer aid: lists every public type of the managed PIX assembly with its declared members when PIXMCP_DUMP_PIX_API_TYPES names a file.</summary>
    [Fact]
    public void DumpEveryPixApiTypeForTheMaintainer()
    {
        string? target = Environment.GetEnvironmentVariable("PIXMCP_DUMP_PIX_API_TYPES");
        if (target is null) return;
        Assembly pix = typeof(Microsoft.PIX.IPixGpuCaptureTiming).Assembly;
        var lines = new List<string> { "assembly " + pix.GetName().Name + " " + System.Diagnostics.FileVersionInfo.GetVersionInfo(pix.Location).FileVersion };
        foreach (Type type in pix.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            lines.Add("== " + type.FullName + (type.IsInterface ? " (interface)" : type.IsEnum ? " (enum)" : type.IsValueType ? " (struct)" : " (class)"));
            if (type.IsEnum) continue;
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).OrderBy(m => m.Name, StringComparer.Ordinal))
                lines.Add("  " + method.ReturnType.Name + " " + method.Name + "(" + string.Join(", ", method.GetParameters().Select(p => (p.IsOut ? "out " : "") + p.ParameterType.Name + " " + p.Name)) + ")");
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                lines.Add("  field " + field.FieldType.Name + " " + field.Name);
        }
        File.WriteAllLines(target, lines);
    }
}
