using Microsoft.Data.Sqlite;

namespace PixMcp.Pix;

internal sealed partial class TimingDatabase
{
    internal TimingOverviewDto Overview(string handle, uint? processId, int offset, int limit) => Guard<TimingOverviewDto>(() =>
    {
        ValidatePage(offset, limit);
        var (_, _, range) = Range();
        var capabilities = new Dictionary<string, TimingCapabilityDto>();
        void Capability(string name, string table, params string[] columns) => capabilities[name] = Has(table, columns)
            ? new("available") : new("unsupported", $"Required {table} schema is absent.");
        Capability("cpuEvents", "PixCpuExecution", "BeginTimestamp", "EndTimestamp", "ThreadRowId", "EventId");
        Capability("gpuEvents", "PixGpuExecution", "BeginTimestamp", "EndTimestamp", "ApiCommandQueueId", "EventId");
        Capability("cpuExecutionTiming", "PixCpuExecutionTimes", "EventId", "BeginTimestamp", "EndTimestamp", "Execution", "Stall");
        Capability("submissions", "ApiQueueExecution", "Id", "ApiCommandQueueId", "ThreadId", "SubmitTimestamp", "BeginTimestamp", "EndTimestamp");
        Capability("threadSwitches", "ContextSwitch", "Core", "Timestamp", "FromProcThreadId", "ToProcThreadId", "FromThreadWaitReason");
        Capability("counters", "PixCounters", "CounterId", "Timestamp", "Value");
        Capability("samples", "CpuSample", "Core", "Timestamp", "ProcThreadId");
        Capability("stacks", "Stacks", "Id", "NumFrames", "Addresses");
        Capability("symbols", "FunctionInformation", "ModuleId", "Offset", "Size", "DecoratedNameId");
        if (!Has("StackEvents", "OSThreadId", "StartTimestamp", "EndTimestamp", "StackEventData") || !HasFunction("FindStackId", 2))
            capabilities["stacks"] = new("unsupported", "The recorded stack association schema or PixStorage FindStackId function is absent.");
        if (!Has("SymbolStrings", "Id", "Value")) capabilities["symbols"] = new("unsupported", "The recorded symbol string table is absent.");
        Require("Processes", "Id", "ProcessId", "ImageNameId");
        Require("Threads", "Id", "ProcThreadId", "ThreadNameId", "ProcessRowId", "SampleCount");
        Require("Strings", "Id", "Value");
        (string, object?)[] filter = [("$pid", processId), ("$offset", offset), ("$limit", limit)];
        const string processWhere = " WHERE ($pid IS NULL OR p.ProcessId=$pid)";
        long processTotal = Count("Processes p", processWhere, ("$pid", processId));
        var processes = Rows("SELECT p.Id,p.ProcessId,s.Value,COUNT(t.Id),COALESCE(SUM(t.SampleCount),0) FROM Processes p LEFT JOIN Strings s ON s.Id=p.ImageNameId LEFT JOIN Threads t ON t.ProcessRowId=p.Id" + processWhere +
            " GROUP BY p.Id ORDER BY COALESCE(SUM(t.SampleCount),0) DESC,p.Id LIMIT $limit OFFSET $offset",
            r => new TimingProcessDto(Id(r, 0), checked((uint)r.GetInt64(1)), Text(r, 2), r.GetInt64(3), r.GetInt64(4)), filter);
        const string threadFrom = "Threads t JOIN Processes p ON p.Id=t.ProcessRowId LEFT JOIN Strings s ON s.Id=t.ThreadNameId";
        long threadTotal = Count(threadFrom, processWhere, ("$pid", processId));
        var threads = Rows("SELECT t.Id,p.ProcessId,t.ProcThreadId,s.Value,t.SampleCount FROM " + threadFrom + processWhere +
            " ORDER BY COALESCE(t.SampleCount,0) DESC,t.Id LIMIT $limit OFFSET $offset",
            r => new TimingThreadDto(Id(r, 0), checked((uint)r.GetInt64(1)), unchecked((uint)r.GetInt64(2)), Text(r, 3), Number(r, 4)), filter);
        var queues = new List<TimingQueueDto>(); long queueTotal = 0;
        if (Has("ApiCommandQueue", "Id", "ProcessId", "NameId", "TypeId"))
        {
            const string queueFrom = "ApiCommandQueue q JOIN Processes p ON p.Id=q.ProcessId LEFT JOIN Strings s ON s.Id=q.NameId LEFT JOIN Strings ty ON ty.Id=q.TypeId";
            queueTotal = Count(queueFrom, processWhere, ("$pid", processId));
            queues = Rows("SELECT q.Id,p.ProcessId,s.Value,ty.Value FROM " + queueFrom + processWhere + " ORDER BY q.Id LIMIT $limit OFFSET $offset",
                r => new TimingQueueDto(Id(r, 0), checked((uint)r.GetInt64(1)), Text(r, 2), Text(r, 3)), filter);
        }
        long sampleCount = Has("CpuSample", "ProcThreadId") ? Count("CpuSample", " WHERE ($pid IS NULL OR (ProcThreadId >> 32)=$pid)", ("$pid", processId)) : 0;
        long stackCount = Has("Stacks", "Id") ? Count("Stacks") : 0;
        long symbolCount = Has("FunctionInformation", "Id") ? Count("FunctionInformation") : 0;
        if (capabilities["samples"].State == "available" && sampleCount == 0) capabilities["samples"] = new("empty", "No recorded CPU samples match this process selection.");
        if (capabilities["stacks"].State == "available" && stackCount == 0) capabilities["stacks"] = new("empty", "No callstacks were recorded.");
        if (capabilities["symbols"].State == "available" && symbolCount == 0) capabilities["symbols"] = new("unresolved", "No resolved function symbols are stored. pix_timing_resolve_symbols can load matching PDBs.");
        var calls = new List<ToolCallDto>
        {
            new("pix_timing_events", new { handle, processId }), new("pix_timing_counters_list", new { handle, processId }),
            new("pix_timing_hotspots", new { handle, processId }),
            new("pix_timing_submissions", new { handle, processId }),
        };
        if (offset + limit < Math.Max(processTotal, Math.Max(threadTotal, queueTotal)))
            calls.Add(new("pix_timing_overview", new { handle, processId, offset = offset + limit, limit }));
        return new(handle, range, capabilities, Paging.Page(processes, processTotal, offset, limit), Paging.Page(threads, threadTotal, offset, limit),
            Paging.Page(queues, queueTotal, offset, limit), Has("PixCounterInfo", "Id") ? Count("PixCounterInfo") : 0, sampleCount, stackCount, symbolCount, calls);
    });

