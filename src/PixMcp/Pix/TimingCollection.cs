using PixMcp.Pix.Handles;

namespace PixMcp.Pix;

internal static class TimingCollection
{
    /// <summary>Publishes one complete timing snapshot; failed or cancelled reads leave existing state untouched.</summary>
    internal static void Collect<TTiming>(
        Func<TTiming> collect,
        IEnumerable<QueueEntry> queues,
        Func<TTiming, QueueEntry, CancellationToken, EventTimingRow[]> buildRows,
        Action<TTiming, Dictionary<int, EventTimingRow[]>> publish,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TTiming timing = collect();
        var rows = new Dictionary<int, EventTimingRow[]>();
        foreach (QueueEntry queue in queues)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(queue.Index, buildRows(timing, queue, cancellationToken));
        }
        cancellationToken.ThrowIfCancellationRequested();
        publish(timing, rows);
    }
}
