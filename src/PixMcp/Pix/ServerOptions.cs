using System.Globalization;

namespace PixMcp.Pix;

/// <summary>One configured value with where it came from, as reported by pix_info.options.</summary>
public sealed record ConfiguredValue(object? Value, string Source, string Variable);

public sealed record ServerOptionsSummary(ConfiguredValue InlineResultBytes, ConfiguredValue MaxResultBytes,
    ConfiguredValue ResultMemoryBytes, ConfiguredValue ResultDiskBytes, ConfiguredValue ResultDirectory, ConfiguredValue VerifyBulkReadback, ConfiguredValue? GpuSqlMaxBytes = null,
    ConfiguredValue? Toolsets = null, ConfiguredValue? TextContent = null, ConfiguredValue? TimingExperimentParts = null);

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
    bool VerifyBulkReadback = false, string VerifySource = ServerOptions.FromDefault,
    long GpuSqlMaxBytes = ServerOptions.DefaultGpuSqlMaxBytes, string GpuSqlSource = ServerOptions.FromDefault,
    IReadOnlySet<string>? Toolsets = null, string ToolsetsSource = ServerOptions.FromDefault,
    string TextContent = ServerOptions.TextContentFull, string TextContentSource = ServerOptions.FromDefault,
    IReadOnlyList<string>? TimingExperimentParts = null, string TimingExperimentPartsSource = ServerOptions.FromDefault)
{
    public const string VerifyVariable = "PIXMCP_VERIFY_BULK_READBACK";
    public const string InlineVariable = "PIXMCP_INLINE_RESULT_BYTES";
    public const string MaxVariable = "PIXMCP_MAX_RESULT_BYTES";
    public const string MemoryVariable = "PIXMCP_RESULT_MEMORY_BYTES";
    public const string DiskVariable = "PIXMCP_RESULT_DISK_BYTES";
    public const string DirectoryVariable = "PIXMCP_RESULT_DIR";
    public const string GpuSqlMaxVariable = "PIXMCP_GPUSQL_MAX_BYTES";
    /// <summary>Toolsets to advertise (see Toolsets); unset or "all" enables every toolset.</summary>
    public const string ToolsetsVariable = "PIXMCP_TOOLSETS";
    /// <summary>full (default): text blocks repeat the JSON; summary: one short text block per successful result.</summary>
    public const string TextContentVariable = "PIXMCP_TEXT_CONTENT";
    public const string TextContentFull = "full", TextContentSummary = "summary";
    /// <summary>
    /// Timing capture option parts (PIX_TIMING_CAPTURE_OPTION_PART_TYPE suffixes) appended to every timing capture, for experiments such as
    /// finding which part populates GpuFrame. VIDEO_SOURCEID is excluded because it needs a source.
    /// </summary>
    public const string TimingExperimentPartsVariable = "PIXMCP_TIMING_EXPERIMENT_PARTS";
    public static IReadOnlyList<string> TimingExperimentPartNames =>
        ["VIDEO", "INCLUDE_CAPTURE_ETL", "CIRCULAR", "PAGEFAULT", "CAPTURE_SYSMON_COUNTERS", "CLRDATA", "FORCE_COM_PATH", "GPU_ONLY_EVENTS", "MINIMAL_INSTRUMENTATION"];
    public const long DefaultGpuSqlMaxBytes = 1L << 30, MinGpuSqlMaxBytes = 1L << 20;
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
        (long gpuSql, string gpuSqlSource) = ParseLong(environment, GpuSqlMaxVariable, DefaultGpuSqlMaxBytes, MinGpuSqlMaxBytes, long.MaxValue, problems);
        IReadOnlySet<string>? toolsets = null;
        string toolsetsSource = FromDefault;
        if (environment(ToolsetsVariable) is string toolsetText)
        {
            toolsets = global::PixMcp.Pix.Toolsets.Parse(toolsetText, out string? toolsetProblem);
            if (toolsetProblem is null) toolsetsSource = FromEnvironment;
            else problems.Add(toolsetProblem);
        }
        string textContent = TextContentFull, textContentSource = FromDefault;
        if (environment(TextContentVariable) is string textText)
        {
            switch (textText.Trim().ToLowerInvariant())
            {
                case TextContentFull: textContentSource = FromEnvironment; break;
                case TextContentSummary: textContent = TextContentSummary; textContentSource = FromEnvironment; break;
                default: problems.Add($"{TextContentVariable} must be full or summary; got '{textText}'."); break;
            }
        }
        IReadOnlyList<string>? timingParts = null;
        string timingPartsSource = FromDefault;
        if (environment(TimingExperimentPartsVariable) is string partsText && !string.IsNullOrWhiteSpace(partsText))
        {
            string[] names = partsText.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(name => name.ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToArray();
            string[] unknown = names.Where(name => !TimingExperimentPartNames.Contains(name)).ToArray();
            if (unknown.Length > 0)
                problems.Add($"{TimingExperimentPartsVariable} names unknown timing capture option parts: {string.Join(", ", unknown)}. Valid parts: {string.Join(", ", TimingExperimentPartNames)}.");
            else
            {
                timingParts = names;
                timingPartsSource = FromEnvironment;
            }
        }
        return new(inline, inlineSource, max, maxSource, memory, memorySource, disk, diskSource, directory, directorySource, problems, verify, verifySource, gpuSql, gpuSqlSource,
            toolsets, toolsetsSource, textContent, textContentSource, timingParts, timingPartsSource);
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
        string? resultDirectory = null, bool? verifyBulkReadback = null, long? gpuSqlMaxBytes = null, string? textContent = null,
        IReadOnlyList<string>? timingExperimentParts = null)
        => Global with
        {
            InlineResultBytes = inlineResultBytes ?? Global.InlineResultBytes,
            MaxResultBytes = maxResultBytes ?? Global.MaxResultBytes,
            ResultMemoryBytes = resultMemoryBytes ?? Global.ResultMemoryBytes,
            ResultDiskBytes = resultDiskBytes ?? Global.ResultDiskBytes,
            ResultDirectory = resultDirectory ?? Global.ResultDirectory,
            VerifyBulkReadback = verifyBulkReadback ?? Global.VerifyBulkReadback,
            GpuSqlMaxBytes = gpuSqlMaxBytes ?? Global.GpuSqlMaxBytes,
            TextContent = textContent ?? Global.TextContent,
            TimingExperimentParts = timingExperimentParts ?? Global.TimingExperimentParts,
        };

    public ServerOptionsSummary Describe() => new(
        new(InlineResultBytes, InlineSource, InlineVariable),
        new(MaxResultBytes, MaxSource, MaxVariable),
        new(ResultMemoryBytes, MemorySource, MemoryVariable),
        new(ResultDiskBytes, DiskSource, DiskVariable),
        new(ResultDirectory, DirectorySource, DirectoryVariable),
        new(VerifyBulkReadback, VerifySource, VerifyVariable),
        new(GpuSqlMaxBytes, GpuSqlSource, GpuSqlMaxVariable),
        new(Toolsets is null ? "all" : string.Join(",", Toolsets.Order(StringComparer.Ordinal)), ToolsetsSource, ToolsetsVariable),
        new(TextContent, TextContentSource, TextContentVariable),
        new(TimingExperimentParts is null ? "none" : string.Join(",", TimingExperimentParts), TimingExperimentPartsSource, TimingExperimentPartsVariable));

    private sealed class Restore(ServerOptions? previous) : IDisposable
    {
        public void Dispose() => Scoped.Value = previous;
    }
}
