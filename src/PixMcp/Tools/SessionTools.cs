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
    // pix_info and pix_handles deliberately stay off the PIX worker thread (except for the optional
    // factory probe): they are the diagnostics an agent reaches for when calls stall, so they must
    // answer even while a replay occupies the worker. Everything they read is immutable or a
    // thread-safe snapshot.
    [McpServerTool(Name = "pix_info", ReadOnly = true), Description("Reports the PIX install being used, whether the PIX API loaded, Windows Developer Mode state, open handles, jobs, and whether the single PIX worker thread is busy (queued calls wait behind the running job). Call this first if anything fails or stalls.")]
    public static async Task<string> Info(
        PixSession session,
        JobManager jobs,
        [Description("When true, also creates the PIX factory to verify the native API loads (runs on the PIX thread).")] bool probe = false,
        CancellationToken cancellationToken = default)
    {
        string? probeError = null;
        if (probe)
        {
            try { await session.Run(() => session.Factory, cancellationToken, "pix_info: factory probe").ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { probeError = PixErrors.Describe(ex); }
        }
        Job? running = jobs.Running;
        WorkerSnapshot worker = session.Worker.Snapshot();
        return Json.Serialize(new
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
            pixdiff = PixDiffDiscovery.Info(),
            process = new
            {
                pid = Environment.ProcessId,
                is64Bit = Environment.Is64BitProcess,
                runtime = RuntimeInformation.FrameworkDescription,
                os = RuntimeInformation.OSDescription,
                version = ServerHost.Version,
            },
            worker = new
            {
                busy = worker.Busy,
                runningJob = running?.Id,
                queuedCalls = worker.QueuedCalls,
                operation = worker.Operation,
                startedAt = worker.StartedAt,
                elapsedSeconds = worker.ElapsedSeconds,
            },
            results = session.Results.Summary(),
            handles = session.Handles.Select(h => h.Summary()).ToArray(),
            jobs = jobs.All.Select(j => j.ToDto()).ToArray(),
        });
    }

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

    [McpServerTool(Name = "pix_handles", ReadOnly = true), Description("Lists open handles (GPU captures, timing captures, dump files, device connections) with their summaries. Answers even while the PIX thread is busy.")]
    public static string Handles(PixSession session) => Json.Serialize(session.Handles.Select(h => h.Summary()).ToArray());

    [McpServerTool(Name = "pix_close", Destructive = true), Description("Closes a handle: stops any running analysis, disconnects, and releases the document. Collected timing/counter data for the handle is discarded.")]
    public static Task<string> Close(PixSession session, [Description("Handle id, e.g. gpu-1")] string handle, CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_close", () => session.Close(handle), cancellationToken);

    [McpServerTool(Name = "pix_close_all", Destructive = true), Description("Closes every open handle.")]
    public static Task<string> CloseAll(PixSession session, CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_close_all", () => session.CloseAll(), cancellationToken);

    [McpServerTool(Name = "pix_jobs", ReadOnly = true), Description("Lists background jobs (analysis start, timing/counter collection, Dr. PIX runs, symbol resolution, captures) and their status. Only the most recent finished jobs are retained.")]
    public static string Jobs(JobManager jobs) => Json.Serialize(jobs.All.Select(j => j.ToDto()).ToArray());

    [McpServerTool(Name = "pix_job_status", ReadOnly = true), Description("Returns compact job status, progress, recent messages and a resultRef once finished. Read the result with the exact pix_result_read nextCall.")]
    public static string JobStatus(JobManager jobs, [Description("Job id, e.g. job-1")] string jobId)
    {
        Job job = jobs.Get(jobId);
        return Json.Serialize(job.ToDto());
    }

    [McpServerTool(Name = "pix_job_wait", ReadOnly = true), Description("Blocks until a job finishes or the timeout elapses, then returns compact status and a resultRef. Job payloads are retrieved with pix_result_read.")]
    public static async Task<string> JobWait(
        JobManager jobs,
        [Description("Job id, e.g. job-1")] string jobId,
        [Description("Maximum seconds to wait (default 120, max 3600).")] double timeoutSeconds = 120,
        CancellationToken cancellationToken = default)
    {
        Job job = jobs.Get(jobId);
        return Json.Serialize(await jobs.WaitOrStatus(job, Math.Clamp(timeoutSeconds, 0, 3600), cancellationToken).ConfigureAwait(false));
    }

    [McpServerTool(Name = "pix_job_cancel"), Description("Requests cancellation of a running job (best effort; PIX honours it at its next checkpoint, and work that completes first stays succeeded).")]
    public static string JobCancel(JobManager jobs, [Description("Job id")] string jobId)
    {
        Job job = jobs.Get(jobId);
        if (job.IsFinished)
        {
            throw new McpException($"Job {jobId} already finished with status {job.Status}.");
        }
        jobs.Cancel(job);
        return Json.Serialize(job.ToDto());
    }

    [McpServerTool(Name = "pix_log", ReadOnly = true), Description("Returns recent PIX engine log messages (warnings/errors reported by PIX itself). Useful when a call fails without a clear reason.")]
    public static string Log(PixSession session, [Description("Number of most recent entries (default 50, max 500).")] int count = 50)
        => Json.Serialize(session.Log.Recent(Math.Clamp(count, 1, 500)));
}
