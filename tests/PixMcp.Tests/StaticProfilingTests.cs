using System.Text;
using PixMcp.Pix;
using PixMcp.Pix.StaticProfiling;
using Xunit;

namespace PixMcp.Tests;

public sealed class StaticProfilingTests
{
    private const uint None = StaticResultData.InvalidId;

    private static IReadOnlyList<(uint Id, IReadOnlyList<uint> Successors)> Graph(params (uint Id, uint[] Successors)[] blocks)
        => blocks.Select(b => (b.Id, (IReadOnlyList<uint>)b.Successors)).ToArray();

    private static ShaderTargetDto Target(uint id, string vendor, string name, string family, uint familyId)
        => new(id.ToString(System.Globalization.CultureInfo.InvariantCulture), id, vendor, name, null, family, familyId, StaticTargets.Architecture(name, family));

    private static StaticResultContext Context(ShaderTargetDto target, int topN)
        => new("inline", target, topN, false, false, new StaticDefinesFormatDto(StaticSourcePolicy.DefinesFormat, true), [], []);

    [Fact]
    public void ControlFlowFindsNaturalLoopsNestingAndIrreducibleRegions()
    {
        ControlFlowAnalysis linear = ControlFlow.Analyze(Graph((0, [1]), (1, [2]), (2, [])));
        Assert.Empty(linear.Loops);
        Assert.False(linear.Irreducible);

        ControlFlowAnalysis single = ControlFlow.Analyze(Graph((0, [1]), (1, [2, 3]), (2, [1]), (3, [])));
        ControlFlowLoop loop = Assert.Single(single.Loops);
        Assert.Equal(1u, loop.Header);
        Assert.Equal(new uint[] { 1, 2 }, loop.Blocks.Order());
        Assert.Equal(1, single.LoopDepth[2]);
        Assert.Equal(0, single.LoopDepth[3]);

        ControlFlowAnalysis nested = ControlFlow.Analyze(Graph((0, [1]), (1, [2, 5]), (2, [3, 4]), (3, [2]), (4, [1]), (5, [])));
        Assert.Equal(2, nested.Loops.Count);
        Assert.Equal(2, nested.LoopDepth[3]);
        Assert.Equal(1, nested.LoopDepth[4]);
        Assert.Equal(0, nested.LoopDepth[5]);

        ControlFlowAnalysis self = ControlFlow.Analyze(Graph((0, [0, 1]), (1, [])));
        Assert.Equal(0u, Assert.Single(self.Loops).Header);
        Assert.Equal(1, self.LoopDepth[0]);

        ControlFlowAnalysis irreducible = ControlFlow.Analyze(Graph((0, [1, 2]), (1, [2]), (2, [1])));
        Assert.True(irreducible.Irreducible);
        Assert.Empty(irreducible.Loops);

        ControlFlowAnalysis unreachable = ControlFlow.Analyze(Graph((0, [99]), (7, [7])));
        Assert.Empty(unreachable.Loops);
        Assert.DoesNotContain(7u, unreachable.Reachable);
        Assert.Empty(ControlFlow.Analyze([]).Loops);
    }

