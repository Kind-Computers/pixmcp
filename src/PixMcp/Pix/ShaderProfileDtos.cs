namespace PixMcp.Pix;

// Snapshot of an IPixShaderProfilingLiveResult: plain data copied on the PIX worker, summarised off it (and in unit tests).

public sealed record LiveStallTypeInfo(uint Id, string Name, string? Description);

public sealed record LiveStallSample(uint Type, uint Samples);

public sealed record LiveInstructionData(uint Id, uint OffsetBytes, uint Samples, IReadOnlyList<LiveStallSample> Stalls);

/// <summary><c>Stage</c> uses the capture tools' spelling (PIXEL, COMPUTE, ...).</summary>
public sealed record LiveShaderData(uint Id, string Stage, string? Hash, IReadOnlyList<LiveInstructionData> Instructions);

public sealed record LiveProfileData(IReadOnlyList<LiveStallTypeInfo> StallTypes, IReadOnlyList<LiveShaderData> Shaders);

public sealed record StallTotalDto(string Type, ulong Samples, double Percent);

/// <summary>Sample counts of one shader: stalled samples are a subset of the total, issuing = total - stalled.</summary>
public sealed record SampleBreakdownDto(ulong Total, ulong Stalled, ulong Issuing, double StalledPercent);

/// <summary>The dominant stall type of a shader and what a keyword rule suggests it means; always a heuristic.</summary>
public sealed record StallClassificationDto(string DominantStall, double Percent, string Bound, string Hint, bool WeakSignal, string Rule, string Basis = "heuristic");

public sealed record HotInstructionDto(uint Id, uint OffsetBytes, uint Samples, double PercentOfShader, IReadOnlyList<StallTotalDto> Stalls);

public sealed record LiveShaderSummaryDto(int Index, string Stage, string? Hash, string? ShaderKey, IReadOnlyList<ShaderRef> ShaderRefs, int InstructionCount,
    SampleBreakdownDto Samples, IReadOnlyList<StallTotalDto> StallTotals, StallClassificationDto? Classification, IReadOnlyList<HotInstructionDto> HottestInstructions,
    bool HottestTruncated, string DetailPointer, IReadOnlyList<ToolCallDto> NextCalls);

public sealed record LiveProfileTotalsDto(int ShaderCount, ulong TotalSamples, ulong StalledSamples, ulong IssuingSamples, double StalledPercent,
    IReadOnlyList<StallTotalDto> StallTotalsAcrossShaders);

/// <summary>Every instruction of one shader, hottest first.</summary>
public sealed record LiveShaderDetailDto(int Index, string Stage, string? Hash, IReadOnlyList<HotInstructionDto> Instructions);

public sealed record ShaderProfileFilterDto(string? ShaderKey, ShaderRef? ShaderRef, string? Stage, string? Hash);

/// <summary>The job result of pix_gpu_shader_profile when the driver profiled the range.</summary>
public sealed record ShaderProfileDto(object? Range, object? Scope, string Vendor, ShaderProfileFilterDto? Filter, LiveProfileTotalsDto Totals,
    IReadOnlyList<LiveShaderSummaryDto> Shaders, IReadOnlyList<string> AvailableShaderKeys, IReadOnlyList<LiveStallTypeInfo> StallTypes,
    IReadOnlyList<object> Coverage, IReadOnlyList<string> CompatibilityNotes, string Semantics, IReadOnlyList<ToolCallDto> NextCalls)
{
    public object? Provenance { get; init; }
    public IReadOnlyList<LiveShaderDetailDto>? Detail { get; init; }
}

/// <summary>The cached job result of pix_gpu_shader_profile when the driver cannot profile shaders.</summary>
public sealed record ShaderProfileUnavailableDto(bool Unavailable, string Feature, string State, string Reason, ErrorDto Error, string Vendor,
    IReadOnlyList<string> CompatibilityNotes, IReadOnlyList<ToolCallDto> NextCalls);

