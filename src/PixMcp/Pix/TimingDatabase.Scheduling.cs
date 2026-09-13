namespace PixMcp.Pix;

internal sealed partial class TimingDatabase
{
    internal TimingSubmissionsDto Submissions(string handle, int generation, string? submissionRef, uint? processId,
        uint? threadId, string? queueId, long? start, long? end, int offset, int limit, string rangeMode = RangeModeFull) => Guard<TimingSubmissionsDto>(() =>
    {
        ValidatePage(offset, limit);
        if (submissionRef is not null && (processId.HasValue || threadId.HasValue || queueId is not null || start.HasValue || end.HasValue))
            throw new PixToolException(PixErrors.Codes.InvalidArguments, "Use submissionRef alone, or omit it to select process/thread/queue/time filters.");
        long? id = submissionRef is null ? null : TimingSubmissionReferences.Parse(submissionRef, handle, generation);
        long? queue = queueId is null ? null : ParseId(queueId, nameof(queueId));
        var (a, b, provenance) = Range(start, end, rangeMode);
        Require("ApiQueueExecution", "Id", "ApiCommandQueueId", "ThreadId", "SubmitTimestamp", "BeginTimestamp", "EndTimestamp");
        Require("ApiCommandQueue", "Id", "ProcessId", "NameId");
        Require("Threads", "Id", "ProcThreadId", "ProcessRowId", "ThreadNameId");
        Require("Processes", "Id", "ProcessId"); Require("Strings", "Id", "Value");
        bool threadLifetimes = Has("Threads", "StartTimestamp", "EndTimestamp");
        const string from = "ApiQueueExecution e LEFT JOIN ApiCommandQueue q ON q.Id=e.ApiCommandQueueId " +
            "LEFT JOIN Threads t ON t.Id=e.ThreadId LEFT JOIN Processes p ON p.Id=t.ProcessRowId " +
            "LEFT JOIN Processes qp ON qp.Id=q.ProcessId LEFT JOIN Strings qn ON qn.Id=q.NameId LEFT JOIN Strings tn ON tn.Id=t.ThreadNameId";
        string where = id.HasValue ? " WHERE e.Id=$id" :
            " WHERE e.SubmitTimestamp >= $start AND e.SubmitTimestamp < $end" +
            " AND ($pid IS NULL OR COALESCE(p.ProcessId,qp.ProcessId)=$pid)" +
            " AND ($tid IS NULL OR (t.ProcThreadId & 4294967295)=$tid) AND ($queue IS NULL OR e.ApiCommandQueueId=$queue)";
        (string, object?)[] parameters = [("$id", id), ("$start", a), ("$end", b), ("$pid", processId), ("$tid", threadId), ("$queue", queue)];
        long total = Count(from, where, parameters);
        if (id.HasValue && total == 0)
            throw new PixToolException(PixErrors.Codes.InvalidReference, "The referenced queue submission is absent from this timing capture.");
        var rows = Rows("SELECT e.Id,e.ApiCommandQueueId,qn.Value,e.ThreadId,p.ProcessId,t.ProcThreadId,tn.Value," +
            "e.SubmitTimestamp,e.BeginTimestamp,e.EndTimestamp,qp.ProcessId," +
            (threadLifetimes ? "t.StartTimestamp,t.EndTimestamp" : "NULL,NULL") + " FROM " + from + where +
            " ORDER BY e.SubmitTimestamp,e.Id LIMIT $limit OFFSET $offset", r =>
        {
            string reference = TimingSubmissionReferences.Create(handle, generation, r.GetInt64(0));
            uint? pid = r.IsDBNull(4) ? null : checked((uint)r.GetInt64(4));
            long? packedThread = r.IsDBNull(5) ? null : r.GetInt64(5);
            uint? tid = packedThread.HasValue ? unchecked((uint)packedThread.Value) : null;
            uint? queuePid = r.IsDBNull(10) ? null : checked((uint)r.GetInt64(10));
            bool threadMatches = pid.HasValue && packedThread.HasValue &&
                unchecked((uint)((ulong)packedThread.Value >> 32)) == pid && (!queuePid.HasValue || queuePid == pid);
            long? submit = r.IsDBNull(7) ? null : r.GetInt64(7), begin = r.IsDBNull(8) ? null : r.GetInt64(8), finish = r.IsDBNull(9) ? null : r.GetInt64(9);
            var (valid, _, reason) = SubmissionValidity(submit, begin, finish);
            string? threadRowId = r.IsDBNull(3) ? null : Id(r, 3);
            long? threadStart = r.IsDBNull(11) ? null : r.GetInt64(11), threadEnd = r.IsDBNull(12) ? null : r.GetInt64(12);
            bool safeCpuRange = threadStart is >= 0 && threadEnd > threadStart && submit >= threadStart && submit < threadEnd;
            var calls = new List<ToolCallDto> { new("pix_timing_submissions", new { handle, submissionRef = reference }) };
            if (valid && threadMatches && threadRowId is not null)
            {
                string startNs = Ns(submit!.Value), endNs = Ns(finish!.Value);
                // CPU queries take OS IDs, which can be reused while submitted GPU work is still running.
                // Keep their investigation inside this submitting thread's recorded lifetime.
                if (safeCpuRange)
                {
                    string cpuEndNs = Ns(Math.Min(finish.Value, threadEnd!.Value));
                    calls.Add(new("pix_timing_events", new { handle, domain = "cpu", processId = pid, threadId = tid, startNs, endNs = cpuEndNs }));
                    calls.Add(new("pix_timing_hotspots", new { handle, processId = pid, threadId = tid, startNs, endNs = cpuEndNs }));
                }
                calls.Add(new("pix_timing_thread_switches", new { handle, threadRowId, startNs, endNs }));
            }
            return new TimingSubmissionDto(Id(r, 0), reference, r.IsDBNull(1) ? null : Id(r, 1), Text(r, 2), threadRowId,
                pid ?? queuePid, tid, Text(r, 6), submit.HasValue ? Ns(submit.Value) : null,
                begin.HasValue ? Ns(begin.Value) : null, finish.HasValue ? Ns(finish.Value) : null,
                valid ? Ns(begin!.Value - submit!.Value) : null, valid ? Ns(finish!.Value - begin!.Value) : null,
                new(valid ? "available" : "unavailable", reason),
                new(threadMatches ? "available" : "unavailable", threadMatches ? null : "Submitting thread metadata is missing or inconsistent; the recorded thread row ID is preserved."), calls);
        }, [.. parameters, ("$limit", limit), ("$offset", offset)]);
        PageResult<TimingSubmissionDto> page = Paging.Page(rows, total, offset, limit);
        return new(handle, provenance, page, page.NextOffset is int n ? [new("pix_timing_submissions", new
            { handle, submissionRef, processId, threadId, queueId, startNs = submissionRef is null ? Ns(a) : null,
                endNs = submissionRef is null ? Ns(b) : null, offset = n, limit })] : [],
            submissionRef is null ? "submission timestamp in [start,end)" : "exact submission reference; no time filter");
    });

