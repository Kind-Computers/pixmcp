using System.Collections.Concurrent;
using System.ComponentModel;
using Microsoft.PIX;
using Microsoft.PIX.Extension;
using Microsoft.PIX.Extension.DrPix;
using Microsoft.PIX.Extension.GpuCapture.Analysis;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using PixMcp.Pix.Handles;

namespace PixMcp.Tools;

[McpServerToolType]
public static class DrPixTools
{
    public const int DefaultMaxRuns = 50;
    private const string CostHint = "One replay of the range per run; about the cost of a timing collection over that range.";

    [McpServerTool(Name = "pix_gpu_drpix_experiments", Title = "List Dr. PIX experiments", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Replays the capture on the local GPU if analysis is not started. Lists the Dr. PIX experiments for this capture with category, source, family, what a result proves, whether the experiment honours event ranges, and help text; extra lists the categories, families and sources. Pass names, categories or families to pix_gpu_drpix_run.")]
    public static Task<string> Experiments(
        PixSession session,
        JobManager jobs,
        [Description("GPU capture handle")] string handle,
        [Description("Only experiments whose category contains this text (case-insensitive).")] string? category = null,
        [Description("Only this family: basic, depthStencil, rasterization, executeIndirect, shaderCorrectness, debugBreak, memory, vendor or other.")] string? family = null,
        [Description("Only experiments from this source, such as PIX (case-insensitive).")] string? source = null,
        [Description(Tools.ReadyWaitDescription)] double waitSeconds = Tools.DefaultReadyWaitSeconds,
        CancellationToken cancellationToken = default)
    {
        string? familyName = family is null ? null : RollupTools.Canonical(family, DrPixFamilies.Names, "family");
        return Tools.RunWhenReady(session, jobs, "pix_gpu_drpix_experiments", handle, GpuCaptureHandle.AnalysisPreparation(handle), h =>
        {
            DrPixExperimentDto[] all = LoadExperiments(h, null).Select(Describe).ToArray();
            DrPixExperimentDto[] items = all.Where(e => (category is null || e.Category.Contains(category.Trim(), StringComparison.OrdinalIgnoreCase))
                && (familyName is null || e.Family == familyName) && (source is null || e.Source.Equals(source.Trim(), StringComparison.OrdinalIgnoreCase))).ToArray();
            return Paging.Page(items, items.Length, 0, Math.Max(1, items.Length), new
            {
                categories = all.Select(e => e.Category).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                families = all.Select(e => e.Family).Distinct().OrderBy(f => Array.IndexOf(DrPixFamilies.Names, f)).ToArray(),
                sources = all.Select(e => e.Source).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                notes = CompatibilityNotes.Texts("drPixVendorExperiments", h.EffectiveVendor().Vendor, PixDiscovery.Version),
            });
        }, waitSeconds, cancellationToken);
    }

    internal static DrPixExperimentDto Describe(ExperimentInfo e)
    {
        string source = Json.EnumName(e.Source);
        (string family, string proves) = DrPixFamilies.Of(e.Name, e.Category, source);
        return new(e.Guid, e.Name, e.Category, e.HelpText, source, family, proves, DrPixFamilies.SupportsRanges(e.Name), CostHint);
    }

    internal static unsafe List<ExperimentInfo> LoadExperiments(GpuCaptureHandle h, Job? job)
    {
        if (h.Experiments is not null)
        {
            return h.Experiments;
        }
        h.EnsureAnalysisStarted(job);
        h.DrPix ??= PixApiExtensionsGpuCaptureAnalysis.GetDrPix(h.GetAnalysis());
        var list = new List<ExperimentInfo>();
        ulong count = h.DrPix.GetExperimentCount();
        for (ulong i = 0; i < count; i++)
        {
            PIX_EXPERIMENT_DESC desc = default;
            h.DrPix.GetExperiment(i, &desc);
            list.Add(new ExperimentInfo(desc.Guid, Interop.W(desc.Name), Interop.W(desc.Category), Interop.W(desc.HelpText), desc.Source));
        }
        h.Experiments = list;
        return list;
    }

    [McpServerTool(Name = "pix_gpu_drpix_run", Title = "Run Dr. PIX experiments (replays per run)", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), Description("Runs Dr. PIX experiments over event ranges as a job. The result lists every run (experiment x range) with structured records, a baseline/experiment timing pair where the experiment reports one, a one-row-per-run table, savings with implications, a summary and notes. Each run replays its range, so this can take minutes. Pick the range with scope (an event and its descendants), markerPathPrefix (one range per top-most matching marker) or wholeCapture=true; perEvent=true splits each range into one run per draw, dispatch or ExecuteIndirect. While the job runs, pix_job_status offers partialResultRef with the runs finished so far; a cancelled or failed job keeps them as a partial result.")]
    public static async Task<string> Run(
        PixSession session,
        JobManager jobs,
        [Description("GPU capture handle")] string handle,
        [Description("Experiment guids or (case-insensitive) names to run; omit for every experiment the category and family filters keep.")] string[]? experiments = null,
        [Description(EventScope.Description)] EventRef? scope = null,
        [Description(EventScope.PrefixDescription + " Each top-most matching marker becomes its own range.")] string? markerPathPrefix = null,
        [Description("Queue the prefix refers to when it matches markers on several queues.")] int? queueIndex = null,
        [Description("Run over every GPU event of the capture (default false); replays everything for each experiment.")] bool wholeCapture = false,
        [Description("Only experiments whose category contains one of these texts (case-insensitive).")] string[]? categories = null,
        [Description("Only experiments in these families: basic, depthStencil, rasterization, executeIndirect, shaderCorrectness, debugBreak, memory, vendor or other.")] string[]? families = null,
        [Description("One range per draw, dispatch or ExecuteIndirect inside each range (default false).")] bool perEvent = false,
        [Description("Refuse more runs (experiments times ranges) than this (default 50, max 1000).")] int maxRuns = DefaultMaxRuns,
        [Description(Tools.WaitSecondsDescription)] double waitSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (wholeCapture == (scope is not null || markerPathPrefix is not null))
                throw new PixToolException(PixErrors.Codes.InvalidArguments,
                    "Dr. PIX replays the capture once per experiment per range: pass scope or markerPathPrefix, or wholeCapture=true.",
                    nextCalls: [new("pix_gpu_overview", new { handle })]);
            if (maxRuns is < 1 or > 1000) throw PixErrors.InvalidArguments("maxRuns must be between 1 and 1000.");
            string[]? familyNames = families?.Select(f => RollupTools.Canonical(f, DrPixFamilies.Names, "families")).ToArray();
            ScopeSelection selection = EventScope.Resolve(session, handle, queueIndex, scope, markerPathPrefix);
            var request = new DrPixRequest(experiments?.ToArray(), categories?.ToArray(), familyNames, wholeCapture ? null : selection, perEvent, maxRuns);
            Job job = jobs.StartForHandle<GpuCaptureHandle>("drpix", $"Run Dr. PIX experiments on {handle}", handle, (j, h) => RunCore(h, j, request));
            return Json.Serialize(await jobs.WaitOrStatus(job, waitSeconds, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            throw PixErrors.ToMcp(ex, "pix_gpu_drpix_run");
        }
    }

    internal sealed record DrPixRequest(string[]? Experiments, string[]? Categories, string[]? Families, ScopeSelection? Selection, bool PerEvent, int MaxRuns);

    private sealed record RangePlan(EventRange Range, string? MarkerPath);

    internal static object RunCore(GpuCaptureHandle h, Job job, DrPixRequest request)
    {
        List<ExperimentInfo> all = LoadExperiments(h, job);
        if (all.Count == 0) throw PixErrors.UnsupportedFeature("No Dr. PIX experiments are available for this capture.");
        List<ExperimentInfo> selected = Select(h, all, request);
        List<RangePlan> ranges = Ranges(h, request, job);
        long requested = (long)selected.Count * ranges.Count;
        if (requested > request.MaxRuns)
            throw PixErrors.InvalidArguments($"{requested} runs requested ({selected.Count} experiment(s) x {ranges.Count} range(s)); maxRuns is {request.MaxRuns}. " +
                "Narrow the experiments, families or range, or raise maxRuns.", [new ToolCallDto("pix_gpu_drpix_experiments", new { handle = h.Id }, CostHints.Cached)]);

        var runs = new List<(ExperimentInfo Experiment, int RangeIndex)>();
        foreach (ExperimentInfo experiment in selected)
            for (int r = 0; r < ranges.Count; r++) runs.Add((experiment, r));
        var runParams = new PIX_EXPERIMENT_RUN_PARAMS[runs.Count];
        for (int i = 0; i < runs.Count; i++)
        {
            runParams[i].ExperimentGuid = runs[i].Experiment.Guid;
            runParams[i].FirstEvent = ranges[runs[i].RangeIndex].Range.First;
            runParams[i].LastEvent = ranges[runs[i].RangeIndex].Range.Last;
        }
        DrPixRangeDto[] rangeDtos = ranges.Select((plan, i) => RangeDto(h.Id, i, plan)).ToArray();
        job.AddMessage($"Running {runs.Count} run(s): {selected.Count} experiment(s) over {ranges.Count} range(s).");

        Dictionary<Guid, ExperimentInfo> byGuid = all.GroupBy(e => e.Guid).ToDictionary(g => g.Key, g => g.First());
        var arrived = new ConcurrentQueue<IPixGpuCaptureExperimentResult>();
        var gate = new object();
        var finished = new List<object>();
        bool[] claimed = new bool[runs.Count];
        var callback = new DelegateExperimentCallback
        {
            OnResultAvailable = result =>
            {
                try
                {
                    arrived.Enqueue(result);
                    PIX_EXPERIMENT_RUN_PARAMS p = PixApiExtensionsDrPix.GetExperimentRunParams(result);
                    int status = PixApiExtensionsDrPix.GetExperimentStatus(result);
                    object snapshot;
                    lock (gate)
                    {
                        int run = Claim(runs, ranges, claimed, p);
                        string name = byGuid.TryGetValue(p.ExperimentGuid, out ExperimentInfo? known) ? known.Name : p.ExperimentGuid.ToString();
                        finished.Add(new { run, experiment = name, rangeIndex = run >= 0 ? runs[run].RangeIndex : (int?)null, status = PixErrors.Hex(status), succeeded = status >= 0 });
                        snapshot = new
                        {
                            partial = true, runsRequested = runs.Count, runsCompleted = finished.Count, ranges = rangeDtos, runs = finished.ToArray(),
                            note = "Records, timings and savings are decoded when the job finishes; read the job result for them.",
                        };
                        job.AddMessage($"[{finished.Count}/{runs.Count}] {name}: status {PixErrors.Hex(status)}");
                        job.SetProgress((float)finished.Count / runs.Count);
                    }
                    job.SetPartial(snapshot);
                }
                catch { }
            },
        };

        job.ThrowIfCancellationRequested();
        IPixCollection results;
        try
        {
            results = PixApiExtensionsDrPix.RunExperiments(h.DrPix!, runParams, callback, job.Sink, job.PixToken!);
        }
        catch (Exception ex) when (!arrived.IsEmpty)
        {
            DrPixResultDto? partial = null;
            try { partial = Build(h, request, runs, ranges, rangeDtos, byGuid, arrived.ToArray(), partial: true); }
            catch (Exception) { }
            if (partial is null) throw;
            throw new PartialResultException(ex, partial);
        }
        return Build(h, request, runs, ranges, rangeDtos, byGuid, Interop.Items<IPixGpuCaptureExperimentResult>(results).ToArray(), partial: false);
    }

    private static List<ExperimentInfo> Select(GpuCaptureHandle h, List<ExperimentInfo> all, DrPixRequest request)
    {
        IEnumerable<ExperimentInfo> selected = all;
        if (request.Experiments is { Length: > 0 })
        {
            var named = new List<ExperimentInfo>();
            foreach (string r in request.Experiments)
            {
                ExperimentInfo? match = all.FirstOrDefault(e => e.Guid.ToString().Equals(r.Trim('{', '}'), StringComparison.OrdinalIgnoreCase)
                    || e.Name.Equals(r, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                    throw PixErrors.InvalidReference($"Unknown experiment '{r}'. Available: {string.Join(", ", all.Select(e => e.Name))}.",
                        new ToolCallDto("pix_gpu_drpix_experiments", new { handle = h.Id }, CostHints.Cached));
                named.Add(match);
            }
            selected = named;
        }
        if (request.Categories is { Length: > 0 } categories)
            selected = selected.Where(e => categories.Any(c => e.Category.Contains(c.Trim(), StringComparison.OrdinalIgnoreCase)));
        if (request.Families is { Length: > 0 } families)
            selected = selected.Where(e => families.Contains(Describe(e).Family));
        List<ExperimentInfo> list = selected.Distinct().ToList();
        if (list.Count == 0)
            throw PixErrors.InvalidArguments("No experiment matches the requested names, categories and families.",
                [new ToolCallDto("pix_gpu_drpix_experiments", new { handle = h.Id }, CostHints.Cached)]);
        return list;
    }

    private static List<RangePlan> Ranges(GpuCaptureHandle h, DrPixRequest request, Job job)
    {
        var plans = new List<RangePlan>();
        if (request.Selection is not ScopeSelection selection)
        {
            if (!request.PerEvent) return [new(EventScope.WholeCapture(h), null)];
            foreach (QueueEntry queue in h.Queues)
            {
                EventRecord[] events = h.AllEvents(queue.Index);
                foreach (uint i in DrPixRanges.WorkEvents(events, _ => true)) plans.Add(EventPlan(h, queue.Index, events, i));
            }
        }
        else
        {
            List<(ScopeSelection Part, EventRef? Root)> parts = selection.Root is not null
                ? [(selection, selection.Root)]
                : selection.MatchedRoots(h.Queues.Count, h.AllEvents).Select(r => (new ScopeSelection(h.Id, r.QueueIndex, r, null, null), (EventRef?)r)).ToList();
            if (parts.Count == 0)
                throw new PixToolException(PixErrors.Codes.InvalidReference, $"markerPathPrefix '{selection.MarkerPathPrefix}' matches no marker in this capture.",
                    nextCalls: [new("pix_gpu_events", new { handle = h.Id, kind = "marker", limit = 25 })]);
            foreach ((ScopeSelection part, EventRef? root) in parts)
            {
                string? path = root is null ? null : MarkerPathText(h, root);
                if (request.PerEvent)
                {
                    int q = root?.QueueIndex ?? EventScope.SingleQueue(part, h.Queues.Count, h.AllEvents);
                    EventRecord[] events = h.AllEvents(q);
                    foreach (uint i in DrPixRanges.WorkEvents(events, i => part.Contains(q, events, i))) plans.Add(EventPlan(h, q, events, i));
                    continue;
                }
                try { plans.Add(new(EventScope.ToEventRange(h, part), path)); }
                catch (PixToolException ex) when (ex.Detail.Code == PixErrors.Codes.InvalidReference && parts.Count > 1)
                {
                    job.AddMessage($"Skipped {path}: {ex.Message}");
                }
            }
        }
        if (plans.Count == 0)
            throw new PixToolException(PixErrors.Codes.InvalidReference, "The selected range contains no draw, dispatch or ExecuteIndirect events to run experiments on.",
                nextCalls: [new("pix_gpu_events", new { handle = h.Id, kind = "work", limit = 25 })]);
        return plans;
    }

    private static RangePlan EventPlan(GpuCaptureHandle h, int queue, EventRecord[] events, uint index)
    {
        uint gpu = events[index].GpuId;
        return new(EventScope.ToEventRange(h, new IndexRange(queue, index, index, gpu, gpu, 1, 0, queue)), MarkerPathText(h, new EventRef(h.Id, queue, index)));
    }

    private static string MarkerPathText(GpuCaptureHandle h, EventRef at)
    {
        EventRecord[] events = h.AllEvents(at.QueueIndex);
        return string.Join("/", EventNavigation.MarkerPath(events, at.EventIndex).Append(events[at.EventIndex].Name));
    }

    private static DrPixRangeDto RangeDto(string handle, int index, RangePlan plan)
    {
        IndexRange r = plan.Range.Range;
        return new(index, new EventRef(handle, r.QueueIndex, r.FirstIndex), new EventRef(handle, r.LastQueueIndex, r.LastIndex), r.FirstGpuId, r.LastGpuId, r.WorkEvents,
            plan.Range.Swapped, plan.MarkerPath);
    }

    /// <summary>The first unclaimed run with the result's experiment and GPU ids; experiments that ignore ranges fall back to the experiment alone.</summary>
    private static int Claim(List<(ExperimentInfo Experiment, int RangeIndex)> runs, List<RangePlan> ranges, bool[] claimed, PIX_EXPERIMENT_RUN_PARAMS p)
    {
        for (int i = 0; i < runs.Count; i++)
            if (!claimed[i] && runs[i].Experiment.Guid == p.ExperimentGuid && ranges[runs[i].RangeIndex].Range.First.GpuId == p.FirstEvent.GpuId
                && ranges[runs[i].RangeIndex].Range.Last.GpuId == p.LastEvent.GpuId)
                return Take(i);
        for (int i = 0; i < runs.Count; i++)
            if (!claimed[i] && runs[i].Experiment.Guid == p.ExperimentGuid) return Take(i);
        return -1;
        int Take(int i)
        {
            claimed[i] = true;
            return i;
        }
    }

    private static DrPixResultDto Build(GpuCaptureHandle h, DrPixRequest request, List<(ExperimentInfo Experiment, int RangeIndex)> runs, List<RangePlan> ranges,
        DrPixRangeDto[] rangeDtos, Dictionary<Guid, ExperimentInfo> byGuid, IReadOnlyList<IPixGpuCaptureExperimentResult> results, bool partial)
    {
        bool[] claimed = new bool[runs.Count];
        var dtos = new List<DrPixRunDto>();
        foreach (IPixGpuCaptureExperimentResult result in results)
        {
            PIX_EXPERIMENT_RUN_PARAMS p = PixApiExtensionsDrPix.GetExperimentRunParams(result);
            int status = PixApiExtensionsDrPix.GetExperimentStatus(result);
            int run = Claim(runs, ranges, claimed, p);
            ExperimentInfo experiment = run >= 0 ? runs[run].Experiment
                : byGuid.TryGetValue(p.ExperimentGuid, out ExperimentInfo? known) ? known : new ExperimentInfo(p.ExperimentGuid, p.ExperimentGuid.ToString(), "", "", default);
            IReadOnlyList<DrPixRecordDto> records = [];
            IReadOnlyList<DrPixMessageDto> messages = [];
            object? unavailable = null;
            try
            {
                records = DrPixMetrics.Records(PixApiExtensionsDrPix.GetExperimentMetrics(result)
                    .Select(m => new DrPixRawMetric(Interop.WOrNull(m.GroupName), Interop.W(m.Name), Interop.WOrNull(m.ValueLabel), Interop.Value(m.Value), (int)m.Depth)).ToArray());
            }
            catch (Exception ex) { unavailable = PixErrors.Unavailable("metrics", ex); }
            try
            {
                messages = PixApiExtensionsDrPix.GetExperimentMessages(result).Select(m => new DrPixMessageDto(Json.EnumName(m.Type), Interop.W(m.Message))).ToArray();
            }
            catch (Exception ex) { unavailable ??= PixErrors.Unavailable("messages", ex); }
            DrPixExperimentDto described = Describe(experiment);
            bool rangeIgnored = messages.Any(m => m.Message.Contains("support subranges", StringComparison.OrdinalIgnoreCase));
            dtos.Add(new DrPixRunDto(run >= 0 ? run : runs.Count + dtos.Count, experiment.Name, experiment.Guid, experiment.Category, described.Family, described.WhatItProves,
                described.Source, run >= 0 ? runs[run].RangeIndex : 0, PixErrors.Hex(status), status >= 0, rangeIgnored, DrPixMetrics.Timing(records), records, messages)
            { Unavailable = unavailable });
        }
        DrPixRunDto[] ordered = dtos.OrderBy(r => r.Run).ToArray();
        DrPixSavingDto[] savings = ordered.Where(r => r.Timing is not null)
            .Select(r => new DrPixSavingDto(r.Run, r.Experiment, r.Family, r.RangeIndex, r.Timing!.BaselineMs, r.Timing.ExperimentMs, r.Timing.SavedMs, r.Timing.SavedPercent,
                DrPixMetrics.Implication(r.Family, r.Timing)))
            .ToArray();
        var notes = new List<string>();
        if (ordered.Any(r => r.RangeIgnored)) notes.Add("Runs marked rangeIgnored ran over the whole capture because the experiment does not support sub-ranges.");
        if (ordered.Any(r => r.Records.Any(record => record.Values.Values.OfType<string>().Any(v => v.Contains("Shader Model 6.0+ required", StringComparison.OrdinalIgnoreCase)))))
            notes.Add("Rows reading 'Shader Model 6.0+ required' mean the shaders predate SM 6.0 and were not instrumented; they are not findings.");
        if (partial) notes.Add("The job stopped before every run finished; runs lists only the finished ones.");
        notes.Add("Timings are replay measurements on this machine; savings describe the replayed range, not application frame latency.");

        var calls = new List<ToolCallDto> { new("pix_gpu_inspect_event", new { eventRef = rangeDtos[0].FirstEventRef }) };
        if (request.Selection?.Root is EventRef root)
        {
            calls.Add(new("pix_gpu_timing_tree", new { handle = h.Id, scope = root, sortBy = "self" }));
            if (savings.Any(s => s.Family == "basic" && s.SavedPercent > 0)) calls.Add(new("pix_gpu_shaders", new { handle = h.Id, scope = root }));
        }
        return new DrPixResultDto(h.Id, runs.Count, ordered.Length, partial, request.Selection is null, request.PerEvent, rangeDtos, ordered,
            DrPixMetrics.Table(ordered, rangeDtos), savings, DrPixMetrics.Summary(ordered, savings), notes, calls.Where(ToolRegistry.Accepts).ToArray())
        { Scope = request.Selection?.DescribeOrNull(h), Range = ranges.Count == 1 ? ranges[0].Range.ToDto(h.Id) : null };
    }
}
