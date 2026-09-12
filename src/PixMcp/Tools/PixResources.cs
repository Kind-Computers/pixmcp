using System.ComponentModel;
using ModelContextProtocol.Server;
using PixMcp.Pix;

namespace PixMcp.Tools;

/// <summary>Read-only MCP resources mirroring the handle and job tables (the same data as pix_handles / pix_jobs, for clients that prefer resources).</summary>
[McpServerResourceType]
public static class PixResources
{
    [McpServerResource(UriTemplate = "pix://handles", Name = "Open PIX handles", MimeType = "application/json"), Description("The list of open PIX handles with their summaries.")]
    public static string Handles(PixSession session) => Json.Serialize(session.Handles.Select(h => h.Summary()).ToArray());

    [McpServerResource(UriTemplate = "pix://handles/{handle}", Name = "PIX handle summary", MimeType = "application/json"), Description("Summary of one open handle.")]
    public static string Handle(PixSession session, [Description("Handle id, e.g. gpu-1 (see pix://handles).")] string handle) => Json.Serialize(session.Get(handle).Summary());

    [McpServerResource(UriTemplate = "pix://jobs", Name = "PIX jobs", MimeType = "application/json"), Description("Background jobs with status, progress and messages (results omitted; read pix://jobs/{jobId} or call pix_job_status).")]
    public static string Jobs(JobManager jobs) => Json.Serialize(jobs.All.Select(j => j.ToDto()).ToArray());

    [McpServerResource(UriTemplate = "pix://jobs/{jobId}", Name = "PIX job", MimeType = "application/json"), Description("Compact job status with a resultRef once finished; use pix_result_read for the complete result.")]
    public static string Job(JobManager jobs, [Description("Job id, e.g. job-1 (see pix://jobs).")] string jobId) => Json.Serialize(jobs.Get(jobId).ToDto());
}
