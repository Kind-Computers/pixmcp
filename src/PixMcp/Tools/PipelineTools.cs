using System.ComponentModel;
using System.Text.Json;
using Microsoft.PIX;
using Microsoft.PIX.Extension;
using Microsoft.PIX.Extension.GpuCapture;
using Microsoft.PIX.Extension.GpuCapture.Resources;
using Microsoft.PIX.Internal;
using Microsoft.PIX.Internal.Extension.PipelineState;
using Microsoft.PIX.Internal.Extension.PipelineState.Serialization;
using Microsoft.PIX.Internal.Extension.Shaders;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using PixMcp.Pix.Handles;
using Windows.Win32.Foundation;

namespace PixMcp.Tools;

[McpServerToolType]
public static class PipelineTools
{
    private const int MaxSubobjectJsonChars = 16000;

    private static readonly Lazy<JsonSerializerOptions> SubobjectJsonOptions = new(() =>
    {
        var options = new JsonSerializerOptions(PipelineStateSubobjectJsonSerializerOptions.DefaultOptions);
        options.Converters.Insert(0, Json.EnumConverterFactory);
        return options;
    });

    [McpServerTool(Name = "pix_gpu_pipeline_state", ReadOnly = true), Description("Program/pipeline state bound at a Draw, Dispatch, DispatchMesh or DispatchRays event: program type, pipeline type, PSO subobjects (blend, rasterizer, depth-stencil, input layout, ...), global root signature and bound shaders. The event must be a draw/dispatch (other events fail with E_NOT_VALID_STATE). Needs GPU analysis: started automatically as a job (see waitSeconds). For bound resources use pix_gpu_event_resources; for shader code use pix_gpu_shader_code.")]
    public static Task<string> PipelineState(
        PixSession session,
        JobManager jobs,
        [Description("GPU capture handle")] string handle,
        [Description("Queue index")] int queueIndex,
        [Description("Event index of a draw/dispatch event")] uint eventIndex,
        [Description("Include decoded PSO subobject details (default true).")] bool includeSubobjects = true,
        [Description("Include the global root signature parameters (default true).")] bool includeRootSignature = true,
        [Description("Include the bound shaders (default true).")] bool includeShaders = true,
        [Description(Tools.ReadyWaitDescription)] double waitSeconds = Tools.DefaultReadyWaitSeconds,
        CancellationToken cancellationToken = default)
        => Tools.RunWhenReady(session, jobs, "pix_gpu_pipeline_state", handle, GpuCaptureHandle.AnalysisPreparation(handle), h =>
        {
            EventRecord record = h.Event(queueIndex, eventIndex);
            PIX_EVENT_INFO info = h.EventInfo(queueIndex, eventIndex);

            IPixProgramState programState = PixApiExtensionsGpuCapture.GetProgramState(h.Document, ref info);
            D3D12_PROGRAM_TYPE? programType = PixApiExtensionsGpuCaptureResources.GetGpuProgramType(programState);
            IPixGpuProgram program = PixApiExtensionsGpuCaptureResources.GetGpuProgram<IPixGpuProgram>(programState);

            object? rootSignature = null;
            if (includeRootSignature)
            {
                try
                {
                    IPixRootSignature rs = PixApiExtensionsGpuCaptureResources.GetGlobalRootSignature(program);
                    rootSignature = rs is null ? null : RootSignatureDto(rs);
                }
                catch (Exception ex) { rootSignature = PixErrors.Unavailable("globalRootSignature", ex); }
            }

            object? shaders = null;
            if (includeShaders)
            {
                try { shaders = Shaders(program); }
                catch (Exception ex) { shaders = PixErrors.Unavailable("shaders", ex); }
            }

            object? generic = null;
            object? raytracing = null;
            if (programType == D3D12_PROGRAM_TYPE.D3D12_PROGRAM_TYPE_GENERIC_PIPELINE)
            {
                try
                {
                    IPixGenericPipeline gp = PixApiExtensionsGpuCaptureResources.GetGpuProgram<IPixGenericPipeline>(programState);
                    PIX_GENERIC_PIPELINE_TYPE pipelineType = PixApiExtensionsGpuCaptureResources.GetPipelineType(gp);
                    object? subobjects = null;
                    if (includeSubobjects)
                    {
                        IPixPipelineState ps = PixApiExtensionsGpuCaptureResources.GetPipelineState(gp);
                        List<D3D12_STATE_SUBOBJECT> list = PixApiExtensionsGpuCaptureResources.GetSubobjects(ps);
                        subobjects = list.Select((so, i) => new { index = i, type = so.Type, detail = SubobjectDetail(so) }).ToArray();
                    }
                    generic = new { pipelineType, subobjects };
                }
                catch (Exception ex) { generic = PixErrors.Unavailable("genericPipeline", ex); }
            }
            else if (programType == D3D12_PROGRAM_TYPE.D3D12_PROGRAM_TYPE_RAYTRACING_PIPELINE)
            {
                try
                {
                    IPixRaytracingPipeline rt = PixApiExtensionsGpuCaptureResources.GetGpuProgram<IPixRaytracingPipeline>(programState);
                    var tables = new List<object>();
                    foreach (PIX_RAYTRACING_PIPELINE_STAGE stage in new[] { PIX_RAYTRACING_PIPELINE_STAGE.PIX_RAYTRACING_PIPELINE_STAGE_RAYGEN, PIX_RAYTRACING_PIPELINE_STAGE.PIX_RAYTRACING_PIPELINE_STAGE_MISS, PIX_RAYTRACING_PIPELINE_STAGE.PIX_RAYTRACING_PIPELINE_STAGE_HITGROUP, PIX_RAYTRACING_PIPELINE_STAGE.PIX_RAYTRACING_PIPELINE_STAGE_CALLABLE })
                    {
                        try
                        {
                            IPixShaderTable table = PixApiExtensionsGpuCaptureResources.GetShaderTable(rt, stage);
                            uint recordCount = table.GetRecordCount();
                            var records = new List<object>();
                            for (uint i = 0; i < Math.Min(recordCount, 64u); i++)
                            {
                                IPixShaderRecord rec = PixApiExtensionsGpuCaptureResources.GetShaderRecord(table, i);
                                string? localRs = null;
                                try { localRs = Interop.WOrNull(PixApiExtensionsGpuCaptureResources.GetLocalRootSignature(rec)?.GetName() ?? default); } catch { }
                                records.Add(new { index = i, exportName = Interop.W(rec.GetExportName()), localRootSignature = localRs });
                            }
                            string? resourceName = null;
                            try { resourceName = Interop.WOrNull(PixApiExtensionsGpuCaptureResources.GetResource(table)?.GetName() ?? default); } catch { }
                            tables.Add(new { stage, recordCount, records, resource = resourceName });
                        }
                        catch (Exception ex) { tables.Add(new { stage, unavailable = true, reason = PixErrors.Describe(ex) }); }
                    }
                    raytracing = new { shaderTables = tables };
                }
                catch (Exception ex) { raytracing = PixErrors.Unavailable("raytracingPipeline", ex); }
            }

            return new
            {
                @event = record.ToDto(queueIndex),
                programType,
                rootSignature,
                shaders,
                genericPipeline = generic,
                raytracingPipeline = raytracing,
            };
        }, waitSeconds, cancellationToken);

