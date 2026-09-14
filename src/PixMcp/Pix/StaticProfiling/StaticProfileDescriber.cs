using System.Globalization;
using System.Text.RegularExpressions;

namespace PixMcp.Pix.StaticProfiling;

/// <summary>What a compile produced: success with a snapshot, or the failing phase (preprocess or compile) with the compiler messages.</summary>
internal sealed record StaticCompileOutcome(bool Succeeded, string? Phase, IReadOnlyList<CompilerMessageDto> Messages, StaticResultData? Data, double ElapsedMs);

/// <summary>How to present an outcome: the resolved target, inline or capture source, topN and coverage facts.</summary>
internal sealed record StaticResultContext(string Source, ShaderTargetDto Target, int TopN, bool PipelineStateFromCapture, bool RootSignatureFromSource,
    StaticDefinesFormatDto DefinesFormat, IReadOnlyList<string> ShadersWithoutSource, IReadOnlyList<ToolCallDto> NextCalls)
{
    public Func<string, ShaderRef?>? ShaderRefForStage { get; init; }
    public Func<string, string?>? EntryForStage { get; init; }
    /// <summary>Stages to summarise (null = every shader); detail follows the same filter.</summary>
    public Func<string, bool>? IncludeStage { get; init; }
    public IReadOnlyList<string> ExtraNotes { get; init; } = [];
}

