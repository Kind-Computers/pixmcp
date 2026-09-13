using Microsoft.Data.Sqlite;

namespace PixMcp.Pix;

internal sealed partial class TimingDatabase
{
    internal const int CorrelationMaxEvents = 200_000;
    internal const int CorrelationWaitThreads = 8;

    internal static readonly string[] CorrelationSemantics =
    [
        "Matching compares marker names only (the full path, then a leaf unique on both sides) after case, whitespace, legacy PIX prefix and trailing-number normalization; it does not establish identity.",
        "GPU times are replay EOP durations on this machine; recorded times are the original run's marker intervals on the capture clock. ratioRecordedToReplay divides the recorded average occurrence by the replay average and mixes both.",
        "submissionsInOccurrences counts submissions whose CPU submit time falls inside the recorded occurrences on their threads; blockedNs and readyNs integrate those threads' context switches over the same occurrences.",
    ];

    /// <summary>Recorded marker paths aggregated across lanes under one normalized full path.</summary>
    private sealed class RecordedPathGroup(CorrelationKey key, string path, string domain)
    {
        public CorrelationKey Key { get; } = key;
        public string Path { get; set; } = path;
        public string Domain { get; set; } = domain;
        public long Occurrences { get; set; }
        public long InclusiveNs { get; set; }
        public long MostLaneOccurrences { get; set; }
        public List<long> Durations { get; } = [];
        public Dictionary<(string Kind, long Lane), List<(long Begin, long End)>> Intervals { get; } = new();
    }

    /// <summary>
    /// The thread's scheduling states over [from, to) from its context switches, seeded by the last switch-in and switch-out
    /// before <paramref name="from"/>; null when ContextSwitch was not recorded.
    /// </summary>
    private RecordedThreadStates? ThreadStates(long packed, long from, long to, long? threadEnd, out long switchEvents, List<long>? readyLatencies = null)
    {
        switchEvents = 0;
        if (!Has("ContextSwitch", "Timestamp", "FromProcThreadId", "ToProcThreadId", "FromThreadWaitReason")) return null;
        bool readyLinks = Has("ContextSwitch", "ReadyThreadId") && Has("ReadyThread", "Id", "Timestamp");
        string switchIn = "SELECT c.Timestamp," + (readyLinks ? "r.Timestamp" : "NULL") + " FROM ContextSwitch c" +
            (readyLinks ? " LEFT JOIN ReadyThread r ON r.Id=c.ReadyThreadId AND c.ReadyThreadId<>0" : "") + " WHERE c.ToProcThreadId=$packed";
        const string switchOut = "SELECT Timestamp,FromThreadWaitReason FROM ContextSwitch WHERE FromProcThreadId=$packed";
        static RecordedSwitch Out(SqliteDataReader r) => new(r.GetInt64(0), false, r.IsDBNull(1) ? null : r.GetInt32(1));
        static RecordedSwitch In(SqliteDataReader r) => new(r.GetInt64(0), true, null, OptionalInt64(r, 1));
        var switches = new List<RecordedSwitch>();
        switches.AddRange(Rows(switchOut + " AND Timestamp >= $from AND Timestamp < $to", Out, ("$packed", packed), ("$from", from), ("$to", to)));
        switches.AddRange(Rows(switchIn + " AND c.Timestamp >= $from AND c.Timestamp < $to", In, ("$packed", packed), ("$from", from), ("$to", to)));
        switchEvents = switches.Count;
        readyLatencies?.AddRange(switches.Where(s => s.SwitchIn && s.ReadyTimestamp is long ready && ready <= s.Timestamp).Select(s => s.Timestamp - s.ReadyTimestamp!.Value));
        // The last switches before the window seed the thread's state at its start.
        switches.AddRange(Rows(switchOut + " AND Timestamp < $from ORDER BY Timestamp DESC LIMIT 1", Out, ("$packed", packed), ("$from", from)));
        switches.AddRange(Rows(switchIn + " AND c.Timestamp < $from ORDER BY c.Timestamp DESC LIMIT 1", In, ("$packed", packed), ("$from", from)));
        long closeAt = threadEnd is long endOfThread && endOfThread > from ? Math.Min(to, endOfThread) : to;
        return RecordedThreadStates.Build(switches, closeAt);
    }

