using Microsoft.PIX;
using PixMcp.Pix.Handles;

namespace PixMcp.Pix;

public sealed class CounterCollectionCache(IPixGpuCaptureCounterData data, CounterInfo[] counters)
{
    public IPixGpuCaptureCounterData Data { get; } = data;
    public CounterInfo[] Counters { get; } = counters;
    public Dictionary<int, CounterEventRow[]> RowsByQueue { get; } = new();

    public CounterEventRow[] GetOrCreateRows(int queueIndex, Func<CounterEventRow[]> create)
    {
        if (RowsByQueue.TryGetValue(queueIndex, out CounterEventRow[]? rows)) return rows;
        rows = create();
        RowsByQueue.Add(queueIndex, rows);
        return rows;
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
