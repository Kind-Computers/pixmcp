using System.Globalization;
using System.Text.Json;
using PixMcp.Pix.Sql;

namespace PixMcp.Pix;

/// <summary>Capture metadata as recorded (CaptureData name/value strings).</summary>
public sealed record TimingCaptureInfoDto(IReadOnlyDictionary<string, string> Data)
{
    /// <summary>CaptureData keys worth an overview; the stop time, target process id, OS build and machine name are left to pix_timing_sql.</summary>
    public static readonly IReadOnlySet<string> OverviewKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "Target Process Name", "PIX Version", "OS", "CPU", "CPU Architecture", "Memory (system)", "GPU Info", "Capture Options",
    };
}

/// <summary>Whether the recording is complete: dropped, truncated and lost ETW events, and the effective CPU sampling rate.</summary>
public sealed record TimingDataQualityDto(string State, long DroppedEvents, IReadOnlyDictionary<string, long> DroppedByType, long TruncatedEvents,
    long LostEtwEvents, long LostEtwBuffers, double? SamplesPerSecond);

/// <summary>One API command queue of the target process in the window (from the gpu_busy_per_queue named query).</summary>
public sealed record TimingGpuQueueSummaryDto(string QueueId, string? Name, string? Type, long Submissions, long BusyNs, double BusyMs,
    double? BusyPercentOfWindow, long SpanNs, double? AvgSubmitLatencyMs, double? MaxSubmitLatencyMs);

/// <summary>Display refresh pacing from the busiest VSync lane (frames_vsync).</summary>
public sealed record TimingFramesSummaryDto(string Source, string? Monitor, long Intervals, double AvgMs, double? P50Ms, double? P95Ms, double MaxMs, double? AvgHz);

public sealed record TimingCoresSummaryDto(long LogicalCores, long PhysicalCores, bool Heterogeneous, IReadOnlyList<long> EfficiencyClasses);
public sealed record TimingModulesSummaryDto(long Modules, long Resolved, IReadOnlyList<string> UnresolvedExamples);
public sealed record TimingVramPoolDto(string AdapterGroup, string Pool, double? LastBudgetMb, double? PeakUsageMb, double? LastUsageMb, double? PeakPercentOfBudget);

/// <summary>A finding with the numbers behind it, what it means, and the exact calls that dig further.</summary>
public sealed record TimingInsightDto(string Id, string Severity, string Summary, IReadOnlyDictionary<string, object?> Evidence, string Implication,
    IReadOnlyList<ToolCallDto> NextCalls);

/// <summary>
/// Overview sections computed with the named query library, so they always agree with pix_timing_sql query=name. A
/// section is null when this capture lacks its tables or its query failed; Unavailable says which and why.
/// </summary>
public sealed record TimingOverviewSectionsDto(TimingCaptureInfoDto? Capture, TimingDataQualityDto? DataQuality, IReadOnlyList<TimingGpuQueueSummaryDto>? Gpu,
    TimingFramesSummaryDto? Frames, TimingCoresSummaryDto? Cores, TimingModulesSummaryDto? Modules, IReadOnlyList<TimingVramPoolDto>? Vram,
    IReadOnlyList<TimingInsightDto> Insights, IReadOnlyList<string> Unavailable);

internal sealed partial class TimingDatabase
{
    public const int MaxInsights = 8;

    internal TimingOverviewSectionsDto OverviewSections(string handle, string rangeMode, IReadOnlyDictionary<string, TimingCapabilityDto> capabilities, TimingRangeDto range)
    {
        TimingSqlBindings bindings = SqlBindings(null, null, rangeMode);
        var unavailable = new List<string>();

        TimingCaptureInfoDto? capture = null;
        if (Has("CaptureData", "NameId", "ValueId") && Has("Strings", "Id", "Value"))
        {
            var data = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach ((string name, string value) in Rows("SELECT n.Value, v.Value FROM CaptureData d JOIN Strings n ON n.Id = d.NameId JOIN Strings v ON v.Id = d.ValueId",
                r => (r.IsDBNull(0) ? "" : r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1).Trim())))
                if (TimingCaptureInfoDto.OverviewKeys.Contains(name)) data[name] = value;
            capture = new TimingCaptureInfoDto(data);
        }
        else unavailable.Add("capture: CaptureData is absent.");

