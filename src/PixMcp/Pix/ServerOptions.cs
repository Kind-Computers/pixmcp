using System.Globalization;

namespace PixMcp.Pix;

/// <summary>One configured value with where it came from, as reported by pix_info.options.</summary>
public sealed record ConfiguredValue(object? Value, string Source, string Variable);

public sealed record ServerOptionsSummary(ConfiguredValue InlineResultBytes, ConfiguredValue MaxResultBytes,
    ConfiguredValue ResultMemoryBytes, ConfiguredValue ResultDiskBytes, ConfiguredValue ResultDirectory, ConfiguredValue VerifyBulkReadback);

/// <summary>
/// Server configuration, parsed once from the environment before any protocol output. Every consumer reads
/// <see cref="Current"/>; a malformed variable is reported by <see cref="Problems"/> (Program exits 1) and the
/// default is used so static initialisation never throws. Tests scope a replacement with <see cref="Override"/>.
/// </summary>
public sealed record ServerOptions(
    int InlineResultBytes, string InlineSource,
    int MaxResultBytes, string MaxSource,
    long ResultMemoryBytes, string MemorySource,
    long ResultDiskBytes, string DiskSource,
    string? ResultDirectory, string DirectorySource,
    IReadOnlyList<string> Problems,
    bool VerifyBulkReadback = false, string VerifySource = ServerOptions.FromDefault)
{
    public const string VerifyVariable = "PIXMCP_VERIFY_BULK_READBACK";
    public const string InlineVariable = "PIXMCP_INLINE_RESULT_BYTES";
    public const string MaxVariable = "PIXMCP_MAX_RESULT_BYTES";
    public const string MemoryVariable = "PIXMCP_RESULT_MEMORY_BYTES";
    public const string DiskVariable = "PIXMCP_RESULT_DISK_BYTES";
    public const string DirectoryVariable = "PIXMCP_RESULT_DIR";
    public const int DefaultInlineResultBytes = 32 * 1024;
    public const int MinInlineResultBytes = 1024;
    public const int DefaultMaxResultBytes = 2 * 1024 * 1024;
    public const long DefaultResultMemoryBytes = 256L * 1024 * 1024;
    public const long DefaultResultDiskBytes = 2L * 1024 * 1024 * 1024;
    public const string FromDefault = "default", FromEnvironment = "env";

    private static readonly ServerOptions Global = Parse(Environment.GetEnvironmentVariable);
    private static readonly AsyncLocal<ServerOptions?> Scoped = new();

    /// <summary>The options in effect: a test override for the current async flow, else the process configuration.</summary>
    public static ServerOptions Current => Scoped.Value ?? Global;

    /// <summary>Parses every variable; each malformed one yields one message in <see cref="Problems"/> and keeps its default.</summary>
    public static ServerOptions Parse(Func<string, string?> environment)
    {
        var problems = new List<string>();
        (int max, string maxSource) = ParseInt(environment, MaxVariable, DefaultMaxResultBytes, 1, int.MaxValue, problems);
        int inlineDefault = Math.Min(DefaultInlineResultBytes, max);
        (int inline, string inlineSource) = ParseInt(environment, InlineVariable, inlineDefault, MinInlineResultBytes, max, problems);
        if (inlineSource == FromEnvironment && inline > max) { problems.Add($"{InlineVariable} ({inline}) cannot exceed {MaxVariable} ({max})."); inline = inlineDefault; inlineSource = FromDefault; }
        (long memory, string memorySource) = ParseLong(environment, MemoryVariable, DefaultResultMemoryBytes, 0, long.MaxValue, problems);
        (long disk, string diskSource) = ParseLong(environment, DiskVariable, DefaultResultDiskBytes, 0, long.MaxValue, problems);
        if (memory == 0 && disk == 0)
        {
            problems.Add($"{MemoryVariable} and {DiskVariable} cannot both be 0; at least one result budget must be positive.");
            memory = DefaultResultMemoryBytes; memorySource = FromDefault; disk = DefaultResultDiskBytes; diskSource = FromDefault;
        }
        string? directory = null, directorySource = FromDefault;
        string? configured = environment(DirectoryVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!Path.IsPathRooted(configured)) problems.Add($"{DirectoryVariable} must be an absolute directory path; got '{configured}'.");
            else
            {
                try { Directory.CreateDirectory(configured); directory = Path.GetFullPath(configured); directorySource = FromEnvironment; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                { problems.Add($"{DirectoryVariable} '{configured}' cannot be created or used: {ex.Message}"); }
            }
        }
        (bool verify, string verifySource) = ParseFlag(environment, VerifyVariable, problems);
        return new(inline, inlineSource, max, maxSource, memory, memorySource, disk, diskSource, directory, directorySource, problems, verify, verifySource);
    }

    private static (bool, string) ParseFlag(Func<string, string?> environment, string variable, List<string> problems)
    {
        string? text = environment(variable);
        if (text is null) return (false, FromDefault);
        switch (text.Trim().ToLowerInvariant())
        {
            case "1" or "true" or "yes": return (true, FromEnvironment);
            case "0" or "false" or "no" or "": return (false, FromEnvironment);
            default: problems.Add($"{variable} must be 1 or 0; got '{text}'."); return (false, FromDefault);
        }
    }

    private static (int, string) ParseInt(Func<string, string?> environment, string variable, int fallback, int min, int max, List<string> problems)
    {
        (long value, string source) = ParseLong(environment, variable, fallback, min, max, problems);
        return ((int)value, source);
    }

    private static (long, string) ParseLong(Func<string, string?> environment, string variable, long fallback, long min, long max, List<string> problems)
    {
        string? text = environment(variable);
        if (text is null) return (fallback, FromDefault);
        if (long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long value) && value >= min && value <= max)
            return (value, FromEnvironment);
        problems.Add(max == long.MaxValue
            ? $"{variable} must be an integer byte count of at least {min}; got '{text}'."
            : $"{variable} must be an integer between {min} and {max}; got '{text}'.");
        return (fallback, FromDefault);
    }

    /// <summary>Scopes <paramref name="options"/> to the current async flow (tests); dispose to restore.</summary>
    public static IDisposable Override(ServerOptions options)
    {
        ServerOptions? previous = Scoped.Value;
        Scoped.Value = options;
        return new Restore(previous);
    }

    /// <summary>A copy of the process configuration with some values replaced (for tests).</summary>
    public static ServerOptions With(int? inlineResultBytes = null, int? maxResultBytes = null, long? resultMemoryBytes = null, long? resultDiskBytes = null,
        string? resultDirectory = null, bool? verifyBulkReadback = null)
        => Global with
        {
            InlineResultBytes = inlineResultBytes ?? Global.InlineResultBytes,
            MaxResultBytes = maxResultBytes ?? Global.MaxResultBytes,
            ResultMemoryBytes = resultMemoryBytes ?? Global.ResultMemoryBytes,
            ResultDiskBytes = resultDiskBytes ?? Global.ResultDiskBytes,
            ResultDirectory = resultDirectory ?? Global.ResultDirectory,
            VerifyBulkReadback = verifyBulkReadback ?? Global.VerifyBulkReadback,
        };

    public ServerOptionsSummary Describe() => new(
        new(InlineResultBytes, InlineSource, InlineVariable),
        new(MaxResultBytes, MaxSource, MaxVariable),
        new(ResultMemoryBytes, MemorySource, MemoryVariable),
        new(ResultDiskBytes, DiskSource, DiskVariable),
        new(ResultDirectory, DirectorySource, DirectoryVariable),
        new(VerifyBulkReadback, VerifySource, VerifyVariable));

    private sealed class Restore(ServerOptions? previous) : IDisposable
    {
        public void Dispose() => Scoped.Value = previous;
    }
}
