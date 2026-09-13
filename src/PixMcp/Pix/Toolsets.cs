namespace PixMcp.Pix;

/// <summary>The toolsets PIXMCP_TOOLSETS enables, as reported by pix_info.toolsets.</summary>
public sealed record ToolsetsInfoDto(IReadOnlyList<string> Enabled, IReadOnlyList<string> Disabled, int HiddenTools, string Source, string Variable);

/// <summary>
/// Named groups of tools that PIXMCP_TOOLSETS can enable. A disabled tool is dropped from tools/list, a call to it fails with
/// tool_disabled, prompts that need it are hidden, and <see cref="ToolRegistry"/> never suggests it in nextCalls. The session
/// toolset (info, handles, jobs, results, log) is always on.
/// </summary>
internal static class Toolsets
{
    public const string Session = "session", Gpu = "gpu", Timing = "timing", Dump = "dump", Device = "device", Csv = "csv",
        GpuSql = "gpusql", DrPix = "drpix", Shader = "shader";

    /// <summary>Every toolset name, sorted.</summary>
    public static readonly IReadOnlyList<string> Names = [Csv, Device, DrPix, Dump, Gpu, GpuSql, Session, Shader, Timing];

    private static readonly HashSet<string> SessionTools = new(StringComparer.Ordinal)
    {
        "pix_info", "pix_handles", "pix_close", "pix_close_all", "pix_jobs", "pix_job_status", "pix_job_wait", "pix_job_cancel",
        "pix_log", "pix_result_read", "pix_result_export",
    };

    /// <summary>The toolset a tool belongs to, or null for a name no rule covers (a test pins that every registered tool maps).</summary>
    public static string? For(string tool)
    {
        if (SessionTools.Contains(tool)) return Session;
        if (tool.StartsWith("pix_gpu_sql", StringComparison.Ordinal)) return GpuSql;
        if (tool.StartsWith("pix_gpu_drpix_", StringComparison.Ordinal)) return DrPix;
        if (tool is "pix_gpu_shader_profile" or "pix_gpu_shader_static_profile" || tool.StartsWith("pix_shader_", StringComparison.Ordinal)) return Shader;
        if (tool.StartsWith("pix_gpu_", StringComparison.Ordinal) || tool.StartsWith("pix_capture_", StringComparison.Ordinal)) return Gpu;
        if (tool.StartsWith("pix_timing_", StringComparison.Ordinal) || tool == "pix_correlate") return Timing;
        if (tool.StartsWith("pix_dump_", StringComparison.Ordinal)) return Dump;
        if (tool.StartsWith("pix_device_", StringComparison.Ordinal)) return Device;
        if (tool.StartsWith("pix_csv_", StringComparison.Ordinal)) return Csv;
        return null;
    }

    /// <summary>True unless the tool's toolset is disabled; unknown names are left to the SDK. pix_correlate also needs gpu.</summary>
    public static bool Enabled(string tool, ServerOptions? options = null)
    {
        IReadOnlySet<string>? enabled = (options ?? ServerOptions.Current).Toolsets;
        if (enabled is null) return true;
        string? set = For(tool);
        if (set is null or Session) return true;
        if (!enabled.Contains(set)) return false;
        return tool != "pix_correlate" || enabled.Contains(Gpu);
    }

    /// <summary>
    /// Parses PIXMCP_TOOLSETS: names separated by commas or spaces, case-insensitive. Empty or "all" enables everything (null);
    /// otherwise the set always contains session. Unknown names produce a <paramref name="problem"/> and null.
    /// </summary>
    public static IReadOnlySet<string>? Parse(string text, out string? problem)
    {
        problem = null;
        string[] names = text.Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => n.ToLowerInvariant()).ToArray();
        if (names.Length == 0 || names.Contains("all")) return null;
        string[] unknown = names.Where(n => !Names.Contains(n)).Distinct(StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0)
        {
            problem = $"{ServerOptions.ToolsetsVariable} names unknown toolset(s) {string.Join(", ", unknown.Select(u => $"'{u}'"))}; valid names: {string.Join(", ", Names)}, or all.";
            return null;
        }
        return names.Append(Session).ToHashSet(StringComparer.Ordinal);
    }

    public static ToolsetsInfoDto Describe(ServerOptions? options = null)
    {
        ServerOptions current = options ?? ServerOptions.Current;
        string[] enabled = Names.Where(n => current.Toolsets is null || current.Toolsets.Contains(n)).ToArray();
        return new(enabled, Names.Except(enabled).ToArray(), ToolRegistry.AllNames.Count(t => !Enabled(t, current)),
            current.ToolsetsSource, ServerOptions.ToolsetsVariable);
    }
}
