using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using PixMcp.Pix.Handles;

namespace PixMcp.Tools;

[McpServerToolType]
public static class TimingQueryTools
{
    private const string WaitDescription = "Seconds to wait for the cached SQLite query job (default 2, max 3600). Pending responses include exact wait and retry calls. Queries do not replay the GPU.";
    private const string TimeDescription = "Optional decimal nanosecond timestamp in the recorded capture clock. The default interval is first reliable timestamp through capture stop; endNs is exclusive.";

    [McpServerTool(Name = "pix_timing_overview", Title = "Timing capture overview", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Summarizes recorded timing data: reliable interval, process/thread/queue pages, recorded counters, sample/callstack coverage and symbol availability. Start here for a CPU/GPU timing investigation; no GPU replay is performed.")]
    public static Task<string> Overview(PixSession session, JobManager jobs, [Description("Timing capture handle (from pix_timing_open).")] string handle, [Description("Recorded process id; default: the capture's target process.")] uint? processId = null,
        [Description("First item to return (default 0).")] int offset = 0, [Description("Maximum items to return (default 25, max 1000).")] int limit = 25, [Description(WaitDescription)] double waitSeconds = 2,
        [Description(Tools.IncludeProvenanceDescription)] bool includeProvenance = false,
        CancellationToken cancellationToken = default)
    {
        TimingDatabase.ValidatePage(offset, limit);
        return Query(session, jobs, "pix_timing_overview", handle, new { query = "overview", processId, offset, limit },
            db => db.Overview(handle, processId, offset, limit), waitSeconds, cancellationToken);
    }

    [McpServerTool(Name = "pix_timing_submissions", Title = "Recorded GPU submissions", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Pages recorded queue submissions correlated directly to their GPU execution. Returns the submitting thread, CPU submit timestamp, GPU start/end and validated latency. This is queue submission correlation, not individual draw or named-marker correlation. Zero or inconsistent GPU timing remains explicitly unavailable. Submission references expire on save, symbol resolution or close.")]
    public static Task<string> Submissions(PixSession session, JobManager jobs, [Description("Timing capture handle (from pix_timing_open).")] string handle,
        [Description("Exact submission reference returned by this tool; omit process/thread/queue/time filters when supplied.")] string? submissionRef = null,
        [Description("Recorded process id; default: the capture's target process.")] uint? processId = null, [Description("Only rows of this recorded thread id.")] uint? threadId = null, [Description("Only submissions to this recorded queue id.")] string? queueId = null,
        [Description("Optional decimal nanoseconds. The [startNs,endNs) interval selects CPU submission timestamps; original GPU timing is retained outside the selection. Defaults to the reliable capture interval.")] string? startNs = null,
        [Description("Window end in capture nanoseconds (exclusive); default: the end of the capture range.")] string? endNs = null, [Description("First item to return (default 0).")] int offset = 0, [Description("Maximum items to return (default 25, max 1000).")] int limit = 25,
        [Description(WaitDescription)] double waitSeconds = 2,
        [Description(Tools.IncludeProvenanceDescription)] bool includeProvenance = false,
        CancellationToken cancellationToken = default)
    {
        TimingDatabase.ValidatePage(offset, limit);
        long? start = TimingDatabase.ParseNs(startNs, nameof(startNs)), end = TimingDatabase.ParseNs(endNs, nameof(endNs));
        return Query(session, jobs, "pix_timing_submissions", handle,
            new { query = "submissions", submissionRef, processId, threadId, queueId, start, end, offset, limit },
            (db, generation) => db.Submissions(handle, generation, submissionRef, processId, threadId, queueId, start, end, offset, limit), waitSeconds, cancellationToken);
    }

    [McpServerTool(Name = "pix_timing_thread_switches", Title = "Recorded thread switches", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Pages recorded switch-in/out scheduling evidence for one capture-local thread lifetime. Switch-out rows include the raw wait-reason code and the stack recorded at that exact timestamp when present; unresolved addresses remain visible. These records do not establish blocked duration, waited-on objects, or the cause of a GPU gap.")]
    public static Task<string> ThreadSwitches(PixSession session, JobManager jobs, [Description("Timing capture handle (from pix_timing_open).")] string handle,
        [Description("Capture-local threadRowId from pix_timing_overview or pix_timing_submissions; not an OS thread ID.")] string threadRowId,
        [Description(TimeDescription)] string? startNs = null, [Description("Window end in capture nanoseconds (exclusive); default: the end of the capture range.")] string? endNs = null, [Description("First item to return (default 0).")] int offset = 0, [Description("Maximum items to return (default 25, max 1000).")] int limit = 25,
        [Description(WaitDescription)] double waitSeconds = 2,
        [Description(Tools.IncludeProvenanceDescription)] bool includeProvenance = false,
        CancellationToken cancellationToken = default)
    {
        TimingDatabase.ValidatePage(offset, limit); TimingDatabase.ParseId(threadRowId, nameof(threadRowId));
        long? start = TimingDatabase.ParseNs(startNs, nameof(startNs)), end = TimingDatabase.ParseNs(endNs, nameof(endNs));
        return Query(session, jobs, "pix_timing_thread_switches", handle,
            new { query = "thread-switches", threadRowId, start, end, offset, limit },
            db => db.ThreadSwitches(handle, threadRowId, start, end, offset, limit), waitSeconds, cancellationToken);
    }

    [McpServerTool(Name = "pix_timing_events", Title = "Recorded timing events", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Pages recorded PIX CPU/GPU executions with event names, lanes, original durations and overlap with the selected [startNs,endNs) interval. Event IDs identify marker definitions within this timing capture, not GPU-capture EventRefs. Nested event durations overlap and must not be summed into frame latency.")]
    public static Task<string> Events(PixSession session, JobManager jobs, [Description("Timing capture handle (from pix_timing_open).")] string handle,
        [Description("cpu, gpu, or all (default all).")] string domain = "all", [Description("Recorded process id; default: the capture's target process.")] uint? processId = null, [Description("Only rows of this recorded thread id.")] uint? threadId = null,
        [Description("Recorded queue ID from pix_timing_overview; requires domain=gpu.")] string? queueId = null,
        [Description("Only rows whose name contains this text (case-insensitive).")] string? nameContains = null, [Description(TimeDescription)] string? startNs = null, [Description("Window end in capture nanoseconds (exclusive); default: the end of the capture range.")] string? endNs = null,
        [Description("start (chronological, default) or duration (longest first).")] string orderBy = "start",
        [Description("First item to return (default 0).")] int offset = 0, [Description("Maximum items to return (default 25, max 1000).")] int limit = 25, [Description(WaitDescription)] double waitSeconds = 2,
        [Description(Shaping.FormatDescription)] string format = "objects",
        [Description(Shaping.BriefDescription)] bool brief = false,
        [Description(Shaping.TopNDescription)] int? topN = null,
        [Description(Shaping.MaxStringLengthDescription)] int? maxStringLength = null,
        [Description(Tools.IncludeProvenanceDescription)] bool includeProvenance = false,
        CancellationToken cancellationToken = default)
    {
        TimingDatabase.ValidatePage(offset, limit);
        ShapingOptions shaping = Shaping.Options(format, brief, topN, maxStringLength, offset);
        (int o, int l) = Shaping.Window(shaping, offset, limit);
        long? start = TimingDatabase.ParseNs(startNs, nameof(startNs)), end = TimingDatabase.ParseNs(endNs, nameof(endNs));
        return Query(session, jobs, "pix_timing_events", handle, new { query = "events", domain, processId, threadId, queueId, nameContains, start, end, orderBy, offset = o, limit = l },
            db => db.Events(handle, domain, processId, threadId, queueId, nameContains, start, end, orderBy, o, l), waitSeconds, cancellationToken,
            (reference, generation) =>
            {
                TimingEventsDto dto = Metadata<TimingEventsDto>(session.Results, reference, "", cancellationToken);
                ToolCallDto Call(int at, int? strings) => new("pix_timing_events", new { handle, domain, processId, threadId, queueId, nameContains, startNs, endNs, orderBy,
                    offset = at, limit = l, format, brief, maxStringLength = strings });
                if (shaping.Table)
                    return Shaping.Apply(dto.Events.Items, dto.Events.Total, o, l, shaping, RowShapes.RecordedTimingEvents, handle,
                        new { provenance = dto.Provenance, cpuExecutionTiming = dto.CpuExecutionTiming }, next => Call(next, maxStringLength), () => Call(o, Shaping.FullStringLength));
                if (shaping.Brief || shaping.TopN.HasValue)
                    dto = dto with { Events = dto.Events with
                    {
                        Items = shaping.Brief ? dto.Events.Items.Select(RowShapes.RecordedTimingEvents.Brief!).ToArray() : dto.Events.Items,
                        NextOffset = shaping.TopN.HasValue ? null : dto.Events.NextOffset,
                    } };
                return RowShapes.Finish(dto, shaping);
            });
    }

    [McpServerTool(Name = "pix_timing_counters_list", Title = "List recorded counters", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Pages the counters actually recorded in this timing capture, including capture-local IDs, deterministic group paths, descriptions and units. Follow a returned counterId with pix_timing_counters_read.")]
    public static Task<string> Counters(PixSession session, JobManager jobs, [Description("Timing capture handle (from pix_timing_open).")] string handle, [Description("Recorded process id; default: the capture's target process.")] uint? processId = null,
        [Description("Only rows whose name contains this text (case-insensitive).")] string? nameContains = null, [Description("First item to return (default 0).")] int offset = 0, [Description("Maximum items to return (default 25, max 1000).")] int limit = 25, [Description(WaitDescription)] double waitSeconds = 2,
        CancellationToken cancellationToken = default)
    {
        TimingDatabase.ValidatePage(offset, limit);
        return Query(session, jobs, "pix_timing_counters_list", handle, new { query = "counters", processId, nameContains, offset, limit },
            db => db.Counters(handle, processId, nameContains, offset, limit), waitSeconds, cancellationToken);
    }

    [McpServerTool(Name = "pix_timing_counters_read", Title = "Read recorded counter", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Pages exact recorded samples for one timing-capture counter in [startNs,endNs). Values retain the counter's native units; no downsampling or GPU replay occurs.")]
    public static Task<string> CounterSamples(PixSession session, JobManager jobs, [Description("Timing capture handle (from pix_timing_open).")] string handle, [Description("Recorded counter id from pix_timing_counters_list.")] string counterId,
        [Description(TimeDescription)] string? startNs = null, [Description("Window end in capture nanoseconds (exclusive); default: the end of the capture range.")] string? endNs = null, [Description("First item to return (default 0).")] int offset = 0, [Description("Maximum items to return (default 25, max 1000).")] int limit = 25,
        [Description(WaitDescription)] double waitSeconds = 2,
        [Description(Tools.IncludeProvenanceDescription)] bool includeProvenance = false,
        CancellationToken cancellationToken = default)
    {
        TimingDatabase.ValidatePage(offset, limit); TimingDatabase.ParseId(counterId, nameof(counterId));
        long? start = TimingDatabase.ParseNs(startNs, nameof(startNs)), end = TimingDatabase.ParseNs(endNs, nameof(endNs));
        return Query(session, jobs, "pix_timing_counters_read", handle, new { query = "counter-samples", counterId, start, end, offset, limit },
            db => db.CounterSamples(handle, counterId, start, end, offset, limit), waitSeconds, cancellationToken);
    }

    [McpServerTool(Name = "pix_timing_hotspots", Title = "CPU sample hotspots", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Ranks CPU sampled functions/addresses by inclusive sample count. Recursion counts once per function per sample; exclusive counts use the sampled leaf. Percentages use all selected samples, including samples without callstacks. Counts are statistical observations, not exact CPU time. Unresolved addresses remain visible; symbol resolution is always explicit.")]
    public static Task<string> Hotspots(PixSession session, JobManager jobs, [Description("Timing capture handle (from pix_timing_open).")] string handle, [Description("Recorded process id; default: the capture's target process.")] uint? processId = null, [Description("Only rows of this recorded thread id.")] uint? threadId = null,
        [Description(TimeDescription)] string? startNs = null, [Description("Window end in capture nanoseconds (exclusive); default: the end of the capture range.")] string? endNs = null, [Description("First item to return (default 0).")] int offset = 0, [Description("Maximum items to return (default 25, max 1000).")] int limit = 25,
        [Description(WaitDescription)] double waitSeconds = 2,
        [Description(Shaping.FormatDescription)] string format = "objects",
        [Description(Shaping.BriefDescription)] bool brief = false,
        [Description(Shaping.TopNDescription)] int? topN = null,
        [Description(Shaping.MaxStringLengthDescription)] int? maxStringLength = null,
        [Description(Tools.IncludeProvenanceDescription)] bool includeProvenance = false,
        CancellationToken cancellationToken = default)
    {
        TimingDatabase.ValidatePage(offset, limit);
        ShapingOptions shaping = Shaping.Options(format, brief, topN, maxStringLength, offset);
        long? start = TimingDatabase.ParseNs(startNs, nameof(startNs)), end = TimingDatabase.ParseNs(endNs, nameof(endNs));
        return Query(session, jobs, "pix_timing_hotspots", handle, new { query = "samples", processId, threadId, start, end },
            db => db.Samples(handle, processId, threadId, start, end), waitSeconds, cancellationToken, (reference, generation) =>
            {
                session.Get<TimingCaptureHandle>(handle).RememberProfile(reference, generation);
                ResultStore store = session.Results;
                TimingRangeDto provenance = Metadata<TimingRangeDto>(store, reference, "/provenance", cancellationToken);
                TimingSampleCoverageDto coverage = Metadata<TimingSampleCoverageDto>(store, reference, "/coverage", cancellationToken);
                long total = store.Read(reference, "/hotspots", 0, 1, cancellationToken).Total;
                (int o, int l) = Shaping.Window(shaping, offset, limit);
                TimingHotspotDto[] rows = store.EnumerateArray(reference, "/hotspots", Tools.MaxResultBytes, cancellationToken)
                    .Skip(o).Take(l).Select(e => e.Deserialize<TimingHotspotDto>(Json.Options)!).ToArray();
                ToolCallDto Call(int at, int? strings) => new("pix_timing_hotspots", new { handle, processId, threadId, startNs, endNs, offset = at, limit = l, format, brief, maxStringLength = strings });
                var calltree = new ToolCallDto("pix_timing_calltree", new { handle, profileRef = reference });
                if (shaping.Table)
                    return Shaping.Apply(rows, total, o, l, shaping, RowShapes.Hotspots, handle, new { profileRef = reference, provenance, coverage, calltree },
                        next => Call(next, maxStringLength), () => Call(o, Shaping.FullStringLength));
                PageResult<TimingHotspotDto> page = Paging.Page(rows, total, o, l);
                if (shaping.TopN.HasValue) page = page with { NextOffset = null };
                var next = new List<ToolCallDto> { calltree };
                if (page.NextOffset is int n) next.Add(Call(n, maxStringLength));
                return RowShapes.Finish(new TimingHotspotsDto(handle, reference, provenance, coverage, page, next), shaping);
            });
    }

    [McpServerTool(Name = "pix_timing_calltree", Title = "CPU sample call tree", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Pages caller-to-callee children in a recorded CPU sampling tree. Start with filters or reuse profileRef from hotspots; returned child calls preserve the exact profile and parentNodeId. Profiles expire on close, save, symbol resolution or result eviction. Missing stacks and unresolved symbols are reported in coverage.")]
    public static Task<string> Calltree(PixSession session, JobManager jobs, [Description("Timing capture handle (from pix_timing_open).")] string handle,
        [Description("Existing sampled profile; omit process/thread/time filters when supplied.")] string? profileRef = null,
        [Description("root, or a child node ID accompanied by its profileRef. Default: root.")] string parentNodeId = "root",
        [Description("Recorded process id; default: the capture's target process.")] uint? processId = null, [Description("Only rows of this recorded thread id.")] uint? threadId = null, [Description(TimeDescription)] string? startNs = null, [Description("Window end in capture nanoseconds (exclusive); default: the end of the capture range.")] string? endNs = null,
        [Description("First item to return (default 0).")] int offset = 0, [Description("Maximum items to return (default 25, max 1000).")] int limit = 25, [Description(WaitDescription)] double waitSeconds = 2,
        [Description(Tools.IncludeProvenanceDescription)] bool includeProvenance = false,
        CancellationToken cancellationToken = default)
    {
        TimingDatabase.ValidatePage(offset, limit);
        long? start = TimingDatabase.ParseNs(startNs, nameof(startNs)), end = TimingDatabase.ParseNs(endNs, nameof(endNs));
        if (profileRef is not null && (processId.HasValue || threadId.HasValue || start.HasValue || end.HasValue))
            throw new PixToolException(PixErrors.Codes.InvalidArguments, "Use profileRef to retain its sample selection, or omit profileRef to select new filters.");
        if (profileRef is null && parentNodeId != "root") throw new PixToolException(PixErrors.Codes.InvalidArguments, "A parentNodeId requires its profileRef.");
        if (profileRef is not null)
            return PixErrors.Guard("pix_timing_calltree", () => Task.FromResult(Tools.Serialize(Project(profileRef), "pix_timing_calltree", session, handle)));
        return Query(session, jobs, "pix_timing_calltree", handle, new { query = "samples", processId, threadId, start, end },
            db => db.Samples(handle, processId, threadId, start, end), waitSeconds, cancellationToken, (reference, generation) =>
            { session.Get<TimingCaptureHandle>(handle).RememberProfile(reference, generation); return Project(reference); });

        TimingCalltreeDto Project(string reference)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimingCaptureHandle capture = session.Get<TimingCaptureHandle>(handle);
            if (!capture.OwnsProfile(reference) || !session.Results.IsAvailable(reference))
                throw new PixToolException(PixErrors.Codes.ResultExpired, "The sampled profile is unavailable or belongs to a different/changed timing capture. Repeat pix_timing_hotspots or start a new calltree.",
                    nextCalls: [new("pix_timing_calltree", new { handle })]);
            ResultStore store = session.Results;
            TimingCallNodeDto? parent = null;
            var children = new List<TimingCallNodeDto>();
            long childIndex = 0;
            foreach (JsonElement item in store.EnumerateArray(reference, "/nodes", Tools.MaxResultBytes, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                TimingCallNodeDto node = item.Deserialize<TimingCallNodeDto>(Json.Options)!;
                if (node.NodeId == parentNodeId) parent = node;
                if (node.ParentNodeId == parentNodeId && childIndex++ >= offset && children.Count < limit) children.Add(node);
            }
            if (parent is null) throw new PixToolException(PixErrors.Codes.InvalidReference, "parentNodeId is not present in this sampled profile.");
            TimingCallNodeDto[] rows = children.ToArray();
            PageResult<TimingCallNodeDto> page = Paging.Page(rows, childIndex, offset, limit);
            var next = rows.Where(n => n.ChildCount > 0).Select(n => new ToolCallDto("pix_timing_calltree", new { handle, profileRef = reference, parentNodeId = n.NodeId, limit })).ToList();
            if (page.NextOffset is int n) next.Add(new("pix_timing_calltree", new { handle, profileRef = reference, parentNodeId, offset = n, limit }));
            var result = new TimingCalltreeDto(handle, reference, Metadata<TimingRangeDto>(store, reference, "/provenance", cancellationToken),
                Metadata<TimingSampleCoverageDto>(store, reference, "/coverage", cancellationToken), parent, page, next);
            if (!capture.OwnsProfile(reference))
                throw new PixToolException(PixErrors.Codes.ResultExpired, "The timing capture changed while this profile was being read. Start a new calltree.",
                    nextCalls: [new("pix_timing_calltree", new { handle })]);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
    }

    private static T Metadata<T>(ResultStore store, string reference, string pointer, CancellationToken cancellationToken)
        => store.ReadElement(reference, pointer, Tools.MaxResultBytes, cancellationToken).Deserialize<T>(Json.Options)!;

    private static Task<string> Query(PixSession session, JobManager jobs, string tool, string handle, object key,
        Func<TimingDatabase, object> query, double waitSeconds, CancellationToken cancellationToken, Func<string, int, object>? project = null)
        => Query(session, jobs, tool, handle, key, (database, _) => query(database), waitSeconds, cancellationToken, project);

    private static Task<string> Query(PixSession session, JobManager jobs, string tool, string handle, object key,
        Func<TimingDatabase, int, object> query, double waitSeconds, CancellationToken cancellationToken, Func<string, int, object>? project = null)
        => PixErrors.Guard(tool, async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!double.IsFinite(waitSeconds) || waitSeconds is < 0 or > 3600) throw new PixToolException(PixErrors.Codes.InvalidArguments, "waitSeconds must be between 0 and 3600.");
            TimingCaptureHandle capture = session.Get<TimingCaptureHandle>(handle);
            Job job = capture.QueryJob(jobs, session.Results, tool, key, query, out int generation);
            if (!job.IsFinished && waitSeconds > 0)
            {
                try { await job.WaitAsync(TimeSpan.FromSeconds(waitSeconds), cancellationToken).ConfigureAwait(false); }
                catch (TimeoutException) { }
            }
            if (!job.IsFinished)
                return Json.Serialize(new PendingDto(true, job.Id, tool, "Recorded timing data is being queried. Wait for the job, then repeat the original call.", job.ToDto(),
                    [new("pix_job_wait", new { jobId = job.Id, timeoutSeconds = 2 }), new(tool, StructuredToolResults.CurrentArguments() ?? new { handle })]));
            if (job.Status != JobStatus.Succeeded)
                throw new PixToolException(job.ErrorDetail ?? new("timing_query_failed", job.Error ?? "Timing query failed.", null, false, []));
            string resultRef = job.ResultRef ?? throw new PixToolException(PixErrors.Codes.ResultExpired, "The timing query result is unavailable. Repeat this query.");
            cancellationToken.ThrowIfCancellationRequested();
            capture.RequireQueryGeneration(generation);
            object value;
            try { value = project?.Invoke(resultRef, generation) ?? session.Results.ReadElement(resultRef, maxBytes: Tools.MaxResultBytes, cancellationToken: cancellationToken); }
            catch (PixToolException ex) when (ex.Detail.Code == "result_too_large")
            { throw new PixToolException(PixErrors.Codes.ResultTooLarge, "The complete timing result is retained. Read it in bounded windows with pix_result_read.", nextCalls: [ResultStore.ReadCall(resultRef)]); }
            capture.RequireQueryGeneration(generation);
            cancellationToken.ThrowIfCancellationRequested();
            return Tools.Serialize(value, tool, session, handle);
        });
}
