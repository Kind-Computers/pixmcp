using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PixMcp.Pix;

namespace PixMcp;

internal static class ServerHost
{
    public static async Task RunAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // stdout is the MCP transport; all logging goes to stderr.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        builder.Services.AddSingleton<PixWorker>();
        builder.Services.AddSingleton<PixSession>();
        builder.Services.AddSingleton<JobManager>();

        builder.Services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new() { Name = "pixmcp", Version = "0.1.0" };
                o.ServerInstructions =
                    "PIX on Windows MCP server. Open a .wpix GPU capture with pix_gpu_open (returns a handle), " +
                    "a timing capture with pix_timing_open, or a DirectX dump (.dxdmp_preview) with pix_dump_open. " +
                    "Enumeration tools are paged (offset/limit). GPU analysis tools (timing, counters, pipeline " +
                    "state, resources, Dr. PIX) replay the capture on the local GPU; they start analysis " +
                    "automatically, which can take a while the first time. Long operations return a jobId: poll " +
                    "pix_job_status or block with pix_job_wait. Use pix_gpu_counters_start to collect counters " +
                    "in the background before paging pix_gpu_counters_collect. Cancellation is best effort; " +
                    "cancellationRequested does not mean an operation was interrupted. Call pix_close when done with a handle.";
            })
            .WithStdioServerTransport()
            .WithRequestFilters(StructuredToolResults.Configure)
            .WithToolsFromAssembly()
            .WithResourcesFromAssembly();

        await builder.Build().RunAsync();
    }
}
