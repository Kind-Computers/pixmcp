using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PixMcp.Pix;

internal sealed partial class TimingDatabase
{
    internal const int MarkerTreeMaxRows = 200_000;
    internal const int ExecutionLookupBudget = 2_000;

    internal static readonly RecordedDenominatorsDto MarkerTreeDenominators = new(
        "time / lane span (first to last top-level event, clipped to the window)",
        "time / sum of top-level event time in the window (lane.totals.sumOfIntervals)",
        "busy / selected window length (endNs - startNs)",
        "union of top-level events clipped to the window")
    { PercentOfParent = "time / the parent path's inclusive time" };

    internal static string NormalizeTreeSort(string? sortBy)
    {
        string value = string.IsNullOrWhiteSpace(sortBy) ? "inclusive" : sortBy.Trim();
        return RecordedMarkerTree.SortKeys.FirstOrDefault(k => k.Equals(value, StringComparison.OrdinalIgnoreCase))
            ?? throw PixErrors.InvalidArguments($"sortBy must be one of {string.Join(", ", RecordedMarkerTree.SortKeys)} (got '{sortBy}').");
    }

    /// <summary>One exact CpuExecutionRowId lookup: available when a single row's Execution + Stall equals both the recorded and the event duration.</summary>
    private static (string State, long Execution, long Stall) ExecutionTimeByRowId(SqliteCommand lookup, SqliteParameter parameter, long rowId, long duration)
    {
        parameter.Value = rowId;
        using SqliteDataReader reader = lookup.ExecuteReader();
        if (!reader.Read()) return ("unavailable", 0, 0);
        long recorded = Number(reader, 0), execution = Number(reader, 1), stall = Number(reader, 2);
        bool duplicate = reader.Read();
        bool consistent = !duplicate && execution >= 0 && stall >= 0 && execution + stall == recorded && recorded == duration;
        return (consistent ? "available" : duplicate ? "ambiguous" : "inconsistent", execution, stall);
    }

