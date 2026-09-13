using System.ComponentModel;
using Microsoft.PIX;
using Microsoft.PIX.Extension;
using Microsoft.PIX.Extension.DrPix;
using Microsoft.PIX.Extension.GpuCapture.Analysis;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using PixMcp.Pix.Handles;

namespace PixMcp.Tools;

[McpServerToolType]
public static class DrPixTools
{
    [McpServerTool(Name = "pix_gpu_drpix_experiments", Title = "List Dr. PIX experiments", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Replays the capture on the local GPU if analysis is not started. Lists the Dr. PIX experiments available for this capture (guid, name, category, help text, source); pass names or guids to pix_gpu_drpix_run. Needs GPU analysis: started automatically as a job (see waitSeconds).")]
    public static Task<string> Experiments(
        PixSession session,
        JobManager jobs,
        [Description("GPU capture handle")] string handle,
        [Description(Tools.ReadyWaitDescription)] double waitSeconds = Tools.DefaultReadyWaitSeconds,
        CancellationToken cancellationToken = default)
        => Tools.RunWhenReady(session, jobs, "pix_gpu_drpix_experiments", handle, GpuCaptureHandle.AnalysisPreparation(handle), h =>
        {
            List<ExperimentInfo> experiments = LoadExperiments(h, null);
            return SessionTools.Envelope(experiments.Select(e => new { guid = e.Guid, name = e.Name, category = e.Category, helpText = e.HelpText, source = e.Source }).ToArray());
        }, waitSeconds, cancellationToken);

    private static unsafe List<ExperimentInfo> LoadExperiments(GpuCaptureHandle h, Job? job)
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

    [McpServerTool(Name = "pix_gpu_drpix_run", Title = "Run Dr. PIX experiments", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), Description("Runs Dr. PIX experiments (all by default) over the GPU events of a scope and returns their metrics and messages. This replays the capture once per experiment and can take minutes; returns a job. Pass scope (an event and its descendants), markerPathPrefix, or wholeCapture=true; there is no whole-capture default. The result carries the resolved range as event references.")]
    public static async Task<string> Run(
        PixSession session,
        JobManager jobs,
        [Description("GPU capture handle")] string handle,
        [Description("Experiment guids or (case-insensitive) names to run; omit for all.")] string[]? experiments = null,
        [Description(EventScope.Description)] EventRef? scope = null,
        [Description(EventScope.PrefixDescription)] string? markerPathPrefix = null,
        [Description("Queue the prefix refers to when it matches markers on several queues.")] int? queueIndex = null,
        [Description("Run over every GPU event of the capture (default false); replays everything for each experiment.")] bool wholeCapture = false,
        [Description(Tools.WaitSecondsDescription)] double waitSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (wholeCapture == (scope is not null || markerPathPrefix is not null))
                throw new PixToolException(PixErrors.Codes.InvalidArguments,
                    "Dr. PIX replays the capture once per experiment per range: pass scope or markerPathPrefix, or wholeCapture=true.",
                    nextCalls: [new("pix_gpu_overview", new { handle })]);
            ScopeSelection selection = EventScope.Resolve(session, handle, queueIndex, scope, markerPathPrefix);
            string[]? requestedExperiments = experiments?.ToArray();
            Job job = jobs.StartForHandle<GpuCaptureHandle>("drpix", $"Run Dr. PIX experiments on {handle}", handle,
                (j, h) => RunCore(h, j, requestedExperiments, wholeCapture ? null : selection));
            return Json.Serialize(await jobs.WaitOrStatus(job, waitSeconds, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            throw PixErrors.ToMcp(ex, "pix_gpu_drpix_run");
        }
    }

    private static object RunCore(GpuCaptureHandle h, Job job, string[]? requested, ScopeSelection? selection)
    {
        List<ExperimentInfo> all = LoadExperiments(h, job);
        if (all.Count == 0)
        {
            throw PixErrors.UnsupportedFeature("No Dr. PIX experiments are available for this capture.");
        }

        List<ExperimentInfo> selected;
        if (requested is { Length: > 0 })
        {
            selected = new List<ExperimentInfo>();
            foreach (string r in requested)
            {
                ExperimentInfo? match = all.FirstOrDefault(e => e.Guid.ToString().Equals(r.Trim('{', '}'), StringComparison.OrdinalIgnoreCase)
                                                             || e.Name.Equals(r, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    throw PixErrors.InvalidReference($"Unknown experiment '{r}'. Available: {string.Join(", ", all.Select(e => e.Name))}.",
                        new ToolCallDto("pix_gpu_drpix_experiments", new { handle = h.Id }, CostHints.Cached));
                }
                selected.Add(match);
            }
        }
        else
        {
            selected = all;
        }

        EventRange range = selection is null ? EventScope.WholeCapture(h) : EventScope.ToEventRange(h, selection);
        PIX_EVENT_INFO first = range.First, last = range.Last;
        job.AddMessage($"Running {selected.Count} experiment(s) over GPU events {first.GpuId}..{last.GpuId} ({range.Range.WorkEvents} GPU event(s)).");

        var runParams = new PIX_EXPERIMENT_RUN_PARAMS[selected.Count];
        for (int i = 0; i < selected.Count; i++)
        {
            runParams[i].ExperimentGuid = selected[i].Guid;
            runParams[i].FirstEvent = first;
            runParams[i].LastEvent = last;
        }

        var byGuid = all.ToDictionary(e => e.Guid, e => e.Name);
        int completed = 0;
        var callback = new DelegateExperimentCallback
        {
            OnResultAvailable = result =>
            {
                try
                {
                    PIX_EXPERIMENT_RUN_PARAMS p = PixApiExtensionsDrPix.GetExperimentRunParams(result);
                    int status = PixApiExtensionsDrPix.GetExperimentStatus(result);
                    int n = Interlocked.Increment(ref completed);
                    job.AddMessage($"[{n}/{selected.Count}] {(byGuid.TryGetValue(p.ExperimentGuid, out string? name) ? name : p.ExperimentGuid.ToString())}: status {PixErrors.Hex(status)}");
                    job.SetProgress((float)n / selected.Count);
                }
                catch { }
            },
        };

        job.ThrowIfCancellationRequested();
        IPixCollection results = PixApiExtensionsDrPix.RunExperiments(h.DrPix!, runParams, callback, job.Sink, job.PixToken!);

        var dtos = new List<object>();
        foreach (IPixGpuCaptureExperimentResult result in Interop.Items<IPixGpuCaptureExperimentResult>(results))
        {
            PIX_EXPERIMENT_RUN_PARAMS p = PixApiExtensionsDrPix.GetExperimentRunParams(result);
            int status = PixApiExtensionsDrPix.GetExperimentStatus(result);
            object metrics;
            object messages;
            try
            {
                metrics = PixApiExtensionsDrPix.GetExperimentMetrics(result).Select(m => new
                {
                    group = Interop.WOrNull(m.GroupName),
                    name = Interop.W(m.Name),
                    valueLabel = Interop.WOrNull(m.ValueLabel),
                    value = Interop.Value(m.Value),
                    depth = m.Depth,
                }).ToArray();
            }
            catch (Exception ex) { metrics = PixErrors.Unavailable("metrics", ex); }
            try
            {
                messages = PixApiExtensionsDrPix.GetExperimentMessages(result).Select(m => new { type = m.Type, message = Interop.W(m.Message) }).ToArray();
            }
            catch (Exception ex) { messages = PixErrors.Unavailable("messages", ex); }

            dtos.Add(new
            {
                experiment = byGuid.TryGetValue(p.ExperimentGuid, out string? name) ? name : p.ExperimentGuid.ToString(),
                guid = p.ExperimentGuid,
                status = PixErrors.Hex(status),
                succeeded = status >= 0,
                firstGpuId = p.FirstEvent.GpuId,
                lastGpuId = p.LastEvent.GpuId,
                metrics,
                messages,
            });
        }

        return new
        {
            handle = h.Id, experimentsRun = selected.Count, range = range.ToDto(h.Id),
            scope = selection?.DescribeOrNull(h), wholeCapture = selection is null, results = dtos,
        };
    }
}
