using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PixMcp.Pix;

internal sealed partial class TimingDatabase
{
    internal TimingOverviewDto Overview(string handle, uint? processId, int offset, int limit, string rangeMode = RangeModeFull) => Guard<TimingOverviewDto>(() =>
    {
        ValidatePage(offset, limit);
        var (_, _, range) = Range(null, null, rangeMode);
        var capabilities = new Dictionary<string, TimingCapabilityDto>();
        void Capability(string name, string table, params string[] columns) => capabilities[name] = Has(table, columns)
            ? new("available") : new("unsupported", $"Required {table} schema is absent.");
        // A recorded family: unsupported without its schema, empty without rows, otherwise available with its row count.
        void Family(string name, string table, string[] columns, string? emptyReason = null)
        {
            if (!Has(table, columns)) { capabilities[name] = new("unsupported", $"Required {table} schema is absent."); return; }
            long rows = Count(Quote(table));
            capabilities[name] = rows > 0 ? new("available", null, rows) : new("empty", emptyReason ?? $"{table} has no rows in this capture.", 0);
        }
        Family("cpuEvents", "PixCpuExecution", ["BeginTimestamp", "EndTimestamp", "ThreadRowId", "EventId"]);
        Family("cpuMarkers", "PixCpuMarker", ["InfoId", "Timestamp", "ThreadId"]);
        Family("gpuMarkers", "PixGpuExecution", ["BeginTimestamp", "EndTimestamp", "ApiCommandQueueId", "EventId"],
            "No GPU-side PIX events were recorded; recorded GPU work is in gpuSubmissions and gpuHardware.");
        Family("gpuSubmissions", "ApiQueueExecution", ["Id", "ApiCommandQueueId", "ThreadId", "SubmitTimestamp", "BeginTimestamp", "EndTimestamp"]);
        Family("gpuHardware", "GpuWorkRange", ["HardwareQueueId", "BeginTimestamp", "EndTimestamp"]);
        Capability("cpuExecutionTiming", "PixCpuExecutionTimes", "EventId", "BeginTimestamp", "EndTimestamp", "Execution", "Stall");
        Family("threadSwitches", "ContextSwitch", ["Core", "Timestamp", "FromProcThreadId", "ToProcThreadId", "FromThreadWaitReason"]);
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
        bool lifetimes = Has("Threads", "StartTimestamp", "EndTimestamp", "PixEventCount", "ContextSwitchCount", "MarkerCount");
        var threads = Rows("SELECT t.Id,p.ProcessId,t.ProcThreadId,s.Value,t.SampleCount," +
            (lifetimes ? "t.StartTimestamp,t.EndTimestamp,t.PixEventCount,t.ContextSwitchCount,t.MarkerCount" : "NULL,NULL,NULL,NULL,NULL") +
            " FROM " + threadFrom + processWhere + " ORDER BY COALESCE(t.SampleCount,0) DESC,t.Id LIMIT $limit OFFSET $offset",
            r => new TimingThreadDto(Id(r, 0), checked((uint)r.GetInt64(1)), unchecked((uint)r.GetInt64(2)), Text(r, 3), Number(r, 4))
            {
                StartNs = r.IsDBNull(5) ? null : Id(r, 5),
                EndNs = r.IsDBNull(6) ? null : Id(r, 6),
                PixEventCount = r.IsDBNull(7) ? null : r.GetInt64(7),
                ContextSwitchCount = r.IsDBNull(8) ? null : r.GetInt64(8),
                MarkerCount = r.IsDBNull(9) ? null : r.GetInt64(9),
            }, filter);

        var queues = new List<TimingQueueDto>(); long queueTotal = 0;
        if (Has("ApiCommandQueue", "Id", "ProcessId", "NameId", "TypeId"))
        {
            bool details = Has("ApiCommandQueue", "BeginTimestamp", "EndTimestamp", "ApiExecutionCount", "CommandListCount", "MaxWorkLevel");
            bool adapters = Has("ApiCommandQueue", "HardwareAdapterId") && Has("HardwareAdapter", "Id", "NameId");
            string queueFrom = "ApiCommandQueue q JOIN Processes p ON p.Id=q.ProcessId LEFT JOIN Strings s ON s.Id=q.NameId LEFT JOIN Strings ty ON ty.Id=q.TypeId" +
                (adapters ? " LEFT JOIN HardwareAdapter ha ON ha.Id=q.HardwareAdapterId LEFT JOIN Strings an ON an.Id=ha.NameId" : "");
            queueTotal = Count(queueFrom, processWhere, ("$pid", processId));
            queues = Rows("SELECT q.Id,p.ProcessId,s.Value,ty.Value," + (adapters ? "an.Value" : "NULL") + "," +
                (details ? "q.BeginTimestamp,q.EndTimestamp,q.ApiExecutionCount,q.CommandListCount,q.MaxWorkLevel" : "NULL,NULL,NULL,NULL,NULL") +
                " FROM " + queueFrom + processWhere + " ORDER BY q.Id LIMIT $limit OFFSET $offset",
                r => new TimingQueueDto(Id(r, 0), checked((uint)r.GetInt64(1)), Text(r, 2), Text(r, 3))
                {
                    AdapterName = Text(r, 4),
                    BeginNs = r.IsDBNull(5) ? null : Id(r, 5),
                    EndNs = r.IsDBNull(6) ? null : Id(r, 6),
                    ApiExecutionCount = r.IsDBNull(7) ? null : r.GetInt64(7),
                    CommandListCount = r.IsDBNull(8) ? null : r.GetInt64(8),
                    MaxWorkLevel = r.IsDBNull(9) ? null : r.GetInt64(9),
                }, filter);
        }

        // Hardware queues are system-wide (not filtered by process); only queues that carried GpuWorkRange rows are listed.
        var hardwareQueues = new List<TimingHardwareQueueDto>(); long hardwareTotal = 0;
        if (Has("HardwareCommandQueue", "Id", "NameId") && Has("GpuWorkRange", "HardwareQueueId", "BeginTimestamp", "EndTimestamp"))
        {
            bool adapters = Has("HardwareCommandQueue", "AdapterId") && Has("HardwareAdapter", "Id", "NameId");
            string hardwareFrom = "HardwareCommandQueue h JOIN (SELECT HardwareQueueId,COUNT(*) Ranges,MIN(BeginTimestamp) FirstTimestamp,MAX(EndTimestamp) LastTimestamp FROM GpuWorkRange GROUP BY HardwareQueueId) w ON w.HardwareQueueId=h.Id" +
                " LEFT JOIN Strings hn ON hn.Id=h.NameId" + (adapters ? " LEFT JOIN HardwareAdapter ha ON ha.Id=h.AdapterId LEFT JOIN Strings an ON an.Id=ha.NameId" : "");
            hardwareTotal = Count(hardwareFrom);
            hardwareQueues = Rows("SELECT h.Id,hn.Value," + (adapters ? "an.Value" : "NULL") + ",w.Ranges,w.FirstTimestamp,w.LastTimestamp FROM " + hardwareFrom +
                " ORDER BY w.Ranges DESC,h.Id LIMIT $limit OFFSET $offset",
                r => new TimingHardwareQueueDto(Id(r, 0), Text(r, 1), Text(r, 2), r.GetInt64(3), r.IsDBNull(4) ? null : Id(r, 4), r.IsDBNull(5) ? null : Id(r, 5)),
                ("$offset", offset), ("$limit", limit));
        }

        long sampleCount = Has("CpuSample", "ProcThreadId") ? Count("CpuSample", " WHERE ($pid IS NULL OR (ProcThreadId >> 32)=$pid)", ("$pid", processId)) : 0;
        long stackCount = Has("Stacks", "Id") ? Count("Stacks") : 0;
        long symbolCount = Has("FunctionInformation", "Id") ? Count("FunctionInformation") : 0;
        if (capabilities["samples"].State == "available" && sampleCount == 0) capabilities["samples"] = new("empty", "No recorded CPU samples match this process selection.");
        if (capabilities["stacks"].State == "available" && stackCount == 0) capabilities["stacks"] = new("empty", "No callstacks were recorded.");
        if (capabilities["symbols"].State == "available" && symbolCount == 0) capabilities["symbols"] = new("unresolved", "No resolved function symbols are stored. pix_timing_resolve_symbols can load matching PDBs.");

        var calls = new List<ToolCallDto>();
        if (capabilities["gpuSubmissions"].State == "available") calls.Add(new("pix_timing_events", new { handle, processId, domain = "gpuSubmissions", rangeMode }));
        if (capabilities["gpuHardware"].State == "available") calls.Add(new("pix_timing_events", new { handle, domain = "gpuHardware", rangeMode }));
        if (capabilities["cpuEvents"].State == "available")
        {
            // The marker tree of the thread with the most PIX events is the better CPU entry point; raw events otherwise.
            if (threads.Where(t => t.PixEventCount > 0).OrderByDescending(t => t.PixEventCount).FirstOrDefault() is { } marked)
                calls.Add(new("pix_timing_tree", new { handle, threadRowId = marked.ThreadRowId, rangeMode }));
            else calls.Add(new("pix_timing_events", new { handle, processId, domain = "cpu", rangeMode }));
        }
        calls.Add(new("pix_timing_counters_list", new { handle, processId }));
        calls.Add(new("pix_timing_hotspots", new { handle, processId, rangeMode }));
        calls.Add(new("pix_timing_submissions", new { handle, processId, rangeMode }));
        calls.Add(new("pix_timing_schema", new { handle }));
        if (offset + limit < new[] { processTotal, threadTotal, queueTotal, hardwareTotal }.Max())
            calls.Add(new("pix_timing_overview", new { handle, processId, offset = offset + limit, limit, rangeMode }));
        TimingOverviewSectionsDto sections = OverviewSections(handle, rangeMode, capabilities, range);
        // A call an insight already offers is not repeated at the top level (arguments compared with nulls omitted).
        var offered = sections.Insights.SelectMany(i => i.NextCalls).Select(c => c.Tool + Json.Serialize(c.Arguments)).ToHashSet(StringComparer.Ordinal);
        calls.RemoveAll(c => offered.Contains(c.Tool + Json.Serialize(c.Arguments)));
        return new TimingOverviewDto(handle, range, capabilities, Paging.Page(processes, processTotal, offset, limit), Paging.Page(threads, threadTotal, offset, limit),
            Paging.Page(queues, queueTotal, offset, limit), Has("PixCounterInfo", "Id") ? Count("PixCounterInfo") : 0, sampleCount, stackCount, symbolCount, calls,
            Paging.Page(hardwareQueues, hardwareTotal, offset, limit))
        {
            Sections = sections,
        };
    });