/// <summary>Pure summary of a static profiling snapshot: instruction mix, register pressure, loops, loop-weighted hot spots and source lines.</summary>
internal static class StaticProfileDescriber
{
    public const string WeightRule = "weight = the instruction type's fixed cycles (1 when it has no fixed rate) x 10^loopDepth, capped at depth 6; a static ranking, not measured time.";
    public const string LineRule = "Source lines and columns are 1-based.";
    private const uint Invalid = StaticResultData.InvalidId;
    private const int MaxLoops = 16, MaxWarnings = 20, MaxInlining = 8, TopTypes = 10, MaxTextLength = 160;
    private static readonly Regex UsagePair = new(@"([A-Za-z][A-Za-z ]*?)\s*:\s*([0-9]+(?:\.[0-9]+)?)\s*(kB|MB|B)?\s*(?:/\s*([0-9]+(?:\.[0-9]+)?)\s*(kB|MB|B)?)?",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static StaticProfileDto Build(StaticCompileOutcome outcome, StaticResultContext context, IReadOnlyList<string> notes)
    {
        StaticTargetInfoDto target = TargetInfo(outcome.Data, context.Target);
        double elapsed = Math.Round(outcome.ElapsedMs, 1);
        if (!outcome.Succeeded || outcome.Data is null)
            return new StaticProfileDto(false, context.Source, outcome.Phase ?? "compile", target, outcome.Messages,
                StaticSourcePolicy.Hints(outcome.Messages.Select(m => m.Text), context.PipelineStateFromCapture), [], null, Coverage(context, false, notes), context.NextCalls)
            { ElapsedMs = elapsed };
        (IReadOnlyList<StaticShaderSummaryDto> shaders, StaticLegendDto legend, IReadOnlyList<StaticShaderDetailDto> detail) = Describe(outcome.Data, context);
        bool mapping = outcome.Data.Shaders.Any(s => s.Locations.Count > 0);
        return new StaticProfileDto(true, context.Source, null, target, outcome.Messages, [], shaders, legend, Coverage(context, mapping, notes), context.NextCalls)
        { ElapsedMs = elapsed, Detail = detail };
    }

    private static StaticTargetInfoDto TargetInfo(StaticResultData? data, ShaderTargetDto target)
        => data is null
            ? new(target.TargetRef, target.Vendor, target.Name, target.Family, target.Architecture, null, null, null)
            : new(target.TargetRef, target.Vendor, string.IsNullOrWhiteSpace(data.AdapterName) ? target.Name : data.AdapterName,
                string.IsNullOrWhiteSpace(data.Family) ? target.Family : data.Family, target.Architecture, data.FrontendCompiler, data.BackendCompiler,
                StaticTargets.DocumentationLink(data.IsaDocumentationLink));

    private static StaticCoverageDto Coverage(StaticResultContext context, bool mapping, IReadOnlyList<string> notes)
        => new(mapping, context.PipelineStateFromCapture, context.RootSignatureFromSource, context.ShadersWithoutSource, context.DefinesFormat,
            notes.Concat(context.ExtraNotes).Distinct(StringComparer.Ordinal).ToArray());

    internal static (IReadOnlyList<StaticShaderSummaryDto> Shaders, StaticLegendDto Legend, IReadOnlyList<StaticShaderDetailDto> Detail) Describe(StaticResultData data, StaticResultContext context)
    {
        var types = data.InstructionTypes.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
        var categories = data.Categories.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First().Name);
        var branchNames = data.BranchTypes.GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First().Name);
        var dependencyNames = data.DependencyTypes.GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.First().Name);
        string TypeName(uint id) => types.TryGetValue(id, out StaticInstructionTypeInfo? t) ? t.Name : $"type {id}";
        uint? CyclesOf(uint id) => types.TryGetValue(id, out StaticInstructionTypeInfo? t) && !t.IsVariable ? t.Cycles : null;
        double Weight(uint type, int depth)
        {
            double baseWeight = types.TryGetValue(type, out StaticInstructionTypeInfo? t) && !t.IsVariable ? Math.Max(1, t.Cycles) : 1;
            return baseWeight * Math.Pow(10, Math.Min(depth, 6));
        }

        var summaries = new List<StaticShaderSummaryDto>();
        var details = new List<StaticShaderDetailDto>();
        for (int index = 0; index < data.Shaders.Count; index++)
        {
            StaticShaderData shader = data.Shaders[index];
            if (context.IncludeStage is not null && !context.IncludeStage(shader.Stage)) continue;
            ControlFlowAnalysis flow = ControlFlow.Analyze(shader.Blocks.Select(b => (b.Id, b.Successors)).ToArray());
            var rows = shader.Blocks
                .SelectMany(b => b.Instructions.Select(ins => (Block: b.Id, Instruction: ins, Depth: flow.LoopDepth.GetValueOrDefault(b.Id))))
                .ToArray();
            int total = rows.Length;

            var files = shader.Files.GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First().Name);
            var functions = shader.Functions.GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First().Name);
            var locations = shader.Locations.GroupBy(l => l.Id).ToDictionary(g => g.Key, g => g.First());
            string? FunctionName(uint id) => id != Invalid && functions.TryGetValue(id, out string? name) ? name : null;
            StaticSourceLocationDto? Source(uint id)
            {
                if (id == Invalid || !locations.TryGetValue(id, out StaticSourceLocationInfo? location)) return null;
                var chain = new List<string>();
                var seen = new HashSet<uint> { id };
                uint calling = location.CallingLocationId;
                while (calling != Invalid && chain.Count < MaxInlining && seen.Add(calling) && locations.TryGetValue(calling, out StaticSourceLocationInfo? caller))
                {
                    chain.Add($"{FunctionName(caller.FunctionId) ?? "?"} ({files.GetValueOrDefault(caller.FileId) ?? "?"}:{caller.Line + 1})");
                    calling = caller.CallingLocationId;
                }
                return new(files.GetValueOrDefault(location.FileId), location.Line + 1, location.Column + 1, FunctionName(location.FunctionId), chain.Count == 0 ? null : chain);
            }

            StaticCountDto[] byCategory = rows
                .GroupBy(r => types.TryGetValue(r.Instruction.Type, out StaticInstructionTypeInfo? t) ? t.Category : Invalid)
                .Select(g => new StaticCountDto(categories.TryGetValue(g.Key, out string? name) ? name : "uncategorized", g.Count(), Percent(g.Count(), total)))
                .OrderByDescending(c => c.Count).ThenBy(c => c.Name, StringComparer.Ordinal)
                .ToArray();
            StaticCountDto[] topTypes = rows
                .GroupBy(r => r.Instruction.Type)
                .Select(g => new StaticCountDto(TypeName(g.Key), g.Count(), Percent(g.Count(), total), CyclesOf(g.Key)))
                .OrderByDescending(c => c.Count).ThenBy(c => c.Name, StringComparer.Ordinal)
                .Take(TopTypes)
                .ToArray();
            ulong fixedCycles = (ulong)rows.Sum(r => (long)(CyclesOf(r.Instruction.Type) ?? 0));
            int variable = rows.Count(r => types.TryGetValue(r.Instruction.Type, out StaticInstructionTypeInfo? t) && t.IsVariable);

            var registerTypeOf = shader.Registers.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First().Type);
            var pressure = new List<StaticRegisterPressureDto>();
            foreach (StaticRegisterTypeInfo type in data.RegisterTypes)
            {
                uint allocated = shader.AllocatedByType.GetValueOrDefault(type.Id);
                int peak = 0;
                double sum = 0;
                foreach (var row in rows)
                {
                    int live = row.Instruction.Live.Count(id => registerTypeOf.TryGetValue(id, out uint t) && t == type.Id);
                    peak = Math.Max(peak, live);
                    sum += live;
                }
                if (allocated == 0 && peak == 0) continue;
                pressure.Add(new(type.Name, type.Prefix, allocated, peak, total == 0 ? 0 : Math.Round(sum / total, 2)));
            }
            uint totalAllocated = (uint)shader.AllocatedByType.Values.Sum(v => (long)v);

            StaticLoopDto[] loops = flow.Loops
                .Select(loop => new StaticLoopDto(loop.Header, loop.Blocks.Count, flow.LoopDepth.GetValueOrDefault(loop.Header),
                    shader.Blocks.Where(b => loop.Blocks.Contains(b.Id)).Sum(b => b.Instructions.Count)))
                .OrderByDescending(l => l.Depth).ThenByDescending(l => l.Instructions).ThenBy(l => l.HeaderBlock)
                .Take(MaxLoops)
                .ToArray();
            var controlFlow = new StaticControlFlowDto(shader.Blocks.Count, flow.Loops.Count, flow.LoopDepth.Values.DefaultIfEmpty(0).Max(), flow.Irreducible, loops);

            var weighted = rows
                .Select(r => (r.Block, r.Instruction, r.Depth, Weight: Weight(r.Instruction.Type, r.Depth)))
                .OrderByDescending(r => r.Weight).ThenBy(r => r.Instruction.OffsetBytes)
                .ToArray();
            StaticHotSpotDto[] hotSpots = weighted.Take(context.TopN)
                .Select(r => new StaticHotSpotDto(r.Instruction.Id, r.Block, TypeName(r.Instruction.Type), Clip(r.Instruction.Text), CyclesOf(r.Instruction.Type), r.Depth,
                    Math.Round(r.Weight, 2), Source(r.Instruction.SourceLocation)))
                .ToArray();
            StaticSourceLineDto[] lines = weighted
                .Where(r => r.Instruction.SourceLocation != Invalid && locations.ContainsKey(r.Instruction.SourceLocation))
                .GroupBy(r => (locations[r.Instruction.SourceLocation].FileId, locations[r.Instruction.SourceLocation].Line))
                .Select(g => new StaticSourceLineDto(files.GetValueOrDefault(g.Key.FileId), g.Key.Line + 1,
                    FunctionName(locations[g.First().Instruction.SourceLocation].FunctionId), g.Count(), Math.Round(g.Sum(r => r.Weight), 2),
                    (ulong)g.Sum(r => (long)(CyclesOf(r.Instruction.Type) ?? 0))))
                .OrderByDescending(l => l.Weight).ThenBy(l => l.Line)
                .Take(context.TopN)
                .ToArray();
            string[] warnings = rows
                .SelectMany(r => r.Instruction.Comments.Where(c => c.Severity is "warning" or "error").Select(c => $"{c.Severity}: {c.Text}"))
                .Distinct(StringComparer.Ordinal).Take(MaxWarnings)
                .ToArray();
            StaticInfoDto[] info = shader.Infos.Select(i => new StaticInfoDto(i.Name.Trim(), Whitespace.Replace(i.Value, " ").Trim())).ToArray();

            summaries.Add(new StaticShaderSummaryDto(index, shader.Stage, shader.Hash, context.ShaderRefForStage?.Invoke(shader.Stage),
                context.EntryForStage?.Invoke(shader.Stage) ?? FunctionName(shader.EntryFunction), total, info, pressure, totalAllocated,
                new StaticInstructionMixDto(total, byCategory, topTypes, fixedCycles, variable), controlFlow, hotSpots, total > context.TopN, lines, warnings,
                ResourceUsage(info), $"/detail/{details.Count}"));

            var registerNames = shader.Registers.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First().Name);
            string Register(uint id) => registerNames.TryGetValue(id, out string? name) ? name : $"#{id}";
            details.Add(new StaticShaderDetailDto(index, shader.Stage, shader.Registers, shader.Blocks.Select(block => new StaticBlockDetailDto(block.Id, block.Name, block.Successors,
                block.SuccessorBranchTypes.Select(t => branchNames.TryGetValue(t, out string? name) ? name : $"type {t}").ToArray(), flow.LoopDepth.GetValueOrDefault(block.Id),
                block.Instructions.Select(ins => new StaticInstructionDetailDto(ins.Id, TypeName(ins.Type), ins.Text, ins.OffsetBytes, ins.SizeBytes, ins.Predicate, CyclesOf(ins.Type),
                    ins.Read.Select(Register).ToArray(), ins.Written.Select(Register).ToArray(), ins.Live.Count, ins.Freed.Select(Register).ToArray(),
                    ins.Dependencies.Count == 0 ? null : ins.Dependencies.Select(d => $"{(dependencyNames.TryGetValue(d.Type, out string? name) ? name : $"type {d.Type}")} -> {d.Instruction}").ToArray(),
                    ins.Comments.Count == 0 ? null : ins.Comments.Select(c => $"{c.Severity}: {c.Text}").ToArray(),
                    Source(ins.SourceLocation))).ToArray())).ToArray()));
        }

        var legend = new StaticLegendDto(data.Categories, data.RegisterTypes, data.BranchTypes.Select(b => b.Name).ToArray(),
            data.DependencyTypes.Select(d => d.Description is null ? d.Name : $"{d.Name}: {d.Description}").ToArray(), WeightRule, LineRule);
        return (summaries, legend, details);
    }

    /// <summary>The vendor compiler's "Resource usage" info (AMD) as used/limit entries, or null.</summary>
    internal static StaticResourceUsageDto? ResourceUsage(IReadOnlyList<StaticInfoDto> info)
    {
        StaticInfoDto? usage = info.FirstOrDefault(i => i.Name.Equals("Resource usage", StringComparison.OrdinalIgnoreCase));
        if (usage is null) return null;
        StaticUsageEntryDto[] entries = UsagePair.Matches(usage.Value)
            .Select(m => new StaticUsageEntryDto(m.Groups[1].Value.Trim(), double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                m.Groups[4].Success ? double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) : null,
                m.Groups[5].Success ? m.Groups[5].Value : m.Groups[3].Success ? m.Groups[3].Value : null))
            .ToArray();
        return entries.Length == 0 ? null
            : new("compilerInfo", entries, "The vendor compiler's resource usage as used/limit; what each entry counts is vendor-defined, and it is not a measured occupancy.");
    }

    private static double Percent(int part, int total) => total == 0 ? 0 : Math.Round(100.0 * part / total, 2);

    private static string Clip(string text) => text.Length <= MaxTextLength ? text : text[..(MaxTextLength - 1)] + "…";
}