    private static StaticResultData IntelLike()
    {
        StaticInstructionTypeInfo[] types =
        [
            new(0, 0, "mov", null, 2),
            new(1, 0, "add", null, 3),
            new(2, 1, "jmpi", null, 4),
            new(3, 7, "sync", null, StaticInstructionTypeInfo.Variable),
        ];
        static StaticInstructionData Instruction(uint id, uint type, uint location, uint[] live, params StaticComment[] comments)
            => new(id, type, $"op{id}", id * 4, 4, location, null, [0], [1], live, [], [], comments);
        var entry = new StaticBlockData(0, "entry", [1], [0], [], [], [Instruction(0, 0, 0, [0]), Instruction(1, 3, None, [0, 1])]);
        var body = new StaticBlockData(1, "loop", [1, 2], [1, 0], [], [], [Instruction(2, 1, 1, [0, 1, 2]), Instruction(3, 2, 2, [0, 1], new StaticComment("warning", "slow path"))]);
        var exit = new StaticBlockData(2, "exit", [], [], [], [], [Instruction(4, 0, None, [])]);
        var shader = new StaticShaderData(0, "COMPUTE", 0, null, true, [new StaticInfoDto("Notes", "a\n  b")],
            [new StaticNamedItem(0, "main.hlsl")],
            [new StaticSourceFunctionInfo(0, "main", 0, 0, 20), new StaticSourceFunctionInfo(1, "helper", 0, 30, 40)],
            [new StaticSourceLocationInfo(0, 0, 4, 4, 0, 10, 0, None), new StaticSourceLocationInfo(1, 0, 32, 32, 2, 8, 1, 2), new StaticSourceLocationInfo(2, 0, 9, 9, 4, 20, 0, None)],
            [new StaticRegisterInfo(0, "r0", 0, 0), new StaticRegisterInfo(1, "r1", 0, 1), new StaticRegisterInfo(2, "f0", 1, 0)],
            new Dictionary<uint, uint> { [0] = 2, [1] = 1 }, [entry, body, exit]);
        return new StaticResultData("INTEL", "Arc", "Battlemage", "dxcompiler.dll", "igd12umd64.dll", "Link",
            [new StaticNamedItem(0, "Arithmetic"), new StaticNamedItem(1, "Flow control")], types,
            [new StaticRegisterTypeInfo(0, "General Purpose", "r"), new StaticRegisterTypeInfo(1, "Flag", "f")],
            [new StaticNamedItem(0, "Conditional"), new StaticNamedItem(1, "Jump")], [new StaticNamedItem(0, "Register", "value flow")], [shader]);
    }

    [Fact]
    public void DescriberSummarisesMixPressureLoopsHotSpotsAndSources()
    {
        StaticProfileDto dto = StaticProfileDescriber.Build(new StaticCompileOutcome(true, null, [], IntelLike(), 42.04), Context(Target(0, "intel", "Arc", "Battlemage family", 0), 2), ["note"]);
        Assert.True(dto.Succeeded);
        Assert.Null(dto.Target.IsaDocumentationLink);
        Assert.Equal(42.0, dto.ElapsedMs);
        StaticShaderSummaryDto shader = Assert.Single(dto.Shaders);
        Assert.Equal(5, shader.InstructionCount);
        Assert.Equal(100, shader.InstructionMix.ByCategory.Sum(c => c.Percent), 1);
        Assert.Equal("Arithmetic", shader.InstructionMix.ByCategory[0].Name);
        Assert.Equal(3, shader.InstructionMix.ByCategory[0].Count);
        Assert.Contains(shader.InstructionMix.ByCategory, c => c.Name == "uncategorized" && c.Count == 1);
        Assert.Equal(11UL, shader.InstructionMix.EstimatedFixedCycles);
        Assert.Equal(1, shader.InstructionMix.VariableRateInstructions);
        Assert.Null(shader.InstructionMix.TopTypes.Single(t => t.Name == "sync").Cycles);

        StaticRegisterPressureDto general = shader.RegisterPressure.Single(p => p.Type == "General Purpose");
        Assert.Equal(2u, general.Allocated);
        Assert.Equal(2, general.PeakLive);
        Assert.Equal(1.4, general.AverageLive);
        StaticRegisterPressureDto flag = shader.RegisterPressure.Single(p => p.Type == "Flag");
        Assert.Equal(1, flag.PeakLive);
        Assert.Equal(0.2, flag.AverageLive);
        Assert.Equal(3u, shader.TotalRegistersAllocated);

        Assert.Equal(1, shader.ControlFlow.LoopCount);
        Assert.Equal(1, shader.ControlFlow.MaxLoopDepth);
        Assert.Equal(new uint[] { 3, 2 }, shader.HotSpots.Select(h => h.InstructionId));
        Assert.True(shader.HotSpotsTruncated);
        Assert.Equal(40, shader.HotSpots[0].Weight);
        Assert.Equal(1, shader.HotSpots[0].LoopDepth);
        StaticSourceLocationDto inlined = shader.HotSpots[1].Source!;
        Assert.Equal("main.hlsl", inlined.File);
        Assert.Equal(33u, inlined.Line);
        Assert.Equal(3u, inlined.Column);
        Assert.Equal("helper", inlined.Function);
        Assert.Equal(new[] { "main (main.hlsl:10)" }, inlined.InlinedFrom);
        Assert.Equal(new uint[] { 10, 33 }, shader.SourceLines.Select(l => l.Line));
        Assert.Equal("main", shader.SourceLines[0].Function);
        Assert.Equal(new[] { "warning: slow path" }, shader.Warnings);
        Assert.Equal("a b", Assert.Single(shader.Info).Value);
        Assert.Null(shader.CompiledHash);
        Assert.Equal("main", shader.Entry);
        Assert.True(dto.Coverage.SourceMappingAvailable);
        Assert.Contains("note", dto.Coverage.Notes);

        StaticShaderDetailDto detail = Assert.Single(dto.Detail!);
        Assert.Equal("/detail/0", shader.DetailPointer);
        Assert.Equal(3, detail.Blocks.Count);
        StaticInstructionDetailDto first = detail.Blocks[0].Instructions[0];
        Assert.Equal("mov", first.Type);
        Assert.Equal(new[] { "r0" }, first.Read);
        Assert.Equal(new[] { "r1" }, first.Written);
        Assert.Equal(new[] { "Jump", "Conditional" }, detail.Blocks[1].SuccessorBranchTypes);
        Assert.Equal(1, detail.Blocks[1].LoopDepth);
        Assert.Equal(new[] { "Register: value flow" }, dto.Legend!.DependencyTypes);
        Assert.InRange(Encoding.UTF8.GetByteCount(Json.Serialize(dto with { Detail = null })), 1, 12_000);
    }

