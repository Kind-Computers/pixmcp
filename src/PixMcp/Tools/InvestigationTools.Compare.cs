using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using PixMcp.Pix.Handles;

namespace PixMcp.Tools;

public static partial class InvestigationTools
{
    private const long MaxHlslBytes = 64L * 1024 * 1024;

    [McpServerTool(Name = "pix_gpu_compare", Title = "Compare captures", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description("Compare two GPU captures, or two scopes or frames of one capture, as a job. Timings are the default; optionally shaders (with HLSL line diffs when a hash changes), pipeline state and resource bindings. Matches unique queue names/types and exact marker paths, optionally by order within a path; ambiguous events require explicit pairs. The summary adds busy totals, deltas rolled up by marker path, a noise floor from repeated replays, provenance mismatch warnings and code diffs. Baseline data is copied before stopping its analysis to replay the candidate. Results never claim application frame latency.")]
    public static Task<string> Compare(PixSession session, JobManager jobs,
        [Description("GPU capture handle of the baseline.")] string baselineHandle,
        [Description("GPU capture handle of the candidate (may equal the baseline when scopes or frames differ).")] string candidateHandle,
        [Description("Sections to compare: timings (default), shaders, pipeline, resources.")] ComparisonSection[]? sections = null,
        [Description("Explicit baseline-to-candidate queue pairs; default matches queues by type and name.")] QueuePair[]? queuePairs = null,
        [Description("Explicit baseline-to-candidate event pairs; default matches events by marker path and name.")] EventPair[]? eventPairs = null,
        [Description("Changes and ambiguities to include inline (default 10, max 1000); the full lists are in fullResultRef.")] int limit = 10,
        [Description(Tools.WaitSecondsDescription)] double waitSeconds = 0,
        [Description("Stop the baseline's GPU analysis after its snapshot so the candidate can replay (default true). false fails fast with analysis_active when the baseline analysis is started and a different candidate must replay.")] bool stopBaselineAnalysis = true,
        [Description("Baseline side only: compare this event and its descendants; marker paths are taken relative to it.")] EventRef? baselineScope = null,
        [Description("Candidate side only: compare this event and its descendants; marker paths are taken relative to it.")] EventRef? candidateScope = null,
        [Description(EventScope.PrefixDescription + " Applies to both sides.")] string? markerPathPrefix = null,
        [Description("Baseline side only: compare one Present-delimited frame (0-based).")] int? baselineFrame = null,
        [Description("Candidate side only: compare one Present-delimited frame (0-based).")] int? candidateFrame = null,
        [Description("Deepest marker-path level for byMarkerPath (default 2, max 8).")] int rollupDepth = 2,
        [Description("Pair events that share a marker path by order when both sides have the same count (default true; such pairs get confidence low).")] bool ordinalMatching = true,
        [Description("Compare descriptor-heap indices and binding evidence sources too (default false; they change without a behaviour change).")] bool includeDescriptorHeapIndices = false,
        [Description("Diff HLSL line by line for matched events whose shader hash changed (default true; needs the shaders section).")] bool shaderCodeDiff = true,
        [Description("Timing collections per side (default 1, max 5); 2 or more give median EOP, spreads and a noise floor.")] int repeats = 1,
        CancellationToken cancellationToken = default)
    {
        var baseline = session.Get<GpuCaptureHandle>(baselineHandle);
        var candidate = session.Get<GpuCaptureHandle>(candidateHandle);
        bool sameHandle = baselineHandle == candidateHandle;
        if (sameHandle && Equals(baselineScope, candidateScope) && baselineFrame == candidateFrame)
            throw PixErrors.InvalidArguments("Comparing a capture with itself needs different baselineScope and candidateScope, or different baselineFrame and candidateFrame.",
                [new ToolCallDto("pix_gpu_overview", new { handle = baselineHandle }, CostHints.Cached)]);
        if (!stopBaselineAnalysis && !sameHandle && baseline.AnalysisStarted)
            throw PixErrors.AnalysisActive(baselineHandle, $"Analysis is started on {baselineHandle}; replaying {candidateHandle} needs it stopped. Pass stopBaselineAnalysis=true (default) or stop it first.");
        ComparisonSection[] selected = sections is { Length: > 0 } ? sections.Distinct().ToArray() : [ComparisonSection.timings];
        if (selected.Any(s => !Enum.IsDefined(s))) throw PixErrors.InvalidArguments($"Unknown comparison section. Valid sections: {string.Join(", ", Enum.GetNames<ComparisonSection>())}.");
        if (limit is < 1 or > 1000) throw PixErrors.InvalidArguments("limit must be between 1 and 1000.");
        if (rollupDepth is < 1 or > 8) throw PixErrors.InvalidArguments("rollupDepth must be between 1 and 8.");
        if (repeats is < 1 or > 5) throw PixErrors.InvalidArguments("repeats must be between 1 and 5.");
        if (baselineFrame < 0 || candidateFrame < 0) throw PixErrors.InvalidArguments("Frame indexes must be nonnegative.");
        foreach (QueuePair pair in queuePairs ?? []) { baseline.Queue(pair.BaselineQueueIndex); candidate.Queue(pair.CandidateQueueIndex); }
        if ((queuePairs ?? []).Select(p => p.BaselineQueueIndex).Distinct().Count() != (queuePairs?.Length ?? 0)
            || (queuePairs ?? []).Select(p => p.CandidateQueueIndex).Distinct().Count() != (queuePairs?.Length ?? 0))
            throw PixErrors.InvalidArguments("Explicit queue pairs must be one-to-one.");
        foreach (EventPair pair in eventPairs ?? []) { Validate(pair.Baseline, baseline); Validate(pair.Candidate, candidate); }
        if ((eventPairs ?? []).Select(p => p.Baseline).Distinct().Count() != (eventPairs?.Length ?? 0)
            || (eventPairs ?? []).Select(p => p.Candidate).Distinct().Count() != (eventPairs?.Length ?? 0))
            throw PixErrors.InvalidArguments("Explicit event pairs must be one-to-one.");
        var sideA = new CompareSide(Side(session, baselineHandle, baselineScope, markerPathPrefix), baselineScope, baselineFrame);
        var sideB = new CompareSide(Side(session, candidateHandle, candidateScope, markerPathPrefix), candidateScope, candidateFrame);
        var snapshotOptions = new SnapshotOptions(selected, includeDescriptorHeapIndices, shaderCodeDiff && selected.Contains(ComparisonSection.shaders), repeats);
        var echo = new ComparisonOptionsDto(baselineScope, candidateScope, markerPathPrefix, baselineFrame, candidateFrame, rollupDepth, ordinalMatching,
            includeDescriptorHeapIndices, shaderCodeDiff, repeats);
        return Tools.RunJob(jobs, "pix_gpu_compare", () => jobs.Start("capture-compare", $"Compare {baselineHandle} with {candidateHandle}", job =>
        {
            ComparisonSnapshot a, b;
            string method;
            if (sameHandle)
            {
                RawSnapshot raw = Collect(session.Get<GpuCaptureHandle>(baselineHandle), snapshotOptions, job);
                (a, b, method) = (Filter(raw, sideA), Filter(raw, sideB), raw.RepeatMethod);
            }
            else
            {
                RawSnapshot rawA = Collect(session.Get<GpuCaptureHandle>(baselineHandle), snapshotOptions, job);
                a = Filter(rawA, sideA);
                var warnings = new List<string>();
                if (stopBaselineAnalysis) baseline.StopAnalysis(warnings);
                a = a with { Coverage = a.Coverage.Concat(new object[] { new { handle = baselineHandle,
                    analysisStopped = stopBaselineAnalysis, reason = "Snapshot complete; replaying candidate sequentially", warnings } }).ToArray() };
                job.ThrowIfCancellationRequested();
                RawSnapshot rawB = Collect(session.Get<GpuCaptureHandle>(candidateHandle), snapshotOptions, job);
                b = Filter(rawB, sideB);
                method = rawA.RepeatMethod == rawB.RepeatMethod ? rawA.RepeatMethod : rawA.RepeatMethod + "/" + rawB.RepeatMethod;
            }
            var options = new ComparisonOptions(ordinalMatching, includeDescriptorHeapIndices, rollupDepth, limit, snapshotOptions.CodeDiff, sameHandle, repeats, method);
            ComparisonResultDto full = CaptureComparison.Compare(a, b, queuePairs, eventPairs, options) with { Options = echo };
            string resultRef = session.Results.Store(full, jobId: job.Id);
            var calls = new List<ToolCallDto>
            {
                new("pix_gpu_compare_changes", new { fullResultRef = resultRef, direction = "regressions", excludeBelowNoise = repeats >= 2 }, CostHints.Cached),
                ResultStore.ReadCall(resultRef, "/items", 0, limit), ResultStore.ReadCall(resultRef, "/byMarkerPath", 0, 25),
                ResultStore.ReadCall(resultRef, "/ambiguous", 0, limit), ResultStore.ReadCall(resultRef, "/baselineOnly", 0, limit),
                ResultStore.ReadCall(resultRef, "/candidateOnly", 0, limit),
            };
            if (full.CodeDiffs is { Count: > 0 }) calls.Add(ResultStore.ReadCall(resultRef, "/codeDiffs", 0, 25));
            if (full.ProvenanceMismatch) calls.Add(new ToolCallDto("pix_gpu_analysis_adapters", new { handle = candidateHandle }, CostHints.Cached));
            return new ComparisonSummaryDto(baselineHandle, candidateHandle, full.MatchedCount, full.Items.Count,
                full.BaselineOnly.Count, full.CandidateOnly.Count, full.Ambiguous.Count, full.Items.Take(limit).ToArray(),
                full.Ambiguous.Take(limit).ToArray(), a.Provenance, b.Provenance, resultRef, calls, full.Coverage)
            {
                Totals = full.Totals, ByMarkerPath = ComparisonRollup.Inline(full.ByMarkerPath ?? [], ComparisonRollup.InlineMarkerPaths), Noise = full.Noise,
                Warnings = full.Warnings, ProvenanceMismatch = full.ProvenanceMismatch, CodeDiffs = full.CodeDiffs?.Take(ComparisonRollup.InlineCodeDiffs).ToArray(), Options = echo,
            };
        }), waitSeconds, cancellationToken);

        static void Validate(EventRef reference, GpuCaptureHandle handle)
        {
            if (reference.Handle != handle.Id || reference.EventIndex >= handle.Queue(reference.QueueIndex).EventCount)
                throw PixErrors.InvalidReference($"Explicit event reference {reference.Handle}[{reference.QueueIndex}:{reference.EventIndex}] is outside its comparison capture {handle.Id}.",
                    new ToolCallDto("pix_gpu_events", new { handle = handle.Id, queueIndex = reference.QueueIndex }, CostHints.Query));
        }
    }

    private sealed record SnapshotOptions(ComparisonSection[] Sections, bool IncludeSoftFields, bool CodeDiff, int Repeats);
    private sealed record CompareSide(ScopeSelection Selection, EventRef? Root, int? Frame);
    private sealed record RawQueue(QueueEntry Queue, EventRecord[] Events, int[] ChildCounts, ComparisonEvent[] All, EventTimingRow[] Rows);
    private sealed record RawSnapshot(string Handle, RawQueue[] Queues, ReplayProvenance Provenance, IReadOnlyList<object> Coverage,
        IReadOnlyDictionary<string, string> Hlsl, FrameTable Frames, string RepeatMethod);

    private static ScopeSelection Side(PixSession session, string handle, EventRef? scope, string? markerPathPrefix)
    {
        if (scope is not null && scope.Handle != handle)
            throw PixErrors.InvalidReference($"Scope {scope.Handle}[{scope.QueueIndex}:{scope.EventIndex}] does not belong to {handle}.",
                new ToolCallDto("pix_handles", new { }, CostHints.Cached));
        return EventScope.Resolve(session, handle, scope?.QueueIndex, scope, markerPathPrefix);
    }

    /// <summary>
    /// Every event of the capture with timing (median and spread over repeats), the requested sections, bound shader hashes and
    /// their HLSL, plus the frame table; scopes and frames are applied afterwards so one collection serves both sides of a
    /// same-handle comparison.
    /// </summary>
    private static RawSnapshot Collect(GpuCaptureHandle h, SnapshotOptions options, Job job)
    {
        ComparisonSection[] sections = options.Sections;
        bool timing = sections.Contains(ComparisonSection.timings);
        var samples = new Dictionary<(int Queue, uint Index), List<ulong>>();
        string method = "single";
        if (timing)
        {
            Dictionary<int, EventTimingRow[]>? previous = null;
            for (int r = 0; r < options.Repeats; r++)
            {
                if (r == 0) CountersTools.CollectTiming(h, job);
                else
                {
                    job.ThrowIfCancellationRequested();
                    job.AddMessage($"Collecting timing for {h.Id} again ({r + 1}/{options.Repeats})...");
                    bool restart = method == "restart";
                    if (!restart)
                    {
                        method = "recollect";
                        try
                        {
                            ResetTiming(h, restart: false, job);
                            CountersTools.CollectTiming(h, job);
                            if (previous is not null && SameTimestamps(previous, h.TimingRowsByQueue))
                            {
                                job.AddMessage("The second collection repeated the first timestamps; restarting analysis to measure replay variance.");
                                restart = true;
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            job.AddMessage("Collecting timing again on the running analysis failed (" + PixErrors.Describe(ex) + "); restarting analysis.");
                            restart = true;
                        }
                    }
                    if (restart)
                    {
                        method = "restart";
                        ResetTiming(h, restart: true, job);
                        CountersTools.CollectTiming(h, job);
                    }
                }
                previous = h.TimingRowsByQueue.ToDictionary(kv => kv.Key, kv => kv.Value);
                foreach (QueueEntry queue in h.Queues)
                {
                    TimingTreeNode[] nodes = h.TimingTreeNodes(queue.Index);
                    for (uint i = 0; i < nodes.Length; i++)
                    {
                        if (!nodes[i].IsTimed) continue;
                        if (!samples.TryGetValue((queue.Index, i), out List<ulong>? list)) samples[(queue.Index, i)] = list = [];
                        list.Add(nodes[i].InclusiveEopNs);
                    }
                }
            }
        }
        if (sections.Any(s => s != ComparisonSection.timings)) h.EnsureAnalysisStarted(job);
        if (sections.Contains(ComparisonSection.resources)) h.EnsureAccessedResources(job);
        var hlsl = new Dictionary<string, string>(StringComparer.Ordinal);
        long hlslBytes = 0;
        var queues = new List<RawQueue>(); var coverage = new List<object>();
        foreach (QueueEntry queue in h.Queues)
        {
            var events = new List<ComparisonEvent>();
            TimingTreeNode[]? nodes = timing ? h.TimingTreeNodes(queue.Index) : null;
            int[] childCounts = h.ChildCounts(queue.Index);
            EventRecord[] all = h.AllEvents(queue.Index);
            foreach (EventRecord record in all)
            {
                job.ThrowIfCancellationRequested();
                var reference = new EventRef(h.Id, queue.Index, record.Index);
                var data = new Dictionary<string, JsonElement>(); string? shaderKey = null;
                IReadOnlyList<ComparisonShader>? shaderList = null;
                if (Tools.MatchesKind(record, "work"))
                {
                    if (sections.Contains(ComparisonSection.shaders))
                        Read("shaders", () =>
                        {
                            var shaders = PipelineTools.ReadShaders(h, reference);
                            shaderKey = ShaderIdentity.PsoKey(shaders);
                            shaderList = shaders.Where(s => s.Hash is not null).Select(s => new ComparisonShader(s.Stage, s.Hash!, s.Index)).ToArray();
                            if (options.CodeDiff)
                                foreach (ShaderInfoDto shader in shaders)
                                {
                                    if (shader.Hash is null || shader.ShaderRef is null || hlsl.ContainsKey(shader.Hash) || hlslBytes >= MaxHlslBytes) continue;
                                    try
                                    {
                                        if (PipelineTools.ReadHlsl(h, shader.ShaderRef) is string code)
                                        {
                                            hlsl[shader.Hash] = code;
                                            hlslBytes += code.Length * 2L;
                                        }
                                    }
                                    catch (Exception ex) when (ex is not OperationCanceledException)
                                    {
                                        coverage.Add(new { shaderRef = shader.ShaderRef, section = "shaderCode", unavailable = true, reason = PixErrors.Describe(ex) });
                                    }
                                }
                            return CaptureComparison.NormalizeSet(shaders.Select(s => new { s.Stage, s.Hash, s.Entry, s.Target, s.Defines, s.Flags }), options.IncludeSoftFields);
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
                                    else data["rootConstants"] = JsonSerializer.SerializeToElement(CaptureComparison.NormalizeSet(page.RootConstants, options.IncludeSoftFields), Json.Options);
                                }
                                resources.AddRange(page.Resources.SelectMany(r => r.Views.Select(view => (object)new { r.Resource, view })));
                                resources.AddRange(page.OtherViews);
                                if (!page.NextViewOffset.HasValue) break;
                                offset = page.NextViewOffset.Value;
                            } while (true);
                            return CaptureComparison.NormalizeSet(resources, options.IncludeSoftFields);
                        });
                }
                TimingTreeNode? node = nodes?[record.Index];
                string kind = Tools.Classify(record, childCounts[record.Index] > 0);
                bool timed = node is not null && node.IsTimed;
                ulong? eop = timed ? node!.InclusiveEopNs : null;
                ulong? spread = null;
                int count = 0;
                if (samples.TryGetValue((queue.Index, record.Index), out List<ulong>? values) && values.Count > 0)
                {
                    ulong[] sorted = values.Order().ToArray();
                    count = sorted.Length;
                    if (count >= 2)
                    {
                        eop = sorted[(count - 1) / 2];
                        spread = sorted[^1] - sorted[0];
                    }
                }
                events.Add(new ComparisonEvent(reference, EventNavigation.MarkerPath(all, record.Index), record.Name, kind,
                    kind == "marker", eop, timed ? node!.Semantics : TimingSemantics.Untimed, shaderKey, data) { Shaders = shaderList, SpreadNs = spread, Samples = count });

                void Read(string section, Func<object> query)
                {
                    try
                    {
                        JsonElement value = CaptureComparison.Normalize(query(), options.IncludeSoftFields);
                        var unavailable = MissingFields(value).ToArray();
                        if (unavailable.Length == 0) data[section] = value;
                        else coverage.Add(new { eventRef = reference, section, unavailable = true,
                            reason = "Section omitted from comparison because native details are incomplete.", fields = unavailable });
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { coverage.Add(new { eventRef = reference, section, unavailable = true, reason = PixErrors.Describe(ex) }); }
                }
            }
            queues.Add(new(queue, all, childCounts, events.ToArray(), timing ? h.TimingRowsByQueue.GetValueOrDefault(queue.Index, []) : []));
        }
        FrameTable frames = FrameSegmentation.Build(queues.Select(q => new FrameQueueInput(q.Queue.Index, q.Events, timing ? q.Rows : null)).ToList(),
            e => Tools.MatchesKind(e, "present"));
        return new(h.Id, queues.ToArray(), h.Provenance(), coverage, hlsl, frames, method);
    }

    /// <summary>One side of a comparison: the events inside its scope and frame, marker paths relative to its scope root, and busy time.</summary>
    private static ComparisonSnapshot Filter(RawSnapshot raw, CompareSide side)
    {
        if (side.Frame is int frame && frame >= raw.Frames.Count)
            throw PixErrors.InvalidArguments($"Frame {frame} is outside {raw.Handle}'s {raw.Frames.Count} frame(s).",
                [new ToolCallDto("pix_gpu_overview", new { handle = raw.Handle }, CostHints.Cached)]);
        var queues = new List<ComparisonQueue>();
        foreach (RawQueue q in raw.Queues)
        {
            int queueIndex = q.Queue.Index;
            bool Keep(uint i) => side.Selection.Contains(queueIndex, q.Events, i) && (side.Frame is not int f || raw.Frames.Contains(f, queueIndex, i))
                && !(side.Root is EventRef root && root.QueueIndex == queueIndex && root.EventIndex == i);
            string[]? rootPath = side.Root is EventRef r && r.QueueIndex == queueIndex && r.EventIndex < q.Events.Length
                ? [.. EventNavigation.MarkerPath(q.Events, r.EventIndex), q.Events[r.EventIndex].Name] : null;
            ComparisonEvent[] events = q.All.Where(e => Keep(e.EventRef.EventIndex))
                .Select(e => rootPath is not null && e.MarkerPath.Length >= rootPath.Length && e.MarkerPath.Take(rootPath.Length).SequenceEqual(rootPath)
                    ? e with { MarkerPath = e.MarkerPath[rootPath.Length..] } : e)
                .ToArray();
            if (events.Length == 0 && !side.Selection.IsUnrestricted) continue;
            queues.Add(new ComparisonQueue(queueIndex, q.Queue.Name, Json.EnumName(q.Queue.Type), events) { BusyNs = Busy(q, Keep) });
        }
        return new ComparisonSnapshot(raw.Handle, queues.ToArray(), raw.Provenance, raw.Coverage) { HlslByHash = raw.Hlsl };
    }

    private static ulong? Busy(RawQueue q, Func<uint, bool> keep)
    {
        if (q.Rows.Length == 0) return null;
        var windows = new List<(ulong Start, ulong End)>();
        foreach (EventTimingRow row in q.Rows)
        {
            if (row.EopDuration == GpuCaptureHandle.TimingNone || row.EopStart == GpuCaptureHandle.TimingNone || row.Index >= q.Events.Length
                || q.ChildCounts[row.Index] > 0 || !keep(row.Index)) continue;
            ulong end = row.EopStart + row.EopDuration;
            ulong start = row.TopStart != GpuCaptureHandle.TimingNone && row.TopStart <= end ? Math.Min(row.TopStart, row.EopStart) : row.EopStart;
            windows.Add((start, end));
        }
        return Metrics.UnionLength(windows);
    }

    private static bool SameTimestamps(Dictionary<int, EventTimingRow[]> before, Dictionary<int, EventTimingRow[]> after)
    {
        foreach ((int queue, EventTimingRow[] rows) in before)
        {
            if (!after.TryGetValue(queue, out EventTimingRow[]? other) || other.Length != rows.Length) return false;
            for (int i = 0; i < rows.Length; i++)
                if (rows[i].Index != other[i].Index || rows[i].EopStart != other[i].EopStart || rows[i].EopDuration != other[i].EopDuration) return false;
        }
        return before.Count == after.Count;
    }

    /// <summary>Forgets collected timing: on the running analysis (recollect) or by stopping it so the next collection restarts it.</summary>
    private static void ResetTiming(GpuCaptureHandle h, bool restart, Job job)
    {
        if (restart)
        {
            var warnings = new List<string>();
            h.StopAnalysis(warnings);
            foreach (string warning in warnings) job.AddMessage(warning);
            return;
        }
        h.Timing = null;
        h.TimingRowsByQueue = new();
        h.TimingReadbackByQueue.Clear();
        h.TimingTreeByQueue.Clear();
        h.TimelineCache.Clear();
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
