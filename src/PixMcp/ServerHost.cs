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
                    "Enumeration tools are paged (offset/limit). GPU analysis tools (timing, counters, pipeline " +
                    "state, resources, Dr. PIX) replay the capture on the local GPU. They start analysis " +
                    "automatically as a background job and wait up to waitSeconds for it; if it is still running " +
                    "they return { pending: true, jobId }: call pix_job_wait with that jobId, then repeat the call. " +
                    "Long operations return a jobId: poll pix_job_status or block with pix_job_wait. All PIX work runs " +
                    "on one thread, so calls queue behind a running job (pix_info shows worker.busy). " +
                    "Use pix_gpu_counters_start to collect counters in the background before paging pix_gpu_counters_collect. " +
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
