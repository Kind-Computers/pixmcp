using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using PixMcp.Pix.Handles;

namespace PixMcp.Tools;

public enum ComparisonSection { timings, shaders, pipeline, resources }

[McpServerToolType]
public static class InvestigationTools
{
    [McpServerTool(Name = "pix_gpu_overview", Title = "GPU capture overview", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Replays the capture on the local GPU if analysis is not started. Start a GPU investigation: capture queues, event kinds, capabilities and the most expensive marker passes and draw/dispatch events. Timing is prepared as a job by default; includeTiming=false only reads metadata. Counters and Dr. PIX are never run automatically. Nested passes overlap; do not sum their costs into frame latency.")]
    public static Task<string> Overview(PixSession session, JobManager jobs, [Description("GPU capture handle (from pix_gpu_open).")] string handle,
        [Description("Collect replay timing for totals, top passes and top draws (default true); false answers from metadata only.")] bool includeTiming = true, [Description("Top passes and top draws to rank (default 10, max 1000).")] int limit = 10,
        [Description(Tools.ReadyWaitDescription)] double waitSeconds = Tools.DefaultReadyWaitSeconds,
        [Description(EventScope.Description + " Restricts topPasses and topDraws.")] EventRef? scope = null,
        [Description(EventScope.PrefixDescription)] string? markerPathPrefix = null,
        [Description(Shaping.BriefDescription + " Trims marker paths and drops execution durations.")] bool brief = false,
        [Description(Shaping.MaxStringLengthDescription)] int? maxStringLength = null,
        [Description(Tools.IncludeProvenanceDescription)] bool includeProvenance = false,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 1000) throw new PixToolException(PixErrors.Codes.InvalidArguments, "limit must be between 1 and 1000.");
        ScopeSelection selection = EventScope.Resolve(session, handle, null, scope, markerPathPrefix);
        ShapingOptions shaping = Shaping.Options("objects", brief, null, maxStringLength, 0);
        if (!includeTiming) return Tools.Run(session, "pix_gpu_overview", () => Present(QueryOverview(session.Get<GpuCaptureHandle>(handle), false, limit, selection), shaping), cancellationToken);
        return Tools.RunWhenReadyOrPartial(session, jobs, "pix_gpu_overview", handle, CountersTools.TimingPreparation(handle),
            (h, onWorker) => QueryOverview(h, false, limit, selection, onWorker),
            h => Present(QueryOverview(h, true, limit, selection), shaping),
            (partial, section) => partial is CaptureOverviewDto metadata ? Present(metadata with { Timing = section, NextCalls = section.NextCalls }, shaping) : null,
            waitSeconds, cancellationToken);
    }

    /// <summary>Applies brief (marker paths trimmed to the last two segments, execution durations dropped) and string truncation.</summary>
    internal static object Present(CaptureOverviewDto overview, ShapingOptions shaping)
    {
        if (shaping.Brief)
        {
            static EventMetricDto Trim(EventMetricDto m) => m with { MarkerPath = m.MarkerPath.Count > 2 ? m.MarkerPath.Skip(m.MarkerPath.Count - 2).ToArray() : m.MarkerPath, Exec = null };
            overview = overview with { TopPasses = overview.TopPasses.Select(Trim).ToArray(), TopDraws = overview.TopDraws.Select(Trim).ToArray() };
        }
        return RowShapes.Finish(overview, shaping);
    }

    /// <summary>
    /// Builds the overview. With <paramref name="onWorker"/> false (a caller joining a running timing job) only cached event
    /// arrays are read; a queue whose events are not cached yet reports empty kinds.
    /// </summary>
    internal static CaptureOverviewDto QueryOverview(GpuCaptureHandle h, bool timing, int limit, ScopeSelection? selection = null, bool onWorker = true)
    {
        selection ??= new ScopeSelection(h.Id, null, null, null, null);
        var queues = new List<QueueOverviewDto>();
        var passes = new List<EventMetricDto>(); var draws = new List<EventMetricDto>();
        foreach (QueueEntry queue in h.Queues)
        {
            EventRecord[]? cached = onWorker ? h.AllEvents(queue.Index) : h.CachedEvents(queue.Index);
            if (cached is null)
            {
                queues.Add(new(queue.Index, queue.Name, Json.EnumName(queue.Type), queue.EventCount, new Dictionary<string, int>(), null));
                continue;
            }
            EventRecord[] events = cached;
            int[] childCounts = onWorker ? h.ChildCounts(queue.Index) : EventNavigation.ChildCounts(events);
            var kinds = events.GroupBy(e => Tools.Classify(e, childCounts[e.Index] > 0)).ToDictionary(g => g.Key, g => g.Count());
            QueueTotals? totals = timing ? h.QueueTotals(queue.Index) : null;
            queues.Add(new(queue.Index, queue.Name, Json.EnumName(queue.Type), queue.EventCount, kinds, totals));
            if (!timing) continue;
            foreach (TimingTreeNode node in h.TimingTreeNodes(queue.Index))
                if (selection.Contains(h, queue.Index, node.Index) && IsPass(node, events[node.Index], childCounts[node.Index] > 0))
                    passes.Add(new(new(h.Id, queue.Index, node.Index), EventNavigation.MarkerPath(events, node.Index), node.Name, "marker",
                        node.Semantics, Metrics.Duration(node.InclusiveEopNs, totals), node.ExecutionNs is ulong exec ? Metrics.Duration(exec, totals) : null));
            foreach (EventTimingRow row in h.TimingRowsByQueue.GetValueOrDefault(queue.Index, []))
                if (row.EopDuration != GpuCaptureHandle.TimingNone && row.Index < events.Length && selection.Contains(h, queue.Index, row.Index)
                    && Tools.MatchesKind(events[row.Index], "work"))
                {
                    TimingEventDto dto = CountersTools.TimingRowDto(h, row);
                    draws.Add(new(dto.EventRef, dto.MarkerPath, dto.Name, dto.Kind, TimingSemantics.Measured, dto.Eop, dto.Exec));
                }
        }
        EventMetricDto[] topPasses = Rank(passes, limit), topDraws = Rank(draws, limit);
        var nextCalls = new List<ToolCallDto> { new("pix_gpu_events", new { handle = h.Id, kind = "work", limit, scope = selection.Root, markerPathPrefix = selection.MarkerPathPrefix }) };
        if (timing && topPasses.Length > 0)
            nextCalls.Add(new("pix_gpu_timing_tree", new { handle = h.Id, scope = topPasses[0].EventRef, sortBy = "self" }));
        if (timing) nextCalls.Add(new("pix_gpu_timing_events", new { handle = h.Id, sortBy = "eopDuration", limit }));
        return new(h.Id, queues, h.CapabilitiesSnapshot(), timing ? Metrics.Denominators : null, timing ? h.Provenance() : null,
            topPasses, topDraws, nextCalls) { Scope = selection.DescribeOrNull(h), Vendor = h.CachedCaptureVendor };
    }

    /// <summary>A pass is a marker that carries timing itself or through its descendants; timed leaf labels are not passes.</summary>
    internal static bool IsPass(TimingTreeNode node, EventRecord record, bool hasChildren)
        => Tools.Classify(record, hasChildren) == "marker" && node.IsTimed;

    private static EventMetricDto[] Rank(List<EventMetricDto> items, int limit)
        => items.OrderByDescending(p => p.Eop.Ns).ThenBy(p => p.EventRef.QueueIndex).ThenBy(p => p.EventRef.EventIndex).Take(limit)
            .Select((p, i) => p with { Eop = p.Eop with { Rank = i + 1 } }).ToArray();

    [McpServerTool(Name = "pix_gpu_compare", Title = "Compare captures", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description("Compare two GPU captures as a job. Timings are the default; optionally inspect shaders, pipeline state and resource descriptions. Matches unique queue names/types and exact marker paths; ambiguous events require explicit pairs. Baseline data is copied before stopping its analysis to replay the candidate. Results retain replay settings and never claim application frame latency.")]
    public static Task<string> Compare(PixSession session, JobManager jobs, [Description("GPU capture handle of the baseline.")] string baselineHandle, [Description("GPU capture handle of the candidate (may equal the baseline when scopes differ).")] string candidateHandle,
        [Description("Sections to compare: timings (default), shaders, pipeline, resources.")] ComparisonSection[]? sections = null, [Description("Explicit baseline-to-candidate queue pairs; default matches queues by type and name.")] QueuePair[]? queuePairs = null, [Description("Explicit baseline-to-candidate event pairs; default matches events by marker path and name.")] EventPair[]? eventPairs = null,
        [Description("Changes and ambiguities to include inline (default 10, max 1000); the full lists are in fullResultRef.")] int limit = 10, [Description(Tools.WaitSecondsDescription)] double waitSeconds = 0,
        [Description("Stop the baseline's GPU analysis after its snapshot so the candidate can replay (default true). false fails fast with analysis_active when the baseline analysis is started and a different candidate must replay.")] bool stopBaselineAnalysis = true,
        CancellationToken cancellationToken = default)
    {
        var baseline = session.Get<GpuCaptureHandle>(baselineHandle);
        var candidate = session.Get<GpuCaptureHandle>(candidateHandle);
        if (!stopBaselineAnalysis && baselineHandle != candidateHandle && baseline.AnalysisStarted)
            throw PixErrors.AnalysisActive(baselineHandle, $"Analysis is started on {baselineHandle}; replaying {candidateHandle} needs it stopped. Pass stopBaselineAnalysis=true (default) or stop it first.");
        ComparisonSection[] selected = sections is { Length: > 0 } ? sections.Distinct().ToArray() : [ComparisonSection.timings];
        if (selected.Any(s => !Enum.IsDefined(s))) throw PixErrors.InvalidArguments($"Unknown comparison section. Valid sections: {string.Join(", ", Enum.GetNames<ComparisonSection>())}.");
        if (limit is < 1 or > 1000) throw PixErrors.InvalidArguments("limit must be between 1 and 1000.");
        foreach (QueuePair pair in queuePairs ?? []) { baseline.Queue(pair.BaselineQueueIndex); candidate.Queue(pair.CandidateQueueIndex); }
        if ((queuePairs ?? []).Select(p => p.BaselineQueueIndex).Distinct().Count() != (queuePairs?.Length ?? 0)
            || (queuePairs ?? []).Select(p => p.CandidateQueueIndex).Distinct().Count() != (queuePairs?.Length ?? 0))
            throw PixErrors.InvalidArguments("Explicit queue pairs must be one-to-one.");
        foreach (EventPair pair in eventPairs ?? []) { Validate(pair.Baseline, baseline); Validate(pair.Candidate, candidate); }
        if ((eventPairs ?? []).Select(p => p.Baseline).Distinct().Count() != (eventPairs?.Length ?? 0)
            || (eventPairs ?? []).Select(p => p.Candidate).Distinct().Count() != (eventPairs?.Length ?? 0))
            throw PixErrors.InvalidArguments("Explicit event pairs must be one-to-one.");
        return Tools.RunJob(jobs, "pix_gpu_compare", () => jobs.Start("capture-compare", $"Compare {baselineHandle} with {candidateHandle}", job =>
        {
            var a = Snapshot(session.Get<GpuCaptureHandle>(baselineHandle), selected, job);
            ComparisonSnapshot b;
            if (baselineHandle == candidateHandle) b = a;
            else
            {
                var warnings = new List<string>();
                if (stopBaselineAnalysis) baseline.StopAnalysis(warnings);
                a = a with { Coverage = a.Coverage.Concat(new object[] { new { handle = baselineHandle,
                    analysisStopped = true, reason = "Snapshot complete; replaying candidate sequentially", warnings } }).ToArray() };
                job.ThrowIfCancellationRequested();
                b = Snapshot(session.Get<GpuCaptureHandle>(candidateHandle), selected, job);
            }
            ComparisonResultDto full = CaptureComparison.Compare(a, b, queuePairs, eventPairs);
            string resultRef = session.Results.Store(full, jobId: job.Id);
            return new ComparisonSummaryDto(baselineHandle, candidateHandle, full.MatchedCount, full.Items.Count,
                full.BaselineOnly.Count, full.CandidateOnly.Count, full.Ambiguous.Count, full.Items.Take(limit).ToArray(),
                full.Ambiguous.Take(limit).ToArray(), a.Provenance, b.Provenance, resultRef,
                [ResultStore.ReadCall(resultRef, "/items", 0, limit), ResultStore.ReadCall(resultRef, "/ambiguous", 0, limit),
                 ResultStore.ReadCall(resultRef, "/baselineOnly", 0, limit), ResultStore.ReadCall(resultRef, "/candidateOnly", 0, limit)], full.Coverage);
        }), waitSeconds, cancellationToken);

        static void Validate(EventRef reference, GpuCaptureHandle handle)
        {
            if (reference.Handle != handle.Id || reference.EventIndex >= handle.Queue(reference.QueueIndex).EventCount)
                throw PixErrors.InvalidReference($"Explicit event reference {reference.Handle}[{reference.QueueIndex}:{reference.EventIndex}] is outside its comparison capture {handle.Id}.",
                    new ToolCallDto("pix_gpu_events", new { handle = handle.Id, queueIndex = reference.QueueIndex }, CostHints.Query));
        }
    }

    private static ComparisonSnapshot Snapshot(GpuCaptureHandle h, ComparisonSection[] sections, Job job)
    {
        bool timing = sections.Contains(ComparisonSection.timings);
        if (timing) CountersTools.CollectTiming(h, job);
        if (sections.Any(s => s != ComparisonSection.timings)) h.EnsureAnalysisStarted(job);
        if (sections.Contains(ComparisonSection.resources)) h.EnsureAccessedResources(job);
        var queues = new List<ComparisonQueue>(); var coverage = new List<object>();
        foreach (QueueEntry queue in h.Queues)
        {
            var events = new List<ComparisonEvent>();
            TimingTreeNode[]? nodes = timing ? h.TimingTreeNodes(queue.Index) : null;
            int[] childCounts = h.ChildCounts(queue.Index);
            foreach (EventRecord record in h.AllEvents(queue.Index))
            {
                job.ThrowIfCancellationRequested();
                var reference = new EventRef(h.Id, queue.Index, record.Index);
                var data = new Dictionary<string, JsonElement>(); string? shaderKey = null;
                if (Tools.MatchesKind(record, "work"))
                {
                    if (sections.Contains(ComparisonSection.shaders))
                        Read("shaders", () =>
                        {
                            var shaders = PipelineTools.ReadShaders(h, reference);
                            shaderKey = shaders.Count > 0 && shaders.All(s => s.Hash is not null) ? string.Join(";", shaders.Select(s => s.Stage + ":" + s.Hash).Order()) : null;
                            return CaptureComparison.NormalizeSet(shaders.Select(s => new { s.Stage, s.Hash, s.Entry, s.Target, s.Defines, s.Flags }));
                        });
                    if (sections.Contains(ComparisonSection.pipeline))
                        Read("pipeline", () =>
                        {
                            var pipeline = PipelineTools.QueryPipelineState(h, reference, includeShaders: false);
                            return new { pipeline.ProgramType, pipeline.RootSignature, pipeline.GenericPipeline, pipeline.RaytracingPipeline };
                        });
                    if (sections.Contains(ComparisonSection.resources))
                        Read("resources", () =>
                        {
                            var resources = new List<object>();
                            int offset = 0;
                            do
                            {
                                job.ThrowIfCancellationRequested();
                                var page = ResourceTools.QueryEventResources(h, reference, viewOffset: offset, viewLimit: 1000);
                                if (offset == 0)
                                {
                                    if (page.RootConstantCoverage.Count > 0)
                                        coverage.Add(new { eventRef = reference, section = "rootConstants", unavailable = true, detail = page.RootConstantCoverage });
                                    else data["rootConstants"] = JsonSerializer.SerializeToElement(CaptureComparison.NormalizeSet(page.RootConstants), Json.Options);
                                }
                                resources.AddRange(page.Resources.SelectMany(r => r.Views.Select(view => (object)new { r.Resource, view })));
                                resources.AddRange(page.OtherViews);
                                if (!page.NextViewOffset.HasValue) break;
                                offset = page.NextViewOffset.Value;
                            } while (true);
                            return CaptureComparison.NormalizeSet(resources);
                        });
                }
                TimingTreeNode? node = nodes?[record.Index];
                string kind = Tools.Classify(record, childCounts[record.Index] > 0);
                bool timed = node is not null && node.IsTimed;
                events.Add(new(reference, EventNavigation.MarkerPath(h.AllEvents(queue.Index), record.Index), record.Name, kind,
                    kind == "marker", timed ? node!.InclusiveEopNs : null, timed ? node!.Semantics : TimingSemantics.Untimed, shaderKey, data));

                void Read(string section, Func<object> query)
                {
                    try
                    {
                        JsonElement value = CaptureComparison.Normalize(query());
                        var unavailable = MissingFields(value).ToArray();
                        if (unavailable.Length == 0) data[section] = value;
                        else coverage.Add(new { eventRef = reference, section, unavailable = true,
                            reason = "Section omitted from comparison because native details are incomplete.", fields = unavailable });
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { coverage.Add(new { eventRef = reference, section, unavailable = true, reason = PixErrors.Describe(ex) }); }
                }
            }
            queues.Add(new(queue.Index, queue.Name, Json.EnumName(queue.Type), events.ToArray()));
        }
        return new(h.Id, queues.ToArray(), h.Provenance(), coverage);
    }

    private static IEnumerable<object> MissingFields(JsonElement value, string pointer = "")
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("unavailable", out var missing) && missing.ValueKind == JsonValueKind.True)
            {
                yield return new { pointer, detail = value };
                yield break;
            }
            foreach (var property in value.EnumerateObject())
                foreach (var item in MissingFields(property.Value, pointer + "/" + property.Name.Replace("~", "~0").Replace("/", "~1")))
                    yield return item;
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (var child in value.EnumerateArray())
            {
                foreach (var item in MissingFields(child, pointer + "/" + index)) yield return item;
                index++;
            }
        }
    }
}
