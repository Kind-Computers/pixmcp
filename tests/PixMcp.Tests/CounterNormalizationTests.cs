using PixMcp.Pix;
using PixMcp.Pix.Handles;
using Xunit;

namespace PixMcp.Tests;

public sealed class CounterNormalizationTests
{
    private static readonly CounterInfo[] Counters = [new(1, "GPU Utilization (%)", "", "FLOAT64", []), new(2, "Primitives", "", "UINT64", []), new(3, "Busy", "", "BOOL", [])];

    [Fact]
    public void DivisorsComeFromTimingWorkItemsOrTargetsAndExplainTheirAbsence()
    {
        (ulong, string?) NoPixels() => throw new InvalidOperationException("pixels are only read for draws");
        DurationDto eop = Metrics.Duration(2_000_000, null);
        Assert.Equal(((double?)2, (string?)null), CounterNormalization.Divisor("perMs", "draw", eop, null, NoPixels));
        Assert.Equal("The event has no replay EOP time.", CounterNormalization.Divisor("perMs", "draw", null, null, NoPixels).Reason);
        Assert.Equal((double?)64, CounterNormalization.Divisor("perThreadGroup", "dispatch", null, ApiCallParser.Parse("Dispatch(8, 8, 1)"), NoPixels).Divisor);
        Assert.Null(CounterNormalization.Divisor("perThreadGroup", "draw", null, ApiCallParser.Parse("DrawInstanced(3, 1, 0, 0)"), NoPixels).Divisor);
        Assert.Equal((double?)307_200, CounterNormalization.Divisor("perPixel", "draw", null, null, () => (307_200UL, null)).Divisor);
        Assert.Equal("Only draws render pixels.", CounterNormalization.Divisor("perPixel", "dispatch", null, null, NoPixels).Reason);
        Assert.Equal("No depth.", CounterNormalization.Divisor("perPixel", "draw", null, null, () => (0UL, "No depth.")).Reason);
        Assert.Equal(((double?)null, (string?)null), CounterNormalization.Divisor("none", "draw", eop, null, NoPixels));
    }

    [Fact]
    public void NormalizationSkipsPercentAndBooleanCountersAndRatiosAvoidZeroDenominators()
    {
        IReadOnlyDictionary<string, double?> normalized = CounterNormalization.Normalize(Counters, [50.0, 1200UL, true], 4);
        Assert.Equal(new double?[] { null, 300, null }, new[] { normalized["1"], normalized["2"], normalized["3"] });
        Assert.Equal((double?)0.5, CounterNormalization.Ratio(1UL, 2.0));
        Assert.Null(CounterNormalization.Ratio(1UL, 0UL));
        Assert.Null(CounterNormalization.Ratio(null, 2UL));
        Assert.Null(CounterNormalization.Ratio(double.NaN, 2UL));
        Assert.Equal((double?)1.5, CounterNormalization.ToDouble((Half)1.5));
        Assert.Null(CounterNormalization.ToDouble("12"));
    }
}
