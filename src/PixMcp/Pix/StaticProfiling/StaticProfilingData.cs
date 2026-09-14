using System.ComponentModel;

namespace PixMcp.Pix.StaticProfiling;

// Snapshot of an IPixShaderProfilingStaticResult: plain data copied on the PIX worker, described off it (and in unit tests).

public sealed record StaticNamedItem(uint Id, string Name, string? Description = null);

public sealed record StaticInstructionTypeInfo(uint Id, uint Category, string Name, string? Description, uint Cycles)
{
    /// <summary>PIX_SHADER_PROFILING_INSTRUCTION_RATE_VARIABLE: the type has no fixed cycle count.</summary>
    public const uint Variable = uint.MaxValue;
    public bool IsVariable => Cycles == Variable;
}

public sealed record StaticRegisterTypeInfo(uint Id, string Name, string? Prefix);

public sealed record StaticRegisterInfo(uint Id, string Name, uint Type, uint Ordinal);

public sealed record StaticSourceFunctionInfo(uint Id, string Name, uint FileId, uint Line, uint EndLine);

/// <summary>Lines and columns are 0-based as PIX reports them; <c>CallingLocationId</c> is the inlining parent or <see cref="StaticResultData.InvalidId"/>.</summary>
public sealed record StaticSourceLocationInfo(uint Id, uint FileId, uint Line, uint EndLine, uint Column, uint EndColumn, uint FunctionId, uint CallingLocationId);

public sealed record StaticDependency(uint Type, uint Instruction);

/// <summary>Severity is info, warning or error.</summary>
public sealed record StaticComment(string Severity, string Text);

public sealed record StaticInstructionData(uint Id, uint Type, string Text, uint OffsetBytes, uint SizeBytes, uint SourceLocation, string? Predicate,
    IReadOnlyList<uint> Read, IReadOnlyList<uint> Written, IReadOnlyList<uint> Live, IReadOnlyList<uint> Freed,
    IReadOnlyList<StaticDependency> Dependencies, IReadOnlyList<StaticComment> Comments);

public sealed record StaticBlockData(uint Id, string? Name, IReadOnlyList<uint> Successors, IReadOnlyList<uint> SuccessorBranchTypes,
    IReadOnlyList<uint> LiveIn, IReadOnlyList<uint> LiveOut, IReadOnlyList<StaticInstructionData> Instructions);

/// <summary>One compiled shader. <c>Stage</c> uses the capture tools' spelling (COMPUTE, PIXEL, ...); <c>Hash</c> is null when the vendor reports a placeholder.</summary>
public sealed record StaticShaderData(uint Id, string Stage, uint EntryFunction, string? Hash, bool HashIsPlaceholder,
    IReadOnlyList<StaticInfoDto> Infos, IReadOnlyList<StaticNamedItem> Files, IReadOnlyList<StaticSourceFunctionInfo> Functions,
    IReadOnlyList<StaticSourceLocationInfo> Locations, IReadOnlyList<StaticRegisterInfo> Registers, IReadOnlyDictionary<uint, uint> AllocatedByType,
    IReadOnlyList<StaticBlockData> Blocks);

public sealed record StaticResultData(string Vendor, string AdapterName, string Family, string? FrontendCompiler, string? BackendCompiler, string? IsaDocumentationLink,
    IReadOnlyList<StaticNamedItem> Categories, IReadOnlyList<StaticInstructionTypeInfo> InstructionTypes, IReadOnlyList<StaticRegisterTypeInfo> RegisterTypes,
    IReadOnlyList<StaticNamedItem> BranchTypes, IReadOnlyList<StaticNamedItem> DependencyTypes, IReadOnlyList<StaticShaderData> Shaders)
{
    /// <summary>PIX_SHADER_PROFILING_INVALID_ID.</summary>
    public const uint InvalidId = uint.MaxValue;
}

// Tool inputs.

public sealed record StaticShaderSource(
    [property: Description("HLSL target profile such as cs_6_0, vs_6_6 or ps_6_0; the stage comes from its prefix (vs, ps, gs, hs, ds, cs, ms, as).")] string Target,
    [property: Description("Entry point function name (default main).")] string? Entry = null,
    [property: Description("Complete HLSL of the main file. Exactly one of hlsl or files.")] string? Hlsl = null,
    [property: Description("Source files with the main file first; later files satisfy #include by file name. Exactly one of hlsl or files.")] IReadOnlyList<StaticSourceFile>? Files = null,
    [property: Description("Preprocessor defines as NAME or NAME=VALUE; values cannot contain spaces or quotes (default none).")] IReadOnlyList<string>? Defines = null,
    [property: Description("Extra DXC compiler arguments such as -HV 2021 (default none).")] string? Flags = null);

public sealed record StaticSourceFile(
    [property: Description("File name used by #include and in reported source locations, e.g. lighting.hlsli.")] string Path,
    [property: Description("Complete file contents.")] string Content);

public sealed record StaticPipelineOptions(
    [property: Description("Pipeline type: compute, graphics or mesh (default inferred from the sources' stages).")] string? Type = null,
    [property: Description("Render target formats as DXGI names without the DXGI_FORMAT_ prefix, one per SV_Target (default [R8G8B8A8_UNORM] for graphics and mesh).")] IReadOnlyList<string>? RenderTargetFormats = null,
    [property: Description("Depth stencil format such as D32_FLOAT (default none).")] string? DepthStencilFormat = null,
    [property: Description("MSAA sample count, 1 to 32 (default 1).")] uint? SampleCount = null,
    [property: Description("Primitive topology type: point, line, triangle or patch (default triangle).")] string? Topology = null);