    [Fact]
    public void VendorsWithoutCategoriesOrSourceMappingStillSummarise()
    {
        StaticInstructionTypeInfo[] types = [new(0, None, "SALU", null, 0), new(1, None, "BRANCH", null, 0)];
        var block = new StaticBlockData(0, null, [], [], [], [],
        [
            new StaticInstructionData(0, 0, "s_add", 0, 4, None, "exec != 0", [], [], [], [], [], []),
            new StaticInstructionData(1, 1, "s_endpgm", 4, 4, None, null, [], [], [], [], [], []),
        ]);
        var shader = new StaticShaderData(0, "COMPUTE", None, "93a9be82", false,
            [new StaticInfoDto("Resource usage", "\nVGPRs: 7/12 \nSGPRs: 12/106\nLDS: 0/64kB\nScratch: 0kB; "), new StaticInfoDto("VGPR allocation granularity", "12")],
            [], [], [], [], new Dictionary<uint, uint> { [1] = 12 }, [block]);
        var data = new StaticResultData("AMD", "AMD Radeon RX 9070 XT", "gfx1201 (RDNA4)", null, null, "https://gpuopen.com/machine-readable-isa/", [], types,
            [new StaticRegisterTypeInfo(0, "VGPR", "v"), new StaticRegisterTypeInfo(1, "SGPR", "s")], [], [], [shader]);
        StaticProfileDto dto = StaticProfileDescriber.Build(new StaticCompileOutcome(true, null, [], data, 300),
            Context(Target(58, "amd", "AMD Radeon RX 9070 XT", "gfx1201 (RDNA4)", 12), 25), []);

        StaticShaderSummaryDto summary = Assert.Single(dto.Shaders);
        StaticCountDto category = Assert.Single(summary.InstructionMix.ByCategory);
        Assert.Equal("uncategorized", category.Name);
        Assert.Equal(100.0, category.Percent);
        Assert.False(dto.Coverage.SourceMappingAvailable);
        Assert.Empty(summary.SourceLines);
        Assert.Equal("93a9be82", summary.CompiledHash);
        Assert.Equal("RDNA4", dto.Target.Architecture);
        Assert.Equal("https://gpuopen.com/machine-readable-isa/", dto.Target.IsaDocumentationLink);
        StaticResourceUsageDto usage = summary.ResourceUsage!;
        Assert.Equal(new[] { "VGPRs", "SGPRs", "LDS", "Scratch" }, usage.Entries.Select(e => e.Name));
        Assert.Equal(7.0, usage.Entries[0].Used);
        Assert.Equal(12.0, usage.Entries[0].Limit);
        Assert.Equal("kB", usage.Entries[2].Unit);
        Assert.Equal(64.0, usage.Entries[2].Limit);
        Assert.Null(usage.Entries[3].Limit);
        StaticRegisterPressureDto sgpr = Assert.Single(summary.RegisterPressure);
        Assert.Equal("SGPR", sgpr.Type);
        Assert.Equal(12u, sgpr.Allocated);
    }

