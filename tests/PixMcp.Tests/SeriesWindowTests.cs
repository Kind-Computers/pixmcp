using Microsoft.Extensions.Logging.Abstractions;
using PixMcp.Pix;
using PixMcp.Tools;
using Xunit;

namespace PixMcp.Tests;

public sealed class SeriesWindowTests
{
    [Fact]
    public void StepSeriesHoldUntilTheNextPointAndSeedFromThePointBeforeTheWindow()
    {
        StepWindowStats stats = SeriesWindow.Step([(0, 4), (10, 8)], 5, 15, 16);
        Assert.Equal(((double?)6, (uint?)8, (double?)37.5, (double?)50, 10UL, 100.0),
            (stats.AverageSlots, stats.PeakSlots, stats.AveragePercent, stats.PeakPercent, stats.ActiveNs, stats.CoveragePercent));
        StepWindowStats before = SeriesWindow.Step([(10, 4)], 0, 5, 16);
        Assert.Equal(((double?)null, 0.0), (before.AverageSlots, before.CoveragePercent));
        StepWindowStats half = SeriesWindow.Step([(10, 4)], 0, 20, 16);
        Assert.Equal(((double?)4, 50.0), (half.AverageSlots, half.CoveragePercent));
        Assert.Equal(0UL, SeriesWindow.Step([(0, 0), (10, 2)], 0, 10, 16).ActiveNs);
        Assert.Null(SeriesWindow.Step([(0, 4)], 0, 10, 0).AveragePercent);
        Assert.Equal(0.0, SeriesWindow.Step([(0, 4)], 10, 10, 16).CoveragePercent);
    }

    [Fact]
    public void SampleStatsReportCoverageTimeWeightingAndTheNearestSample()
    {
        (ulong, double)[] samples = [(0, 10), (10, 30), (20, 50)];
        SampleWindowStats inside = SeriesWindow.Samples(samples, 5, 25);
        Assert.Equal((2, (double?)30, (double?)50, (double?)40, (double?)30, "full"),
            (inside.Count, inside.Min, inside.Max, inside.Average, inside.TimeWeightedAverage, inside.Coverage));
        SampleWindowStats partial = SeriesWindow.Samples(samples[1..], 5, 15);
        Assert.Equal((1, "partial", (double?)30), (partial.Count, partial.Coverage, partial.TimeWeightedAverage));
        SampleWindowStats held = SeriesWindow.Samples(samples, 11, 12);
        Assert.Equal((0, "held", (double?)30, (ulong?)1, (double?)30), (held.Count, held.Coverage, held.HeldValue, held.NearestSampleDistanceNs, held.TimeWeightedAverage));
        SampleWindowStats none = SeriesWindow.Samples([(100, 1)], 0, 10);
        Assert.Equal((0, "none", (ulong?)90, (double?)null), (none.Count, none.Coverage, none.NearestSampleDistanceNs, none.TimeWeightedAverage));
        Assert.Equal("none", SeriesWindow.Samples([], 0, 10).Coverage);
    }

    [Theory]
    [InlineData(0UL, 100UL, 0UL, 100UL, "verified", 1.0)]
    [InlineData(0UL, 60UL, 0UL, 100UL, "partial", 0.6)]
    [InlineData(80UL, 200UL, 0UL, 100UL, "mismatch", 0.2)]
    [InlineData(0UL, 100UL, 50UL, 50UL, "verified", 1.0)]
    public void ClockChecksCompareTheSeriesRangeWithTheWindow(ulong seriesStart, ulong seriesEnd, ulong windowStart, ulong windowEnd, string state, double fraction)
    {
        ClockCheckDto check = SeriesWindow.Clock(seriesStart, seriesEnd, windowStart, windowEnd);
        Assert.Equal((state, fraction), (check.State, check.OverlapFraction));
    }

    [Fact]
    public void UtilizationRankingNeedsThreePercentCountersAndATenPointMargin()
    {
        Assert.Null(SeriesWindow.Rank([("A (%)", "percent", 90.0), ("B (%)", "percent", 10.0), ("C", "bytes", 50.0)]));
        UtilizationRankingDto clear = SeriesWindow.Rank([("ALU (%)", "percent", 92.0), ("Sampler (%)", "percent", 40.0), ("Bytes", "bytes", 99.0), ("Z (%)", "percent", 70.0)])!;
        Assert.Equal(new[] { "ALU (%)", "Z (%)", "Sampler (%)" }, clear.Ranked.Select(r => r.Counter));
        Assert.Equal(("ALU (%)", 22.0, "heuristic"), (clear.LikelyLimiter, clear.MarginPoints, clear.Confidence));
        Assert.Null(SeriesWindow.Rank([("A (%)", "percent", 75.0), ("B (%)", "percent", 70.0), ("C (%)", "percent", 10.0)])!.LikelyLimiter);
    }

    [Fact]
    public async Task OccupancyAndHfArgumentRulesFailBeforeStartingAnyJob()
    {
        using var worker = new PixWorker();
        using var session = new PixSession(worker, NullLogger<PixSession>.Instance);
        var jobs = new JobManager(worker, session);
        var eventRef = new EventRef("gpu-1", 0, 5);
        async Task Rejected(Func<Task<string>> call)
            => Assert.Equal("invalid_arguments", (await Assert.ThrowsAsync<PixToolException>(call)).Detail.Code);

        await Rejected(() => CountersTools.Occupancy(session, jobs, "gpu-1", eventRef: eventRef, scope: eventRef));
        await Rejected(() => CountersTools.Occupancy(session, jobs, "gpu-1", eventRef: eventRef, markerPathPrefix: "Frame"));
        await Rejected(() => CountersTools.Occupancy(session, jobs, "gpu-1", groupBy: "marker", markerPathPrefix: "Frame"));
        await Rejected(() => CountersTools.Occupancy(session, jobs, "gpu-1", groupBy: "event"));
        await Rejected(() => CountersTools.Occupancy(session, jobs, "gpu-1", groupBy: "shader"));
        await Rejected(() => CountersTools.HighFrequencyCounters(session, jobs, "gpu-1", setIndex: 0, setName: "Utilization"));
        await Rejected(() => CountersTools.HighFrequencyCounters(session, jobs, "gpu-1", setIndex: -1));
        await Rejected(() => CountersTools.HighFrequencyCounters(session, jobs, "gpu-1", groupBy: "event", markerPathPrefix: "Frame"));
        await Rejected(() => CountersTools.HighFrequencyCounters(session, jobs, "gpu-1", groupBy: "marker", setIndex: 0));
        Assert.Empty(jobs.All);
        Assert.Equal(0, worker.PendingCount);
    }
}
