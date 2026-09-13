using System.ComponentModel;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using PixMcp.Pix.Handles;

namespace PixMcp.Tools;

/// <summary>Joins a replayed GPU capture to a recorded timing capture by marker name.</summary>
[McpServerToolType]
public static class TimingCorrelationTools
{
    internal const int MaxGpuPaths = 2000;

    [McpServerTool(Name = "pix_correlate", Title = "Correlate GPU and timing captures", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false),
     Description("Replays the capture on the local GPU if analysis is not started. Joins the timed marker passes of a GPU capture to the recorded PIX markers of a timing capture by name: the full marker path first, then a leaf name unique on both sides, after normalizing case, whitespace, the legacy '<deprecated - use pix3.h instead>' prefix and trailing numbers. Each match reports the replayed inclusive EOP time, the recorded occurrences and durations, submissions made inside those occurrences, the emitting threads' blocked and ready time and a recorded-to-replay ratio; unmatched paths on both sides and a queue map follow. Names are not identity: recorded occurrences are averaged and the clocks differ.")]
    public static async Task<string> Correlate(PixSession session, JobManager jobs,
        [Description("GPU capture handle (from pix_gpu_open).")] string gpuHandle,
        [Description("Timing capture handle (from pix_timing_open).")] string timingHandle,
        [Description("GPU queue whose markers are correlated; default: every queue the scope touches.")] int? queueIndex = null,
        [Description(EventScope.Description + " Restricts the GPU marker paths.")] EventRef? scope = null,
        [Description(EventScope.PrefixDescription + " Restricts the GPU marker paths.")] string? markerPathPrefix = null,
        [Description("Recorded process id; default: the timing capture's target process.")] uint? processId = null,
        [Description(TimingQueryTools.TimeDescription)] string? startNs = null,
        [Description("Recorded window end in capture nanoseconds (exclusive); default: per rangeMode.")] string? endNs = null,
        [Description(TimingSqlTools.RangeModeDescription)] string rangeMode = TimingDatabase.RangeModeFull,
        [Description("First match to return (default 0).")] int offset = 0,
        [Description("Maximum matches to return (default 10, max 1000).")] int limit = 10,
        [Description(Tools.ReadyWaitDescription)] double waitSeconds = Tools.DefaultReadyWaitSeconds,
        CancellationToken cancellationToken = default)
    {
        TimingDatabase.ValidatePage(offset, limit);
        long? start = TimingDatabase.ParseNs(startNs, nameof(startNs)), end = TimingDatabase.ParseNs(endNs, nameof(endNs));
        string mode = TimingDatabase.NormalizeRangeMode(rangeMode);
        session.Get<TimingCaptureHandle>(timingHandle);
        ScopeSelection selection = EventScope.Resolve(session, gpuHandle, queueIndex, scope, markerPathPrefix);
        GpuCaptureHandle capture = session.Get<GpuCaptureHandle>(gpuHandle);
        if (queueIndex.HasValue) capture.Queue(queueIndex.Value);

        // The GPU side runs on the PIX worker once timing is prepared; the snapshot stays in memory instead of being serialized.
        GpuCorrelationSnapshot? snapshot = null;
        string hop = await Tools.RunWhenReady(session, jobs, "pix_correlate", gpuHandle, CountersTools.TimingPreparation(gpuHandle), h =>
        {
            snapshot = Snapshot(h, selection);
            return new { ready = true };
        }, waitSeconds, cancellationToken).ConfigureAwait(false);
        if (snapshot is not GpuCorrelationSnapshot gpu) return hop;

        return await TimingQueryTools.Query(session, jobs, "pix_correlate", timingHandle,
            new { query = "correlate", gpuHandle, gpu = gpu.Stamp(), processId, start, end, mode, offset, limit },
            (db, _) => db.Correlate(timingHandle, gpu, processId, start, end, mode, offset, limit,
                next => new { gpuHandle, timingHandle, queueIndex, scope, markerPathPrefix, processId, startNs, endNs, rangeMode = mode, offset = next, limit }),
            waitSeconds, cancellationToken, owners: [gpuHandle, timingHandle]).ConfigureAwait(false);
    }

    /// <summary>Timed marker paths of the selected queues (worker thread; timing prepared), aggregated per queue and path, largest inclusive time first.</summary>
    internal static GpuCorrelationSnapshot Snapshot(GpuCaptureHandle h, ScopeSelection selection)
    {
        var byPath = new Dictionary<(int Queue, string Path), (List<string> Segments, uint First, int Count, ulong Inclusive, string Semantics)>();
        int markers = 0;
        foreach (int queue in selection.Queues(h))
        {
            EventRecord[] events = h.AllEvents(queue);
            TimingTreeNode[] nodes = h.TimingTreeFor(queue).Nodes;
            int[] children = EventNavigation.ChildCounts(events);
            for (uint i = 0; i < events.Length && i < nodes.Length; i++)
            {
                EventRecord e = events[i];
                if (!selection.Contains(queue, events, i) || !Tools.IsMarker(e, children[i] > 0) || !nodes[i].IsTimed) continue;
                markers++;
                List<string> segments = [.. EventNavigation.MarkerPath(events, i), e.Name];
                var key = (queue, string.Join("/", segments));
                byPath[key] = byPath.TryGetValue(key, out var entry)
                    ? (entry.Segments, entry.First, entry.Count + 1, entry.Inclusive + nodes[i].InclusiveEopNs, entry.Semantics == nodes[i].Semantics ? entry.Semantics : "mixed")
                    : (segments, i, 1, nodes[i].InclusiveEopNs, nodes[i].Semantics);
            }
        }
        var ordered = byPath.OrderByDescending(p => p.Value.Inclusive).ThenBy(p => p.Key.Queue).ThenBy(p => p.Key.Path, StringComparer.Ordinal).ToList();
        GpuMarkerPathSnapshot[] paths = ordered.Take(MaxGpuPaths)
            .Select(p => new GpuMarkerPathSnapshot(p.Value.Segments, p.Key.Queue, p.Value.First, p.Value.Count, p.Value.Inclusive, p.Value.Semantics)).ToArray();
        GpuQueueSnapshot[] queues = h.Queues.Select(q => new GpuQueueSnapshot(q.Index, q.Name, Json.EnumName(q.Type))).ToArray();
        return new GpuCorrelationSnapshot(h.Id, paths, queues, markers, ordered.Count > MaxGpuPaths, selection.DescribeOrNull(h), h.Provenance());
    }
}
