namespace PixMcp.Pix;

/// <summary>Pure arithmetic over recorded (timing capture) intervals in capture-clock nanoseconds; no SQLite or PIX types.</summary>
internal static class RecordedTiming
{
    public static double Ms(long ns) => Math.Round(ns / 1e6, 3);

    /// <summary>Percentage rounded to 2 decimals, or null when the denominator is not positive.</summary>
    public static double? Percent(long part, long whole) => whole <= 0 ? null : Math.Round(100.0 * part / whole, 2);

    /// <summary>A duration with its share of a lane span and of a lane's summed durations.</summary>
    public static DurationDto Duration(long ns, long spanNs, long sumNs, long? parentNs = null, int? rank = null)
        => new((ulong)Math.Max(0, ns), Ms(ns), Percent(ns, spanNs), Percent(ns, sumNs), parentNs is long parent ? Percent(ns, parent) : null, rank);

    /// <summary>Nearest-rank percentile of an ascending array: the value at rank ceil(p * n / 100).</summary>
    public static long NearestRank(IReadOnlyList<long> sorted, int percentile)
    {
        if (sorted.Count == 0) throw new ArgumentException("At least one value is required.", nameof(sorted));
        int rank = (percentile * sorted.Count + 99) / 100;
        return sorted[Math.Clamp(rank, 1, sorted.Count) - 1];
    }

    public static RecordedStatsDto? Stats(IEnumerable<long> values)
    {
        long[] sorted = values.Order().ToArray();
        if (sorted.Length == 0) return null;
        double mean = 0;
        foreach (long value in sorted) mean += value;
        mean /= sorted.Length;
        long avg = (long)Math.Round(mean, MidpointRounding.AwayFromZero), p50 = NearestRank(sorted, 50), p95 = NearestRank(sorted, 95), max = sorted[^1];
        return new(sorted.Length, avg, p50, p95, max, Ms(avg), Ms(p50), Ms(p95), Ms(max));
    }

    /// <summary>Half-open intervals merged into a sorted, disjoint list (touching intervals join).</summary>
    public static List<(long Start, long End)> Merge(IEnumerable<(long Start, long End)> intervals)
    {
        var merged = new List<(long Start, long End)>();
        foreach (var (start, end) in intervals.Where(i => i.End > i.Start).OrderBy(i => i.Start).ThenBy(i => i.End))
        {
            if (merged.Count > 0 && start <= merged[^1].End)
            {
                if (end > merged[^1].End) merged[^1] = (merged[^1].Start, end);
                continue;
            }
            merged.Add((start, end));
        }
        return merged;
    }

    /// <summary>Total length covered by a set of half-open intervals (overlaps counted once).</summary>
    public static long UnionLength(IEnumerable<(long Start, long End)> intervals) => Merge(intervals).Sum(i => i.End - i.Start);

    /// <summary>Length of [start, end) covered by a merged (sorted, disjoint) interval list.</summary>
    public static long CoveredLength(IReadOnlyList<(long Start, long End)> merged, long start, long end)
    {
        if (end <= start || merged.Count == 0) return 0;
        int low = 0, high = merged.Count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (merged[middle].End <= start) low = middle + 1; else high = middle;
        }
        long covered = 0;
        for (int i = low; i < merged.Count && merged[i].Start < end; i++)
            covered += Math.Min(end, merged[i].End) - Math.Max(start, merged[i].Start);
        return covered;
    }

    /// <summary>
    /// Busy (union), idle, span and summed length of intervals clipped to [windowStart, windowEnd); null when no interval
    /// has a positive length inside the window.
    /// </summary>
    public static RecordedLaneTotalsDto? Totals(IEnumerable<(long Begin, long End)> intervals, long windowStart, long windowEnd)
    {
        var clipped = new List<(long Start, long End)>();
        long sum = 0, first = long.MaxValue, last = long.MinValue;
        foreach (var (begin, end) in intervals)
        {
            long s = Math.Max(begin, windowStart), e = Math.Min(end, windowEnd);
            if (e <= s) continue;
            clipped.Add((s, e));
            sum += e - s;
            first = Math.Min(first, s);
            last = Math.Max(last, e);
        }
        if (clipped.Count == 0) return null;
        long busy = UnionLength(clipped), span = last - first, idle = Math.Max(0, span - busy);
        return new(Duration(busy, span, sum), Duration(idle, span, 0), Duration(span, span, 0), Duration(sum, span, sum), sum > busy,
            Percent(busy, windowEnd - windowStart), TimingDatabase.Ns(first), TimingDatabase.Ns(last));
    }
}
