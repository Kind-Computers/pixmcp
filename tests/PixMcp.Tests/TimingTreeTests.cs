using PixMcp.Pix;
using PixMcp.Pix.Handles;
using PixMcp.Tools;
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
    private static readonly SyntheticQueue Canonical = SyntheticGpuCapture.Canonical().Queues[0];
    private static readonly EventRecord[] Events = Canonical.Events;
    private static readonly EventTimingRow[] Rows = Canonical.Rows;

    private static readonly object Provenance = new { source = "test" };

    [Fact]
    public void UnmeasuredMarkersSumTheirChildrenAndMeasuredOnesAreTakenAsIs()
    {
        TimingTreeNode[] nodes = TimingTree.Build(Events, Rows).Nodes;
        // Frame: no own measurement, children Shadow (30, derived) + Main (8, measured) = a mixed sum.
        Assert.Null(nodes[0].MeasuredEopNs);
        Assert.Equal(38UL, nodes[0].InclusiveEopNs);
        Assert.Equal(38UL, nodes[0].ChildSumEopNs);
        Assert.Equal(0UL, nodes[0].SelfEopNs);
        Assert.Equal(4, nodes[0].TimedDescendants);
        Assert.Equal(2, nodes[0].ChildCount);
        Assert.Equal(TimingSemantics.Mixed, nodes[0].Semantics);
        Assert.Equal(0, nodes[0].UntimedChildren);
        // Shadow pass: sum of two measured draws.
        Assert.Equal(30UL, nodes[1].InclusiveEopNs);
        Assert.Equal(0UL, nodes[1].SelfEopNs);
        Assert.Equal(TimingSemantics.DerivedSum, nodes[1].Semantics);
        // Main pass: PIX measured the span (8), children explain 5 of it; the remaining 3 is self time. Not 8 + 5.
        Assert.Equal(8UL, nodes[4].MeasuredEopNs);
        Assert.Equal(8UL, nodes[4].InclusiveEopNs);
        Assert.Equal(5UL, nodes[4].ChildSumEopNs);
        Assert.Equal(3UL, nodes[4].SelfEopNs);
        Assert.Equal(1, nodes[4].TimedDescendants);
        Assert.Equal(1, nodes[4].UntimedChildren);
        Assert.False(nodes[4].ChildrenExceedMeasured);
        Assert.Equal(TimingSemantics.Measured, nodes[4].Semantics);
        Assert.Equal(TimingSemantics.DerivedSum, nodes[5].Semantics);
        Assert.Equal(5UL, nodes[5].InclusiveEopNs);
        // Leaves.
        Assert.Equal(20UL, nodes[3].SelfEopNs);
        Assert.True(nodes[3].HasOwnTiming);
        Assert.Equal(TimingSemantics.Measured, nodes[3].Semantics);
        Assert.False(nodes[7].HasOwnTiming);
        Assert.Equal(0UL, nodes[7].InclusiveEopNs);
        Assert.Equal(TimingSemantics.Untimed, nodes[7].Semantics);
        Assert.False(nodes[7].IsTimed);
        Assert.All(nodes, n => Assert.False(n.Repaired));
        // Queue total counts each top-level subtree once.
        Assert.Equal(39UL, TimingTree.Total(nodes));
    }

    [Fact]
    public void QueueTotalsComeFromLeafWindowsAndReportOverlap()
    {
        TimingTreeResult tree = TimingTree.Build(Events, Rows, queueIndex: 3);
        QueueTotals totals = tree.Totals;
        Assert.Equal(3, totals.QueueIndex);
        Assert.Equal(39UL, totals.SumOfRootsNs);
        Assert.Equal(5, totals.TimedEvents);
        Assert.Equal(4, totals.UntimedEvents);
        // Every fixture row starts at 0, so the span is the longest EOP interval and the union of leaf windows covers it.
        Assert.Equal(20UL, totals.SpanNs);
        Assert.Equal(20UL, totals.BusyNs);
        Assert.Equal(0UL, totals.IdleNs);
        Assert.True(totals.RootsOverlap);
        Assert.Equal("topEop", totals.BusySource);
        Assert.Equal(0, tree.Repairs);
        Assert.Equal(0UL, tree.Nodes[3].TopStartNs);
        Assert.Equal(20UL, tree.Nodes[3].EopEndNs);
        Assert.Equal(20UL, tree.Nodes[3].ExecutionNs);
        Assert.Null(tree.Nodes[0].ExecutionNs);
        Assert.Equal(0UL, tree.Nodes[0].TopStartNs); // derived markers span their timed children
        Assert.Equal(20UL, tree.Nodes[0].EopEndNs);
        Assert.Null(tree.Nodes[7].TopStartNs);
    }

    [Fact]
    public void ChildrenAreOrderedMostExpensiveFirstOrByTheRequestedKey()
    {
        TimingTreeNode[] nodes = TimingTree.Build(Events, Rows).Nodes;
        Assert.Equal(new uint[] { 0, 8 }, TimingTree.Children(nodes, null).Select(n => n.Index).ToArray());
        Assert.Equal(new uint[] { 1, 4 }, TimingTree.Children(nodes, 0).Select(n => n.Index).ToArray());
        Assert.Equal(new uint[] { 4, 1 }, TimingTree.Children(nodes, 0, "self").Select(n => n.Index).ToArray());
        Assert.Equal(new uint[] { 1, 4 }, TimingTree.Children(nodes, 0, "index").Select(n => n.Index).ToArray());
        Assert.Equal(new uint[] { 1, 4 }, TimingTree.Children(nodes, 0, "topStart").Select(n => n.Index).ToArray());
        Assert.Equal(new uint[] { 1, 4 }, TimingTree.Children(nodes, 0, "childCount").Select(n => n.Index).ToArray()); // equal counts: index order
        Assert.Equal(new uint[] { 3, 2 }, TimingTree.Children(nodes, 1).Select(n => n.Index).ToArray());
        Assert.Equal(new uint[] { 5, 7 }, TimingTree.Children(nodes, 4).Select(n => n.Index).ToArray());
        Assert.Empty(TimingTree.Children(nodes, 8));
        Assert.Equal("self", TimingTree.NormalizeSortBy(" SELF "));
        Assert.Equal("inclusive", TimingTree.NormalizeSortBy(null));
        Assert.Null(TimingTree.NormalizeSortBy("bogus"));
        Assert.Throws<ArgumentException>(() => TimingTree.Children(nodes, 0, "bogus").ToArray());
    }

    [Fact]
    public void MeasuredMarkerSmallerThanItsChildrenReportsTheOverflowInsteadOfNegativeSelfTime()
    {
        EventRecord[] events =
        {
            new(0, uint.MaxValue, uint.MaxValue, "Pass", "", 0, 0),
            new(1, 1, 0, "Draw", "Draw", 0, 0),
        };
        TimingTreeNode[] nodes = TimingTree.Build(events, new[] { Row(events, 0, 4), Row(events, 1, 9) }).Nodes;
        Assert.Equal(4UL, nodes[0].InclusiveEopNs);
        Assert.Equal(0UL, nodes[0].SelfEopNs);
        Assert.True(nodes[0].ChildrenExceedMeasured);
        Assert.Equal(5UL, nodes[0].ChildOverflowNs);
        Assert.Equal(TimingSemantics.Measured, nodes[0].Semantics);
        Assert.Equal(4UL, TimingTree.Total(nodes));
    }

    [Fact]
    public void CorruptParentLinksAreRepairedAndCounted()
    {
        EventRecord[] cyclic =
        {
            new(0, uint.MaxValue, 1, "A", "", 0, 0),
            new(1, uint.MaxValue, 0, "B", "", 0, 0),
            new(2, 1, 1, "Draw", "Draw", 0, 0),
        };
        TimingTreeResult tree = TimingTree.Build(cyclic, new[] { Row(cyclic, 2, 4) });
        Assert.Equal(3, tree.Nodes.Length);
        Assert.All(tree.Nodes, n => Assert.NotNull(n));
        Assert.Equal(4UL, tree.Nodes[2].InclusiveEopNs);
        Assert.Equal(4UL, tree.Nodes[1].InclusiveEopNs);
        Assert.Equal(1, tree.Repairs);
        Assert.Contains(tree.Nodes, n => n.Repaired);

        EventRecord[] outOfRange = { new(0, uint.MaxValue, 99, "Orphan", "", 0, 0), new(1, 1, 0, "Draw", "Draw", 0, 0) };
        tree = TimingTree.Build(outOfRange, new[] { Row(outOfRange, 1, 4) });
        Assert.True(tree.Nodes[0].Repaired);
        Assert.Null(tree.Nodes[0].ParentIndex);
        Assert.Equal(1, tree.Repairs);
    }

    [Fact]
    public void OverlappingRootsMakeSpanPercentsSmallerThanSumPercents()
    {
        EventRecord[] events = { new(0, 1, uint.MaxValue, "Draw", "Draw", 0, 0), new(1, 2, uint.MaxValue, "Draw", "Draw", 0, 0) };
        EventTimingRow[] rows = { new(0, 0, 1, "Draw", "Draw", 0, 10, 0, 10), new(0, 1, 2, "Draw", "Draw", 5, 10, 5, 10) };
        TimingTreeResult tree = TimingTree.Build(events, rows);
        Assert.True(tree.Totals.RootsOverlap);
        Assert.Equal(15UL, tree.Totals.SpanNs);
        Assert.Equal(20UL, tree.Totals.SumOfRootsNs);
        DurationDto d = Metrics.Duration(tree.Nodes[0].InclusiveEopNs, tree.Totals);
        Assert.True(d.PercentOfQueueSpan > d.PercentOfQueueSum);
    }

    [Fact]
    public void BuiltTreeCarriesDurationsSemanticsRanksAndFilters()
    {
        TimingTreeResult tree = TimingTree.Build(Events, Rows);
        TimingTreeDto dto = CountersTools.BuildTimingTree("gpu-1", 0, tree, null, 0, 25, 2, 100, 0, 0, "inclusive", Provenance);
        Assert.Equal(2, dto.ChildCount);
        Assert.Equal(39UL, dto.Queue.SumOfRootsNs);
        Assert.Equal(5, dto.TimedEvents);
        Assert.Equal(4, dto.UntimedEvents);
        Assert.Equal("inclusive", dto.SortBy);
        Assert.Null(dto.Scope);
        TimingBranchDto frame = dto.Children[0];
        Assert.Equal(0u, frame.Index);
        Assert.Equal(TimingSemantics.Mixed, frame.Semantics);
        Assert.Equal(1, frame.Inclusive.Rank);
        Assert.Equal(38UL, frame.Inclusive.Ns);
        Assert.Equal(190d, frame.Inclusive.PercentOfQueueSpan);
        Assert.Equal(97.44d, frame.Inclusive.PercentOfQueueSum);
        Assert.Null(frame.Inclusive.PercentOfParent);
        Assert.Equal(2, dto.Children[1].Inclusive.Rank);
        TimingBranchDto shadow = frame.Children[0];
        Assert.Equal(1u, shadow.Index);
        Assert.Equal(78.95d, shadow.Inclusive.PercentOfParent);
        Assert.Equal(1, shadow.Inclusive.Rank);
        Assert.Equal(1, frame.Children[1].UntimedChildren);

        // Self-time order puts Main pass (3 ns self) before Shadow pass (0 ns self); minSelfNs drops Shadow entirely.
        TimingTreeDto bySelf = CountersTools.BuildTimingTree("gpu-1", 0, tree, new EventRef("gpu-1", 0, 0), 0, 25, 1, 100, 0, 0, "self", Provenance);
        Assert.Equal(new uint[] { 4, 1 }, bySelf.Children.Select(c => c.Index).ToArray());
        Assert.Equal(21.05d, bySelf.Children[0].Inclusive.PercentOfParent);
        TimingTreeDto filtered = CountersTools.BuildTimingTree("gpu-1", 0, tree, new EventRef("gpu-1", 0, 0), 0, 25, 1, 100, 0, 1, "self", Provenance);
        Assert.Equal(4u, Assert.Single(filtered.Children).Index);
        Assert.Equal(0u, filtered.Scope!.EventIndex);

        var error = Assert.Throws<PixToolException>(() => CountersTools.BuildTimingTree("gpu-1", 0, tree, new EventRef("gpu-1", 0, 99), 0, 25, 1, 100, 0, 0, "inclusive", Provenance));
        Assert.Equal("invalid_reference", error.Detail.Code);
        Assert.NotEmpty(error.Detail.NextCalls);
    }

    [Fact]
    public void ChildrenExceedingTheMeasuredSpanGetAnEventOrderNextCall()
    {
        EventRecord[] events = { new(0, uint.MaxValue, uint.MaxValue, "Pass", "", 0, 0), new(1, 1, 0, "Draw", "Draw", 0, 0) };
        TimingTreeResult tree = TimingTree.Build(events, new[] { Row(events, 0, 4), Row(events, 1, 9) });
        TimingTreeDto dto = CountersTools.BuildTimingTree("gpu-1", 0, tree, null, 0, 25, 1, 100, 0, 0, "inclusive", Provenance);
        TimingBranchDto pass = Assert.Single(dto.Children);
        Assert.True(pass.ChildrenExceedMeasured);
        Assert.Equal(5UL, pass.ChildOverflowNs);
        ToolCallDto call = Assert.Single(pass.NextCalls, c => c.Tool == "pix_gpu_timing_events");
        Assert.Contains("eopStart", Json.Serialize(call.Arguments));
    }

    private static EventTimingRow Row(EventRecord[] events, uint index, ulong eop) => new(0, index, index, events[index].Name, events[index].ApiCallData, 0, eop, 0, eop);
}
