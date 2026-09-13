using System.ComponentModel;
using ModelContextProtocol.Server;
using PixMcp.Pix;

namespace PixMcp.Tools;

/// <summary>Recorded-timing rollups built on submissions, scheduling and PIX markers; no GPU replay.</summary>
[McpServerToolType]
public static class TimingAnalysisTools
{
    [McpServerTool(Name = "pix_timing_gpu_summary", Title = "Recorded GPU queue summary", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false),
     Description("Summarizes recorded GPU work per API command queue without replay: valid and invalid submissions, busy/idle/span as the union of execution intervals clipped to the window, submit latency and execution p50/p95/max, top submitting threads, longest executions with submissionRef, hardware queue busy time, VSync pacing and whether CPU-to-GPU marker links were recorded. Submissions are selected by CPU submit timestamp; recorded execution spans whole ExecuteCommandLists calls, not draws.")]
    public static Task<string> GpuSummary(PixSession session, JobManager jobs,
        [Description("Timing capture handle (from pix_timing_open).")] string handle,
        [Description("Recorded process id; default: the capture's target process.")] uint? processId = null,
        [Description("Only this recorded API command queue (queueId from pix_timing_overview).")] string? queueId = null,
        [Description(TimingQueryTools.TimeDescription)] string? startNs = null,
        [Description("Window end in capture nanoseconds (exclusive); default: per rangeMode.")] string? endNs = null,
        [Description(TimingSqlTools.RangeModeDescription)] string rangeMode = TimingDatabase.RangeModeFull,
        [Description("Top submitting threads, longest executions and hardware queues to list (default 10, max 100).")] int limit = 10,
        [Description(TimingQueryTools.WaitDescription)] double waitSeconds = 2,
        [Description(Tools.IncludeProvenanceDescription)] bool includeProvenance = false,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100) throw PixErrors.InvalidArguments("limit must be between 1 and 100.");
        long? start = TimingDatabase.ParseNs(startNs, nameof(startNs)), end = TimingDatabase.ParseNs(endNs, nameof(endNs));
        if (queueId is not null) TimingDatabase.ParseId(queueId, nameof(queueId));
        string mode = TimingDatabase.NormalizeRangeMode(rangeMode);
        return TimingQueryTools.Query(session, jobs, "pix_timing_gpu_summary", handle, new { query = "gpu-summary", processId, queueId, start, end, mode, limit },
            (db, generation) => db.GpuSummary(handle, generation, processId, queueId, start, end, mode, limit), waitSeconds, cancellationToken);
    }

    [McpServerTool(Name = "pix_timing_tree", Title = "Recorded marker tree", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false),
     Description("Aggregates one lane's recorded PIX events by marker path without replay: a thread's CPU events (threadRowId) or an API queue's GPU-side events (queueId), nested by recorded level and interval. Each path reports occurrences, inclusive and self time clipped to the window with shares of the lane span and parent, complete-occurrence duration statistics, execution/stall sums on thread lanes, and malformed nesting. Sum siblings, never ancestors.")]
    public static Task<string> Tree(PixSession session, JobManager jobs,
        [Description("Timing capture handle (from pix_timing_open).")] string handle,
        [Description("Capture-local thread row id (threadRowId from pix_timing_overview or pix_timing_submissions); selects that thread's PIX CPU events. Pass this or queueId.")] string? threadRowId = null,
        [Description("Recorded API command queue id; selects its GPU-side PIX events, which are empty unless the application emits GPU markers. Pass this or threadRowId.")] string? queueId = null,
        [Description("List the descendants of this marker path (names joined by '/', as returned in path); default: top-level events.")] string? parentPath = null,
        [Description("Levels below the parent to list (default 2, max 8).")] int depth = 2,
        [Description("Sibling order: inclusive (default), self, occurrences, name or firstStart.")] string sortBy = "inclusive",
        [Description("Hide paths whose self time is below this many nanoseconds unless a descendant is listed.")] long? minSelfNs = null,
        [Description(TimingQueryTools.TimeDescription)] string? startNs = null,
        [Description("Window end in capture nanoseconds (exclusive); default: per rangeMode.")] string? endNs = null,
        [Description(TimingSqlTools.RangeModeDescription)] string rangeMode = TimingDatabase.RangeModeFull,
        [Description("Sum recorded execution and stall per path on thread lanes (one lookup per event, up to 2000 events; default true).")] bool includeExecution = true,
        [Description("First item to return (default 0).")] int offset = 0,
        [Description("Maximum items to return (default 25, max 1000).")] int limit = 25,
        [Description(TimingQueryTools.WaitDescription)] double waitSeconds = 2,
        [Description(Tools.IncludeProvenanceDescription)] bool includeProvenance = false,
        CancellationToken cancellationToken = default)
    {
        TimingDatabase.ValidatePage(offset, limit);
        if ((threadRowId is null) == (queueId is null)) throw PixErrors.InvalidArguments("Pass exactly one lane: threadRowId or queueId.");
        if (threadRowId is not null) TimingDatabase.ParseId(threadRowId, nameof(threadRowId));
        if (queueId is not null) TimingDatabase.ParseId(queueId, nameof(queueId));
        if (depth is < 1 or > 8) throw PixErrors.InvalidArguments("depth must be between 1 and 8.");
        if (minSelfNs is < 0) throw PixErrors.InvalidArguments("minSelfNs must be nonnegative.");
        string sort = TimingDatabase.NormalizeTreeSort(sortBy);
        long? start = TimingDatabase.ParseNs(startNs, nameof(startNs)), end = TimingDatabase.ParseNs(endNs, nameof(endNs));
        string mode = TimingDatabase.NormalizeRangeMode(rangeMode);
        return TimingQueryTools.Query(session, jobs, "pix_timing_tree", handle,
            new { query = "marker-tree", threadRowId, queueId, parentPath, depth, sort, minSelfNs, start, end, mode, includeExecution, offset, limit },
            db => db.MarkerTree(handle, threadRowId, queueId, parentPath, depth, sort, minSelfNs, start, end, offset, limit, includeExecution, mode), waitSeconds, cancellationToken);
    }

    [McpServerTool(Name = "pix_timing_verdict", Title = "Recorded frame verdict", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false),
     Description("Classifies recorded frames as gpuBound, cpuBound, syncBound, waitBound, contended, presentBound or balanced without replay (experimental heuristic). Frames come from GpuFrame presents, the render thread's most repeated top-level PIX event, VSync markers or submission cadence. Per frame it measures GPU busy time (union of recorded queue execution) and the render thread's on-CPU, blocked and ready-not-running time from context switches, then applies the returned rules and thresholds. Returns the dominant verdict with confidence, frame statistics, the longest frames, wait reasons with probable names, a paged per-frame table and follow-up calls.")]
    public static Task<string> Verdict(PixSession session, JobManager jobs,
        [Description("Timing capture handle (from pix_timing_open).")] string handle,
        [Description("Recorded process id; default: the capture's target process.")] uint? processId = null,
        [Description("auto (default: present, then cpuMarker, vsync, submission), present (GpuFrame), cpuMarker (repeated top-level PIX event on the render thread), vsync (busiest VSync lane) or submission (render-thread submit cadence).")] string frameSource = "auto",
        [Description("PIX event name that delimits frames on the render thread (frameSource cpuMarker or auto); default: the most repeated event at the shallowest level.")] string? frameMarkerName = null,
        [Description("Capture-local thread row id of the render thread; default: the thread with the most submissions in the window.")] string? renderThreadRowId = null,
        [Description(TimingQueryTools.TimeDescription)] string? startNs = null,
        [Description("Window end in capture nanoseconds (exclusive); default: per rangeMode.")] string? endNs = null,
        [Description(TimingSqlTools.RangeModeDescription)] string rangeMode = TimingDatabase.RangeModeFull,
        [Description("Frames to analyze from the window start (default 600, 2-5000); a continuation call covers the rest.")] int maxFrames = 600,
        [Description("First per-frame row to return (default 0).")] int offset = 0,
        [Description("Per-frame rows to return (default 5, max 1000).")] int limit = 5,
        [Description(TimingQueryTools.WaitDescription)] double waitSeconds = 2,
        [Description(Tools.IncludeProvenanceDescription)] bool includeProvenance = false,
        CancellationToken cancellationToken = default)
    {
        TimingDatabase.ValidatePage(offset, limit);
        if (maxFrames is < 2 or > 5000) throw PixErrors.InvalidArguments("maxFrames must be between 2 and 5000.");
        string source = TimingDatabase.NormalizeFrameSource(frameSource);
        if (renderThreadRowId is not null) TimingDatabase.ParseId(renderThreadRowId, nameof(renderThreadRowId));
        long? start = TimingDatabase.ParseNs(startNs, nameof(startNs)), end = TimingDatabase.ParseNs(endNs, nameof(endNs));
        string mode = TimingDatabase.NormalizeRangeMode(rangeMode);
        return TimingQueryTools.Query(session, jobs, "pix_timing_verdict", handle,
            new { query = "verdict", processId, source, frameMarkerName, renderThreadRowId, start, end, mode, maxFrames, offset, limit },
            db => db.Verdict(handle, processId, source, frameMarkerName, renderThreadRowId, start, end, maxFrames, offset, limit, mode), waitSeconds, cancellationToken);
    }
}