    public static readonly string[] EventDomains = ["cpu", "cpuMarkers", "gpuMarkers", "gpuSubmissions", "gpuHardware", "all"];

    /// <summary>One recorded family pix_timing_events can page; every SQL projects the same 19 columns in the same order.</summary>
    private sealed record EventSource(string Domain, string Source, string Table, bool Thread, bool Queue, string Sql);

    private const string EventColumnsHeader = "SELECT NULL Source,NULL Domain,NULL RowKey,NULL EventId,NULL Name,NULL BeginTimestamp,NULL EndTimestamp,NULL Level,NULL ProcessId,NULL ThreadId," +
        "NULL ThreadName,NULL QueueId,NULL QueueName,NULL LaneId,NULL SubmitTimestamp,NULL HardwareQueueId,NULL HardwareQueueName,NULL OverlapLevel,NULL Color WHERE 0";

    private static readonly EventSource[] EventSources =
    [
        new("cpu", "pixCpuEvent", "PixCpuExecution", true, false,
            "SELECT 'pixCpuEvent' Source,'cpu' Domain,e.rowid RowKey,e.EventId EventId,s.Value Name,e.BeginTimestamp BeginTimestamp,e.EndTimestamp EndTimestamp,e.Level Level,p.ProcessId ProcessId," +
            "(t.ProcThreadId & 4294967295) ThreadId,tn.Value ThreadName,NULL QueueId,NULL QueueName,t.Id LaneId,NULL SubmitTimestamp,NULL HardwareQueueId,NULL HardwareQueueName,NULL OverlapLevel,e.Color Color" +
            " FROM PixCpuExecution e JOIN PixEventInfo info ON info.Id=e.EventId LEFT JOIN Strings s ON s.Id=info.NameId JOIN Threads t ON t.Id=e.ThreadRowId JOIN Processes p ON p.Id=t.ProcessRowId LEFT JOIN Strings tn ON tn.Id=t.ThreadNameId"),
        new("cpuMarkers", "pixCpuMarker", "PixCpuMarker", true, false,
            "SELECT 'pixCpuMarker','cpuMarkers',NULL,m.InfoId,s.Value,m.Timestamp,m.Timestamp,0,p.ProcessId,(t.ProcThreadId & 4294967295),tn.Value,NULL,NULL,t.Id,NULL,NULL,NULL,NULL,CAST(m.Color AS INTEGER)" +
            " FROM PixCpuMarker m JOIN PixMarkerInfo info ON info.Id=m.InfoId LEFT JOIN Strings s ON s.Id=info.NameId JOIN Threads t ON t.Id=m.ThreadId JOIN Processes p ON p.Id=t.ProcessRowId LEFT JOIN Strings tn ON tn.Id=t.ThreadNameId"),
        new("gpuMarkers", "pixGpuMarker", "PixGpuExecution", false, true,
            "SELECT 'pixGpuMarker','gpuMarkers',NULL,e.EventId,s.Value,e.BeginTimestamp,e.EndTimestamp,e.Level,p.ProcessId,NULL,NULL,q.Id,qn.Value,q.Id,NULL,NULL,NULL,NULL,e.Color" +
            " FROM PixGpuExecution e JOIN PixEventInfo info ON info.Id=e.EventId LEFT JOIN Strings s ON s.Id=info.NameId JOIN ApiCommandQueue q ON q.Id=e.ApiCommandQueueId JOIN Processes p ON p.Id=q.ProcessId LEFT JOIN Strings qn ON qn.Id=q.NameId"),
        new("gpuSubmissions", "apiSubmission", "ApiQueueExecution", true, true,
            "SELECT 'apiSubmission','gpuSubmissions',x.Id,x.Id,'ExecuteCommandLists',x.BeginTimestamp,x.EndTimestamp,0,p.ProcessId,(t.ProcThreadId & 4294967295),tn.Value,q.Id,qn.Value,q.Id,x.SubmitTimestamp,NULL,NULL,NULL,NULL" +
            " FROM ApiQueueExecution x JOIN ApiCommandQueue q ON q.Id=x.ApiCommandQueueId JOIN Processes p ON p.Id=q.ProcessId LEFT JOIN Strings qn ON qn.Id=q.NameId LEFT JOIN Threads t ON t.Id=x.ThreadId LEFT JOIN Strings tn ON tn.Id=t.ThreadNameId"),
        new("gpuHardware", "hardwareQueue", "GpuWorkRange", false, true,
            "SELECT 'hardwareQueue','gpuHardware',g.Id,g.Id,COALESCE(hn.Value,'GpuWorkRange'),g.BeginTimestamp,g.EndTimestamp,COALESCE(g.OverlapLevel,0),p.ProcessId,NULL,NULL,q.Id,qn.Value,h.Id,NULL,h.Id,hn.Value,g.OverlapLevel,NULL" +
            " FROM GpuWorkRange g JOIN HardwareCommandQueue h ON h.Id=g.HardwareQueueId LEFT JOIN Strings hn ON hn.Id=h.NameId LEFT JOIN ApiCommandQueue q ON q.Id=g.ApiCommandQueueId LEFT JOIN Processes p ON p.Id=q.ProcessId LEFT JOIN Strings qn ON qn.Id=q.NameId"),
    ];