    [Fact]
    public void FailedCompilationsBecomeDataWithHints()
    {
        var outcome = new StaticCompileOutcome(false, "compile",
            [new CompilerMessageDto("UNKNOWN", "Shader UAV descriptor range (BaseShaderRegister=0, NumDescriptors=1, RegisterSpace=0) is not fully bound in root signature")], null, 12.5);
        StaticProfileDto dto = StaticProfileDescriber.Build(outcome, Context(Target(0, "intel", "Arc", "Battlemage family", 0), 5), []);
        Assert.False(dto.Succeeded);
        Assert.Equal("compile", dto.Phase);
        Assert.Empty(dto.Shaders);
        Assert.Null(dto.Detail);
        Assert.Contains(dto.Hints, h => h.Contains("[RootSignature", StringComparison.Ordinal));
        Assert.Equal("Arc", dto.Target.AdapterName);

        Assert.Contains(StaticSourcePolicy.Hints(["neither VS, CS nor MS specified"], false), h => h.Contains("vertex shader", StringComparison.Ordinal));
        Assert.Contains(StaticSourcePolicy.Hints(["No Root Signature found in the pipeline desc or in any bound Shader bytecode"], false), h => h.Contains("[RootSignature", StringComparison.Ordinal));
        Assert.Contains(StaticSourcePolicy.Hints(["probe.hlsl:1:35: error: use of undeclared identifier 'x'"], false), h => h.Contains("file:line:column", StringComparison.Ordinal));
        Assert.Empty(StaticSourcePolicy.Hints(["all good"], false));
    }

    [Fact]
    public void DefinesAreFormattedAsCompilerArgumentsAndValidated()
    {
        Assert.True(StaticSourcePolicy.TryFormatDefines(["HEAVY", "ITERATIONS=8"], out string text, out _));
        Assert.Equal("-DHEAVY -DITERATIONS=8", text);
        Assert.False(StaticSourcePolicy.TryFormatDefines(["1BAD"], out _, out string? badName));
        Assert.Contains("1BAD", badName);
        Assert.False(StaticSourcePolicy.TryFormatDefines(["NAME=a b"], out _, out string? badValue));
        Assert.Contains("spaces", badValue);
        Assert.Equal("-DA=1 -DB=", StaticSourcePolicy.FormatCapturedDefines(new Dictionary<string, string> { ["A"] = "1", ["B"] = "", ["bad name"] = "x" }, out IReadOnlyList<string> skipped));
        Assert.Equal(new[] { "bad name" }, skipped);
    }

    private static string Problem(IReadOnlyList<StaticShaderSource> sources, StaticPipelineOptions? pipeline)
    {
        Assert.Null(StaticSourcePolicy.Plan(sources, pipeline, out string? problem));
        return problem!;
    }

