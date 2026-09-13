using PixMcp.Pix;
using PixMcp.Pix.Handles;
using Xunit;
using ToolKinds = PixMcp.Tools.Tools;

namespace PixMcp.Tests;

/// <summary>Frames delimited by Present: the presenting queue split by index, other queues by replay-clock windows.</summary>
public sealed class FrameSegmentationTests
{
    internal static EventRecord E(uint index, string name, uint parent = uint.MaxValue, uint gpuId = uint.MaxValue, string api = "")
        => new(index, gpuId, parent, name, api, 0, 0);

    internal static EventTimingRow Row(int queue, EventRecord e, ulong eopStart, ulong eopDuration, ulong top = GpuCaptureHandle.TimingNone)
        => new(queue, e.Index, e.GpuId, e.Name, e.ApiCallData, top, top == GpuCaptureHandle.TimingNone ? GpuCaptureHandle.TimingNone : eopStart + eopDuration - top, eopStart, eopDuration);

    internal static readonly EventRecord[] Graphics =
    [
        E(0, "DrawInstanced", gpuId: 0, api: "x"), E(1, "Present", gpuId: 1, api: "x"), E(2, "DrawInstanced", gpuId: 2, api: "x"), E(3, "Present", gpuId: 3, api: "x"),
        E(4, "DrawInstanced", gpuId: 4, api: "x"), E(5, "Present", gpuId: 5, api: "x"), E(6, "DrawInstanced", gpuId: 6, api: "x"),
    ];

    internal static readonly EventTimingRow[] GraphicsRows =
    [
        Row(0, Graphics[0], 0, 10), Row(0, Graphics[1], 10, 2), Row(0, Graphics[2], 20, 10), Row(0, Graphics[3], 30, 2),
        Row(0, Graphics[4], 40, 10), Row(0, Graphics[5], 50, 2), Row(0, Graphics[6], 60, 10),
    ];

    internal static readonly EventRecord[] Compute =
        [E(0, "Dispatch", gpuId: 10, api: "x"), E(1, "Wave"), E(2, "Dispatch", 1, 11, "x"), E(3, "Dispatch", gpuId: 12, api: "x")];

    internal static readonly EventTimingRow[] ComputeRows = [Row(1, Compute[0], 5, 5), Row(1, Compute[2], 35, 5), Row(1, Compute[3], 55, 5)];

    internal static bool IsPresent(EventRecord e) => ToolKinds.MatchesKind(e, "present");

    [Fact]
    public void PresentsSplitThePresentingQueueAndWindowsAssignOtherQueues()
    {
        FrameTable table = FrameSegmentation.Build([new(0, Graphics, GraphicsRows), new(1, Compute, ComputeRows)], IsPresent);
        Assert.Equal((4, (int?)0, "present"), (table.Count, table.PresentQueueIndex, table.Assignment));
        Assert.Equal(new[] { (0u, 1u, (uint?)1u, false), (2u, 3u, (uint?)3u, false), (4u, 5u, (uint?)5u, false), (6u, 6u, (uint?)null, true) },
            table.Frames.Select(f => (f.FirstEventIndex, f.LastEventIndex, f.PresentEventIndex, f.Partial)));
        Assert.Equal(new ulong?[] { 0, 12, 32, 52 }, table.Frames.Select(f => f.WindowStartNs));
        Assert.Equal(new ulong?[] { 12, 32, 52, null }, table.Frames.Select(f => f.WindowEndNs));
        Assert.Equal(new int?[] { 0, 2, 2, 3 }, Enumerable.Range(0, 4).Select(i => table.FrameOf(1, (uint)i)));
        Assert.Equal(2, table.FrameOf(0, 4));
        Assert.True(table.Contains(3, 1, 3));
        Assert.False(table.Contains(0, 1, 3));
        Assert.Null(table.FrameOf(7, 0));
    }

    [Fact]
    public void WithoutTimingOtherQueuesStayUnassignedAndWithoutPresentsTheCaptureIsOneFrame()
    {
        FrameTable indexOnly = FrameSegmentation.Build([new(0, Graphics, null), new(1, Compute, null)], IsPresent);
        Assert.Equal((4, "indexOnly"), (indexOnly.Count, indexOnly.Assignment));
        Assert.Null(indexOnly.FrameOf(1, 0));
        Assert.All(indexOnly.Frames, f => Assert.Null(f.WindowStartNs));
        Assert.False(indexOnly.Contains(0, 1, 0));
        Assert.Equal(1, indexOnly.FrameOf(0, 2));

        FrameTable none = FrameSegmentation.Build([new(1, Compute, ComputeRows)], IsPresent);
        Assert.Equal((1, (int?)null, "none"), (none.Count, none.PresentQueueIndex, none.Assignment));
        Assert.True(none.Contains(0, 1, 2));
        Assert.Equal(0, none.FrameOf(1, 3));

        FrameTable single = FrameSegmentation.Build([new(0, [E(0, "DrawInstanced", gpuId: 0, api: "x"), E(1, "Present", gpuId: 1, api: "x")], null), new(1, Compute, null)], IsPresent);
        Assert.Equal((1, false), (single.Count, single.Frames[0].Partial));
        Assert.True(single.Contains(0, 1, 3));

        FrameTable chosen = FrameSegmentation.Build(
            [new(0, [E(0, "Present", gpuId: 0, api: "x")], null), new(1, [E(0, "Present", gpuId: 0, api: "x"), E(1, "Present", gpuId: 1, api: "x")], null)], IsPresent);
        Assert.Equal((1, 2), (chosen.PresentQueueIndex!.Value, chosen.Count));

        EventTimingRow[] untimedPresent = GraphicsRows.Where(r => r.Index != 3).ToArray();
        Assert.Equal("indexOnly", FrameSegmentation.Build([new(0, Graphics, untimedPresent), new(1, Compute, ComputeRows)], IsPresent).Assignment);
    }
}
