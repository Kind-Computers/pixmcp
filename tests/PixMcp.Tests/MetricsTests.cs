using PixMcp.Pix;
using PixMcp.Pix.Handles;
using Xunit;

namespace PixMcp.Tests;

public sealed class MetricsTests
{
    private const ulong None = GpuCaptureHandle.TimingNone;

    private static EventTimingRow Row(uint index, ulong eopStart, ulong eop, ulong topStart = None)
        => new(0, index, index, "Draw", "Draw", topStart, topStart == None ? None : eop, eopStart, eop);

    [Fact]
    public void EopOnlyRowsGiveBusyIdleSpanAndPercents()
    {
        var totals = Metrics.Totals(0, [Row(0, 0, 10), Row(1, 20, 20), Row(2, 42, 8)], sumOfRootsNs: 38, eventCount: 4);
        Assert.Equal(50UL, totals.SpanNs);
        Assert.Equal(38UL, totals.BusyNs);
        Assert.Equal(12UL, totals.IdleNs);
        Assert.Equal("eopOnly", totals.BusySource);
        Assert.Equal(3, totals.TimedEvents);
        Assert.Equal(1, totals.UntimedEvents);
        Assert.False(totals.RootsOverlap);
        Assert.Equal(24d, totals.IdlePercent);
        DurationDto twenty = Metrics.Duration(20, totals);
        Assert.Equal(40d, twenty.PercentOfQueueSpan);
        Assert.Equal(52.63d, twenty.PercentOfQueueSum);
        Assert.Null(twenty.PercentOfParent);
        Assert.Null(twenty.Rank);
    }

    [Fact]
    public void TopWindowsAreUnionedAndClippedToTheEopSpan()
    {
        // Window [0,10) starts before the first EOP start (5) and is clipped; [8,16) overlaps it.
        var totals = Metrics.Totals(0, [Row(0, 5, 5, topStart: 0), Row(1, 12, 4, topStart: 8)], sumOfRootsNs: 9, eventCount: 2);
        Assert.Equal("topEop", totals.BusySource);
        Assert.Equal(11UL, totals.SpanNs);
        Assert.Equal(11UL, totals.BusyNs);
        Assert.Equal(0UL, totals.IdleNs);
        Assert.Equal(5UL, totals.FirstEopStartNs);
        Assert.Equal(16UL, totals.LastEopEndNs);
    }

    [Fact]
    public void UnionLengthCountsOverlapsOnce()
    {
        Assert.Equal(20UL, Metrics.UnionLength([(0UL, 10UL), (5UL, 15UL), (20UL, 25UL)]));
        Assert.Equal(10UL, Metrics.UnionLength([(5UL, 10UL), (0UL, 5UL)]));
        Assert.Equal(0UL, Metrics.UnionLength([]));
        Assert.Equal(3UL, Metrics.UnionLength([(4UL, 4UL), (1UL, 4UL)]));
    }

    [Fact]
    public void ExcludedRowsDoNotFeedTheBusyUnion()
    {
        var rows = new[] { Row(9, 0, 50), Row(0, 0, 10), Row(1, 40, 10) };
        var all = Metrics.Totals(0, rows, 50, 3);
        Assert.Equal(50UL, all.BusyNs);
        var leavesOnly = Metrics.Totals(0, rows, 50, 3, index => index == 9);
        Assert.Equal(20UL, leavesOnly.BusyNs);
        Assert.Equal(30UL, leavesOnly.IdleNs);
        Assert.Equal(50UL, leavesOnly.SpanNs);
    }

    [Fact]
    public void PercentsAreNullOnZeroDenominatorsAndRoundedOtherwise()
    {
        var empty = Metrics.Totals(0, [], 0, 3);
        Assert.Equal(0UL, empty.SpanNs);
        Assert.Equal(3, empty.UntimedEvents);
        Assert.Null(empty.IdlePercent);
        DurationDto d = Metrics.Duration(5, empty, parentInclusiveNs: 0, rank: 2);
        Assert.Null(d.PercentOfQueueSpan);
        Assert.Null(d.PercentOfQueueSum);
        Assert.Null(d.PercentOfParent);
        Assert.Equal(2, d.Rank);
        Assert.Equal(1.235d, Metrics.Ms(1_234_567));
        Assert.Equal(33.33d, Metrics.Percent(1, 3));
        Assert.Equal(50d, Metrics.Duration(5, empty, parentInclusiveNs: 10).PercentOfParent);
    }

    [Fact]
    public void OverlappingRootsAreFlagged()
    {
        var totals = Metrics.Totals(0, [Row(0, 0, 10), Row(1, 5, 10)], sumOfRootsNs: 20, eventCount: 2);
        Assert.Equal(15UL, totals.SpanNs);
        Assert.Equal(15UL, totals.BusyNs);
        Assert.True(totals.RootsOverlap);
        DurationDto ten = Metrics.Duration(10, totals);
        Assert.True(ten.PercentOfQueueSpan > ten.PercentOfQueueSum);
    }
}
