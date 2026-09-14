using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.PIX;
using Microsoft.PIX.Extension;
using Microsoft.PIX.Extension.GpuCapture;
using Microsoft.PIX.Extension.GpuCapture.Resources;
using Microsoft.PIX.Internal;
using Microsoft.PIX.Internal.Extension.Shaders;
using Microsoft.PIX.Internal.Extension.ShaderProfiling.Types;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using PixMcp.Pix.Handles;
using PixMcp.Pix.StaticProfiling;

namespace PixMcp.Tools;

/// <summary>Static shader profiling with the AMD and Intel offline compilers shipped in the PIX install (experimental PIX API).</summary>
[McpServerToolType]
public static class StaticShaderProfilingTools
{
    private static readonly ConditionalWeakTable<GpuCaptureHandle, StaticProfileJobCache> CaptureJobs = new();

    [McpServerTool(Name = "pix_shader_targets", Title = "List static profiling targets", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Lists the AMD and Intel GPU targets that pix_gpu_shader_static_profile can compile for with the offline compilers in the PIX install, whatever GPU this machine has. Ids can change between PIX releases, so pass an architecture (Xe2-HPG, RDNA4), family (gfx1201) or adapter name as the target; extra.families gives one stable spelling per family.")]
    public static Task<string> Targets(
        PixSession session,
        [Description("Only targets of this vendor: intel or amd (default both).")] string? vendor = null,
        [Description("Only targets whose name, family or architecture contains this text, case-insensitive (default all).")] string? nameContains = null,
        [Description("First row to return (default 0).")] int offset = 0,
        [Description("Maximum rows to return (default 25, max 1000).")] int limit = Paging.DefaultLimit,
        CancellationToken cancellationToken = default)
    {
        if (vendor is not null && !StaticTargets.Vendors.Contains(vendor.Trim().ToLowerInvariant()))
            throw PixErrors.InvalidArguments("vendor must be intel or amd; static shader profiling has no other offline compilers.");
        ReferenceValidation.Page(offset, limit);
        return Tools.Run(session, "pix_shader_targets", () =>
        {
            IReadOnlyList<ShaderTargetDto> all = session.ShaderProfiling.Targets;
            ShaderTargetDto[] filtered = StaticTargets.Filter(all, vendor, nameContains);
            ShaderTargetDto[] page = filtered.Skip(offset).Take(limit).ToArray();
            // Known issues are per vendor, so they travel once in extra instead of on every row.
            Dictionary<string, IReadOnlyList<string>> issues = StaticTargets.Vendors
                .ToDictionary(v => v, v => CompatibilityNotes.Texts("staticShaderProfiling", GpuVendors.FromVendorName(v), PixDiscovery.Version));
            return Paging.Page(page, filtered.Length, offset, limit, StaticTargets.Extra(all, PixDiscovery.InstallDir, PixDiscovery.Version, issues));
        }, cancellationToken);
    }

    [McpServerTool(Name = "pix_gpu_shader_static_profile", Title = "Static shader profile (AMD/Intel)", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Replays the capture on the local GPU if analysis is not started. That only happens for shaderRef or shaderKey; inline sources compile without a capture. Compiles a shader pipeline with an AMD or Intel offline compiler from the PIX install and returns a job whose result summarises each shader: instruction mix with fixed cycle estimates, register pressure, loops, hot spots weighted by loop depth, HLSL source lines where the vendor maps them, and every instruction under /detail. A preprocess or compile error is data (succeeded false, phase, compilerOutput, hints). Weights and cycles are static estimates, not measured GPU time.")]
    public static Task<string> StaticProfile(
        PixSession session,
        JobManager jobs,
        [Description("Target GPU from pix_shader_targets: an id, adapter name, family or architecture such as Xe2-HPG, Xe3-LPG, gfx1201 or RDNA3.5 when it selects one family.")] string target,
        [Description("Captured shader to profile, { eventRef, shaderIndex } from pix_gpu_pipeline_state or pix_gpu_shaders; the capture must hold its HLSL. Exactly one of shaderRef, shaderKey or sources.")] ShaderRef? shaderRef = null,
        [Description(ShaderIdentity.KeyDescription + " Exactly one of shaderRef, shaderKey or sources; shaderKey needs handle.")] string? shaderKey = null,
        [Description("GPU capture handle for shaderKey.")] string? handle = null,
        [Description("Inline shaders to compile without a capture, 1 to 5 with one per stage (a graphics pipeline needs its vertex shader). Shaders that bind resources declare [RootSignature(...)] in HLSL.")] IReadOnlyList<StaticShaderSource>? sources = null,
        [Description("Pipeline options for inline sources (default: type from the stages, triangle topology, one R8G8B8A8_UNORM render target, 1 sample).")] StaticPipelineOptions? pipeline = null,
        [Description("Summarise every compiled shader of the captured pipeline (default true); false keeps only the requested shader, whose pipeline is still compiled whole.")] bool wholePipeline = true,
        [Description("Hot spots and source lines per shader (default 25, max 200).")] int topN = 25,
        [Description("Attach the capture's replay provenance to the result (default false).")] bool includeProvenance = false,
        [Description(Tools.WaitSecondsDescription)] double waitSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        const string tool = "pix_gpu_shader_static_profile";
        int modes = (shaderRef is null ? 0 : 1) + (shaderKey is null ? 0 : 1) + (sources is null ? 0 : 1);
        if (modes != 1)
            throw PixErrors.InvalidArguments("Pass exactly one of shaderRef, shaderKey or sources.", [new ToolCallDto("pix_shader_targets", new { }, CostHints.Query)]);
        if (string.IsNullOrWhiteSpace(target))
            throw PixErrors.InvalidArguments("target is required; list targets with pix_shader_targets.", [new ToolCallDto("pix_shader_targets", new { }, CostHints.Query)]);
        if (topN is < 1 or > 200) throw PixErrors.InvalidArguments("topN must be between 1 and 200.");
        string targetKey = target.Trim().ToLowerInvariant();

        if (sources is not null)
        {
            StaticInlinePlan plan = StaticSourcePolicy.Plan(sources, pipeline, out string? problem) ?? throw PixErrors.InvalidArguments(problem!);
            var formats = new List<string>(plan.RenderTargetFormats);
            if (plan.DepthStencilFormat is not null) formats.Add(plan.DepthStencilFormat);
            foreach (string format in formats)
                if (!ShaderProfilingSession.TryFormat(format, out _)) throw PixErrors.InvalidArguments($"'{format}' is not a DXGI format name (for example R8G8B8A8_UNORM or D32_FLOAT).");
            string key = Hash(new { target = targetKey, sources, pipeline, topN });
            return Tools.RunJob(jobs, tool, () => session.ShaderProfiling.InlineJobs.StartOrJoin(key, () => jobs.Start("static-shader-profile",
                $"Static profile of {plan.Sources.Count} inline shader(s) ({plan.PipelineType}) for '{target.Trim()}'", job => RunInline(session, job, target, plan, topN))),
                waitSeconds, cancellationToken);
        }

        if (pipeline is not null) throw PixErrors.InvalidArguments("pipeline applies to inline sources only; a captured shader uses the capture's pipeline state.");
        string capture;
        string? canonicalKey = null;
        if (shaderKey is not null)
        {
            if (handle is null) throw PixErrors.InvalidArguments("shaderKey needs handle.", [new ToolCallDto("pix_handles", new { }, CostHints.Cached)]);
            if (!ShaderIdentity.TryParseShaderKey(shaderKey, out string keyStage, out string keyHash))
                throw PixErrors.InvalidArguments("shaderKey must look like hash:STAGE:HASH.", [new ToolCallDto("pix_gpu_shaders", new { handle }, CostHints.Replay)]);
            canonicalKey = ShaderIdentity.ShaderKey(keyStage, keyHash)!;
            capture = handle;
        }
        else
        {
            ReferenceValidation.Shader(session, shaderRef!);
            capture = shaderRef!.EventRef.Handle;
        }
        GpuCaptureHandle captureHandle = session.Get<GpuCaptureHandle>(capture);
        string identity = shaderRef is null ? canonicalKey! : $"{shaderRef.EventRef.QueueIndex}:{shaderRef.EventRef.EventIndex}:{shaderRef.ShaderIndex}";
        string cacheKey = $"{identity}|{targetKey}|{wholePipeline}|{topN}|{includeProvenance}";
        return Tools.RunJob(jobs, tool, () => CaptureJobs.GetValue(captureHandle, _ => new StaticProfileJobCache()).StartOrJoin(cacheKey, () => jobs.StartForHandle<GpuCaptureHandle>("static-shader-profile",
            $"Static profile of {identity} in {capture} for '{target.Trim()}'", capture,
            (job, h) => RunCapture(session, job, h, target, shaderRef, canonicalKey, wholePipeline, topN, includeProvenance))), waitSeconds, cancellationToken);
    }

    private static object RunInline(PixSession session, Job job, string targetSpec, StaticInlinePlan plan, int topN)
    {
        ShaderTargetDto target = ResolveTarget(session, targetSpec);
        ShaderProfilingSession profiling = session.ShaderProfiling;
        StaticDefinesFormatDto definesFormat = profiling.DefinesFormat;
        StaticCompileSource[] sources = plan.Sources
            .Select(s => new StaticCompileSource(ShaderProfilingSession.Stage(s.Stage), s.Target, s.Entry, s.Flags, s.Defines, s.Files)).ToArray();
        StaticCompileOutcome outcome = profiling.Compile(target.Id, () => ShaderProfilingSession.BuildPipeline(plan), sources, null, job);
        var context = new StaticResultContext("inline", target, topN, false, plan.DropRootSignature, definesFormat, [],
            [new ToolCallDto("pix_shader_targets", new { vendor = OtherVendor(target) }, CostHints.Query)])
        {
            EntryForStage = stage => plan.Sources.FirstOrDefault(s => string.Equals(s.Stage, stage, StringComparison.OrdinalIgnoreCase))?.Entry,
        };
        return StaticProfileDescriber.Build(outcome, context, VendorNotes(target));
    }

    private static object RunCapture(PixSession session, Job job, GpuCaptureHandle h, string targetSpec, ShaderRef? shaderRef, string? shaderKey,
        bool wholePipeline, int topN, bool includeProvenance)
    {
        ShaderTargetDto target = ResolveTarget(session, targetSpec);
        h.EnsureAnalysisStarted(job);
        if (shaderRef is null)
        {
            Preparation<GpuCaptureHandle> index = GpuCaptureHandle.ShaderIndexPreparation(h.Id);
            if (!index.IsReady(h)) index.Prepare(h, job);
            shaderRef = h.ShaderIndex!.FirstReference(shaderKey!)
                ?? throw PixErrors.InvalidReference($"No indexed shader in {h.Id} has key {shaderKey}.", new ToolCallDto("pix_gpu_shaders", new { handle = h.Id }, CostHints.Cached));
        }
        EventRef eventRef = shaderRef.EventRef;
        PIX_EVENT_INFO info = h.EventInfo(eventRef.QueueIndex, eventRef.EventIndex);
        IPixProgramState state = PixApiExtensionsGpuCapture.GetProgramState(h.Document, ref info);
        if (PixApiExtensionsGpuCaptureResources.GetGpuProgramType(state) != D3D12_PROGRAM_TYPE.D3D12_PROGRAM_TYPE_GENERIC_PIPELINE)
            throw new PixToolException(PixErrors.Codes.UnsupportedFeature, "Static shader profiling compiles graphics, compute and mesh pipelines; this event binds a raytracing pipeline or no program.", false,
                [new ToolCallDto("pix_gpu_pipeline_state", new { eventRef }, CostHints.Query)]);
        IPixGpuProgram program = PixApiExtensionsGpuCaptureResources.GetGpuProgram<IPixGpuProgram>(state);
        IPixCollection collection = PixApiExtensionsGpuCaptureResources.GetShaders(program);
        ulong count = collection.GetCount();
        if (shaderRef.ShaderIndex < 0 || (ulong)shaderRef.ShaderIndex >= count)
            throw PixErrors.InvalidReference($"shaderIndex {shaderRef.ShaderIndex} is out of range; the event has {count} shader(s).",
                new ToolCallDto("pix_gpu_pipeline_state", new { eventRef }, CostHints.Query));

        PIX_SHADER_CODE_TYPE hlsl = Tools.ParseEnum<PIX_SHADER_CODE_TYPE>("HLSL");
        var captured = new List<(ShaderInfoDto Identity, IReadOnlyList<StaticSourceFile> Files)>();
        for (ulong i = 0; i < count; i++)
        {
            job.ThrowIfCancellationRequested();
            IPixShader shader = collection.Get<IPixShader>(i);
            ShaderInfoDto identity = PipelineTools.ShaderDto((int)i, shader, eventRef);
            captured.Add((identity, ReadHlslFiles(shader, hlsl, identity.Stage)));
        }
        (ShaderInfoDto requested, IReadOnlyList<StaticSourceFile> requestedFiles) = captured[shaderRef.ShaderIndex];
        if (requestedFiles.Count == 0)
            throw PixErrors.UnavailableShaderData($"PIX has no HLSL for shader {shaderRef.ShaderIndex} ({requested.Stage}) at this event, and static profiling compiles HLSL. Profile the source inline with sources.",
                [new ToolCallDto("pix_gpu_shader_diagnostics", new { shaderRef }, CostHints.Query), new ToolCallDto("pix_gpu_shader_code", new { shaderRef, codeType = "ISA" }, CostHints.Query)]);

        var sources = new List<StaticCompileSource>();
        var withoutSource = new List<string>();
        var notes = new List<string>();
        foreach ((ShaderInfoDto identity, IReadOnlyList<StaticSourceFile> files) in captured)
        {
            if (files.Count == 0 || string.IsNullOrWhiteSpace(identity.Target) || !ShaderProfilingSession.TryStage(identity.Stage, out PIX_SHADER_STAGE stage))
            {
                withoutSource.Add(identity.Stage);
                continue;
            }
            string defines = StaticSourcePolicy.FormatCapturedDefines(identity.Defines, out IReadOnlyList<string> skipped);
            if (skipped.Count > 0) notes.Add($"{identity.Stage}: defines {string.Join(", ", skipped)} cannot travel as compiler arguments and were dropped.");
            sources.Add(new StaticCompileSource(stage, identity.Target!, identity.Entry ?? "main", identity.Flags ?? "", defines, files));
        }

        IPixGenericPipeline generic = PixApiExtensionsGpuCaptureResources.GetGpuProgram<IPixGenericPipeline>(state);
        IPixPipelineState pipelineState = PixApiExtensionsGpuCaptureResources.GetPipelineState(generic);
        var subobjects = PixApiExtensionsGpuCaptureResources.GetSubobjects(pipelineState);
        bool hasApplication = h.Document.HasApplicationDescription();
        PixApplicationDesc? application = hasApplication ? PixApiExtensionsGpuCapture.GetApplicationDescription(h.Document) : null;
        ShaderProfilingSession profiling = session.ShaderProfiling;
        StaticDefinesFormatDto definesFormat = profiling.DefinesFormat;
        // The captured subobjects borrow native storage owned by the pipeline state; keep it alive through the compile.
        StaticCompileOutcome outcome = PipelineTools.ReadWithNativeOwner(pipelineState,
            () => profiling.Compile(target.Id, () => new PixShaderProfilingPipelineState(subobjects), sources, application, job));

        var references = captured.GroupBy(c => c.Identity.Stage, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Identity, StringComparer.OrdinalIgnoreCase);
        ToolCallDto[] next =
        [
            new("pix_gpu_shader_code", new { shaderRef, codeType = "ISA" }, CostHints.Query),
            new("pix_shader_targets", new { vendor = OtherVendor(target) }, CostHints.Query),
            new("pix_gpu_inspect_event", new { eventRef }, CostHints.Replay),
        ];
        var context = new StaticResultContext("capture", target, topN, true, false, definesFormat, withoutSource, next)
        {
            ShaderRefForStage = stage => references.TryGetValue(stage, out ShaderInfoDto? found) ? found.ShaderRef : null,
            EntryForStage = stage => references.TryGetValue(stage, out ShaderInfoDto? found) ? found.Entry : null,
            IncludeStage = wholePipeline ? null : stage => string.Equals(stage, requested.Stage, StringComparison.OrdinalIgnoreCase),
            ExtraNotes = notes,
        };
        StaticProfileDto dto = StaticProfileDescriber.Build(outcome, context, VendorNotes(target));
        return includeProvenance ? dto with { Provenance = h.Provenance() } : dto;
    }

    private static IReadOnlyList<StaticSourceFile> ReadHlslFiles(IPixShader shader, PIX_SHADER_CODE_TYPE hlsl, string stage)
    {
        IPixCollection? nodes = PixApiExtensionsShaders.TryGetNodes(shader, hlsl, out _);
        if (nodes is null) return [];
        var files = new List<StaticSourceFile>();
        ulong count = nodes.GetCount();
        for (ulong n = 0; n < count; n++)
        {
            IPixShaderNode? node = PixApiExtensionsShaders.TryGetNode(nodes, n, out _);
            if (node is null) continue;
            try { files.Add(new StaticSourceFile(Interop.WOrNull(node.GetName()) ?? $"{stage.ToLowerInvariant()}{n}.hlsl", PipelineTools.ReadCode(nodes, n))); }
            catch (PixToolException) { }
        }
        return files;
    }

    private static ShaderTargetDto ResolveTarget(PixSession session, string spec)
    {
        StaticTargetMatch match = StaticTargets.Resolve(session.ShaderProfiling.Targets, spec);
        if (match.Target is not null) return match.Target;
        string candidates = string.Join("; ", match.Candidates.Take(12).Select(c => $"{c.Id}: {c.Name} ({c.Family}{(c.Architecture is null ? "" : ", " + c.Architecture)})"));
        throw PixErrors.InvalidArguments($"{match.Problem} Candidates: {candidates}.", [new ToolCallDto("pix_shader_targets", new { }, CostHints.Query)]);
    }

    private static IReadOnlyList<string> VendorNotes(ShaderTargetDto target)
        => CompatibilityNotes.Texts("staticShaderProfiling", GpuVendors.FromVendorName(target.Vendor), PixDiscovery.Version);

    private static string OtherVendor(ShaderTargetDto target) => target.Vendor == "intel" ? "amd" : "intel";

    private static string Hash(object value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(value, Json.Options))));
}
