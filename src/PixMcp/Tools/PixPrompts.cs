using System.ComponentModel;
using ModelContextProtocol.Server;
using PixMcp.Pix;

namespace PixMcp.Tools;

/// <summary>
/// Investigation playbooks served as MCP prompts. The text comes from <see cref="Playbooks"/>; the titles, descriptions and
/// argument names here must match it (pinned by PromptTests). Prompts whose tools PIXMCP_TOOLSETS hides are not listed.
/// </summary>
[McpServerPromptType]
public static class PixPrompts
{
    private static string Render(string name, params (string Name, string? Value)[] arguments)
        => Playbooks.Render(name, arguments.ToDictionary(a => a.Name, a => a.Value, StringComparer.Ordinal));

    [McpServerPrompt(Name = "pix_frame_budget", Title = "Where the GPU frame goes"),
     Description("Frame budget playbook for a GPU capture: queue totals, top passes and draws, per-pass rollup, queue overlap and the costliest event.")]
    public static string FrameBudget(
        [Description(Playbooks.GpuHandleArgument)] string? handle = null,
        [Description(Playbooks.EventArgument)] string? eventRef = null)
        => Render("pix_frame_budget", ("handle", handle), ("eventRef", eventRef));

    [McpServerPrompt(Name = "pix_regression", Title = "Find a GPU regression between two captures"),
     Description("Regression playbook: compare a baseline and a candidate capture, filter the saved comparison, check noise and inspect the regressed event.")]
    public static string Regression(
        [Description(Playbooks.BaselineHandleArgument)] string? baselineHandle = null,
        [Description(Playbooks.CandidateHandleArgument)] string? handle = null,
        [Description("Optional marker path prefix that focuses the comparison, e.g. Frame/Lighting.")] string? markerPathPrefix = null,
        [Description("eventRef JSON object of the regressed event on the candidate.")] string? eventRef = null)
        => Render("pix_regression", ("baselineHandle", baselineHandle), ("handle", handle), ("markerPathPrefix", markerPathPrefix), ("eventRef", eventRef));

    [McpServerPrompt(Name = "pix_dispatch_slow", Title = "Why is this dispatch or draw slow"),
     Description("Event playbook: inspect one dispatch or draw, classify its limiter, read counters and occupancy, run Dr. PIX experiments and read its shader.")]
    public static string DispatchSlow(
        [Description(Playbooks.GpuHandleArgument)] string? handle = null,
        [Description("eventRef JSON object of the dispatch or draw, e.g. {\"handle\":\"gpu-1\",\"queueIndex\":2,\"eventIndex\":5}.")] string? eventRef = null)
        => Render("pix_dispatch_slow", ("handle", handle), ("eventRef", eventRef));

    [McpServerPrompt(Name = "pix_cpu_vs_gpu", Title = "Is the recorded run CPU, GPU or sync bound"),
     Description("Timing capture playbook: frame verdict and overview, recorded GPU queue summary, then CPU hotspots or thread waits of the worst frame.")]
    public static string CpuVsGpu([Description(Playbooks.TimingHandleArgument)] string? handle = null)
        => Render("pix_cpu_vs_gpu", ("handle", handle));

    [McpServerPrompt(Name = "pix_crash_triage", Title = "Triage a GPU crash or device removal"),
     Description("DirectX dump playbook: ranked triage evidence, dump metadata, queue status, runtime journal and page faults.")]
    public static string CrashTriage([Description(Playbooks.DumpHandleArgument)] string? handle = null)
        => Render("pix_crash_triage", ("handle", handle));

    [McpServerPrompt(Name = "pix_async_overlap", Title = "Do the GPU queues overlap"),
     Description("Async compute playbook: queue overlap verdicts, idle bubbles with causes and the events that explain them.")]
    public static string AsyncOverlap(
        [Description(Playbooks.GpuHandleArgument)] string? handle = null,
        [Description(Playbooks.EventArgument)] string? eventRef = null)
        => Render("pix_async_overlap", ("handle", handle), ("eventRef", eventRef));

    [McpServerPrompt(Name = "pix_bandwidth_hogs", Title = "Find memory bandwidth hogs"),
     Description("Resource playbook: largest resources, heaviest read traffic in a scope, one resource's timeline and bandwidth counters.")]
    public static string BandwidthHogs(
        [Description(Playbooks.GpuHandleArgument)] string? handle = null,
        [Description(Playbooks.ScopeArgument)] string? scope = null,
        [Description(Playbooks.PrefixArgument)] string? markerPathPrefix = null)
        => Render("pix_bandwidth_hogs", ("handle", handle), ("scope", scope), ("markerPathPrefix", markerPathPrefix));

    [McpServerPrompt(Name = "pix_fill_vs_vertex", Title = "Is a pass fill-rate or vertex bound"),
     Description("Shading playbook: bottleneck classification with Dr. PIX evidence, shader cost rollup, per-stage counters and render-target size.")]
    public static string FillVsVertex(
        [Description(Playbooks.GpuHandleArgument)] string? handle = null,
        [Description(Playbooks.ScopeArgument)] string? scope = null,
        [Description(Playbooks.PrefixArgument)] string? markerPathPrefix = null,
        [Description("eventRef JSON object of a representative draw in the pass.")] string? eventRef = null)
        => Render("pix_fill_vs_vertex", ("handle", handle), ("scope", scope), ("markerPathPrefix", markerPathPrefix), ("eventRef", eventRef));

    [McpServerPrompt(Name = "pix_sql_investigation", Title = "Answer a question with SQL"),
     Description("SQL playbook for timing and GPU captures: read the schema, run a named query, then adapt its SQL.")]
    public static string SqlInvestigation([Description(Playbooks.AnyHandleArgument)] string? handle = null)
        => Render("pix_sql_investigation", ("handle", handle));

    [McpServerPrompt(Name = "pix_gpu_bottleneck", Title = "Classify what limits a GPU pass"),
     Description("Bottleneck playbook: one-call limiter verdict, fuller evidence, then the counters, occupancy and events behind it.")]
    public static string Bottleneck(
        [Description(Playbooks.GpuHandleArgument)] string? handle = null,
        [Description(Playbooks.ScopeArgument)] string? scope = null,
        [Description(Playbooks.PrefixArgument)] string? markerPathPrefix = null)
        => Render("pix_gpu_bottleneck", ("handle", handle), ("scope", scope), ("markerPathPrefix", markerPathPrefix));
}