    internal TimingEventsDto Events(string handle, string domain, uint? processId, uint? threadId, string? queueId,
        string? nameContains, long? start, long? end, string orderBy, int offset, int limit) => Guard<TimingEventsDto>(() =>
    {
        ValidatePage(offset, limit);
        if (domain is not ("cpu" or "gpu" or "all")) throw new PixToolException("invalid_arguments", "domain must be cpu, gpu, or all.");
        if (orderBy is not ("start" or "duration")) throw new PixToolException("invalid_arguments", "orderBy must be start or duration.");
        if (threadId.HasValue && domain != "cpu") throw new PixToolException("invalid_arguments", "threadId requires domain=cpu.");
        if (queueId is not null && domain != "gpu") throw new PixToolException("invalid_arguments", "queueId requires domain=gpu.");
        long? queue = queueId is null ? null : ParseId(queueId, nameof(queueId));
        var (a, b, provenance) = Range(start, end);
        Require("PixEventInfo", "Id", "NameId"); Require("Strings", "Id", "Value"); Require("Processes", "Id", "ProcessId");
        var sources = new List<string>();
        if (domain is "cpu" or "all")
        {
            Require("PixCpuExecution", "BeginTimestamp", "EndTimestamp", "Level", "ThreadRowId", "EventId");
            Require("Threads", "Id", "ProcessRowId", "ProcThreadId", "ThreadNameId");
            sources.Add("SELECT e.EventId,'cpu' Domain,s.Value Name,e.BeginTimestamp,e.EndTimestamp,e.Level,p.ProcessId,(t.ProcThreadId & 4294967295) ThreadId,tn.Value ThreadName,NULL QueueId,NULL QueueName,t.Id LaneId FROM PixCpuExecution e JOIN PixEventInfo info ON info.Id=e.EventId LEFT JOIN Strings s ON s.Id=info.NameId JOIN Threads t ON t.Id=e.ThreadRowId JOIN Processes p ON p.Id=t.ProcessRowId LEFT JOIN Strings tn ON tn.Id=t.ThreadNameId");
        }
        if (domain is "gpu" or "all")
        {
            Require("PixGpuExecution", "BeginTimestamp", "EndTimestamp", "Level", "ApiCommandQueueId", "EventId");
            Require("ApiCommandQueue", "Id", "ProcessId", "NameId");
            sources.Add("SELECT e.EventId,'gpu' Domain,s.Value Name,e.BeginTimestamp,e.EndTimestamp,e.Level,p.ProcessId,NULL ThreadId,NULL ThreadName,q.Id QueueId,qn.Value QueueName,q.Id LaneId FROM PixGpuExecution e JOIN PixEventInfo info ON info.Id=e.EventId LEFT JOIN Strings s ON s.Id=info.NameId JOIN ApiCommandQueue q ON q.Id=e.ApiCommandQueueId JOIN Processes p ON p.Id=q.ProcessId LEFT JOIN Strings qn ON qn.Id=q.NameId");
        }
        string from = "(" + string.Join(" UNION ALL ", sources) + ")";
        const string where = " WHERE BeginTimestamp < $end AND (EndTimestamp > $start OR (EndTimestamp=BeginTimestamp AND BeginTimestamp >= $start)) AND EndTimestamp>=BeginTimestamp AND ($pid IS NULL OR ProcessId=$pid) AND ($tid IS NULL OR ThreadId=$tid) AND ($queue IS NULL OR QueueId=$queue) AND ($name IS NULL OR instr(lower(COALESCE(Name,'')),lower($name))>0)";
        (string, object?)[] parameters = [("$start", a), ("$end", b), ("$pid", processId), ("$tid", threadId), ("$queue", queue), ("$name", nameContains)];
        long total = Count(from, where, parameters);
        string ordering = (orderBy == "duration" ? "(EndTimestamp-BeginTimestamp) DESC," : "") + "BeginTimestamp,EndTimestamp,Domain,LaneId,EventId,Level";
        var events = Rows("SELECT EventId,Domain,Name,BeginTimestamp,EndTimestamp,Level,ProcessId,ThreadId,ThreadName,QueueId,QueueName FROM " + from + where + " ORDER BY " + ordering + " LIMIT $limit OFFSET $offset", r =>
        {
            long begin = r.GetInt64(3), finish = r.GetInt64(4);
            return new RecordedTimingEventDto(Id(r, 0), r.GetString(1), Text(r, 2), Ns(begin), Ns(finish), Ns(finish - begin),
                Ns(Math.Max(0, Math.Min(finish, b) - Math.Max(begin, a))), r.GetInt32(5), checked((uint)r.GetInt64(6)),
                r.IsDBNull(7) ? null : checked((uint)r.GetInt64(7)), Text(r, 8), r.IsDBNull(9) ? null : Id(r, 9), Text(r, 10));
        }, [.. parameters, ("$limit", limit), ("$offset", offset)]);
        TimingCapabilityDto executionCapability = new("not_applicable", "The selected page contains no CPU executions.");
        if (events.Any(e => e.Domain == "cpu"))
        {
            bool available = Has("PixCpuExecutionTimes", "EventId", "BeginTimestamp", "EndTimestamp", "Execution", "Stall");
            executionCapability = available ? new("available", "executionNs/stallNs describe each complete event, not the clipped overlap interval.")
                : new("unsupported", "PixCpuExecutionTimes is absent from this capture/extension schema.");
            var values = new Dictionary<(string id, string begin, string end), (long execution, long stall, int count, long occurrences)>();
            if (available)
            {
                try
                {
                    // Bound materialized timing data to this page, including duplicate native rows
                    // needed to detect ambiguous attribution across lanes.
                    var wanted = events.Where(e => e.Domain == "cpu").Select(e => (e.EventId, e.BeginNs, e.EndNs)).Distinct().ToArray();
                    var timingParameters = new List<(string, object?)>();
                    var tuples = new List<string>();
                    for (int i = 0; i < wanted.Length; i++)
                    {
                        tuples.Add($"($id{i},$begin{i},$end{i})");
                        timingParameters.Add(($"$id{i}", ParseId(wanted[i].EventId, "eventId")));
                        timingParameters.Add(($"$begin{i}", ParseId(wanted[i].BeginNs, "beginNs")));
                        timingParameters.Add(($"$end{i}", ParseId(wanted[i].EndNs, "endNs")));
                    }
                    string timingSql = "WITH wanted(Id,BeginTime,EndTime) AS (VALUES " + string.Join(",", tuples) +
                        "), occurrences AS (SELECT cpu.EventId,cpu.BeginTimestamp,cpu.EndTimestamp,COUNT(*) Total FROM PixCpuExecution cpu" +
                        " JOIN wanted w ON w.Id=cpu.EventId AND w.BeginTime=cpu.BeginTimestamp AND w.EndTime=cpu.EndTimestamp" +
                        " GROUP BY cpu.EventId,cpu.BeginTimestamp,cpu.EndTimestamp)" +
                        " SELECT e.EventId,e.BeginTimestamp,e.EndTimestamp,e.Execution,e.Stall,c.Total FROM PixCpuExecutionTimes e" +
                        " JOIN occurrences c ON c.EventId=e.EventId AND c.BeginTimestamp=e.BeginTimestamp AND c.EndTimestamp=e.EndTimestamp";
                    foreach (var row in Rows(timingSql,
                        r => (id: Id(r, 0), begin: Id(r, 1), end: Id(r, 2), execution: Number(r, 3), stall: Number(r, 4), occurrences: Number(r, 5)), timingParameters.ToArray()))
                    {
                        var key = (row.id, row.begin, row.end);
                        values.TryGetValue(key, out var previous);
                        values[key] = (row.execution, row.stall, previous.count + 1, row.occurrences);
                    }
                }
                catch (SqliteException ex)
                {
                    Check(); available = false;
                    executionCapability = new("unavailable", "Execution/stall calculation failed: " + ex.Message);
                }
            }
            for (int i = 0; i < events.Count; i++)
            {
                RecordedTimingEventDto row = events[i];
                if (row.Domain != "cpu") continue;
                bool found = values.TryGetValue((row.EventId, row.BeginNs, row.EndNs), out var value);
                bool exact = available && found && value.count == 1 && value.occurrences == 1 && value.execution >= 0 && value.stall >= 0;
                events[i] = row with { ExecutionNs = exact ? Ns(value.execution) : null, StallNs = exact ? Ns(value.stall) : null,
                    ExecutionTimingState = exact ? "available" : !available ? executionCapability.State : found && (value.count > 1 || value.occurrences > 1) ? "ambiguous" : "unavailable" };
            }
        }
        PageResult<RecordedTimingEventDto> page = Paging.Page(events, total, offset, limit);
        return new(handle, provenance, page, executionCapability, page.NextOffset is int next ? [new("pix_timing_events", new
            { handle, domain, processId, threadId, queueId, nameContains, startNs = Ns(a), endNs = Ns(b), orderBy, offset = next, limit })] : []);
    });

