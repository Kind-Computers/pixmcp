using PixMcp.Pix;
using PixMcp.Pix.Handles;
using Xunit;
using ToolKinds = PixMcp.Tools.Tools;

namespace PixMcp.Tests;

/// <summary>Interval arithmetic, cross-queue overlap, idle gaps with causes, and their insights over synthetic timelines.</summary>
public sealed class QueueAnalysisTests
{
    private const uint None32 = uint.MaxValue;
    private const ulong None = GpuCaptureHandle.TimingNone;

    private static EventRecord E(uint index, string name, uint parent = None32, uint gpuId = None32, uint commandList = 1, string api = "x")
        => new(index, gpuId, parent, name, api, commandList, 0);

    private static EventTimingRow Row(int queue, EventRecord e, ulong eopStart, ulong eopDuration, ulong top = None)
        => new(queue, e.Index, e.GpuId, e.Name, e.ApiCallData, top, top == None || top > eopStart + eopDuration ? None : eopStart + eopDuration - top, eopStart, eopDuration);

    private static QueueTimeline Timeline(int queue, EventRecord[] events, params EventTimingRow[] rows)
        => QueueTimelines.Build(queue, $"Queue {queue}", queue == 0 ? "GRAPHICS" : "COMPUTE", events, EventNavigation.ChildCounts(events), rows);

    private static Func<uint, string> Kinds(EventRecord[] events) => i => ToolKinds.Classify(events[i], false);

    [Fact]
    public void IntervalsMergeIntersectClipAndFindGaps()
    {
        List<Interval> union = Intervals.Union([new(10, 20), new(0, 5), new(5, 8), new(15, 30), new(40, 40), new(50, 60)]);
        Assert.Equal(new Interval[] { new(0, 8), new(10, 30), new(50, 60) }, union);
        Assert.Equal(38UL, Intervals.Length(union));
        Assert.Equal(new Interval[] { new(25, 30), new(50, 55) }, Intervals.Intersection(union, [new(25, 55)]));
        Assert.Equal(new Interval[] { new(5, 8), new(10, 12) }, Intervals.Clip(union, 5, 12));
        Assert.Equal(new Interval[] { new(8, 10), new(30, 50), new(60, 70) }, Intervals.Gaps(union, 0, 70));
        Assert.Equal(10UL, Intervals.CoveredLength(union, 25, 55));
        Assert.Empty(Intervals.Gaps([new(0, 10)], 0, 10));
        Assert.Equal(0UL, Intervals.CoveredLength(union, 30, 30));
    }

    [Theory]
    [InlineData(50UL, 150UL, "overlapping", 50UL)]
    [InlineData(90UL, 140UL, "mostlySerialized", 10UL)]
    [InlineData(100UL, 150UL, "serialized", 0UL)]
    public void PairsCompareTheOverlapWithTheSmallerBusyTime(ulong computeStart, ulong computeEnd, string verdict, ulong overlapNs)
    {
        EventRecord[] graphics = [E(0, "DrawInstanced", gpuId: 1)];
        EventRecord[] compute = [E(0, "Dispatch", gpuId: 2)];
        OverlapResult result = QueueOverlapAnalysis.Compute(
            [Timeline(0, graphics, Row(0, graphics[0], 0, 100)), Timeline(1, compute, Row(1, compute[0], computeStart, computeEnd - computeStart))], null);
        QueueOverlapPair pair = Assert.Single(result.Pairs);
        Assert.Equal((verdict, overlapNs), (pair.Verdict, pair.OverlapNs));
        Assert.Equal((0UL, computeEnd, (int?)0), (result.CaptureStart, result.CaptureEnd, result.CriticalPath));
        Assert.Equal(100 - overlapNs, result.Queues[0].SoloBusyNs);
    }

    [Fact]
    public void QueuesWithoutTimedLeavesOrOutsideTheWindowAreUnavailable()
    {
        EventRecord[] graphics = [E(0, "DrawInstanced", gpuId: 1)];
        EventRecord[] compute = [E(0, "Dispatch", gpuId: 2)];
        OverlapResult empty = QueueOverlapAnalysis.Compute([Timeline(0, graphics, Row(0, graphics[0], 0, 100)), Timeline(1, compute)], null);
        Assert.Equal(("noTimedEvents", 1), (Assert.Single(empty.Unavailable).Reason, empty.Unavailable[0].Timeline.QueueIndex));
        Assert.Empty(empty.Pairs);
        OverlapResult windowed = QueueOverlapAnalysis.Compute(
            [Timeline(0, graphics, Row(0, graphics[0], 0, 100)), Timeline(1, compute, Row(1, compute[0], 200, 10))], new Interval(20, 60));
        Assert.Equal("noTimedEventsInWindow", Assert.Single(windowed.Unavailable).Reason);
        Assert.Equal((40UL, 40UL, 20UL, 60UL), (windowed.Queues[0].BusyNs, windowed.Queues[0].SpanNs, windowed.CaptureStart, windowed.CaptureEnd));
    }

    [Fact]
    public void MarkersDoNotBridgeGapsAndTheBarrierBetweenDrawsIsTheCause()
    {
        EventRecord[] events = [E(0, "Pass", api: ""), E(1, "DrawInstanced", 0, 1), E(2, "ResourceBarrier", 0), E(3, "DrawInstanced", 0, 2)];
        QueueTimeline timeline = Timeline(0, events, Row(0, events[0], 0, 100), Row(0, events[1], 0, 20), Row(0, events[3], 70, 30));
        Assert.Equal(50UL, Intervals.Length(timeline.Busy));
        Bubble bubble = Assert.Single(BubbleAnalysis.Find(timeline, events, Kinds(events), [], 0, null).Bubbles);
        Assert.Equal((20UL, 50UL, 1U, 3U, "barrier", (uint?)2, false, (double?)null),
            (bubble.Start, bubble.DurationNs, bubble.BeforeIndex, bubble.AfterIndex, bubble.PrimaryCause, bubble.EvidenceIndex, bubble.CommandListChanged, bubble.OtherQueueBusyPercent));
        Assert.Empty(BubbleAnalysis.Find(timeline, events, Kinds(events), [], 51, null).Bubbles);
    }

