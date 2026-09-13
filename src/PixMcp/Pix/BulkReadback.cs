using PixMcp.Pix.Handles;

namespace PixMcp.Pix;

/// <summary>One event's replay timing as PIX reports it; every field is <see cref="None"/> when the event has no timing.</summary>
public readonly record struct TimingSample(ulong TopStart, ulong TopDuration, ulong EopStart, ulong EopDuration)
{
    public const ulong None = ulong.MaxValue;
    public static readonly TimingSample Missing = new(None, None, None, None);
    public bool IsNone => TopStart == None && TopDuration == None && EopStart == None && EopDuration == None;
}

/// <summary>Reads replay timing for a queue. The PIX implementation wraps IPixGpuCaptureTiming; tests use fakes.</summary>
public interface ITimingReader
{
    /// <summary>Event-indexed entries PIX holds for the queue, or null when the bulk API is unavailable (<paramref name="error"/> says why).</summary>
    ulong? QueueDataCount(int queueIndex, out Exception? error);
    TimingSample QueueData(int queueIndex, ulong index);
    bool HasEventData(int queueIndex, uint eventIndex);
    TimingSample EventData(int queueIndex, uint eventIndex);
}

/// <summary>Reads collected counter values for a queue. The PIX implementation wraps IPixGpuCaptureCounterData; tests use fakes.</summary>
public interface ICounterReader
{
    ulong? QueueDataCount(int queueIndex, out Exception? error);
    /// <summary>The raw value bits, or <see cref="TimingSample.None"/> when the event has no data for the counter.</summary>
    ulong QueueData(uint counterId, int queueIndex, ulong index);
    bool HasEventData(uint counterId, int queueIndex, uint eventIndex);
    ulong EventData(uint counterId, int queueIndex, uint eventIndex);
}

/// <summary>How a queue's timing rows were read: <c>bulk</c> (event-indexed readback) or <c>perEvent</c> (one call pair per event).</summary>
public sealed record ReadbackCoverage(string Readback, string? Reason, int Events, int TimedEvents, int InteropCalls, int FailedReads, string? FirstError,
    ReadbackVerification? Verify = null);

/// <summary>Result of comparing sampled bulk rows against the per-event API (PIXMCP_VERIFY_BULK_READBACK=1).</summary>
public sealed record ReadbackVerification(int Sampled, int Mismatches, string? FirstMismatch);

/// <summary>Readback state of one counter on one queue.</summary>
public sealed record CounterQueueCoverage(uint CounterId, string Readback, string? Reason, int Cells, int DataCells, int FailedCells, string? FirstError, bool Unavailable);

/// <summary>
/// Event-indexed ("bulk") readback of timing and counter data with the per-event API as fallback. Pure over the reader
/// seams: no PIX types, so the count-mismatch, failure and probe rules are unit-tested.
/// </summary>
public static class BulkReadback
{
    public const string Bulk = "bulk", PerEvent = "perEvent";
    public const int CancellationStride = 256;
    /// <summary>Consecutive same-HRESULT failures before the per-event API is probed once to tell "no data" from "unreadable".</summary>
    public const int DatalessProbeThreshold = 8;
    public const int VerifySamples = 50;

    public static EventTimingRow[] TimingRows(ITimingReader reader, int queueIndex, EventRecord[] events, CancellationToken cancellationToken, out ReadbackCoverage coverage)
    {
        var rows = new List<EventTimingRow>();
        ulong? count = reader.QueueDataCount(queueIndex, out Exception? error);
        int calls = 1, failed = 0;
        string? firstError = error is null ? null : PixErrors.Describe(error);
        if (count == (ulong)events.Length)
        {
            for (int i = 0; i < events.Length; i++)
            {
                if (i % CancellationStride == 0) cancellationToken.ThrowIfCancellationRequested();
                calls++;
                TimingSample sample;
                try { sample = reader.QueueData(queueIndex, (ulong)i); }
                catch (Exception ex) { failed++; firstError ??= PixErrors.Describe(ex); continue; }
                if (!sample.IsNone) rows.Add(Row(queueIndex, events[i], sample));
            }
            coverage = new(Bulk, null, events.Length, rows.Count, calls, failed, firstError);
            return rows.ToArray();
        }
        string reason = count is null ? "bulkUnavailable" + (error is null || PixErrors.HResultOf(error) is not int hr ? "" : ":" + PixErrors.Hex(hr))
            : $"countMismatch:{count}/{events.Length}";
        for (int i = 0; i < events.Length; i++)
        {
            if (i % CancellationStride == 0) cancellationToken.ThrowIfCancellationRequested();
            try
            {
                calls++;
                if (!reader.HasEventData(queueIndex, events[i].Index)) continue;
                calls++;
                TimingSample sample = reader.EventData(queueIndex, events[i].Index);
                if (!sample.IsNone) rows.Add(Row(queueIndex, events[i], sample));
            }
            catch (Exception ex) { failed++; firstError ??= PixErrors.Describe(ex); }
        }
        coverage = new(PerEvent, reason, events.Length, rows.Count, calls, failed, firstError);
        return rows.ToArray();
    }

