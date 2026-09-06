using System.ComponentModel;
using Microsoft.PIX;
using Microsoft.PIX.Extension;
using Microsoft.PIX.Extension.GpuCapture;
using Microsoft.PIX.Extension.GpuCapture.Resources;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using PixMcp.Pix.Handles;

namespace PixMcp.Tools;

[McpServerToolType]
public static class ResourceTools
{
    [McpServerTool(Name = "pix_gpu_resources", ReadOnly = true), Description("Lists the D3D12 resources in a GPU capture (buffers and textures) with dimensions and formats, paged and filterable. No GPU analysis needed. Use pix_gpu_resource for one resource's full details and views, pix_gpu_event_resources for what a draw binds.")]
    public static Task<string> Resources(
        PixSession session,
        [Description("GPU capture handle")] string handle,
        [Description("First item (default 0).")] int offset = 0,
        [Description("Maximum items (default 100, max 1000).")] int limit = Paging.DefaultLimit,
        [Description("Only resources whose name contains this text (case-insensitive).")] string? nameContains = null,
        [Description("Filter by allocation type: COMMITTED, PLACED, RESERVED.")] string? type = null,
        [Description("Filter by dimension: BUFFER, TEXTURE1D, TEXTURE2D, TEXTURE3D.")] string? dimension = null,
        CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_gpu_resources", () =>
        {
            GpuCaptureHandle h = session.Get<GpuCaptureHandle>(handle);
            (int o, int l) = Paging.Normalize(offset, limit);
            PIX_D3D12_RESOURCE_TYPE? wantedType = string.IsNullOrEmpty(type) ? null : Tools.ParseEnum<PIX_D3D12_RESOURCE_TYPE>(type);
            D3D12_RESOURCE_DIMENSION? wantedDim = string.IsNullOrEmpty(dimension) ? null : Tools.ParseEnum<D3D12_RESOURCE_DIMENSION>(dimension);

            IPixD3D12Resources resources = PixApiExtensionsGpuCapture.GetD3D12Resources(h.Document);
            uint count = resources.GetCount();
            var page = new List<object>();
            long total = 0;
            for (uint i = 0; i < count; i++)
            {
                PIX_D3D12_RESOURCE_TYPE? resourceType = PixApiExtensionsGpuCaptureResources.GetType(resources, i);
                if (wantedType.HasValue && resourceType != wantedType.Value)
                {
                    continue;
                }
                IPixD3D12Resource resource;
                try { resource = PixApiExtensionsGpuCaptureResources.GetD3D12Resource<IPixD3D12Resource>(resources, i); }
                catch { continue; }
                string name = Interop.W(resource.GetName());
                if (!Tools.Contains(name, nameContains))
                {
                    continue;
                }
                D3D12_RESOURCE_DESC2 desc = PixApiExtensionsGpuCaptureResources.GetDesc(resource);
                if (wantedDim.HasValue && desc.Dimension != wantedDim.Value)
                {
                    continue;
                }
                if (total >= o && page.Count < l)
                {
                    page.Add(new
                    {
                        index = i,
                        apiObjectId = Interop.Hex(resource.GetApiObjectId()),
                        name = string.IsNullOrEmpty(name) ? null : name,
                        type = resourceType,
                        dimension = desc.Dimension,
                        width = desc.Width,
                        height = desc.Height,
                        depthOrArraySize = desc.DepthOrArraySize,
                        mipLevels = desc.MipLevels,
                        format = desc.Format,
                        sampleCount = desc.SampleDesc.Count,
                        flags = desc.Flags,
                    });
                }
                total++;
            }
            return Paging.Page(page, total, o, l);
        }, cancellationToken);