    private static object? SubobjectDetail(D3D12_STATE_SUBOBJECT subobject)
    {
        try
        {
            object? wrapper = PixApiExtensionsPipelineState.ToWrapper(subobject);
            if (wrapper is null)
            {
                return null;
            }
            string json = JsonSerializer.Serialize(wrapper, wrapper.GetType(), SubobjectJsonOptions.Value);
            if (json.Length > MaxSubobjectJsonChars)
            {
                return new { truncated = true, jsonLength = json.Length, preview = json[..MaxSubobjectJsonChars] };
            }
            return JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch (Exception ex)
        {
            return new { unavailable = true, reason = ex.Message };
        }
    }

    internal static object RootSignatureDto(IPixRootSignature rs)
    {
        D3D12_VERSIONED_ROOT_SIGNATURE_DESC desc = PixApiExtensionsGpuCaptureResources.GetVersionedDesc(rs);
        object? parameters = null;
        object? staticSamplers = null;
        try
        {
            switch (desc.Version)
            {
                case D3D_ROOT_SIGNATURE_VERSION.D3D_ROOT_SIGNATURE_VERSION_1_0:
                    parameters = PixApiExtensionsGpuCaptureResources.GetRootParameters(desc).Select((p, i) => new
                    {
                        index = i,
                        type = p.Type,
                        visibility = p.ShaderVisibility,
                        descriptorTable = p.DescriptorTable.HasValue ? new { ranges = p.DescriptorTable.Value.DescriptorRanges.Select(r => (object)new { rangeType = r.RangeType, numDescriptors = r.NumDescriptors, baseShaderRegister = r.BaseShaderRegister, registerSpace = r.RegisterSpace, offsetInDescriptorsFromTableStart = r.OffsetInDescriptorsFromTableStart }).ToArray() } : null,
                        descriptor = p.Descriptor.HasValue ? new { shaderRegister = p.Descriptor.Value.ShaderRegister, registerSpace = p.Descriptor.Value.RegisterSpace } : null,
                        constants = p.Constant.HasValue ? new { shaderRegister = p.Constant.Value.ShaderRegister, registerSpace = p.Constant.Value.RegisterSpace, num32BitValues = p.Constant.Value.Num32BitValues } : null,
                    }).ToArray();
                    staticSamplers = PixApiExtensionsGpuCaptureResources.GetStaticSamplers(desc).Select(s => Reflect.ToObject(s)).ToArray();
                    break;
                default:
                    parameters = PixApiExtensionsGpuCaptureResources.GetRootParameters1(desc).Select((p, i) => new
                    {
                        index = i,
                        type = p.Type,
                        visibility = p.ShaderVisibility,
                        descriptorTable = p.DescriptorTable.HasValue ? new { ranges = p.DescriptorTable.Value.DescriptorRanges.Select(r => (object)new { rangeType = r.RangeType, numDescriptors = r.NumDescriptors, baseShaderRegister = r.BaseShaderRegister, registerSpace = r.RegisterSpace, offsetInDescriptorsFromTableStart = r.OffsetInDescriptorsFromTableStart, flags = r.Flags }).ToArray() } : null,
                        descriptor = p.Descriptor.HasValue ? new { shaderRegister = p.Descriptor.Value.ShaderRegister, registerSpace = p.Descriptor.Value.RegisterSpace, flags = p.Descriptor.Value.Flags } : null,
                        constants = p.Constant.HasValue ? new { shaderRegister = p.Constant.Value.ShaderRegister, registerSpace = p.Constant.Value.RegisterSpace, num32BitValues = p.Constant.Value.Num32BitValues } : null,
                    }).ToArray();
                    try { staticSamplers = PixApiExtensionsGpuCaptureResources.GetStaticSamplers1(desc).Select(s => Reflect.ToObject(s)).ToArray(); }
                    catch { staticSamplers = PixApiExtensionsGpuCaptureResources.GetStaticSamplers(desc).Select(s => Reflect.ToObject(s)).ToArray(); }
                    break;
            }
        }
        catch (Exception ex)
        {
            parameters ??= PixErrors.Unavailable("rootParameters", ex);
        }

