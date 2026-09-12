namespace PixMcp.Pix;

public sealed record ShaderPdbMetadataDto(string State, string? Hash, string? Reason = null,
    string? ErrorCode = null, string? Hresult = null);

public sealed record ShaderCodeAvailabilityDto(string CodeType, string State, ulong? NodeCount,
    string? Reason = null, string? ErrorCode = null, string? Hresult = null);

public sealed record ShaderDiagnosticsDto(ShaderRef ShaderRef, string Id, string Stage,
    ShaderPdbMetadataDto Pdb, IReadOnlyList<ShaderCodeAvailabilityDto> CodeAvailability,
    IReadOnlyList<string> Guidance, IReadOnlyList<ToolCallDto> NextCalls);