    [McpServerTool(Name = "pix_gpu_resource", ReadOnly = true), Description("Full details for one D3D12 resource: description, clear value, initial state/layout, castable formats, heap info and the views created on it. Identify it by index (from pix_gpu_resources), else by exact name, else by apiObjectId (checked in that order). Use pix_gpu_resources to find candidates and pix_gpu_event_resources for what one draw binds.")]
    public static Task<string> Resource(
        PixSession session,
        [Description("GPU capture handle")] string handle,
        [Description("Resource apiObjectId (hex like 0x1A2B or decimal).")] string? apiObjectId = null,
        [Description("Resource index from pix_gpu_resources.")] uint? index = null,
        [Description("Exact resource name.")] string? name = null,
        [Description("Include views created on the resource (default true; may need analysis).")] bool includeViews = true,
        [Description("Resource-local view index to include; omit for all views.")] uint? viewIndex = null,
        [Description("First binding to return in each included view (default 0).")] int bindingOffset = 0,
        [Description("Maximum bindings per view (default 32, max 1000).")] int bindingLimit = 32,
        CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_gpu_resource", () =>
        {
            GpuCaptureHandle h = session.Get<GpuCaptureHandle>(handle);
            IPixD3D12Resource resource = FindResource(h, apiObjectId, index, name);
            if (viewIndex.HasValue && !includeViews) throw new McpException("viewIndex requires includeViews=true.");
            return ResourceDto(resource, includeViews, includeDesc: true, viewIndex, bindingOffset, bindingLimit);
        }, cancellationToken);

    private static IPixD3D12Resource FindResource(GpuCaptureHandle h, string? apiObjectId, uint? index, string? name)
    {
        IPixD3D12Resources resources = PixApiExtensionsGpuCapture.GetD3D12Resources(h.Document);
        if (index.HasValue)
        {
            if (index.Value >= resources.GetCount())
            {
                throw new McpException($"Resource index {index} is out of range; the capture has {resources.GetCount()} resource(s).");
            }
            return PixApiExtensionsGpuCaptureResources.GetD3D12Resource<IPixD3D12Resource>(resources, index.Value);
        }
        if (!string.IsNullOrEmpty(name))
        {
            if (PixApiExtensionsGpuCapture.TryGetResourceByName<IPixD3D12Resource>(h.Document, name, out IPixD3D12Resource byName) && byName is not null)
            {
                return byName;
            }
            throw new McpException($"No resource named '{name}' was found.");
        }
        if (!string.IsNullOrEmpty(apiObjectId))
        {
            ulong id = Tools.ParseId(apiObjectId, "apiObjectId");
            uint count = resources.GetCount();
            for (uint i = 0; i < count; i++)
            {
                IPixD3D12Resource candidate;
                try { candidate = PixApiExtensionsGpuCaptureResources.GetD3D12Resource<IPixD3D12Resource>(resources, i); }
                catch { continue; }
                if (candidate.GetApiObjectId() == id)
                {
                    return candidate;
                }
            }
            throw new McpException($"No resource with apiObjectId {apiObjectId} was found.");
        }
        throw new McpException("Specify apiObjectId, index or name.");
    }

    internal static object ResourceDesc(D3D12_RESOURCE_DESC2 desc) => new
    {
        dimension = desc.Dimension,
        alignment = desc.Alignment,
        width = desc.Width,
        height = desc.Height,
        depthOrArraySize = desc.DepthOrArraySize,
        mipLevels = desc.MipLevels,
        format = desc.Format,
        sampleCount = desc.SampleDesc.Count,
        sampleQuality = desc.SampleDesc.Quality,
        layout = desc.Layout,
        flags = desc.Flags,
    };

