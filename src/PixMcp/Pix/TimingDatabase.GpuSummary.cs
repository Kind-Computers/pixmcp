using System.Globalization;
using Microsoft.Data.Sqlite;
using PixMcp.Pix.Sql;

namespace PixMcp.Pix;

internal sealed partial class TimingDatabase
{
    private readonly record struct RecordedSubmission(long Id, long Queue, long? Thread, long? Submit, long? Begin, long? End);

    internal static readonly RecordedDenominatorsDto GpuSummaryDenominators = new(
        "duration / lane span (first to last clipped interval, capture clock)",
        "duration / sum of the lane's clipped interval lengths (overlapping intervals counted each time)",
        "busy / selected window length (endNs - startNs)",
        "union of the lane's intervals clipped to [startNs,endNs); overlapping intervals count once");

    private static long? OptionalInt64(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);

    /// <summary>The capture's target process (CaptureFacts 4), when recorded.</summary>
    internal uint? CaptureTargetProcessId()
    {
        if (!Has("CaptureFacts", "Id", "Value")) return null;
        List<long> values = Rows("SELECT Value FROM CaptureFacts WHERE Id=4", r => r.GetInt64(0));
        return values.Count == 1 && values[0] is >= 0 and <= 4294967295L ? (uint)values[0] : null;
    }

