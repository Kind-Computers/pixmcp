namespace PixMcp.Pix;

public sealed record ShaderInventoryItemDto(ShaderInfoDto Shader, int UseCount, IReadOnlyList<ToolCallDto> NextCalls)
{
    /// <summary>hash:STAGE:HASH; null when PIX exposes no hash (the row is then one occurrence).</summary>
    public string? ShaderKey { get; init; }
    /// <summary>Summed replay EOP time of the events using the shader; null until timing is collected.</summary>
    public ulong? GpuTimeNs { get; init; }
    public double? GpuTimeMs { get; init; }
}
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
    public string? ShaderKey { get; init; }
}

internal sealed record ShaderOccurrence(ShaderInfoDto Shader, IReadOnlyList<string> MarkerPath);

/// <summary>Capture-local metadata only. Names and missing hashes never establish shader identity.</summary>
internal sealed class ShaderIndex
{
    public static readonly string[] SortKeys = ["stage", "entry", "hash", "useCount", "gpuTime"];

    private readonly ShaderOccurrence[] _occurrences;
    private readonly IReadOnlyDictionary<ShaderRef, ShaderOccurrence> _byReference;
    private readonly IReadOnlyDictionary<EventRef, ShaderOccurrence[]> _byEvent;
    private readonly HashSet<EventRef> _incomplete;
    internal IReadOnlyList<object> Coverage { get; }

    /// <param name="incomplete">Events where at least one bound shader could not be read; they have no pipeline identity.</param>
    internal ShaderIndex(IEnumerable<ShaderOccurrence> occurrences, IReadOnlyList<object> coverage, IEnumerable<EventRef>? incomplete = null)
    {
        _occurrences = occurrences.Where(o => o.Shader.ShaderRef is not null)
            .OrderBy(o => o.Shader.ShaderRef!.EventRef.QueueIndex)
            .ThenBy(o => o.Shader.ShaderRef!.EventRef.EventIndex).ThenBy(o => o.Shader.ShaderRef!.ShaderIndex).ToArray();
        _byReference = _occurrences.ToDictionary(o => o.Shader.ShaderRef!);
        _byEvent = _occurrences.GroupBy(o => o.Shader.ShaderRef!.EventRef).ToDictionary(g => g.Key, g => g.ToArray());
        _incomplete = (incomplete ?? []).ToHashSet();
        Coverage = coverage;
    }

    private static string Identity(ShaderInfoDto shader)
        => ShaderIdentity.ShaderKey(shader.Stage, shader.Hash) ?? "occurrence:" + Json.Serialize(shader.ShaderRef);

    /// <param name="sortBy">stage (default: stage, entry, hash), entry, hash, useCount or gpuTime (both largest first).</param>
    /// <param name="eop">EOP nanoseconds of an event when timing is collected; fills gpuTime.</param>
    internal IReadOnlyList<ShaderInventoryItemDto> Inventory(string? stage, string? entryContains, string? hash,
        Func<EventRef, bool> within, EventRef? scope = null, string sortBy = "stage", Func<EventRef, ulong?>? eop = null)
    {
        ShaderInventoryItemDto[] rows = _occurrences.Where(o => within(o.Shader.ShaderRef!.EventRef))
            .Where(o => (stage is null || o.Shader.Stage.Equals(stage, StringComparison.OrdinalIgnoreCase)) &&
                (entryContains is null || (o.Shader.Entry?.Contains(entryContains, StringComparison.OrdinalIgnoreCase) ?? false)) &&
                (hash is null || string.Equals(o.Shader.Hash, hash, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(o => Identity(o.Shader)).Select(group =>
            {
                ShaderInfoDto representative = group.First().Shader;
                var nextCalls = new List<ToolCallDto> { new("pix_gpu_shader_uses", new { shaderRef = representative.ShaderRef, scope }) };
                string? code = representative.AvailableCode.FirstOrDefault(c => c is "HLSL" or "IL" or "ISA");
                if (code is not null) nextCalls.Add(new("pix_gpu_shader_code", new { shaderRef = representative.ShaderRef, codeType = code }));
                EventRef[] events = group.Select(o => o.Shader.ShaderRef!.EventRef).Distinct().ToArray();
                ulong? gpuTime = null;
                if (eop is not null)
                    foreach (EventRef e in events)
                        if (eop(e) is ulong ns) gpuTime = (gpuTime ?? 0) + ns;
                return new ShaderInventoryItemDto(representative, events.Length, nextCalls)
                {
                    ShaderKey = ShaderIdentity.ShaderKey(representative.Stage, representative.Hash),
                    GpuTimeNs = gpuTime,
                    GpuTimeMs = gpuTime is ulong total ? Metrics.Ms(total) : null,
                };
            }).OrderBy(row => row.Shader.Stage, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Shader.Entry, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.Shader.Hash, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Shader.ShaderRef!.EventRef.QueueIndex).ThenBy(row => row.Shader.ShaderRef!.EventRef.EventIndex)
            .ThenBy(row => row.Shader.ShaderRef!.ShaderIndex).ToArray();
        return sortBy switch
        {
            "entry" => rows.OrderBy(r => r.Shader.Entry, StringComparer.OrdinalIgnoreCase).ToArray(),
            "hash" => rows.OrderBy(r => r.Shader.Hash, StringComparer.OrdinalIgnoreCase).ToArray(),
            "useCount" => rows.OrderByDescending(r => r.UseCount).ToArray(),
            "gpuTime" => rows.OrderByDescending(r => r.GpuTimeNs ?? 0).ThenByDescending(r => r.UseCount).ToArray(),
            _ => rows,
        };
    }

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

    internal IReadOnlyList<ShaderOccurrence> Occurrences => _occurrences;

    /// <summary>False when a bound shader of the event could not be read while indexing.</summary>
    internal bool IsComplete(EventRef eventRef) => !_incomplete.Contains(eventRef);

    /// <summary>The first indexed occurrence of a canonical shader key, or null when the capture has none.</summary>
    internal ShaderRef? FirstReference(string shaderKey)
        => _occurrences.FirstOrDefault(o => Identity(o.Shader) == shaderKey)?.Shader.ShaderRef;

    internal IReadOnlyList<ShaderInfoDto> ShadersAt(EventRef eventRef)
        => _byEvent.TryGetValue(eventRef, out ShaderOccurrence[]? found) ? found.Select(o => o.Shader).ToArray() : [];

    /// <summary>The shader keys bound at an event (shaders without a hash are left out).</summary>
    internal IReadOnlyList<string> ShaderKeysAt(EventRef eventRef)
        => _byEvent.TryGetValue(eventRef, out ShaderOccurrence[]? found)
            ? found.Select(o => ShaderIdentity.ShaderKey(o.Shader.Stage, o.Shader.Hash)).OfType<string>().Distinct().ToArray() : [];

    /// <summary>The event's pipeline key, or null when a bound shader lacks a hash or could not be read.</summary>
    internal string? PsoKeyOf(EventRef eventRef)
        => !_incomplete.Contains(eventRef) && _byEvent.TryGetValue(eventRef, out ShaderOccurrence[]? found) ? ShaderIdentity.PsoKey(found.Select(o => o.Shader)) : null;
}
