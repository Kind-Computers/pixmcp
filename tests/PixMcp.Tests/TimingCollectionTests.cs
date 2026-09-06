using PixMcp.Pix;
using PixMcp.Pix.Handles;
using Xunit;

namespace PixMcp.Tests;

public sealed class TimingCollectionTests
{
    [Fact]
    public void FailedSecondQueueLeavesPriorSnapshotAndRetryPublishesEveryQueue()
    {
        QueueEntry[] queues = [Queue(0), Queue(1)];
        object priorTiming = new(), failedTiming = new(), retriedTiming = new();
        var priorRows = new Dictionary<int, EventTimingRow[]> { [7] = [Row(7)] };
        object publishedTiming = priorTiming;
        Dictionary<int, EventTimingRow[]> publishedRows = priorRows;
        var built = new List<int>();
        int publications = 0;
        void Publish(object timing, Dictionary<int, EventTimingRow[]> rows)
        {
            publications++;
            publishedTiming = timing;
            publishedRows = rows;
        }

        Assert.Throws<InvalidOperationException>(() => TimingCollection.Collect(() => failedTiming, queues,
            (timing, queue, _) =>
            {
                Assert.Same(failedTiming, timing);
                built.Add(queue.Index);
                if (queue.Index == 1) throw new InvalidOperationException("Native row read failed.");
                return [Row(queue.Index)];
            }, Publish));
        Assert.Equal(new[] { 0, 1 }, built);
        Assert.Equal(0, publications);
        Assert.Same(priorTiming, publishedTiming);
        Assert.Same(priorRows, publishedRows);
        Assert.Single(priorRows);

        built.Clear();
        TimingCollection.Collect(() => retriedTiming, queues, (timing, queue, _) =>
        {
            Assert.Same(retriedTiming, timing);
            built.Add(queue.Index);
            return [Row(queue.Index)];
        }, Publish);
        Assert.Equal(new[] { 0, 1 }, built);
        Assert.Equal(1, publications);
        Assert.Same(retriedTiming, publishedTiming);
        Assert.Equal(new[] { 0, 1 }, publishedRows.Keys.OrderBy(x => x));
        Assert.All(publishedRows, pair => Assert.Equal(pair.Key, Assert.Single(pair.Value).QueueIndex));
        Assert.Single(priorRows);
    }

    [Fact]
    public void CancellationAfterLastQueueDoesNotPublish()
    {
        using var cancellation = new CancellationTokenSource();
        bool published = false;
        Assert.Throws<OperationCanceledException>(() => TimingCollection.Collect(() => new object(), [Queue(0)],
            (_, queue, ct) =>
            {
                Assert.Equal(cancellation.Token, ct);
                cancellation.Cancel();
                return [Row(queue.Index)];
            }, (_, _) => published = true, cancellation.Token));
        Assert.False(published);
    }

    [Fact]
    public void CancellationBeforeCollectionSkipsNativeWork()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool collected = false;
        Assert.Throws<OperationCanceledException>(() => TimingCollection.Collect(() => collected = true, [Queue(0)],
            (_, _, _) => [], (_, _) => { }, cancellation.Token));
        Assert.False(collected);
    }

    private static EventTimingRow Row(int queue) => new(queue, 0, 1, "Draw", "DrawInstanced", 0, 1, 0, 1);
    private static QueueEntry Queue(int index) => new()
    {
        Index = index, Id = (uint)index, Info = null!, Name = $"Queue {index}", Type = default,
        AdapterId = 0, AdapterName = "Test", EventCount = 1,
    };
}
