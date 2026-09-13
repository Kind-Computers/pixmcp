using System.Runtime.InteropServices;
using System.Text;
using Microsoft.PIX;
using PixMcp.Pix;
using PixMcp.Pix.Handles;
using Xunit;

namespace PixMcp.Tests;

/// <summary>R07: event-indexed readback with the per-event API as fallback, subset reuse, and marker-row semantics.</summary>
public sealed class BulkReadbackTests
{
    private static EventRecord[] Events(int count, uint parentOfAll = uint.MaxValue)
        => Enumerable.Range(0, count).Select(i => new EventRecord((uint)i, (uint)i + 1, i == 0 ? uint.MaxValue : parentOfAll, i == 0 ? "Frame" : "Draw" + i, i == 0 ? "" : "Draw(" + i + ")", 0, 0)).ToArray();

    private sealed class FakeTimingReader : ITimingReader
    {
        public ulong? Count; public Exception? CountError;
        public Dictionary<ulong, TimingSample> Bulk = new();
        public Dictionary<uint, TimingSample> PerEvent = new();
        public HashSet<ulong> FailingBulk = new();
        public int BulkCalls, HasCalls, EventCalls;
        public ulong? QueueDataCount(int queueIndex, out Exception? error) { error = CountError; return Count; }
        public TimingSample QueueData(int queueIndex, ulong index)
        {
            BulkCalls++;
            if (FailingBulk.Contains(index)) throw new COMException("cell", unchecked((int)0x80004005));
            return Bulk.TryGetValue(index, out TimingSample s) ? s : TimingSample.Missing;
        }
        public bool HasEventData(int queueIndex, uint eventIndex) { HasCalls++; return PerEvent.ContainsKey(eventIndex); }
        public TimingSample EventData(int queueIndex, uint eventIndex) { EventCalls++; return PerEvent[eventIndex]; }
    }

    private sealed class FakeCounterReader : ICounterReader
    {
        public ulong? Count;
        public Func<uint, ulong, ulong>? Bulk;                 // (counterId, index) -> bits; throw to fail
        public Func<uint, uint, bool> Has = (_, _) => false;
        public Func<uint, uint, ulong> Event = (_, _) => throw new COMException("event", unchecked((int)0x80004005));
        public int BulkCalls, HasCalls, EventCalls;
        public ulong? QueueDataCount(int queueIndex, out Exception? error) { error = null; return Count; }
        public ulong QueueData(uint counterId, int queueIndex, ulong index) { BulkCalls++; return Bulk!(counterId, index); }
        public bool HasEventData(uint counterId, int queueIndex, uint eventIndex) { HasCalls++; return Has(counterId, eventIndex); }
        public ulong EventData(uint counterId, int queueIndex, uint eventIndex) { EventCalls++; return Event(counterId, eventIndex); }
    }

    private static CounterInfo Counter(uint id) => new(id, "Counter " + id, "", "UINT64", []) { FormatSpecifier = (PIX_FORMAT_SPECIFIER_TYPE)((uint)PIX_FORMAT_SPECIFIER_TYPE.PIX_FORMAT_SPECIFIER_TYPE_UINT | (uint)PIX_FORMAT_SPECIFIER_TYPE.PIX_FORMAT_SPECIFIER_SIZE_64BIT) };

    [Fact]
    public void BulkPathReadsEveryIndexOnceAndSkipsNoneRows()
    {
        EventRecord[] events = Events(5);
        var reader = new FakeTimingReader { Count = 5, Bulk = { [1] = new(10, 5, 12, 8), [3] = new(30, 1, 31, 2) } };
        EventTimingRow[] rows = BulkReadback.TimingRows(reader, 0, events, default, out ReadbackCoverage coverage);
        Assert.Equal(new uint[] { 1, 3 }, rows.Select(r => r.Index));
        Assert.Equal(8UL, rows[0].EopDuration);
        Assert.Equal("bulk", coverage.Readback);
        Assert.Null(coverage.Reason);
        Assert.Equal(5, coverage.Events); Assert.Equal(2, coverage.TimedEvents);
        Assert.Equal(6, coverage.InteropCalls); // one count call plus five reads
        Assert.Equal(0, reader.HasCalls); Assert.Equal(0, reader.EventCalls);
    }