    private bool EventSourcePresent(EventSource source) => source.Domain switch
    {
        "cpu" => Has("PixCpuExecution", "BeginTimestamp", "EndTimestamp", "Level", "ThreadRowId", "EventId", "Color") && Has("PixEventInfo", "Id", "NameId")
            && Has("Threads", "Id", "ProcessRowId", "ProcThreadId", "ThreadNameId"),
        "cpuMarkers" => Has("PixCpuMarker", "InfoId", "Timestamp", "ThreadId", "Color") && Has("PixMarkerInfo", "Id", "NameId")
            && Has("Threads", "Id", "ProcessRowId", "ProcThreadId", "ThreadNameId"),
        "gpuMarkers" => Has("PixGpuExecution", "BeginTimestamp", "EndTimestamp", "Level", "ApiCommandQueueId", "EventId", "Color") && Has("PixEventInfo", "Id", "NameId")
            && Has("ApiCommandQueue", "Id", "ProcessId", "NameId"),
        "gpuSubmissions" => Has("ApiQueueExecution", "Id", "ApiCommandQueueId", "ThreadId", "SubmitTimestamp", "BeginTimestamp", "EndTimestamp")
            && Has("ApiCommandQueue", "Id", "ProcessId", "NameId") && Has("Threads", "Id", "ProcThreadId", "ThreadNameId"),
        "gpuHardware" => Has("GpuWorkRange", "Id", "HardwareQueueId", "ApiCommandQueueId", "BeginTimestamp", "EndTimestamp", "OverlapLevel")
            && Has("HardwareCommandQueue", "Id", "NameId") && Has("ApiCommandQueue", "Id", "ProcessId", "NameId"),
        _ => false,
    };

