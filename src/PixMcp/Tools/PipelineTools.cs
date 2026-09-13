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
    private static readonly Lazy<JsonSerializerOptions> SubobjectJsonOptions = new(() =>
    {
        var options = new JsonSerializerOptions(PipelineStateSubobjectJsonSerializerOptions.DefaultOptions);
        options.Converters.Insert(0, Json.EnumConverterFactory);
        return options;
    });

    [McpServerTool(Name = "pix_gpu_pipeline_state", Title = "Pipeline state at event", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Replays the capture on the local GPU if analysis is not started. Program/pipeline state bound at a Draw, Dispatch, DispatchMesh or DispatchRays event: program type, pipeline type, PSO subobjects (blend, rasterizer, depth-stencil, input layout, ...), global root signature and bound shaders. The event must be a draw/dispatch (other events fail with E_NOT_VALID_STATE). Needs GPU analysis: started automatically as a job (see waitSeconds). For bound resources use pix_gpu_event_resources; for shader code use pix_gpu_shader_code.")]
    public static Task<string> PipelineState(
        PixSession session,
        JobManager jobs,
        [Description("Event reference from an event listing or timing result.")] EventRef eventRef,
        [Description("Include decoded PSO subobject details (default false).")] bool includeSubobjects = false,
        [Description("Include the global root signature parameters (default false).")] bool includeRootSignature = false,
        [Description("Include the bound shaders (default true).")] bool includeShaders = true,
        [Description(Tools.ReadyWaitDescription)] double waitSeconds = Tools.DefaultReadyWaitSeconds,
        CancellationToken cancellationToken = default)
    {
        ReferenceValidation.Event(session, eventRef);
        return Tools.RunWhenReady(session, jobs, "pix_gpu_pipeline_state", eventRef.Handle,
            GpuCaptureHandle.AnalysisPreparation(eventRef.Handle),
            h => QueryPipelineState(h, eventRef, includeSubobjects, includeRootSignature, includeShaders), waitSeconds, cancellationToken);
    }

    internal static PipelineStateDto QueryPipelineState(GpuCaptureHandle h, EventRef eventRef,
        bool includeSubobjects = true, bool includeRootSignature = true, bool includeShaders = true)
    {
            int queueIndex = eventRef.QueueIndex;
            uint eventIndex = eventRef.EventIndex;
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
                try { shaders = Shaders(program, eventRef); }
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
                        // GetSubobjects copies structs, not the native pDesc storage and strings.
                        // Keep the COM owner alive until every borrowed pointer has been decoded.
                        subobjects = ReadWithNativeOwner(ps,
                            () => list.Select((so, i) => new { index = i, type = so.Type, detail = SubobjectDetail(so) }).ToArray());
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
                            for (uint i = 0; i < recordCount; i++)
                            {
                                IPixShaderRecord rec = PixApiExtensionsGpuCaptureResources.GetShaderRecord(table, i);
                                object? localRs = Tools.Try(() => Interop.WOrNull(PixApiExtensionsGpuCaptureResources.GetLocalRootSignature(rec)?.GetName() ?? default), "localRootSignature");
                                records.Add(new { index = i, exportName = Interop.W(rec.GetExportName()), localRootSignature = localRs });
                            }
                            object? resourceName = Tools.Try(() => Interop.WOrNull(PixApiExtensionsGpuCaptureResources.GetResource(table)?.GetName() ?? default), "resource");
                            tables.Add(new { stage, recordCount, records, resource = resourceName });
                        }
                        catch (Exception ex) { tables.Add(new { stage, unavailable = true, reason = PixErrors.Describe(ex) }); }
                    }
                    raytracing = new { shaderTables = tables };
                }
                catch (Exception ex) { raytracing = PixErrors.Unavailable("raytracingPipeline", ex); }
            }

            return new PipelineStateDto(eventRef, EventNavigation.MarkerPath(h.AllEvents(queueIndex), eventIndex),
                h.DescribeEvent(queueIndex, record), programType.HasValue ? Json.EnumName(programType.Value) : null,
                rootSignature, shaders, generic, raytracing);
    }

    internal static T ReadWithNativeOwner<T>(object owner, Func<T> read)
    {
        try { return read(); }
        finally { GC.KeepAlive(owner); }
    }

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

    internal static unsafe List<object> Shaders(IPixGpuProgram program, EventRef? eventRef = null)
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
            result.Add(ShaderDto((int)i, shader, eventRef));
        }
        return result;
    }

    internal static unsafe ShaderInfoDto ShaderDto(int index, IPixShader shader, EventRef? eventRef = null)
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

        return new ShaderInfoDto(eventRef is null ? null : new ShaderRef(eventRef, index), index,
            shader.GetId().ToString(System.Globalization.CultureInfo.InvariantCulture), Json.EnumName(shader.GetStage()), hash,
            Interop.WOrNull(shader.GetEntry()), Interop.WOrNull(shader.GetTarget()), Interop.WOrNull(shader.GetFlags()),
            defines.Count == 0 ? null : defines, shader.GetSize(), Interop.Hex(shader.GetAddress()), codeTypes);
    }

    internal static IPixGpuProgram ReadProgram(GpuCaptureHandle h, EventRef eventRef)
    {
        PIX_EVENT_INFO info = h.EventInfo(eventRef.QueueIndex, eventRef.EventIndex);
        IPixProgramState state = PixApiExtensionsGpuCapture.GetProgramState(h.Document, ref info);
        return PixApiExtensionsGpuCaptureResources.GetGpuProgram<IPixGpuProgram>(state);
    }

    internal static IReadOnlyList<ShaderInfoDto> ReadShaders(GpuCaptureHandle h, EventRef eventRef)
    {
        List<object> shaders = Shaders(ReadProgram(h, eventRef), eventRef);
        if (shaders.Any(s => s is not ShaderInfoDto))
            throw new PixToolException(PixErrors.Codes.UnavailableShaderData, "PIX could not read every shader bound at this event; shader matching requires a complete identity list.",
                nextCalls: [new("pix_gpu_pipeline_state", new { eventRef })]);
        return shaders.Cast<ShaderInfoDto>().ToArray();
    }

    internal static IPixShader ReadShader(GpuCaptureHandle h, ShaderRef shaderRef)
    {
        IPixCollection shaders = PixApiExtensionsGpuCaptureResources.GetShaders(ReadProgram(h, shaderRef.EventRef));
        if (shaderRef.ShaderIndex < 0 || (ulong)shaderRef.ShaderIndex >= shaders.GetCount())
            throw PixErrors.InvalidReference($"shaderIndex {shaderRef.ShaderIndex} is out of range; the event has {shaders.GetCount()} shader(s).",
                new ToolCallDto("pix_gpu_pipeline_state", new { eventRef = shaderRef.EventRef }, CostHints.Query));
        return shaders.Get<IPixShader>((ulong)shaderRef.ShaderIndex);
    }

    /// <summary>HLSL of a shader's first code node, or null when PIX has no HLSL for it.</summary>
    internal static string? ReadHlsl(GpuCaptureHandle h, ShaderRef shaderRef)
    {
        IPixCollection nodes = ReadNodes(ReadShader(h, shaderRef), Tools.ParseEnum<PIX_SHADER_CODE_TYPE>("HLSL"));
        return nodes.GetCount() == 0 ? null : ReadCode(nodes, 0);
    }

    [McpServerTool(Name = "pix_gpu_shader_code", Title = "Shader code", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Replays the capture on the local GPU if analysis is not started. Retrieve HLSL, IL or ISA using one-based line windows. Every line is retrievable with nextStartLine. Code nodes are paged independently; nodeIndex=-1 lists nodes only.")]
    public static Task<string> ShaderCode(PixSession session, JobManager jobs,
        [Description("Shader reference returned by pipeline inspection.")] ShaderRef shaderRef,
        [Description("Shader code kind: HLSL (default), IL or ISA.")] ShaderCodeKind codeType = ShaderCodeKind.HLSL,
        [Description("Code node to read (default 0); -1 lists the nodes without code.")] int nodeIndex = 0, [Description("First line to return, 1-based (default 1).")] int startLine = 1, [Description("Lines to return (default 100, max 1000).")] int lineCount = 100,
        [Description("First node to list (default 0).")] int nodeOffset = 0, [Description("Maximum nodes to list (default 25, max 1000).")] int nodeLimit = Paging.DefaultLimit,
        [Description(Tools.ReadyWaitDescription)] double waitSeconds = Tools.DefaultReadyWaitSeconds,
        CancellationToken cancellationToken = default)
    {
        ValidateShaderRequest(shaderRef, nodeIndex, startLine, lineCount, nodeOffset, nodeLimit);
        if (!Enum.IsDefined(codeType)) throw PixErrors.InvalidArguments("codeType must be HLSL, IL or ISA.");
        ReferenceValidation.Shader(session, shaderRef);
        return Tools.RunWhenReady(session, jobs, "pix_gpu_shader_code", shaderRef.EventRef.Handle,
            GpuCaptureHandle.AnalysisPreparation(shaderRef.EventRef.Handle), h =>
            {
                IPixShader shader = ReadShader(h, shaderRef);
                PIX_SHADER_CODE_TYPE type = Tools.ParseEnum<PIX_SHADER_CODE_TYPE>(codeType.ToString());
                IPixCollection nodes = ReadNodes(shader, type);
                ulong count = nodes.GetCount();
                var nodeList = new List<ShaderNodeDto>();
                for (ulong i = (ulong)nodeOffset; i < count && nodeList.Count < nodeLimit; i++)
                {
                    IPixShaderNode? node = PixApiExtensionsShaders.TryGetNode(nodes, i, out _);
                    nodeList.Add(new(i, node?.GetId().ToString(), node is null ? null : Interop.WOrNull(node.GetName())));
                }
                string? code = null;
                int returned = 0, total = 0;
                int? next = null;
                if (nodeIndex >= 0 && count > 0)
                {
                    if ((ulong)nodeIndex >= count) throw PixErrors.InvalidReference($"nodeIndex {nodeIndex} is out of range; there are {count} nodes.",
                        new ToolCallDto("pix_gpu_shader_diagnostics", new { shaderRef }, CostHints.Query));
                    string fullCode = ReadCode(nodes, (ulong)nodeIndex);
                    (code, returned, total, next) = ShaderText.Window(fullCode, startLine, lineCount);
                }
                PageResult<ShaderNodeDto> nodePage = Paging.Page(nodeList, checked((long)count), nodeOffset, nodeLimit);
                var nextCalls = new List<ToolCallDto>();
                if (next.HasValue) nextCalls.Add(new("pix_gpu_shader_code", new { shaderRef, codeType, nodeIndex,
                    startLine = next.Value, lineCount, nodeOffset, nodeLimit }));
                if (nodePage.NextOffset.HasValue) nextCalls.Add(new("pix_gpu_shader_code", new { shaderRef, codeType,
                    nodeIndex = -1, nodeOffset = nodePage.NextOffset.Value, nodeLimit }));
                return new ShaderCodeDto(shaderRef, ShaderDto(shaderRef.ShaderIndex, shader, shaderRef.EventRef),
                    Json.EnumName(type), nodePage, nodeIndex >= 0 ? nodeIndex : null, startLine, returned, total, next, code)
                    { NextCalls = nextCalls };
            }, waitSeconds, cancellationToken);
    }

    [McpServerTool(Name = "pix_gpu_shader_search", Title = "Search shader code", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Replays the capture on the local GPU if analysis is not started. Search shader code for literal case-insensitive text. Returns matching line numbers and context, paged across all code nodes or a selected node. Use pix_gpu_shader_code to read larger windows.")]
    public static Task<string> ShaderSearch(PixSession session, JobManager jobs, [Description("Shader reference { eventRef, shaderIndex } as returned by pix_gpu_pipeline_state or pix_gpu_shaders.")] ShaderRef shaderRef,
        [Description("Literal text to find (case-insensitive, single line).")] string query, [Description("Shader code kind: HLSL (default), IL or ISA.")] ShaderCodeKind codeType = ShaderCodeKind.HLSL, [Description("Restrict the search to one code node; default searches every node.")] int? nodeIndex = null,
        [Description("Lines of context around each match (default 2, max 20).")] int contextLines = 2, [Description("First item to return (default 0).")] int offset = 0, [Description("Maximum items to return (default 25, max 1000).")] int limit = Paging.DefaultLimit,
        [Description(Tools.ReadyWaitDescription)] double waitSeconds = Tools.DefaultReadyWaitSeconds,
        CancellationToken cancellationToken = default)
    {
        ValidateShaderRequest(shaderRef, nodeIndex ?? -1, 1, 1, offset, limit);
        if (!Enum.IsDefined(codeType)) throw PixErrors.InvalidArguments("codeType must be HLSL, IL or ISA.");
        ReferenceValidation.Shader(session, shaderRef);
        if (nodeIndex < 0) throw PixErrors.InvalidArguments("nodeIndex must be nonnegative when supplied.");
        if (string.IsNullOrEmpty(query)) throw PixErrors.InvalidArguments("query must not be empty.");
        if (query.Contains('\n') || query.Contains('\r')) throw PixErrors.InvalidArguments("query must fit on a single line.");
        if (contextLines is < 0 or > 20) throw PixErrors.InvalidArguments("contextLines must be between 0 and 20.");
        return Tools.RunWhenReady(session, jobs, "pix_gpu_shader_search", shaderRef.EventRef.Handle,
            GpuCaptureHandle.AnalysisPreparation(shaderRef.EventRef.Handle), h =>
            {
                IPixShader shader = ReadShader(h, shaderRef);
                PIX_SHADER_CODE_TYPE type = Tools.ParseEnum<PIX_SHADER_CODE_TYPE>(codeType.ToString());
                IPixCollection nodes = ReadNodes(shader, type);
                ulong nodeCount = nodes.GetCount();
                if (nodeIndex is >= 0 && (ulong)nodeIndex.Value >= nodeCount) throw PixErrors.InvalidReference($"nodeIndex {nodeIndex} is out of range; there are {nodeCount} nodes.",
                    new ToolCallDto("pix_gpu_shader_diagnostics", new { shaderRef }, CostHints.Query));
                var items = new List<ShaderSearchMatchDto>();
                var coverage = new List<object>();
                long total = 0;
                for (ulong i = 0; i < nodeCount; i++)
                {
                    if (nodeIndex.HasValue && (ulong)nodeIndex.Value != i) continue;
                    try
                    {
                        IPixShaderNode? node = PixApiExtensionsShaders.TryGetNode(nodes, i, out _);
                        string? name = node is null ? null : Interop.WOrNull(node.GetName());
                        foreach (ShaderSearchMatchDto match in ShaderText.Search(ReadCode(nodes, i), query, i, name, contextLines))
                        {
                            if (total >= offset && items.Count < limit) items.Add(match);
                            total++;
                        }
                    }
                    catch (Exception ex) { coverage.Add(new { nodeIndex = i, unavailable = true, reason = PixErrors.Describe(ex) }); }
                }
                int? next = (long)offset + items.Count < total ? offset + items.Count : null;
                return new ShaderSearchDto(shaderRef, Json.EnumName(type), query, false, total, offset, items.Count,
                    next, items, coverage)
                    { NextCalls = next.HasValue ? [new("pix_gpu_shader_search", new { shaderRef, query, codeType,
                        nodeIndex, contextLines, offset = next.Value, limit })] : [] };
            }, waitSeconds, cancellationToken);
    }

    private static void ValidateShaderRequest(ShaderRef shaderRef, int nodeIndex, int startLine, int lineCount, int offset, int limit)
    {
        if (shaderRef is null || shaderRef.EventRef is null || string.IsNullOrWhiteSpace(shaderRef.EventRef.Handle)) throw PixErrors.InvalidArguments("shaderRef and its eventRef are required.");
        if (shaderRef.ShaderIndex < 0 || shaderRef.EventRef.QueueIndex < 0) throw PixErrors.InvalidArguments("Shader and queue indices must be nonnegative.");
        if (nodeIndex < -1) throw PixErrors.InvalidArguments("nodeIndex must be -1 or a nonnegative index.");
        ShaderText.ValidateWindow(startLine, lineCount);
        if (offset < 0 || limit is < 1 or > Paging.MaxLimit) throw PixErrors.InvalidArguments("offset must be nonnegative and limit must be between 1 and 1000.");
    }

    private static IPixCollection ReadNodes(IPixShader shader, PIX_SHADER_CODE_TYPE type)
    {
        IPixCollection? nodes = PixApiExtensionsShaders.TryGetNodes(shader, type, out Exception ex);
        return nodes ?? throw PixErrors.UnavailableShaderData($"No {Json.EnumName(type)} code is available: {(ex is null ? "PIX returned no nodes" : PixErrors.Describe(ex))}");
    }

    private static string ReadCode(IPixCollection nodes, ulong nodeIndex)
    {
        IPixShaderNode? node = PixApiExtensionsShaders.TryGetNode(nodes, nodeIndex, out Exception nodeError);
        if (node is null) throw PixErrors.UnavailableShaderData(nodeError is null ? "Shader node is unavailable." : PixErrors.Describe(nodeError));
        IPixAnnotatedString? text = PixApiExtensionsShaders.TryGetCode(node, out Exception codeError);
        if (text is null) throw PixErrors.UnavailableShaderData(codeError is null ? "Shader code is unavailable." : PixErrors.Describe(codeError));
        return Interop.W(text.GetString());
    }
}

public enum ShaderCodeKind { HLSL, IL, ISA }
