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
    [McpServerTool(Name = "pix_gpu_drpix_experiments"), Description("Lists the Dr. PIX experiments available for this capture (guid, name, category, help text, source). Starts analysis if needed.")]
    public static Task<string> Experiments(PixSession session, [Description("GPU capture handle")] string handle)
        => Tools.Run(session, "pix_gpu_drpix_experiments", () =>
        {
            GpuCaptureHandle h = session.Get<GpuCaptureHandle>(handle);
            List<ExperimentInfo> experiments = LoadExperiments(h, null);
            return experiments.Select(e => new { guid = e.Guid, name = e.Name, category = e.Category, helpText = e.HelpText, source = e.Source }).ToArray();
        });

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

    [McpServerTool(Name = "pix_gpu_drpix_run"), Description("Runs Dr. PIX experiments (all by default) over a GPU event range and returns their metrics and messages. This replays the capture repeatedly and can take minutes; returns a job.")]
    public static async Task<string> Run(
        PixSession session,
        JobManager jobs,
        [Description("GPU capture handle")] string handle,
        [Description("Experiment guids or (case-insensitive) names to run; omit for all.")] string[]? experiments = null,
        [Description("First GPU event id of the range (default: first GPU event in the capture).")] uint? firstEventGpuId = null,
        [Description("Last GPU event id of the range (default: last GPU event in the capture).")] uint? lastEventGpuId = null,
        [Description("Seconds to wait inline for completion (default 0 = return job immediately).")] double waitSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string[]? requestedExperiments = experiments?.ToArray();
            Job job = jobs.StartForHandle<GpuCaptureHandle>("drpix", $"Run Dr. PIX experiments on {handle}", handle,
                (j, h) => RunCore(h, j, requestedExperiments, firstEventGpuId, lastEventGpuId));
            return Json.Serialize(await jobs.WaitOrStatus(job, waitSeconds, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            throw PixErrors.ToMcp(ex, "pix_gpu_drpix_run");
        }
    }

    private static object RunCore(GpuCaptureHandle h, Job job, string[]? requested, uint? firstGpuId, uint? lastGpuId)
    {
        List<ExperimentInfo> all = LoadExperiments(h, job);
        if (all.Count == 0)
        {
            throw new McpException("No Dr. PIX experiments are available for this capture.");
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
                    throw new McpException($"Unknown experiment '{r}'. Available: {string.Join(", ", all.Select(e => e.Name))}.");
                }
                selected.Add(match);
            }
        }
        else
        {
            selected = all;
        }

        (PIX_EVENT_INFO first, PIX_EVENT_INFO last) = EventRange(h, firstGpuId, lastGpuId);
        job.AddMessage($"Running {selected.Count} experiment(s) over GPU events {first.GpuId}..{last.GpuId}.");

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
                firstEventGpuId = p.FirstEvent.GpuId,
                lastEventGpuId = p.LastEvent.GpuId,
                metrics,
                messages,
            });
        }

        return new { handle = h.Id, experimentsRun = selected.Count, results = dtos };
    }

    private static (PIX_EVENT_INFO first, PIX_EVENT_INFO last) EventRange(GpuCaptureHandle h, uint? firstGpuId, uint? lastGpuId)
    {
        PIX_EVENT_INFO? first = null;
        PIX_EVENT_INFO? last = null;
        foreach (QueueEntry queue in h.Queues)
        {
            for (uint i = 0; i < queue.EventCount; i++)
            {
                PIX_EVENT_INFO e = Microsoft.PIX.Extension.GpuCapture.PixApiExtensionsGpuCapture.GetEvent(queue.Info, i);
                if (e.GpuId == uint.MaxValue)
                {
                    continue;
                }
                if (firstGpuId.HasValue)
                {
                    if (e.GpuId == firstGpuId.Value) first = e;
                }
                else if (first is null || e.GpuId < first.Value.GpuId)
                {
                    first = e;
                }
                if (lastGpuId.HasValue)
                {
                    if (e.GpuId == lastGpuId.Value) last = e;
                }
                else if (last is null || e.GpuId > last.Value.GpuId)
                {
                    last = e;
                }
            }
        }
        if (first is null)
        {
            throw new McpException(firstGpuId.HasValue ? $"No event with gpuId {firstGpuId} found." : "The capture contains no GPU events.");
        }
        if (last is null)
        {
            throw new McpException(lastGpuId.HasValue ? $"No event with gpuId {lastGpuId} found." : "The capture contains no GPU events.");
        }
        return (first.Value, last.Value);
    }
}
