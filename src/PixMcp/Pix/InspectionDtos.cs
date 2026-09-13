namespace PixMcp.Pix;

public sealed record RootConstantValueDto(uint Index, uint Value, string Hex);
public sealed record RootConstantsDto(uint ShaderRegister, uint RegisterSpace, uint Num32BitValues,
    IReadOnlyList<RootConstantValueDto> Values, IReadOnlyList<BindingDto> Bindings);

public sealed record ResourceSummaryDto(uint Index, ResourceRef ResourceRef, string ApiObjectId,
    string? Name, string? Type, string Dimension, ulong Width, uint Height, ushort DepthOrArraySize,
    ushort MipLevels, string Format, uint SampleCount, string Flags)
{
    /// <summary>Estimated bytes (see estimateMethod); null for unsized formats.</summary>
    public ulong? EstimatedBytes { get; init; }
    /// <summary>bufferWidth, dimsMipsArraySamples, blockCompressed or unknownFormat.</summary>
    public string? EstimateMethod { get; init; }
    /// <summary>committed, placed, reserved or unknown.</summary>
    public string? HeapKind { get; init; }
    /// <summary>Estimated bytes times the distinct events that read or write the resource in the selection; only with the resource-use index.</summary>
    public ulong? TrafficBytes { get; init; }
}

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
    public int? QueueIndex { get; init; }
    public uint? EventIndex { get; init; }
    public uint? GpuId { get; init; }
    /// <summary>read, write, readWrite, copySrc, copyDst, barrier or unknown.</summary>
    public string? Access { get; init; }
    /// <summary>eventScopedView, capturedApiArgument, barrierArgument or nativeBinding.</summary>
    public string? Evidence { get; init; }
    public string? Stage { get; init; }
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

/// <summary>
/// Sections are typed objects when ready (timing: <see cref="EventTimingInspectionDto"/>, targets: <see cref="EventTargetsDto"/>,
/// counters: <see cref="EventCountersDto"/>, hints: <see cref="InsightDto"/> list) and <see cref="InspectionPendingDto"/> while
/// their preparation still runs; <see cref="Preparation"/> then carries the shared job.
/// </summary>
public sealed record EventInspectionDto(EventRef EventRef, IReadOnlyList<string> MarkerPath, EventDto Event,
    [property: System.ComponentModel.Description("draw, dispatch, executeIndirect, copy, clear, resolve, barrier, present, marker, label or other.")] string Kind,
    [property: System.ComponentModel.Description("The captured API call parsed into named arguments and work items; null for events without call text.")] ApiCallDto? Parameters,
    object? Timing, object? Pipeline, object? Bindings,
    IReadOnlyList<object> Coverage)
{
    public object? Targets { get; init; }
    public object? Counters { get; init; }
    public object? Occupancy { get; init; }
    public object? Hf { get; init; }
    public object? Hints { get; init; }
    public PixMcp.Pix.Handles.ReplayProvenance? Provenance { get; init; }
    public IReadOnlyList<ToolCallDto> NextCalls { get; init; } = [];
    /// <summary>Set on a partial answer: the shared preparation job the replay-dependent sections wait for.</summary>
    public PendingSectionDto? Preparation { get; init; }
}

/// <summary>A section still waiting for the preparation job named in the response's preparation block.</summary>
public sealed record InspectionPendingDto(bool Pending, string JobId);
