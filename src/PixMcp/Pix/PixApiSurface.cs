using System.Diagnostics;
using System.Reflection;

namespace PixMcp.Pix;

/// <summary>One managed PIX type or member the server relies on (or knows to be absent), probed by name in the loaded assembly.</summary>
public sealed record ApiProbe(string Feature, string Type, string? Member, bool Present, bool Expected)
{
    /// <summary>True when the loaded assembly disagrees with what this server build expects.</summary>
    public bool Drift => Present != Expected;
}

/// <summary>
/// Lazily probes the loaded PixApiCsExt.experimental assembly for the types and members the server binds against,
/// by name and without native calls, so capability verdicts and pix_info can name the DLL version they come from.
/// </summary>
public static class PixApiSurface
{
    public const string AssemblyName = "PixApiCsExt.experimental";

    /// <summary>Feature, type name, member name (null = the type itself) and whether this build expects it to exist.</summary>
    public static readonly IReadOnlyList<(string Feature, string Type, string? Member, bool Expected)> Definitions =
    [
        ("bulkTimingReadback", "Microsoft.PIX.IPixGpuCaptureTiming", "GetQueueDataCount", true),
        ("bulkCounterReadback", "Microsoft.PIX.IPixGpuCaptureCounterData", "GetQueueDataCount", true),
        ("timingPassOccupancy", "Microsoft.PIX.IPixGpuCaptureTiming", "HasOccupancyData", true),
        ("timingPassHighFrequencyCounters", "Microsoft.PIX.IPixGpuCaptureTiming", "HasHighFrequencyCounterData", true),
        ("occupancyEventPoints", "Microsoft.PIX.IPixGpuCaptureOccupancyData", "GetEventPoints", true),
        ("staticShaderProfiling", "Microsoft.PIX.Internal.IPixShaderProfilingDocument", "Compile", true),
        ("shaderProfilingDocument", "Microsoft.PIX.Internal.IPixFactoryExperimental", "OpenShaderProfilingDocument", true),
        ("shaderProfilingVendorName", "Microsoft.PIX.IPixShaderProfilingAdapter", "GetVendorName", true),
        ("liveShaderProfiling", "Microsoft.PIX.Internal.IPixGpuCaptureAnalysisExperimental", "ProfileShaderPipeline", true),
        ("tileMappings", "Microsoft.PIX.IPixTileMappings", null, true),
        ("captureScreenshot", "Microsoft.PIX.IPixGpuCaptureResult", "GetScreenshotPngData", true),
        ("remoteCaptureCopy", "Microsoft.PIX.IPixConnectionDocument", "CopyCaptureFromRemote", true),
        ("liveShaderDebugging", "Microsoft.PIX.Internal.IPixLiveShaderDebuggingSession", null, true),
        ("d3dState", "Microsoft.PIX.IPixD3DState", null, false),
        ("pixelHistory", "Microsoft.PIX.IPixPixelHistory", null, false),
        ("captureShaderStepping", "Microsoft.PIX.IPixGpuCaptureShaderDebugging", null, false),
        ("resourceContents", "Microsoft.PIX.IPixResourceReadback", null, false),
    ];

    private static readonly Lazy<(Assembly? Assembly, string? Error)> _assembly = new(Load);
    private static readonly Lazy<IReadOnlyList<ApiProbe>> _probes = new(() =>
        _assembly.Value.Assembly is { } assembly ? ProbeAssembly(assembly, Definitions) : Array.Empty<ApiProbe>());
    private static readonly Lazy<string?> _loadedFileVersion = new(() =>
    {
        try
        {
            string? location = _assembly.Value.Assembly?.Location;
            return string.IsNullOrEmpty(location) ? null : FileVersionInfo.GetVersionInfo(location).FileVersion;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
    });

    /// <summary>Why the assembly could not be loaded (PIX missing, wrong image); null when the probes ran.</summary>
    public static string? LoadError => _assembly.Value.Error;
    /// <summary>File version of the assembly actually loaded into this process (cross-checks the install's version).</summary>
    public static string? LoadedFileVersion => _loadedFileVersion.Value;
    public static IReadOnlyList<ApiProbe> Probes => _probes.Value;

    /// <summary>Whether the feature's type/member exists in the loaded assembly; null when the assembly could not be probed.</summary>
    public static bool? Has(string feature)
        => Probes.FirstOrDefault(p => p.Feature == feature) is { } probe ? probe.Present : null;

    public static IReadOnlyList<ApiProbe> ProbeAssembly(Assembly assembly, IEnumerable<(string Feature, string Type, string? Member, bool Expected)> definitions)
        => definitions.Select(d => new ApiProbe(d.Feature, d.Type, d.Member, Present(assembly, d.Type, d.Member), d.Expected)).ToArray();

    /// <summary>True when the type (and member) exists; false when it is missing or cannot be loaded (a missing dependency counts as absent).</summary>
    public static bool Present(Assembly assembly, string type, string? member)
    {
        try
        {
            Type? found = assembly.GetType(type, throwOnError: false, ignoreCase: false);
            if (found is null) return false;
            if (member is null) return true;
            return found.GetMember(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Length > 0;
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or TypeLoadException or BadImageFormatException)
        {
            return false;
        }
    }

    private static (Assembly?, string?) Load()
    {
        if (PixDiscovery.InstallDir is null) return (null, "The PIX assembly is not available: " + (PixDiscovery.Error ?? "no PIX Preview install discovered."));
        try
        {
            return (Assembly.Load(new AssemblyName(AssemblyName)), null);
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return (null, ex.GetType().Name + ": " + ex.Message);
        }
    }
}