    internal static object ResourceDto(IPixD3D12Resource resource, bool includeViews, bool includeDesc,
        uint? viewIndex = null, int bindingOffset = 0, int bindingLimit = 32)
    {
        string name = Interop.W(resource.GetName());
        PIX_D3D12_RESOURCE_TYPE type = resource.GetType();
        object? desc = null;
        object? clearValue = null;
        object? initialState = null;
        object? initialLayout = null;
        object? castable = null;
        object? heap = null;
        if (includeDesc)
        {
            desc = Tools.Try(() => ResourceDesc(PixApiExtensionsGpuCaptureResources.GetDesc(resource)), "desc");
            clearValue = Tools.Try(() => Reflect.ToObject(PixApiExtensionsGpuCaptureResources.GetClearValue(resource)), "clearValue");
            initialState = Tools.Try(() => PixApiExtensionsGpuCaptureResources.GetInitialState(resource), "initialState");
            initialLayout = Tools.Try(() => PixApiExtensionsGpuCaptureResources.GetInitialLayout(resource), "initialLayout");
            castable = Tools.Try(() => PixApiExtensionsGpuCaptureResources.GetCastableFormats(resource), "castableFormats");
            try
            {
                if (resource is IPixCommittedD3D12Resource committed)
                {
                    heap = new { kind = "committed", properties = Reflect.ToObject(PixApiExtensionsGpuCaptureResources.GetHeapProperties(committed)), flags = committed.GetHeapFlags() };
                }
                else if (resource is IPixPlacedD3D12Resource placed)
                {
                    heap = new { kind = "placed", heapId = Interop.Hex(placed.GetHeapId()), heapOffset = placed.GetHeapOffset() };
                }
                else if (resource is IPixReservedD3D12Resource)
                {
                    heap = new { kind = "reserved" };
                }
            }
            catch (Exception ex) { heap = PixErrors.Unavailable("heap", ex); }
        }

        object? views = null;
        if (includeViews)
        {
            try
            {
                IPixResourceViewsForResource rv = PixApiExtensionsGpuCaptureResources.GetD3D12ResourceViews(resource);
                views = ViewList(rv, includeResource: false, viewIndex, bindingOffset, bindingLimit);
            }
            catch (McpException) { throw; }
            catch (Exception ex) { views = PixErrors.Unavailable("views", ex); }
        }

        return new
        {
            apiObjectId = Interop.Hex(resource.GetApiObjectId()),
            name = string.IsNullOrEmpty(name) ? null : name,
            type,
            barrierStateType = resource.GetInitialBarrierStateType(),
            desc,
            clearValue,
            initialState,
            initialLayout,
            castableFormats = castable,
            heap,
            views,
        };
    }

    internal static List<object> ViewList(IPixResourceViews views, bool includeResource,
        uint? viewIndex = null, int bindingOffset = 0, int bindingLimit = 32)
    {
        var list = new List<object>();
        uint count = views.GetCount();
        ValidateViewIndex(viewIndex, count);
        for (uint i = 0; i < count; i++)
        {
            if (viewIndex.HasValue && viewIndex.Value != i) continue;
            list.Add(ViewDto(views, i, includeResource, bindingOffset, bindingLimit));
        }
        return list;
    }

    internal static void ValidateViewIndex(uint? viewIndex, uint count)
    {
        if (viewIndex >= count) throw new McpException($"viewIndex {viewIndex} is out of range; there are {count} view(s).");
    }

    internal static object ViewDto(IPixResourceViews views, uint index, bool includeResource, int bindingOffset = 0, int bindingLimit = 32)
    {
        PIX_RESOURCE_VIEW_TYPE viewType = PIX_RESOURCE_VIEW_TYPE.PIX_RESOURCE_NONE;
        string? viewTypeError = null;
        try { _IPixResourceViews_Extensions.GetType(views, index, ref viewType); } catch (Exception ex) { viewTypeError = PixErrors.Describe(ex); }