    private List<TimingCounterDto> CounterMetadata(uint? processId = null, string? nameContains = null)
    {
        Require("PixCounterInfo", "Id", "GroupId", "ProcessId", "NameId", "DescriptionId", "UnitsId");
        Require("PixCounterGroup", "Id", "ParentGroupId", "NameId"); Require("Strings", "Id", "Value"); Require("Processes", "Id", "ProcessId");
        var groups = Rows("SELECT g.Id,g.ParentGroupId,s.Value FROM PixCounterGroup g LEFT JOIN Strings s ON s.Id=g.NameId", r =>
            (id: r.GetInt64(0), parent: r.IsDBNull(1) ? (long?)null : r.GetInt64(1), name: Text(r, 2))).ToDictionary(g => g.id);
        return Rows("SELECT c.Id,c.GroupId,n.Value,d.Value,u.Value,p.ProcessId FROM PixCounterInfo c LEFT JOIN Strings n ON n.Id=c.NameId LEFT JOIN Strings d ON d.Id=c.DescriptionId LEFT JOIN Strings u ON u.Id=c.UnitsId LEFT JOIN Processes p ON p.Id=c.ProcessId WHERE ($pid IS NULL OR p.ProcessId=$pid) AND ($name IS NULL OR instr(lower(COALESCE(n.Value,'')),lower($name))>0) ORDER BY c.Id", r =>
        {
            var path = new List<string>(); var seen = new HashSet<long>();
            long? group = r.IsDBNull(1) ? null : r.GetInt64(1);
            while (group.HasValue && groups.TryGetValue(group.Value, out var item) && seen.Add(group.Value))
            { if (item.name is not null) path.Add(item.name); group = item.parent; }
            path.Reverse();
            return new TimingCounterDto(Id(r, 0), Text(r, 2), path, Text(r, 3), Text(r, 4), r.IsDBNull(5) ? null : checked((uint)r.GetInt64(5)));
        }, ("$pid", processId), ("$name", nameContains));
    }

