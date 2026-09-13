using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PixMcp.Pix;

namespace PixMcp;

internal static class ServerHost
{
    /// <summary>The server version reported to MCP clients: the assembly's informational version without build metadata.</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        string? informational = typeof(ServerHost).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;
        string version = string.IsNullOrWhiteSpace(informational) ? typeof(ServerHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.0" : informational;
        int metadata = version.IndexOf('+');
        return metadata > 0 ? version[..metadata] : version;
    }

    public static async Task RunAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // stdout is the MCP transport; all logging goes to stderr. The level defaults to Information
        // but honours the standard configuration, e.g. Logging__LogLevel__Default=Debug in the
        // environment to also see PIX engine informational messages.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        if (builder.Configuration["Logging:LogLevel:Default"] is null)
        {
            builder.Logging.SetMinimumLevel(LogLevel.Information);
        }

        builder.Services.AddSingleton<PixWorker>();
        builder.Services.AddSingleton<PixSession>();
        builder.Services.AddSingleton<JobManager>();

        builder.Services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new() { Name = "pixmcp", Version = Version };
                o.ServerInstructions =
                    "PIX on Windows MCP server. Open a .wpix GPU capture with pix_gpu_open (returns a handle), " +
                    "a timing capture with pix_timing_open, or a DirectX dump (.dxdmp_preview) with pix_dump_open. " +
                    "Start with pix_gpu_overview, then pass eventRef to pix_gpu_inspect_event. " +
                    "Follow resourceRef with pix_gpu_resource_uses and shaderRef with pix_gpu_shader_code or pix_gpu_shader_search. " +
                    "Use pix_gpu_shader_diagnostics for HLSL/IL/ISA node availability and PDB hash evidence when source is missing. " +
                    "Use pix_gpu_shaders and pix_gpu_shader_uses for shader inventory and reverse navigation. " +
                    "Use pix_gpu_compare for baseline/candidate differences, then pix_gpu_compare_changes to filter its saved fullResultRef without replay. " +
                    "Use pix_dump_triage for crash evidence. " +
                    "Enumeration tools are paged (offset/limit, default 25). GPU analysis tools (timing, counters, pipeline " +
                    "state, resources, Dr. PIX) replay the capture on the local GPU. They start analysis " +
                    "automatically as a background job and wait up to waitSeconds for it; if it is still running " +
                    "they return { pending: true, jobId }: call pix_job_wait with that jobId, then repeat the call. " +
                    "pix_gpu_overview and pix_gpu_inspect_event answer partially instead: metadata now, and { pending, jobId } inside the sections that wait for replay. " +
                    "Paged tools accept format=table (positional rows with a legend), brief, topN and maxStringLength; nextCalls carry a cost hint; a repeated provenance block is a { provenanceRef, unchanged } stub. " +
                    "Long operations return a jobId: poll pix_job_status or block with pix_job_wait, then read resultRef with pix_result_read. " +
                    "Large results also return resultRef; use JSON pointers and offsets to retrieve every nested value, or pix_result_export to save JSON. " +
                    "Result snapshots use bounded memory and temporary disk; close, pruning, or storage pressure can expire them. " +
                    "Use nextCalls for exact recovery and continuation arguments. All PIX work runs " +
                    "on one thread. Query waitSeconds includes queue admission; worker_busy returns recovery calls if admission times out. " +
                    "pix_info shows the active operation and storage usage. Queued cancellation is immediate; running native cancellation is best effort. " +
                    "Use pix_gpu_counters_prepare to collect counters in the background before paging pix_gpu_counters_read. " +
                    "Timing describes GPU replay; nested marker sums and replay queue spans are not application frame latency. " +
                    "Recorded timing captures use pix_timing_overview, pix_timing_events, pix_timing_counters_list/read, " +
                    "pix_timing_hotspots and pix_timing_calltree without GPU replay. Times are nanoseconds with exclusive interval ends. " +
                    "CPU samples are statistical counts, not exact CPU time; inspect stack/symbol coverage and explicitly resolve symbols when needed. " +
                    "Use pix_timing_submissions for recorded CPU queue submission to GPU execution correlation, then follow threadRowId " +
                    "with pix_timing_thread_switches for exact recorded scheduling transitions and switch-out stacks. These do not establish " +
                    "arbitrary draw correlation or the cause of GPU idle time. Enable contextSwitchStacks and captureSysmonCounters " +
                    "explicitly when taking a timing capture for this workflow. " +
                    "pix_gpu_preview uses pixtool and requires all connected analyses to be stopped first. " +
                    "pix_gpu_export_cpp also uses pixtool with that coordination; it exports to a new directory and keeps generated files. " +
                    "pix_csv_compare uses optional pixdiff to compare recorded Unreal CSVs (candidate minus baseline, median by default). " +
                    "Read the saved resultRef and use pix_csv_pass_candidates to locate possible PIX markers by CSV pass name. " +
                    "CSV comparisons run independently of the PIX worker. Name matches do not establish identity or make CSV and replay timings equivalent. " +
                    "pix_gpu_preview_image retrieves preview or screenshot artifacts with optional crop/maxDimension; use ignoreAlpha=true to view stored RGB as opaque when render-target alpha hides scene colors. Byte retrieval preserves originals. " +
                    "Live GPU capture waits for target readiness (default 30 seconds) with optional warmup (default zero). " +
                    "Cancellation is best effort; cancellationRequested does not mean an operation was interrupted. " +
                    "Call pix_close when done with a handle.";
            })
            .WithStdioServerTransport()
            .WithRequestFilters(StructuredToolResults.Configure)
            .WithToolsFromAssembly()
            .WithResourcesFromAssembly();

        await builder.Build().RunAsync();
    }
}
