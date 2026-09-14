using PixMcp.Pix;
using PixMcp.Pix.Handles;
using Xunit;

namespace PixMcp.Tests;

public sealed class SyntheticGpuCaptureTests
{
    [Fact]
    public void CanonicalCaptureAgreesWithTheTimingTreeAndItsIndependentExpectations()
    {
        SyntheticQueue queue = SyntheticGpuCapture.Canonical().Queues[0];
        Assert.Equal(["Frame", "Shadow pass", "DrawInstanced", "DrawInstanced", "Main pass", "Marker", "Dispatch", "DrawInstanced", "Present"], queue.Events.Select(e => e.Name));
        Assert.Equal([uint.MaxValue, uint.MaxValue, 1u, 2u, uint.MaxValue, uint.MaxValue, 3u, 4u, 5u], queue.Events.Select(e => e.GpuId));
        Assert.Equal([uint.MaxValue, 0u, 1u, 1u, 0u, 4u, 5u, 4u, uint.MaxValue], queue.Events.Select(e => e.ParentIndex));
        Assert.Equal(GpuCaptureHandle.TimingNone, queue.Rows.Single(r => r.Index == 7).EopDuration);

        TimingTreeNode[] nodes = TimingTree.Build(queue.Events, queue.Rows).Nodes;
        Assert.Equal(38UL, queue.ExpectedInclusiveEopNs(0));
        for (uint i = 0; i < queue.Events.Length; i++)
        {
            if (queue.ExpectedInclusiveEopNs(i) is ulong expected)
                Assert.Equal(expected, (ulong)nodes[i].InclusiveEopNs);
            else
                Assert.Equal(TimingSemantics.Untimed, nodes[i].Semantics);
            Assert.Equal(queue.ExpectedKind(i), PixMcp.Tools.Tools.Classify(queue.Events[i], queue.HasChildren(i)));
        }
    }

    [Fact]
    public void KindCasesClassifyAsDeclared()
    {
        SyntheticQueue queue = SyntheticGpuCapture.Kinds().Queues[0];
        for (uint i = 0; i < queue.Events.Length; i++)
            Assert.True(queue.ExpectedKind(i) == PixMcp.Tools.Tools.Classify(queue.Events[i], queue.HasChildren(i)),
                $"{queue.Events[i].Name}: declared {queue.ExpectedKind(i)}, classified {PixMcp.Tools.Tools.Classify(queue.Events[i], queue.HasChildren(i))}");
    }
}
