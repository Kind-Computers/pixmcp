using System.Globalization;
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
    /// <summary>Installs are accepted when their YYMM.DD date is after this (matches the pix-samples resolver). 2606.18-preview is the build the server has been verified against.</summary>
    public const string MinPreviewDate = "2606.15";
    public const string VerifiedVersion = "2606.18-preview";
    public static string Requirement => $"a PIX Preview build newer than {MinPreviewDate} ({VerifiedVersion} or later; retail PIX builds do not ship the API)";

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
        => Find(Environment.GetEnvironmentVariable("PIX_DIR"),
            Environment.GetEnvironmentVariable("ProgramW6432")
                ?? Environment.GetEnvironmentVariable("ProgramFiles")
                ?? @"C:\Program Files",
            out source, out error);

    internal static string? Find(string? pixDir, string programFiles, out string? source, out string? error)
    {
        source = null;
        error = null;

        if (!string.IsNullOrEmpty(pixDir))
        {
            if (File.Exists(Path.Combine(pixDir, MarkerDll)))
            {
                source = "PIX_DIR";
                return Path.GetFullPath(pixDir);
            }
            error = $"PIX_DIR is set to '{pixDir}' but it does not contain {MarkerDll}. Correct PIX_DIR or unset it to discover an installed PIX Preview.";
            return null;
        }

        string previewRoot = Path.Combine(programFiles, "Microsoft PIX Preview");
        if (!Directory.Exists(previewRoot))
        {
            error ??= $"No PIX Preview install found under '{previewRoot}' and PIX_DIR is not set. Install {Requirement} from https://devblogs.microsoft.com/pix/download/.";
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

        candidates.Sort((a, b) =>
        {
            int comparison = CompareVersions(a.version, b.version);
            return comparison != 0 ? comparison : StringComparer.OrdinalIgnoreCase.Compare(a.dir, b.dir);
        });
        source = "Program Files scan";
        error = null;
        return Path.GetFullPath(candidates[^1].dir);
    }

    /// <summary>Parses "YYMM.DD[.NNN][-flavor]" into integer parts; null if not parseable.</summary>
    public static int[]? ParseVersion(string leaf)
    {
        string version = leaf.Split('-', 2)[0];
        var parts = new List<int>();
        foreach (string component in version.Split('.'))
        {
            if (!int.TryParse(component, NumberStyles.None, CultureInfo.InvariantCulture, out int part))
            {
                return null;
            }
            parts.Add(part);
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
