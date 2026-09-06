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
    [McpServerTool(Name = "pix_gpu_shader_profile"), Description("Experimental: profiles the shaders executed by a range of events on one queue with PIX's shader profiler and reports, per shader stage, the hottest instructions (byte offset, sample count, stall reasons) plus the stall-type legend. Replays the range repeatedly, so it runs as a job; starts GPU analysis if needed. Needs driver support: on unsupported GPUs the job fails with PIX's error. Map offsets to code with pix_gpu_shader_code(codeType: ISA).")]
    public static Task<string> Profile(
        PixSession session,
        JobManager jobs,
        [Description("GPU capture handle")] string handle,
        [Description("Queue index")] int queueIndex,
        [Description("First event index of the range (a draw/dispatch or a marker).")] uint firstEventIndex,
        [Description("Last event index of the range (default: same as firstEventIndex).")] uint? lastEventIndex = null,
        [Description("Maximum instructions reported per shader, hottest first (default 50, max 1000).")] int maxInstructions = 50,
        [Description(Tools.WaitSecondsDescription)] double waitSeconds = 0,
        CancellationToken cancellationToken = default)
        => Tools.RunJob(jobs, "pix_gpu_shader_profile", () => jobs.StartForHandle<GpuCaptureHandle>("shader-profile",
            $"Profile shaders of {handle} queue {queueIndex} events {firstEventIndex}..{lastEventIndex ?? firstEventIndex}", handle, (j, h) =>
        {
            int max = Math.Clamp(maxInstructions, 1, Paging.MaxLimit);
            h.EnsureAnalysisStarted(j);
            PIX_EVENT_INFO first = h.EventInfo(queueIndex, firstEventIndex);
            PIX_EVENT_INFO last = h.EventInfo(queueIndex, lastEventIndex ?? firstEventIndex);
            IPixGpuCaptureAnalysisExperimental experimental = h.GetAnalysis() as IPixGpuCaptureAnalysisExperimental
                ?? ExperimentalCapture.GetAnalysisExperimental(h.Document);
            j.AddMessage("Profiling shaders (replaying the event range)...");
            j.ThrowIfCancellationRequested();
            IPixShaderProfilingLiveResult result = ExperimentalAnalysis.ProfileShaderPipeline(experimental, first, last);
            return Describe(result, max);
        }), waitSeconds, cancellationToken);

    private static unsafe object Describe(IPixShaderProfilingLiveResult result, int maxInstructions)
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
            var hottest = instructions.OrderByDescending(i => i.Samples).ThenBy(i => i.Offset).Take(maxInstructions)
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
                instructionCount,
                totalSamples,
                instructions = hottest,
                instructionsTruncated = instructionCount > maxInstructions,
            });
        }

        return new
        {
            shaderCount,
            stallTypes = stallTypes.Select(kv => new { id = kv.Key, name = kv.Value.Name, description = kv.Value.Description }).ToArray(),
            shaders,
        };
    }
}