    [Fact]
    public void InlinePlansInferStagesPipelineTypeAndRootSignaturePolicy()
    {
        StaticInlinePlan? compute = StaticSourcePolicy.Plan(
            [new StaticShaderSource("CS_6_0", Hlsl: "[RootSignature(\"UAV(u0)\")] [numthreads(1,1,1)] void main() {}", Defines: ["HEAVY"])], null, out string? problem);
        Assert.Null(problem);
        Assert.Equal("compute", compute!.PipelineType);
        Assert.True(compute.DropRootSignature);
        StaticPlannedSource source = Assert.Single(compute.Sources);
        Assert.Equal(("compute", "cs_6_0", "main", "-DHEAVY"), (source.Stage, source.Target, source.Entry, source.Defines));
        Assert.Equal("compute.hlsl", Assert.Single(source.Files).Path);

        StaticInlinePlan? graphics = StaticSourcePolicy.Plan([new StaticShaderSource("vs_6_0", Hlsl: "v"), new StaticShaderSource("ps_6_0", Entry: "PSMain", Hlsl: "p")],
            new StaticPipelineOptions(RenderTargetFormats: ["r16g16b16a16_float"], SampleCount: 4), out problem);
        Assert.Null(problem);
        Assert.Equal("graphics", graphics!.PipelineType);
        Assert.False(graphics.DropRootSignature);
        Assert.Equal(new[] { "R16G16B16A16_FLOAT" }, graphics.RenderTargetFormats);
        Assert.Equal("triangle", graphics.Topology);
        Assert.Equal(4u, graphics.SampleCount);
        Assert.Equal("PSMain", graphics.Sources[1].Entry);

        Assert.Contains("vertex shader", Problem([new StaticShaderSource("ps_6_0", Hlsl: "p")], null));
        Assert.Contains("profile", Problem([new StaticShaderSource("lib_6_3", Hlsl: "x")], null));
        Assert.Contains("exactly one of hlsl or files", Problem([new StaticShaderSource("cs_6_0")], null));
        Assert.Contains("twice", Problem([new StaticShaderSource("cs_6_0", Hlsl: "a"), new StaticShaderSource("cs_6_0", Hlsl: "b")], null));
        Assert.Contains("pipeline.type", Problem([new StaticShaderSource("cs_6_0", Hlsl: "a")], new StaticPipelineOptions(Type: "raytracing")));
        Assert.Contains("1 to 5", Problem([], null));
        Assert.Contains("topology", Problem([new StaticShaderSource("vs_6_0", Hlsl: "v")], new StaticPipelineOptions(Topology: "strip")));
        Assert.Contains("sampleCount", Problem([new StaticShaderSource("vs_6_0", Hlsl: "v")], new StaticPipelineOptions(SampleCount: 64)));
        Assert.Contains("function name", Problem([new StaticShaderSource("cs_6_0", Entry: "main()", Hlsl: "a")], null));

        Assert.Equal("pixel", StaticSourcePolicy.StageOf("PS_6_0"));
        Assert.Null(StaticSourcePolicy.StageOf("lib_6_3"));
        Assert.Equal(Path.Combine("src", "common", "light.hlsli"), StaticSourcePolicy.SafeRelativePath(@"C:\src\..\common\light.hlsli"));
        Assert.Equal("source.hlsl", StaticSourcePolicy.SafeRelativePath(".."));
    }

