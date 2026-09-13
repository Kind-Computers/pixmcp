namespace PixMcp.Pix;

/// <summary>Detached evidence for a completed capture-wide or scoped fallback scan.</summary>
internal sealed class ResourceUseIndex
{
    private readonly IReadOnlyDictionary<string, ResourceUseDto[]> _byResource;
    internal IReadOnlyList<object> Coverage { get; }

    /// <summary>Every row of the index in scan order.</summary>
    internal IReadOnlyList<ResourceUseDto> Rows { get; }

    internal ResourceUseIndex(IEnumerable<ResourceUseDto> rows, IReadOnlyList<object> coverage)
    {
        ResourceUseDto[] all = rows.ToArray();
        Rows = all;
        _byResource = all.GroupBy(r => r.ResourceRef.ApiObjectId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        Coverage = coverage;
    }

    internal ResourceUsesSnapshot For(ResourceRef resource)
        => new(_byResource.GetValueOrDefault(resource.ApiObjectId, []), Coverage, "eventCollectionsAndApiArguments");
}
