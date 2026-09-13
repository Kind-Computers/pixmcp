using System.ComponentModel;

namespace PixMcp.Pix;

public sealed record ResourceBucketDto(int Count, [property: Description("Summed estimated bytes; unsized resources add nothing.")] ulong EstimatedBytes);

public sealed record ResourceFormatBucketDto(string Format, int Count, ulong EstimatedBytes);

/// <summary>pix_gpu_resources extra.totals over the filtered resources.</summary>
public sealed record ResourceTotalsDto(int Count, ulong EstimatedBytes,
    [property: Description("Resources whose format has no single texel size, so they add nothing to the byte totals.")] int UnknownSizes,
    IReadOnlyDictionary<string, ResourceBucketDto> ByDimension,
    [property: Description("The eight largest formats by estimated bytes, then other.")] IReadOnlyList<ResourceFormatBucketDto> ByFormat,
    IReadOnlyDictionary<string, ResourceBucketDto> ByHeapKind);

public sealed record ResourceTargetSummaryDto(ResourceRef ResourceRef, string? Name, ulong Width, uint Height, string Format, uint SampleCount,
    [property: Description("Distinct events inside the selection that bind it this way.")] int Events);

/// <summary>pix_gpu_resources extra.scopeSummary: what the selection renders into.</summary>
public sealed record ResourceScopeSummaryDto(
    [property: Description("Draw, dispatch and ExecuteIndirect events inside the selection.")] int WorkEvents,
    IReadOnlyList<ResourceTargetSummaryDto> RenderTargets, IReadOnlyList<ResourceTargetSummaryDto> DepthTargets,
    [property: Description("Uses inside the selection whose access could not be classified.")] int UnknownAccessRows);
