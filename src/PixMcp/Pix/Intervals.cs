namespace PixMcp.Pix;

/// <summary>A half-open replay-clock interval [Start, End).</summary>
public readonly record struct Interval(ulong Start, ulong End)
{
    public ulong Length => End > Start ? End - Start : 0;
}

/// <summary>Pure arithmetic over sorted, merged interval lists (the output of <see cref="Union"/>).</summary>
internal static class Intervals
{
    /// <summary>Sorted, merged union: touching intervals merge and empty ones are dropped.</summary>
    public static List<Interval> Union(IEnumerable<Interval> intervals)
    {
        var merged = new List<Interval>();
        foreach (Interval i in intervals.Where(i => i.End > i.Start).OrderBy(i => i.Start).ThenBy(i => i.End))
        {
            if (merged.Count > 0 && i.Start <= merged[^1].End)
            {
                if (i.End > merged[^1].End) merged[^1] = merged[^1] with { End = i.End };
                continue;
            }
            merged.Add(i);
        }
        return merged;
    }

    public static ulong Length(IReadOnlyList<Interval> merged)
    {
        ulong total = 0;
        foreach (Interval i in merged) total += i.Length;
        return total;
    }

    /// <summary>Intersection of two merged lists by a two-pointer sweep.</summary>
    public static List<Interval> Intersection(IReadOnlyList<Interval> a, IReadOnlyList<Interval> b)
    {
        var result = new List<Interval>();
        int i = 0, j = 0;
        while (i < a.Count && j < b.Count)
        {
            ulong start = Math.Max(a[i].Start, b[j].Start), end = Math.Min(a[i].End, b[j].End);
            if (end > start) result.Add(new(start, end));
            if (a[i].End < b[j].End) i++;
            else j++;
        }
        return result;
    }

    /// <summary>The parts of a merged list inside [start, end).</summary>
    public static List<Interval> Clip(IReadOnlyList<Interval> merged, ulong start, ulong end)
    {
        var result = new List<Interval>();
        if (end <= start) return result;
        for (int i = FirstEndingAfter(merged, start); i < merged.Count && merged[i].Start < end; i++)
            result.Add(new(Math.Max(merged[i].Start, start), Math.Min(merged[i].End, end)));
        return result;
    }

    /// <summary>Length of [start, end) covered by a merged list, without allocating.</summary>
    public static ulong CoveredLength(IReadOnlyList<Interval> merged, ulong start, ulong end)
    {
        ulong total = 0;
        if (end <= start) return 0;
        for (int i = FirstEndingAfter(merged, start); i < merged.Count && merged[i].Start < end; i++)
            total += Math.Min(merged[i].End, end) - Math.Max(merged[i].Start, start);
        return total;
    }

    /// <summary>The parts of [start, end) that no interval of a merged list covers.</summary>
    public static List<Interval> Gaps(IReadOnlyList<Interval> merged, ulong start, ulong end)
    {
        var gaps = new List<Interval>();
        ulong cursor = start;
        foreach (Interval i in Clip(merged, start, end))
        {
            if (i.Start > cursor) gaps.Add(new(cursor, i.Start));
            cursor = Math.Max(cursor, i.End);
        }
        if (end > cursor) gaps.Add(new(cursor, end));
        return gaps;
    }

    private static int FirstEndingAfter(IReadOnlyList<Interval> merged, ulong at)
    {
        int lo = 0, hi = merged.Count;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (merged[mid].End <= at) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
