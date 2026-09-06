using System.ComponentModel;
using Microsoft.PIX;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using PixMcp.Pix.Handles;

namespace PixMcp.Tools;

[McpServerToolType]
public static class AnalysisTools
{
    [McpServerTool(Name = "pix_gpu_analysis_start"), Description("Connects the capture to the local GPU and starts analysis (replay). Required for timing, counters, pipeline state, resources and Dr. PIX; those tools also start it implicitly, but for big captures start it here so the wait is visible. Returns a job; poll pix_job_status or pass waitSeconds.")]
    public static async Task<string> Start(
        PixSession session,
        JobManager jobs,
        [Description("GPU capture handle")] string handle,
        [Description("Adapter id from pix_gpu_analysis_adapters (default: chosen by PIX).")] ulong? adapterId = null,
        [Description("Power state id from pix_gpu_analysis_adapters (default: chosen by PIX).")] uint? powerStateId = null,
        [Description("Analysis flags, e.g. IGNORE_INCOMPATIBILITIES, USE_SINGLE_COMMAND_QUEUE, ENABLE_DEBUG_LAYER, ENABLE_RECREATE_AT_GPUVA, DISABLE_GPU_PLUGINS.")] string[]? flags = null,
        [Description("Seconds to wait inline for completion before returning (default 0 = return the job immediately).")] double waitSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            PIX_ANALYSIS_FLAGS? requestedFlags = null;
            if (flags is { Length: > 0 })
            {
                PIX_ANALYSIS_FLAGS combined = 0;
                foreach (string flag in flags)
                {
                    combined |= Tools.ParseEnum<PIX_ANALYSIS_FLAGS>(flag);
                }
                requestedFlags = combined;
            }
            var options = new AnalysisOptions(adapterId, powerStateId, requestedFlags);
            Job job = jobs.StartForHandle<GpuCaptureHandle>("analysis", $"Start GPU analysis for {handle}", handle, (j, h) =>
            {
                if (h.ConfigureAnalysis(options))
                {
                    return new { alreadyStarted = true, analysis = h.AnalysisStatus() };
                }
                h.EnsureAnalysisStarted(j);
                return h.AnalysisStatus();
            });
            return Json.Serialize(await jobs.WaitOrStatus(job, waitSeconds, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            throw PixErrors.ToMcp(ex, "pix_gpu_analysis_start");
        }
    }

    [McpServerTool(Name = "pix_gpu_analysis_status", ReadOnly = true), Description("Whether analysis is connected/started for a GPU capture, which adapter is used, and which data has been collected.")]
    public static Task<string> Status(PixSession session, [Description("GPU capture handle")] string handle)
        => Tools.Run(session, "pix_gpu_analysis_status", () => session.Get<GpuCaptureHandle>(handle).AnalysisStatus());

    [McpServerTool(Name = "pix_gpu_analysis_adapters"), Description("Lists the GPU adapters (and their power states) available for analysing this capture. Connects to the local PIX device if needed.")]
    public static Task<string> Adapters(PixSession session, [Description("GPU capture handle")] string handle)
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
                adapters.Add(new { id, name, powerStates });
            }
            return new { adapters, selectedAdapter = h.SelectedAdapter, selectedPowerState = h.SelectedPowerState };
        });

    [McpServerTool(Name = "pix_gpu_analysis_stop"), Description("Stops analysis and disconnects from the GPU, discarding collected timing/counter data. The capture stays open.")]
    public static Task<string> Stop(PixSession session, [Description("GPU capture handle")] string handle)
        => Tools.Run(session, "pix_gpu_analysis_stop", () =>
        {
            GpuCaptureHandle h = session.Get<GpuCaptureHandle>(handle);
            var warnings = new List<string>();
            h.StopAnalysis(warnings);
            return new { stopped = true, warnings = warnings.Count == 0 ? null : warnings, analysis = h.AnalysisStatus() };
        });
}