    [Fact]
    public void CausesFollowPrecedenceAndCommandListChanges()
    {
        EventRecord[] events =
        [
            E(0, "DrawInstanced", gpuId: 1, commandList: 2), E(1, "ResourceBarrier", commandList: 2), E(2, "Present", gpuId: 2, commandList: 0),
            E(3, "Wait", commandList: 0), E(4, "Dispatch", gpuId: 3, commandList: 3), E(5, "Signal", gpuId: 4, commandList: 0), E(6, "DrawInstanced", gpuId: 5, commandList: 4),
        ];
        QueueTimeline timeline = Timeline(0, events, Row(0, events[0], 0, 10), Row(0, events[2], 20, 0), Row(0, events[4], 30, 10), Row(0, events[6], 60, 10));
        Bubble[] found = BubbleAnalysis.Find(timeline, events, Kinds(events), [], 1, null).Bubbles.ToArray();
        Assert.Equal(new[] { "present", "present", "queueSignal" }, found.Select(b => b.PrimaryCause));
        Assert.Equal(new[] { "present", "queueWait" }, found[1].Causes);
        Assert.Equal(new[] { "present", "barrier" }, found[0].Causes);
        Assert.Equal((uint?)2, found[0].EvidenceIndex);
        Assert.Equal(new[] { "queueSignal", "commandListBoundary" }, found[2].Causes);
        Assert.Equal((false, true), (found[1].CommandListChanged, found[2].CommandListChanged));
    }

    [Fact]
    public void GapsAndBusyFillTheSpanAndOtherQueuesCoverGaps()
    {
        EventRecord[] events = [E(0, "DrawInstanced", gpuId: 1), E(1, "DrawInstanced", gpuId: 2), E(2, "DrawInstanced", gpuId: 3), E(3, "DrawInstanced", gpuId: 4)];
        QueueTimeline timeline = Timeline(0, events, Row(0, events[0], 0, 30), Row(0, events[1], 20, 20), Row(0, events[2], 60, 10), Row(0, events[3], 100, 20));
        BubbleScan scan = BubbleAnalysis.Find(timeline, events, Kinds(events), [new Interval(40, 60), new Interval(80, 90)], 0, null);
        Assert.Equal(1, scan.OverlappedPairs);
        Assert.Equal(new[] { (40UL, 20UL, (double?)100), (70UL, 30UL, (double?)33.33) }, scan.Bubbles.Select(b => (b.Start, b.DurationNs, b.OtherQueueBusyPercent)));
        Assert.Equal(timeline.SpanEnd - timeline.SpanStart, Intervals.Length(timeline.Busy) + scan.Bubbles.Aggregate(0UL, (sum, b) => sum + b.DurationNs));
        Assert.Equal(new[] { (50UL, 10UL), (70UL, 10UL) },
            BubbleAnalysis.Find(timeline, events, Kinds(events), [], 0, new Interval(50, 80)).Bubbles.Select(b => (b.Start, b.DurationNs)));
    }

    [Fact]
    public void TopAfterEopEndIsAClockAnomalyThatFallsBackToEop()
    {
        EventRecord[] events = [E(0, "DrawInstanced", gpuId: 1), E(1, "DrawInstanced", gpuId: 2)];
        QueueTimeline timeline = Timeline(0, events, Row(0, events[0], 10, 10, top: 5), Row(0, events[1], 30, 10, top: 90));
        Assert.Equal((1, false), (timeline.ClockAnomalies, timeline.EopOnly));
        Assert.Equal(new Interval[] { new(5, 20), new(30, 40) }, timeline.Busy);
        Assert.True(Timeline(0, events, Row(0, events[0], 10, 10)).EopOnly);
    }

    [Fact]
    public void InsightsFlagCopyQueueBlockingSerializedQueuesAndDominantGaps()
    {
        EventRecord[] graphics = [E(0, "DrawInstanced", gpuId: 1), E(1, "DrawInstanced", gpuId: 2)];
        EventRecord[] copy = [E(0, "CopyResource", gpuId: 3)];
        QueueTimeline g = Timeline(0, graphics, Row(0, graphics[0], 0, 10), Row(0, graphics[1], 90, 10));
        QueueTimeline c = QueueTimelines.Build(1, "Copy", "COPY", copy, EventNavigation.ChildCounts(copy), [Row(1, copy[0], 10, 80)]);
        OverlapResult overlap = QueueOverlapAnalysis.Compute([g, c], null);
        var bubbles = new Dictionary<int, IReadOnlyList<Bubble>> { [0] = BubbleAnalysis.Find(g, graphics, Kinds(graphics), c.Busy, 0, null).Bubbles, [1] = [] };
        Assert.Equal(new[] { "copy_queue_blocking", "queues_serialized", "bubbles_dominant" }, QueueAnalysisInsights.Evaluate("gpu-1", overlap, bubbles).Select(i => i.Id));
        Assert.Equal(100.0, BubbleAnalysis.Find(g, graphics, Kinds(graphics), c.Busy, 0, null).Bubbles.Single().OtherQueueBusyPercent);
    }
}