        return new
        {
            name = Interop.WOrNull(rs.GetName()),
            apiObjectId = Interop.Hex(rs.GetApiObjectId()),
            version = desc.Version,
            parameters,
            staticSamplers,
        };
    }

    internal static unsafe List<object> Shaders(IPixGpuProgram program)
    {
        var result = new List<object>();
        IPixCollection shaders = PixApiExtensionsGpuCaptureResources.GetShaders(program);
        ulong count = shaders.GetCount();
        for (ulong i = 0; i < count; i++)
        {
            IPixShader? shader = PixApiExtensions.TryGet<IPixShader>(shaders, i, out Exception ex);
            if (shader is null)
            {
                result.Add(new { index = i, unavailable = true, reason = ex?.Message });
                continue;
            }
            result.Add(ShaderDto((int)i, shader));
        }
        return result;
    }

    internal static unsafe object ShaderDto(int index, IPixShader shader)
    {
        string? hash = null;
        try
        {
            byte[]? bytes = PixApiExtensionsShaders.TryGetShaderHash(shader, out _);
            hash = bytes is { Length: > 0 } ? Convert.ToHexString(bytes).ToLowerInvariant() : null;
        }
        catch { }

        var defines = new Dictionary<string, string>();
        try
        {
            uint defineCount = shader.GetDefineCount();
            for (uint d = 0; d < defineCount; d++)
            {
                PCWSTR name = default, value = default;
                shader.GetDefine(d, &name, &value);
                defines[Interop.W(name)] = Interop.W(value);
            }
        }
        catch { }

        var codeTypes = new List<string>();
        foreach (PIX_SHADER_CODE_TYPE codeType in Enum.GetValues<PIX_SHADER_CODE_TYPE>())
        {
            try
            {
                IPixCollection? nodes = PixApiExtensionsShaders.TryGetNodes(shader, codeType, out _);
                if (nodes is not null && nodes.GetCount() > 0)
                {
                    codeTypes.Add(Json.EnumName(codeType));
                }
            }
            catch { }
        }

