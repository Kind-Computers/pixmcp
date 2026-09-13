namespace PixMcp.Pix;

public sealed record RootConstantValueDto(uint Index, uint Value, string Hex);
public sealed record RootConstantsDto(uint ShaderRegister, uint RegisterSpace, uint Num32BitValues,
    IReadOnlyList<RootConstantValueDto> Values, IReadOnlyList<BindingDto> Bindings);

public sealed record ResourceSummaryDto(uint Index, ResourceRef ResourceRef, string ApiObjectId,
    string? Name, string? Type, string Dimension, ulong Width, uint Height, ushort DepthOrArraySize,
    ushort MipLevels, string Format, uint SampleCount, string Flags);

public sealed record ResourceDetailsDto(ResourceRef? ResourceRef, string ApiObjectId, string? Name,
    string Type, string BarrierStateType, object? Desc, object? ClearValue, object? InitialState,
    object? InitialLayout, object? CastableFormats, object? Heap, object? Views)
{
    public IReadOnlyList<ToolCallDto> NextCalls { get; init; } = [];
}

public sealed record ShaderInfoDto(ShaderRef? ShaderRef, int Index, string Id, string Stage, string? Hash,
    string? Entry, string? Target, string? Flags, IReadOnlyDictionary<string, string>? Defines,
    ulong SizeBytes, string Address, IReadOnlyList<string> AvailableCode);

public sealed record PipelineStateDto(EventRef EventRef, IReadOnlyList<string> MarkerPath,
    EventDto Event, string? ProgramType, object? RootSignature, object? Shaders,
    object? GenericPipeline, object? RaytracingPipeline);

public sealed record ResourceGroupDto(object Resource, IReadOnlyList<object> Views);

public sealed record EventResourcesDto(EventRef EventRef, IReadOnlyList<string> MarkerPath, EventDto Event,
    uint ViewCount, int ViewOffset, int Count, int? NextViewOffset,
    IReadOnlyList<ResourceGroupDto> Resources, IReadOnlyList<object> OtherViews)
{
    public IReadOnlyList<ToolCallDto> NextCalls { get; init; } = [];
    public IReadOnlyList<RootConstantsDto> RootConstants { get; init; } = [];
    public IReadOnlyList<object> RootConstantCoverage { get; init; } = [];
}

public sealed record BindingDto(uint Index, string? Type, EventRef? EventRef, IReadOnlyList<string>? MarkerPath,
    object? Detail, bool Unavailable = false, string? Reason = null)
{
    public string? EventSource { get; init; }
}

public sealed record ResourceUseDto(ResourceRef ResourceRef, uint? ViewIndex, string ViewType, BindingDto Binding)
{
    public string ViewIndexScope { get; init; } = "resource";
}

internal sealed record ResourceUsesSnapshot(IReadOnlyList<ResourceUseDto> Items, IReadOnlyList<object> Coverage, string Evidence);

public sealed record ResourceUsesDto(ResourceRef ResourceRef, string Evidence, long Total, int Offset,
    int Count, int? NextOffset, IReadOnlyList<ResourceUseDto> Items, IReadOnlyList<object> Coverage)
{
    public IReadOnlyList<ToolCallDto> NextCalls { get; init; } = [];
    public ScopeDescriptionDto? Scope { get; init; }
}

public sealed record ShaderNodeDto(ulong Index, string? Id, string? Name);

public sealed record ShaderCodeDto(ShaderRef ShaderRef, ShaderInfoDto Shader, string CodeType,
    PageResult<ShaderNodeDto> Nodes, int? NodeIndex, int StartLine, int Count, int TotalLines,
    int? NextStartLine, string? Code)
{
    public IReadOnlyList<ToolCallDto> NextCalls { get; init; } = [];
}

public sealed record ShaderSearchMatchDto(ulong NodeIndex, string? NodeName, int Line,
    int StartLine, IReadOnlyList<string> Lines);

public sealed record ShaderSearchDto(ShaderRef ShaderRef, string CodeType, string Query,
    bool CaseSensitive, long Total, int Offset, int Count, int? NextOffset,
    IReadOnlyList<ShaderSearchMatchDto> Items, IReadOnlyList<object> Coverage)
{
    public IReadOnlyList<ToolCallDto> NextCalls { get; init; } = [];
}

/// <summary>Sections are typed objects when ready and a <see cref="PendingSectionDto"/> while their preparation still runs.</summary>
public sealed record EventInspectionDto(EventRef EventRef, IReadOnlyList<string> MarkerPath, EventDto Event,
    object? Timing, object? Pipeline, object? Bindings,
    IReadOnlyList<object> Coverage)
{
    public IReadOnlyList<ToolCallDto> NextCalls { get; init; } = [];
    /// <summary>Set on a partial answer: the shared preparation job the replay-dependent sections wait for.</summary>
    public PendingSectionDto? Preparation { get; init; }
}