    [Theory]
    [InlineData(4UL, "countMismatch:4/5")]
    [InlineData(6UL, "countMismatch:6/5")]
    [InlineData(0UL, "countMismatch:0/5")]
    public void CountMismatchFallsBackToThePerEventApiAndReportsWhy(ulong count, string reason)
    {
        EventRecord[] events = Events(5);
        var reader = new FakeTimingReader { Count = count, PerEvent = { [2] = new(1, 1, 2, 3) } };
        EventTimingRow[] rows = BulkReadback.TimingRows(reader, 0, events, default, out ReadbackCoverage coverage);
        Assert.Equal(new uint[] { 2 }, rows.Select(r => r.Index));
        Assert.Equal("perEvent", coverage.Readback);
        Assert.Equal(reason, coverage.Reason);
        Assert.Equal(0, reader.BulkCalls);
        Assert.Equal(5, reader.HasCalls); Assert.Equal(1, reader.EventCalls);
        Assert.Equal(1 + 5 + 1, coverage.InteropCalls);
    }

    [Fact]
    public void BulkUnavailableNamesTheHresultAndAnAllNoneQueueRaisesNothing()
    {
        EventRecord[] events = Events(3);
        var reader = new FakeTimingReader { Count = null, CountError = new COMException("no bulk", unchecked((int)0x80004001)) };
        EventTimingRow[] rows = BulkReadback.TimingRows(reader, 0, events, default, out ReadbackCoverage coverage);
        Assert.Empty(rows);
        Assert.Equal("perEvent", coverage.Readback);
        Assert.Equal("bulkUnavailable:0x80004001", coverage.Reason);
        Assert.Contains("no bulk", coverage.FirstError);
        var none = new FakeTimingReader { Count = 3 };
        Assert.Empty(BulkReadback.TimingRows(none, 0, events, default, out ReadbackCoverage all));
        Assert.Equal("bulk", all.Readback); Assert.Equal(0, all.TimedEvents); Assert.Null(all.FirstError);
    }

    [Fact]
    public void AFailingBulkReadDropsOneRowAndIsCounted()
    {
        EventRecord[] events = Events(4);
        var reader = new FakeTimingReader { Count = 4, Bulk = { [1] = new(1, 1, 1, 1), [2] = new(2, 2, 2, 2) }, FailingBulk = { 2 } };
        EventTimingRow[] rows = BulkReadback.TimingRows(reader, 0, events, default, out ReadbackCoverage coverage);
        Assert.Equal(new uint[] { 1 }, rows.Select(r => r.Index));
        Assert.Equal(1, coverage.FailedReads);
        Assert.Contains("cell", coverage.FirstError);
        Assert.Equal("bulk", coverage.Readback);
    }

    [Fact]
    public void VerificationComparesSampledRowsAgainstThePerEventApi()
    {
        EventRecord[] events = Events(10);
        var reader = new FakeTimingReader { Count = 10 };
        for (uint i = 0; i < 10; i++) { var s = new TimingSample(i, 1, i, 1); reader.Bulk[i] = s; reader.PerEvent[i] = s; }
        EventTimingRow[] rows = BulkReadback.TimingRows(reader, 0, events, default, out _);
        ReadbackVerification ok = BulkReadback.Verify(reader, 0, events, rows, 4);
        Assert.Equal(4, ok.Sampled); Assert.Equal(0, ok.Mismatches);
        reader.PerEvent[9] = new(9, 2, 9, 1);
        ReadbackVerification bad = BulkReadback.Verify(reader, 0, events, rows, 4);
        Assert.Equal(1, bad.Mismatches);
        Assert.Contains("event 9", bad.FirstMismatch);
        Assert.Equal(new ReadbackVerification(0, 0, null), BulkReadback.Verify(reader, 0, [], [], 4));
    }

