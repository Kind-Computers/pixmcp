using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using PixMcp.Pix;

namespace PixMcp.Tools;

/// <summary>Read-only MCP resources mirroring the handle, job and result tables (the same envelopes as the tools, for clients that prefer resources).</summary>
[McpServerResourceType]
public static class PixResources
{
    [McpServerResource(UriTemplate = "pix://handles", Name = "Open PIX handles", MimeType = "application/json"), Description("The open PIX handles with their summaries, as the pix_handles page envelope.")]
    public static string Handles(PixSession session)
        => Serve(session, () => SessionTools.Envelope(session.Handles.Select(h => h.Summary()).ToArray()));

    [McpServerResource(UriTemplate = "pix://handles/{handle}", Name = "PIX handle summary", MimeType = "application/json"), Description("Summary of one open handle; an unknown id returns an error envelope (unknown_handle).")]
    public static string Handle(PixSession session, [Description("Handle id, e.g. gpu-1 (see pix://handles).")] string handle)
        => Serve(session, () => session.Get(handle).Summary());

    [McpServerResource(UriTemplate = "pix://jobs", Name = "PIX jobs", MimeType = "application/json"), Description("Background jobs with status, progress and messages as the pix_jobs page envelope (results omitted; read pix://jobs/{jobId} or call pix_job_status).")]
    public static string Jobs(PixSession session, JobManager jobs)
        => Serve(session, () => SessionTools.Envelope(jobs.All.Select(j => j.ToDto()).ToArray()));

    [McpServerResource(UriTemplate = "pix://jobs/{jobId}", Name = "PIX job", MimeType = "application/json"), Description("Compact job status with a resultRef once finished; use pix_result_read for the complete result.")]
    public static string Job(PixSession session, JobManager jobs, [Description("Job id, e.g. job-1 (see pix://jobs).")] string jobId)
        => Serve(session, () => jobs.Get(jobId).ToDto());

    [McpServerResource(UriTemplate = "pix://results/{resultRef}", Name = "PIX result window", MimeType = "application/json"), Description("The first page (25 items) of a retained result, exactly as pix_result_read returns it; page further with the tool.")]
    public static string Result(PixSession session, [Description("Result reference, e.g. r-3f9a1c2e7k.")] string resultRef)
        => Serve(session, () => session.Results.Query(resultRef, "", 0, 25, "values", null, null), bounded: true);

    /// <summary>
    /// Serializes a resource body under the same budgets as tool results: oversized bodies become a deferred snapshot
    /// (except reader windows, which are already bounded) and failures become the ErrorDto envelope instead of a raw
    /// JSON-RPC error.
    /// </summary>
    internal static string Serve(PixSession session, Func<object?> produce, bool bounded = false)
    {
        try
        {
            string json = Json.Serialize(produce());
            if (!bounded && Encoding.UTF8.GetByteCount(json) > ResultStore.TargetBytes)
            {
                using JsonDocument document = JsonDocument.Parse(json);
                string resultRef = session.Results.StoreElement(document.RootElement, []);
                json = Json.Serialize(new DeferredResultDto(true, resultRef, Encoding.UTF8.GetByteCount(json), ResultStore.DeferredCalls(resultRef)));
            }
            Tools.EnsureByteBudget(json, "resource");
            return json;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Json.Serialize(PixErrors.ToDto(ex));
        }
    }
}
