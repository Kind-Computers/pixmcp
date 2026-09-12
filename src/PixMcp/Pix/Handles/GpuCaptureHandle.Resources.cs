using PixMcp.Tools;

namespace PixMcp.Pix.Handles;

public sealed partial class GpuCaptureHandle
{
    public bool AccessedResourcesGathered { get; private set; }
    private Exception? _accessedResourcesUnavailable;
    internal Exception? AccessedResourcesUnavailable => _accessedResourcesUnavailable;
    internal Dictionary<string, ResourceUsesSnapshot> ResourceUseSnapshots { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal ResourceUseIndex? ResourceUseFallbackIndex { get; set; }

    internal static Preparation<GpuCaptureHandle> AccessedResourcesPreparation(string handle)
        => new("accessed-resources", "accessed-resources", $"Gather accessed resources for {handle}",
            h => h.AccessedResourcesGathered, (h, job) => h.EnsureAccessedResources(job));

    internal static Preparation<GpuCaptureHandle> ResourceUsesPreparation(string handle, string apiObjectId)
        => new("resource-uses:" + apiObjectId, "resource-uses", $"Index resource uses for {apiObjectId} in {handle}",
            h => h.ResourceUseFallbackIndex is not null || h.ResourceUseSnapshots.ContainsKey(apiObjectId), (h, job) =>
            {
                ResourceTools.FindResource(h, apiObjectId);
                h.EnsureAccessedResources(job);
                if (!h.ResourceUseSnapshots.ContainsKey(apiObjectId))
                    h.ResourceUseSnapshots[apiObjectId] = ResourceTools.BuildResourceUseSnapshot(h, new(h.Id, apiObjectId), job);
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
        ResourceUseSnapshots.Clear();
        ResourceUseFallbackIndex = null;
    }
}
