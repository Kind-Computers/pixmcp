using System.ComponentModel;

namespace PixMcp.Pix;

/// <summary>Time-weighted statistics of a step series (occupancy slots held until the next point) over a window.</summary>
public sealed record StepWindowStats(
    [property: Description("Time-weighted average slots over the covered part of the window; null when nothing covers it.")] double? AverageSlots,
    uint? PeakSlots,
    [property: Description("AverageSlots as a percent of the type's maximum slots.")] double? AveragePercent,
    [property: Description("PeakSlots as a percent of the type's maximum slots.")] double? PeakPercent,
    [property: Description("Nanoseconds of the window with at least one slot occupied.")] ulong ActiveNs,
    [property: Description("Percent of the window the series covers: from the last point at or before its start, else from its first point inside it.")] double CoveragePercent);

/// <summary>Statistics of sampled values (high-frequency counters) over a window.</summary>
public sealed record SampleWindowStats(
    [property: Description("Samples inside the window.")] int Count,
    double? Min, double? Max, double? Average,
    [property: Description("Average weighted by how long each sample holds (until the next sample, clipped to the window).")] double? TimeWeightedAverage,
    [property: Description("full (samples inside and one at or before the start), partial (samples inside, none before), held (no sample inside; the previous value holds) or none.")] string Coverage,
    [property: Description("The value held from the last sample before the window start.")] double? HeldValue,
    [property: Description("Distance to the nearest sample when none falls inside the window.")] ulong? NearestSampleDistanceNs);

/// <summary>Whether a sampled series spans a replay-clock window.</summary>
public sealed record ClockCheckDto(
    [property: Description("verified (the series spans at least 90 % of the window), partial (at least 50 %) or mismatch (window statistics are withheld). A range check on the replay clock, not proof of alignment.")] string State,
    double OverlapFraction, ulong SeriesStartNs, ulong SeriesEndNs, ulong WindowStartNs, ulong WindowEndNs);

public sealed record UtilizationRankDto(string Counter, double Average, int Rank);

/// <summary>Percent-unit counters ranked by their average: a heuristic hint at the saturated lane, never a verdict.</summary>
public sealed record UtilizationRankingDto(string Method,
    [property: Description("Always heuristic.")] string Confidence,
    IReadOnlyList<UtilizationRankDto> Ranked,
    [property: Description("The top counter when it leads the second by at least requiredMarginPoints; null otherwise.")] string? LikelyLimiter,
    double MarginPoints, double RequiredMarginPoints, string Caveat);

/// <summary>Pure window arithmetic over occupancy step series and high-frequency samples; inputs are sorted by time.</summary>
internal static class SeriesWindow
{
    public const double VerifiedOverlap = 0.9, PartialOverlap = 0.5, LimiterMarginPoints = 10;
    public const int MinRankedCounters = 3;

    public static StepWindowStats Step(IReadOnlyList<(ulong TimeNs, uint Slots)> points, ulong startNs, ulong endNs, uint maxSlots)
    {
        var empty = new StepWindowStats(null, null, null, null, 0, 0);
        if (endNs <= startNs || points.Count == 0) return empty;
        int before = LastAtOrBefore(points.Count, i => points[i].TimeNs, startNs);
        ulong coveredStart = before >= 0 ? startNs : Math.Max(startNs, points[0].TimeNs);
        double weighted = 0;
        ulong covered = 0, active = 0;
        uint peak = 0;
        for (int i = Math.Max(before, 0); i < points.Count; i++)
        {
            ulong segmentStart = Math.Max(points[i].TimeNs, coveredStart);
            if (segmentStart >= endNs) break;
            ulong segmentEnd = i + 1 < points.Count ? Math.Min(points[i + 1].TimeNs, endNs) : endNs;
            if (segmentEnd <= segmentStart) continue;
            ulong length = segmentEnd - segmentStart;
            weighted += points[i].Slots * (double)length;
            covered += length;
            if (points[i].Slots > 0) active += length;
            peak = Math.Max(peak, points[i].Slots);
        }
        if (covered == 0) return empty;
        double average = weighted / covered;
        return new(Math.Round(average, 3), peak, maxSlots == 0 ? null : Math.Round(100 * average / maxSlots, 2), maxSlots == 0 ? null : Math.Round(100.0 * peak / maxSlots, 2),
            active, Math.Round(100.0 * covered / (endNs - startNs), 2));
    }