    internal TimingEventsDto Events(string handle, string domain, uint? processId, uint? threadId, string? queueId,
        string? nameContains, long? start, long? end, string orderBy, int offset, int limit, string rangeMode = RangeModeFull,
        string? queueName = null, string? hardwareQueueId = null, int generation = 0) => Guard<TimingEventsDto>(() =>
    {
        ValidatePage(offset, limit);
        if (!EventDomains.Contains(domain))
            throw PixErrors.InvalidArguments($"domain must be one of {string.Join(", ", EventDomains)}; recorded GPU work is in gpuSubmissions and gpuHardware.");
        if (orderBy is not ("start" or "duration")) throw PixErrors.InvalidArguments("orderBy must be start or duration.");
        EventSource? only = domain == "all" ? null : EventSources.Single(s => s.Domain == domain);
        if (threadId.HasValue && only is { Thread: false })
            throw PixErrors.InvalidArguments("threadId applies to the cpu, cpuMarkers and gpuSubmissions domains (or all).");
        if ((queueId is not null || queueName is not null) && only is { Queue: false })
            throw PixErrors.InvalidArguments("queueId and queueName apply to the gpuMarkers, gpuSubmissions and gpuHardware domains (or all).");
        if (hardwareQueueId is not null && only is not null && only.Domain != "gpuHardware")
            throw PixErrors.InvalidArguments("hardwareQueueId applies to the gpuHardware domain (or all).");
        long? queue = queueId is null ? null : ParseId(queueId, nameof(queueId));
        long? hardwareQueue = hardwareQueueId is null ? null : ParseId(hardwareQueueId, nameof(hardwareQueueId));
        var (a, b, provenance) = Range(start, end, rangeMode);
        Require("Strings", "Id", "Value"); Require("Processes", "Id", "ProcessId");
        var candidates = EventSources.Where(s => only is null || s == only).Select(s => (Source: s, Present: EventSourcePresent(s))).ToList();
        if (only is not null && !candidates[0].Present)
            throw new PixToolException(PixErrors.Codes.TimingSchemaUnsupported,
                $"This timing capture does not expose {only.Table} for domain {domain}. Other domains may still be available.",
                nextCalls: [new ToolCallDto("pix_timing_overview", new { handle }, CostHints.Query), new ToolCallDto("pix_timing_events", new { handle, domain = "all" }, CostHints.Query)]);
        var included = candidates.Where(c => c.Present).Select(c => c.Source).ToList();
        (string, object?)[] parameters = [("$start", a), ("$end", b), ("$pid", processId), ("$tid", threadId), ("$queue", queue), ("$queueName", queueName), ("$hw", hardwareQueue), ("$name", nameContains)];
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        var events = new List<RecordedTimingEventDto>();
        var rowKeys = new List<long?>();
        if (included.Count > 0)
        {
            // A zero-row header arm names the columns, since only the first arm of a compound SELECT does.
            string from = "(" + EventColumnsHeader + " UNION ALL " + string.Join(" UNION ALL ", included.Select(s => s.Sql)) + ")";
            const string where = " WHERE BeginTimestamp < $end AND (EndTimestamp > $start OR (EndTimestamp=BeginTimestamp AND BeginTimestamp >= $start)) AND EndTimestamp>=BeginTimestamp" +
                " AND ($pid IS NULL OR ProcessId=$pid) AND ($tid IS NULL OR ThreadId=$tid) AND ($queue IS NULL OR QueueId=$queue) AND ($hw IS NULL OR HardwareQueueId=$hw)" +
                " AND ($queueName IS NULL OR instr(lower(COALESCE(QueueName,'')),lower($queueName))>0) AND ($name IS NULL OR instr(lower(COALESCE(Name,'')),lower($name))>0)";
            foreach ((string source, long count) in Rows("SELECT Source,COUNT(*) FROM " + from + where + " GROUP BY Source", r => (r.GetString(0), r.GetInt64(1)), parameters))
                counts[source] = count;
            string ordering = (orderBy == "duration" ? "(EndTimestamp-BeginTimestamp) DESC," : "") + "BeginTimestamp,EndTimestamp,Domain,LaneId,EventId,Level,RowKey";
            var page = Rows("SELECT Source,Domain,RowKey,EventId,Name,BeginTimestamp,EndTimestamp,Level,ProcessId,ThreadId,ThreadName,QueueId,QueueName,SubmitTimestamp,HardwareQueueId,HardwareQueueName,OverlapLevel,Color FROM " +
                from + where + " ORDER BY " + ordering + " LIMIT $limit OFFSET $offset", r =>
            {
                long begin = r.GetInt64(5), finish = r.GetInt64(6);
                long? key = r.IsDBNull(2) ? null : r.GetInt64(2);
                long? submit = r.IsDBNull(13) ? null : r.GetInt64(13);
                string rowDomain = r.GetString(1);
                var row = new RecordedTimingEventDto(Id(r, 3), rowDomain, Text(r, 4), Ns(begin), Ns(finish), Ns(finish - begin),
                    Ns(Math.Max(0, Math.Min(finish, b) - Math.Max(begin, a))), r.IsDBNull(7) ? 0 : r.GetInt32(7),
                    r.IsDBNull(8) ? null : checked((uint)r.GetInt64(8)), r.IsDBNull(9) ? null : checked((uint)r.GetInt64(9)), Text(r, 10),
                    r.IsDBNull(11) ? null : Id(r, 11), Text(r, 12),
                    Source: r.GetString(0),
                    SubmitNs: submit is long submitted ? Ns(submitted) : null,
                    SubmitLatencyNs: submit is long waited && begin >= waited ? Ns(begin - waited) : null,
                    SubmissionRef: rowDomain == "gpuSubmissions" && key is long submission ? TimingSubmissionReferences.Create(handle, generation, submission) : null,
                    HardwareQueueId: r.IsDBNull(14) ? null : Id(r, 14), HardwareQueueName: Text(r, 15),
                    OverlapLevel: r.IsDBNull(16) ? null : r.GetInt32(16), Color: r.IsDBNull(17) ? null : r.GetInt64(17));
                return (Row: row, Key: key);
            }, [.. parameters, ("$limit", limit), ("$offset", offset)]);
            foreach (var entry in page) { events.Add(entry.Row); rowKeys.Add(entry.Key); }
        }

        int[] submissions = Enumerable.Range(0, events.Count).Where(i => events[i].Domain == "gpuSubmissions" && rowKeys[i] is not null).ToArray();
        if (submissions.Length > 0 && Has("ApiQueueExecutionCommandList", "ApiQueueExecutionId", "Count"))
        {
            (string, object?)[] listParameters = submissions.Select((i, n) => ($"$s{n}", (object?)rowKeys[i])).ToArray();
            var lists = Rows("SELECT ApiQueueExecutionId,SUM(Count) FROM ApiQueueExecutionCommandList WHERE ApiQueueExecutionId IN (" + string.Join(",", listParameters.Select(p => p.Item1)) + ") GROUP BY ApiQueueExecutionId",
                r => (Id: r.GetInt64(0), Count: r.GetInt64(1)), listParameters).ToDictionary(x => x.Id, x => x.Count);
            foreach (int i in submissions)
                if (lists.TryGetValue(rowKeys[i]!.Value, out long count)) events[i] = events[i] with { CommandListCount = count };
        }

        TimingCapabilityDto executionCapability = new("not_applicable", "The selected page contains no CPU executions.");
        int[] cpu = Enumerable.Range(0, events.Count).Where(i => events[i].Domain == "cpu").ToArray();
        if (cpu.Length > 0)
        {
            if (!Has("PixCpuExecutionTimes", "Duration", "Execution", "Stall", "EventId", "BeginTimestamp", "EndTimestamp"))
            {
                executionCapability = new("unsupported", "PixCpuExecutionTimes is absent from this capture/extension schema.");
                foreach (int i in cpu) events[i] = events[i] with { ExecutionTimingState = "unsupported" };
            }
            else if (Has("PixCpuExecutionTimes", "CpuExecutionRowId") && cpu.All(i => rowKeys[i] is not null))
                executionCapability = ExecutionTimesByRowId(events, rowKeys, cpu);
            else
                executionCapability = ExecutionTimesByTuple(events, cpu);
        }

        var sources = EventSources.Where(s => only is null || s == only).Select(s => new TimingEventSourceDto(s.Domain, s.Source, s.Table,
            included.Contains(s) ? "included" : "absent", counts.GetValueOrDefault(s.Source))).ToArray();
        long total = counts.Values.Sum();
        PageResult<RecordedTimingEventDto> result = Paging.Page(events, total, offset, limit);
        return new(handle, provenance, result, executionCapability, result.NextOffset is int next ? [new("pix_timing_events", new
            { handle, domain, processId, threadId, queueId, queueName, hardwareQueueId, nameContains, startNs = Ns(a), endNs = Ns(b), orderBy, offset = next, limit })] : [], sources);
    });

