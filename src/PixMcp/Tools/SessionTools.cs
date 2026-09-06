using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using PixMcp.Pix;

namespace PixMcp.Tools;

[McpServerToolType]
public static class SessionTools
{
    [McpServerTool(Name = "pix_info", ReadOnly = true), Description("Reports the PIX install being used, whether the PIX API loaded, Windows Developer Mode state, open handles and jobs. Call this first if anything fails.")]
    public static Task<string> Info(
        PixSession session,
        JobManager jobs,
        [Description("When true, also creates the PIX factory to verify the native API loads.")] bool probe = false)
        => Tools.Run(session, "pix_info", () =>
        {
            string? probeError = null;
            if (probe)
            {
                try { _ = session.Factory; }
                catch (Exception ex) { probeError = PixErrors.Describe(ex); }
            }
            return new
            {
                pix = new
                {
                    installDir = PixDiscovery.InstallDir,
                    version = PixDiscovery.Version,
                    discoveredVia = PixDiscovery.Source,
                    discoveryError = PixDiscovery.Error,
                    apiLoaded = session.FactoryCreated,
                    probeError,
                },
                developerModeEnabled = DeveloperModeEnabled(),
                process = new
                {
                    pid = Environment.ProcessId,
                    is64Bit = Environment.Is64BitProcess,
                    runtime = RuntimeInformation.FrameworkDescription,
                    os = RuntimeInformation.OSDescription,
                },
                handles = session.Handles.Select(h => h.Summary()).ToArray(),
                jobs = jobs.All.Select(j => j.ToDto(includeResult: false)).ToArray(),
            };
        });

    private static bool? DeveloperModeEnabled()
    {
        try
        {
            object? value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock", "AllowDevelopmentWithoutDevLicense", null);
            return value is int i && i != 0;
        }
        catch
        {
            return null;
        }
    }

    [McpServerTool(Name = "pix_handles", ReadOnly = true), Description("Lists open handles (GPU captures, timing captures, dump files, device connections) with their summaries.")]
    public static Task<string> Handles(PixSession session)
        => Tools.Run(session, "pix_handles", () => session.Handles.Select(h => h.Summary()).ToArray());

    [McpServerTool(Name = "pix_close"), Description("Closes a handle: stops any running analysis, disconnects, and releases the document.")]
    public static Task<string> Close(PixSession session, [Description("Handle id, e.g. gpu-1")] string handle)
        => Tools.Run(session, "pix_close", () => session.Close(handle));

    [McpServerTool(Name = "pix_close_all"), Description("Closes every open handle.")]
    public static Task<string> CloseAll(PixSession session)
        => Tools.Run(session, "pix_close_all", () => session.CloseAll());

    [McpServerTool(Name = "pix_jobs", ReadOnly = true), Description("Lists background jobs (analysis start, Dr. PIX runs, symbol resolution, captures) and their status.")]
    public static string Jobs(JobManager jobs) => Json.Serialize(jobs.All.Select(j => j.ToDto(includeResult: false)).ToArray());

    [McpServerTool(Name = "pix_job_status", ReadOnly = true), Description("Returns a job's status, progress, recent status messages, and its result once finished.")]
    public static string JobStatus(JobManager jobs, [Description("Job id, e.g. job-1")] string jobId)
    {
        Job job = jobs.Get(jobId);
        return Json.Serialize(job.ToDto(includeResult: true));
    }

    [McpServerTool(Name = "pix_job_wait"), Description("Blocks until a job finishes or the timeout elapses, then returns its status and result.")]
    public static async Task<string> JobWait(
        JobManager jobs,
        [Description("Job id, e.g. job-1")] string jobId,
        [Description("Maximum seconds to wait (default 120).")] double timeoutSeconds = 120,
        CancellationToken cancellationToken = default)
    {
        Job job = jobs.Get(jobId);
        return Json.Serialize(await jobs.WaitOrStatus(job, Math.Clamp(timeoutSeconds, 0, 3600), cancellationToken).ConfigureAwait(false));
    }

    [McpServerTool(Name = "pix_job_cancel"), Description("Requests cancellation of a running job (best effort; PIX honours it at its next checkpoint).")]
    public static string JobCancel(JobManager jobs, [Description("Job id")] string jobId)
    {
        Job job = jobs.Get(jobId);
        if (job.IsFinished)
        {
            throw new McpException($"Job {jobId} already finished with status {job.Status}.");
        }
        jobs.Cancel(job);
        return Json.Serialize(job.ToDto(includeResult: false));
    }

    [McpServerTool(Name = "pix_log", ReadOnly = true), Description("Returns recent PIX engine log messages (warnings/errors reported by PIX itself). Useful when a call fails without a clear reason.")]
    public static string Log(PixSession session, [Description("Number of most recent entries (default 50).")] int count = 50)
        => Json.Serialize(session.Log.Recent(Math.Clamp(count, 1, 500)));
}
