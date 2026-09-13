using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using PixMcp.Pix.Handles;

namespace PixMcp.Tools;

[McpServerToolType]
public static class BottleneckTools
{
    public static readonly string[] EvidenceSources = ["timing", "counters", "occupancy", "hf", "drpix", "shaderProfile"];
    private static readonly string[] DefaultEvidence = ["timing", "counters", "occupancy"];
    private static readonly string[] DrPixPreference = ["1x1 Viewport", "ForceEarlyZ", "Quad Histogram", "MSAA", "Vertex/Primitive Efficiency"];
    private const int SmallDispatchThreadGroups = 64, MaxInlineRows = 30;

    [McpServerTool(Name = "pix_gpu_bottleneck", Title = "Classify GPU bottleneck", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), Description("Replays the capture on the local GPU as a job. Classifies what limits one scope (scope or markerPathPrefix, required) from evidence gathered in stages: timing (idle time in the scope window, work pending before execution, small dispatches), counters (a vendor preset plus D3D pipeline statistics and depth occlusion over the scope's work events, with derived ratios), occupancy and high-frequency counters over the scope's replay window, Dr. PIX savings (only when requested; replays per experiment) and a pointer to live shader profiling. Heuristic rules score limiters (pixelShading, vertexOrGeometry, rasterOrDepth, memoryBandwidth, cacheMiss, occupancyLatency, launchOverhead, syncIdle); the result has a verdict with confidence, alternatives, the evidence table, rule results, recommendations with calls, coverage per stage and a detailRef. Results are cached per scope and evidence until analysis stops.")]
    public static async Task<string> Bottleneck(PixSession session, JobManager jobs,
        [Description("GPU capture handle")] string handle,
        [Description(EventScope.Description + " Required unless markerPathPrefix is given.")] EventRef? scope = null,
        [Description(EventScope.PrefixDescription + " Required unless scope is given.")] string? markerPathPrefix = null,
        [Description("Evidence stages: timing, counters and occupancy by default, plus hf, drpix (replays per experiment) and shaderProfile; timing always runs.")] string[]? evidence = null,
        [Description(CounterPresets.NamesDescription + " Vendor preset for the counters stage (default utilization); pipeline statistics and depth occlusion are always added.")] string preset = "utilization",
        [Description("Collect occupancy and high-frequency counters with their own replay even when the timing pass has them (default false).")] bool forceStandalone = false,
        [Description("Dr. PIX experiments to run when evidence includes drpix (default 3, max 8).")] int maxDrPixRuns = 3,
        [Description("Include replay provenance in the result (default true).")] bool includeProvenance = true,
        [Description(Tools.WaitSecondsDescription)] double waitSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (scope is null && markerPathPrefix is null)
                throw new PixToolException(PixErrors.Codes.InvalidArguments, "pix_gpu_bottleneck classifies one scope: pass scope or markerPathPrefix.",
                    nextCalls: [new("pix_gpu_overview", new { handle })]);
            string[] stages = (evidence is { Length: > 0 } ? evidence : DefaultEvidence).Select(e => RollupTools.Canonical(e, EvidenceSources, "evidence")).Distinct().ToArray();
            if (!stages.Contains("timing")) stages = ["timing", .. stages];
            if (maxDrPixRuns is < 1 or > 8) throw PixErrors.InvalidArguments("maxDrPixRuns must be between 1 and 8.");
            if (CounterPresets.Description(preset).Length == 0)
                throw PixErrors.InvalidArguments($"Unknown preset '{preset}'. Presets: {string.Join(", ", CounterPresets.Names)}.");
            ScopeSelection selection = EventScope.Resolve(session, handle, scope?.QueueIndex, scope, markerPathPrefix);
            var request = new BottleneckRequest(stages, preset, forceStandalone, maxDrPixRuns, scope, markerPathPrefix, includeProvenance);
            Job job = jobs.StartForHandle<GpuCaptureHandle>("bottleneck", $"Classify the bottleneck of a scope in {handle}", handle, (j, h) => Run(session, h, j, selection, request));
            return Json.Serialize(await jobs.WaitOrStatus(job, waitSeconds, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            throw PixErrors.ToMcp(ex, "pix_gpu_bottleneck");
        }
    }

    internal sealed record BottleneckRequest(string[] Stages, string Preset, bool ForceStandalone, int MaxDrPixRuns, EventRef? Scope, string? MarkerPathPrefix, bool IncludeProvenance);

    private static object Run(PixSession session, GpuCaptureHandle h, Job job, ScopeSelection selection, BottleneckRequest request)
    {
        string key = string.Join("|", request.Scope?.QueueIndex, request.Scope?.EventIndex, request.MarkerPathPrefix, string.Join(",", request.Stages.Order(StringComparer.Ordinal)),
            request.Preset, request.ForceStandalone, request.MaxDrPixRuns);
        if (h.BottleneckCache.TryGetValue(key, out BottleneckDto? cached))
        {
            job.AddMessage("Served from the bottleneck cache for this scope and evidence.");
            return cached with { FromCache = true };
        }
        var evidence = new List<BottleneckEvidenceDto>();
        var coverage = new List<BottleneckCoverageDto>();
        bool missing = false;
        (ulong Start, ulong End)? window = null;
        BottleneckTimingDto? timing = null;

        void Stage(string name, Action run)
        {
            if (!request.Stages.Contains(name))
            {
                coverage.Add(new(name, "notRequested", null, []));
                return;
            }
            job.ThrowIfCancellationRequested();
            job.AddMessage($"Bottleneck evidence: {name}...");
            try { run(); }
            catch (OperationCanceledException) { throw; }
            catch (PixToolException ex) when (ex.Detail.Code == "analysis_settings_conflict") { throw; }
            catch (Exception ex)
            {
                missing = true;
                coverage.Add(new(name, PixErrors.ToDto(ex).Code == "unsupported_feature" ? "unsupported" : "failed", PixErrors.Describe(ex), []));
            }
            job.SetPartial(new { partial = true, stage = name, evidence = evidence.ToArray(), coverage = coverage.ToArray() });
        }

        Stage("timing", () =>
        {
            CountersTools.CollectTiming(h, job);
            (timing, window) = TimingEvidence(h, selection, evidence);
            coverage.Add(new("timing", "available", null, []));
        });
        Stage("counters", () =>
        {
            List<CounterInfo> counters = CountersTools.LoadCounters(h, job);
            GpuVendor vendor = h.EffectiveVendor().Vendor;
            uint[] ids = new[] { request.Preset, "pipelineStatistics", "depthOcclusion" }.Distinct()
                .SelectMany(name => CounterPresets.Resolve(vendor, name, counters)?.Ids ?? []).Distinct().Order().ToArray();
            if (ids.Length == 0)
            {
                missing = true;
                coverage.Add(new("counters", "unavailable", $"No counter of this capture matches the {request.Preset}, pipelineStatistics or depthOcclusion presets.",
                    [new ToolCallDto("pix_gpu_counters_list", new { handle = h.Id }, CostHints.Replay)]));
                return;
            }
            CounterEvidence(h, selection, CountersTools.CounterSet(h, ids, job), evidence);
            coverage.Add(new("counters", "available", $"{ids.Length} counter(s) from {request.Preset}, pipelineStatistics and depthOcclusion.", []));
        });
        Stage("occupancy", () =>
        {
            Preparation<GpuCaptureHandle> preparation = CountersTools.OccupancyPreparation(h.Id, request.ForceStandalone);
            if (!preparation.IsReady(h)) preparation.Prepare(h, job);
            if (h.OptionalUnavailable.TryGetValue("occupancy", out object? unavailable))
            {
                missing = true;
                coverage.Add(new("occupancy", "unsupported", Reason(unavailable), []));
                return;
            }
            if (h.OccupancyData is not OccupancyCache cache || window is not { } w)
            {
                missing = true;
                coverage.Add(new("occupancy", "unavailable", window is null ? "The scope has no timed events, so it has no replay window." : "No occupancy data was collected.", []));
                return;
            }
            double? average = null, peak = null;
            foreach (OccupancySeriesSnapshot series in CountersTools.OccupancySnapshots(cache))
            {
                if (series.Points.Length == 0 || SeriesWindow.Clock(series.Points[0].TimeNs, series.Points[^1].TimeNs, w.Start, w.End).State == "mismatch") continue;
                StepWindowStats stats = SeriesWindow.Step(series.Points, w.Start, w.End, series.MaxSlots);
                if (stats.AveragePercent is double a) average = Math.Max(average ?? 0, a);
                if (stats.PeakPercent is double p) peak = Math.Max(peak ?? 0, p);
            }
            if (average is not double averagePercent)
            {
                missing = true;
                coverage.Add(new("occupancy", "withheld", "No occupancy series covers the scope's replay window (clock check mismatch).", []));
                return;
            }
            evidence.Add(new("occupancy", "occupancy.averagePercent", averagePercent, "percent", "medium", "series", "Highest time-weighted average occupancy over the scope window across types and stages."));
            if (peak is double peakPercent) evidence.Add(new("occupancy", "occupancy.peakPercent", peakPercent, "percent", "medium", "series", "Highest peak occupancy over the scope window."));
            coverage.Add(new("occupancy", "available", cache.Source, []));
        });
        Stage("hf", () =>
        {
            Preparation<GpuCaptureHandle> preparation = CountersTools.HfPreparation(h.Id, 0, request.ForceStandalone);
            if (!preparation.IsReady(h)) preparation.Prepare(h, job);
            if (h.OptionalUnavailable.TryGetValue("hf:0", out object? unavailable))
            {
                missing = true;
                coverage.Add(new("hf", "unsupported", Reason(unavailable), []));
                return;
            }
            if (!h.HighFrequencyCollections.TryGetValue(0, out HfCollectionCache? cache) || window is not { } w)
            {
                missing = true;
                coverage.Add(new("hf", "unavailable", window is null ? "The scope has no timed events, so it has no replay window." : "No high-frequency counter set was collected.", []));
                return;
            }
            int added = 0;
            foreach (HfSeriesSnapshot series in CountersTools.HfSnapshots(cache))
                if (SeriesWindow.Samples(series.Samples, w.Start, w.End) is { TimeWeightedAverage: double average } stats)
                {
                    evidence.Add(new("hf", series.Counter, Math.Round(average, 3), series.Unit, "medium", "series", $"Time-weighted average over the scope window ({stats.Coverage} coverage)."));
                    added++;
                }
            coverage.Add(new("hf", added > 0 ? "available" : "withheld", $"{added} counter(s) from set {cache.Set} ({cache.Source}).", []));
        });
        Stage("drpix", () =>
        {
            List<ExperimentInfo> all = DrPixTools.LoadExperiments(h, job);
            string[] names = all.Select(e => (Experiment: e, Rank: Array.FindIndex(DrPixPreference, p => e.Name.Contains(p, StringComparison.OrdinalIgnoreCase))))
                .Where(x => x.Rank >= 0).OrderBy(x => x.Rank).Take(request.MaxDrPixRuns).Select(x => x.Experiment.Name).ToArray();
            if (names.Length == 0)
            {
                missing = true;
                coverage.Add(new("drpix", "unavailable", "None of the 1x1 Viewport, ForceEarlyZ, Quad Histogram, MSAA or primitive efficiency experiments is available.",
                    [new ToolCallDto("pix_gpu_drpix_experiments", new { handle = h.Id }, CostHints.Replay)]));
                return;
            }
            var result = (DrPixResultDto)DrPixTools.RunCore(h, job, new DrPixTools.DrPixRequest(names, null, null, selection, false, 64));
            foreach (DrPixRunDto run in result.Runs)
            {
                if (run.Timing is { SavedPercent: double saved } runTiming)
                    evidence.Add(new("drpix", $"drpix.{run.Experiment}.savedPercent", saved, "percent", "high", "derived",
                        $"{runTiming.Experiment} against {runTiming.Baseline}: {runTiming.BaselineMs} ms to {runTiming.ExperimentMs} ms."));
                if (run.Records.FirstOrDefault(r => r.Name.Equals("Quad Efficiency", StringComparison.OrdinalIgnoreCase)) is { } quad
                    && quad.Values.Values.Select(DrPixMetrics.Number).FirstOrDefault(v => v is not null) is double efficiency)
                    evidence.Add(new("drpix", "drpix.quadEfficiencyPercent", efficiency, "percent", "high", "derived", "Share of 2x2 quads with all four pixels covered."));
            }
            coverage.Add(new("drpix", result.Runs.All(r => r.Succeeded) ? "available" : "partial", $"{result.Runs.Count} experiment run(s): {string.Join(", ", names)}.", []));
        });
        Stage("shaderProfile", () =>
        {
            missing = true;
            coverage.Add(new("shaderProfile", "notRun", "Live shader profiling runs as its own job and its stall shares are not scored yet.",
                [new ToolCallDto("pix_gpu_shader_profile", new { handle = h.Id, scope = request.Scope, markerPathPrefix = request.MarkerPathPrefix }, CostHints.Replay)]));
        });

        VendorIdentity vendorIdentity = h.EffectiveVendor();
        BottleneckClassification classification = BottleneckRules.Classify(evidence, vendorIdentity.Vendor, missing);
        string limiter = classification.Verdict.Limiter;
        string? markerPath = request.Scope is EventRef root && h.AllEvents(root.QueueIndex) is { } rootEvents && root.EventIndex < rootEvents.Length
            ? string.Join("/", EventNavigation.MarkerPath(rootEvents, root.EventIndex).Append(rootEvents[root.EventIndex].Name))
            : request.MarkerPathPrefix;
        BottleneckRecommendationDto[] recommendations = BottleneckRules.Recommendations(limiter, h.Id, request.Scope, request.MarkerPathPrefix);
        string detailRef = session.Results.Store(new { evidence, ruleResults = classification.Results }, jobId: job.Id);
        var notes = new List<string>
        {
            "Heuristic classification from the embedded bottleneck-rules.json; no vendor block is validated on hardware yet, so confidence stays below high.",
            "Counters are aggregated over the scope's work events (percent units EOP-weighted, others summed); PIX marker rounds are reported separately and never summed.",
            "Timing is replay timing on this machine, not application frame latency.",
        };
        notes.AddRange(CompatibilityNotes.Texts("bottleneck", vendorIdentity.Vendor, PixDiscovery.Version));
        var dto = new BottleneckDto(h.Id, selection.DescribeOrNull(h), markerPath, GpuVendors.Name(vendorIdentity.Vendor), request.Stages, timing, classification.Verdict,
            classification.Alternatives, InlineEvidence(evidence), classification.Results.OrderByDescending(r => r.Satisfied).Take(MaxInlineRows).ToArray(),
            BottleneckRules.Implication(limiter), recommendations, coverage,
            new BottleneckRulesInfoDto(BottleneckRules.Default.Version, classification.VendorValidated, BottleneckRules.Default.Validated, classification.SatisfiedIds),
            notes, detailRef, false, recommendations.SelectMany(r => r.NextCalls).Where(ToolRegistry.Accepts).Take(6).ToArray())
        { Provenance = request.IncludeProvenance ? h.Provenance() : null };
        h.BottleneckCache[key] = dto;
        return dto;
    }

    /// <summary>
    /// The inline evidence table: zero counter values and marker rounds equal to their event aggregate carry no signal and stay
    /// in detailRef only; rule-bearing rows (timing, derived, series, Dr. PIX) come first.
    /// </summary>
    internal static BottleneckEvidenceDto[] InlineEvidence(IReadOnlyList<BottleneckEvidenceDto> evidence)
        => evidence
            .Where(e => !(e.Source == "counters" && e.RowKind is "event" or "marker" && e.Value == 0))
            .Where(e => e.RowKind != "marker" || !evidence.Any(o => o.RowKind == "event" && o.Source == e.Source && o.Metric + " (marker round)" == e.Metric && o.Value == e.Value))
            .OrderBy(e => e.RowKind is "derived" or "series" ? 0 : 1)
            .Take(MaxInlineRows)
            .ToArray();

    private static string? Reason(object unavailable)
    {
        JsonElement element = JsonSerializer.SerializeToElement(unavailable, Json.Options);
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty("reason", out JsonElement reason) ? reason.GetString() : null;
    }

    /// <summary>Scope window, busy and idle time, pending share and small-dispatch cost from the scope's timed events.</summary>
    private static (BottleneckTimingDto Timing, (ulong Start, ulong End)? Window) TimingEvidence(GpuCaptureHandle h, ScopeSelection selection, List<BottleneckEvidenceDto> evidence)
    {
        int work = 0, timedWork = 0;
        ulong eop = 0, exec = 0, execEop = 0, dispatchEop = 0, smallEop = 0, smallExec = 0, smallExecEop = 0;
        ulong? windowStart = null, windowEnd = null;
        var leafWindows = new List<(ulong Start, ulong End)>();
        foreach (int q in selection.Queues(h))
        {
            EventRecord[] events = h.AllEvents(q);
            int[] children = h.ChildCounts(q);
            Dictionary<uint, EventTimingRow> rows = h.TimingRowsByQueue.GetValueOrDefault(q, []).GroupBy(r => r.Index).ToDictionary(g => g.Key, g => g.First());
            for (uint i = 0; i < events.Length; i++)
            {
                if (!selection.Contains(q, events, i)) continue;
                bool isWork = Tools.MatchesKind(events[i], "work");
                if (isWork) work++;
                if (!rows.TryGetValue(i, out EventTimingRow? row) || row.EopDuration == GpuCaptureHandle.TimingNone || row.EopStart == GpuCaptureHandle.TimingNone) continue;
                ulong end = row.EopStart + row.EopDuration;
                bool hasTop = row.TopStart != GpuCaptureHandle.TimingNone && row.TopStart <= end;
                ulong start = hasTop ? Math.Min(row.TopStart, row.EopStart) : row.EopStart;
                windowStart = windowStart is ulong ws ? Math.Min(ws, start) : start;
                windowEnd = windowEnd is ulong we ? Math.Max(we, end) : end;
                if (i < children.Length && children[i] == 0) leafWindows.Add((start, end));
                if (!isWork) continue;
                timedWork++;
                eop += row.EopDuration;
                if (hasTop)
                {
                    exec += end - start;
                    execEop += row.EopDuration;
                }
                if (Tools.Classify(events[i], false) != "dispatch") continue;
                dispatchEop += row.EopDuration;
                if (ApiCallParser.Parse(events[i].ApiCallData, events[i].Name) is { WorkItemKind: "threadGroups", WorkItems: long groups } && groups < SmallDispatchThreadGroups)
                {
                    smallEop += row.EopDuration;
                    if (hasTop)
                    {
                        smallExec += end - start;
                        smallExecEop += row.EopDuration;
                    }
                }
            }
        }
        ulong busy = Metrics.UnionLength(leafWindows);
        double? idle = windowStart is ulong s && windowEnd is ulong e && e > s ? Math.Round(100.0 * (e - s - Math.Min(busy, e - s)) / (e - s), 2) : null;
        double? eopShare = exec > 0 ? Math.Round(100.0 * execEop / exec, 2) : null;
        if (idle is double idlePercent)
            evidence.Add(new("timing", "timing.idlePercent", idlePercent, "percent", "high", "derived", "Share of the scope window (first TOP to last EOP) not covered by timed leaf work."));
        if (eopShare is double share)
            evidence.Add(new("timing", "timing.eopShareOfExecPercent", share, "percent", "high", "derived", "Summed EOP over summed TOP-to-EOP time of timed work events."));
        if (dispatchEop > 0)
            evidence.Add(new("timing", "timing.smallDispatchEopPercent", Math.Round(100.0 * smallEop / dispatchEop, 2), "percent", "high", "derived",
                $"Share of dispatch EOP spent in dispatches under {SmallDispatchThreadGroups} thread groups."));
        if (smallExecEop > 0)
            evidence.Add(new("timing", "timing.smallDispatchExecToEop", Math.Round((double)smallExec / smallExecEop, 3), "ratio", "high", "derived",
                "TOP-to-EOP time over EOP time for those small dispatches."));
        TimingTreeNode? rootNode = selection.Root is EventRef root && h.TimingTreeFor(root.QueueIndex).Nodes is { } nodes && root.EventIndex < nodes.Length ? nodes[root.EventIndex] : null;
        var dto = new BottleneckTimingDto(work, timedWork, Metrics.Ms(eop), exec > 0 ? Metrics.Ms(exec) : null,
            windowStart is ulong a && windowEnd is ulong b ? Metrics.Ms(b - a) : null, windowStart is null ? null : Metrics.Ms(busy), idle, eopShare,
            rootNode is { IsTimed: true } ? Metrics.Ms(rootNode.InclusiveEopNs) : null, rootNode?.Semantics);
        return (dto, windowStart is ulong first && windowEnd is ulong last ? (first, last) : null);
    }

    /// <summary>Per counter over the scope's work events: EOP-weighted mean for percent units, sum otherwise; marker rounds separately; derived D3D ratios.</summary>
    private static void CounterEvidence(GpuCaptureHandle h, ScopeSelection selection, CounterCollectionCache cache, List<BottleneckEvidenceDto> evidence)
    {
        int n = cache.Counters.Length;
        var sums = new double[n];
        var weighted = new double[n];
        var weights = new double[n];
        var counts = new int[n];
        var markerRounds = new double?[n];
        foreach (int q in selection.Queues(h))
        {
            EventRecord[] events = h.AllEvents(q);
            Dictionary<uint, ulong> eops = h.TimingRowsByQueue.GetValueOrDefault(q, []).Where(r => r.EopDuration != GpuCaptureHandle.TimingNone)
                .GroupBy(r => r.Index).ToDictionary(g => g.Key, g => g.First().EopDuration);
            foreach (CounterEventRow row in CountersTools.CachedCounterRows(h, cache, q))
            {
                uint i = row.Event.Index;
                if (!row.HasData || !selection.Contains(q, events, i)) continue;
                bool isRoot = selection.Root is EventRef root && root.QueueIndex == q && root.EventIndex == i;
                bool isWork = Tools.MatchesKind(row.Event, "work");
                for (int c = 0; c < n; c++)
                {
                    if (CounterNormalization.ToDouble(row.Values[c]) is not double value) continue;
                    if (isRoot) markerRounds[c] = value;
                    if (!isWork) continue;
                    sums[c] += value;
                    counts[c]++;
                    double weight = eops.TryGetValue(i, out ulong eop) ? eop : 0;
                    weighted[c] += value * weight;
                    weights[c] += weight;
                }
            }
        }
        var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        for (int c = 0; c < n; c++)
        {
            CounterInfo counter = cache.Counters[c];
            if (counts[c] > 0)
            {
                bool percent = counter.Unit.Unit == "percent";
                double value = percent ? (weights[c] > 0 ? weighted[c] / weights[c] : sums[c] / counts[c]) : sums[c];
                totals[counter.Name] = value;
                evidence.Add(new("counters", counter.Name, Math.Round(value, 3), counter.Unit.Unit, counter.Unit.UnitConfidence, "event",
                    percent ? $"EOP-weighted mean over {counts[c]} work event(s)." : $"Sum over {counts[c]} work event(s)."));
            }
            if (markerRounds[c] is double marker)
                evidence.Add(new("counters", counter.Name + " (marker round)", Math.Round(marker, 3), counter.Unit.Unit, counter.Unit.UnitConfidence, "marker",
                    "PIX's own round for the scope's marker; never summed from its children."));
        }
        double? Get(string name) => totals.TryGetValue(name, out double value) ? value : null;
        if (Get("Samples Submitted") is double submitted && submitted > 0 && Get("Samples Rejected") is double rejected)
            evidence.Add(new("counters", "counters.depthRejectedPercent", Math.Round(100 * rejected / submitted, 2), "percent", "high", "derived", "Samples Rejected over Samples Submitted."));
        if (Get("IA Primitives") is double primitives && primitives > 0 && Get("PS Invocations") is double invocations)
            evidence.Add(new("counters", "counters.psInvocationsPerPrimitive", Math.Round(invocations / primitives, 3), "ratio", "high", "derived", "PS Invocations over IA Primitives."));
        if (Get("Clipper Primitives In") is double clipIn && clipIn > 0 && Get("Clipper Primitives Out") is double clipOut)
            evidence.Add(new("counters", "counters.clippedPercent", Math.Round(100 * (1 - clipOut / clipIn), 2), "percent", "high", "derived", "Primitives removed by clipping."));
    }
}