    /// <summary>
    /// Recorded GPU work per API command queue: submissions selected by CPU submit timestamp in [start, end), validity
    /// counts, busy/idle/span over valid execution intervals clipped to the window, latency and execution statistics,
    /// top submitting threads and longest executions; plus hardware queues, VSync pacing and CPU-to-GPU link state.
    /// </summary>
    internal TimingGpuSummaryDto GpuSummary(string handle, int generation, uint? processId, string? queueId, long? start, long? end,
        string rangeMode = RangeModeFull, int limit = 10) => Guard<TimingGpuSummaryDto>(() =>
    {
        if (limit is < 1 or > 100) throw PixErrors.InvalidArguments("limit must be between 1 and 100.");
        long? queueFilter = queueId is null ? null : ParseId(queueId, nameof(queueId));
        var (a, b, provenance) = Range(start, end, rangeMode);
        Require("ApiQueueExecution", "Id", "ApiCommandQueueId", "ThreadId", "SubmitTimestamp", "BeginTimestamp", "EndTimestamp");
        Require("ApiCommandQueue", "Id", "ProcessId", "NameId");
        Require("Processes", "Id", "ProcessId"); Require("Strings", "Id", "Value");
        uint? pid = processId ?? CaptureTargetProcessId();
        var unavailable = new List<string>();

        bool types = Has("ApiCommandQueue", "TypeId");
        bool adapters = Has("ApiCommandQueue", "HardwareAdapterId") && Has("HardwareAdapter", "Id", "NameId");
        var queues = Rows("SELECT q.Id,n.Value," + (types ? "t.Value" : "NULL") + "," + (adapters ? "an.Value" : "NULL") +
            " FROM ApiCommandQueue q JOIN Processes p ON p.Id=q.ProcessId LEFT JOIN Strings n ON n.Id=q.NameId" +
            (types ? " LEFT JOIN Strings t ON t.Id=q.TypeId" : "") +
            (adapters ? " LEFT JOIN HardwareAdapter ha ON ha.Id=q.HardwareAdapterId LEFT JOIN Strings an ON an.Id=ha.NameId" : "") +
            " WHERE ($pid IS NULL OR p.ProcessId=$pid) AND ($queue IS NULL OR q.Id=$queue) ORDER BY q.Id",
            r => (Id: r.GetInt64(0), Name: Text(r, 1), Type: Text(r, 2), Adapter: Text(r, 3)), ("$pid", (object?)pid), ("$queue", (object?)queueFilter));
        if (queueFilter.HasValue && queues.Count == 0)
            throw new PixToolException(PixErrors.Codes.InvalidReference,
                $"queueId {queueId} is not a recorded API command queue" + (pid is uint owner ? $" of process {owner}." : "."),
                nextCalls: [new("pix_timing_overview", new { handle, processId = pid })]);
        if (queues.Count == 0) unavailable.Add("queues: no API command queue is recorded" + (pid is uint target ? $" for process {target}." : "."));

        var byQueue = queues.ToDictionary(q => q.Id, _ => new List<RecordedSubmission>());
        foreach (RecordedSubmission row in Rows("SELECT e.Id,e.ApiCommandQueueId,e.ThreadId,e.SubmitTimestamp,e.BeginTimestamp,e.EndTimestamp" +
            " FROM ApiQueueExecution e JOIN ApiCommandQueue q ON q.Id=e.ApiCommandQueueId JOIN Processes p ON p.Id=q.ProcessId" +
            " WHERE e.SubmitTimestamp >= $start AND e.SubmitTimestamp < $end AND ($pid IS NULL OR p.ProcessId=$pid) AND ($queue IS NULL OR e.ApiCommandQueueId=$queue)",
            r => new RecordedSubmission(r.GetInt64(0), r.GetInt64(1), OptionalInt64(r, 2), OptionalInt64(r, 3), OptionalInt64(r, 4), OptionalInt64(r, 5)),
            ("$start", a), ("$end", b), ("$pid", (object?)pid), ("$queue", (object?)queueFilter)))
            if (byQueue.TryGetValue(row.Queue, out List<RecordedSubmission>? list)) list.Add(row);

        var threads = new Dictionary<long, (uint? Pid, uint? Tid, string? Name)>();
        long[] threadIds = byQueue.Values.SelectMany(l => l).Where(s => s.Thread.HasValue).Select(s => s.Thread!.Value).Distinct().Order().ToArray();
        if (threadIds.Length > 0 && Has("Threads", "Id", "ProcThreadId"))
        {
            bool names = Has("Threads", "ThreadNameId");
            foreach (long[] chunk in threadIds.Chunk(500))
                foreach (var t in Rows("SELECT t.Id,t.ProcThreadId," + (names ? "s.Value" : "NULL") + " FROM Threads t" +
                    (names ? " LEFT JOIN Strings s ON s.Id=t.ThreadNameId" : "") + " WHERE t.Id IN (" + string.Join(",", chunk.Select(Ns)) + ")",
                    r => (Id: r.GetInt64(0), Packed: OptionalInt64(r, 1), Name: Text(r, 2))))
                {
                    uint? threadPid = t.Packed is long high ? unchecked((uint)((ulong)high >> 32)) : null;
                    uint? threadTid = t.Packed is long low ? unchecked((uint)low) : null;
                    threads[t.Id] = (threadPid, threadTid, t.Name);
                }
        }

        var rollups = new List<TimingGpuQueueRollupDto>();
        foreach (var queue in queues)
        {
            Check();
            List<RecordedSubmission> rows = byQueue[queue.Id];
            var invalid = new SortedDictionary<string, long>(StringComparer.Ordinal);
            var valid = new List<(RecordedSubmission Row, long Latency, long Duration)>();
            foreach (RecordedSubmission row in rows)
            {
                var (ok, key, _) = SubmissionValidity(row.Submit, row.Begin, row.End);
                if (ok) valid.Add((row, row.Begin!.Value - row.Submit!.Value, row.End!.Value - row.Begin.Value));
                else invalid[key!] = invalid.GetValueOrDefault(key!) + 1;
            }
            ILookup<long?, long> latencyByThread = valid.ToLookup(v => v.Row.Thread, v => v.Latency);
            List<TimingGpuThreadDto> top = rows.GroupBy(s => s.Thread).Select(g => (Thread: g.Key, Count: (long)g.Count()))
                .OrderByDescending(t => t.Count).ThenBy(t => t.Thread ?? long.MaxValue).Take(limit)
                .Select(t =>
                {
                    threads.TryGetValue(t.Thread ?? -1, out var info);
                    long[] latencies = latencyByThread[t.Thread].ToArray();
                    return new TimingGpuThreadDto(t.Thread is long id ? Ns(id) : null, info.Pid, info.Tid, info.Name, t.Count, latencies.Length, RecordedTiming.Stats(latencies));
                }).ToList();
            List<TimingGpuExecutionDto> longest = valid.OrderByDescending(v => v.Duration).ThenBy(v => v.Row.Id).Take(limit)
                .Select(v => new TimingGpuExecutionDto(TimingSubmissionReferences.Create(handle, generation, v.Row.Id), v.Row.Thread is long id ? Ns(id) : null,
                    Ns(v.Row.Submit!.Value), Ns(v.Row.Begin!.Value), v.Duration, RecordedTiming.Ms(v.Duration), v.Latency)).ToList();
            rollups.Add(new TimingGpuQueueRollupDto(Ns(queue.Id), queue.Name, queue.Type, queue.Adapter, rows.Count, valid.Count, invalid,
                RecordedTiming.Totals(valid.Select(v => (v.Row.Begin!.Value, v.Row.End!.Value)), a, b),
                RecordedTiming.Stats(valid.Select(v => v.Latency)), RecordedTiming.Stats(valid.Select(v => v.Duration)), top, longest));
        }
        rollups = rollups.OrderByDescending(r => r.Totals?.Busy.Ns ?? 0).ThenBy(r => long.Parse(r.QueueId, CultureInfo.InvariantCulture)).ToList();

        List<TimingHardwareQueueRollupDto>? hardware = null;
        if (Has("GpuWorkRange", "HardwareQueueId", "BeginTimestamp", "EndTimestamp") && Has("HardwareCommandQueue", "Id", "NameId"))
        {
            bool levels = Has("GpuWorkRange", "OverlapLevel");
            bool hardwareAdapters = Has("HardwareCommandQueue", "AdapterId") && Has("HardwareAdapter", "Id", "NameId");
            var ranges = Rows("SELECT HardwareQueueId,BeginTimestamp,EndTimestamp," + (levels ? "OverlapLevel" : "NULL") +
                " FROM GpuWorkRange WHERE HardwareQueueId IS NOT NULL AND BeginTimestamp < $end AND EndTimestamp > $start",
                r => (Queue: r.GetInt64(0), Begin: r.GetInt64(1), End: r.GetInt64(2), Level: OptionalInt64(r, 3)), ("$start", a), ("$end", b));
            hardware = [];
            if (ranges.Count > 0)
            {
                var names = Rows("SELECT h.Id,hn.Value," + (hardwareAdapters ? "an.Value" : "NULL") +
                    " FROM HardwareCommandQueue h LEFT JOIN Strings hn ON hn.Id=h.NameId" +
                    (hardwareAdapters ? " LEFT JOIN HardwareAdapter ha ON ha.Id=h.AdapterId LEFT JOIN Strings an ON an.Id=ha.NameId" : "") +
                    " WHERE h.Id IN (" + string.Join(",", ranges.Select(r => r.Queue).Distinct().Select(Ns)) + ")",
                    r => (Id: r.GetInt64(0), Name: Text(r, 1), Adapter: Text(r, 2))).ToDictionary(x => x.Id);
                hardware = ranges.GroupBy(r => r.Queue).Select(g =>
                {
                    names.TryGetValue(g.Key, out var name);
                    return new TimingHardwareQueueRollupDto(Ns(g.Key), name.Name, name.Adapter, g.Count(),
                        RecordedTiming.Totals(g.Select(r => (r.Begin, r.End)), a, b), g.Max(r => r.Level));
                }).OrderByDescending(h => h.Totals?.Busy.Ns ?? 0).ThenBy(h => long.Parse(h.HardwareQueueId, CultureInfo.InvariantCulture)).Take(limit).ToList();
            }
        }
        else unavailable.Add("hardwareQueues: this capture lacks GpuWorkRange or HardwareCommandQueue.");

        List<TimingVsyncMonitorDto>? vsync = null;
        if (Library(handle, "frames_vsync", SqlBindings(start, end, rangeMode), unavailable) is SqlResultDto v)
        {
            vsync = [];
            for (int i = 0; i < v.RowCount; i++)
                vsync.Add(new TimingVsyncMonitorDto(Convert.ToString(Cell(v, i, "lane"), CultureInfo.InvariantCulture) ?? "", Cell(v, i, "monitor") as string,
                    Long(Cell(v, i, "intervals")) ?? 0, Ms(Double(Cell(v, i, "avgIntervalNs")) ?? 0), Double(Cell(v, i, "p50IntervalNs")) is double p50 ? Ms(p50) : null,
                    Double(Cell(v, i, "p95IntervalNs")) is double p95 ? Ms(p95) : null, Ms(Double(Cell(v, i, "maxIntervalNs")) ?? 0), Double(Cell(v, i, "avgHz"))));
        }

        long? cpuGpuLinks = Has("CpuGpuExecutionMap", "CpuMarkerId", "GpuEventId") ? Count("CpuGpuExecutionMap") : null;
        long? markerWorkLinks = Has("ApiMarkerGpuWorkMap", "ApiMarkerId", "GpuWorkId") ? Count("ApiMarkerGpuWorkMap") : null;
        long? gpuMarkerRows = Has("PixGpuExecution", "BeginTimestamp") ? Count("PixGpuExecution") : null;
        string state = cpuGpuLinks is null && markerWorkLinks is null ? "unsupported" : cpuGpuLinks > 0 || markerWorkLinks > 0 ? "available" : "empty";
        var causality = new TimingCausalityDto(state, cpuGpuLinks, markerWorkLinks, gpuMarkerRows, state switch
        {
            "available" => "PIX linked CPU PIX markers to GPU work; join CpuGpuExecutionMap or ApiMarkerGpuWorkMap with pix_timing_sql.",
            "empty" => "No CPU marker to GPU work links were recorded; they require GPU-side PIX markers (PIXBeginEvent on a command list or queue). Submissions still tie the submitting thread to GPU execution.",
            _ => "This capture lacks CpuGpuExecutionMap and ApiMarkerGpuWorkMap.",
        });

        var calls = new List<ToolCallDto>();
        if (rollups.FirstOrDefault(r => r.Submissions > 0) is { } busiest)
        {
            calls.Add(new("pix_timing_submissions", new { handle, processId = pid, queueId = busiest.QueueId, startNs = Ns(a), endNs = Ns(b), rangeMode }, CostHints.Query));
            if (gpuMarkerRows > 0)
                calls.Add(new("pix_timing_tree", new { handle, queueId = busiest.QueueId, startNs = Ns(a), endNs = Ns(b), rangeMode }, CostHints.Query));
            if (busiest.TopSubmittingThreads.FirstOrDefault(t => t.ThreadRowId is not null) is { } submitter)
                calls.Add(new("pix_timing_thread_switches", new { handle, threadRowId = submitter.ThreadRowId, startNs = Ns(a), endNs = Ns(b), rangeMode }, CostHints.Query));
        }
        calls.Add(new("pix_timing_sql", new { handle, query = "gpu_busy_per_queue", @params = pid is uint queryPid ? new { pid = queryPid } : null,
            startNs = start.HasValue ? Ns(a) : null, endNs = end.HasValue ? Ns(b) : null, rangeMode }, CostHints.Query));
        calls.Add(new("pix_timing_verdict", new { handle, processId = pid, startNs = start.HasValue ? Ns(a) : null, endNs = end.HasValue ? Ns(b) : null, rangeMode }, CostHints.Query));

        string[] notes =
        [
            "Submissions are selected by CPU submit timestamp in [startNs,endNs); GPU intervals are clipped to the window for busy, idle, span and sum.",
            "Recorded GPU execution spans a whole ExecuteCommandLists call as observed by the OS, not individual draws or passes.",
            "Latency is GPU begin minus CPU submit and execution is GPU end minus begin, over valid submissions only.",
        ];
        return new TimingGpuSummaryDto(handle, provenance, pid, "CPU submit timestamp in [start,end); GPU intervals clipped to the window",
            rollups, hardware, vsync, causality, GpuSummaryDenominators, notes, unavailable, calls);
    });
}