    /// <summary>
    /// A lane's recorded PIX events (a thread's CPU events or an API queue's GPU-side events) overlapping [start, end),
    /// nested by Level and interval, aggregated by marker path and listed in pre-order below <paramref name="parentPath"/>.
    /// </summary>
    internal RecordedMarkerTreeDto MarkerTree(string handle, string? threadRowId, string? queueId, string? parentPath, int depth, string sortBy, long? minSelfNs,
        long? start, long? end, int offset, int limit, bool includeExecution, string rangeMode = RangeModeFull) => Guard<RecordedMarkerTreeDto>(() =>
    {
        ValidatePage(offset, limit);
        if ((threadRowId is null) == (queueId is null))
            throw PixErrors.InvalidArguments("Pass exactly one lane: threadRowId (a thread's PIX CPU events) or queueId (an API command queue's GPU-side PIX events).");
        if (depth is < 1 or > 8) throw PixErrors.InvalidArguments("depth must be between 1 and 8.");
        if (minSelfNs is < 0) throw PixErrors.InvalidArguments("minSelfNs must be nonnegative.");
        string sort = NormalizeTreeSort(sortBy);
        bool cpu = threadRowId is not null;
        long laneId = ParseId(cpu ? threadRowId! : queueId!, cpu ? nameof(threadRowId) : nameof(queueId));
        var (a, b, provenance) = Range(start, end, rangeMode);
        Require("PixEventInfo", "Id", "NameId"); Require("Strings", "Id", "Value");

        uint? lanePid = null, laneTid = null;
        string? laneName;
        if (cpu)
        {
            Require("PixCpuExecution", "BeginTimestamp", "EndTimestamp", "Level", "ThreadRowId", "EventId");
            Require("Threads", "Id", "ProcThreadId");
            bool names = Has("Threads", "ThreadNameId");
            var found = Rows("SELECT t.ProcThreadId," + (names ? "s.Value" : "NULL") + " FROM Threads t" + (names ? " LEFT JOIN Strings s ON s.Id=t.ThreadNameId" : "") + " WHERE t.Id=$lane",
                r => (Packed: OptionalInt64(r, 0), Name: Text(r, 1)), ("$lane", laneId));
            if (found.Count == 0)
                throw new PixToolException(PixErrors.Codes.InvalidReference, "threadRowId is absent from this timing capture.", nextCalls: [new("pix_timing_overview", new { handle })]);
            if (found[0].Packed is long packed)
            {
                lanePid = unchecked((uint)((ulong)packed >> 32));
                laneTid = unchecked((uint)packed);
            }
            laneName = found[0].Name;
        }
        else
        {
            Require("PixGpuExecution", "BeginTimestamp", "EndTimestamp", "Level", "ApiCommandQueueId", "EventId");
            Require("ApiCommandQueue", "Id", "ProcessId", "NameId"); Require("Processes", "Id", "ProcessId");
            var found = Rows("SELECT p.ProcessId,n.Value FROM ApiCommandQueue q LEFT JOIN Processes p ON p.Id=q.ProcessId LEFT JOIN Strings n ON n.Id=q.NameId WHERE q.Id=$lane",
                r => (Pid: OptionalInt64(r, 0), Name: Text(r, 1)), ("$lane", laneId));
            if (found.Count == 0)
                throw new PixToolException(PixErrors.Codes.InvalidReference, "queueId is absent from this timing capture.", nextCalls: [new("pix_timing_overview", new { handle })]);
            lanePid = found[0].Pid is long pid and >= 0 and <= 4294967295L ? (uint)pid : null;
            laneName = found[0].Name;
        }

        string table = cpu ? "PixCpuExecution" : "PixGpuExecution", laneColumn = cpu ? "ThreadRowId" : "ApiCommandQueueId";
        var rows = Rows($"SELECT e.rowid,e.BeginTimestamp,e.EndTimestamp,e.Level,e.EventId,s.Value FROM {table} e" +
            " LEFT JOIN PixEventInfo i ON i.Id=e.EventId LEFT JOIN Strings s ON s.Id=i.NameId" +
            $" WHERE e.{laneColumn}=$lane AND e.BeginTimestamp < $end AND e.EndTimestamp > $start ORDER BY e.BeginTimestamp,e.Level LIMIT $cap",
            r => (RowId: r.GetInt64(0), Begin: r.GetInt64(1), End: r.GetInt64(2), Level: r.IsDBNull(3) ? 0 : r.GetInt32(3), EventId: OptionalInt64(r, 4), Name: Text(r, 5)),
            ("$lane", laneId), ("$start", a), ("$end", b), ("$cap", MarkerTreeMaxRows + 1));
        bool truncated = rows.Count > MarkerTreeMaxRows;
        long clipEnd = b;
        if (truncated)
        {
            long firstUnread = rows[MarkerTreeMaxRows].Begin;
            rows.RemoveAt(MarkerTreeMaxRows);
            if (firstUnread > a) clipEnd = firstUnread;
        }

        var executions = new long?[rows.Count];
        var stalls = new long?[rows.Count];
        TimingCapabilityDto execution = new("notApplicable", "GPU-side PIX events have no execution/stall split.");
        if (cpu)
        {
            if (!includeExecution) execution = new("skipped", "includeExecution=false.");
            else if (!Has("PixCpuExecutionTimes", "CpuExecutionRowId", "Duration", "Execution", "Stall"))
                execution = new("unsupported", "PixCpuExecutionTimes.CpuExecutionRowId is absent; per-path execution and stall need the exact row lookup.");
            else if (rows.Count > ExecutionLookupBudget)
                execution = new("skipped", $"{rows.Count} events exceed the {ExecutionLookupBudget} execution lookups one call performs; narrow startNs/endNs for execution and stall.");
            else
            {
                using SqliteCommand lookup = Command("SELECT Duration,Execution,Stall FROM PixCpuExecutionTimes WHERE CpuExecutionRowId=$row");
                SqliteParameter parameter = lookup.Parameters.Add("$row", SqliteType.Integer);
                long inconsistent = 0;
                for (int i = 0; i < rows.Count; i++)
                {
                    Check();
                    var (state, executionNs, stallNs) = ExecutionTimeByRowId(lookup, parameter, rows[i].RowId, rows[i].End - rows[i].Begin);
                    if (state == "available")
                    {
                        executions[i] = executionNs;
                        stalls[i] = stallNs;
                    }
                    else if (state is "inconsistent" or "ambiguous") inconsistent++;
                }
                execution = new("available", "executionNs/stallNs sum PixCpuExecutionTimes over complete occurrences (CpuExecutionRowId lookups); inclusive and self are clipped to the window." +
                    (inconsistent > 0 ? $" {inconsistent} occurrence(s) had inconsistent or duplicate execution rows and are excluded." : ""));
            }
        }

        var markerRows = new RecordedMarkerRow[rows.Count];
        for (int i = 0; i < rows.Count; i++)
            markerRows[i] = new(rows[i].RowId, rows[i].Begin, rows[i].End, rows[i].Level,
                rows[i].Name ?? "(event " + (rows[i].EventId?.ToString(CultureInfo.InvariantCulture) ?? "?") + ")", executions[i], stalls[i]);
        RecordedMarkerTree tree = RecordedMarkerTree.Build(markerRows, a, clipEnd);
        RecordedLaneTotalsDto? totals = RecordedTiming.Totals(tree.RootIntervals, a, clipEnd);
        long span = (long)(totals?.Span.Ns ?? 0), sumOfRoots = (long)(totals?.SumOfIntervals.Ns ?? 0);

        string? startArgument = start.HasValue ? Ns(a) : null, endArgument = end.HasValue ? Ns(b) : null;
        RecordedMarkerTree.PathNode parent = tree.Root;
        if (!string.IsNullOrEmpty(parentPath))
            parent = tree.Find(parentPath) ?? throw new PixToolException(PixErrors.Codes.InvalidReference,
                $"parentPath '{parentPath}' has no recorded occurrence on this lane in the window.",
                nextCalls: [new("pix_timing_tree", new { handle, threadRowId, queueId, depth = 1, startNs = startArgument, endNs = endArgument, rangeMode })]);

        List<(RecordedMarkerTree.PathNode Node, int Rank)> listed = tree.Flatten(parent, depth, sort, minSelfNs ?? 0);
        RecordedTreeNodeDto[] items = listed.Skip(offset).Take(limit).Select(x =>
        {
            RecordedMarkerTree.PathNode node = x.Node;
            long? parentInclusive = node.Parent?.InclusiveNs;
            string timing = !cpu ? "notApplicable" : execution.State == "available" ? node.ExecutionTiming : execution.State;
            return new RecordedTreeNodeDto(node.Path, node.Name, node.Depth, node.Occurrences,
                RecordedTiming.Duration(node.InclusiveNs, span, sumOfRoots, parentInclusive, x.Rank), RecordedTiming.Duration(node.SelfNs, span, sumOfRoots, parentInclusive),
                node.ChildSumNs, node.ChildrenExceedMeasured, node.ChildOverflowNs, RecordedTiming.Stats(node.Durations)!,
                node.TimedOccurrences > 0 ? node.ExecutionNs : null, node.TimedOccurrences > 0 ? node.StallNs : null, timing,
                node.Children.Count, Ns(node.FirstStart), Ns(node.SlowestBegin), Ns(node.SlowestEnd));
        }).ToArray();
        PageResult<RecordedTreeNodeDto> page = Paging.Page(items, listed.Count, offset, limit);

        var calls = new List<ToolCallDto>();
        if (page.NextOffset is int next)
            calls.Add(new("pix_timing_tree", new { handle, threadRowId, queueId, parentPath, depth, sortBy = sort, minSelfNs, startNs = startArgument, endNs = endArgument, rangeMode, offset = next, limit }, CostHints.Query));
        foreach (RecordedTreeNodeDto node in items.Where(n => n.ChildPaths > 0 && n.Depth == parent.Depth + depth).Take(3))
            calls.Add(new("pix_timing_tree", new { handle, threadRowId, queueId, parentPath = node.Path, depth, sortBy = sort, startNs = startArgument, endNs = endArgument, rangeMode }, CostHints.Query));
        if (items.FirstOrDefault() is { } top && top.OccurrenceDuration.MaxNs > 0)
        {
            if (cpu)
            {
                calls.Add(new("pix_timing_hotspots", new { handle, processId = lanePid, threadId = laneTid, startNs = top.SlowestStartNs, endNs = top.SlowestEndNs }, CostHints.Query));
                calls.Add(new("pix_timing_thread_switches", new { handle, threadRowId, startNs = top.SlowestStartNs, endNs = top.SlowestEndNs }, CostHints.Query));
            }
            else calls.Add(new("pix_timing_submissions", new { handle, queueId, startNs = top.SlowestStartNs, endNs = top.SlowestEndNs }, CostHints.Query));
        }
        if (truncated && clipEnd < b)
            calls.Add(new("pix_timing_tree", new { handle, threadRowId, queueId, parentPath, depth, sortBy = sort, startNs = Ns(clipEnd), endNs = Ns(b), rangeMode }, CostHints.Query));
        if (rows.Count == 0 && !cpu)
            calls.Add(new("pix_timing_gpu_summary", new { handle, queueId, startNs = startArgument, endNs = endArgument, rangeMode }, CostHints.Query));

        var notes = new List<string>
        {
            "Each path aggregates every occurrence: inclusive and self are clipped to the window, occurrenceDuration" + (cpu ? ", executionNs and stallNs describe" : " describes") + " complete occurrences.",
            "Nesting follows the recorded Level and intervals; a child extending past its parent sets childrenExceedMeasured and childOverflowNs.",
        };
        if (rows.Count == 0)
            notes.Add(cpu ? "This thread recorded no PIX CPU events in the window." : "This queue recorded no GPU-side PIX events in the window; pix_timing_gpu_summary describes its submissions.");
        if (truncated) notes.Add($"The lane has more than {MarkerTreeMaxRows} events in the window; only events before truncatedAtNs were aggregated.");

        return new RecordedMarkerTreeDto(handle, provenance, cpu ? "cpu" : "gpuMarkers",
            new RecordedLaneDto(cpu ? "thread" : "queue", Ns(laneId), laneName, lanePid, laneTid, totals, tree.Rows, tree.RootIntervals.Count, tree.Orphans, tree.MalformedNestings),
            string.IsNullOrEmpty(parentPath) ? null : parentPath, depth, sort, minSelfNs, page, truncated, truncated && clipEnd < b ? Ns(clipEnd) : null,
            execution, MarkerTreeDenominators, "Sum siblings, never ancestors: a path's inclusive time already contains its children's.", notes, calls);
    });
}
