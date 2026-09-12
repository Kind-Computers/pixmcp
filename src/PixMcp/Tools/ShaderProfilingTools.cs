using System.ComponentModel;
using Microsoft.PIX;
using Microsoft.PIX.Internal;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using PixMcp.Pix.Handles;
using ExperimentalAnalysis = Microsoft.PIX.Internal.Extension.GpuCapture.Analysis.PixApiExtensionsGpuCaptureAnalysis;
using ExperimentalCapture = Microsoft.PIX.Internal.Extension.GpuCapture.PixApiExtensionsGpuCapture;

namespace PixMcp.Tools;

/// <summary>PIX shader profiling over a GPU event range (experimental PIX API surface).</summary>
[McpServerToolType]
public static class ShaderProfilingTools
{
    [McpServerTool(Name = "pix_gpu_shader_profile"), Description("Experimental shader profiling for an event range on one queue. Returns a job whose stored result includes every instruction, hottest first, plus sample/stall metadata and shader references matched by hash and stage. Read result pages with pix_result_read. Byte offsets refer to ISA, not HLSL lines; no source mapping is inferred. Requires driver support.")]
    public static Task<string> Profile(
        PixSession session,
        JobManager jobs,
        [Description("First event of the range (a draw/dispatch or marker).")] EventRef firstEventRef,
        [Description("Last event; defaults to firstEventRef. Must use the same capture and queue.")] EventRef? lastEventRef = null,
        [Description(Tools.WaitSecondsDescription)] double waitSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        ReferenceValidation.Event(session, firstEventRef);
        lastEventRef ??= firstEventRef;
        ReferenceValidation.Event(session, lastEventRef);
        if (lastEventRef.Handle != firstEventRef.Handle || lastEventRef.QueueIndex != firstEventRef.QueueIndex ||
            lastEventRef.EventIndex < firstEventRef.EventIndex) throw new McpException("The event range must be ordered within the same capture and queue.");
        return Tools.RunJob(jobs, "pix_gpu_shader_profile", () => jobs.StartForHandle<GpuCaptureHandle>("shader-profile",
            $"Profile shaders of {firstEventRef.Handle} queue {firstEventRef.QueueIndex} events {firstEventRef.EventIndex}..{lastEventRef.EventIndex}", firstEventRef.Handle, (j, h) =>
        {
            if (h.OptionalUnavailable.TryGetValue("shaderProfiling", out object? cached)) return cached;
            h.EnsureAnalysisStarted(j);
            PIX_EVENT_INFO first = h.EventInfo(firstEventRef.QueueIndex, firstEventRef.EventIndex);
            PIX_EVENT_INFO last = h.EventInfo(lastEventRef.QueueIndex, lastEventRef.EventIndex);
            j.AddMessage("Profiling shaders (replaying the event range)...");
            j.ThrowIfCancellationRequested();
            try
            {
                IPixGpuCaptureAnalysisExperimental experimental = h.GetAnalysis() as IPixGpuCaptureAnalysisExperimental
                    ?? ExperimentalCapture.GetAnalysisExperimental(h.Document);
                IPixShaderProfilingLiveResult result = ExperimentalAnalysis.ProfileShaderPipeline(experimental, first, last);
                var identities = new List<ShaderInfoDto>();
                var coverage = new List<object>();
                EventRecord[] records = h.AllEvents(firstEventRef.QueueIndex);
                foreach (EventRecord record in records)
                {
                    bool inRange = record.Index >= firstEventRef.EventIndex && record.Index <= lastEventRef.EventIndex;
                    if ((!inRange && !EventNavigation.IsWithin(records, record.Index, firstEventRef.EventIndex) &&
                        !EventNavigation.IsWithin(records, record.Index, lastEventRef.EventIndex)) ||
                        !Tools.MatchesKind(record, "drawOrDispatch")) continue;
                    j.ThrowIfCancellationRequested();
                    var eventRef = new EventRef(h.Id, firstEventRef.QueueIndex, record.Index);
                    try { identities.AddRange(PipelineTools.ReadShaders(h, eventRef)); }
                    catch (Exception ex) { coverage.Add(new { eventRef, unavailable = true, reason = PixErrors.Describe(ex) }); }
                }
                h.MarkCapability("shaderProfiling", "supported");
                return Describe(result, identities, coverage, h.Provenance(), firstEventRef, lastEventRef);
            }
            catch (Exception ex) when (PixErrors.ToDto(ex).Code == "unsupported_feature")
            {
                object unavailable = PixErrors.Unavailable("shaderProfiling", ex);
                h.OptionalUnavailable["shaderProfiling"] = unavailable;
                h.MarkCapability("shaderProfiling", "unsupported", PixErrors.Describe(ex));
                return unavailable;
            }
        }), waitSeconds, cancellationToken);
    }