// Tool outputs.

public sealed record ShaderTargetDto(string TargetRef, uint Id, string Vendor, string Name, string? Description, string Family, uint FamilyId, string? Architecture);

public sealed record ShaderTargetFamilyDto(string Vendor, string Family, string? Architecture, int Adapters, string ExampleTarget);

public sealed record OfflineCompilerDto(string Vendor, string File, string? FileVersion);

public sealed record ShaderTargetsExtraDto(IReadOnlyList<string> Vendors, IReadOnlyList<ShaderTargetFamilyDto> Families, IReadOnlyList<OfflineCompilerDto> OfflineCompilers,
    string? PixVersion, string Note, IReadOnlyDictionary<string, IReadOnlyList<string>> KnownIssues, IReadOnlyList<ToolCallDto> ExampleCalls);

public sealed record StaticTargetInfoDto(string TargetRef, string Vendor, string AdapterName, string Family, string? Architecture, string? FrontendCompiler, string? BackendCompiler, string? IsaDocumentationLink);

public sealed record CompilerMessageDto(string Stage, string Text);

public sealed record StaticInfoDto(string Name, string Value);

public sealed record StaticRegisterPressureDto(string Type, string? Prefix, uint Allocated, int PeakLive, double AverageLive);

public sealed record StaticCountDto(string Name, int Count, double Percent, uint? Cycles = null);

public sealed record StaticInstructionMixDto(int Count, IReadOnlyList<StaticCountDto> ByCategory, IReadOnlyList<StaticCountDto> TopTypes, ulong EstimatedFixedCycles, int VariableRateInstructions);

public sealed record StaticLoopDto(uint HeaderBlock, int Blocks, int Depth, int Instructions);

public sealed record StaticControlFlowDto(int BlockCount, int LoopCount, int MaxLoopDepth, bool Irreducible, IReadOnlyList<StaticLoopDto> Loops);

public sealed record StaticSourceLocationDto(string? File, uint Line, uint Column, string? Function, IReadOnlyList<string>? InlinedFrom);

public sealed record StaticHotSpotDto(uint InstructionId, uint Block, string Type, string Text, uint? Cycles, int LoopDepth, double Weight, StaticSourceLocationDto? Source);

public sealed record StaticSourceLineDto(string? File, uint Line, string? Function, int Instructions, double Weight, ulong FixedCycles);

public sealed record StaticUsageEntryDto(string Name, double Used, double? Limit, string? Unit);

public sealed record StaticResourceUsageDto(string Source, IReadOnlyList<StaticUsageEntryDto> Entries, string Note);

public sealed record StaticShaderSummaryDto(int Index, string Stage, string? CompiledHash, ShaderRef? ShaderRef, string? Entry, int InstructionCount,
    IReadOnlyList<StaticInfoDto> Info, IReadOnlyList<StaticRegisterPressureDto> RegisterPressure, uint TotalRegistersAllocated,
    StaticInstructionMixDto InstructionMix, StaticControlFlowDto ControlFlow, IReadOnlyList<StaticHotSpotDto> HotSpots, bool HotSpotsTruncated,
    IReadOnlyList<StaticSourceLineDto> SourceLines, IReadOnlyList<string> Warnings, StaticResourceUsageDto? ResourceUsage, string DetailPointer);

public sealed record StaticLegendDto(IReadOnlyList<StaticNamedItem> Categories, IReadOnlyList<StaticRegisterTypeInfo> RegisterTypes, IReadOnlyList<string> BranchTypes,
    IReadOnlyList<string> DependencyTypes, string Weight, string Lines);

public sealed record StaticInstructionDetailDto(uint Id, string Type, string Text, uint OffsetBytes, uint SizeBytes, string? Predicate, uint? Cycles,
    IReadOnlyList<string> Read, IReadOnlyList<string> Written, int Live, IReadOnlyList<string> Freed, IReadOnlyList<string>? Dependencies,
    IReadOnlyList<string>? Comments, StaticSourceLocationDto? Source);

public sealed record StaticBlockDetailDto(uint Id, string? Name, IReadOnlyList<uint> Successors, IReadOnlyList<string> SuccessorBranchTypes, int LoopDepth,
    IReadOnlyList<StaticInstructionDetailDto> Instructions);

public sealed record StaticShaderDetailDto(int Index, string Stage, IReadOnlyList<StaticRegisterInfo> Registers, IReadOnlyList<StaticBlockDetailDto> Blocks);

public sealed record StaticDefinesFormatDto(string Format, bool Verified);

public sealed record StaticCoverageDto(bool SourceMappingAvailable, bool PipelineStateFromCapture, bool RootSignatureFromSource, IReadOnlyList<string> ShadersWithoutSource,
    StaticDefinesFormatDto DefinesFormat, IReadOnlyList<string> Notes);

/// <summary>The job result of pix_gpu_shader_static_profile. A failed preprocess or compile is data: <c>Succeeded</c> false with the compiler output.</summary>
public sealed record StaticProfileDto(bool Succeeded, string Source, string? Phase, StaticTargetInfoDto Target, IReadOnlyList<CompilerMessageDto> CompilerOutput,
    IReadOnlyList<string> Hints, IReadOnlyList<StaticShaderSummaryDto> Shaders, StaticLegendDto? Legend, StaticCoverageDto Coverage, IReadOnlyList<ToolCallDto> NextCalls)
{
    public double? ElapsedMs { get; init; }
    public object? Provenance { get; init; }
    /// <summary>Every block and instruction per shader, addressed by each summary's detailPointer.</summary>
    public IReadOnlyList<StaticShaderDetailDto>? Detail { get; init; }
}
