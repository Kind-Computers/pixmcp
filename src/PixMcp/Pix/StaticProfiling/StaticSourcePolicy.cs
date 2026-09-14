using System.Text.RegularExpressions;

namespace PixMcp.Pix.StaticProfiling;

/// <summary>One validated inline shader: lower-case stage, profile, entry, compiler flags, formatted defines and files (main file first).</summary>
internal sealed record StaticPlannedSource(string Stage, string Target, string Entry, string Flags, string Defines, IReadOnlyList<StaticSourceFile> Files)
{
    public bool DeclaresRootSignature => Files.Any(f => StaticSourcePolicy.DeclaresRootSignature(f.Content));
}

/// <summary>A validated inline pipeline. Formats are DXGI names without the prefix; the root signature default is dropped when a source declares one.</summary>
internal sealed record StaticInlinePlan(string PipelineType, IReadOnlyList<StaticPlannedSource> Sources, IReadOnlyList<string> RenderTargetFormats,
    string? DepthStencilFormat, uint SampleCount, string Topology)
{
    public bool DropRootSignature => Sources.Any(s => s.DeclaresRootSignature);
}

/// <summary>
/// Pure rules for static shader profiling inputs, as observed on PIX 2606.18: defines travel as -DNAME=VALUE compiler arguments,
/// inline pipelines start from PIX's defaults (an empty root signature unless a source declares [RootSignature(...)]), and a
/// graphics pipeline needs its vertex shader.
/// </summary>
internal static class StaticSourcePolicy
{
    public const int MaxSources = 5, MaxFilesPerSource = 32, MaxTotalCharacters = 1 << 20, MaxFlagsLength = 512, MaxRenderTargets = 8;
    public const string DefinesFormat = "-DNAME=VALUE";
    public static readonly string[] PipelineTypes = ["compute", "graphics", "mesh"];
    public static readonly string[] Topologies = ["point", "line", "triangle", "patch"];

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly Regex Profile = new(@"^(vs|ps|gs|hs|ds|cs|ms|as)_(\d)_(\d)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RegexTimeout);
    private static readonly Regex Identifier = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex DefineValue = new(@"^[^\s""'`]*$", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex RootSignature = new(@"\[\s*RootSignature\s*\(", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex FormatName = new(@"^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>The stage (vertex, pixel, geometry, hull, domain, compute, mesh, amplification) of a profile such as ps_6_0, or null.</summary>
    public static string? StageOf(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        Match match = Profile.Match(target.Trim());
        if (!match.Success) return null;
        return match.Groups[1].Value.ToLowerInvariant() switch
        {
            "vs" => "vertex",
            "ps" => "pixel",
            "gs" => "geometry",
            "hs" => "hull",
            "ds" => "domain",
            "cs" => "compute",
            "ms" => "mesh",
            "as" => "amplification",
            _ => null,
        };
    }

    public static bool DeclaresRootSignature(string hlsl) => RootSignature.IsMatch(hlsl);

    /// <summary>NAME or NAME=VALUE entries as -DNAME[=VALUE] compiler arguments; values with spaces or quotes cannot be carried.</summary>
    public static bool TryFormatDefines(IEnumerable<string>? defines, out string text, out string? problem)
    {
        text = "";
        problem = null;
        var parts = new List<string>();
        foreach (string raw in defines ?? Enumerable.Empty<string>())
        {
            string item = raw?.Trim() ?? "";
            int equals = item.IndexOf('=');
            string name = equals < 0 ? item : item[..equals];
            string? value = equals < 0 ? null : item[(equals + 1)..];
            if (!Identifier.IsMatch(name))
            {
                problem = $"Define '{raw}' needs a C identifier name (NAME or NAME=VALUE).";
                return false;
            }
            if (value is not null && !DefineValue.IsMatch(value))
            {
                problem = $"Define '{raw}' has a value with spaces or quotes, which compiler arguments cannot carry.";
                return false;
            }
            parts.Add(value is null ? $"-D{name}" : $"-D{name}={value}");
        }
        text = string.Join(' ', parts);
        return true;
    }

    /// <summary>A captured shader's defines as compiler arguments; names or values that arguments cannot carry are skipped and listed.</summary>
    public static string FormatCapturedDefines(IEnumerable<KeyValuePair<string, string>>? defines, out IReadOnlyList<string> skipped)
    {
        var parts = new List<string>();
        var skip = new List<string>();
        foreach ((string name, string value) in defines ?? Enumerable.Empty<KeyValuePair<string, string>>())
        {
            if (!Identifier.IsMatch(name ?? "") || !DefineValue.IsMatch(value ?? ""))
            {
                skip.Add(name ?? "");
                continue;
            }
            parts.Add($"-D{name}={value}");
        }
        skipped = skip;
        return string.Join(' ', parts);
    }

    /// <summary>A relative path under a scratch directory: separators normalised, roots, drives, "." and ".." removed.</summary>
    public static string SafeRelativePath(string path)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string[] parts = (path ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p is not "." and not ".." && !p.EndsWith(':'))
            .Select(p => string.Concat(p.Where(c => !invalid.Contains(c))))
            .Where(p => p.Length > 0)
            .ToArray();
        return parts.Length == 0 ? "source.hlsl" : Path.Combine(parts);
    }

    /// <summary>Validates inline sources and pipeline options; null with <paramref name="problem"/> when they cannot be compiled as given.</summary>
    public static StaticInlinePlan? Plan(IReadOnlyList<StaticShaderSource>? sources, StaticPipelineOptions? pipeline, out string? problem)
    {
        problem = null;
        if (sources is null || sources.Count is 0 or > MaxSources)
        {
            problem = $"sources needs 1 to {MaxSources} shaders.";
            return null;
        }
        var planned = new List<StaticPlannedSource>();
        long characters = 0;
        for (int i = 0; i < sources.Count; i++)
        {
            StaticShaderSource source = sources[i];
            string where = $"sources[{i}]";
            if (source is null)
            {
                problem = $"{where} is null.";
                return null;
            }
            string? stage = StageOf(source.Target);
            if (stage is null)
            {
                problem = $"{where}.target '{source.Target}' is not a vs, ps, gs, hs, ds, cs, ms or as profile such as cs_6_0.";
                return null;
            }
            if (planned.Any(p => p.Stage == stage))
            {
                problem = $"{where} is a second {stage} shader; the {stage} stage appears twice and a pipeline has one shader per stage.";
                return null;
            }
            if ((source.Hlsl is null) == (source.Files is null))
            {
                problem = $"{where} needs exactly one of hlsl or files.";
                return null;
            }
            IReadOnlyList<StaticSourceFile> files;
            if (source.Hlsl is not null) files = new[] { new StaticSourceFile($"{stage}.hlsl", source.Hlsl) };
            else files = source.Files!;
            if (files.Count is 0 or > MaxFilesPerSource)
            {
                problem = $"{where}.files needs 1 to {MaxFilesPerSource} files.";
                return null;
            }
            foreach (StaticSourceFile file in files)
            {
                if (file is null || string.IsNullOrWhiteSpace(file.Path) || file.Content is null)
                {
                    problem = $"{where}.files entries need a path and content.";
                    return null;
                }
                characters += file.Content.Length;
            }
            string entry = string.IsNullOrWhiteSpace(source.Entry) ? "main" : source.Entry.Trim();
            if (!Identifier.IsMatch(entry))
            {
                problem = $"{where}.entry '{source.Entry}' is not a function name.";
                return null;
            }
            if (!TryFormatDefines(source.Defines, out string defines, out string? defineProblem))
            {
                problem = $"{where}: {defineProblem}";
                return null;
            }
            string flags = source.Flags?.Trim() ?? "";
            if (flags.Length > MaxFlagsLength || flags.Contains('\n') || flags.Contains('\r'))
            {
                problem = $"{where}.flags must be one line of at most {MaxFlagsLength} characters.";
                return null;
            }
            planned.Add(new(stage, source.Target.Trim().ToLowerInvariant(), entry, flags, defines, files));
        }
        if (characters > MaxTotalCharacters)
        {
            problem = $"sources hold {characters} characters; the limit is {MaxTotalCharacters}.";
            return null;
        }

        string[] stages = planned.Select(p => p.Stage).ToArray();
        string type = pipeline?.Type?.Trim().ToLowerInvariant() ?? (stages.Contains("compute") ? "compute" : stages.Contains("mesh") ? "mesh" : "graphics");
        if (!PipelineTypes.Contains(type))
        {
            problem = $"pipeline.type must be one of {string.Join(", ", PipelineTypes)}.";
            return null;
        }
        string? mismatch = type switch
        {
            "compute" when stages is not ["compute"] => "A compute pipeline takes exactly one cs_* shader.",
            "graphics" when !stages.Contains("vertex") => "A graphics pipeline needs its vertex shader: add a vs_* source next to the pixel shader.",
            "graphics" when stages.Any(s => s is "compute" or "mesh" or "amplification") => "A graphics pipeline takes vs, hs, ds, gs and ps shaders only.",
            "mesh" when !stages.Contains("mesh") => "A mesh pipeline needs an ms_* source.",
            "mesh" when stages.Any(s => s is "compute" or "vertex" or "hull" or "domain" or "geometry") => "A mesh pipeline takes as, ms and ps shaders only.",
            _ => null,
        };
        if (mismatch is not null)
        {
            problem = mismatch;
            return null;
        }
        string topology = pipeline?.Topology?.Trim().ToLowerInvariant() ?? "triangle";
        if (!Topologies.Contains(topology))
        {
            problem = $"pipeline.topology must be one of {string.Join(", ", Topologies)}.";
            return null;
        }
        string[] formats = pipeline?.RenderTargetFormats?.Select(f => f?.Trim().ToUpperInvariant() ?? "").ToArray() ?? new[] { "R8G8B8A8_UNORM" };
        if (formats.Length > MaxRenderTargets || formats.Any(f => !FormatName.IsMatch(f)))
        {
            problem = $"pipeline.renderTargetFormats needs at most {MaxRenderTargets} DXGI format names such as R8G8B8A8_UNORM.";
            return null;
        }
        string? depth = pipeline?.DepthStencilFormat?.Trim().ToUpperInvariant();
        if (depth is not null && !FormatName.IsMatch(depth))
        {
            problem = "pipeline.depthStencilFormat must be a DXGI format name such as D32_FLOAT.";
            return null;
        }
        uint samples = pipeline?.SampleCount ?? 1;
        if (samples is 0 or > 32)
        {
            problem = "pipeline.sampleCount must be between 1 and 32.";
            return null;
        }
        return new(type, planned, formats, depth, samples, topology);
    }

    /// <summary>Actionable advice for the compiler messages PIX 2606.18 emits for pipeline mistakes.</summary>
    public static IReadOnlyList<string> Hints(IEnumerable<string> messages, bool capturePath)
    {
        string all = string.Join("\n", messages);
        var hints = new List<string>();
        if (all.Contains("not fully bound in root signature", StringComparison.OrdinalIgnoreCase))
            hints.Add(capturePath
                ? "The captured root signature does not cover a resource the recompiled HLSL binds; the capture's HLSL may differ from its bytecode. Profile the source inline with a [RootSignature(...)] attribute."
                : "Declare the resources' root signature in HLSL with [RootSignature(\"...\")] on the entry point; the server then replaces its empty default root signature.");
        if (all.Contains("No Root Signature found", StringComparison.OrdinalIgnoreCase))
            hints.Add("Add a [RootSignature(\"...\")] attribute to one of the shaders.");
        if (all.Contains("neither VS, CS nor MS specified", StringComparison.OrdinalIgnoreCase))
            hints.Add(capturePath
                ? "The pipeline's vertex or mesh shader has no HLSL in the capture; profile the shaders inline with sources."
                : "A graphics pipeline needs its vertex shader (a vs_* source); a mesh pipeline needs an ms_* source.");
        if (all.Contains("primitive topology", StringComparison.OrdinalIgnoreCase))
            hints.Add("Set pipeline.topology to the primitive type the pipeline draws.");
        if (all.Contains("Render Target View bound to slot", StringComparison.OrdinalIgnoreCase))
            hints.Add("List one pipeline.renderTargetFormats entry per SV_Target the pixel shader writes.");
        if (hints.Count == 0 && all.Contains("error", StringComparison.OrdinalIgnoreCase))
            hints.Add("Fix the reported HLSL error (file:line:column) and profile again.");
        return hints;
    }
}