    private static unsafe object Describe(IPixShaderProfilingLiveResult result, IReadOnlyList<ShaderInfoDto> identities,
        IReadOnlyList<object> coverage, ReplayProvenance provenance, EventRef firstEventRef, EventRef lastEventRef)
    {
        var stallTypes = new Dictionary<uint, (string Name, string? Description)>();
        uint stallTypeCount = result.GetStallTypeCount();
        for (uint i = 0; i < stallTypeCount; i++)
        {
            PIX_SHADER_PROFILING_STALL_TYPE type = default;
            Internal_IPixShaderProfilingLiveResult_Extensions.GetStallType(result, i, ref type);
            stallTypes[type.Id] = (Interop.W(type.Name), Interop.WOrNull(type.Description));
        }

        var shaders = new List<object>();
        uint shaderCount = result.GetShaderCount();
        Guid shaderGuid = typeof(IPixShaderProfilingLiveShader).GUID;
        Guid instructionGuid = typeof(IPixShaderProfilingLiveInstruction).GUID;
        for (uint s = 0; s < shaderCount; s++)
        {
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
            catch { }

            uint instructionCount = shader.GetInstructionCount();
            var instructions = new List<(uint Id, uint Offset, uint Samples, object[] Stalls)>();
            ulong totalSamples = 0;
            for (uint k = 0; k < instructionCount; k++)
            {
                Internal_IPixShaderProfilingLiveShader_Extensions.GetInstruction(shader, k, in instructionGuid, out object instructionObject);
                var instruction = (IPixShaderProfilingLiveInstruction)instructionObject;
                uint samples = instruction.GetSampleCount();
                totalSamples += samples;
                uint stallCount = instruction.GetStallCount();
                var stalls = new List<object>();
                for (uint t = 0; t < stallCount; t++)
                {
                    PIX_SHADER_PROFILING_STALL stall = default;
                    Internal_IPixShaderProfilingLiveInstruction_Extensions.GetStall(instruction, t, ref stall);
                    stalls.Add(new { type = stallTypes.TryGetValue(stall.Type, out var known) ? known.Name : stall.Type.ToString(), samples = stall.SampleCount });
                }
                instructions.Add((instruction.GetId(), instruction.GetOffsetBytes(), samples, stalls.ToArray()));
            }
            var hottest = instructions.OrderByDescending(i => i.Samples).ThenBy(i => i.Offset)
                .Select(i => new
                {
                    id = i.Id,
                    offsetBytes = i.Offset,
                    samples = i.Samples,
                    percentOfShader = totalSamples == 0 ? 0 : Math.Round(100.0 * i.Samples / totalSamples, 2),
                    stalls = i.Stalls,
                }).ToArray();
            shaders.Add(new
            {
                index = s,
                id = shader.GetId(),
                stage = shader.GetStage(),
                hash,
                shaderRefs = MatchReferences(identities, hash, Json.EnumName(shader.GetStage())),
                instructionCount,
                totalSamples,
                instructions = hottest,
            });
        }

        return new
        {
            firstEventRef,
            lastEventRef,
            provenance,
            coverage,
            shaderCount,
            stallTypes = stallTypes.Select(kv => new { id = kv.Key, name = kv.Value.Name, description = kv.Value.Description }).ToArray(),
            shaders,
        };
    }

    internal static IReadOnlyList<ShaderRef> MatchReferences(IEnumerable<ShaderInfoDto> identities, string? hash, string stage)
        => string.IsNullOrEmpty(hash) ? [] : identities
            .Where(s => s.ShaderRef is not null && string.Equals(s.Hash, hash, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.Stage, stage, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.ShaderRef!).Distinct().ToArray();
}
