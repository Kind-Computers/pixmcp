using System.Text.Json;

namespace PixMcp.Pix.Sql;

/// <summary>A declared parameter of a library query; Default is bound when the caller omits it (null binds SQL NULL). Required parameters have no default.</summary>
internal sealed record TimingQueryParam(string Name, string Type, object? Default, string Description, bool Required = false);

/// <summary>
/// A curated read-only query over PixStorage. Bodies bind the pre-bound window ($start, $end) and facts ($targetPid,
/// $reliableStart, $reliableEnd, $captureEnd) plus their own params; Requires lists "Table:Column" pairs or module
/// names checked before the query runs.
/// </summary>
internal sealed record NamedTimingQuery(string Name, string Description, string Sql, IReadOnlyList<TimingQueryParam> Params,
    IReadOnlyList<string> Requires, IReadOnlyList<string> Caveats, string ExampleQuestion)
{
    /// <summary>The listing form carries what is needed to call the query; requirements, caveats and SQL come with <paramref name="detailed"/> (query=name).</summary>
    public TimingNamedQueryDto Describe(bool requirementsMet, bool detailed)
        => new(Name, Description, Params.Select(p => new TimingQueryParamDto(p.Name, p.Type, p.Default, p.Description + (p.Required ? " Required." : ""))).ToArray(),
            detailed ? Requires : [], requirementsMet ? "available" : "unsupported", detailed ? Caveats : [], ExampleQuestion, detailed ? Sql : null);

    /// <summary>Caller params merged with defaults; unknown names and missing required parameters are errors that list the declared ones.</summary>
    public IReadOnlyDictionary<string, JsonElement> BindParams(IReadOnlyDictionary<string, JsonElement>? supplied)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach ((string key, JsonElement value) in supplied ?? new Dictionary<string, JsonElement>())
        {
            string bare = key.TrimStart('$', '@', ':');
            if (!Params.Any(p => p.Name == bare))
                throw PixErrors.InvalidArguments($"Named query '{Name}' has no parameter '{bare}'. Parameters: {(Params.Count == 0 ? "none" : string.Join(", ", Params.Select(p => p.Name)))}.");
            result[bare] = value;
        }
        string[] missing = Params.Where(p => p.Required && (!result.TryGetValue(p.Name, out JsonElement value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined))
            .Select(p => p.Name).ToArray();
        if (missing.Length > 0)
            throw PixErrors.InvalidArguments($"Named query '{Name}' requires params {string.Join(", ", missing)}.");
        foreach (TimingQueryParam param in Params)
            if (!result.ContainsKey(param.Name)) result[param.Name] = JsonSerializer.SerializeToElement(param.Default);
        return result;
    }
}

/// <summary>The named queries pix_timing_sql runs with query=name and pix_timing_schema lists.</summary>
internal static class TimingQueryLibrary
{
    private static readonly TimingQueryParam ProcessParam = new("pid", "integer", null, "Recorded process id; null uses the capture's target process ($targetPid).");
    private static readonly TimingQueryParam ThreadParam = new("tid", "integer", null, "OS thread id; null includes every thread of the process.");
    private const string NearestRank = "Percentiles use the nearest-rank method over the rows in the window.";