        TimingDataQualityDto? quality = null;
        if (Library(handle, "dropped_data", bindings, unavailable) is SqlResultDto dropped)
        {
            long droppedEvents = 0, truncated = 0, lostEvents = 0, lostBuffers = 0;
            var byType = new SortedDictionary<string, long>(StringComparer.Ordinal);
            for (int i = 0; i < dropped.RowCount; i++)
            {
                long events = Long(Cell(dropped, i, "events")) ?? 0;
                switch (Cell(dropped, i, "kind") as string)
                {
                    case "dropped": droppedEvents += events; byType[Convert.ToString(Cell(dropped, i, "type"), CultureInfo.InvariantCulture) ?? "?"] = events; break;
                    case "truncated": truncated += events; break;
                    case "lostEtwEvents": lostEvents += events; break;
                    case "lostEtwBuffers": lostBuffers += events; break;
                }
            }
            double? samplesPerSecond = null;
            if (Has("CpuSample", "Timestamp"))
            {
                var (count, first, last) = Rows("SELECT COUNT(*), MIN(Timestamp), MAX(Timestamp) FROM CpuSample", r => (r.GetInt64(0), r.IsDBNull(1) ? 0 : r.GetInt64(1), r.IsDBNull(2) ? 0 : r.GetInt64(2)))[0];
                if (count > 1 && last > first) samplesPerSecond = Math.Round(count / ((last - first) / 1e9), 1);
            }
            quality = new TimingDataQualityDto(droppedEvents + truncated + lostEvents + lostBuffers > 0 ? "lossy" : "clean", droppedEvents, byType, truncated, lostEvents, lostBuffers, samplesPerSecond);
        }

        List<TimingGpuQueueSummaryDto>? gpu = null;
        if (Library(handle, "gpu_busy_per_queue", bindings, unavailable) is SqlResultDto busy)
        {
            gpu = [];
            for (int i = 0; i < busy.RowCount; i++)
            {
                long busyNs = Long(Cell(busy, i, "busyNs")) ?? 0, windowNs = Long(Cell(busy, i, "windowNs")) ?? 0;
                gpu.Add(new TimingGpuQueueSummaryDto(Convert.ToString(Cell(busy, i, "queueId"), CultureInfo.InvariantCulture) ?? "", Cell(busy, i, "queueName") as string,
                    Cell(busy, i, "queueType") as string, Long(Cell(busy, i, "submissions")) ?? 0, busyNs, Ms(busyNs),
                    windowNs > 0 ? Math.Round(100.0 * busyNs / windowNs, 2) : null, Long(Cell(busy, i, "spanNs")) ?? 0,
                    Double(Cell(busy, i, "avgSubmitLatencyNs")) is double avg ? Ms(avg) : null, Double(Cell(busy, i, "maxSubmitLatencyNs")) is double max ? Ms(max) : null));
            }
        }

        TimingFramesSummaryDto? frames = null;
        if (Library(handle, "frames_vsync", bindings, unavailable) is SqlResultDto vsync && vsync.RowCount > 0)
            frames = new TimingFramesSummaryDto("CustomMarker:VSync", Cell(vsync, 0, "monitor") as string, Long(Cell(vsync, 0, "intervals")) ?? 0,
                Ms(Double(Cell(vsync, 0, "avgIntervalNs")) ?? 0), Double(Cell(vsync, 0, "p50IntervalNs")) is double p50 ? Ms(p50) : null,
                Double(Cell(vsync, 0, "p95IntervalNs")) is double p95 ? Ms(p95) : null, Ms(Double(Cell(vsync, 0, "maxIntervalNs")) ?? 0), Double(Cell(vsync, 0, "avgHz")));

        TimingCoresSummaryDto? cores = null;
        if (Library(handle, "core_efficiency", bindings, unavailable) is SqlResultDto coreRows && coreRows.RowCount > 0)
        {
            var classes = Enumerable.Range(0, coreRows.RowCount).Select(i => Long(Cell(coreRows, i, "efficiencyClass")) ?? 0).ToArray();
            cores = new TimingCoresSummaryDto(Enumerable.Range(0, coreRows.RowCount).Sum(i => Long(Cell(coreRows, i, "logicalCores")) ?? 0),
                Enumerable.Range(0, coreRows.RowCount).Sum(i => Long(Cell(coreRows, i, "physicalCores")) ?? 0), classes.Length > 1, classes);
        }

        TimingModulesSummaryDto? modules = null;
        if (Library(handle, "module_symbols", bindings, unavailable, maxRows: 5000) is SqlResultDto moduleRows)
        {
            int[] rows = Enumerable.Range(0, moduleRows.RowCount).ToArray();
            modules = new TimingModulesSummaryDto(moduleRows.RowCount, rows.Count(i => Cell(moduleRows, i, "symbolState") as string == "resolved"),
                rows.Where(i => Cell(moduleRows, i, "symbolState") as string == "unresolved").Select(i => Cell(moduleRows, i, "module") as string ?? "?").Take(3).ToArray());
        }

