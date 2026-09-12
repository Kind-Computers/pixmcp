using System.Runtime.InteropServices;
using PixMcp.Tools;
using Xunit;

namespace PixMcp.Tests;

public sealed class DumpQueueCoverageTests
{
    [Fact]
    public void AbsentQueuesRemainUncachedWhileValidEmptyQueuesAreAvailableAndCached()
    {
        List<int>? cached = null;
        int loads = 0;
        DumpQueueRead<int> Read(Func<IEnumerable<int>?> load) => DumpTools.ReadQueueCollection(cached,
            () => { loads++; return load(); }, items => cached = items);

        DumpQueueRead<int> absent = Read(() => null);
        Assert.Empty(absent.Items);
        Assert.Null(cached);
        Assert.Equal(new DumpTriageCoverage("absent"),
            DumpTools.QueueEventCoverage(absent, _ => throw new InvalidOperationException("Must not traverse absent queues.")));

        DumpQueueRead<int> empty = Read(() => []);
        Assert.Empty(empty.Items);
        Assert.Same(empty.Items, cached);
        Assert.Equal(new DumpTriageCoverage("available", 0), DumpTools.QueueEventCoverage(empty, queues => queues.Count));
        Assert.Same(empty.Items, Read(() => throw new InvalidOperationException("Must reuse valid empty queues.")).Items);
        Assert.Equal(2, loads);
    }

    [Fact]
    public void AnEarlierQueueListFailureCannotPoisonLaterTriageReads()
    {
        List<int>? cached = null;
        int loads = 0;
        DumpQueueRead<int> Read() => DumpTools.ReadQueueCollection(cached, () =>
        {
            loads++;
            if (loads <= 2) throw new COMException("Queue history unavailable", unchecked((int)0x80004001));
            return new[] { 7 };
        }, items => cached = items);

        // Open/info callers only consume Items, preserving their existing queue-list shape.
        Assert.Empty(Read().Items);
        Assert.Null(cached);

        DumpTriageCoverage failed = DumpTools.QueueEventCoverage(Read(),
            _ => throw new InvalidOperationException("Must not traverse unavailable queues."));
        Assert.Equal("unavailable", failed.State);
        Assert.Null(failed.Count);
        Assert.Contains("Queue history unavailable", failed.Reason);
        Assert.Null(cached);

        DumpTriageCoverage recovered = DumpTools.QueueEventCoverage(Read(), queues =>
        {
            Assert.Equal(7, Assert.Single(queues));
            return 42;
        });
        Assert.Equal(new DumpTriageCoverage("available", 42), recovered);
        Assert.Equal(new[] { 7 }, Read().Items);
        Assert.Equal(3, loads);
    }

    [Fact]
    public void EnumerationFailureDoesNotPublishPartialQueueIdentities()
    {
        List<int>? cached = null;
        static IEnumerable<int> BrokenCollection()
        {
            yield return 7;
            throw new InvalidOperationException("Second queue could not be read");
        }

        DumpQueueRead<int> failed = DumpTools.ReadQueueCollection(cached, BrokenCollection, items => cached = items);
        Assert.Empty(failed.Items);
        Assert.Null(cached);
        Assert.Equal("unavailable", failed.Coverage.State);
        Assert.Contains("Second queue could not be read", failed.Coverage.Reason);

        DumpQueueRead<int> recovered = DumpTools.ReadQueueCollection(cached, () => new[] { 7, 8 }, items => cached = items);
        Assert.Equal(new[] { 7, 8 }, recovered.Items);
        Assert.Same(recovered.Items, cached);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationDuringLoadingOrEnumerationPropagatesWithoutCaching(bool duringEnumeration)
    {
        List<int>? cached = null;
        static IEnumerable<int> CancelledCollection()
        {
            yield return 7;
            throw new OperationCanceledException("Queue enumeration cancelled");
        }

        Assert.Throws<OperationCanceledException>(() => DumpTools.ReadQueueCollection(cached,
            () => duringEnumeration ? CancelledCollection() : throw new OperationCanceledException("Queue loading cancelled"),
            items => cached = items));
        Assert.Null(cached);
    }

    [Fact]
    public void EventTraversalErrorsAndCancellationKeepTheirExistingSemantics()
    {
        var queues = new DumpQueueRead<int>([7], new("available", 1));
        DumpTriageCoverage failed = DumpTools.QueueEventCoverage(queues,
            _ => throw new InvalidOperationException("Event history unavailable"));
        Assert.Equal("unavailable", failed.State);
        Assert.Contains("Event history unavailable", failed.Reason);
        Assert.Throws<OperationCanceledException>(() => DumpTools.QueueEventCoverage(queues,
            _ => throw new OperationCanceledException()));
    }
}