    [Fact]
    public void CountersReadColumnMajorWithNoneCellsAndAFailingCellNullsOneValue()
    {
        EventRecord[] events = Events(4);
        var reader = new FakeCounterReader
        {
            Count = 4,
            Bulk = (id, index) => (id, index) switch
            {
                (7, 2) => throw new COMException("cell", unchecked((int)0x80004005)),
                (7, _) => 100 + index,
                (9, 1) => TimingSample.None,
                (9, _) => 900 + index,
                _ => throw new InvalidOperationException(),
            },
        };
        CounterEventRow[] rows = BulkReadback.CounterRows(reader, [Counter(7), Counter(9)], 0, events, "gpu-1", default, out CounterQueueCoverage[] coverage);
        Assert.Equal(4, rows.Length);
        Assert.Equal(100UL, rows[0].Values[0]); Assert.Null(rows[2].Values[0]); Assert.Equal(103UL, rows[3].Values[0]);
        Assert.Null(rows[1].Values[1]); Assert.Equal(902UL, rows[2].Values[1]);
        Assert.All(rows, r => Assert.True(r.HasData));
        Assert.Equal(new[] { "bulk", "bulk" }, coverage.Select(c => c.Readback));
        Assert.Equal(3, coverage[0].DataCells); Assert.Equal(1, coverage[0].FailedCells); Assert.False(coverage[0].Unavailable);
        Assert.Equal(3, coverage[1].DataCells); Assert.Equal(0, coverage[1].FailedCells);
        Assert.Equal(8, reader.BulkCalls);
    }

    [Fact]
    public void EightSameHresultFailuresProbeOnceAndADatalessRunContinues()
    {
        EventRecord[] events = Events(20);
        int probes = 0;
        var reader = new FakeCounterReader
        {
            Count = 20,
            Bulk = (id, index) => index < 12 ? throw new COMException("nodata", unchecked((int)0x80004005)) : 5UL,
            Has = (_, _) => { probes++; return false; },
        };
        CounterEventRow[] rows = BulkReadback.CounterRows(reader, [Counter(1)], 0, events, "gpu-1", default, out CounterQueueCoverage[] coverage);
        Assert.Equal(1, probes);
        Assert.Equal(8, coverage[0].DataCells);
        Assert.Equal(12, coverage[0].FailedCells);
        Assert.False(coverage[0].Unavailable);
        Assert.Equal(20, reader.BulkCalls);
        Assert.Equal(5UL, rows[19].Values[0]);
    }

    [Fact]
    public void AReadableEventThatStillFailsMarksTheCounterUnavailableAndAllUnavailableIsCounterReadFailed()
    {
        EventRecord[] events = Events(20);
        var reader = new FakeCounterReader
        {
            Count = 20,
            Bulk = (_, _) => throw new COMException("broken", unchecked((int)0x8ABC0001)),
            Has = (_, _) => true,
        };
        PixToolException error = Assert.Throws<PixToolException>(() => BulkReadback.CounterRows(reader, [Counter(1), Counter(2)], 0, events, "gpu-1", default, out _));
        Assert.Equal(PixErrors.Codes.CounterReadFailed, error.Detail.Code);
        Assert.Equal(new[] { "pix_gpu_analysis_stop", "pix_gpu_counters_list" }, error.Detail.NextCalls.Select(c => c.Tool));
        Assert.Equal(2, reader.EventCalls); // one probe per counter, both fail
        Assert.True(reader.BulkCalls < 40, "the counter stopped after the probe instead of trying every cell");

        // One readable counter keeps the queue usable; the broken one is reported per counter.
        var partial = new FakeCounterReader
        {
            Count = 20,
            Bulk = (id, index) => id == 2 ? throw new COMException("broken", unchecked((int)0x8ABC0001)) : 1UL,
            Has = (_, _) => true,
        };
        CounterEventRow[] rows = BulkReadback.CounterRows(partial, [Counter(1), Counter(2)], 0, events, "gpu-1", default, out CounterQueueCoverage[] coverage);
        Assert.False(coverage[0].Unavailable); Assert.True(coverage[1].Unavailable);
        Assert.Equal(20, coverage[0].DataCells);
        Assert.All(rows, r => Assert.True(r.HasData));
    }

    [Fact]
    public void CounterPerEventFallbackUsesHasAndGetEventData()
    {
        EventRecord[] events = Events(3);
        var reader = new FakeCounterReader { Count = 2, Has = (_, i) => i == 1, Event = (_, i) => 42 };
        CounterEventRow[] rows = BulkReadback.CounterRows(reader, [Counter(1)], 0, events, "gpu-1", default, out CounterQueueCoverage[] coverage);
        Assert.Equal("perEvent", coverage[0].Readback);
        Assert.Equal("countMismatch:2/3", coverage[0].Reason);
        Assert.Equal(42UL, rows[1].Values[0]); Assert.Null(rows[0].Values[0]);
        Assert.Equal(0, reader.BulkCalls); Assert.Equal(3, reader.HasCalls); Assert.Equal(1, reader.EventCalls);
    }

