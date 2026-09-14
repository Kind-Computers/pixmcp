using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PixMcp.Pix;

namespace PixMcp;

internal static class ServerHost
{
    /// <summary>The server version reported to MCP clients: the assembly's informational version without build metadata.</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>
    /// Sent with every initialize, so it stays at 800 characters or fewer (pinned by PromptTests). Tool descriptions, nextCalls,
    /// the playbook prompts and docs/investigation-ladder.md carry the detail.
    /// </summary>
    internal const string Instructions =
        "PIX on Windows MCP server. Open captures with pix_gpu_open, pix_timing_open or pix_dump_open; each returns a handle. " +
        "Start at Level 0: pix_gpu_overview, pix_gpu_bottleneck, pix_timing_overview, pix_timing_verdict or pix_dump_triage. " +
        "References (eventRef, resourceRef, shaderRef) are JSON objects: copy them from results; scope is an eventRef. " +
        "{ pending, jobId } means call pix_job_wait with that jobId, then repeat the call. Read a resultRef with pix_result_read. " +
        "Follow nextCalls: they carry exact arguments and a cost hint. The prompts are step-by-step investigation playbooks. " +
        "GPU analysis replays the capture on the local GPU; replay timing is not application frame latency. Call pix_close when done.";

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

    public static async Task RunAsync(string[] args, Stream? protocolOutput = null)
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

        IMcpServerBuilder server = builder.Services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new() { Name = "pixmcp", Version = Version };
                o.ServerInstructions = Instructions;
            });
        // A claimed protocol stream keeps native writes to standard output out of the MCP transport (see ProtocolStdout).
        server = protocolOutput is null ? server.WithStdioServerTransport() : server.WithStreamServerTransport(Console.OpenStandardInput(), protocolOutput);
        server
            .WithRequestFilters(StructuredToolResults.Configure)
            .WithToolsFromAssembly()
            .WithResourcesFromAssembly()
            .WithPromptsFromAssembly();

        await builder.Build().RunAsync();
    }
}