        List<TimingVramPoolDto>? vram = null;
        if (Library(handle, "vram_budget", bindings, unavailable) is SqlResultDto vramRows)
            vram = Enumerable.Range(0, vramRows.RowCount).Select(i => new TimingVramPoolDto(Cell(vramRows, i, "adapterGroup") as string ?? "", Cell(vramRows, i, "pool") as string ?? "",
                Double(Cell(vramRows, i, "lastBudgetMb")), Double(Cell(vramRows, i, "peakUsageMb")), Double(Cell(vramRows, i, "lastUsageMb")), Double(Cell(vramRows, i, "peakPercentOfBudget")))).ToList();

        var insights = Insights(handle, rangeMode, capabilities, range, quality, gpu, frames, cores, modules, vram);
        return new TimingOverviewSectionsDto(capture, quality, gpu, frames, cores, modules, vram, insights, unavailable);
    }

    private static List<TimingInsightDto> Insights(string handle, string rangeMode, IReadOnlyDictionary<string, TimingCapabilityDto> capabilities, TimingRangeDto range,
        TimingDataQualityDto? quality, IReadOnlyList<TimingGpuQueueSummaryDto>? gpu, TimingFramesSummaryDto? frames, TimingCoresSummaryDto? cores,
        TimingModulesSummaryDto? modules, IReadOnlyList<TimingVramPoolDto>? vram)
    {
        ToolCallDto Sql(string query) => new("pix_timing_sql", new { handle, query, rangeMode }, CostHints.Query);
        var insights = new List<TimingInsightDto>();

        if (rangeMode == RangeModeReliable && range.Coverage is { GpuSubmissions: { } submissions } && submissions.InRange < submissions.Total)
            insights.Add(new("window_truncates_data", "warning",
                $"rangeMode=reliable excludes {submissions.Total - submissions.InRange} of {submissions.Total} GPU submissions recorded after the stop timestamp.",
                new Dictionary<string, object?> { ["gpuSubmissionsInRange"] = submissions.InRange, ["gpuSubmissionsTotal"] = submissions.Total, ["reliableEndNs"] = range.ReliableEndNs, ["captureEndNs"] = range.CaptureEndNs },
                "Totals and percentages cover only part of the recording.", [new ToolCallDto("pix_timing_overview", new { handle, rangeMode = RangeModeFull }, CostHints.Query)]));

        if (quality is { State: "lossy" })
            insights.Add(new("dropped_data", quality.DroppedEvents + quality.LostEtwEvents > 1000 ? "warning" : "info",
                $"The recorder dropped {quality.DroppedEvents} events, truncated {quality.TruncatedEvents} and lost {quality.LostEtwEvents} ETW events.",
                new Dictionary<string, object?> { ["droppedEvents"] = quality.DroppedEvents, ["truncatedEvents"] = quality.TruncatedEvents, ["lostEtwEvents"] = quality.LostEtwEvents },
                "Counts and durations on the affected lanes are lower bounds.", [Sql("dropped_data")]));

        if (vram is not null)
            foreach (TimingVramPoolDto pool in vram.Where(p => p.PeakPercentOfBudget >= 90))
                insights.Add(new(pool.PeakPercentOfBudget >= 100 ? "vram_over_budget" : "vram_near_budget", pool.PeakPercentOfBudget >= 100 ? "warning" : "info",
                    $"{pool.AdapterGroup} {pool.Pool} video memory peaked at {pool.PeakPercentOfBudget} % of its budget.",
                    new Dictionary<string, object?> { ["peakUsageMb"] = pool.PeakUsageMb, ["budgetMb"] = pool.LastBudgetMb, ["peakPercentOfBudget"] = pool.PeakPercentOfBudget },
                    "Over budget, the OS demotes allocations to system memory, which shows up as GPU stalls and paging.", [Sql("vram_budget")]));

        if (gpu is { Count: > 0 })
        {
            TimingGpuQueueSummaryDto busiest = gpu.OrderByDescending(q => q.BusyNs).First();
            if (busiest.BusyPercentOfWindow is double percent && percent < 50)
                insights.Add(new("gpu_idle_high", "info",
                    $"The busiest GPU queue ({busiest.Name ?? busiest.QueueId}) executed work for only {percent} % of the window.",
                    new Dictionary<string, object?> { ["busyMs"] = busiest.BusyMs, ["busyPercentOfWindow"] = percent },
                    "The GPU mostly waited for work: the frame is likely CPU-bound, paced by presentation, or the capture includes idle time.",
                    [Sql("submit_latency_per_thread"), new ToolCallDto("pix_timing_events", new { handle, domain = "gpuSubmissions", orderBy = "duration", rangeMode }, CostHints.Query)]));
            TimingGpuQueueSummaryDto? waited = gpu.Where(q => q.MaxSubmitLatencyMs >= 1).OrderByDescending(q => q.MaxSubmitLatencyMs).FirstOrDefault();
            if (waited is not null)
                insights.Add(new("submit_latency_high", "info",
                    $"Submitted GPU work on {waited.Name ?? waited.QueueId} waited up to {waited.MaxSubmitLatencyMs} ms before the GPU started it (average {waited.AvgSubmitLatencyMs} ms).",
                    new Dictionary<string, object?> { ["maxSubmitLatencyMs"] = waited.MaxSubmitLatencyMs, ["avgSubmitLatencyMs"] = waited.AvgSubmitLatencyMs },
                    "Long waits mean the GPU was still executing earlier work (GPU-bound) or the driver held the submission.", [Sql("submit_latency_per_thread")]));
        }

        if (capabilities.TryGetValue("gpuMarkers", out TimingCapabilityDto? markers) && markers.State != "available"
            && capabilities.TryGetValue("gpuSubmissions", out TimingCapabilityDto? submitted) && submitted.State == "available")
            insights.Add(new("no_gpu_markers", "info", "No GPU-side PIX events were recorded; GPU work is visible only as queue submissions and hardware ranges.",
                new Dictionary<string, object?> { ["gpuMarkers"] = markers.State, ["gpuSubmissions"] = submitted.Rows },
                "Per-pass GPU timing needs PIXBeginEvent on command lists or a GPU capture (pix_gpu_open).",
                [new ToolCallDto("pix_timing_events", new { handle, domain = "gpuSubmissions", rangeMode }, CostHints.Query)]));

        if (frames is null && capabilities.ContainsKey("cpuEvents"))
            insights.Add(new("frames_unavailable", "info", "No VSync markers were recorded, so display refresh pacing cannot be derived.",
                new Dictionary<string, object?>(), "Use PIX CPU events or GPU submission cadence to delimit frames.", [Sql("cpu_execution_rollup")]));

        if (cores is { Heterogeneous: true })
            insights.Add(new("hybrid_cores_present", "info", $"The CPU has {cores.EfficiencyClasses.Count} core efficiency classes.",
                new Dictionary<string, object?> { ["efficiencyClasses"] = cores.EfficiencyClasses, ["logicalCores"] = cores.LogicalCores },
                "Threads scheduled on efficiency cores run slower; compare where the game's threads ran.", [Sql("core_efficiency")]));

        if (modules is { Modules: > 0, Resolved: 0 })
            insights.Add(new("symbols_unresolved", "info", $"None of the {modules.Modules} modules of the target process have resolved symbols.",
                new Dictionary<string, object?> { ["modules"] = modules.Modules, ["examples"] = modules.UnresolvedExamples },
                "CPU hotspots and call trees show addresses instead of function names until pix_timing_resolve_symbols loads matching PDBs.", [Sql("module_symbols")]));

        return insights.OrderBy(i => i.Severity == "warning" ? 0 : 1).Take(MaxInsights).ToList();
    }

    /// <summary>Runs a named query under the SQL guard with the overview's bindings; missing tables and SQL failures make the section unavailable instead of failing the overview.</summary>
    private SqlResultDto? Library(string handle, string name, TimingSqlBindings bindings, List<string> unavailable, object? parameters = null, int maxRows = 50)
    {
        NamedTimingQuery query = TimingQueryLibrary.Find(name) ?? throw new InvalidOperationException("Unknown named query " + name);
        if (!RequirementsMet(query.Requires))
        {
            unavailable.Add($"{name}: this capture lacks {string.Join(", ", query.Requires)}.");
            return null;
        }
        try
        {
            var supplied = parameters is null ? null : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(parameters));
            return SqlQuery.Execute(this, new SqlRequest(query.Sql)
            {
                Params = query.BindParams(supplied), Prebound = bindings.Values, MaxRows = maxRows, MaxBytes = SqlRequest.MaxMaxBytes, TimeoutSeconds = 20,
            }, "pixstorage", handle);
        }
        catch (PixToolException ex) when (ex.Detail.Code is not (PixErrors.Codes.TimingQueryInvalidated or PixErrors.Codes.TimingQueryTimeout))
        {
            unavailable.Add($"{name}: {ex.Detail.Code} ({ex.Detail.Message})");
            return null;
        }
    }

    private static object? Cell(SqlResultDto result, int row, string column)
    {
        for (int i = 0; i < result.Columns.Count; i++)
            if (result.Columns[i].Name == column) return result.Rows[row][i];
        return null;
    }

    private static long? Long(object? value) => value switch
    {
        null => null,
        long l => l,
        double d => (long)Math.Round(d),
        string s when long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long parsed) => parsed,
        _ => null,
    };

    private static double? Double(object? value) => value switch
    {
        null => null,
        long l => l,
        double d => d,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) => parsed,
        _ => null,
    };

    private static double Ms(double nanoseconds) => Math.Round(nanoseconds / 1_000_000.0, 3);
}
