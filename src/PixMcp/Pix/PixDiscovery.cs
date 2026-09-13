using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Xml.Linq;

namespace PixMcp.Pix;

/// <summary>
/// The PIX build an install ships: <c>version.xml</c> (Version, Build, Commit) and the file version of the managed
/// PIX assembly. <c>effective</c> is the version string used for comparisons (version.xml, else the directory name).
/// </summary>
public sealed record PixInstallVersion(string? Effective, string? XmlVersion, string? Build, string? Commit, string? FileVersion, string Source, string? Error);

/// <summary>The PIX build this server was compiled against, recorded by Directory.Build.props as assembly metadata.</summary>
public sealed record PixBuildReference(string? XmlVersion, string? Build, string? Commit, string? FileVersion, string MinPreviewDate, string VerifiedVersion);

/// <summary>
/// How the loaded PIX relates to the build: match, newerUnverified (newer than the verified build), olderThanBuild
/// (API members can be missing), mismatch (same version, different assembly file version) or unknown.
/// <c>exit</c> says whether the server refuses to start under the strict-mode setting (PIXMCP_PIX_STRICT).
/// </summary>
public sealed record PixCompatibility(string State, string Message, string StrictMode, bool Exit);

/// <summary>
/// Locates the PIX Preview install that hosts the PIX API and wires the CLR up so that
/// PixApiCsExt.experimental.dll is loaded in place from that directory.
/// Port of pix-samples/api/PixApiResolver.cs and api/_pix_bootstrap.py.
/// </summary>
public static class PixDiscovery
{
    public const string MarkerDll = "PixApiCsExt.experimental.dll";
    public const string PickNewestVariable = "PIXMCP_PIX_PICK_NEWEST";
    public const string StrictVariable = "PIXMCP_PIX_STRICT";

    /// <summary>Installs are accepted when their YYMM.DD date is after this (Directory.Build.props PixPreviewMinDate; matches the pix-samples resolver).</summary>
    public static string MinPreviewDate { get; } = Metadata("PixPreviewMinDate") ?? "2606.15";
    /// <summary>The build the server has been verified against (Directory.Build.props PixVerifiedVersion).</summary>
    public static string VerifiedVersion { get; } = Metadata("PixVerifiedVersion") ?? "2606.18-preview";
    public static string Requirement => $"a PIX Preview build newer than {MinPreviewDate} ({VerifiedVersion} is verified; retail PIX builds do not ship the API)";

    /// <summary>The PIX build recorded at compile time (empty values when the assembly was built without discovery metadata).</summary>
    public static PixBuildReference BuiltAgainst { get; } = new(Metadata("PixBuiltAgainstXmlVersion"), Metadata("PixBuiltAgainstBuild"),
        Metadata("PixBuiltAgainstCommit"), Metadata("PixBuiltAgainstFileVersion"), MinPreviewDate, VerifiedVersion);

    public static string? InstallDir { get; private set; }
    public static string? Source { get; private set; }
    public static string? Version => InstallDir is null ? null : Path.GetFileName(InstallDir.TrimEnd('\\', '/'));
    /// <summary>True when PIXMCP_PIX_PICK_NEWEST allowed discovery to choose the newest of several eligible installs.</summary>
    public static bool PickNewest { get; private set; }

    private static readonly Lazy<PixInstallVersion?> _installVersion = new(() => InstallDir is null ? null : ReadInstallVersion(InstallDir));
    private static readonly Lazy<PixCompatibility> _compatibility = new(() => Classify(_installVersion.Value, BuiltAgainst, Environment.GetEnvironmentVariable(StrictVariable)));

    /// <summary>version.xml and assembly file version of the discovered install; null when nothing was discovered.</summary>
    public static PixInstallVersion? InstallVersion => _installVersion.Value;
    /// <summary>File version of the loaded PIX managed assembly (for example 1.0.2606.18001), the PIX build the server runs against.</summary>
    public static string? AssemblyFileVersion => _installVersion.Value?.FileVersion;
    /// <summary>The install compared with the build reference under the current PIXMCP_PIX_STRICT setting.</summary>
    public static PixCompatibility Compatibility => _compatibility.Value;
    public static string? Error { get; private set; }

