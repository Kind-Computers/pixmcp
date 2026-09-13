namespace PixMcp.Pix;

public sealed record ShaderInventoryItemDto(ShaderInfoDto Shader, int UseCount, IReadOnlyList<ToolCallDto> NextCalls);
public sealed record ShaderInventoryDto(string Handle, long Total, int Offset, int Count, int? NextOffset,
    IReadOnlyList<ShaderInventoryItemDto> Items, IReadOnlyList<object> Coverage, IReadOnlyList<ToolCallDto> NextCalls)
{
    public ScopeDescriptionDto? Scope { get; init; }
}
public sealed record ShaderUseEventDto(EventRef EventRef, IReadOnlyList<string> MarkerPath, IReadOnlyList<ShaderRef> ShaderRefs);
public sealed record ShaderUsesDto(ShaderRef ShaderRef, string MatchMethod, long Total, int Offset, int Count, int? NextOffset,
    IReadOnlyList<ShaderUseEventDto> Items, IReadOnlyList<object> Coverage, IReadOnlyList<ToolCallDto> NextCalls)
{
    public ScopeDescriptionDto? Scope { get; init; }
}

internal sealed record ShaderOccurrence(ShaderInfoDto Shader, IReadOnlyList<string> MarkerPath);

/// <summary>Capture-local metadata only. Names and missing hashes never establish shader identity.</summary>
internal sealed class ShaderIndex
{
    private readonly ShaderOccurrence[] _occurrences;
    private readonly IReadOnlyDictionary<ShaderRef, ShaderOccurrence> _byReference;
    internal IReadOnlyList<object> Coverage { get; }

    internal ShaderIndex(IEnumerable<ShaderOccurrence> occurrences, IReadOnlyList<object> coverage)
    {
        _occurrences = occurrences.Where(o => o.Shader.ShaderRef is not null)
            .OrderBy(o => o.Shader.ShaderRef!.EventRef.QueueIndex)
            .ThenBy(o => o.Shader.ShaderRef!.EventRef.EventIndex).ThenBy(o => o.Shader.ShaderRef!.ShaderIndex).ToArray();
        _byReference = _occurrences.ToDictionary(o => o.Shader.ShaderRef!);
        Coverage = coverage;
    }

    private static string Identity(ShaderInfoDto shader)
        => string.IsNullOrEmpty(shader.Hash) ? "occurrence:" + Json.Serialize(shader.ShaderRef)
            : "hash:" + shader.Stage.ToUpperInvariant() + ":" + shader.Hash.ToUpperInvariant();

    internal IReadOnlyList<ShaderInventoryItemDto> Inventory(string? stage, string? entryContains, string? hash,
        Func<EventRef, bool> within, EventRef? scope = null)
        => _occurrences.Where(o => within(o.Shader.ShaderRef!.EventRef))
            .Where(o => (stage is null || o.Shader.Stage.Equals(stage, StringComparison.OrdinalIgnoreCase)) &&
                (entryContains is null || (o.Shader.Entry?.Contains(entryContains, StringComparison.OrdinalIgnoreCase) ?? false)) &&
                (hash is null || string.Equals(o.Shader.Hash, hash, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(o => Identity(o.Shader)).Select(group =>
            {
                ShaderInfoDto representative = group.First().Shader;
                var nextCalls = new List<ToolCallDto> { new("pix_gpu_shader_uses", new { shaderRef = representative.ShaderRef, scope }) };
                string? code = representative.AvailableCode.FirstOrDefault(c => c is "HLSL" or "IL" or "ISA");
                if (code is not null) nextCalls.Add(new("pix_gpu_shader_code", new { shaderRef = representative.ShaderRef, codeType = code }));
                return new ShaderInventoryItemDto(representative, group.Select(o => o.Shader.ShaderRef!.EventRef).Distinct().Count(), nextCalls);
            }).OrderBy(row => row.Shader.Stage, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Shader.Entry, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.Shader.Hash, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Shader.ShaderRef!.EventRef.QueueIndex).ThenBy(row => row.Shader.ShaderRef!.EventRef.EventIndex)
            .ThenBy(row => row.Shader.ShaderRef!.ShaderIndex).ToArray();

    internal (string MatchMethod, IReadOnlyList<ShaderUseEventDto> Items) Uses(ShaderRef shaderRef, Func<EventRef, bool> within)
    {
        if (!_byReference.TryGetValue(shaderRef, out ShaderOccurrence? occurrence))
            throw new PixToolException(PixErrors.Codes.UnavailableShaderData, "This shader occurrence was not readable while indexing the capture.",
                nextCalls: [new("pix_gpu_pipeline_state", new { eventRef = shaderRef.EventRef })]);
        string key = Identity(occurrence.Shader);
        var items = _occurrences.Where(o => Identity(o.Shader) == key && within(o.Shader.ShaderRef!.EventRef))
            .GroupBy(o => o.Shader.ShaderRef!.EventRef)
            .Select(group => new ShaderUseEventDto(group.Key, group.First().MarkerPath,
                group.Select(o => o.Shader.ShaderRef!).ToArray())).ToArray();
        return (string.IsNullOrEmpty(occurrence.Shader.Hash) ? "exactOccurrence" : "hashAndStage", items);
    }
}