        return new
        {
            index,
            id = shader.GetId(),
            stage = shader.GetStage(),
            hash,
            entry = Interop.WOrNull(shader.GetEntry()),
            target = Interop.WOrNull(shader.GetTarget()),
            flags = Interop.WOrNull(shader.GetFlags()),
            defines = defines.Count == 0 ? null : defines,
            sizeBytes = shader.GetSize(),
            address = Interop.Hex(shader.GetAddress()),
            availableCode = codeTypes,
        };
    }

    [McpServerTool(Name = "pix_gpu_shader_code", ReadOnly = true), Description("Returns shader source/IL/ISA for a shader bound at a draw/dispatch event (see pix_gpu_pipeline_state for shader indices). Code is split into nodes (files/functions); pick a node or get the first one. Output is capped by maxChars (truncated: true when cut). Needs GPU analysis: started automatically as a job (see waitSeconds).")]
    public static Task<string> ShaderCode(
        PixSession session,
        JobManager jobs,
        [Description("GPU capture handle")] string handle,
        [Description("Queue index")] int queueIndex,
        [Description("Event index of a draw/dispatch event")] uint eventIndex,
        [Description("Shader index within the event's shader list")] int shaderIndex,
        [Description("HLSL, IL (DXIL disassembly) or ISA (default HLSL).")] string codeType = "HLSL",
        [Description("Node index to return code for (default: first node). Use -1 to list nodes only.")] int nodeIndex = 0,
        [Description("Maximum characters of code to return (default 30000).")] int maxChars = 30000,
        [Description(Tools.ReadyWaitDescription)] double waitSeconds = Tools.DefaultReadyWaitSeconds,
        CancellationToken cancellationToken = default)
        => Tools.RunWhenReady(session, jobs, "pix_gpu_shader_code", handle, GpuCaptureHandle.AnalysisPreparation(handle), h =>
        {
            PIX_EVENT_INFO info = h.EventInfo(queueIndex, eventIndex);
            IPixProgramState programState = PixApiExtensionsGpuCapture.GetProgramState(h.Document, ref info);
            IPixGpuProgram program = PixApiExtensionsGpuCaptureResources.GetGpuProgram<IPixGpuProgram>(programState);
            IPixCollection shaders = PixApiExtensionsGpuCaptureResources.GetShaders(program);
            if (shaderIndex < 0 || (ulong)shaderIndex >= shaders.GetCount())
            {
                throw new McpException($"shaderIndex {shaderIndex} is out of range; the event has {shaders.GetCount()} shader(s).");
            }
            IPixShader shader = shaders.Get<IPixShader>((ulong)shaderIndex);
            PIX_SHADER_CODE_TYPE type = Tools.ParseEnum<PIX_SHADER_CODE_TYPE>(codeType);

            IPixCollection? nodes = PixApiExtensionsShaders.TryGetNodes(shader, type, out Exception ex);
            if (nodes is null)
            {
                throw new McpException($"No {Json.EnumName(type)} code is available for this shader: {(ex is null ? "unknown reason" : PixErrors.Describe(ex))}");
            }

            var nodeList = new List<object>();
            ulong nodeCount = nodes.GetCount();
            for (ulong i = 0; i < nodeCount; i++)
            {
                IPixShaderNode? node = PixApiExtensionsShaders.TryGetNode(nodes, i, out _);
                nodeList.Add(new { index = i, id = node?.GetId(), name = node is null ? null : Interop.WOrNull(node.GetName()) });
            }

            string? code = null;
            bool truncated = false;
            if (nodeIndex >= 0 && nodeCount > 0)
            {
                if ((ulong)nodeIndex >= nodeCount)
                {
                    throw new McpException($"nodeIndex {nodeIndex} is out of range; the shader has {nodeCount} node(s).");
                }
                IPixShaderNode? node = PixApiExtensionsShaders.TryGetNode(nodes, (ulong)nodeIndex, out _);
                IPixAnnotatedString? text = node is null ? null : PixApiExtensionsShaders.TryGetCode(node, out _);
                code = text is null ? null : Interop.W(text.GetString());
                if (code is not null && code.Length > maxChars)
                {
                    code = code[..Math.Max(0, maxChars)];
                    truncated = true;
                }
            }

            return new
            {
                shader = ShaderDto(shaderIndex, shader),
                codeType = type,
                nodes = nodeList,
                nodeIndex = nodeIndex >= 0 ? nodeIndex : (int?)null,
                code,
                truncated,
            };
        }, waitSeconds, cancellationToken);
}