    [ModuleInitializer]
    internal static void Init()
    {
        try
        {
            PickNewest = IsTruthy(Environment.GetEnvironmentVariable(PickNewestVariable));
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

    /// <summary>PIX_DIR first, then the single eligible %ProgramFiles%\Microsoft PIX Preview\&lt;version&gt; (newest when PIXMCP_PIX_PICK_NEWEST is set).</summary>
    public static string? Find(out string? source, out string? error)
        => Find(Environment.GetEnvironmentVariable("PIX_DIR"),
            Environment.GetEnvironmentVariable("ProgramW6432")
                ?? Environment.GetEnvironmentVariable("ProgramFiles")
                ?? @"C:\Program Files",
            out source, out error, PickNewest);

    internal static string? Find(string? pixDir, string programFiles, out string? source, out string? error, bool pickNewest = false)
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
        if (candidates.Count > 1 && !pickNewest)
        {
            error = $"Multiple PIX Preview installs are eligible under '{previewRoot}': {string.Join(", ", candidates.Select(c => Path.GetFileName(c.dir)))}. " +
                $"Set PIX_DIR to one of them, or set {PickNewestVariable}=1 to use the newest ({Path.GetFileName(candidates[^1].dir)}).";
            return null;
        }
        source = candidates.Count > 1 ? $"Program Files scan (newest of {candidates.Count})" : "Program Files scan";
        error = null;
        return Path.GetFullPath(candidates[^1].dir);
    }

    /// <summary>Reads version.xml (Version, Build, Commit) and the marker DLL's file version from an install directory.</summary>
    public static PixInstallVersion ReadInstallVersion(string installDir)
    {
        string leaf = Path.GetFileName(installDir.TrimEnd('\\', '/'));
        string? xmlVersion = null, build = null, commit = null, error = null, fileVersion = null;
        string source = "directoryName";
        string xmlPath = Path.Combine(installDir, "version.xml");
        if (File.Exists(xmlPath))
        {
            try
            {
                XElement? root = XDocument.Load(xmlPath).Root;
                xmlVersion = Text(root, "Version");
                build = Text(root, "Build");
                commit = Text(root, "Commit");
                if (xmlVersion is not null) source = "version.xml";
                else error = "version.xml has no Version element";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                error = "version.xml could not be read: " + ex.Message;
            }
        }
        string marker = Path.Combine(installDir, MarkerDll);
        if (File.Exists(marker))
        {
            try
            {
                fileVersion = FileVersionInfo.GetVersionInfo(marker).FileVersion;
                if (string.IsNullOrWhiteSpace(fileVersion)) fileVersion = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error ??= "the assembly file version could not be read: " + ex.Message; }
        }
        return new(xmlVersion ?? leaf, xmlVersion, build, commit, fileVersion, source, error);

        static string? Text(XElement? root, string name)
        {
            string? value = root?.Element(name)?.Value?.Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }
    }

    /// <summary>
    /// Compares an install with the build reference. Strict mode: "0"/"off" never exits, "1"/"on" exits on anything but
    /// match/unknown, unset exits on mismatch and olderThanBuild only (a newer PIX starts with a warning).
    /// </summary>
    public static PixCompatibility Classify(PixInstallVersion? install, PixBuildReference built, string? strictSetting)
    {
        string strict = strictSetting?.Trim().ToLowerInvariant() switch
        {
            "0" or "false" or "off" or "no" => "off",
            "1" or "true" or "on" or "yes" => "on",
            _ => "default",
        };
        string builtVersion = built.XmlVersion ?? built.VerifiedVersion;
        string? installVersion = install?.Effective;
        int[]? installed = installVersion is null ? null : ParseVersion(installVersion);
        int[]? reference = ParseVersion(builtVersion);
        string state, message;
        if (installed is null || reference is null)
        {
            state = "unknown";
            message = $"The installed PIX version could not be determined ({install?.Error ?? (installVersion is null ? "no install discovered" : $"'{installVersion}' is not a YYMM.DD version")}); the server was built against {builtVersion}.";
        }
        else
        {
            int comparison = CompareVersions(installed, reference);
            if (comparison < 0)
            {
                state = "olderThanBuild";
                message = $"PIX {installVersion} is older than the PIX build this server was compiled against ({builtVersion}); API members can be missing. Install {built.VerifiedVersion} or rebuild the server against the installed PIX.";
            }
            else if (comparison > 0)
            {
                state = "newerUnverified";
                message = $"PIX {installVersion} is newer than the verified build {built.VerifiedVersion}; the experimental API may have changed (pix_api_mismatch errors name the missing member).";
            }
            else if (install!.FileVersion is not null && built.FileVersion is not null && !string.Equals(install.FileVersion, built.FileVersion, StringComparison.Ordinal))
            {
                state = "mismatch";
                message = $"PIX {installVersion} ships {MarkerDll} {install.FileVersion} but this server was built against {built.FileVersion}; rebuild the server against the installed PIX.";
            }
            else
            {
                state = "match";
                message = $"PIX {installVersion} matches the build this server was compiled against.";
            }
        }
        bool exit = strict switch
        {
            "off" => false,
            "on" => state is not ("match" or "unknown"),
            _ => state is "olderThanBuild" or "mismatch",
        };
        return new(state, message, strict, exit);
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

    internal static bool IsTruthy(string? value) => value?.Trim().ToLowerInvariant() is "1" or "true" or "on" or "yes";

    private static string? Metadata(string key)
    {
        string? value = typeof(PixDiscovery).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.Ordinal))?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectoryW(string lpPathName);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string newDirectory);
}