    internal TimingCountersDto Counters(string handle, uint? processId, string? nameContains, int offset, int limit) => Guard<TimingCountersDto>(() =>
    {
        ValidatePage(offset, limit);
        List<TimingCounterDto> all = CounterMetadata(processId, nameContains);
        PageResult<TimingCounterDto> page = Paging.Page(all.Skip(offset).Take(limit).ToArray(), all.Count, offset, limit);
        var next = new List<ToolCallDto>();
        if (page.NextOffset is int following) next.Add(new("pix_timing_counters_list", new { handle, processId, nameContains, offset = following, limit }));
        next.AddRange(page.Items.Select(c => new ToolCallDto("pix_timing_counters_read", new { handle, counterId = c.CounterId })));
        return new(handle, page, next);
    });

    internal TimingCounterSamplesDto CounterSamples(string handle, string counterId, long? start, long? end, int offset, int limit) => Guard<TimingCounterSamplesDto>(() =>
    {
        ValidatePage(offset, limit);
        long id = ParseId(counterId, nameof(counterId));
        var (a, b, provenance) = Range(start, end);
        Require("PixCounters", "CounterId", "Timestamp", "Value");
        TimingCounterDto counter = CounterMetadata().FirstOrDefault(c => c.CounterId == Ns(id))
            ?? throw new PixToolException("timing_counter_not_found", $"Counter {counterId} was not recorded in this capture.", nextCalls: [new("pix_timing_counters_list", new { handle })]);
        const string where = " WHERE CounterId=$id AND Timestamp >= $start AND Timestamp < $end";
        (string, object?)[] parameters = [("$id", id), ("$start", a), ("$end", b)];
        long total = Count("PixCounters", where, parameters);
        var samples = Rows("SELECT Timestamp,Value FROM PixCounters" + where + " ORDER BY Timestamp,Value LIMIT $limit OFFSET $offset",
            r => new TimingCounterSampleDto(Id(r, 0), r.GetDouble(1)), [.. parameters, ("$limit", limit), ("$offset", offset)]);
        PageResult<TimingCounterSampleDto> page = Paging.Page(samples, total, offset, limit);
        return new(handle, provenance, counter, page, page.NextOffset is int next ? [new("pix_timing_counters_read", new
            { handle, counterId, startNs = Ns(a), endNs = Ns(b), offset = next, limit })] : []);
    });
}
