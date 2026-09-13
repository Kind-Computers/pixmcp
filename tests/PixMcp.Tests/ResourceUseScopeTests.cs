using PixMcp.Pix;
using PixMcp.Pix.Handles;
using PixMcp.Tools;
using Xunit;

namespace PixMcp.Tests;

public sealed class ResourceUseScopeTests
{
    private static readonly ResourceRef Resource = new("gpu-1", "0xAB");
    private static readonly EventRef FirstPass = new("gpu-1", 0, 0);
    private static readonly EventRef SecondPass = new("gpu-1", 0, 3);

    private static EventRecord[] Events() =>
    [
        new(0, 0, uint.MaxValue, "Pass", "", 0, 0),
        new(1, 1, 0, "Nested", "", 0, 0),
        new(2, 2, 1, "Draw", "Draw", 0, 0),
        new(3, 3, uint.MaxValue, "Pass", "", 0, 0),
        new(4, 4, 3, "Draw", "Draw", 0, 0),
    ];

    private static ResourceUseDto Row(ResourceRef resource, uint eventIndex)
        => new(resource, null, "API_ARGUMENT",
            new(0, "API_PARAMETER", new("gpu-1", 0, eventIndex), ["Pass"], null)
            { EventSource = "capturedApiArgument" });

    private static ResourceUsesSnapshot Snapshot(uint eventIndex)
        => new([Row(Resource, eventIndex)], [], "nativeBinding");

    [Fact]
    public void FallbackScanVisitsOnlySelectedSubtreeAndQueue()
    {
        EventRecord[] events = Events();
        // These records are the ones the production fallback passes to native event inspection.
        Assert.Equal(new uint[] { 0, 1, 2 }, ResourceTools.ResourceUseScanEvents(0, events, FirstPass).Select(e => e.Index));
        Assert.Equal(new uint[] { 3, 4 }, ResourceTools.ResourceUseScanEvents(0, events, SecondPass).Select(e => e.Index));
        Assert.Equal(2u, Assert.Single(ResourceTools.ResourceUseScanEvents(0, events, new("gpu-1", 0, 2))).Index);
        Assert.Empty(ResourceTools.ResourceUseScanEvents(1, events, FirstPass));
        Assert.Same(events, ResourceTools.ResourceUseScanEvents(0, events, null));
    }

    [Fact]
    public void PreparationsHaveDistinctScopeIdentities()
    {
        string Key(EventRef? scope) => GpuCaptureHandle.ResourceUsesPreparation("gpu-1", "0xAB", scope).Key;
        Assert.Equal(Key(FirstPass), Key(new("gpu-1", 0, 0)));
        Assert.Equal(4, new[] { Key(null), Key(FirstPass), Key(SecondPass), Key(new("gpu-1", 1, 0)) }.Distinct().Count());
        Assert.NotEqual(Key(FirstPass), GpuCaptureHandle.ResourceUsesPreparation("gpu-1", "0xAC", FirstPass).Key);
    }

    [Fact]
    public void ScopedFallbackIsReusableForOtherResourcesButCannotSatisfyOtherScopesOrGlobalQueries()
    {
        var otherResource = new ResourceRef("gpu-1", "0xAC");
        var cache = new ResourceUseCache();
        cache.PublishFallback(FirstPass, new([Row(Resource, 2), Row(otherResource, 1)], ["first scope"]));

        Assert.True(cache.TryGet(Resource with { ApiObjectId = "0xab" }, new("gpu-1", 0, 0), out var first));
        Assert.Equal(2u, Assert.Single(first.Items).Binding.EventRef!.EventIndex);
        Assert.True(cache.TryGet(otherResource, FirstPass, out var other));
        Assert.Equal(otherResource, Assert.Single(other.Items).ResourceRef);
        Assert.False(cache.TryGet(Resource, null, out _));
        Assert.False(cache.TryGet(Resource, SecondPass, out _));
        Assert.False(cache.TryGet(Resource, new("gpu-1", 1, 0), out _));

        cache.PublishFallback(SecondPass, new([Row(Resource, 4)], ["second scope"]));
        Assert.True(cache.TryGet(Resource, SecondPass, out var second));
        Assert.Equal(4u, Assert.Single(second.Items).Binding.EventRef!.EventIndex);
        Assert.True(cache.TryGet(Resource, FirstPass, out var stillFirst));
        Assert.Equal(first, stillFirst);
        Assert.False(cache.TryGet(Resource, null, out _));
    }

    [Fact]
    public void ScopedNativeSnapshotsAreIsolatedAndCompletedGlobalSnapshotsCanServeAnyScope()
    {
        var cache = new ResourceUseCache();
        var first = Snapshot(2);
        cache.StoreSnapshot(Resource.ApiObjectId, FirstPass, first);
        Assert.True(cache.TryGet(Resource, FirstPass, out var found));
        Assert.Same(first, found);
        Assert.False(cache.TryGet(Resource, SecondPass, out _));
        Assert.False(cache.TryGet(Resource, null, out _));

        var global = new ResourceUsesSnapshot([Row(Resource, 2), Row(Resource, 4)], [], "nativeBinding");
        cache.StoreSnapshot(Resource.ApiObjectId, null, global);
        foreach (EventRef? scope in new[] { null, FirstPass, SecondPass })
        {
            Assert.True(cache.TryGet(Resource with { ApiObjectId = "0xab" }, scope, out found));
            Assert.Same(global, found); // The query applies scope filtering to this complete snapshot.
        }
    }

    [Fact]
    public void CompletedGlobalFallbackSupersedesScopedCacheWithoutLosingOutsideUses()
    {
        var cache = new ResourceUseCache();
        cache.PublishFallback(FirstPass, new([Row(Resource, 2)], []));
        cache.PublishFallback(null, new([Row(Resource, 2), Row(Resource, 4)], ["global"]));
        foreach (EventRef? scope in new[] { null, FirstPass, SecondPass })
        {
            Assert.True(cache.TryGet(Resource, scope, out var found));
            Assert.Equal(new uint[] { 2, 4 }, found.Items.Select(i => i.Binding.EventRef!.EventIndex));
            Assert.Equal("global", Assert.Single(found.Coverage));
        }
    }

    [Fact]
    public void AnalysisResetClearsEveryScopeAndNativeAndFallbackCache()
    {
        var cache = new ResourceUseCache();
        cache.StoreSnapshot(Resource.ApiObjectId, null, Snapshot(2));
        cache.StoreSnapshot(Resource.ApiObjectId, FirstPass, Snapshot(2));
        cache.PublishFallback(SecondPass, new([Row(Resource, 4)], []));
        cache.PublishFallback(null, new([Row(Resource, 2), Row(Resource, 4)], []));
        cache.Clear();
        Assert.False(cache.TryGet(Resource, null, out _));
        Assert.False(cache.TryGet(Resource, FirstPass, out _));
        Assert.False(cache.TryGet(Resource, SecondPass, out _));
    }
}
