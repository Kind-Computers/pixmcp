using Microsoft.PIX;
using ModelContextProtocol;
using System.Runtime.InteropServices;
using PixMcp.Pix;
using PixMcp.Tools;
using Xunit;

namespace PixMcp.Tests;

public class CounterAndContinuationTests
{
    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(2UL)]
    [InlineData(199UL)]
    [InlineData(200UL)]
    [InlineData(201UL)]
    [InlineData(399UL)]
    [InlineData(400UL)]
    [InlineData(401UL)]
    [InlineData(10000UL)]
    [InlineData(ulong.MaxValue)]
    public void SamplesAreBoundedOrderedAndRetainEndpoints(ulong count)
    {
        ulong[] indices = Sampling.Indices(count, 200).ToArray();
        Assert.Equal((int)Math.Min(count, 200UL), indices.Length);
        if (count == 0) return;
        Assert.Equal(0UL, indices[0]);
        Assert.Equal(count - 1, indices[^1]);
        Assert.All(indices, i => Assert.True(i < count));
        Assert.Equal(indices.Length, indices.Distinct().Count());
        Assert.Equal(indices.OrderBy(i => i), indices);
    }

    [Fact]
    public void CanonicalCounterIdsIgnoreOrderAndDuplicates()
    {
        Assert.Equal(new uint[] { 2, 4, 9 }, CountersTools.NormalizeCounterIds([9, 2, 4, 2]));
        Assert.Throws<McpException>(() => CountersTools.NormalizeCounterIds(null));
        Assert.Throws<McpException>(() => CountersTools.NormalizeCounterIds([]));
    }

    [Fact]
    public void SamplingCopiesOnlyTheRequestedIndicesFromHugeNativeSeries()
    {
        int reads = 0;
        ulong[] result = Sampling.Select(ulong.MaxValue, 200, index => { reads++; return index; });
        Assert.Equal(200, reads);
        Assert.Equal(200, result.Length);
        Assert.Equal(0UL, result[0]);
        Assert.Equal(ulong.MaxValue - 1, result[^1]);
    }

    [Fact]
    public void MaterializedRowsAreReusedAndFailedAttemptsAreNotPublished()
    {
        var cache = new CounterCollectionCache(null!, []);
        int calls = 0;
        Assert.Throws<OperationCanceledException>(() => cache.GetOrCreateRows(0, () =>
        {
            calls++;
            throw new OperationCanceledException();
        }));
        Assert.Empty(cache.RowsByQueue);

        CounterEventRow[] rows = cache.GetOrCreateRows(0, () => { calls++; return []; });
        Assert.Same(rows, cache.GetOrCreateRows(0, () => throw new InvalidOperationException("Must not rebuild a cached queue.")));
        Assert.Equal(2, calls);
        cache.GetOrCreateRows(1, () => { calls++; return []; });
        Assert.Equal(3, calls);
        Assert.Equal(2, cache.RowsByQueue.Count);
    }

    [Fact]
    public unsafe void SelectedCounterSetCollectionExposesOnlyTheRequestedSet()
    {
        var source = new RecordingCollection();
        var selected = new SelectedPixCollection(source, 7);
        Guid iid = typeof(IPixGpuCaptureCounterCollection).GUID;
        Assert.Equal(1UL, selected.GetCount());
        selected.Get(0, &iid, out object item);
        Assert.Same(source.Item, item);
        Assert.Equal(7UL, source.RequestedIndex);
        Assert.Equal(iid, source.RequestedInterface);
        Assert.Throws<ArgumentOutOfRangeException>(() => selected.Get(1, null, out _));
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public unsafe void SelectedCollectionCanBePassedToNativePixAsAComInterface()
    {
        var selected = new SelectedPixCollection(new RecordingCollection(), 7);
        nint pointer = Marshal.GetComInterfaceForObject(selected, typeof(IPixCollection));
        try
        {
            nint vtable = Marshal.ReadIntPtr(pointer);
            var getCount = (delegate* unmanaged[Stdcall]<nint, ulong>)Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);
            Assert.Equal(1UL, getCount(pointer));
        }
        finally { Marshal.Release(pointer); }
    }

    private sealed unsafe class RecordingCollection : IPixCollection
    {
        public object Item { get; } = new();
        public ulong RequestedIndex { get; private set; }
        public Guid RequestedInterface { get; private set; }
        public int Calls { get; private set; }
        public ulong GetCount() => 10;
        public void Get(ulong index, Guid* riid, out object item)
        {
            Calls++;
            RequestedIndex = index;
            RequestedInterface = *riid;
            item = Item;
        }
    }

    [Theory]
    [InlineData(10000, 9500, null, 9400, 200, 9600)]
    [InlineData(10000, 0, null, 0, 200, 200)]
    [InlineData(10000, 10000, null, 9800, 200, null)]
    [InlineData(10000, 9500, 0, 0, 200, 200)]
    [InlineData(10000, 9500, 9950, 9950, 50, null)]
    [InlineData(10000, 9500, 20000, 20000, 0, null)]
    [InlineData(10000, 9500, -1, 0, 200, 200)]
    [InlineData(0, 0, null, 0, 0, null)]
    public void BreadcrumbWindowsReachTheHangAndSupportExplicitContinuation(int total, long completed, int? requested,
        int expectedOffset, int expectedCount, int? expectedNext)
    {
        Assert.Equal((expectedOffset, expectedCount, expectedNext), DumpTools.BreadcrumbWindow(total, completed, 200, requested));
    }

    [Fact]
    public void BindingPagesRetainAbsoluteIndicesAndReachTheTail()
    {
        static object Read(uint index) => index;
        var first = ResourceTools.BindingPage(70, 0, 32, Read);
        var middle = ResourceTools.BindingPage(70, 32, 32, Read);
        var last = ResourceTools.BindingPage(70, 64, 32, Read);
        Assert.Equal(32, first.Items.Count);
        Assert.Equal(32L, first.NextOffset);
        Assert.Equal(32u, middle.Items[0]);
        Assert.Equal(63u, middle.Items[^1]);
        Assert.Equal(64L, middle.NextOffset);
        Assert.Equal(6, last.Items.Count);
        Assert.Equal(69u, last.Items[^1]);
        Assert.Null(last.NextOffset);
        Assert.Empty(ResourceTools.BindingPage(70, 80, 32, Read).Items);
        Assert.Equal(1000, ResourceTools.BindingPage(10000, 0, 10000, Read).Items.Count);
        Assert.Throws<McpException>(() => ResourceTools.ValidateViewIndex(3, 3));
        ResourceTools.ValidateViewIndex(null, 0);
    }
}
