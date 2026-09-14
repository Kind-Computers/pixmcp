using Microsoft.PIX;

namespace PixMcp.Pix;

/// <summary>One decoded analysis flag with the meaning documented in PixApiGpuCaptureTypes.h.</summary>
public sealed record AnalysisFlagDto(string Name, string Meaning);

/// <summary>Replay flags as selected for analysis: the raw bit set, the short names, their meanings and any bits this build does not know.</summary>
public sealed record AnalysisFlagsDto(uint Raw, IReadOnlyList<string> Names, IReadOnlyList<AnalysisFlagDto> Flags, uint UnknownBits);

/// <summary>
/// The PIX_ANALYSIS_FLAGS vocabulary. The enum mixes the PIX_ANALYSIS_FLAG_ and PIX_ANALYSIS_ prefixes, so the wire uses the short names
/// (prefix stripped) and accepts either full spelling.
/// </summary>
internal static class AnalysisFlags
{
    private sealed record Entry(PIX_ANALYSIS_FLAGS Flag, string Name, string Meaning);

    private static readonly Entry[] Table =
    [
        new(PIX_ANALYSIS_FLAGS.PIX_ANALYSIS_FLAG_NONE, "NONE", "No replay flags; combined with other flags it is ignored."),
        new(PIX_ANALYSIS_FLAGS.PIX_ANALYSIS_FLAG_IGNORE_INCOMPATIBILITIES, "IGNORE_INCOMPATIBILITIES",
            "Ignore hardware incompatibilities between the capture device and the analysis device."),
        new(PIX_ANALYSIS_FLAGS.PIX_ANALYSIS_USE_REPLAY_ARGUMENT_BUFFERS, "USE_REPLAY_ARGUMENT_BUFFERS",
            "Use ExecuteIndirect argument buffers generated at replay time instead of the capture-time copies."),
        new(PIX_ANALYSIS_FLAGS.PIX_ANALYSIS_FLAG_USE_SINGLE_COMMAND_QUEUE, "USE_SINGLE_COMMAND_QUEUE",
            "Replay on a single command queue instead of the capture's queue types."),
        new(PIX_ANALYSIS_FLAGS.PIX_ANALYSIS_ENABLE_DEBUG_LAYER, "ENABLE_DEBUG_LAYER", "Enable the D3D12 debug layer during playback."),
        new(PIX_ANALYSIS_FLAGS.PIX_ANALYSIS_ENABLE_RECREATE_AT_GPUVA, "ENABLE_RECREATE_AT_GPUVA",
            "Recreate heaps and buffer resources at their capture-time GPU virtual addresses when the replay device supports it."),
        new(PIX_ANALYSIS_FLAGS.PIX_ANALYSIS_ENABLE_APPLICATION_SPECIFIC_DRIVER_STATE, "ENABLE_APPLICATION_SPECIFIC_DRIVER_STATE",
            "Set the captured application-specific driver state blob when available and supported by the driver."),
        new(PIX_ANALYSIS_FLAGS.PIX_ANALYSIS_FLAG_DISABLE_GPU_PLUGINS, "DISABLE_GPU_PLUGINS", "Disable GPU vendor plugins."),
        new(PIX_ANALYSIS_FLAGS.PIX_ANALYSIS_FORCE_SET_APPLICATION_SPECIFIC_DRIVER_STATE, "FORCE_SET_APPLICATION_SPECIFIC_DRIVER_STATE",
            "Set application-specific driver state even when the device or driver does not match the capture."),
    ];

    /// <summary>Short flag names in bit order, as advertised in the pix_gpu_analysis_start schema.</summary>
    public static readonly string[] Names = Table.Select(e => e.Name).ToArray();

    public const string Description = "Replay flags (default: PIX's own choice). IGNORE_INCOMPATIBILITIES, USE_REPLAY_ARGUMENT_BUFFERS, "
        + "USE_SINGLE_COMMAND_QUEUE, ENABLE_DEBUG_LAYER, ENABLE_RECREATE_AT_GPUVA, ENABLE_APPLICATION_SPECIFIC_DRIVER_STATE, DISABLE_GPU_PLUGINS, "
        + "FORCE_SET_APPLICATION_SPECIFIC_DRIVER_STATE, or NONE; the PIX_ANALYSIS_FLAG_/PIX_ANALYSIS_ prefixes are optional. "
        + "pix_gpu_analysis_status decodes the selected flags.";

    /// <summary>Combines flag names; null for no names (PIX chooses). Unknown names fail with invalid_arguments and a retry without them.</summary>
    public static PIX_ANALYSIS_FLAGS? Parse(IReadOnlyList<string>? names, string? handle = null)
    {
        if (names is null || names.Count == 0) return null;
        PIX_ANALYSIS_FLAGS combined = 0;
        var unknown = new List<string>();
        var known = new List<string>();
        foreach (string value in names)
        {
            if (Find(value) is Entry entry)
            {
                combined |= entry.Flag;
                known.Add(entry.Name);
            }
            else unknown.Add(value);
        }
        if (unknown.Count > 0)
        {
            throw new PixToolException(PixErrors.Codes.InvalidArguments,
                $"Unknown analysis flag(s) {string.Join(", ", unknown.Select(u => $"'{u}'"))}. Valid flags: {string.Join(", ", Names)} (the PIX_ANALYSIS_FLAG_ and PIX_ANALYSIS_ prefixes are optional).",
                false, handle is null ? [] : [new ToolCallDto("pix_gpu_analysis_start", new { handle, flags = known.Distinct().ToArray() }, CostHints.Job)]);
        }
        // NONE is its own bit; it only survives when nothing else was requested.
        if (combined != PIX_ANALYSIS_FLAGS.PIX_ANALYSIS_FLAG_NONE) combined &= ~PIX_ANALYSIS_FLAGS.PIX_ANALYSIS_FLAG_NONE;
        return combined;
    }

    /// <summary>Decodes selected flags; null when none were selected, because PIX's effective default is not observable. Raw 0 and NONE decode alike.</summary>
    public static AnalysisFlagsDto? Decode(PIX_ANALYSIS_FLAGS? flags)
    {
        if (flags is null) return null;
        uint raw = (uint)flags.Value;
        uint known = Table.Aggregate(0u, (all, e) => all | (uint)e.Flag);
        List<Entry> set = Table.Skip(1).Where(e => (raw & (uint)e.Flag) != 0).ToList();
        if (set.Count == 0) set.Add(Table[0]);
        return new(raw, set.Select(e => e.Name).ToArray(), set.Select(e => new AnalysisFlagDto(e.Name, e.Meaning)).ToArray(), raw & ~known);
    }

    /// <summary>explicit when the caller selected flags, pixDefault when PIX chose.</summary>
    public static string Source(PIX_ANALYSIS_FLAGS? flags) => flags is null ? "pixDefault" : "explicit";

    /// <summary>The short spelling of a flag name (trimmed, upper-cased, prefix stripped); not validated.</summary>
    public static string ShortName(string value)
    {
        string name = value.Trim().ToUpperInvariant();
        foreach (string prefix in new[] { "PIX_ANALYSIS_FLAG_", "PIX_ANALYSIS_" })
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal)) return name[prefix.Length..];
        }
        return name;
    }

    private static Entry? Find(string value)
    {
        string name = ShortName(value);
        return Table.FirstOrDefault(e => e.Name == name);
    }
}