/// <summary>Summary options: the filters narrow the shader list, never the totals.</summary>
internal sealed record LiveProfileOptions(string Vendor, int TopN, string? FilterStage, string? FilterHash);

/// <summary>Pure summary of a live shader profile: sample breakdowns, stall totals, keyword classification and hottest instructions.</summary>
internal static class LiveProfileDescriber
{
    public const string Semantics = "Samples are counts attributed to instructions, not time. Stalled samples are a subset of each instruction's samples; issuing = total - stalled. Byte offsets refer to the shader's ISA, not HLSL lines.";
    public const double WeakSignalPercent = 20;

    private static readonly (string[] Keywords, string Bound, string Hint)[] Rules =
    [
        (["memory", "texture", "latency", "cache", "fetch", "sampler", "bandwidth"], "memoryLatency",
            "The dominant stalls wait on memory or texture access, so the shader looks memory-latency bound; check occupancy and the memoryBandwidth counter preset."),
        (["wait", "barrier", "sync", "dependency", "depend"], "dependencies",
            "The dominant stalls wait on dependencies between instructions; reduce the data dependencies across the stalled instructions."),
        (["issue", "alu", "pipe"], "instructionIssue",
            "The dominant stalls are instruction issue stalls, so the execution units are saturated by this shader's instruction mix; a static profile shows that mix per target."),
        (["branch", "divergence", "diverge"], "controlFlow",
            "The dominant stalls come from control flow divergence; branches split the parallel lanes."),
    ];

    public static (LiveProfileTotalsDto Totals, IReadOnlyList<LiveShaderSummaryDto> Shaders, IReadOnlyList<string> AvailableShaderKeys, IReadOnlyList<LiveShaderDetailDto> Detail)
        Describe(LiveProfileData data, LiveProfileOptions options, Func<string?, string, IReadOnlyList<ShaderRef>> references)
    {
        var names = data.StallTypes.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First().Name);
        string Name(uint id) => names.TryGetValue(id, out string? name) ? name : $"stall {id}";