    private static int LowerBound(long[] sorted, long value)
    {
        int low = 0, high = sorted.Length;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (sorted[middle] < value) low = middle + 1; else high = middle;
        }
        return low;
    }

    /// <summary>
    /// Joins replayed GPU marker paths (<paramref name="gpu"/>) to the recorded PIX marker paths of a process in [start, end)
    /// by name, with per-match recorded statistics, submissions inside the recorded occurrences, the emitting threads' blocked
    /// and ready time, unmatched paths on both sides and a queue map.
    /// </summary>
    internal CorrelationDto Correlate(string timingHandle, GpuCorrelationSnapshot gpu, uint? processId, long? start, long? end, string rangeMode,
        int offset, int limit, Func<int, object>? continuation = null) => Guard<CorrelationDto>(() =>
    {
        ValidatePage(offset, limit);
        var (a, b, provenance) = Range(start, end, rangeMode);
        uint? pid = processId ?? CaptureTargetProcessId();
        Require("PixEventInfo", "Id", "NameId"); Require("Strings", "Id", "Value");
        var notes = new List<string>();
        var groups = new Dictionary<string, RecordedPathGroup>(StringComparer.Ordinal);
        long eventsRead = 0;
        bool eventsTruncated = false;

        static (RecordedMarkerRow Row, long Lane) Project(SqliteDataReader r) =>
            (new RecordedMarkerRow(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.IsDBNull(3) ? 0 : r.GetInt32(3),
                r.IsDBNull(6) ? "(event " + (r.IsDBNull(5) ? "?" : Ns(r.GetInt64(5))) + ")" : r.GetString(6)), r.GetInt64(4));

        void AddLanes(string kind, List<(RecordedMarkerRow Row, long Lane)> rows)
        {
            if (rows.Count > CorrelationMaxEvents)
            {
                rows.RemoveRange(CorrelationMaxEvents, rows.Count - CorrelationMaxEvents);
                eventsTruncated = true;
            }
            eventsRead += rows.Count;
            string domain = kind == "thread" ? "cpu" : "gpuMarkers";
            foreach (var lane in rows.GroupBy(x => x.Lane))
            {
                RecordedMarkerTree tree = RecordedMarkerTree.Build(lane.Select(x => x.Row).ToList(), a, b);
                foreach (RecordedMarkerTree.PathNode node in tree.Descendants())
                {
                    CorrelationKey key = TimingCorrelation.Key(node.Segments());
                    if (!groups.TryGetValue(key.FullKey, out RecordedPathGroup? group)) groups[key.FullKey] = group = new(key, node.Path, domain);
                    if (group.Domain != domain) group.Domain = "mixed";
                    group.Occurrences += node.Occurrences;
                    group.InclusiveNs += node.InclusiveNs;
                    group.Durations.AddRange(node.Durations);
                    if (node.Occurrences > group.MostLaneOccurrences)
                    {
                        group.MostLaneOccurrences = node.Occurrences;
                        group.Path = node.Path;
                    }
                    if (!group.Intervals.TryGetValue((kind, lane.Key), out List<(long Begin, long End)>? intervals)) group.Intervals[(kind, lane.Key)] = intervals = [];
                    intervals.AddRange(node.Intervals);
                }
            }
        }

        bool processes = Has("Processes", "Id", "ProcessId");
        if (processes && Has("PixCpuExecution", "BeginTimestamp", "EndTimestamp", "Level", "ThreadRowId", "EventId") && Has("Threads", "Id", "ProcessRowId"))
            AddLanes("thread", Rows("SELECT e.rowid,e.BeginTimestamp,e.EndTimestamp,e.Level,e.ThreadRowId,e.EventId,s.Value FROM PixCpuExecution e JOIN Threads t ON t.Id=e.ThreadRowId" +
                " JOIN Processes p ON p.Id=t.ProcessRowId LEFT JOIN PixEventInfo i ON i.Id=e.EventId LEFT JOIN Strings s ON s.Id=i.NameId" +
                " WHERE e.BeginTimestamp < $end AND e.EndTimestamp > $start AND ($pid IS NULL OR p.ProcessId=$pid) LIMIT $cap",
                Project, ("$start", a), ("$end", b), ("$pid", (object?)pid), ("$cap", CorrelationMaxEvents + 1)));
        if (processes && Has("PixGpuExecution", "BeginTimestamp", "EndTimestamp", "Level", "ApiCommandQueueId", "EventId") && Has("ApiCommandQueue", "Id", "ProcessId"))
            AddLanes("queue", Rows("SELECT e.rowid,e.BeginTimestamp,e.EndTimestamp,e.Level,e.ApiCommandQueueId,e.EventId,s.Value FROM PixGpuExecution e JOIN ApiCommandQueue q ON q.Id=e.ApiCommandQueueId" +
                " JOIN Processes p ON p.Id=q.ProcessId LEFT JOIN PixEventInfo i ON i.Id=e.EventId LEFT JOIN Strings s ON s.Id=i.NameId" +
                " WHERE e.BeginTimestamp < $end AND e.EndTimestamp > $start AND ($pid IS NULL OR p.ProcessId=$pid) LIMIT $cap",
                Project, ("$start", a), ("$end", b), ("$pid", (object?)pid), ("$cap", CorrelationMaxEvents + 1)));
        if (eventsTruncated) notes.Add($"More than {CorrelationMaxEvents} recorded events per family are in the window; narrow startNs/endNs for complete recorded paths.");
        if (gpu.Truncated) notes.Add("The GPU capture has more timed marker paths than one correlation reads; restrict it with queueIndex, scope or markerPathPrefix.");

        List<RecordedPathGroup> recorded = groups.Values.OrderByDescending(g => g.InclusiveNs).ThenBy(g => g.Path, StringComparer.Ordinal).ToList();
        List<CorrelationKey> gpuKeys = gpu.Paths.Select(p => TimingCorrelation.Key(p.Segments)).ToList();
        var (matches, unmatchedGpu, unmatchedRecorded) = TimingCorrelation.Match(gpuKeys, recorded.Select(g => g.Key).ToList());

        // Scheduling evidence covers the threads of matched CPU paths, those with the most matched occurrences first.
        var laneOccurrences = new Dictionary<long, long>();
        foreach (CorrelationMatch match in matches)
            foreach (var (laneKey, intervals) in recorded[match.RecordedIndex].Intervals)
                if (laneKey.Kind == "thread") laneOccurrences[laneKey.Lane] = laneOccurrences.GetValueOrDefault(laneKey.Lane) + intervals.Count;
        bool submissions = Has("ApiQueueExecution", "ThreadId", "SubmitTimestamp");
        var submitTimes = new Dictionary<long, long[]>();
        if (submissions && laneOccurrences.Count > 0)
            foreach (var thread in Rows("SELECT ThreadId,SubmitTimestamp FROM ApiQueueExecution WHERE SubmitTimestamp >= $start AND SubmitTimestamp < $end AND ThreadId IN (" +
                string.Join(",", laneOccurrences.Keys.Select(Ns)) + ")", r => (Thread: r.GetInt64(0), Submit: r.GetInt64(1)), ("$start", a), ("$end", b)).GroupBy(x => x.Thread))
                submitTimes[thread.Key] = thread.Select(x => x.Submit).Order().ToArray();
        long[] waitThreads = laneOccurrences.OrderByDescending(x => x.Value).ThenBy(x => x.Key).Take(CorrelationWaitThreads).Select(x => x.Key).ToArray();
        var threadStates = new Dictionary<long, RecordedThreadStates>();
        if (waitThreads.Length > 0 && Has("Threads", "Id", "ProcThreadId"))
        {
            bool lifetimes = Has("Threads", "EndTimestamp");
            foreach (var thread in Rows("SELECT Id,ProcThreadId," + (lifetimes ? "EndTimestamp" : "NULL") + " FROM Threads WHERE Id IN (" + string.Join(",", waitThreads.Select(Ns)) + ")",
                r => (Id: r.GetInt64(0), Packed: OptionalInt64(r, 1), End: OptionalInt64(r, 2))))
                if (thread.Packed is long packedThread && ThreadStates(packedThread, a, b, thread.End, out _) is RecordedThreadStates states)
                    threadStates[thread.Id] = states;
        }
        if (laneOccurrences.Count > waitThreads.Length)
            notes.Add($"blockedNs and readyNs cover the {CorrelationWaitThreads} threads with the most matched occurrences.");

        CorrelationRowDto Row(CorrelationMatch match)
        {
            GpuMarkerPathSnapshot g = gpu.Paths[match.GpuIndex];
            RecordedPathGroup r = recorded[match.RecordedIndex];
            long? submitted = null, blocked = null, ready = null;
            foreach (var (laneKey, intervals) in r.Intervals)
            {
                if (laneKey.Kind != "thread") continue;
                if (submissions)
                {
                    submitted ??= 0;
                    if (submitTimes.TryGetValue(laneKey.Lane, out long[]? times))
                        foreach (var (begin, finish) in intervals)
                        {
                            long clippedBegin = Math.Max(begin, a), clippedEnd = Math.Min(finish, b);
                            if (clippedEnd > clippedBegin) submitted += LowerBound(times, clippedEnd) - LowerBound(times, clippedBegin);
                        }
                }
                if (threadStates.TryGetValue(laneKey.Lane, out RecordedThreadStates? states))
                    foreach (var (begin, finish) in intervals)
                    {
                        RecordedStateTotals totals = states.Integrate(begin, finish);
                        blocked = (blocked ?? 0) + totals.BlockedNs;
                        ready = (ready ?? 0) + totals.ReadyNs;
                    }
            }
            RecordedStatsDto occurrence = RecordedTiming.Stats(r.Durations)!;
            double gpuAverage = g.Occurrences > 0 ? (double)g.InclusiveEopNs / g.Occurrences : 0;
            var eventRef = new EventRef(gpu.Handle, g.QueueIndex, g.EventIndex);
            var busiestLane = r.Intervals.OrderByDescending(x => x.Value.Count).ThenBy(x => x.Key.Lane).First().Key;
            var calls = new List<ToolCallDto>
            {
                new("pix_gpu_inspect_event", new { eventRef }),
                busiestLane.Kind == "thread"
                    ? new("pix_timing_tree", new { handle = timingHandle, threadRowId = Ns(busiestLane.Lane), parentPath = r.Path }, CostHints.Query)
                    : new("pix_timing_tree", new { handle = timingHandle, queueId = Ns(busiestLane.Lane), parentPath = r.Path }, CostHints.Query),
            };
            return new CorrelationRowDto(string.Join("/", g.Segments), eventRef, g.Occurrences, g.InclusiveEopNs, Metrics.Ms(g.InclusiveEopNs), Math.Round(gpuAverage / 1e6, 3), g.Semantics,
                r.Path, r.Domain, r.Occurrences, r.InclusiveNs, occurrence,
                r.Intervals.OrderByDescending(x => x.Value.Count).ThenBy(x => x.Key.Lane).Take(3).Select(x => (x.Key.Kind == "thread" ? "threadRowId " : "queueId ") + Ns(x.Key.Lane)).ToArray(),
                match.Method, match.Normalizations, match.Method == "pathMatch" && !match.Normalizations.Contains("trailingNumber") ? "medium" : "low",
                submitted, blocked, ready, gpuAverage > 0 ? Math.Round(occurrence.AvgNs / gpuAverage, 3) : null, calls);
        }

        CorrelationRowDto[] items = matches.Skip(offset).Take(limit).Select(Row).ToArray();
        PageResult<CorrelationRowDto> page = Paging.Page(items, matches.Count, offset, limit);

        var recordedQueues = new List<(string Id, string? Name, string? Type)>();
        if (processes && Has("ApiCommandQueue", "Id", "ProcessId", "NameId"))
        {
            bool types = Has("ApiCommandQueue", "TypeId");
            recordedQueues = Rows("SELECT q.Id,n.Value," + (types ? "t.Value" : "NULL") + " FROM ApiCommandQueue q JOIN Processes p ON p.Id=q.ProcessId LEFT JOIN Strings n ON n.Id=q.NameId" +
                (types ? " LEFT JOIN Strings t ON t.Id=q.TypeId" : "") + " WHERE ($pid IS NULL OR p.ProcessId=$pid) ORDER BY q.Id",
                r => (Id: Ns(r.GetInt64(0)), Name: Text(r, 1), Type: Text(r, 2)), ("$pid", (object?)pid));
        }
        List<CorrelationQueueMapDto> queueMap = TimingCorrelation.MapQueues(gpu.Queues.Select(q => (q.Index, q.Name, q.Type)).ToList(), recordedQueues);

        var calls = new List<ToolCallDto>();
        if (page.NextOffset is int next && continuation is not null) calls.Add(new("pix_correlate", continuation(next), CostHints.Query));
        if (matches.Count == 0)
        {
            calls.Add(new("pix_gpu_timing_tree", new { handle = gpu.Handle }));
            if (recorded.FirstOrDefault(g => g.Intervals.Keys.Any(k => k.Kind == "thread")) is { } top)
                calls.Add(new("pix_timing_tree", new { handle = timingHandle, threadRowId = Ns(top.Intervals.Keys.First(k => k.Kind == "thread").Lane) }, CostHints.Query));
            notes.Add("No marker names matched; compare the GPU marker tree with the recorded marker tree and align the names emitted by the application.");
        }

        return new CorrelationDto(gpu.Handle, timingHandle, TimingCorrelation.Identity, gpu.Scope, gpu.Provenance, provenance, pid,
            new CorrelationCountsDto(gpu.Paths.Count, gpu.Markers, gpu.Truncated, recorded.Count, eventsRead, eventsTruncated, matches.Count,
                matches.Count(m => m.Method == "pathMatch"), matches.Count(m => m.Method == "nameMatch"), unmatchedGpu.Count, unmatchedRecorded.Count),
            page,
            unmatchedGpu.Take(20).Select(i => gpu.Paths[i]).Select(g => new CorrelationUnmatchedDto(string.Join("/", g.Segments), g.Occurrences, Metrics.Ms(g.InclusiveEopNs),
                new EventRef(gpu.Handle, g.QueueIndex, g.EventIndex), null)).ToArray(),
            unmatchedRecorded.Take(20).Select(i => recorded[i]).Select(r => new CorrelationUnmatchedDto(r.Path, r.Occurrences, RecordedTiming.Ms(r.InclusiveNs), null, r.Domain)).ToArray(),
            queueMap, CorrelationSemantics, notes, calls);
    });
}
