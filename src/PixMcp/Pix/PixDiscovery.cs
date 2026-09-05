using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace PixMcp.Pix;

/// <summary>
/// Locates the PIX Preview install that hosts the PIX API and wires the CLR up so that
/// PixApiCsExt.experimental.dll is loaded in place from that directory.
/// Port of pix-samples/api/PixApiResolver.cs and api/_pix_bootstrap.py.
/// </summary>
public static class PixDiscovery
{
    public const string MarkerDll = "PixApiCsExt.experimental.dll";
    public const string MinPreviewDate = "2606.15";

    public static string? InstallDir { get; private set; }
    public static string? Source { get; private set; }
    public static string? Version => InstallDir is null ? null : Path.GetFileName(InstallDir.TrimEnd('\\', '/'));
    public static string? Error { get; private set; }

    [ModuleInitializer]
    internal static void Init()
    {
        try
        {
            InstallDir = Find(out var source, out var error);
            Source = source;
            Error = error;
            if (InstallDir is null)
            {
                return;
            }

            string dir = InstallDir;
            AssemblyLoadContext.Default.Resolving += (context, name) =>
            {
                if (name.Name is not ("PixApiCsExt" or "PixApiCsExt.experimental"))
                {
                    return null;
                }
                string candidate = Path.Combine(dir, name.Name + ".dll");
                return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
            };

            // pixapi.dll pulls in ~100 MB of native PIX engine DLLs that must resolve from the
            // install directory; put it on the process DLL search path (same as the Python
            // samples' os.add_dll_directory).
            SetDllDirectoryW(dir);
            AddDllDirectory(dir);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    /// <summary>PIX_DIR first, then the newest %ProgramFiles%\Microsoft PIX Preview\&lt;version&gt; that contains the marker DLL.</summary>
    public static string? Find(out string? source, out string? error)
    {
        source = null;
        error = null;

        string? pixDir = Environment.GetEnvironmentVariable("PIX_DIR");
        if (!string.IsNullOrEmpty(pixDir))
        {
            if (File.Exists(Path.Combine(pixDir, MarkerDll)))
            {
                source = "PIX_DIR";
                return Path.GetFullPath(pixDir);
            }
            error = $"PIX_DIR is set to '{pixDir}' but it does not contain {MarkerDll}.";
        }

        string programFiles = Environment.GetEnvironmentVariable("ProgramW6432")
            ?? Environment.GetEnvironmentVariable("ProgramFiles")
            ?? @"C:\Program Files";
        string previewRoot = Path.Combine(programFiles, "Microsoft PIX Preview");
        if (!Directory.Exists(previewRoot))
        {
            error ??= $"No PIX Preview install found under '{previewRoot}' and PIX_DIR is not set. Install PIX Preview 2606.18 or newer from https://devblogs.microsoft.com/pix/download/.";
            return null;
        }

        var candidates = new List<(int[] version, string dir)>();
        foreach (string dir in Directory.GetDirectories(previewRoot))
        {
            if (!File.Exists(Path.Combine(dir, MarkerDll)))
            {
                continue;
            }
            int[]? version = ParseVersion(Path.GetFileName(dir));
            if (version is null || !IsAfterMinDate(version))
            {
                continue;
            }
            candidates.Add((version, dir));
        }

        if (candidates.Count == 0)
        {
            error ??= $"No PIX Preview build newer than {MinPreviewDate} with {MarkerDll} found under '{previewRoot}'.";
            return null;
        }

        candidates.Sort((a, b) => CompareVersions(a.version, b.version));
        source = "Program Files scan";
        error = null;
        return candidates[^1].dir;
    }

    /// <summary>Parses "YYMM.DD[.NNN][-flavor]" into integer parts; null if not parseable.</summary>
    public static int[]? ParseVersion(string leaf)
    {
        string version = leaf.Split('-', 2)[0];
        var parts = new List<int>();
        foreach (string component in version.Split('.'))
        {
            if (component.Length == 0 || !component.All(char.IsAsciiDigit))
            {
                return null;
            }
            parts.Add(int.Parse(component));
        }
        return parts.Count >= 2 ? parts.ToArray() : null;
    }

    public static int CompareVersions(int[] a, int[] b)
    {
        int n = Math.Max(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            int x = i < a.Length ? a[i] : 0;
            int y = i < b.Length ? b[i] : 0;
            if (x != y)
            {
                return x.CompareTo(y);
            }
        }
        return 0;
    }

    private static bool IsAfterMinDate(int[] version)
    {
        int[] min = ParseVersion(MinPreviewDate)!;
        return CompareVersions(new[] { version[0], version[1] }, min) > 0;
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectoryW(string lpPathName);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string newDirectory);
}
