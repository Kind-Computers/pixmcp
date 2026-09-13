using PixMcp.Pix;
using Xunit;

namespace PixMcp.Tests;

/// <summary>
/// Locates the native test inputs. Environment variables may be absolute or repository-relative
/// (PIX_TEST_CAPTURE=tests/artifacts/baseline.wpix); a missing file or PIX install skips with a reason.
/// </summary>
internal static class TestArtifacts
{
    public static string? Root { get; } = FindRoot();
    public static bool PixInstalled => PixDiscovery.InstallDir is not null;
    public static bool AnalysisEnabled => Environment.GetEnvironmentVariable("PIX_TEST_ANALYSIS") == "1";
    public static string? Capture => Resolve("PIX_TEST_CAPTURE");
    public static string? TimingCapture => Resolve("PIX_TEST_TIMING_CAPTURE");

    /// <summary>The file named by <paramref name="variable"/>, resolved against the repository root when relative; null when unset or missing.</summary>
    public static string? Resolve(string variable)
    {
        string? value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value)) return null;
        string path = Path.IsPathRooted(value) || Root is null ? value : Path.Combine(Root, value);
        return File.Exists(path) ? Path.GetFullPath(path) : null;
    }

    /// <summary>A checked-in artifact under tests/artifacts, or null when the repository or file is absent.</summary>
    public static string? Artifact(params string[] parts)
    {
        if (Root is null) return null;
        string path = Path.Combine([Root, "tests", "artifacts", .. parts]);
        return File.Exists(path) ? path : null;
    }

    public static void SkipUnlessPix() => Skip.IfNot(PixInstalled, "PIX Preview not installed");

    public static string RequireCapture()
    {
        SkipUnlessPix();
        Skip.If(Capture is null, "Set PIX_TEST_CAPTURE to a .wpix file (absolute or repository-relative)");
        return Capture!;
    }

    public static string RequireAnalysisCapture()
    {
        string capture = RequireCapture();
        Skip.IfNot(AnalysisEnabled, "Set PIX_TEST_ANALYSIS=1 to replay on the GPU (Developer Mode required)");
        return capture;
    }

    public static string RequireTimingCapture()
    {
        SkipUnlessPix();
        Skip.If(TimingCapture is null, "Set PIX_TEST_TIMING_CAPTURE to a recorded timing .wpix file (absolute or repository-relative)");
        return TimingCapture!;
    }

    private static string? FindRoot()
    {
        for (DirectoryInfo? current = new(AppContext.BaseDirectory); current is not null; current = current.Parent)
            if (Directory.Exists(Path.Combine(current.FullName, ".git"))) return current.FullName;
        return null;
    }
}
