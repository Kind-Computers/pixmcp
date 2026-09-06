using PixMcp.Pix;
using PixMcp.Pix.Handles;
using Xunit;

namespace PixMcp.Tests;

public sealed class TimingTreeTests
{
    // 0 Frame (unmeasured marker)
    //   1 Shadow pass (unmeasured marker)
    //     2 Draw (10 ns)
    //     3 Draw (20 ns)
    //   4 Main pass (measured by PIX as 8 ns: the span of its contents)
    //     5 Marker (unmeasured)
    //       6 Dispatch (5 ns)
    //     7 Draw (untimed)
    // 8 Present (1 ns, top level)
    private static readonly EventRecord[] Events =
    {
        new(0, uint.MaxValue, uint.MaxValue, "Frame", "", 0, 0),
        new(1, uint.MaxValue, 0, "Shadow pass", "", 0, 0),
        new(2, 1, 1, "DrawInstanced", "DrawInstanced(3)", 0, 0),
        new(3, 2, 1, "DrawInstanced", "DrawInstanced(6)", 0, 0),
        new(4, uint.MaxValue, 0, "Main pass", "", 0, 0),
        new(5, uint.MaxValue, 4, "Marker", "", 0, 0),
        new(6, 3, 5, "Dispatch", "Dispatch(1,1,1)", 0, 0),
        new(7, 4, 4, "DrawInstanced", "DrawInstanced(3)", 0, 0),
        new(8, 5, uint.MaxValue, "Present", "", 0, 0),
    };

    private static readonly EventTimingRow[] Rows =
    {
        Row(2, 10), Row(3, 20), Row(4, 8), Row(6, 5), Row(8, 1),
        new(0, 7, 4, "DrawInstanced", "DrawInstanced(3)", 0, 0, 0, GpuCaptureHandle.TimingNone),
    };

    private static EventTimingRow Row(uint index, ulong eop) => new(0, index, index, Events[index].Name, Events[index].ApiCallData, 0, eop, 0, eop);

    [Fact]
    public void UnmeasuredMarkersSumTheirChildrenAndMeasuredOnesAreTakenAsIs()
    {
        TimingTreeNode[] nodes = TimingTree.Build(Events, Rows);
        // Frame: no own measurement, children Shadow (30) + Main (8).
        Assert.Null(nodes[0].MeasuredEopNs);
        Assert.Equal(38UL, nodes[0].InclusiveEopNs);
        Assert.Equal(0UL, nodes[0].SelfEopNs);
        Assert.Equal(4, nodes[0].TimedDescendants);
        Assert.Equal(2, nodes[0].ChildCount);
        // Shadow pass: sum of two draws.
        Assert.Equal(30UL, nodes[1].InclusiveEopNs);
        Assert.Equal(0UL, nodes[1].SelfEopNs);
        // Main pass: PIX measured the span (8), children explain 5 of it; the remaining 3 is self time. Not 8 + 5.
        Assert.Equal(8UL, nodes[4].MeasuredEopNs);
        Assert.Equal(8UL, nodes[4].InclusiveEopNs);
        Assert.Equal(3UL, nodes[4].SelfEopNs);
        Assert.Equal(1, nodes[4].TimedDescendants);
        Assert.Equal(5UL, nodes[5].InclusiveEopNs);
        // Leaves.
        Assert.Equal(20UL, nodes[3].SelfEopNs);
        Assert.True(nodes[3].HasOwnTiming);
        Assert.False(nodes[7].HasOwnTiming);
        Assert.Equal(0UL, nodes[7].InclusiveEopNs);
        // Queue total counts each top-level subtree once.
        Assert.Equal(39UL, TimingTree.Total(nodes));
    }

    [Fact]
    public void ChildrenAreOrderedMostExpensiveFirst()
    {
        TimingTreeNode[] nodes = TimingTree.Build(Events, Rows);
        Assert.Equal(new uint[] { 0, 8 }, TimingTree.Children(nodes, null).Select(n => n.Index).ToArray());
        Assert.Equal(new uint[] { 1, 4 }, TimingTree.Children(nodes, 0).Select(n => n.Index).ToArray());
        Assert.Equal(new uint[] { 3, 2 }, TimingTree.Children(nodes, 1).Select(n => n.Index).ToArray());
        Assert.Equal(new uint[] { 5, 7 }, TimingTree.Children(nodes, 4).Select(n => n.Index).ToArray());
        Assert.Empty(TimingTree.Children(nodes, 8));
    }

    [Fact]
    public void MeasuredMarkerSmallerThanItsChildrenHasNoNegativeSelfTime()
    {
        EventRecord[] events =
        {
            new(0, uint.MaxValue, uint.MaxValue, "Pass", "", 0, 0),
            new(1, 1, 0, "Draw", "Draw", 0, 0),
        };
        TimingTreeNode[] nodes = TimingTree.Build(events, new[] { Row(events, 0, 4), Row(events, 1, 9) });
        Assert.Equal(4UL, nodes[0].InclusiveEopNs);
        Assert.Equal(0UL, nodes[0].SelfEopNs);
        Assert.Equal(4UL, TimingTree.Total(nodes));
    }

    [Fact]
    public void CorruptParentLinksDoNotLoopForever()
    {
        EventRecord[] cyclic =
        {
            new(0, uint.MaxValue, 1, "A", "", 0, 0),
            new(1, uint.MaxValue, 0, "B", "", 0, 0),
            new(2, 1, 1, "Draw", "Draw", 0, 0),
        };
        TimingTreeNode[] nodes = TimingTree.Build(cyclic, new[] { Row(cyclic, 2, 4) });
        Assert.Equal(3, nodes.Length);
        Assert.All(nodes, n => Assert.NotNull(n));
        Assert.Equal(4UL, nodes[2].InclusiveEopNs);
        Assert.Equal(4UL, nodes[1].InclusiveEopNs);
    }

    private static EventTimingRow Row(EventRecord[] events, uint index, ulong eop) => new(0, index, index, events[index].Name, events[index].ApiCallData, 0, eop, 0, eop);
}