    [Fact]
    public void ProjectingASubsetCopiesColumnsRowsAndCoverageWithoutTheDataObject()
    {
        var superset = new CounterCollectionCache(null!, [Counter(1), Counter(2), Counter(3)]);
        EventRecord[] events = Events(2);
        superset.RowsByQueue[0] = [new(events[0], [1UL, null, 3UL], true), new(events[1], [null, null, null], false)];
        superset.CoverageByQueue[0] = [new(1, "bulk", null, 2, 1, 0, null, false), new(2, "bulk", null, 2, 0, 0, null, false), new(3, "bulk", null, 2, 1, 0, null, false)];
        Assert.True(superset.Covers([3, 1])); Assert.False(superset.Covers([4]));
        CounterCollectionCache projected = superset.Project([3, 1], "1,2,3");
        Assert.Equal(CounterCollectionCache.Projected, projected.Source);
        Assert.Equal("1,2,3", projected.SupersetKey);
        Assert.Equal(new uint[] { 3, 1 }, projected.Counters.Select(c => c.Id));
        Assert.Equal(new object?[] { 3UL, 1UL }, projected.RowsByQueue[0][0].Values);
        Assert.False(projected.RowsByQueue[0][1].HasData);
        Assert.Equal(new uint[] { 3, 1 }, projected.CoverageByQueue[0].Select(c => c.CounterId));
        CounterCollectionCache twice = superset.Project([2], "1,2,3");
        Assert.False(twice.RowsByQueue[0][0].HasData); // the only column is null
        Assert.Throws<ArgumentException>(() => superset.Project([9], "1,2,3"));
    }

    [Fact]
    public void ACounterPageWithTenCountersStaysUnderTheInlineBudget()
    {
        CounterInfo[] counters = Enumerable.Range(1, 10).Select(i => Counter((uint)i)).ToArray();
        var rows = Enumerable.Range(0, 25).Select(i => new CounterValueRowDto(new EventRef("gpu-1", 0, (uint)i), (uint)i, (uint)i, "DrawIndexedInstanced",
            ["Frame", "Shadow pass", "Cascade " + i % 4], counters.ToDictionary(c => c.Id.ToString(), c => (object?)(ulong)(i * 1000 + c.Id)), i % 5 == 0 ? "marker" : "event", i % 5 == 0 ? 4 : null)).ToList();
        object extra = new
        {
            counters = counters.Select(c => new { id = c.Id, name = c.Name, description = c.Description, dataType = c.DataType, unit = "unknown" }).ToArray(),
            rollupSemantics = PixMcp.Tools.CountersTools.RoundsRule, rollupAvailable = true,
            warning = "This page mixes marker rows and event rows; do not add them together (see rollupSemantics).",
            coverage = counters.Select(c => new CounterQueueCoverage(c.Id, "bulk", null, 79, 40, 0, null, false)).ToArray(),
            collection = new { key = "1,2,3,4,5,6,7,8,9,10", source = "exact", supersetKey = (string?)null, replayed = true },
        };
        object page = Shaping.Apply(rows, 79, 0, 25, new ShapingOptions(), RowShapes.Counters(counters), "gpu-1", extra,
            at => new("pix_gpu_counters_read", new { handle = "gpu-1", offset = at }), null);
        Assert.True(Encoding.UTF8.GetByteCount(Json.Serialize(page)) < ResultStore.TargetBytes);
        object table = Shaping.Apply(rows, 79, 0, 25, new ShapingOptions("table"), RowShapes.Counters(counters), "gpu-1", extra, null, null);
        TableDto dto = Assert.IsType<TableDto>(table);
        Assert.Contains(dto.Columns, c => c.Name == "rowKind");
        Assert.Equal("marker", dto.Rows[0][dto.Columns.ToList().FindIndex(c => c.Name == "rowKind")]);
    }
}
