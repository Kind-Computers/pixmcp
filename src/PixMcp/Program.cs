using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PixMcp.Pix;

// NOTE: this file must not reference any Microsoft.PIX type. PixDiscovery's module initializer
// registers the assembly resolver for PixApiCsExt.experimental.dll before Main runs; every PIX
// call lives behind PixSession / PixWorker.

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
            "pix_job_status or block with pix_job_wait. Call pix_close when done with a handle.";
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    .WithResourcesFromAssembly();

await builder.Build().RunAsync();