        ulong total = 0, stalled = 0;
        var across = new Dictionary<uint, ulong>();
        foreach (LiveShaderData shader in data.Shaders)
            Accumulate(shader, ref total, ref stalled, across);
        var totals = new LiveProfileTotalsDto(data.Shaders.Count, total, stalled, total >= stalled ? total - stalled : 0, Percent(stalled, total), StallTotals(across, total, Name));
        string[] keys = data.Shaders.Select(s => ShaderIdentity.ShaderKey(s.Stage, s.Hash)).OfType<string>()
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        var summaries = new List<LiveShaderSummaryDto>();
        var details = new List<LiveShaderDetailDto>();
        for (int index = 0; index < data.Shaders.Count; index++)
        {
            LiveShaderData shader = data.Shaders[index];
            if (options.FilterStage is not null && !string.Equals(shader.Stage, options.FilterStage, StringComparison.OrdinalIgnoreCase)) continue;
            if (options.FilterHash is not null && !string.Equals(shader.Hash, options.FilterHash, StringComparison.OrdinalIgnoreCase)) continue;
            ulong shaderTotal = 0, shaderStalled = 0;
            var stalls = new Dictionary<uint, ulong>();
            Accumulate(shader, ref shaderTotal, ref shaderStalled, stalls);
            HotInstructionDto[] ordered = shader.Instructions
                .OrderByDescending(i => i.Samples).ThenBy(i => i.OffsetBytes)
                .Select(i => new HotInstructionDto(i.Id, i.OffsetBytes, i.Samples, Percent(i.Samples, shaderTotal),
                    i.Stalls.Select(s => new StallTotalDto(Name(s.Type), s.Samples, Percent(s.Samples, i.Samples))).ToArray()))
                .ToArray();
            IReadOnlyList<ShaderRef> refs = references(shader.Hash, shader.Stage);
            summaries.Add(new LiveShaderSummaryDto(index, shader.Stage, shader.Hash, ShaderIdentity.ShaderKey(shader.Stage, shader.Hash), refs, shader.Instructions.Count,
                new SampleBreakdownDto(shaderTotal, shaderStalled, shaderTotal >= shaderStalled ? shaderTotal - shaderStalled : 0, Percent(shaderStalled, shaderTotal)),
                StallTotals(stalls, shaderTotal, Name), Classify(stalls.ToDictionary(kv => Name(kv.Key), kv => kv.Value), shaderTotal),
                ordered.Take(options.TopN).ToArray(), ordered.Length > options.TopN, $"/detail/{details.Count}", ShaderNextCalls(refs, options.Vendor)));
            details.Add(new LiveShaderDetailDto(index, shader.Stage, shader.Hash, ordered));
        }
        return (totals, summaries.OrderByDescending(s => s.Samples.Total).ThenBy(s => s.Index).ToArray(), keys, details);
    }

    /// <summary>The dominant stall type and the first keyword rule it matches; null when the shader has no stall samples.</summary>
    internal static StallClassificationDto? Classify(IReadOnlyDictionary<string, ulong> stallsByName, ulong totalSamples)
    {
        if (totalSamples == 0) return null;
        KeyValuePair<string, ulong> dominant = stallsByName.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).FirstOrDefault();
        if (dominant.Key is null || dominant.Value == 0) return null;
        double percent = Percent(dominant.Value, totalSamples);
        bool weak = percent < WeakSignalPercent;
        foreach ((string[] keywords, string bound, string hint) in Rules)
        {
            string? keyword = keywords.FirstOrDefault(k => dominant.Key.Contains(k, StringComparison.OrdinalIgnoreCase));
            if (keyword is null) continue;
            return new(dominant.Key, percent, bound, weak ? $"Weak signal ({percent}% of samples). {hint}" : hint, weak, $"stall type '{dominant.Key}' contains '{keyword}'");
        }
        return new(dominant.Key, percent, "unclassified",
            weak ? $"Weak signal ({percent}% of samples); no keyword rule matches this vendor stall type." : "No keyword rule matches this vendor stall type; read its description in stallTypes.",
            weak, "no keyword rule matched");
    }

    private static void Accumulate(LiveShaderData shader, ref ulong total, ref ulong stalled, Dictionary<uint, ulong> byType)
    {
        foreach (LiveInstructionData instruction in shader.Instructions)
        {
            total += instruction.Samples;
            foreach (LiveStallSample stall in instruction.Stalls)
            {
                stalled += stall.Samples;
                byType[stall.Type] = byType.GetValueOrDefault(stall.Type) + stall.Samples;
            }
        }
    }

    private static StallTotalDto[] StallTotals(Dictionary<uint, ulong> byType, ulong total, Func<uint, string> name)
        => byType.Select(kv => new StallTotalDto(name(kv.Key), kv.Value, Percent(kv.Value, total)))
            .OrderByDescending(s => s.Samples).ThenBy(s => s.Type, StringComparer.Ordinal).ToArray();

    private static IReadOnlyList<ToolCallDto> ShaderNextCalls(IReadOnlyList<ShaderRef> refs, string vendor)
    {
        if (refs.Count == 0) return [];
        ShaderRef first = refs[0];
        string target = vendor == "amd" ? "gfx1201" : "Xe2-HPG";
        return
        [
            new("pix_gpu_shader_code", new { shaderRef = first, codeType = "ISA", startLine = 1, lineCount = 100 }, CostHints.Query),
            new("pix_gpu_shader_static_profile", new { shaderRef = first, target, waitSeconds = 60 }, CostHints.Job),
        ];
    }

    private static double Percent(ulong part, ulong whole) => whole == 0 ? 0 : Math.Round(100.0 * part / whole, 2);
}