    private static EventTimingRow Row(int queueIndex, EventRecord e, TimingSample s)
        => new(queueIndex, e.Index, e.GpuId, e.Name, e.ApiCallData, s.TopStart, s.TopDuration, s.EopStart, s.EopDuration);

    /// <summary>Compares up to <paramref name="samples"/> evenly spaced events of a bulk readback against the per-event API. Never throws for a mismatch.</summary>
    public static ReadbackVerification Verify(ITimingReader reader, int queueIndex, EventRecord[] events, IReadOnlyList<EventTimingRow> rows, int samples = VerifySamples)
    {
        if (events.Length == 0) return new(0, 0, null);
        var byIndex = rows.ToDictionary(r => r.Index);
        int sampled = 0, mismatches = 0; string? first = null;
        foreach (ulong i in Sampling.Indices((ulong)events.Length, Math.Max(2, samples)))
        {
            sampled++;
            uint index = events[(int)i].Index;
            TimingSample expected;
            try { expected = reader.HasEventData(queueIndex, index) ? reader.EventData(queueIndex, index) : TimingSample.Missing; }
            catch (Exception ex) { mismatches++; first ??= $"event {index}: per-event read failed: {PixErrors.Describe(ex)}"; continue; }
            bool has = byIndex.TryGetValue(index, out EventTimingRow? row);
            bool same = has
                ? row!.TopStart == expected.TopStart && row.TopDuration == expected.TopDuration && row.EopStart == expected.EopStart && row.EopDuration == expected.EopDuration
                : expected.IsNone;
            if (same) continue;
            mismatches++;
            first ??= $"event {index}: bulk {(has ? $"[{row!.TopStart},{row.TopDuration},{row.EopStart},{row.EopDuration}]" : "none")} vs perEvent [{expected.TopStart},{expected.TopDuration},{expected.EopStart},{expected.EopDuration}]";
        }
        return new(sampled, mismatches, first);
    }

    /// <summary>
    /// Reads every counter of a set for one queue, column-major. A failing cell nulls one value; eight consecutive
    /// same-HRESULT failures trigger one per-event probe: no data explains the run, an unreadable readable event marks
    /// the counter unavailable for the queue. counter_read_failed only when every counter is unavailable and nothing was read.
    /// </summary>
    public static CounterEventRow[] CounterRows(ICounterReader reader, CounterInfo[] counters, int queueIndex, EventRecord[] events, string handle,
        CancellationToken cancellationToken, out CounterQueueCoverage[] coverage)
    {
        int n = events.Length;
        var values = new object?[n][];
        for (int i = 0; i < n; i++) values[i] = new object?[counters.Length];
        var coverages = new CounterQueueCoverage[counters.Length];
        ulong? count = reader.QueueDataCount(queueIndex, out Exception? error);
        bool bulk = count == (ulong)n;
        string? reason = bulk ? null : count is null ? "bulkUnavailable" + (error is null || PixErrors.HResultOf(error) is not int hr ? "" : ":" + PixErrors.Hex(hr)) : $"countMismatch:{count}/{n}";
        bool anyValue = false;
        for (int c = 0; c < counters.Length; c++)
        {
            CounterInfo counter = counters[c];
            int data = 0, failed = 0, consecutive = 0; int? lastHResult = null; string? firstError = null; bool unavailable = false, datalessRun = false;
            for (int i = 0; i < n && !unavailable; i++)
            {
                if (i % CancellationStride == 0) cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    ulong bits = bulk ? reader.QueueData(counter.Id, queueIndex, (ulong)i)
                        : reader.HasEventData(counter.Id, queueIndex, events[i].Index) ? reader.EventData(counter.Id, queueIndex, events[i].Index) : TimingSample.None;
                    consecutive = 0; datalessRun = false;
                    if (bits == TimingSample.None) continue;
                    values[i][c] = Interop.NumericValue(bits, counter.FormatSpecifier);
                    data++; anyValue = true;
                }
                catch (Exception ex)
                {
                    failed++; firstError ??= PixErrors.Describe(ex);
                    int? hresult = PixErrors.HResultOf(ex);
                    consecutive = hresult == lastHResult ? consecutive + 1 : 1; lastHResult = hresult;
                    if (consecutive < DatalessProbeThreshold || datalessRun) continue;
                    bool has;
                    try { has = reader.HasEventData(counter.Id, queueIndex, events[i].Index); } catch { has = true; }
                    if (!has) { datalessRun = true; continue; }
                    try { reader.EventData(counter.Id, queueIndex, events[i].Index); consecutive = 0; }
                    catch { unavailable = true; }
                }
            }
            coverages[c] = new(counter.Id, bulk ? Bulk : PerEvent, reason, n, data, failed, firstError, unavailable);
        }
        if (counters.Length > 0 && !anyValue && coverages.All(c => c.Unavailable))
            throw PixErrors.CounterReadFailed($"No counter of the set could be read on queue {queueIndex}: {coverages[0].FirstError}", handle);
        coverage = coverages;
        var rows = new CounterEventRow[n];
        for (int i = 0; i < n; i++) rows[i] = new(events[i], values[i], values[i].Any(v => v is not null));
        return rows;
    }
}
