using System.Runtime.CompilerServices;

namespace PixMcp.Pix;

/// <summary>
/// Path rules that stay correct while static shader profiling moves the process working directory. Intel's offline compiler plugin writes
/// ASD_CompilationReport_*.md into the working directory for each compile and once at unload (the unload report's path is fixed when PIX
/// loads the plugin), so the server points the working directory at a private folder around shader profiling document creation, adapter
/// enumeration, preprocessing and compiling. Client paths therefore resolve against the directory the server started in, never the live
/// working directory.
/// </summary>
internal static class ServerPaths
{
    private static string? _startDirectory;
    private static int _pruned;

    /// <summary>The working directory when the server assembly loaded.</summary>
    public static string StartDirectory => _startDirectory ??= Environment.CurrentDirectory;

    public static string CompilerReportDirectory { get; } = Path.Combine(Path.GetTempPath(), "pixmcp-compiler-reports");

    [ModuleInitializer]
    internal static void CaptureStartDirectory() => _startDirectory ??= Environment.CurrentDirectory;

    /// <summary>A client-supplied path made absolute against <see cref="StartDirectory"/>.</summary>
    public static string Full(string path) => Path.GetFullPath(path, StartDirectory);

    /// <summary>Points the working directory at <see cref="CompilerReportDirectory"/> until disposed. PIX worker only; scopes nest.</summary>
    public static IDisposable CompilerWorkingDirectory()
    {
        string previous = Environment.CurrentDirectory;
        Directory.CreateDirectory(CompilerReportDirectory);
        if (Interlocked.Exchange(ref _pruned, 1) == 0) PruneOldReports();
        Environment.CurrentDirectory = CompilerReportDirectory;
        return new Restore(previous);
    }

    /// <summary>Keeps a week of compiler reports (they describe PIX's own compiles and are useful only for vendor diagnostics).</summary>
    private static void PruneOldReports()
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(CompilerReportDirectory, "*.md"))
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-7)) File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private sealed class Restore(string previous) : IDisposable
    {
        private string? _previous = previous;

        public void Dispose()
        {
            string? target = Interlocked.Exchange(ref _previous, null);
            if (target is not null) Environment.CurrentDirectory = target;
        }
    }
}
