using Microsoft.PIX;
using PixMcp.Pix.Handles;

namespace PixMcp.Pix;

/// <summary>
/// One collected counter set: PIX's data object, the counter metadata, and the per-queue rows materialised from it.
/// A cache is either <c>exact</c> (its own replay) or <c>projectedFromSuperset</c> (columns copied from a larger set
/// that had already been materialised, so no replay ran).
/// </summary>
public sealed class CounterCollectionCache(IPixGpuCaptureCounterData data, CounterInfo[] counters, string source = CounterCollectionCache.Exact, string? supersetKey = null)
{
    public const string Exact = "exact", Projected = "projectedFromSuperset";
    public IPixGpuCaptureCounterData Data { get; } = data;
    public CounterInfo[] Counters { get; } = counters;
    public Dictionary<int, CounterEventRow[]> RowsByQueue { get; } = new();
    /// <summary>Readback state per queue, one entry per counter (in <see cref="Counters"/> order).</summary>
    public Dictionary<int, CounterQueueCoverage[]> CoverageByQueue { get; } = new();
    public string Source { get; } = source;
    public string? SupersetKey { get; } = supersetKey;

    public CounterEventRow[] GetOrCreateRows(int queueIndex, Func<CounterEventRow[]> create)
    {
        if (RowsByQueue.TryGetValue(queueIndex, out CounterEventRow[]? rows)) return rows;
        rows = create();
        RowsByQueue.Add(queueIndex, rows);
        return rows;
    }

    /// <summary>True when every counter in <paramref name="ids"/> is in this set.</summary>
    public bool Covers(IEnumerable<uint> ids) => ids.All(id => Counters.Any(c => c.Id == id));

    /// <summary>A cache for the subset <paramref name="ids"/> whose rows are copied column-wise from this (materialised) set; no replay.</summary>
    public CounterCollectionCache Project(uint[] ids, string supersetKey)
    {
        int[] columns = ids.Select(id => Array.FindIndex(Counters, c => c.Id == id)).ToArray();
        if (columns.Any(c => c < 0)) throw new ArgumentException("The subset must be covered by this set.", nameof(ids));
        var projected = new CounterCollectionCache(Data, columns.Select(c => Counters[c]).ToArray(), Projected, supersetKey);
        foreach ((int queue, CounterEventRow[] rows) in RowsByQueue)
            projected.RowsByQueue[queue] = rows.Select(row =>
            {
                object?[] values = columns.Select(c => row.Values[c]).ToArray();
                return new CounterEventRow(row.Event, values, values.Any(v => v is not null));
            }).ToArray();
        foreach ((int queue, CounterQueueCoverage[] coverage) in CoverageByQueue)
            projected.CoverageByQueue[queue] = columns.Select(c => coverage[c]).ToArray();
        return projected;
    }
}

public sealed record CounterEventRow(EventRecord Event, object?[] Values, bool HasData);

public sealed record HfCounterSamples(string Counter, IPixGpuCaptureHighFrequencyCounter NativeCounter,
    ulong BatchId, ulong SampleCount, double? Min, double? Max, double? Average, string? Error = null);

public sealed record HfCollectionCache(string Set, IPixGpuCaptureHighFrequencyCounterData Data,
    IPixGpuCaptureCounterCollection CounterSet, HfCounterSamples[] Counters);

/// <summary>Bounded, evenly spaced samples that retain both endpoints without overflowing ulong.</summary>
internal static class Sampling
{
    public static T[] Select<T>(ulong count, int limit, Func<ulong, T> read)
        => Indices(count, limit).Select(read).ToArray();

    public static IEnumerable<ulong> Indices(ulong count, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 2);
        ulong take = Math.Min(count, (ulong)limit);
        if (take == 0) yield break;
        if (take == 1) { yield return 0; yield break; }
        ulong denominator = take - 1;
        ulong quotient = (count - 1) / denominator;
        ulong remainder = (count - 1) % denominator;
        for (ulong i = 0; i < take; i++)
            yield return i * quotient + i * remainder / denominator;
    }
}

/// <summary>Exposes exactly one original collection item, preserving PIX's interface negotiation.</summary>
internal sealed unsafe class SelectedPixCollection(IPixCollection source, ulong selectedIndex) : IPixCollection
{
    public ulong GetCount() => 1;

    public void Get(ulong index, Guid* riid, out object item)
    {
        if (index != 0) throw new ArgumentOutOfRangeException(nameof(index));
        source.Get(selectedIndex, riid, out item);
    }
}