        object? desc = null;
        ulong? offset = null;
        object? resource = null;
        try
        {
            switch (viewType)
            {
                case PIX_RESOURCE_VIEW_TYPE.PIX_RESOURCE_CONSTANT_BUFFER_VIEW:
                {
                    var v = PixApiExtensionsGpuCaptureResources.GetResourceView<IPixConstantBufferView>(views, index);
                    desc = Reflect.ToObject(PixApiExtensionsGpuCaptureResources.GetConstantBufferViewDesc(v));
                    offset = v.GetBufferLocationOffset();
                    break;
                }
                case PIX_RESOURCE_VIEW_TYPE.PIX_RESOURCE_VERTEX_BUFFER_VIEW:
                {
                    var v = PixApiExtensionsGpuCaptureResources.GetResourceView<IPixVertexBufferView>(views, index);
                    desc = Reflect.ToObject(PixApiExtensionsGpuCaptureResources.GetVertexBufferViewDesc(v));
                    offset = v.GetBufferLocationOffset();
                    break;
                }
                case PIX_RESOURCE_VIEW_TYPE.PIX_RESOURCE_INDEX_BUFFER_VIEW:
                {
                    var v = PixApiExtensionsGpuCaptureResources.GetResourceView<IPixIndexBufferView>(views, index);
                    desc = Reflect.ToObject(PixApiExtensionsGpuCaptureResources.GetIndexBufferViewDesc(v));
                    offset = v.GetBufferLocationOffset();
                    break;
                }
                case PIX_RESOURCE_VIEW_TYPE.PIX_RESOURCE_STREAM_OUTPUT_VIEW:
                {
                    var v = PixApiExtensionsGpuCaptureResources.GetResourceView<IPixStreamOutputView>(views, index);
                    desc = Reflect.ToObject(PixApiExtensionsGpuCaptureResources.GetStreamOutputViewDesc(v));
                    offset = v.GetBufferLocationOffset();
                    break;
                }
                case PIX_RESOURCE_VIEW_TYPE.PIX_RESOURCE_SHADER_RESOURCE_VIEW:
                {
                    var v = PixApiExtensionsGpuCaptureResources.GetResourceView<IPixShaderResourceView>(views, index);
                    desc = Interop.CollapseUnion(Reflect.ToObject(PixApiExtensionsGpuCaptureResources.GetShaderResourceViewDesc(v)));
                    offset = PixApiExtensionsGpuCaptureResources.GetLocationOffset(v);
                    break;
                }
                case PIX_RESOURCE_VIEW_TYPE.PIX_RESOURCE_RENDER_TARGET_VIEW:
                {
                    var v = PixApiExtensionsGpuCaptureResources.GetResourceView<IPixRenderTargetView>(views, index);
                    desc = Interop.CollapseUnion(Reflect.ToObject(PixApiExtensionsGpuCaptureResources.GetRenderTargetViewDesc(v)));
                    break;
                }
                case PIX_RESOURCE_VIEW_TYPE.PIX_RESOURCE_UNORDERED_ACCESS_VIEW:
                {
                    var v = PixApiExtensionsGpuCaptureResources.GetResourceView<IPixUnorderedAccessView>(views, index);
                    desc = Interop.CollapseUnion(Reflect.ToObject(PixApiExtensionsGpuCaptureResources.GetUnorderedAccessViewDesc(v)));
                    break;
                }
                case PIX_RESOURCE_VIEW_TYPE.PIX_RESOURCE_DEPTH_STENCIL_VIEW:
                {
                    var v = PixApiExtensionsGpuCaptureResources.GetResourceView<IPixDepthStencilView>(views, index);
                    desc = Interop.CollapseUnion(Reflect.ToObject(PixApiExtensionsGpuCaptureResources.GetDepthStencilViewDesc(v)));
                    break;
                }
                case PIX_RESOURCE_VIEW_TYPE.PIX_RESOURCE_BUFFER:
                {
                    var v = PixApiExtensionsGpuCaptureResources.GetResourceView<IPixBufferResourceView>(views, index);
                    desc = new { sizeInBytes = v.GetSizeInBytes() };
                    offset = v.GetBufferLocationOffset();
                    break;
                }
                case PIX_RESOURCE_VIEW_TYPE.PIX_RESOURCE_TEXTURE:
                {
                    var v = PixApiExtensionsGpuCaptureResources.GetResourceView<IPixTextureResourceView>(views, index);
                    desc = Reflect.ToObject(PixApiExtensionsGpuCaptureResources.GetSubresourceRange(v));
                    break;
                }
                case PIX_RESOURCE_VIEW_TYPE.PIX_RESOURCE_SAMPLER:
                {
                    var v = PixApiExtensionsGpuCaptureResources.GetResourceView<IPixSampler>(views, index);
                    desc = Reflect.ToObject(PixApiExtensionsGpuCaptureResources.GetSamplerDesc(v));
                    break;
                }
                case PIX_RESOURCE_VIEW_TYPE.PIX_RESOURCE_STATIC_SAMPLER:
                {
                    var v = PixApiExtensionsGpuCaptureResources.GetResourceView<IPixStaticSampler>(views, index);
                    desc = Reflect.ToObject(PixApiExtensionsGpuCaptureResources.GetSamplerDesc(v));
                    break;
                }
                case PIX_RESOURCE_VIEW_TYPE.PIX_RESOURCE_ROOT_CONSTANT:
                {
                    var v = PixApiExtensionsGpuCaptureResources.GetResourceView<IPixRootConstants>(views, index);
                    desc = Reflect.ToObject(PixApiExtensionsGpuCaptureResources.GetRootConstantsDesc(v));
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            desc = PixErrors.Unavailable("desc", ex);
        }

        if (includeResource)
        {
            // Root constants, samplers and similar views have no backing resource; PIX signals that by failing the query.
            try
            {
                var d3dView = PixApiExtensionsGpuCaptureResources.GetResourceView<IPixD3D12ResourceView>(views, index);
                IPixD3D12Resource res = PixApiExtensionsGpuCaptureResources.GetD3D12Resource(d3dView);
                if (res is not null)
                {
                    resource = new { apiObjectId = Interop.Hex(res.GetApiObjectId()), name = Interop.WOrNull(res.GetName()) };
                }
            }
            catch { }
        }

        object? bindings = null;
        uint? bindingCount = null;
        int? bindingCountReturned = null;
        long? nextBindingOffset = null;
        bool? bindingsTruncated = null;
        (int bo, int bl) = Paging.Normalize(bindingOffset, bindingLimit);
        try
        {
            var view = PixApiExtensionsGpuCaptureResources.GetResourceView<IPixResourceView>(views, index);
            IPixResourceViewBindings rb = PixApiExtensionsGpuCaptureResources.GetResourceBindings(view);
            bindingCount = rb.GetCount();
            var page = BindingPage(bindingCount.Value, bo, bl, b => BindingDto(rb, b));
            bindings = page.Items;
            bindingCountReturned = page.Items.Count;
            nextBindingOffset = page.NextOffset;
            bindingsTruncated = page.Items.Count < bindingCount.Value;
        }
        catch (Exception ex) { bindings = PixErrors.Unavailable("bindings", ex); }

        return new { index, type = viewType, typeError = viewTypeError, desc, bufferLocationOffset = offset, resource, bindings,
            bindingCount, bindingOffset = bindingCount.HasValue ? (int?)bo : null, bindingCountReturned, nextBindingOffset, bindingsTruncated };
    }

    internal static (List<object> Items, long? NextOffset) BindingPage(uint count, int offset, int limit, Func<uint, object> read)
    {
        (int o, int l) = Paging.Normalize(offset, limit);
        var items = new List<object>();
        for (uint i = (uint)o; i < count && items.Count < l; i++) items.Add(read(i));
        long next = (long)o + items.Count;
        return (items, next < count ? next : null);
    }

    private static object BindingDto(IPixResourceViewBindings bindings, uint index)
    {
        try
        {
            IPixResourceBinding binding = PixApiExtensionsGpuCaptureResources.GetViewBinding<IPixResourceBinding>(bindings, index);
            PIX_RESOURCE_BINDING_TYPE type = binding.GetType();
            object? detail = null;
            switch (binding)
            {
                case IPixRootParameterResourceBinding rp:
                    detail = new
                    {
                        name = Interop.WOrNull(rp.GetName()),
                        stage = PixApiExtensionsGpuCaptureResources.GetGenericPipelineStage(rp),
                        space = rp.GetSpace(),
                        register = rp.GetIndex(),
                        rootParameterIndex = rp.GetRootParameterIndex(),
                        rangeIndex = PixApiExtensionsGpuCaptureResources.GetRangeIndex(rp),
                        indexInRange = PixApiExtensionsGpuCaptureResources.GetIndexInRange(rp),
                        descriptorHeapIndex = PixApiExtensionsGpuCaptureResources.GetDescriptorHeapIndex(rp),
                    };
                    break;
                case IPixStaticSamplerResourceBinding ss:
                    detail = new { name = Interop.WOrNull(ss.GetName()), stage = PixApiExtensionsGpuCaptureResources.GetGenericPipelineStage(ss), space = ss.GetSpace(), register = ss.GetIndex() };
                    break;
                case IPixProgramResourceBinding pb:
                    detail = new { stage = PixApiExtensionsGpuCaptureResources.GetGenericPipelineStage(pb), bindingIndex = pb.GetBindingIndex() };
                    break;
                case IPixDynamicHeapResourceBinding dh:
                    detail = new { name = Interop.WOrNull(dh.GetName()), stage = PixApiExtensionsGpuCaptureResources.GetGenericPipelineStage(dh), descriptorHeapIndex = dh.GetDescriptorHeapIndex() };
                    break;
                case IPixApiParameterResourceBinding ap:
                    detail = new { apiParameterType = ap.GetApiParameterType() };
                    break;
            }
            return new { index, type, detail };
        }
        catch (Exception ex)
        {
            return new { index, unavailable = true, reason = PixErrors.Describe(ex) };
        }
    }

    [McpServerTool(Name = "pix_gpu_event_resources", ReadOnly = true), Description("Resources and views accessed by a draw/dispatch event: every bound view (CBV/SRV/UAV/RTV/DSV/VBV/IBV/samplers) with its description and binding (root parameter, register, space, stage), grouped by resource. The event must be a draw/dispatch. Needs GPU analysis: started automatically as a job (see waitSeconds). Bindings per view are paged with bindingOffset/bindingLimit.")]
    public static Task<string> EventResources(
        PixSession session,
        JobManager jobs,
        [Description("GPU capture handle")] string handle,
        [Description("Queue index")] int queueIndex,
        [Description("Event index of a draw/dispatch event")] uint eventIndex,
        [Description("Event-global view index to include; omit for all views.")] uint? viewIndex = null,
        [Description("First binding to return in each included view (default 0).")] int bindingOffset = 0,
        [Description("Maximum bindings per view (default 32, max 1000).")] int bindingLimit = 32,
        [Description(Tools.ReadyWaitDescription)] double waitSeconds = Tools.DefaultReadyWaitSeconds,
        CancellationToken cancellationToken = default)
        => Tools.RunWhenReady(session, jobs, "pix_gpu_event_resources", handle, GpuCaptureHandle.AnalysisPreparation(handle), h =>
        {
            EventRecord record = h.Event(queueIndex, eventIndex);
            PIX_EVENT_INFO info = h.EventInfo(queueIndex, eventIndex);

            IPixProgramState programState = PixApiExtensionsGpuCapture.GetProgramState(h.Document, ref info);
            IPixGenericPipeline pipeline = PixApiExtensionsGpuCaptureResources.GetGpuProgram<IPixGenericPipeline>(programState);
            IPixResourceViewsAtEvent viewsAtEvent = PixApiExtensionsGpuCaptureResources.GetResourceViews(pipeline);

            IPixGpuCaptureAnalysis analysis = h.GetAnalysis();
            analysis.GatherAccessedResources();
            analysis.GetAccessedResources(viewsAtEvent);

            var byResource = new Dictionary<string, (object resource, List<object> views)>();
            var unattached = new List<object>();
            uint count = viewsAtEvent.GetCount();
            ValidateViewIndex(viewIndex, count);
            for (uint i = 0; i < count; i++)
            {
                if (viewIndex.HasValue && viewIndex.Value != i) continue;
                object dto = ViewDto(viewsAtEvent, i, includeResource: true, bindingOffset, bindingLimit);
                IPixD3D12Resource? res = null;
                try
                {
                    var d3dView = PixApiExtensionsGpuCaptureResources.GetResourceView<IPixD3D12ResourceView>(viewsAtEvent, i);
                    res = PixApiExtensionsGpuCaptureResources.GetD3D12Resource(d3dView);
                }
                catch { }

                if (res is null)
                {
                    unattached.Add(dto);
                    continue;
                }
                string key = Interop.Hex(res.GetApiObjectId());
                if (!byResource.TryGetValue(key, out var entry))
                {
                    entry = (ResourceDto(res, includeViews: false, includeDesc: true), new List<object>());
                    byResource[key] = entry;
                }
                entry.views.Add(dto);
            }

            return new
            {
                @event = record.ToDto(queueIndex),
                viewCount = count,
                resources = byResource.Values.Select(v => new { resource = v.resource, views = v.views }).ToArray(),
                otherViews = unattached.Count == 0 ? null : unattached,
            };
        }, waitSeconds, cancellationToken);
}