    /// <summary>
    /// One exact lookup per event: PixStorage answers CpuExecutionRowId = value correctly, while joins, IN lists and
    /// VALUES lists on that hidden column return a single row (observed on 2606.18).
    /// </summary>
    private TimingCapabilityDto ExecutionTimesByRowId(List<RecordedTimingEventDto> events, List<long?> rowKeys, int[] cpu)
    {
        using SqliteCommand lookup = Command("SELECT Duration,Execution,Stall FROM PixCpuExecutionTimes WHERE CpuExecutionRowId=$row");
        SqliteParameter parameter = lookup.Parameters.Add("$row", SqliteType.Integer);
        int inconsistent = 0;
        foreach (int i in cpu)
        {
            Check();
            RecordedTimingEventDto row = events[i];
            long duration = long.Parse(row.DurationNs, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            var (state, execution, stall) = ExecutionTimeByRowId(lookup, parameter, rowKeys[i]!.Value, duration);
            if (state == "inconsistent") inconsistent++;
            events[i] = row with
            {
                ExecutionNs = state == "available" ? Ns(execution) : null,
                StallNs = state == "available" ? Ns(stall) : null,
                ExecutionTimingState = state,
                ExecutionTimingMethod = "rowId",
            };
        }
        string reason = "executionNs/stallNs come from PixCpuExecutionTimes by CpuExecutionRowId (one lookup per event) and describe the complete event, not the clipped overlap.";
        if (inconsistent > 0) reason += $" {inconsistent} event(s) had Execution + Stall different from the recorded duration and are marked inconsistent.";
        return new("available", reason);
    }

    /// <summary>Matches (EventId, BeginTimestamp, EndTimestamp) tuples; identical tuples on several lanes cannot be attributed and stay ambiguous.</summary>
    private TimingCapabilityDto ExecutionTimesByTuple(List<RecordedTimingEventDto> events, int[] cpu)
    {
        TimingCapabilityDto capability = new("available", "executionNs/stallNs match PixCpuExecutionTimes by (EventId, BeginTimestamp, EndTimestamp) and describe the complete event, not the clipped overlap; identical tuples are ambiguous.");
        bool available = true;
        var values = new Dictionary<(string id, string begin, string end), (long execution, long stall, int count, long occurrences)>();
        try
        {
            var wanted = cpu.Select(i => (events[i].EventId, events[i].BeginNs, events[i].EndNs)).Distinct().ToArray();
            var timingParameters = new List<(string, object?)>();
            var tuples = new List<string>();
            for (int n = 0; n < wanted.Length; n++)
            {
                tuples.Add($"($id{n},$begin{n},$end{n})");
                timingParameters.Add(($"$id{n}", ParseId(wanted[n].EventId, "eventId")));
                timingParameters.Add(($"$begin{n}", ParseId(wanted[n].BeginNs, "beginNs")));
                timingParameters.Add(($"$end{n}", ParseId(wanted[n].EndNs, "endNs")));
            }
            string timingSql = "WITH wanted(Id,BeginTime,EndTime) AS (VALUES " + string.Join(",", tuples) +
                "), occurrences AS (SELECT cpu.EventId,cpu.BeginTimestamp,cpu.EndTimestamp,COUNT(*) Total FROM PixCpuExecution cpu" +
                " JOIN wanted w ON w.Id=cpu.EventId AND w.BeginTime=cpu.BeginTimestamp AND w.EndTime=cpu.EndTimestamp" +
                " GROUP BY cpu.EventId,cpu.BeginTimestamp,cpu.EndTimestamp)" +
                " SELECT e.EventId,e.BeginTimestamp,e.EndTimestamp,e.Execution,e.Stall,c.Total FROM PixCpuExecutionTimes e" +
                " JOIN occurrences c ON c.EventId=e.EventId AND c.BeginTimestamp=e.BeginTimestamp AND c.EndTimestamp=e.EndTimestamp" +
                // PixStorage recognizes this explicit EventId constraint; deriving it from the join alone can enumerate
                // execution/stall timing for the entire capture.
                " WHERE e.EventId IN (" + string.Join(",", Enumerable.Range(0, wanted.Length).Select(n => $"$id{n}")) + ")";
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
            Check();
            available = false;
            capability = new("unavailable", "Execution/stall calculation failed: " + ex.Message);
        }
        foreach (int i in cpu)
        {
            RecordedTimingEventDto row = events[i];
            bool found = values.TryGetValue((row.EventId, row.BeginNs, row.EndNs), out var value);
            bool exact = available && found && value.count == 1 && value.occurrences == 1 && value.execution >= 0 && value.stall >= 0;
            events[i] = row with
            {
                ExecutionNs = exact ? Ns(value.execution) : null,
                StallNs = exact ? Ns(value.stall) : null,
                ExecutionTimingState = exact ? "available" : !available ? capability.State : found && (value.count > 1 || value.occurrences > 1) ? "ambiguous" : "unavailable",
                ExecutionTimingMethod = "tupleMatch",
            };
        }
        return capability;
    }

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

    internal TimingCounterSamplesDto CounterSamples(string handle, string counterId, long? start, long? end, int offset, int limit, string rangeMode = RangeModeFull) => Guard<TimingCounterSamplesDto>(() =>
    {
        ValidatePage(offset, limit);
        long id = ParseId(counterId, nameof(counterId));
        var (a, b, provenance) = Range(start, end, rangeMode);
        Require("PixCounters", "CounterId", "Timestamp", "Value");
        TimingCounterDto counter = CounterMetadata().FirstOrDefault(c => c.CounterId == Ns(id))
            ?? throw new PixToolException(PixErrors.Codes.TimingCounterNotFound, $"Counter {counterId} was not recorded in this capture.", nextCalls: [new("pix_timing_counters_list", new { handle })]);
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
