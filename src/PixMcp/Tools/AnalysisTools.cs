using System.ComponentModel;
using Microsoft.PIX;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using PixMcp.Pix.Handles;

namespace PixMcp.Tools;

[McpServerToolType]
public static class AnalysisTools
{
    [McpServerTool(Name = "pix_gpu_analysis_start", Title = "Start GPU analysis (replay)", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), Description("Connects the capture to the local GPU and starts analysis (replay). Required for timing, counters, pipeline state, resources and Dr. PIX; those tools also start it implicitly, but for big captures start it here so the wait is visible. Returns a job; poll pix_job_status or pass waitSeconds.")]
    public static async Task<string> Start(
        PixSession session,
        JobManager jobs,
        [Description("GPU capture handle")] string handle,
        [Description("Adapter id from pix_gpu_analysis_adapters (default: chosen by PIX).")] ulong? adapterId = null,
        [Description("Power state id from pix_gpu_analysis_adapters (default: chosen by PIX).")] uint? powerStateId = null,
        [Description(AnalysisFlags.Description)] string[]? flags = null,
        [Description(Tools.WaitSecondsDescription)] double waitSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            PIX_ANALYSIS_FLAGS? requestedFlags = AnalysisFlags.Parse(flags, handle);
            var options = new AnalysisOptions(adapterId, powerStateId, requestedFlags);
            GpuCaptureHandle h = session.Get<GpuCaptureHandle>(handle);
            bool alreadyStarted = false;
            Preparation<GpuCaptureHandle> preparation = GpuCaptureHandle.AnalysisPreparation(handle) with
            {
                JoinKeys = [],
                Prepare = (h, j) =>
                {
                    alreadyStarted = h.ConfigureAnalysis(options);
                    if (!alreadyStarted) h.EnsureAnalysisStarted(j);
                },
                Result = h => new { alreadyStarted, analysis = h.AnalysisStatus() },
            };
            Job job;
            lock (h.PreparationGate)
            {
                // Settings are validated when joining a queued/running start (its options are pending) or a started
                // analysis (its options are selected); the job itself validates again on the worker.
                if (Tools.FindPreparation(h, preparation) is { IsFinished: false } running)
                {
                    (h.PendingAnalysisOptions ?? new AnalysisOptions()).ValidateRunningRequest(options, handle);
                    job = running;
                }
                else
                {
                    if (h.AnalysisStarted) new AnalysisOptions(h.SelectedAdapter, h.SelectedPowerState, h.SelectedFlags).ValidateRunningRequest(options, handle);
                    else h.PendingAnalysisOptions = options;
                    job = Tools.StartPreparation(session, jobs, handle, preparation);
                }
            }
            return Json.Serialize(await jobs.WaitOrStatus(job, waitSeconds, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            throw PixErrors.ToMcp(ex, "pix_gpu_analysis_start");
        }
    }

    [McpServerTool(Name = "pix_gpu_analysis_status", Title = "GPU analysis status", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Whether analysis is connected/started for a GPU capture, which adapter is used, and which data has been collected.")]
    public static Task<string> Status(PixSession session, [Description("GPU capture handle")] string handle, CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_gpu_analysis_status", () => session.Get<GpuCaptureHandle>(handle).AnalysisStatus(), cancellationToken);

    [McpServerTool(Name = "pix_gpu_analysis_adapters", Title = "List analysis adapters", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Lists the GPU adapters (and their power states) available for analysing this capture. Connects to the local PIX device if needed.")]
    public static Task<string> Adapters(PixSession session, [Description("GPU capture handle")] string handle, CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_gpu_analysis_adapters", () =>
        {
            GpuCaptureHandle h = session.Get<GpuCaptureHandle>(handle);
            h.EnsureConnected(null);
            var adapters = new List<object>();
            foreach ((ulong id, string name) in h.Adapters ?? new())
            {
                object? powerStates;
                try { powerStates = h.PowerStates(id); }
                catch (Exception ex) { powerStates = PixErrors.Unavailable("powerStates", ex); }
                adapters.Add(new { id, name, vendor = GpuVendors.Name(GpuVendors.FromAdapterName(name)), powerStates });
            }
            return new { adapters, selectedAdapter = h.SelectedAdapter, selectedPowerState = h.SelectedPowerState };
        }, cancellationToken);

    [McpServerTool(Name = "pix_gpu_analysis_stop", Title = "Stop GPU analysis", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false), Description("Stops analysis and disconnects from the GPU, discarding collected timing/counter data. The capture stays open. Required before pix_gpu_analysis_start can use a different adapter, power state or flags.")]
    public static Task<string> Stop(PixSession session, [Description("GPU capture handle")] string handle, CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_gpu_analysis_stop", () =>
        {
            GpuCaptureHandle h = session.Get<GpuCaptureHandle>(handle);
            var warnings = new List<string>();
            h.StopAnalysis(warnings);
            return new { stopped = true, warnings = warnings.Count == 0 ? null : warnings, analysis = h.AnalysisStatus() };
        }, cancellationToken);
}
