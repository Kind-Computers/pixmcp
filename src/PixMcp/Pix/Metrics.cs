using PixMcp.Pix.Handles;

namespace PixMcp.Pix;

/// <summary>A GPU duration together with the denominators an investigator needs to judge it.</summary>
/// <param name="Ns">Nanoseconds on the replay clock.</param>
/// <param name="Ms">Milliseconds, rounded to 3 decimals.</param>
/// <param name="PercentOfQueueSpan">Share of the queue wall span (first EOP start to last EOP end); null when the span is 0.</param>
/// <param name="PercentOfQueueSum">Share of the sum of top-level inclusive values; overstates when roots overlap; null when the sum is 0.</param>
/// <param name="PercentOfParent">Share of the parent's inclusive value; null without a parent.</param>
/// <param name="Rank">1-based position in the list or sibling order that produced this value; null when not ranked.</param>
public sealed record DurationDto(ulong Ns, double Ms, double? PercentOfQueueSpan, double? PercentOfQueueSum, double? PercentOfParent, int? Rank);

/// <summary>Whole-queue replay totals every percentage is computed against.</summary>
/// <param name="BusyNs">Length of the union of [TopStart, EopEnd) windows of timed leaf events (EOP-only windows when TOP is unavailable).</param>
/// <param name="SpanNs">First EOP start to last EOP end on the replay clock.</param>
/// <param name="IdleNs">SpanNs minus BusyNs.</param>
/// <param name="SumOfRootsNs">Sum of the inclusive values of the top-level events; equals SpanNs only when nothing overlaps.</param>
/// <param name="RootsOverlap">True when SumOfRootsNs exceeds SpanNs, so percentOfQueueSum overstates.</param>
/// <param name="BusySource">topEop when TOP timestamps contributed, eopOnly otherwise.</param>
public sealed record QueueTotals(int QueueIndex, ulong BusyNs, ulong SpanNs, ulong IdleNs, ulong SumOfRootsNs, bool RootsOverlap,
    int TimedEvents, int UntimedEvents, ulong FirstEopStartNs, ulong LastEopEndNs, string BusySource)
{
    public double BusyMs => Metrics.Ms(BusyNs);
    public double SpanMs => Metrics.Ms(SpanNs);
    public double IdleMs => Metrics.Ms(IdleNs);
    public double? IdlePercent => Metrics.Percent(IdleNs, SpanNs);
}

/// <summary>Explains every percentage field so a reader never has to guess the denominator.</summary>
public sealed record DenominatorsDto(string PercentOfQueueSpan, string PercentOfQueueSum, string PercentOfParent, string BusyNs);

/// <summary>Pure arithmetic over replay timing rows; no PIX types.</summary>
public static class Metrics
{
    public static readonly DenominatorsDto Denominators = new(
        "inclusive EOP / queue wall span (first EopStart to last EopEnd, replay clock)",
        "inclusive EOP / sum of top-level inclusive values (overstates when roots overlap)",
        "inclusive EOP / parent inclusive EOP",
        "union of [TopStart, EopEnd) windows of timed leaf events; eopOnly windows start at EopStart");

    public static double Ms(ulong ns) => Math.Round(ns / 1e6, 3);

    /// <summary>Percentage rounded to 2 decimals, or null when the denominator is 0 (never 0.0).</summary>
    public static double? Percent(ulong part, ulong whole) => whole == 0 ? null : Math.Round(100.0 * part / whole, 2);

    public static DurationDto Duration(ulong ns, QueueTotals? totals, ulong? parentInclusiveNs = null, int? rank = null)
        => new(ns, Ms(ns), Percent(ns, totals?.SpanNs ?? 0), Percent(ns, totals?.SumOfRootsNs ?? 0),
            parentInclusiveNs.HasValue ? Percent(ns, parentInclusiveNs.Value) : null, rank);

    /// <summary>
    /// Queue totals from timing rows. <paramref name="excludeFromBusy"/> names events (by index) whose windows must not
    /// feed the busy union, typically markers with children, because PIX times a marker as the span of its contents and
    /// that span would bridge the idle gaps between the children.
    /// </summary>
    public static QueueTotals Totals(int queueIndex, IEnumerable<EventTimingRow> rows, ulong sumOfRootsNs, int eventCount,
        Func<uint, bool>? excludeFromBusy = null)
    {
        EventTimingRow[] timed = rows.Where(r => r.EopDuration != GpuCaptureHandle.TimingNone).ToArray();
        int timedEvents = timed.Select(r => r.Index).Distinct().Count();
        int untimedEvents = Math.Max(0, eventCount - timedEvents);
        if (timed.Length == 0)
            return new(queueIndex, 0, 0, 0, sumOfRootsNs, sumOfRootsNs > 0, 0, untimedEvents, 0, 0, "eopOnly");

        ulong first = timed.Min(r => r.EopStart);
        ulong last = timed.Max(r => r.EopStart + r.EopDuration);
        bool anyTop = timed.Any(r => r.TopStart != GpuCaptureHandle.TimingNone);
        var windows = new List<(ulong Start, ulong End)>();
        foreach (EventTimingRow r in timed)
        {
            if (excludeFromBusy is not null && excludeFromBusy(r.Index)) continue;
            ulong end = r.EopStart + r.EopDuration;
            ulong start = r.TopStart != GpuCaptureHandle.TimingNone && r.TopStart <= end ? r.TopStart : r.EopStart;
            // Clip to the EOP span so busy can never exceed the span it is compared with.
            start = Math.Max(start, first);
            end = Math.Min(end, last);
            if (end > start) windows.Add((start, end));
        }
        ulong busy = UnionLength(windows);
        ulong span = last - first;
        return new(queueIndex, busy, span, span > busy ? span - busy : 0, sumOfRootsNs, sumOfRootsNs > span,
            timedEvents, untimedEvents, first, last, anyTop ? "topEop" : "eopOnly");
    }

    /// <summary>Total length covered by a set of half-open intervals (overlaps counted once).</summary>
    public static ulong UnionLength(IEnumerable<(ulong Start, ulong End)> windows)
    {
        ulong total = 0;
        ulong? currentStart = null, currentEnd = null;
        foreach (var (start, end) in windows.Where(w => w.End > w.Start).OrderBy(w => w.Start).ThenBy(w => w.End))
        {
            if (currentEnd.HasValue && start <= currentEnd.Value)
            {
                if (end > currentEnd.Value) currentEnd = end;
                continue;
            }
            if (currentStart.HasValue) total += currentEnd!.Value - currentStart.Value;
            currentStart = start;
            currentEnd = end;
        }
        if (currentStart.HasValue) total += currentEnd!.Value - currentStart.Value;
        return total;
    }
}
