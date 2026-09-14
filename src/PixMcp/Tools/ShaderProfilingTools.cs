using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.PIX;
using Microsoft.PIX.Internal;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using PixMcp.Pix.Handles;
using ExperimentalAnalysis = Microsoft.PIX.Internal.Extension.GpuCapture.Analysis.PixApiExtensionsGpuCaptureAnalysis;
using ExperimentalCapture = Microsoft.PIX.Internal.Extension.GpuCapture.PixApiExtensionsGpuCapture;

namespace PixMcp.Tools;

/// <summary>Live shader profiling over a GPU event range (experimental PIX API surface, needs driver support).</summary>
[McpServerToolType]
public static class ShaderProfilingTools
{
    private static readonly ConditionalWeakTable<GpuCaptureHandle, StaticProfileJobCache> ProfileJobs = new();

    [McpServerTool(Name = "pix_gpu_shader_profile", Title = "Profile shader instruction stalls (live replay)", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Replays the capture on the local GPU if analysis is not started. Live shader profiling over the work events of a scope on one queue; needs driver support. Returns a job whose result totals samples and stall samples per shader and across shaders, classifies each shader's dominant stall with a heuristic keyword rule, lists the topN hottest instructions with ISA byte offsets and matched shaderRefs, and keeps every instruction under /detail. A driver that cannot profile yields a cached unsupported marker naming the vendor, with nextCalls to static profiling. Samples are counts, not time.")]
    public static Task<string> Profile(
        PixSession session,
        JobManager jobs,
        [Description("GPU capture handle")] string handle,
        [Description(EventScope.Description)] EventRef? scope = null,
        [Description(EventScope.PrefixDescription)] string? markerPathPrefix = null,
        [Description("Queue to profile; required when neither scope nor a single-queue prefix selects one. Alone it profiles the whole queue.")] int? queueIndex = null,
        [Description("Hottest instructions per shader in the summary (default 25, max 1000); /detail keeps every instruction.")] int topN = 25,
        [Description(ShaderIdentity.KeyDescription + " Keeps only shaders with that stage and hash (default all shaders).")] string? shaderKey = null,
        [Description("Keeps only the shader bound at this reference, matched by stage and hash (default all shaders).")] ShaderRef? shaderRef = null,
        [Description("Keeps only shaders of this stage, e.g. PIXEL or COMPUTE (default all stages).")] string? stage = null,
        [Description("Keeps only shaders with this hash (default all hashes).")] string? hash = null,
        [Description("Attach the replay provenance to the result (default false).")] bool includeProvenance = false,
        [Description(Tools.WaitSecondsDescription)] double waitSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        if (scope is null && markerPathPrefix is null && !queueIndex.HasValue)
            throw new PixToolException(PixErrors.Codes.InvalidArguments, "Pass scope, markerPathPrefix or queueIndex to select the events to profile.",
                nextCalls: [new("pix_gpu_overview", new { handle })]);
        if (topN is < 1 or > 1000) throw PixErrors.InvalidArguments("topN must be between 1 and 1000.");
        string? filterStage = stage?.Trim();
        string? filterHash = hash?.Trim();
        if (shaderKey is not null)
        {
            if (!ShaderIdentity.TryParseShaderKey(shaderKey, out string keyStage, out string keyHash))
                throw PixErrors.InvalidArguments("shaderKey must look like hash:STAGE:HASH.", [new ToolCallDto("pix_gpu_shaders", new { handle }, CostHints.Replay)]);
            filterStage ??= keyStage;
            filterHash ??= keyHash;
        }
        if (shaderRef is not null) ReferenceValidation.Shader(session, shaderRef);
        ScopeSelection selection = EventScope.Resolve(session, handle, queueIndex, scope, markerPathPrefix);
        GpuCaptureHandle captureHandle = session.Get<GpuCaptureHandle>(handle);
        if (queueIndex.HasValue) captureHandle.Queue(queueIndex.Value);
        ShaderProfileFilterDto? filter = shaderKey is null && shaderRef is null && stage is null && hash is null ? null : new(shaderKey, shaderRef, stage, hash);
        string key = string.Join('|', captureHandle.AnalysisGeneration, scope is null ? "" : $"{scope.QueueIndex}:{scope.EventIndex}", markerPathPrefix, queueIndex,
            topN, filterStage, filterHash, shaderRef is null ? "" : $"{shaderRef.EventRef.QueueIndex}:{shaderRef.EventRef.EventIndex}:{shaderRef.ShaderIndex}", includeProvenance);
        string description = $"Profile shaders of {handle}" + (scope is null ? "" : $" under queue {scope.QueueIndex} event {scope.EventIndex}")
            + (markerPathPrefix is null ? "" : $" matching '{markerPathPrefix}'");
        return Tools.RunJob(jobs, "pix_gpu_shader_profile", () => ProfileJobs.GetValue(captureHandle, _ => new StaticProfileJobCache()).StartOrJoin(key,
            () => jobs.StartForHandle<GpuCaptureHandle>("shader-profile", description, handle,
                (j, h) => RunProfile(j, h, selection, scope, markerPathPrefix, topN, filter, filterStage, filterHash, shaderRef, includeProvenance))),
            waitSeconds, cancellationToken);
    }

    private static object RunProfile(Job j, GpuCaptureHandle h, ScopeSelection selection, EventRef? scope, string? markerPathPrefix, int topN,
        ShaderProfileFilterDto? filter, string? filterStage, string? filterHash, ShaderRef? shaderRef, bool includeProvenance)
    {
        if (h.OptionalUnavailable.TryGetValue("shaderProfiling", out object? cached)) return cached;
        h.EnsureAnalysisStarted(j);
        var provenance = h.Provenance();
        string vendor = provenance.Vendor ?? "unknown";
        EventRange range = EventScope.ToEventRange(h, selection);
        int queue = range.Range.QueueIndex;
        j.AddMessage($"Profiling shaders (replaying {range.Range.WorkEvents} GPU event(s) on queue {queue})...");
        j.ThrowIfCancellationRequested();
        LiveProfileData data;
        try
        {
            IPixGpuCaptureAnalysisExperimental experimental;
            try { experimental = h.GetAnalysis() as IPixGpuCaptureAnalysisExperimental ?? ExperimentalCapture.GetAnalysisExperimental(h.Document); }
            catch (InvalidCastException ex)
            {
                throw new PixToolException(PixErrors.Codes.PixApiMismatch, "The PIX analysis does not expose the experimental shader profiling interface: " + PixErrors.Describe(ex), false,
                    [new ToolCallDto("pix_info", new { probe = true }, CostHints.Query)]);
            }
            data = Snapshot(ExperimentalAnalysis.ProfileShaderPipeline(experimental, range.First, range.Last), j);
        }
        catch (Exception ex) when (PixErrors.IsUnsupported(ex))
        {
            var unavailable = Unavailable("unsupported", ex);
            h.OptionalUnavailable["shaderProfiling"] = unavailable;
            h.MarkCapability("shaderProfiling", "unsupported", PixErrors.Describe(ex));
            return unavailable;
        }
        catch (Exception ex) when (PixErrors.IsDeclined(ex))
        {
            // A declined call does not prove the driver lacks support, so the handle keeps no marker; identical calls still share this job.
            return Unavailable("declined", ex);
        }
        catch (Exception ex) when (vendor == "intel" && PixErrors.HResultOf(ex) == unchecked((int)0x80004005))
        {
            // Observed on an Arc B580 for every range of NVIDIA-captured fixtures; reported (uncached) with the static profiling route.
            return Unavailable("failed", ex);
        }

        ShaderProfileUnavailableDto Unavailable(string state, Exception ex) => new(true, "shaderProfiling", state, PixErrors.Describe(ex), PixErrors.ToDto(ex), vendor,
            CompatibilityNotes.Texts("liveShaderProfiling", GpuVendors.FromVendorName(vendor), PixDiscovery.Version),
            [new ToolCallDto("pix_shader_targets", new { }, CostHints.Query), new ToolCallDto("pix_gpu_shaders", new { handle = h.Id }, CostHints.Replay)]);

        var identities = new List<ShaderInfoDto>();
        var coverage = new List<object>();
        EventRecord[] records = h.AllEvents(queue);
        foreach (EventRecord record in records)
        {
            if (!selection.Contains(queue, records, record.Index) || !Tools.MatchesKind(record, "work")) continue;
            j.ThrowIfCancellationRequested();
            var eventRef = new EventRef(h.Id, queue, record.Index);
            try { identities.AddRange(h.ShaderIndex?.ShadersAt(eventRef) ?? PipelineTools.ReadShaders(h, eventRef)); }
            catch (Exception ex) { coverage.Add(new { eventRef, unavailable = true, reason = PixErrors.Describe(ex) }); }
        }
        if (shaderRef is not null)
        {
            ShaderInfoDto bound = identities.FirstOrDefault(i => i.ShaderRef == shaderRef)
                ?? PipelineTools.ReadShaders(h, shaderRef.EventRef).FirstOrDefault(s => s.Index == shaderRef.ShaderIndex)
                ?? throw PixErrors.InvalidReference($"shaderIndex {shaderRef.ShaderIndex} is not bound at that event.", new ToolCallDto("pix_gpu_pipeline_state", new { eventRef = shaderRef.EventRef }, CostHints.Query));
            filterStage = bound.Stage;
            filterHash = bound.Hash ?? "";
        }
        h.MarkCapability("shaderProfiling", "supported");

        var (totals, shaders, keys, detail) = LiveProfileDescriber.Describe(data, new LiveProfileOptions(vendor, topN, filterStage, filterHash),
            (shaderHash, shaderStage) => MatchReferences(identities, shaderHash, shaderStage));
        var next = new List<ToolCallDto> { new("pix_shader_targets", new { }, CostHints.Query) };
        if (shaders.Any(s => s.Classification?.Bound == "memoryLatency"))
        {
            next.Add(new("pix_gpu_occupancy", new { handle = h.Id, scope, markerPathPrefix }, CostHints.Replay));
            next.Add(new("pix_gpu_counters_read", new { handle = h.Id, preset = "memoryBandwidth", scope, markerPathPrefix }, CostHints.Replay));
        }
        return new ShaderProfileDto(range.ToDto(h.Id), selection.DescribeOrNull(h), vendor, filter, totals, shaders, keys, data.StallTypes, coverage,
            CompatibilityNotes.Texts("liveShaderProfiling", GpuVendors.FromVendorName(vendor), PixDiscovery.Version), LiveProfileDescriber.Semantics, next)
        {
            Detail = detail,
            Provenance = includeProvenance ? provenance : null,
        };
    }

    /// <summary>Copies the live result into managed records (worker only).</summary>
    private static LiveProfileData Snapshot(IPixShaderProfilingLiveResult result, Job job)
    {
        var stallTypes = new List<LiveStallTypeInfo>();
        uint stallTypeCount = result.GetStallTypeCount();
        for (uint i = 0; i < stallTypeCount; i++)
        {
            PIX_SHADER_PROFILING_STALL_TYPE type = default;
            Internal_IPixShaderProfilingLiveResult_Extensions.GetStallType(result, i, ref type);
            stallTypes.Add(new(type.Id, Interop.W(type.Name), Interop.WOrNull(type.Description)));
        }

        var shaders = new List<LiveShaderData>();
        uint shaderCount = result.GetShaderCount();
        Guid shaderGuid = typeof(IPixShaderProfilingLiveShader).GUID;
        Guid instructionGuid = typeof(IPixShaderProfilingLiveInstruction).GUID;
        for (uint s = 0; s < shaderCount; s++)
        {
            job.ThrowIfCancellationRequested();
            Internal_IPixShaderProfilingLiveResult_Extensions.GetShader(result, s, in shaderGuid, out object shaderObject);
            var shader = (IPixShaderProfilingLiveShader)shaderObject;
            string? hash = null;
            try
            {
                var bytes = new byte[shader.GetHashSizeBytes()];
                if (bytes.Length > 0)
                {
                    Internal_IPixShaderProfilingLiveShader_Extensions.GetHash(shader, bytes);
                    hash = Convert.ToHexString(bytes).ToLowerInvariant();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }

            uint instructionCount = shader.GetInstructionCount();
            var instructions = new List<LiveInstructionData>((int)instructionCount);
            for (uint k = 0; k < instructionCount; k++)
            {
                Internal_IPixShaderProfilingLiveShader_Extensions.GetInstruction(shader, k, in instructionGuid, out object instructionObject);
                var instruction = (IPixShaderProfilingLiveInstruction)instructionObject;
                uint stallCount = instruction.GetStallCount();
                var stalls = new List<LiveStallSample>((int)stallCount);
                for (uint t = 0; t < stallCount; t++)
                {
                    PIX_SHADER_PROFILING_STALL stall = default;
                    Internal_IPixShaderProfilingLiveInstruction_Extensions.GetStall(instruction, t, ref stall);
                    stalls.Add(new(stall.Type, stall.SampleCount));
                }
                instructions.Add(new(instruction.GetId(), instruction.GetOffsetBytes(), instruction.GetSampleCount(), stalls));
            }
            shaders.Add(new(shader.GetId(), Json.EnumName(shader.GetStage()), hash, instructions));
        }
        return new(stallTypes, shaders);
    }

    internal static IReadOnlyList<ShaderRef> MatchReferences(IEnumerable<ShaderInfoDto> identities, string? hash, string stage)
        => string.IsNullOrEmpty(hash) ? [] : identities
            .Where(s => s.ShaderRef is not null && string.Equals(s.Hash, hash, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.Stage, stage, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.ShaderRef!).Distinct().ToArray();
}
