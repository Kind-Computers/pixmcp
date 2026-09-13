using PixMcp.Tools;

namespace PixMcp.Pix.Handles;

public sealed partial class GpuCaptureHandle
{
    public bool AccessedResourcesGathered { get; private set; }
    private Exception? _accessedResourcesUnavailable;
    internal Exception? AccessedResourcesUnavailable => _accessedResourcesUnavailable;
    internal ResourceUseCache ResourceUses { get; } = new();
    /// <summary>Resource summaries with size estimates; capture metadata, so never cleared with the analysis.</summary>
    internal List<ResourceSummaryDto>? ResourceSummaryCache { get; set; }

    internal static Preparation<GpuCaptureHandle> AccessedResourcesPreparation(string handle)
        => new("accessed-resources", "accessed-resources", $"Gather accessed resources for {handle}",
            h => h.AccessedResourcesGathered, (h, job) => h.EnsureAccessedResources(job))
        { JoinKeys = [PreparationKeys.Inspection] };

    /// <summary>The capture-wide resource-use index: every event's views, API object arguments and barrier arguments, with access classes.</summary>
    internal static Preparation<GpuCaptureHandle> ResourceUseIndexPreparation(string handle)
        => new("resource-use-index", "resource-uses", $"Index resource uses across {handle}",
            h => h.ResourceUses.CaptureWideIndex is not null, (h, job) =>
            {
                if (h.ResourceUses.CaptureWideIndex is not null) return;
                h.EnsureAccessedResources(job);
                ResourceTools.BuildFallbackIndex(h, job, null);
            })
        { JoinKeys = [PreparationKeys.AccessedResources, PreparationKeys.Inspection] };

    internal static Preparation<GpuCaptureHandle> ResourceUsesPreparation(string handle, string apiObjectId, EventRef? scope = null)
        => new("resource-uses:" + apiObjectId + (scope is null ? "" : $":scope:{scope.QueueIndex}:{scope.EventIndex}"),
            "resource-uses", $"Index resource uses for {apiObjectId} in {handle}" +
                (scope is null ? "" : $" within queue {scope.QueueIndex} event {scope.EventIndex} and descendants"),
            h => h.ResourceUses.TryGet(new(h.Id, apiObjectId), scope, out _), (h, job) =>
            {
                if (h.ResourceUses.TryGet(new(h.Id, apiObjectId), scope, out _)) return;
                ResourceTools.FindResource(h, apiObjectId);
                h.EnsureAccessedResources(job);
                ResourceUsesSnapshot snapshot = ResourceTools.BuildResourceUseSnapshot(h, new(h.Id, apiObjectId), job, scope);
                job.ThrowIfCancellationRequested();
                h.ResourceUses.StoreSnapshot(apiObjectId, scope, snapshot);
            });

    internal void EnsureAccessedResources(Job? job)
    {
        if (AccessedResourcesGathered) return;
        if (_accessedResourcesUnavailable is not null) throw _accessedResourcesUnavailable;
        EnsureAnalysisStarted(job);
        job?.ThrowIfCancellationRequested();
        job?.AddMessage("Gathering accessed resources...");
        try { GetAnalysis().GatherAccessedResources(); }
        catch (Exception ex) when (PixErrors.ToDto(ex).Code == "unsupported_feature")
        {
            _accessedResourcesUnavailable = ex;
            MarkCapability("accessedResources", "unsupported", PixErrors.Describe(ex));
            throw;
        }
        job?.ThrowIfCancellationRequested();
        AccessedResourcesGathered = true;
        MarkCapability("accessedResources", "supported");
    }

    internal void ResetAccessedResources()
    {
        AccessedResourcesGathered = false;
        _accessedResourcesUnavailable = null;
        ResourceUses.Clear();
    }
}

/// <summary>Only completed scans are stored. Scoped evidence must never satisfy a capture-wide query.</summary>
internal sealed class ResourceUseCache
{
    private readonly Dictionary<string, ResourceUsesSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<EventRef, Dictionary<string, ResourceUsesSnapshot>> _scopedSnapshots = new();
    private readonly Dictionary<EventRef, ResourceUseIndex> _scopedFallback = new();
    private ResourceUseIndex? _fallback;

    /// <summary>The published capture-wide index, or null until one exists.</summary>
    internal ResourceUseIndex? CaptureWideIndex => _fallback;

    internal bool TryGet(ResourceRef resourceRef, EventRef? scope, out ResourceUsesSnapshot snapshot)
    {
        if (_fallback is not null)
        {
            snapshot = _fallback.For(resourceRef);
            return true;
        }
        if (_snapshots.TryGetValue(resourceRef.ApiObjectId, out snapshot!)) return true;
        if (scope is not null)
        {
            if (_scopedFallback.TryGetValue(scope, out ResourceUseIndex? index))
            {
                snapshot = index.For(resourceRef);
                return true;
            }
            if (_scopedSnapshots.TryGetValue(scope, out var snapshots) && snapshots.TryGetValue(resourceRef.ApiObjectId, out snapshot!)) return true;
        }
        snapshot = null!;
        return false;
    }

    internal void StoreSnapshot(string apiObjectId, EventRef? scope, ResourceUsesSnapshot snapshot)
    {
        if (scope is null) _snapshots[apiObjectId] = snapshot;
        else
        {
            if (!_scopedSnapshots.TryGetValue(scope, out var snapshots))
                _scopedSnapshots.Add(scope, snapshots = new(StringComparer.OrdinalIgnoreCase));
            snapshots[apiObjectId] = snapshot;
        }
    }

    internal void PublishFallback(EventRef? scope, ResourceUseIndex index)
    {
        if (scope is null) _fallback = index;
        else _scopedFallback[scope] = index;
    }

    internal void Clear()
    {
        _snapshots.Clear();
        _scopedSnapshots.Clear();
        _scopedFallback.Clear();
        _fallback = null;
    }
}
