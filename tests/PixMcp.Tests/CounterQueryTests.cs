using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PixMcp.Pix;
using PixMcp.Pix.Handles;
using PixMcp.Tools;
using ToolHelpers = PixMcp.Tools.Tools;
using Xunit;

namespace PixMcp.Tests;

public class CounterQueryTests
{
    private static CounterEventRow Row(uint index, object? value)
        => new(new EventRecord(index, index, uint.MaxValue, "DrawInstanced", "", 0, 0), [value], value is not null);

    [Fact]
    public void FloatingSortRetainsTinyAndLargeFiniteValuesAndPlacesMissingLast()
    {
        CounterEventRow[] rows = [Row(0, 0.0), Row(1, double.Epsilon), Row(2, -double.Epsilon), Row(3, 1e100),
            Row(4, -1e100), Row(5, double.NaN), Row(6, null), Row(7, double.PositiveInfinity)];
        var ascending = CounterQuery.ApplyNumericQuery(rows, 0, false, null, null).Select(row => row.Event.Index);
        var descending = CounterQuery.ApplyNumericQuery(rows, 0, true, null, null).Select(row => row.Event.Index);
        Assert.Equal(new uint[] { 4, 2, 0, 1, 3, 5, 6, 7 }, ascending);
        Assert.Equal(new uint[] { 3, 1, 0, 2, 4, 5, 6, 7 }, descending);
        Assert.True(CounterQuery.IsNumeric(float.Epsilon));
        Assert.True(CounterQuery.IsNumeric((Half)0.5));
    }

    [Fact]
    public void FloatingThresholdsExcludeTinyPositiveValuesFromZeroUpperBound()
    {
        CounterEventRow[] rows = [Row(0, double.Epsilon), Row(1, -double.Epsilon), Row(2, 0.0), Row(3, 1e100), Row(4, null)];
        Assert.Equal(new uint[] { 2, 1 }, CounterQuery.ApplyNumericQuery(rows, 0, true, null, 0).Select(row => row.Event.Index));
        Assert.Equal(new uint[] { 3 }, CounterQuery.ApplyNumericQuery(rows, 0, true, decimal.MaxValue, null).Select(row => row.Event.Index));
        Assert.Equal(new uint[] { 2 }, CounterQuery.ApplyNumericQuery(rows, 0, true, 0, 0).Select(row => row.Event.Index));
    }

    [Fact]
    public void IntegerSortAndThresholdsPreserveAdjacentValuesAcrossFull64BitRange()
    {
        CounterEventRow[] rows = [Row(0, ulong.MaxValue - 1), Row(1, ulong.MaxValue), Row(2, 9007199254740992UL),
            Row(3, 9007199254740993UL), Row(4, long.MinValue), Row(5, long.MinValue + 1)];
        Assert.Equal(new uint[] { 1, 0, 3, 2, 5, 4 }, CounterQuery.ApplyNumericQuery(rows, 0, true, null, null).Select(row => row.Event.Index));
        Assert.Equal(new uint[] { 1 }, CounterQuery.ApplyNumericQuery(rows, 0, true, (decimal)ulong.MaxValue, null).Select(row => row.Event.Index));
        Assert.Equal(new uint[] { 5, 4 }, CounterQuery.ApplyNumericQuery(rows, 0, true, null, (decimal)long.MinValue + 1).Select(row => row.Event.Index));
    }

    [Fact]
    public void NumericQueriesRetainStableTiesAndDoNotTreatBooleanOrHexAsNumbers()
    {
        CounterEventRow[] rows = [Row(3, 7UL), Row(1, 7UL), Row(2, "0x7"), Row(0, true)];
        Assert.Equal(new uint[] { 1, 3, 0, 2 }, CounterQuery.ApplyNumericQuery(rows, 0, false, null, null).Select(row => row.Event.Index));
        Assert.Equal(new uint[] { 1, 3 }, CounterQuery.ApplyNumericQuery(rows, 0, false, 7, 7).Select(row => row.Event.Index));
    }

    [Fact]
    public async Task ConcurrentQueueQueriesShareOneCollectionAndBothQueuesArePrepared()
    {
        using var worker = new PixWorker();
        using var session = new PixSession(worker, NullLogger<PixSession>.Instance);
        var jobs = new JobManager(worker, session, () => null);
        var handle = session.Register(new CounterHandle());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        int collections = 0;
        Preparation<CounterHandle> preparation = CountersTools.SharedCounterPreparation<CounterHandle>(handle.Id, "7", _ => [0, 1], h => h.Cache,
            (h, _) =>
            {
                collections++;
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test did not release collection.");
                return h.Cache ??= new CounterCollectionCache(null!, []);
            },
            (_, cache, queue, _) => cache.GetOrCreateRows(queue, () => [Row((uint)queue, 7UL)]));
        try
        {
            Task<string> first = ToolHelpers.RunWhenReady(session, jobs, "pix_test_queue", handle.Id, preparation,
                h => new { queue = 0, rows = h.Cache!.RowsByQueue[0].Length }, 5, default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task<string> second = ToolHelpers.RunWhenReady(session, jobs, "pix_test_queue", handle.Id, preparation,
                h => new { queue = 1, rows = h.Cache!.RowsByQueue[1].Length }, 5, default);
            release.Set();
            string[] answers = await Task.WhenAll(first, second);
            Assert.Equal(1, collections);
            Assert.Single(jobs.All);
            Assert.True(preparation.IsReady(handle));
            Assert.Equal(new[] { 0, 1 }, handle.Cache!.RowsByQueue.Keys.Order());
            for (int i = 0; i < answers.Length; i++)
            {
                using var document = JsonDocument.Parse(answers[i]);
                Assert.Equal(i, document.RootElement.GetProperty("queue").GetInt32());
                Assert.Equal(1, document.RootElement.GetProperty("rows").GetInt32());
            }
            handle.Cache.RowsByQueue.Remove(1);
            Assert.False(preparation.IsReady(handle));
        }
        finally { release.Set(); }
    }

    private sealed class CounterHandle : PixHandle
    {
        public CounterHandle() : base("counter-test") { }
        public override string Kind => "counter-test";
        public CounterCollectionCache? Cache { get; set; }
        public override void Close(List<string> warnings) => Cache = null;
    }
}