    [Fact]
    public void TargetsResolveByIdNameFamilyOrArchitectureAndReportAmbiguity()
    {
        ShaderTargetDto[] targets =
        [
            Target(0, "intel", "Intel(R) Arc Graphics family (Codename Battlemage)", "Intel(R) Arc Graphics family (Codename Battlemage)", 0),
            Target(1, "intel", "Intel(R) Core Ultra processor graphics family (Codename Lunar Lake)", "Intel(R) Core Ultra processor graphics family (Codename Lunar Lake)", 1),
            Target(2, "intel", "Intel(R) Core Ultra processor graphics family (Codename Panther Lake)", "Intel(R) Core Ultra processor graphics family (Codename Panther Lake)", 2),
            Target(12, "amd", "AMD Radeon(TM) Graphics", "gfx1100 (RDNA3)", 0),
            Target(20, "amd", "AMD Radeon(TM) Graphics", "gfx1101 (RDNA3)", 1),
            Target(55, "amd", "AMD Radeon AI PRO R9700", "gfx1201 (RDNA4)", 12),
            Target(58, "amd", "AMD Radeon RX 9070 XT", "gfx1201 (RDNA4)", 12),
        ];
        Assert.Equal("Xe2-HPG", targets[0].Architecture);
        Assert.Equal("Xe2-LPG", targets[1].Architecture);
        Assert.Equal("Xe3-LPG", targets[2].Architecture);
        Assert.Equal("RDNA4", targets[6].Architecture);

        Assert.Equal(58u, StaticTargets.Resolve(targets, "58").Target!.Id);
        Assert.Equal(0u, StaticTargets.Resolve(targets, "battlemage").Target!.Id);
        Assert.Equal(2u, StaticTargets.Resolve(targets, "xe3-lpg").Target!.Id);
        Assert.Equal(55u, StaticTargets.Resolve(targets, "gfx1201").Target!.Id);
        Assert.Equal(58u, StaticTargets.Resolve(targets, "amd radeon rx 9070 xt").Target!.Id);
        StaticTargetMatch xe2 = StaticTargets.Resolve(targets, "Xe2");
        Assert.Null(xe2.Target);
        Assert.Equal(new uint[] { 0, 1 }, xe2.Candidates.Select(c => c.Id));
        StaticTargetMatch shared = StaticTargets.Resolve(targets, "AMD Radeon(TM) Graphics");
        Assert.Null(shared.Target);
        Assert.Equal(new uint[] { 12, 20 }, shared.Candidates.Select(c => c.Id));
        Assert.Contains("AMD and Intel", StaticTargets.Resolve(targets, "nvidia").Problem);
        Assert.Contains("No shader target has id", StaticTargets.Resolve(targets, "99").Problem);
        Assert.Equal(3, StaticTargets.Resolve(targets, "intel").Candidates.Count);
        Assert.Contains("required", StaticTargets.Resolve(targets, " ").Problem);

        ShaderTargetsExtraDto extra = StaticTargets.Extra(targets, null, "2606.18-preview");
        Assert.Equal(new[] { "amd", "intel" }, extra.Vendors);
        Assert.Equal("Xe2-HPG", extra.Families[0].ExampleTarget);
        Assert.Equal("gfx1100", extra.Families.Single(f => f.Family == "gfx1100 (RDNA3)").ExampleTarget);
        Assert.Equal(2, extra.Families.Single(f => f.Family == "gfx1201 (RDNA4)").Adapters);
        Assert.Equal(2, extra.ExampleCalls.Count);
        Assert.Empty(extra.OfflineCompilers);
        Assert.Equal(new[] { 0u, 1u, 2u }, StaticTargets.Filter(targets, "INTEL", null).Select(t => t.Id));
        Assert.Equal(new[] { 55u, 58u }, StaticTargets.Filter(targets, null, "rdna4").Select(t => t.Id));

        Assert.True(StaticTargets.IsPlaceholderHash([0xE7, 0, 0, 0]));
        Assert.True(StaticTargets.IsPlaceholderHash([]));
        Assert.False(StaticTargets.IsPlaceholderHash([0x93, 0xA9, 0xBE]));
        Assert.Null(StaticTargets.DocumentationLink("Link"));
        Assert.Equal("https://gpuopen.com/machine-readable-isa/", StaticTargets.DocumentationLink("https://gpuopen.com/machine-readable-isa/"));
    }
}
