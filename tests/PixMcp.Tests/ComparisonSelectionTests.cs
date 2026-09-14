using System.Text.Json;
using PixMcp.Pix;
using PixMcp.Pix.Handles;
using PixMcp.Tools;
using Xunit;

namespace PixMcp.Tests;

public sealed class ComparisonSelectionTests
{
    private static readonly ReplayProvenance Provenance = new("gpuReplay", "test", null, null, null, "test");

    [Theory]
    [InlineData("DrawInstanced", "DrawInstanced(3,1,0,0)", "draw")]
    [InlineData("Dispatch", "Dispatch(1,1,1)", "dispatch")]
    [InlineData("Label", "", "label")]
    [InlineData("PIXSetMarker", "PIXSetMarker()", "marker")]
    public void LeafScopesRetainTheirEventsTimingAndRelativePaths(string name, string call, string kind)
    {
        SyntheticQueue a = new SyntheticGpuCapture().Queue().Marker("Baseline pass", pass => pass.Event(name, call, kind, 10));
        SyntheticQueue b = new SyntheticGpuCapture().Queue().Marker("Candidate pass", pass => pass.Event(name, call, kind, 20));
        ComparisonQueue baseline = Select(a, "a", 1)!;
        ComparisonQueue candidate = Select(b, "b", 1)!;
        Assert.Equal(1u, Assert.Single(baseline.Events).EventRef.EventIndex);
        Assert.Empty(baseline.Events[0].MarkerPath);
        Assert.Empty(Assert.Single(candidate.Events).MarkerPath);
        Assert.Equal(10UL, baseline.BusyNs);
        Assert.Equal(20UL, candidate.BusyNs);

        ComparisonResultDto result = CaptureComparison.Compare(new("a", [baseline], Provenance, []), new("b", [candidate], Provenance, []));
        Assert.Equal(1, result.MatchedCount);
        Assert.Equal(10m, Assert.Single(result.Items).DeltaNs);
        Assert.Equal(10UL, result.Totals!.BaselineBusyNs);
        Assert.Equal(20UL, result.Totals.CandidateBusyNs);
    }

    [Fact]
    public void ContainerScopesOmitOnlyTheRootAndPreserveFrameFiltering()
    {
        SyntheticQueue queue = SyntheticGpuCapture.Canonical().Queues[0];
        ComparisonQueue scoped = Select(queue, "gpu-1", 0)!;
        Assert.Equal(Enumerable.Range(1, 7).Select(i => (uint)i), scoped.Events.Select(e => e.EventRef.EventIndex));
        Assert.Empty(scoped.Events.Single(e => e.EventRef.EventIndex == 1).MarkerPath);
        Assert.Equal(new[] { "Shadow pass" }, scoped.Events.Single(e => e.EventRef.EventIndex == 2).MarkerPath);
        Assert.Equal(20UL, scoped.BusyNs);

        ComparisonQueue frame = Select(queue, "gpu-1", 0, i => i == 2)!;
        Assert.Equal(2u, Assert.Single(frame.Events).EventRef.EventIndex);
        Assert.Equal(10UL, frame.BusyNs);
        Assert.Null(Select(queue, "gpu-1", 2, _ => false));

        ComparisonQueue whole = Select(queue, "gpu-1", null)!;
        Assert.Equal(queue.Events.Length, whole.Events.Length);
        Assert.Equal(new[] { "Frame", "Shadow pass" }, whole.Events.Single(e => e.EventRef.EventIndex == 2).MarkerPath);
        Assert.Equal(20UL, whole.BusyNs);
    }

    [Fact]
    public void WorkRootWithChildrenIsRetained()
    {
        EventRecord[] events =
        [
            new(0, 1, uint.MaxValue, "ExecuteIndirect", "ExecuteIndirect()", 0, 0),
            new(1, 2, 0, "DrawInstanced", "DrawInstanced(3,1,0,0)", 0, 0),
        ];
        ComparisonQueue queue = Snapshot("gpu-1", events, []);
        var selection = new ScopeSelection("gpu-1", 0, new("gpu-1", 0, 0), null, null);
        ComparisonQueue filtered = InvestigationTools.FilterComparisonQueue(queue, events, [1, 0], [], selection)!;
        Assert.Equal(new uint[] { 0, 1 }, filtered.Events.Select(e => e.EventRef.EventIndex));
        Assert.Empty(filtered.Events[0].MarkerPath);
    }

    private static ComparisonQueue? Select(SyntheticQueue queue, string handle, uint? root, Func<uint, bool>? inFrame = null)
    {
        EventRecord[] events = queue.Events;
        EventTimingRow[] rows = queue.Rows;
        var selection = new ScopeSelection(handle, null, root is uint index ? new(handle, 0, index) : null, null, null);
        return InvestigationTools.FilterComparisonQueue(Snapshot(handle, events, rows), events, EventNavigation.ChildCounts(events), rows, selection, inFrame);
    }

    private static ComparisonQueue Snapshot(string handle, EventRecord[] events, EventTimingRow[] rows)
    {
        TimingTreeNode[] nodes = TimingTree.Build(events, rows).Nodes;
        return new(0, "Direct", "DIRECT", events.Select(e =>
        {
            string kind = PixMcp.Tools.Tools.Classify(e, nodes[e.Index].ChildCount > 0);
            return new ComparisonEvent(new(handle, 0, e.Index), EventNavigation.MarkerPath(events, e.Index), e.Name, kind, kind == "marker",
                nodes[e.Index].IsTimed ? nodes[e.Index].InclusiveEopNs : null, nodes[e.Index].Semantics, null, new Dictionary<string, JsonElement>());
        }).ToArray());
    }
}