    /// <summary>
    /// The validity rule every submission view shares: submit >= 0, begin >= submit and end > begin. Invalid rows get a
    /// stable key (missingTimestamps, zeroDuration, inconsistentTimestamps) and a sentence.
    /// </summary>
    internal static (bool Valid, string? Key, string? Reason) SubmissionValidity(long? submit, long? begin, long? finish)
    {
        if (submit is >= 0 && begin.HasValue && finish.HasValue && begin >= submit && finish > begin) return (true, null, null);
        if (!submit.HasValue || !begin.HasValue || !finish.HasValue) return (false, "missingTimestamps", "Recorded submission or GPU timestamps are missing.");
        if (finish == begin) return (false, "zeroDuration", "The recorded GPU interval has zero duration; usable execution timing is unavailable.");
        return (false, "inconsistentTimestamps", "Recorded timestamps are inconsistent; derived latency and duration are unavailable.");
    }

    internal TimingThreadSwitchesDto ThreadSwitches(string handle, string threadRowId, long? start, long? end,
        int offset, int limit, string rangeMode = RangeModeFull) => Guard<TimingThreadSwitchesDto>(() =>
    {
        ValidatePage(offset, limit);
        long thread = ParseId(threadRowId, nameof(threadRowId));
        var (a, b, provenance) = Range(start, end, rangeMode);
        Require("Threads", "Id", "ProcThreadId", "ProcessRowId", "ThreadNameId", "SampleCount", "StartTimestamp", "EndTimestamp");
        Require("Processes", "Id", "ProcessId"); Require("Strings", "Id", "Value");
        Require("ContextSwitch", "Core", "Timestamp", "FromProcThreadId", "ToProcThreadId", "FromThreadWaitReason");
        var selected = Rows("SELECT t.ProcThreadId,p.ProcessId,s.Value,t.SampleCount,t.StartTimestamp,t.EndTimestamp " +
            "FROM Threads t JOIN Processes p ON p.Id=t.ProcessRowId LEFT JOIN Strings s ON s.Id=t.ThreadNameId WHERE t.Id=$thread", r =>
            (packed: r.GetInt64(0), pid: checked((uint)r.GetInt64(1)), name: Text(r, 2), samples: Number(r, 3),
                begin: r.IsDBNull(4) ? (long?)null : r.GetInt64(4), end: r.IsDBNull(5) ? (long?)null : r.GetInt64(5)), ("$thread", thread));
        if (selected.Count == 0) throw new PixToolException(PixErrors.Codes.InvalidReference, "threadRowId is absent from this timing capture.");
        var lane = selected[0];
        if (!lane.begin.HasValue || lane.begin < 0 || lane.end.HasValue && lane.end <= lane.begin ||
            unchecked((uint)((ulong)lane.packed >> 32)) != lane.pid)
            throw new PixToolException(PixErrors.Codes.TimingThreadLifetimeUnavailable, "The recorded thread identity/lifetime is inconsistent; scheduling events cannot be attributed safely.");
        long effectiveStart = Math.Max(a, lane.begin.Value), effectiveEnd = lane.end.HasValue ? Math.Min(b, lane.end.Value) : b;
        var info = new TimingThreadDto(threadRowId, lane.pid, unchecked((uint)lane.packed), lane.name, lane.samples);
        bool stacks = Has("Stacks", "Id", "NumFrames", "Addresses") &&
            Has("StackEvents", "OSThreadId", "StartTimestamp", "EndTimestamp", "StackEventData") && HasFunction("FindStackId", 2);
        InitializeSymbols(lane.pid, effectiveStart, Math.Max(effectiveStart, effectiveEnd));
        // Two projections retain both transitions if a native row names the selected thread on both sides.
        const string from = "(SELECT Timestamp,Core,FromProcThreadId,ToProcThreadId,FromThreadWaitReason,'switchOut' Direction " +
            "FROM ContextSwitch WHERE FromProcThreadId=$packed UNION ALL " +
            "SELECT Timestamp,Core,FromProcThreadId,ToProcThreadId,FromThreadWaitReason,'switchIn' Direction " +
            "FROM ContextSwitch WHERE ToProcThreadId=$packed)";
        const string where = " WHERE Timestamp >= $start AND Timestamp < $end";
        (string, object?)[] parameters = [("$packed", lane.packed), ("$start", effectiveStart), ("$end", effectiveEnd)];
        long total = effectiveStart < effectiveEnd ? Count(from, where, parameters) : 0;
        List<TimingThreadSwitchDto> rows = total == 0 ? [] : Rows("SELECT Timestamp,Core,FromProcThreadId,ToProcThreadId,FromThreadWaitReason,Direction" +
            (stacks && info.ThreadId != 0 ? ",CASE WHEN Direction='switchOut' THEN NULLIF(FindStackId($tid,Timestamp),0) ELSE NULL END" : ",NULL") +
            " FROM " + from + where + " ORDER BY Timestamp,Core,FromProcThreadId,ToProcThreadId,FromThreadWaitReason,Direction LIMIT $limit OFFSET $offset", r =>
        {
            long time = r.GetInt64(0); string direction = r.GetString(5);
            long peer = r.GetInt64(direction == "switchOut" ? 3 : 2);
            TimingRecordedStackDto stack;
            if (direction != "switchOut") stack = new("not_applicable", [], "not_applicable", "Switch-in rows do not carry the selected thread's switch-out stack.");
            else if (info.ThreadId == 0) stack = new("unavailable", [], "unavailable", "Idle thread ID zero is shared across cores; the stack lookup cannot attribute its data to this core safely.");
            else if (!stacks) stack = new("unsupported", [], "unavailable", "The recorded stack association schema or FindStackId function is absent.");
            else if (r.IsDBNull(6)) stack = new("missing", [], "unavailable", "No stack was recorded for this OS thread at the exact switch timestamp.");
            else
            {
                ulong[]? addresses = ReadStack(r.GetInt64(6));
                if (addresses is null || addresses.Length == 0) stack = new("invalid", [], "unavailable", "The recorded stack is absent or malformed.");
                else
                {
                    TimingFunctionDto[] frames = addresses.Select(address => ResolveFunction(lane.pid, time, address)).ToArray();
                    int resolved = frames.Count(f => f.SymbolState == "resolved");
                    stack = new("available", frames, resolved == frames.Length ? "resolved" : resolved == 0 ? "unresolved" : "partial");
                }
            }
            return new TimingThreadSwitchDto(Ns(time), direction, r.GetInt32(1), unchecked((uint)((ulong)peer >> 32)),
                unchecked((uint)peer), direction == "switchOut" && !r.IsDBNull(4) ? r.GetInt32(4) : null, stack);
        }, [.. parameters, ("$tid", info.ThreadId), ("$limit", limit), ("$offset", offset)]);
        PageResult<TimingThreadSwitchDto> page = Paging.Page(rows, total, offset, limit);
        return new(handle, provenance, info, Ns(lane.begin.Value), lane.end.HasValue ? Ns(lane.end.Value) : null, page,
            page.NextOffset is int n ? [new("pix_timing_thread_switches", new { handle, threadRowId, startNs = Ns(a), endNs = Ns(b), offset = n, limit })] : []);
    });
}