    public static SampleWindowStats Samples(IReadOnlyList<(ulong TimeNs, double Value)> samples, ulong startNs, ulong endNs)
    {
        if (samples.Count == 0 || endNs <= startNs) return new(0, null, null, null, null, "none", null, null);
        int lower = FirstAtOrAfter(samples.Count, i => samples[i].TimeNs, startNs);
        int count = 0;
        double min = double.MaxValue, max = double.MinValue, sum = 0;
        for (int i = lower; i < samples.Count && samples[i].TimeNs < endNs; i++)
        {
            double value = samples[i].Value;
            count++;
            min = Math.Min(min, value);
            max = Math.Max(max, value);
            sum += value;
        }
        int before = lower - 1;
        double? held = before >= 0 ? samples[before].Value : null;
        ulong coveredStart = before >= 0 ? startNs : lower < samples.Count ? Math.Max(startNs, samples[lower].TimeNs) : endNs;
        double weighted = 0;
        ulong covered = 0;
        for (int i = before >= 0 ? before : lower; i < samples.Count; i++)
        {
            ulong segmentStart = Math.Max(samples[i].TimeNs, coveredStart);
            if (segmentStart >= endNs) break;
            ulong segmentEnd = i + 1 < samples.Count ? Math.Min(samples[i + 1].TimeNs, endNs) : endNs;
            if (segmentEnd <= segmentStart) continue;
            weighted += samples[i].Value * (segmentEnd - segmentStart);
            covered += segmentEnd - segmentStart;
        }
        string coverage = count > 0 ? (before >= 0 || samples[lower].TimeNs == startNs ? "full" : "partial") : held is not null ? "held" : "none";
        ulong? nearest = null;
        if (count == 0)
        {
            if (before >= 0) nearest = startNs - samples[before].TimeNs;
            if (lower < samples.Count) nearest = Math.Min(nearest ?? ulong.MaxValue, samples[lower].TimeNs - endNs);
        }
        return new(count, count == 0 ? null : min, count == 0 ? null : max, count == 0 ? null : Math.Round(sum / count, 6),
            covered == 0 ? null : Math.Round(weighted / covered, 6), coverage, held, nearest);
    }

    public static ClockCheckDto Clock(ulong seriesStartNs, ulong seriesEndNs, ulong windowStartNs, ulong windowEndNs)
    {
        ulong overlapStart = Math.Max(seriesStartNs, windowStartNs), overlapEnd = Math.Min(seriesEndNs, windowEndNs);
        double fraction = windowEndNs <= windowStartNs
            ? (seriesStartNs <= windowStartNs && windowStartNs <= seriesEndNs ? 1 : 0)
            : overlapEnd > overlapStart ? (double)(overlapEnd - overlapStart) / (windowEndNs - windowStartNs) : 0;
        string state = fraction >= VerifiedOverlap ? "verified" : fraction >= PartialOverlap ? "partial" : "mismatch";
        return new(state, Math.Round(fraction, 4), seriesStartNs, seriesEndNs, windowStartNs, windowEndNs);
    }

    /// <summary>Percent-unit counters by average, largest first; null with fewer than three.</summary>
    public static UtilizationRankingDto? Rank(IReadOnlyList<(string Counter, string Unit, double? Average)> counters)
    {
        var percent = counters.Where(c => c.Unit == "percent" && c.Average is double a && double.IsFinite(a))
            .OrderByDescending(c => c.Average).ThenBy(c => c.Counter, StringComparer.Ordinal).ToList();
        if (percent.Count < MinRankedCounters) return null;
        UtilizationRankDto[] ranked = percent.Select((c, i) => new UtilizationRankDto(c.Counter, Math.Round(c.Average!.Value, 2), i + 1)).ToArray();
        double margin = Math.Round(ranked[0].Average - ranked[1].Average, 2);
        return new("highest average among percent-unit counters over the selected window", "heuristic", ranked,
            margin >= LimiterMarginPoints ? ranked[0].Counter : null, margin, LimiterMarginPoints,
            "A saturated lane suggests where the GPU waits; it is not a verdict. Confirm with timing, counters and Dr. PIX before optimizing.");
    }

    private static int LastAtOrBefore(int count, Func<int, ulong> time, ulong at)
    {
        int lo = 0, hi = count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (time(mid) <= at) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }

    private static int FirstAtOrAfter(int count, Func<int, ulong> time, ulong at)
    {
        int lo = 0, hi = count;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (time(mid) < at) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
