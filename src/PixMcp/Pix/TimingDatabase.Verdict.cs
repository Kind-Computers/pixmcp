using System.Globalization;
using Microsoft.Data.Sqlite;
using PixMcp.Pix.Sql;

namespace PixMcp.Pix;

internal sealed partial class TimingDatabase
{
    public static readonly string[] FrameSources = ["auto", "present", "cpuMarker", "vsync", "submission"];
    private static readonly string[] AutoFrameSources = ["present", "cpuMarker", "vsync", "submission"];

    private readonly record struct VerdictFrame(long Start, long End, long? PresentToVsync);

    internal static string NormalizeFrameSource(string? frameSource)
    {
        string value = string.IsNullOrWhiteSpace(frameSource) ? "auto" : frameSource.Trim();
        return FrameSources.FirstOrDefault(s => s.Equals(value, StringComparison.OrdinalIgnoreCase))
            ?? throw PixErrors.InvalidArguments($"frameSource must be one of {string.Join(", ", FrameSources)} (got '{frameSource}').");
    }

    private static double PercentOf(long part, long whole) => whole <= 0 ? 0 : Math.Round(100.0 * part / whole, 2);

    /// <summary>
    /// Recorded frames (GpuFrame presents, the render thread's repeated top-level PIX event, VSync or submission cadence) with
    /// GPU busy time and the render thread's on-CPU, blocked and ready time per frame, classified by <see cref="VerdictRules"/>.
    /// </summary>
    internal TimingVerdictDto Verdict(string handle, uint? processId, string frameSource, string? frameMarkerName, string? renderThreadRowId,
        long? start, long? end, int maxFrames, int offset, int limit, string rangeMode = RangeModeFull) => Guard<TimingVerdictDto>(() =>
    {
        ValidatePage(offset, limit);
        if (maxFrames is < 2 or > 5000) throw PixErrors.InvalidArguments("maxFrames must be between 2 and 5000.");
        string requested = NormalizeFrameSource(frameSource);
        if (frameMarkerName is not null && requested is not ("auto" or "cpuMarker"))
            throw PixErrors.InvalidArguments("frameMarkerName applies only to frameSource cpuMarker or auto.");
        var (a, b, provenance) = Range(start, end, rangeMode);
        uint? pid = processId ?? CaptureTargetProcessId();
        Require("Threads", "Id", "ProcThreadId");
        bool submissionsRecorded = Has("ApiQueueExecution", "ApiCommandQueueId", "ThreadId", "SubmitTimestamp", "BeginTimestamp", "EndTimestamp")
            && Has("ApiCommandQueue", "Id", "ProcessId") && Has("Processes", "Id", "ProcessId");
        const string processSubmissions = " FROM ApiQueueExecution e JOIN ApiCommandQueue q ON q.Id=e.ApiCommandQueueId JOIN Processes p ON p.Id=q.ProcessId";

        long threadRow;
        string threadSelection;
        if (renderThreadRowId is not null)
        {
            threadRow = ParseId(renderThreadRowId, nameof(renderThreadRowId));
            threadSelection = "explicit";
        }
        else
        {
            var chosenThread = new List<long>();
            threadSelection = "mostSubmissions";
            if (submissionsRecorded)
                chosenThread = Rows("SELECT e.ThreadId" + processSubmissions + " WHERE e.ThreadId IS NOT NULL AND e.SubmitTimestamp >= $start AND e.SubmitTimestamp < $end" +
                    " AND ($pid IS NULL OR p.ProcessId=$pid) GROUP BY e.ThreadId ORDER BY count(*) DESC, e.ThreadId LIMIT 1",
                    r => r.GetInt64(0), ("$start", a), ("$end", b), ("$pid", (object?)pid));
            if (chosenThread.Count == 0 && Has("Threads", "PixEventCount", "ProcessRowId") && Has("Processes", "Id", "ProcessId"))
            {
                chosenThread = Rows("SELECT t.Id FROM Threads t JOIN Processes p ON p.Id=t.ProcessRowId WHERE t.PixEventCount > 0 AND ($pid IS NULL OR p.ProcessId=$pid)" +
                    " ORDER BY t.PixEventCount DESC, t.Id LIMIT 1", r => r.GetInt64(0), ("$pid", (object?)pid));
                threadSelection = "mostPixEvents";
            }
            if (chosenThread.Count == 0)
                throw new PixToolException(PixErrors.Codes.TimingSchemaUnsupported,
                    "No render thread can be chosen: the process recorded neither queue submissions nor PIX CPU events in the window. Pass renderThreadRowId.",
                    nextCalls: [new("pix_timing_overview", new { handle, processId = pid, rangeMode })]);
            threadRow = chosenThread[0];
        }

        bool threadNames = Has("Threads", "ThreadNameId"), lifetimes = Has("Threads", "EndTimestamp");
        var thread = Rows("SELECT t.ProcThreadId," + (threadNames ? "s.Value" : "NULL") + "," + (lifetimes ? "t.EndTimestamp" : "NULL") + " FROM Threads t" +
            (threadNames ? " LEFT JOIN Strings s ON s.Id=t.ThreadNameId" : "") + " WHERE t.Id=$thread",
            r => (Packed: OptionalInt64(r, 0), Name: Text(r, 1), End: OptionalInt64(r, 2)), ("$thread", threadRow));
        if (thread.Count == 0 || thread[0].Packed is not long packed)
            throw new PixToolException(PixErrors.Codes.InvalidReference, "renderThreadRowId is absent from this timing capture.",
                nextCalls: [new("pix_timing_overview", new { handle, processId = pid })]);
        uint renderPid = unchecked((uint)((ulong)packed >> 32)), renderTid = unchecked((uint)packed);
        long renderSubmissions = submissionsRecorded
            ? Count("ApiQueueExecution", " WHERE ThreadId=$thread AND SubmitTimestamp >= $start AND SubmitTimestamp < $end", ("$thread", threadRow), ("$start", a), ("$end", b))
            : 0;

        var frames = new List<VerdictFrame>();
        string? detail = null;
        long available = 0;

        bool LoadPresent()
        {
            if (!Has("GpuFrame", "PresentCallTime", "VSyncTime")) return false;
            const string where = " WHERE PresentCallTime IS NOT NULL AND PresentCallTime >= $start AND PresentCallTime < $end";
            var rows = Rows("SELECT PresentCallTime,VSyncTime FROM GpuFrame" + where + " ORDER BY PresentCallTime LIMIT $cap",
                r => (Present: r.GetInt64(0), Vsync: OptionalInt64(r, 1)), ("$start", a), ("$end", b), ("$cap", maxFrames + 1));
            for (int i = 0; i + 1 < rows.Count; i++)
                if (rows[i + 1].Present > rows[i].Present)
                    frames.Add(new(rows[i].Present, rows[i + 1].Present, rows[i].Vsync is long vsync && vsync >= rows[i].Present ? vsync - rows[i].Present : null));
            if (frames.Count == 0) return false;
            available = Count("GpuFrame", where, ("$start", a), ("$end", b)) - 1;
            detail = "GpuFrame present calls";
            return true;
        }

        bool LoadCpuMarker()
        {
            if (!Has("PixCpuExecution", "BeginTimestamp", "EndTimestamp", "Level", "ThreadRowId", "EventId") || !Has("PixEventInfo", "Id", "NameId") || !Has("Strings", "Id", "Value"))
                return false;
            var parameters = new List<(string, object?)> { ("$thread", threadRow), ("$start", a), ("$end", b) };
            if (frameMarkerName is not null) parameters.Add(("$name", frameMarkerName));
            // Without a name: the most repeated event at the shallowest level that repeats at all.
            var candidates = Rows("SELECT e.EventId,e.Level,s.Value,count(*) FROM PixCpuExecution e LEFT JOIN PixEventInfo i ON i.Id=e.EventId LEFT JOIN Strings s ON s.Id=i.NameId" +
                " WHERE e.ThreadRowId=$thread AND e.BeginTimestamp >= $start AND e.BeginTimestamp < $end" + (frameMarkerName is null ? "" : " AND s.Value=$name") +
                " GROUP BY e.EventId,e.Level ORDER BY " + (frameMarkerName is null ? "e.Level,count(*) DESC" : "count(*) DESC,e.Level") + ",e.EventId",
                r => (Event: r.GetInt64(0), Level: r.GetInt64(1), Name: Text(r, 2), Count: r.GetInt64(3)), [.. parameters]);
            var marker = candidates.FirstOrDefault(c => c.Count >= 2);
            if (marker.Count < 2) return false;
            var rows = Rows("SELECT BeginTimestamp,EndTimestamp FROM PixCpuExecution WHERE ThreadRowId=$thread AND EventId=$event AND Level=$level" +
                " AND BeginTimestamp >= $start AND BeginTimestamp < $end ORDER BY BeginTimestamp LIMIT $cap",
                r => (Begin: r.GetInt64(0), End: r.GetInt64(1)), ("$thread", threadRow), ("$event", marker.Event), ("$level", marker.Level), ("$start", a), ("$end", b), ("$cap", maxFrames + 1));
            for (int i = 0; i < rows.Count && frames.Count < maxFrames; i++)
            {
                long frameEnd = i + 1 < rows.Count ? rows[i + 1].Begin : rows[i].End;
                if (frameEnd > rows[i].Begin) frames.Add(new(rows[i].Begin, frameEnd, null));
            }
            if (frames.Count == 0) return false;
            available = marker.Count;
            detail = (marker.Name ?? "(event " + Ns(marker.Event) + ")") + " (level " + Ns(marker.Level) + ")";
            return true;
        }

        bool LoadVsync()
        {
            if (!Has("CustomMarker", "MarkerInfoId", "Timestamp") || !Has("CustomMarkerInfo", "Id", "NameId") || !Has("Strings", "Id", "Value")) return false;
            var lanes = Rows("SELECT m.MarkerInfoId,count(*) FROM CustomMarker m JOIN CustomMarkerInfo i ON i.Id=m.MarkerInfoId JOIN Strings n ON n.Id=i.NameId" +
                " WHERE n.Value='VSync' AND m.Timestamp >= $start AND m.Timestamp < $end GROUP BY m.MarkerInfoId ORDER BY count(*) DESC, m.MarkerInfoId LIMIT 1",
                r => (Lane: r.GetInt64(0), Count: r.GetInt64(1)), ("$start", a), ("$end", b));
            if (lanes.Count == 0 || lanes[0].Count < 2) return false;
            var ticks = Rows("SELECT Timestamp FROM CustomMarker WHERE MarkerInfoId=$lane AND Timestamp >= $start AND Timestamp < $end ORDER BY Timestamp LIMIT $cap",
                r => r.GetInt64(0), ("$lane", lanes[0].Lane), ("$start", a), ("$end", b), ("$cap", maxFrames + 1));
            for (int i = 0; i + 1 < ticks.Count; i++)
                if (ticks[i + 1] > ticks[i]) frames.Add(new(ticks[i], ticks[i + 1], null));
            if (frames.Count == 0) return false;
            available = lanes[0].Count - 1;
            detail = "VSync lane " + Ns(lanes[0].Lane);
            return true;
        }

        bool LoadSubmission()
        {
            if (!submissionsRecorded) return false;
            var queues = Rows("SELECT ApiCommandQueueId,count(*) FROM ApiQueueExecution WHERE ThreadId=$thread AND SubmitTimestamp >= $start AND SubmitTimestamp < $end" +
                " GROUP BY ApiCommandQueueId ORDER BY count(*) DESC, ApiCommandQueueId LIMIT 1", r => (Queue: r.GetInt64(0), Count: r.GetInt64(1)), ("$thread", threadRow), ("$start", a), ("$end", b));
            if (queues.Count == 0 || queues[0].Count < 2) return false;
            var submits = Rows("SELECT SubmitTimestamp FROM ApiQueueExecution WHERE ThreadId=$thread AND ApiCommandQueueId=$queue AND SubmitTimestamp >= $start AND SubmitTimestamp < $end" +
                " ORDER BY SubmitTimestamp LIMIT $cap", r => r.GetInt64(0), ("$thread", threadRow), ("$queue", queues[0].Queue), ("$start", a), ("$end", b), ("$cap", maxFrames + 1));
            for (int i = 0; i + 1 < submits.Count; i++)
                if (submits[i + 1] > submits[i]) frames.Add(new(submits[i], submits[i + 1], null));
            if (frames.Count == 0) return false;
            available = queues[0].Count - 1;
            detail = "render-thread submissions to queue " + Ns(queues[0].Queue);
            return true;
        }

        bool Load(string source) => source switch
        {
            "present" => LoadPresent(),
            "cpuMarker" => LoadCpuMarker(),
            "vsync" => LoadVsync(),
            _ => LoadSubmission(),
        };

        string chosenSource = requested;
        bool loaded = false;
        if (requested != "auto") loaded = Load(requested);
        else
            foreach (string candidate in AutoFrameSources)
                if (Load(candidate))
                {
                    chosenSource = candidate;
                    loaded = true;
                    break;
                }
        if (!loaded)
            throw new PixToolException(PixErrors.Codes.TimingSchemaUnsupported,
                requested == "auto"
                    ? "No frame source has two frames in the window: GpuFrame presents, repeated PIX events on the render thread, VSync markers and render-thread submissions are all absent."
                    : $"frameSource {requested} has fewer than two frames in the window for this render thread.",
                nextCalls: requested == "auto"
                    ? new ToolCallDto[] { new("pix_timing_gpu_summary", new { handle, processId = pid, rangeMode }) }
                    : new ToolCallDto[] { new("pix_timing_verdict", new { handle, processId, frameSource = "auto", renderThreadRowId, rangeMode }) });

        long fs = frames[0].Start, fe = frames[^1].End;
        bool truncated = available > frames.Count;

        var gpuIntervals = new List<(long Start, long End)>();
        var queueIntervals = new Dictionary<long, List<(long Start, long End)>>();
        var renderSubmits = new List<(long Submit, long? Latency)>();
        if (submissionsRecorded)
        {
            foreach (var s in Rows("SELECT e.ApiCommandQueueId,e.SubmitTimestamp,e.BeginTimestamp,e.EndTimestamp" + processSubmissions +
                " WHERE e.BeginTimestamp < $fe AND e.EndTimestamp > $fs AND ($pid IS NULL OR p.ProcessId=$pid)",
                r => (Queue: r.GetInt64(0), Submit: OptionalInt64(r, 1), Begin: OptionalInt64(r, 2), End: OptionalInt64(r, 3)), ("$fs", fs), ("$fe", fe), ("$pid", (object?)pid)))
            {
                if (!SubmissionValidity(s.Submit, s.Begin, s.End).Valid) continue;
                gpuIntervals.Add((s.Begin!.Value, s.End!.Value));
                if (!queueIntervals.TryGetValue(s.Queue, out List<(long Start, long End)>? list)) queueIntervals[s.Queue] = list = [];
                list.Add((s.Begin.Value, s.End.Value));
            }
            foreach (var s in Rows("SELECT SubmitTimestamp,BeginTimestamp,EndTimestamp FROM ApiQueueExecution WHERE ThreadId=$thread AND SubmitTimestamp >= $fs AND SubmitTimestamp < $fe ORDER BY SubmitTimestamp",
                r => (Submit: r.GetInt64(0), Begin: OptionalInt64(r, 1), End: OptionalInt64(r, 2)), ("$thread", threadRow), ("$fs", fs), ("$fe", fe)))
                renderSubmits.Add((s.Submit, SubmissionValidity(s.Submit, s.Begin, s.End).Valid ? s.Begin!.Value - s.Submit : null));
        }

        var readyLatencies = new List<long>();
        RecordedThreadStates? states = ThreadStates(packed, fs, fe, thread[0].End, out long switchEvents, readyLatencies);

        List<(long Start, long End)> gpuMerged = RecordedTiming.Merge(gpuIntervals);
        Dictionary<long, List<(long Start, long End)>> queueMerged = queueIntervals.ToDictionary(kv => kv.Key, kv => RecordedTiming.Merge(kv.Value));
        var queueBusy = queueMerged.Keys.ToDictionary(k => k, _ => 0L);
        var perFrame = new List<TimingFrameVerdictDto>(frames.Count);
        var verdictCounts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var reasons = new Dictionary<(RecordedThreadState State, int Reason), (long Ns, long Count)>();
        long totalFrame = 0, totalGpu = 0, totalOn = 0, totalBlocked = 0, totalReady = 0;
        int nextSubmit = 0;
        for (int k = 0; k < frames.Count; k++)
        {
            Check();
            VerdictFrame frame = frames[k];
            long length = frame.End - frame.Start;
            long gpuNs = RecordedTiming.CoveredLength(gpuMerged, frame.Start, frame.End);
            foreach (var (queue, merged) in queueMerged) queueBusy[queue] += RecordedTiming.CoveredLength(merged, frame.Start, frame.End);
            RecordedStateTotals? state = states?.Integrate(frame.Start, frame.End);
            long onCpu = state?.OnCpuNs ?? 0, blocked = state?.BlockedNs ?? 0, ready = state?.ReadyNs ?? 0, unknown = Math.Max(0, length - onCpu - blocked - ready);
            if (state is not null)
                foreach (var (key, value) in state.ByReason)
                {
                    reasons.TryGetValue(key, out var sum);
                    reasons[key] = (sum.Ns + value.Ns, sum.Count + value.Count);
                }
            while (nextSubmit < renderSubmits.Count && renderSubmits[nextSubmit].Submit < frame.Start) nextSubmit++;
            int submits = 0;
            long? firstOffset = null, maxLatency = null;
            for (int i = nextSubmit; i < renderSubmits.Count && renderSubmits[i].Submit < frame.End; i++)
            {
                submits++;
                firstOffset ??= renderSubmits[i].Submit - frame.Start;
                if (renderSubmits[i].Latency is long latency && latency > (maxLatency ?? -1)) maxLatency = latency;
            }
            double gpuPercent = PercentOf(gpuNs, length), onPercent = PercentOf(onCpu, length), blockedPercent = PercentOf(blocked, length),
                readyPercent = PercentOf(ready, length), unknownPercent = PercentOf(unknown, length);
            string verdict = VerdictRules.Classify(gpuPercent, onPercent, blockedPercent, readyPercent, unknownPercent,
                frame.PresentToVsync is long toVsync ? PercentOf(toVsync, length) : null);
            verdictCounts[verdict] = verdictCounts.GetValueOrDefault(verdict) + 1;
            perFrame.Add(new TimingFrameVerdictDto(k, Ns(frame.Start), length, RecordedTiming.Ms(length), gpuPercent,
                states is null ? null : onPercent, states is null ? null : blockedPercent, states is null ? null : readyPercent, unknownPercent,
                submits, firstOffset is long first ? RecordedTiming.Ms(first) : null, maxLatency is long worst ? RecordedTiming.Ms(worst) : null,
                frame.PresentToVsync is long presentToVsync ? RecordedTiming.Ms(presentToVsync) : null, verdict));
            totalFrame += length;
            totalGpu += gpuNs;
            totalOn += onCpu;
            totalBlocked += blocked;
            totalReady += ready;
        }

        double? Share(long part) => totalFrame > 0 ? Math.Round(100.0 * part / totalFrame, 2) : null;
        long totalUnknown = Math.Max(0, totalFrame - totalOn - totalBlocked - totalReady);
        var (dominant, dominantPercent, confidence) = VerdictRules.Summarize(verdictCounts, frames.Count, Share(totalUnknown) ?? 100);
        var summary = new TimingVerdictSummaryDto(dominant, dominantPercent, confidence, verdictCounts, Share(totalGpu),
            states is null ? null : Share(totalOn), states is null ? null : Share(totalBlocked), states is null ? null : Share(totalReady), Share(totalUnknown),
            RecordedTiming.Stats(frames.Select(f => f.End - f.Start)), RecordedTiming.Stats(readyLatencies), VerdictRules.Implication(dominant));

        TimingWaitReasonDto[] waits = reasons.OrderByDescending(r => r.Value.Ns).ThenBy(r => r.Key.State).ThenBy(r => r.Key.Reason).Take(6)
            .Select(r => new TimingWaitReasonDto(r.Key.State == RecordedThreadState.Blocked ? "blocked" : "readyNotRunning",
                r.Key.Reason < 0 ? null : r.Key.Reason, r.Key.Reason < 0 ? null : WaitReasons.ProbableName(r.Key.Reason),
                r.Value.Ns, RecordedTiming.Ms(r.Value.Ns), Share(r.Value.Ns), r.Value.Count)).ToArray();

        var queueNames = new Dictionary<long, string?>();
        if (queueBusy.Count > 0 && Has("ApiCommandQueue", "NameId") && Has("Strings", "Id", "Value"))
            foreach (var q in Rows("SELECT q.Id,n.Value FROM ApiCommandQueue q LEFT JOIN Strings n ON n.Id=q.NameId WHERE q.Id IN (" + string.Join(",", queueBusy.Keys.Select(Ns)) + ")",
                r => (Id: r.GetInt64(0), Name: Text(r, 1))))
                queueNames[q.Id] = q.Name;
        TimingQueueShareDto[] queues = queueBusy.OrderByDescending(q => q.Value).ThenBy(q => q.Key)
            .Select(q => new TimingQueueShareDto(Ns(q.Key), queueNames.GetValueOrDefault(q.Key), Share(q.Value))).ToArray();

        var calls = new List<ToolCallDto>();
        TimingCapabilityDto vram = new("unavailable", "This capture records no GPU Memory budget counters.");
        if (Library(handle, "vram_budget", SqlBindings(start, end, rangeMode), []) is SqlResultDto vramRows && vramRows.RowCount > 0)
        {
            int worstPool = Enumerable.Range(0, vramRows.RowCount).OrderByDescending(i => Double(Cell(vramRows, i, "peakPercentOfBudget")) ?? 0).First();
            double peak = Double(Cell(vramRows, worstPool, "peakPercentOfBudget")) ?? 0;
            vram = new(peak >= 100 ? "overBudget" : peak >= 90 ? "nearBudget" : "withinBudget",
                $"{Cell(vramRows, worstPool, "adapterGroup")} {Cell(vramRows, worstPool, "pool")} usage peaked at {peak.ToString(CultureInfo.InvariantCulture)} % of its lowest budget before the window end.");
            if (peak >= 90) calls.Add(new("pix_timing_sql", new { handle, query = "vram_budget", rangeMode }, CostHints.Query));
        }

        string threadRowIdText = Ns(threadRow);
        string? startArgument = start.HasValue ? Ns(a) : null, endArgument = end.HasValue ? Ns(b) : null;
        static string FrameEnd(TimingFrameVerdictDto f) => Ns(long.Parse(f.StartNs, CultureInfo.InvariantCulture) + f.FrameNs);
        if (perFrame.Where(f => f.OnCpuPercent > 0).MaxBy(f => f.FrameNs * f.OnCpuPercent!.Value) is { } busiestCpu)
            calls.Add(new("pix_timing_hotspots", new { handle, processId = renderPid, threadId = renderTid, startNs = busiestCpu.StartNs, endNs = FrameEnd(busiestCpu) }, CostHints.Query));
        if (perFrame.Where(f => f.BlockedPercent + f.ReadyPercent > 0).MaxBy(f => f.FrameNs * (f.BlockedPercent!.Value + f.ReadyPercent!.Value)) is { } waiting)
            calls.Add(new("pix_timing_thread_switches", new { handle, threadRowId = threadRowIdText, startNs = waiting.StartNs, endNs = FrameEnd(waiting) }, CostHints.Query));
        TimingFrameVerdictDto longest = perFrame.MaxBy(f => f.FrameNs)!;
        calls.Add(new("pix_timing_tree", new { handle, threadRowId = threadRowIdText, startNs = longest.StartNs, endNs = FrameEnd(longest), depth = 3 }, CostHints.Query));
        calls.Add(new("pix_timing_gpu_summary", new { handle, processId = pid, startNs = Ns(fs), endNs = Ns(fe), rangeMode }, CostHints.Query));

        PageResult<TimingFrameVerdictDto> page = Paging.Page(perFrame.Skip(offset).Take(limit).ToArray(), perFrame.Count, offset, limit);
        if (page.NextOffset is int nextOffset)
            calls.Add(new("pix_timing_verdict", new { handle, processId, frameSource = requested, frameMarkerName, renderThreadRowId, startNs = startArgument, endNs = endArgument,
                rangeMode, maxFrames, offset = nextOffset, limit }, CostHints.Query));
        string? nextStart = truncated ? Ns(fe) : null;
        if (truncated)
            calls.Add(new("pix_timing_verdict", new { handle, processId, frameSource = chosenSource, frameMarkerName, renderThreadRowId = threadRowIdText, startNs = nextStart,
                endNs = endArgument, rangeMode, maxFrames, limit }, CostHints.Query));

        string selection = requested == "auto" ? $"auto: {chosenSource} was the first available of present, cpuMarker, vsync, submission" : "requested";
        return new TimingVerdictDto(handle, provenance, pid,
            new TimingVerdictFramesDto(chosenSource, detail, selection, available, frames.Count, truncated, nextStart),
            new TimingRenderThreadDto(threadRowIdText, renderPid, renderTid, thread[0].Name, renderSubmissions, threadSelection),
            summary, perFrame.OrderByDescending(f => f.FrameNs).ThenBy(f => f.Index).Take(5).ToArray(), page, waits, queues,
            new TimingVerdictCoverageDto(states is null ? "unavailable" : "available", switchEvents, readyLatencies.Count, renderSubmits.Count, gpuIntervals.Count,
                Share(totalFrame - totalUnknown)),
            vram, VerdictRules.RulesDto, VerdictRules.Semantics, calls);
    });
}