    public static readonly IReadOnlyList<NamedTimingQuery> All =
    [
        new("capture_facts",
            "Capture facts with their documented meanings: first reliable timestamp, capture end, target process, logical cores, stop timestamp.",
            "SELECT Id, Value, CASE Id WHEN 2 THEN 'firstReliableNs' WHEN 3 THEN 'captureEndNs' WHEN 4 THEN 'targetProcessId' WHEN 5 THEN 'logicalCores' WHEN 24 THEN 'stopNs' END AS meaning FROM CaptureFacts ORDER BY Id",
            [], ["CaptureFacts:Value"], ["Only facts 2, 3, 4, 5 and 24 have documented meanings; other ids are raw."],
            "When does the reliable part of this capture start and end?"),
        new("gpu_busy_per_queue",
            "Per API command queue in [$start, $end): submissions, summed execution, busy time as the union of execution intervals, span, and average submit-to-begin latency.",
            """
            WITH x AS (
              SELECT q.Id AS queueId, max(e.BeginTimestamp, $start) AS b, min(e.EndTimestamp, $end) AS en,
                     CASE WHEN e.BeginTimestamp >= e.SubmitTimestamp THEN e.BeginTimestamp - e.SubmitTimestamp END AS latency
              FROM ApiQueueExecution e JOIN ApiCommandQueue q ON q.Id = e.ApiCommandQueueId JOIN Processes p ON p.Id = q.ProcessId
              WHERE e.EndTimestamp > $start AND e.BeginTimestamp < $end AND p.ProcessId = coalesce($pid, $targetPid)
            ), ordered AS (
              SELECT queueId, b, en, latency,
                     max(en) OVER (PARTITION BY queueId ORDER BY b, en ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING) AS previousEnd
              FROM x
            )
            SELECT o.queueId, s.Value AS queueName, t.Value AS queueType, count(*) AS submissions,
                   sum(o.en - o.b) AS sumExecutionNs,
                   sum(CASE WHEN o.previousEnd IS NULL OR o.b >= o.previousEnd THEN o.en - o.b WHEN o.en > o.previousEnd THEN o.en - o.previousEnd ELSE 0 END) AS busyNs,
                   max(o.en) - min(o.b) AS spanNs, $end - $start AS windowNs,
                   round(avg(o.latency)) AS avgSubmitLatencyNs, max(o.latency) AS maxSubmitLatencyNs
            FROM ordered o JOIN ApiCommandQueue q ON q.Id = o.queueId
            LEFT JOIN Strings s ON s.Id = q.NameId LEFT JOIN Strings t ON t.Id = q.TypeId
            GROUP BY o.queueId ORDER BY busyNs DESC
            """,
            [ProcessParam], ["ApiQueueExecution:SubmitTimestamp", "ApiCommandQueue:ProcessId", "Processes:ProcessId", "Strings:Value"],
            ["ApiCommandQueue.ProcessId is a Processes row id; the OS process id is Processes.ProcessId.", "Execution intervals are clipped to the window.", "busyNs counts overlapping submissions once; sumExecutionNs counts them each time.", "A submission whose begin precedes its submit timestamp has no latency."],
            "How busy was each GPU queue, and how long did submissions wait before executing?"),
        new("gpu_hardware_queues",
            "Hardware (kernel) queues with GPU work ranges overlapping [$start, $end): adapter, ranges, first and last timestamps, deepest overlap.",
            """
            SELECT h.Id AS hardwareQueueId, hn.Value AS name, an.Value AS adapter, count(*) AS ranges,
                   min(g.BeginTimestamp) AS firstNs, max(g.EndTimestamp) AS lastNs, max(g.OverlapLevel) AS maxOverlapLevel
            FROM GpuWorkRange g JOIN HardwareCommandQueue h ON h.Id = g.HardwareQueueId LEFT JOIN Strings hn ON hn.Id = h.NameId
            LEFT JOIN HardwareAdapter a ON a.Id = h.AdapterId LEFT JOIN Strings an ON an.Id = a.NameId
            WHERE g.BeginTimestamp < $end AND g.EndTimestamp > $start
            GROUP BY h.Id ORDER BY ranges DESC, h.Id
            """,
            [], ["GpuWorkRange:HardwareQueueId", "HardwareCommandQueue:AdapterId", "HardwareAdapter:NameId", "Strings:Value"],
            ["GpuWorkRange rows are containers of many packets; counts are ranges, not work items."],
            "Which hardware queues carried GPU work, and on which adapter?"),
        new("submit_latency_per_thread",
            "Submitting threads of a process with submissions in [$start, $end) by CPU submit time, and the submit-to-GPU-begin latency average, p95 and maximum.",
            """
            WITH x AS (
              SELECT e.ThreadId AS threadRowId, e.BeginTimestamp - e.SubmitTimestamp AS latency
              FROM ApiQueueExecution e JOIN ApiCommandQueue q ON q.Id = e.ApiCommandQueueId JOIN Processes p ON p.Id = q.ProcessId
              WHERE e.SubmitTimestamp >= $start AND e.SubmitTimestamp < $end AND p.ProcessId = coalesce($pid, $targetPid) AND e.BeginTimestamp >= e.SubmitTimestamp
            ), ranked AS (
              SELECT threadRowId, latency, ROW_NUMBER() OVER (PARTITION BY threadRowId ORDER BY latency) AS rn, COUNT(*) OVER (PARTITION BY threadRowId) AS n FROM x
            )
            SELECT r.threadRowId, t.ProcThreadId & 4294967295 AS threadId, s.Value AS threadName, max(r.n) AS submissions,
                   round(avg(r.latency)) AS avgLatencyNs, max(CASE WHEN r.rn = (95 * r.n + 99) / 100 THEN r.latency END) AS p95LatencyNs, max(r.latency) AS maxLatencyNs
            FROM ranked r LEFT JOIN Threads t ON t.Id = r.threadRowId LEFT JOIN Strings s ON s.Id = t.ThreadNameId
            GROUP BY r.threadRowId ORDER BY submissions DESC, r.threadRowId
            """,
            [ProcessParam], ["ApiQueueExecution:SubmitTimestamp", "ApiCommandQueue:ProcessId", "Threads:ProcThreadId", "Strings:Value"],
            [NearestRank, "Submissions whose GPU begin precedes the submit timestamp are excluded."],
            "Which thread submits GPU work, and how long does that work wait before the GPU starts it?"),
        new("thread_summary",
            "Threads of a process with lifetime, recorded PIX event, context switch and sample counts.",
            """
            SELECT t.Id AS threadRowId, t.ProcThreadId & 4294967295 AS threadId, s.Value AS threadName,
                   t.StartTimestamp AS startNs, t.EndTimestamp AS endNs, t.PixEventCount AS pixEvents,
                   t.ContextSwitchCount AS contextSwitches, t.SampleCount AS samples, t.MarkerCount AS markers
            FROM Threads t JOIN Processes p ON p.Id = t.ProcessRowId LEFT JOIN Strings s ON s.Id = t.ThreadNameId
            WHERE p.ProcessId = coalesce($pid, $targetPid)
            ORDER BY t.SampleCount DESC, t.ContextSwitchCount DESC
            """,
            [ProcessParam], ["Threads:ContextSwitchCount", "Processes:ProcessId", "Strings:Value"],
            ["ProcThreadId packs processId << 32 | threadId; the low 32 bits are the OS thread id."],
            "Which threads of the game did the most work?"),
        new("context_switches_per_thread",
            "Context switches in [$start, $end) grouped by the thread switched out, with the raw wait-reason codes (KWAIT_REASON values, not decoded).",
            """
            SELECT c.FromProcThreadId & 4294967295 AS threadId, c.FromThreadWaitReason AS waitReason, count(*) AS switches
            FROM ContextSwitch c
            WHERE c.Timestamp >= $start AND c.Timestamp < $end AND (c.FromProcThreadId >> 32) = coalesce($pid, $targetPid)
            GROUP BY 1, 2 ORDER BY switches DESC
            """,
            [ProcessParam], ["ContextSwitch", "ContextSwitch:FromThreadWaitReason"],
            ["Wait reasons are raw codes as recorded; decode them against KWAIT_REASON yourself.", "Unbounded ContextSwitch scans are slow; keep the time predicate."],
            "Why did the render thread keep getting switched out?"),
        new("context_switch_waits",
            "Time each thread of a process spent switched out in [$start, $end): from a switch-out to the same thread's next switch-in, grouped by the raw wait reason of the switch-out.",
            """
            WITH e AS (
              SELECT ToProcThreadId AS thread, Timestamp AS ts, 1 AS isIn, NULL AS reason FROM ContextSwitch
              WHERE Timestamp >= $start AND Timestamp < $end AND (ToProcThreadId >> 32) = coalesce($pid, $targetPid)
                AND ($tid IS NULL OR (ToProcThreadId & 4294967295) = $tid)
              UNION ALL
              SELECT FromProcThreadId, Timestamp, 0, FromThreadWaitReason FROM ContextSwitch
              WHERE Timestamp >= $start AND Timestamp < $end AND (FromProcThreadId >> 32) = coalesce($pid, $targetPid)
                AND ($tid IS NULL OR (FromProcThreadId & 4294967295) = $tid)
            ), o AS (
              SELECT thread, ts, isIn, reason,
                     LEAD(ts) OVER (PARTITION BY thread ORDER BY ts, isIn) AS nextTs, LEAD(isIn) OVER (PARTITION BY thread ORDER BY ts, isIn) AS nextIn
              FROM e
            )
            SELECT thread & 4294967295 AS threadId, reason AS waitReason, count(*) AS waits, sum(nextTs - ts) AS waitedNs, max(nextTs - ts) AS maxWaitNs
            FROM o WHERE isIn = 0 AND nextIn = 1
            GROUP BY thread, reason ORDER BY waitedNs DESC
            """,
            [ProcessParam, ThreadParam], ["ContextSwitch", "ContextSwitch:FromThreadWaitReason"],
            ["Wait reasons are raw KWAIT_REASON codes as recorded (for example 6 UserRequest, 30 WrQuantumEnd, 32 WrPreempted); quantum-end and preemption waits are runnable time, not blocking.",
             "A switch-out without a later switch-in inside the window is not counted.", "Keep the time window narrow on long captures; both scans read every context switch in it."],
            "What was the render thread waiting on, and for how long?"),
        new("ready_thread_latency",
            "Latency from a thread becoming ready (ReadyThread) to its switch-in on a core, per thread of a process in [$start, $end): readies, average, p95 and maximum.",
            """
            WITH l AS (
              SELECT c.ToProcThreadId & 4294967295 AS threadId, c.Timestamp - r.Timestamp AS latency
              FROM ContextSwitch c JOIN ReadyThread r ON r.Id = c.ReadyThreadId
              WHERE c.Timestamp >= $start AND c.Timestamp < $end AND c.ReadyThreadId <> 0 AND (c.ToProcThreadId >> 32) = coalesce($pid, $targetPid)
                AND ($tid IS NULL OR (c.ToProcThreadId & 4294967295) = $tid)
            ), ranked AS (
              SELECT threadId, latency, ROW_NUMBER() OVER (PARTITION BY threadId ORDER BY latency) AS rn, COUNT(*) OVER (PARTITION BY threadId) AS n FROM l
            )
            SELECT threadId, max(n) AS readies, round(avg(latency)) AS avgLatencyNs,
                   max(CASE WHEN rn = (95 * n + 99) / 100 THEN latency END) AS p95LatencyNs, max(latency) AS maxLatencyNs
            FROM ranked GROUP BY threadId ORDER BY readies DESC
            """,
            [ProcessParam, ThreadParam], ["ContextSwitch:ReadyThreadId", "ReadyThread:Timestamp"],
            [NearestRank, "Only switch-ins that name their ready event (ReadyThreadId <> 0) are measured."],
            "Once the render thread was ready, how long did it wait for a core?"),
        new("core_efficiency",
            "Logical and physical cores per efficiency class with their recorded samples and context switches (heterogeneous P/E cores show more than one class).",
            """
            SELECT pc.EfficiencyClass AS efficiencyClass, count(*) AS logicalCores, count(DISTINCT c.PhysicalCoreId) AS physicalCores,
                   sum(c.SampleCount) AS samples, sum(c.ContextSwitchCount) AS contextSwitches
            FROM Cores c JOIN PhysicalCores pc ON pc.Id = c.PhysicalCoreId
            GROUP BY pc.EfficiencyClass ORDER BY pc.EfficiencyClass DESC
            """,
            [], ["Cores:PhysicalCoreId", "PhysicalCores:EfficiencyClass"],
            ["Counts are capture-wide; the window does not apply.", "A higher EfficiencyClass is more performant, as Windows reports it."],
            "Does this machine have heterogeneous cores, and where did the samples land?"),
        new("module_symbols",
            "Modules loaded by a process with their PDB path, resolved function count and symbol state.",
            """
            SELECT m.Id AS moduleId, pe.Value AS module, pdb.Value AS pdbPath,
                   (SELECT count(*) FROM FunctionInformation f WHERE f.ModuleId = m.Id) AS functions,
                   CASE WHEN EXISTS (SELECT 1 FROM FunctionInformation f WHERE f.ModuleId = m.Id) THEN 'resolved' ELSE 'unresolved' END AS symbolState,
                   count(i.Id) AS loads, min(i.LoadTimestamp) AS firstLoadNs
            FROM Modules m JOIN Images i ON i.ModuleId = m.Id LEFT JOIN Strings pe ON pe.Id = m.PEPathId LEFT JOIN Strings pdb ON pdb.Id = m.PDBPathId
            WHERE i.OSProcessId = coalesce($pid, $targetPid)
            GROUP BY m.Id ORDER BY functions DESC, module
            """,
            [ProcessParam], ["Modules:PDBPathId", "Images:ModuleId", "FunctionInformation:ModuleId", "Strings:Value"],
            ["Functions exist only after pix_timing_resolve_symbols found matching PDBs.", "Counts are capture-wide; the window does not apply."],
            "Which modules still need symbols?"),
        new("counters_bucketed",
            "One recorded counter in [$start, $end) bucketed into bucketNs intervals: samples, minimum, mean and maximum per bucket.",
            """
            SELECT (x.Timestamp - $start) / $bucketNs AS bucket, $start + ((x.Timestamp - $start) / $bucketNs) * $bucketNs AS bucketStartNs,
                   count(*) AS samples, min(x.Value) AS minValue, avg(x.Value) AS meanValue, max(x.Value) AS maxValue
            FROM PixCounters x
            WHERE x.CounterId = $counterId AND x.Timestamp >= $start AND x.Timestamp < $end AND $bucketNs > 0
            GROUP BY bucket ORDER BY bucket
            """,
            [new("counterId", "integer", null, "Counter id from pix_timing_counters_list.", Required: true), new("bucketNs", "integer", 1000000000L, "Bucket width in nanoseconds (default 1 second).")],
            ["PixCounters:CounterId", "PixCounters:Value"],
            ["Counter samples are step values that hold until the next sample; meanValue is the plain mean of samples, not time-weighted.", "Buckets without samples are absent."],
            "How did this counter evolve over the capture?"),
        new("vram_budget",
            "GPU memory budget and usage per adapter counter group and pool (local and non-local): last and minimum budget, peak and last usage, and peak usage as a percentage of the minimum budget, from samples before $end.",
            """
            WITH c AS (
              SELECT gn.Value AS adapterGroup, cn.Value AS counter, x.Timestamp AS ts, x.Value AS value
              FROM PixCounterInfo i JOIN PixCounterGroup g ON g.Id = i.GroupId JOIN Strings gn ON gn.Id = g.NameId JOIN Strings cn ON cn.Id = i.NameId
              JOIN PixCounters x ON x.CounterId = i.Id
              WHERE gn.Value LIKE 'GPU Memory%' AND x.Timestamp < $end
            ), s AS (
              SELECT adapterGroup, CASE WHEN counter LIKE 'Non-Local%' THEN 'nonLocal' ELSE 'local' END AS pool, counter, value,
                     ROW_NUMBER() OVER (PARTITION BY adapterGroup, counter ORDER BY ts DESC) AS newest
              FROM c
            )
            SELECT adapterGroup, pool,
                   max(CASE WHEN counter LIKE '%Budget' AND newest = 1 THEN value END) AS lastBudgetMb,
                   min(CASE WHEN counter LIKE '%Budget' THEN value END) AS minBudgetMb,
                   max(CASE WHEN counter LIKE '%Usage' THEN value END) AS peakUsageMb,
                   max(CASE WHEN counter LIKE '%Usage' AND newest = 1 THEN value END) AS lastUsageMb,
                   round(100.0 * max(CASE WHEN counter LIKE '%Usage' THEN value END) / min(CASE WHEN counter LIKE '%Budget' THEN value END), 2) AS peakPercentOfBudget
            FROM s GROUP BY adapterGroup, pool ORDER BY adapterGroup, pool
            """,
            [], ["PixCounterInfo:GroupId", "PixCounterGroup:NameId", "PixCounters:Value", "Strings:Value"],
            ["Values are MB as recorded in the 'GPU Memory (Adapter #N)' groups.", "Samples before $start still count because a value holds until the next sample.",
             "peakPercentOfBudget compares the highest usage sample with the lowest budget sample, not simultaneous values."],
            "Did the game come close to its video memory budget?"),
        new("cpu_markers",
            "PIX CPU point markers (PIXSetMarker) of a process in [$start, $end) per marker name and thread: count, first and last timestamp.",
            """
            SELECT n.Value AS marker, t.Id AS threadRowId, t.ProcThreadId & 4294967295 AS threadId, tn.Value AS threadName,
                   count(*) AS markers, min(m.Timestamp) AS firstNs, max(m.Timestamp) AS lastNs
            FROM PixCpuMarker m JOIN PixMarkerInfo i ON i.Id = m.InfoId LEFT JOIN Strings n ON n.Id = i.NameId
            JOIN Threads t ON t.Id = m.ThreadId JOIN Processes p ON p.Id = t.ProcessRowId LEFT JOIN Strings tn ON tn.Id = t.ThreadNameId
            WHERE m.Timestamp >= $start AND m.Timestamp < $end AND p.ProcessId = coalesce($pid, $targetPid)
            GROUP BY i.Id, t.Id ORDER BY markers DESC
            """,
            [ProcessParam], ["PixCpuMarker:ThreadId", "PixMarkerInfo:NameId", "Threads:ProcessRowId", "Strings:Value"],
            ["PixCpuMarker.ThreadId is a Threads row id."],
            "Which markers did the game emit, and how often?"),
        new("cpu_execution_rollup",
            "PIX CPU events of a process overlapping [$start, $end) per event name, nesting level and thread: occurrences, inclusive total, average and maximum, with execution and stall sums.",
            """
            WITH e AS (
              SELECT e.EventId AS eventId, e.Level AS level, e.ThreadRowId AS thread, e.BeginTimestamp AS b, e.EndTimestamp AS en
              FROM PixCpuExecution e JOIN Threads t ON t.Id = e.ThreadRowId JOIN Processes p ON p.Id = t.ProcessRowId
              WHERE e.BeginTimestamp < $end AND e.EndTimestamp > $start AND p.ProcessId = coalesce($pid, $targetPid)
            ), times AS MATERIALIZED (
              SELECT EventId AS eventId, BeginTimestamp AS b, EndTimestamp AS en, Execution AS execution, Stall AS stall
              FROM PixCpuExecutionTimes WHERE BeginTimestamp < $end AND EndTimestamp > $start
            )
            SELECT s.Value AS event, e.level, e.thread AS threadRowId, count(*) AS occurrences,
                   sum(e.en - e.b) AS inclusiveNs, round(avg(e.en - e.b)) AS avgNs, max(e.en - e.b) AS maxNs,
                   sum(x.execution) AS executionNs, sum(x.stall) AS stallNs
            FROM e JOIN PixEventInfo i ON i.Id = e.eventId LEFT JOIN Strings s ON s.Id = i.NameId
            LEFT JOIN times x ON x.eventId = e.eventId AND x.b = e.b AND x.en = e.en
            GROUP BY e.eventId, e.level, e.thread ORDER BY inclusiveNs DESC
            """,
            [ProcessParam], ["PixCpuExecution:ThreadRowId", "PixCpuExecutionTimes:Execution", "PixEventInfo:NameId", "Threads:ProcessRowId", "Strings:Value"],
            ["Durations are whole events, not clipped to the window.", "Nested events (level > 0) overlap their parents: sum rows of one level, never across levels.",
             "Execution and stall match by (EventId, begin, end); identical tuples on different threads are counted on each."],
            "Which instrumented CPU scopes cost the most, and how much of that was stall?"),
        new("frames_vsync",
            "VSync intervals per monitor lane in [$start, $end): intervals, average, minimum, p50, p95, maximum and average refresh rate.",
            """
            WITH v AS (
              SELECT m.MarkerInfoId AS lane, dt.Value AS monitor,
                     m.Timestamp - LAG(m.Timestamp) OVER (PARTITION BY m.MarkerInfoId ORDER BY m.Timestamp) AS intervalNs
              FROM CustomMarker m JOIN CustomMarkerInfo i ON i.Id = m.MarkerInfoId JOIN Strings n ON n.Id = i.NameId
              LEFT JOIN CustomDataTypeInfo t ON t.Id = i.DataTypeId LEFT JOIN Strings dt ON dt.Id = t.NameId
              WHERE n.Value = 'VSync' AND m.Timestamp >= $start AND m.Timestamp < $end
            ), ranked AS (
              SELECT lane, monitor, intervalNs, ROW_NUMBER() OVER (PARTITION BY lane ORDER BY intervalNs) AS rn, COUNT(*) OVER (PARTITION BY lane) AS n
              FROM v WHERE intervalNs IS NOT NULL
            )
            SELECT lane, monitor, max(n) AS intervals, round(avg(intervalNs)) AS avgIntervalNs, min(intervalNs) AS minIntervalNs,
                   max(CASE WHEN rn = (50 * n + 99) / 100 THEN intervalNs END) AS p50IntervalNs,
                   max(CASE WHEN rn = (95 * n + 99) / 100 THEN intervalNs END) AS p95IntervalNs,
                   max(intervalNs) AS maxIntervalNs, round(1e9 / avg(intervalNs), 2) AS avgHz
            FROM ranked GROUP BY lane, monitor ORDER BY intervals DESC
            """,
            [], ["CustomMarker:MarkerInfoId", "CustomMarkerInfo:DataTypeId", "CustomDataTypeInfo:NameId", "Strings:Value"],
            [NearestRank, "VSync markers describe display refresh, not application frames; a lane with few, long intervals is an idle or secondary monitor."],
            "What refresh rate was the display running at, and were there hitches?"),
        new("frames_present",
            "Presented frames from GpuFrame in [$start, $end) by present call time: present duration, present-to-VSync latency and GPU busy duration as recorded.",
            """
            SELECT Id AS frame, PresentCallTime AS presentCallNs, PresentReturnTime - PresentCallTime AS presentDurationNs,
                   VSyncTime AS vsyncNs, VSyncTime - PresentCallTime AS presentToVsyncNs, GPUBusyDuration AS gpuBusyDuration
            FROM GpuFrame
            WHERE PresentCallTime IS NOT NULL AND PresentCallTime >= $start AND PresentCallTime < $end
            ORDER BY PresentCallTime
            """,
            [], ["GpuFrame:PresentCallTime", "GpuFrame:VSyncTime", "GpuFrame:GPUBusyDuration"],
            ["API-taken timing captures leave GpuFrame empty or all NULL; use frames_vsync instead.", "GPUBusyDuration is reported in its recorded unit, which is not documented."],
            "How long did each present take to reach the display?"),
        new("dropped_data",
            "Dropped and truncated recording data and lost ETW events: when non-zero, durations and counts in this capture are lower bounds.",
            """
            SELECT 'dropped' AS kind, Type AS type, count(*) AS ranges, sum(Count) AS events, min(BeginTimestamp) AS firstNs, max(EndTimestamp) AS lastNs FROM DroppedData GROUP BY Type
            UNION ALL SELECT 'truncated', NULL, count(*), sum(Count), NULL, NULL FROM TruncatedData
            UNION ALL SELECT 'lostEtwEvents', NULL, count(*), sum(LostEtwEventCount), min(Timestamp), max(Timestamp) FROM CaptureStats
            UNION ALL SELECT 'lostEtwBuffers', NULL, count(*), sum(LostEtwBufferCount), min(Timestamp), max(Timestamp) FROM CaptureStats
            """,
            [], ["DroppedData:Type", "TruncatedData:Count", "CaptureStats:LostEtwEventCount"],
            ["DroppedData.Type values are raw codes as recorded; LaneId values were observed to match OS thread ids."],
            "Is this capture complete, or did recording drop data?"),
        new("file_io_summary",
            "File I/O of a process overlapping [$start, $end) per operation type: operations, bytes, total and maximum duration.",
            """
            SELECT Type AS operation, count(*) AS operations, sum(Size) AS bytes, sum(EndTimestamp - BeginTimestamp) AS durationNs, max(EndTimestamp - BeginTimestamp) AS maxDurationNs
            FROM FileEvents
            WHERE BeginTimestamp < $end AND EndTimestamp > $start AND (ProcThreadId >> 32) = coalesce($pid, $targetPid)
            GROUP BY Type ORDER BY durationNs DESC
            """,
            [ProcessParam], ["FileEvents:Type", "FileEvents:ProcThreadId"],
            ["Empty unless the timing capture recorded file I/O.", "Durations overlap when operations run concurrently."],
            "Was the game stalling on file I/O?"),
        new("memory_summary",
            "CPU memory allocation and free events of a process in [$start, $end): events and bytes, split into allocations and frees.",
            """
            SELECT CASE WHEN IsFree THEN 'free' ELSE 'allocation' END AS kind, count(*) AS events, sum(Size) AS bytes, min(Timestamp) AS firstNs, max(Timestamp) AS lastNs
            FROM CpuMemoryEvent
            WHERE Timestamp >= $start AND Timestamp < $end AND OSProcessId = coalesce($pid, $targetPid)
            GROUP BY IsFree ORDER BY kind
            """,
            [ProcessParam], ["CpuMemoryEvent:IsFree", "CpuMemoryEvent:OSProcessId"],
            ["Empty unless the timing capture recorded memory events."],
            "How much CPU memory did the game allocate and free?"),
    ];

    public static NamedTimingQuery? Find(string name) => All.FirstOrDefault(q => q.Name.Equals(name?.Trim(), StringComparison.Ordinal));
}
