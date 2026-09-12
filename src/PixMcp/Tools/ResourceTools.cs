using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
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
        [Description("Maximum items (default 25, max 1000).")] int limit = Paging.DefaultLimit,
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
            var page = new List<ResourceSummaryDto>();
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
                    page.Add(new ResourceSummaryDto(i, new ResourceRef(handle, Interop.Hex(resource.GetApiObjectId())),
                        Interop.Hex(resource.GetApiObjectId()), string.IsNullOrEmpty(name) ? null : name,
                        resourceType.HasValue ? Json.EnumName(resourceType.Value) : null, Json.EnumName(desc.Dimension),
                        desc.Width, desc.Height, desc.DepthOrArraySize, desc.MipLevels, Json.EnumName(desc.Format),
                        desc.SampleDesc.Count, Json.EnumName(desc.Flags)));
                }
                total++;
            }
            return Paging.Page(page, total, o, l);
        }, cancellationToken);

    [McpServerTool(Name = "pix_gpu_resource", ReadOnly = true), Description("Full details for a resource reference returned by pix_gpu_resources: description, clear value, initial state/layout, castable formats, heap info and independently paged views and bindings.")]
    public static Task<string> Resource(
        PixSession session,
        ResourceRef resourceRef,
        [Description("Include views created on the resource (default true; may need analysis).")] bool includeViews = true,
        [Description("Resource-local view index to include; omit for all views.")] uint? viewIndex = null,
        [Description("First binding to return in each included view (default 0).")] int bindingOffset = 0,
        [Description("Maximum bindings per view (default 25, max 1000).")] int bindingLimit = Paging.DefaultLimit,
        int viewOffset = 0,
        int viewLimit = Paging.DefaultLimit,
        CancellationToken cancellationToken = default)
    {
        ReferenceValidation.Resource(session, resourceRef);
        ReferenceValidation.Page(viewOffset, viewLimit);
        ReferenceValidation.Page(bindingOffset, bindingLimit);
        return Tools.Run(session, "pix_gpu_resource", () =>
        {
            GpuCaptureHandle h = session.Get<GpuCaptureHandle>(resourceRef.Handle);
            IPixD3D12Resource resource = FindResource(h, resourceRef.ApiObjectId, null, null);
            if (viewIndex.HasValue && !includeViews) throw new McpException("viewIndex requires includeViews=true.");
            return ResourceDto(resource, includeViews, includeDesc: true, viewIndex, bindingOffset, bindingLimit, h, viewOffset, viewLimit);
        }, cancellationToken);
    }

    internal static IPixD3D12Resource FindResource(GpuCaptureHandle h, string? apiObjectId, uint? index = null, string? name = null)
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

    internal static ResourceDetailsDto ResourceDto(IPixD3D12Resource resource, bool includeViews, bool includeDesc,
        uint? viewIndex = null, int bindingOffset = 0, int bindingLimit = Paging.DefaultLimit, GpuCaptureHandle? h = null,
        int viewOffset = 0, int viewLimit = Paging.DefaultLimit)
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
                views = ViewList(rv, includeResource: false, viewIndex, bindingOffset, bindingLimit, h, viewOffset, viewLimit,
                    h is null ? null : new ResourceRef(h.Id, Interop.Hex(resource.GetApiObjectId())));
            }
            catch (McpException) { throw; }
            catch (Exception ex) { views = PixErrors.Unavailable("views", ex); }
        }

        ResourceRef? resourceRef = h is null ? null : new ResourceRef(h.Id, Interop.Hex(resource.GetApiObjectId()));
        return new ResourceDetailsDto(resourceRef,
            Interop.Hex(resource.GetApiObjectId()), string.IsNullOrEmpty(name) ? null : name, Json.EnumName(type),
            Json.EnumName(resource.GetInitialBarrierStateType()), desc, clearValue, initialState, initialLayout, castable, heap, views)
        {
            NextCalls = resourceRef is not null && views is PageResult<object> page && page.NextOffset.HasValue
                ? [new("pix_gpu_resource", new { resourceRef, includeViews = true, viewOffset = page.NextOffset.Value,
                    viewLimit, bindingOffset, bindingLimit })] : [],
        };
    }

    internal static PageResult<object> ViewList(IPixResourceViews views, bool includeResource,
        uint? viewIndex = null, int bindingOffset = 0, int bindingLimit = Paging.DefaultLimit, GpuCaptureHandle? h = null,
        int viewOffset = 0, int viewLimit = Paging.DefaultLimit, ResourceRef? ownerResourceRef = null)
    {
        var list = new List<object>();
        uint count = views.GetCount();
        ValidateViewIndex(viewIndex, count);
        (int o, int l) = Paging.Normalize(viewOffset, viewLimit);
        uint first = viewIndex ?? (uint)o;
        for (uint i = first; i < count && list.Count < l; i++)
        {
            if (viewIndex.HasValue && viewIndex.Value != i) continue;
            list.Add(ViewDto(views, i, includeResource, bindingOffset, bindingLimit, h, ownerResourceRef: ownerResourceRef));
            if (viewIndex.HasValue) break;
        }
        return Paging.Page(list, viewIndex.HasValue ? list.Count : count, viewIndex.HasValue ? 0 : o, l);
    }

    internal static void ValidateViewIndex(uint? viewIndex, uint count)
    {
        if (viewIndex >= count) throw new McpException($"viewIndex {viewIndex} is out of range; there are {count} view(s).");
    }

    internal static unsafe object ViewDto(IPixResourceViews views, uint index, bool includeResource, int bindingOffset = 0,
        int bindingLimit = Paging.DefaultLimit, GpuCaptureHandle? h = null, EventRef? ownerEventRef = null, ResourceRef? ownerResourceRef = null)
    {
        PIX_RESOURCE_VIEW_TYPE viewType = PIX_RESOURCE_VIEW_TYPE.PIX_RESOURCE_NONE;
        string? viewTypeError = null;
        try { _IPixResourceViews_Extensions.GetType(views, index, ref viewType); } catch (Exception ex) { viewTypeError = PixErrors.Describe(ex); }

        object? desc = null;
        ulong? offset = null;
        object? resource = null;
        IReadOnlyList<RootConstantValueDto>? values = null;
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
                    D3D12_ROOT_CONSTANTS constants = PixApiExtensionsGpuCaptureResources.GetRootConstantsDesc(v);
                    desc = Reflect.ToObject(constants);
                    var constantValues = new List<RootConstantValueDto>();
                    for (uint i = 0; i < constants.Num32BitValues; i++)
                    {
                        uint value = 0;
                        v.GetValue(i, ref value);
                        constantValues.Add(new(i, value, $"0x{value:X8}"));
                    }
                    values = constantValues;
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
                    resource = new { resourceRef = h is null ? null : new ResourceRef(h.Id, Interop.Hex(res.GetApiObjectId())), apiObjectId = Interop.Hex(res.GetApiObjectId()), name = Interop.WOrNull(res.GetName()) };
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
            var page = BindingPage(bindingCount.Value, bo, bl, b => ReadBinding(rb, b, h, ownerEventRef));
            bindings = page.Items;
            bindingCountReturned = page.Items.Count;
            nextBindingOffset = page.NextOffset;
            bindingsTruncated = page.NextOffset.HasValue;
        }
        catch (Exception ex) { bindings = PixErrors.Unavailable("bindings", ex); }

        var nextCalls = new List<ToolCallDto>();
        if (nextBindingOffset.HasValue)
        {
            if (ownerEventRef is not null) nextCalls.Add(new("pix_gpu_event_resources", new { eventRef = ownerEventRef,
                viewIndex = index, bindingOffset = nextBindingOffset.Value, bindingLimit }));
            else if (ownerResourceRef is not null) nextCalls.Add(new("pix_gpu_resource", new { resourceRef = ownerResourceRef,
                includeViews = true, viewIndex = index, bindingOffset = nextBindingOffset.Value, bindingLimit }));
        }
        return new { index, type = viewType, typeError = viewTypeError, desc, values, bufferLocationOffset = offset, resource, bindings, nextCalls,
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

    internal static unsafe BindingDto ReadBinding(IPixResourceViewBindings bindings, uint index, GpuCaptureHandle? h, EventRef? ownerEventRef = null)
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
            EventRef? eventRef = null;
            IReadOnlyList<string>? markerPath = null;
            string? reason = null;
            if (h is not null)
            {
                try
                {
                    PIX_EVENT_INFO info = default;
                    _IPixResourceBinding_Extensions.GetEvent(binding, ref info);
                    bool empty = info.GpuId == 0 && info.Index == 0 && info.CommandListId == 0 && string.IsNullOrEmpty(Interop.A(info.Name));
                    eventRef = empty ? null : ResolveBindingEvent(h, info);
                    if (eventRef is not null) markerPath = EventNavigation.MarkerPath(h.AllEvents(eventRef.QueueIndex), eventRef.EventIndex);
                    else reason = empty ? "PIX returned an empty binding event." : "PIX binding event could not be uniquely resolved to a capture queue.";
                }
                catch (Exception ex) { reason = PixErrors.Describe(ex); }
            }
            string? eventSource = eventRef is not null ? "nativeBinding" : null;
            return new BindingDto(index, Json.EnumName(type), eventRef, markerPath, detail, Reason: reason) { EventSource = eventSource };
        }
        catch (Exception ex)
        {
            return new BindingDto(index, null, null, null, null, true, PixErrors.Describe(ex));
        }
    }

    internal static EventRef? ResolveBindingEvent(GpuCaptureHandle h, PIX_EVENT_INFO bindingEvent)
    {
        EventRef? match = null;
        foreach (QueueEntry queue in h.Queues)
        {
            if (bindingEvent.Index >= queue.EventCount) continue;
            PIX_EVENT_INFO candidate = h.EventInfo(queue.Index, bindingEvent.Index);
            if (candidate.GpuId != bindingEvent.GpuId || candidate.CommandListId != bindingEvent.CommandListId ||
                candidate.MomentHandle != bindingEvent.MomentHandle) continue;
            if (match is not null) return null;
            match = new(h.Id, queue.Index, bindingEvent.Index);
        }
        return match;
    }

    [McpServerTool(Name = "pix_gpu_event_resources", ReadOnly = true), Description("Views and resources accessed by a draw/dispatch, including actual DWORD root constants and binding events. Views and bindings have independent offsets. Gathering accessed resources runs once as a shared preparation job.")]
    public static Task<string> EventResources(PixSession session, JobManager jobs, EventRef eventRef,
        uint? viewIndex = null, int bindingOffset = 0, int bindingLimit = Paging.DefaultLimit,
        int viewOffset = 0, int viewLimit = Paging.DefaultLimit,
        [Description(Tools.ReadyWaitDescription)] double waitSeconds = Tools.DefaultReadyWaitSeconds,
        CancellationToken cancellationToken = default)
    {
        ReferenceValidation.Event(session, eventRef);
        ReferenceValidation.Page(viewOffset, viewLimit);
        ReferenceValidation.Page(bindingOffset, bindingLimit);
        return Tools.RunWhenReady(session, jobs, "pix_gpu_event_resources", eventRef.Handle,
            GpuCaptureHandle.AccessedResourcesPreparation(eventRef.Handle),
            h => QueryEventResources(h, eventRef, viewIndex, bindingOffset, bindingLimit, viewOffset, viewLimit),
            waitSeconds, cancellationToken);
    }

    internal static EventResourcesDto QueryEventResources(GpuCaptureHandle h, EventRef eventRef,
        uint? viewIndex = null, int bindingOffset = 0, int bindingLimit = Paging.DefaultLimit,
        int viewOffset = 0, int viewLimit = Paging.DefaultLimit)
    {
        int queueIndex = eventRef.QueueIndex;
        uint eventIndex = eventRef.EventIndex;
        EventRecord record = h.Event(queueIndex, eventIndex);
        PIX_EVENT_INFO info = h.EventInfo(queueIndex, eventIndex);
        IPixProgramState programState = PixApiExtensionsGpuCapture.GetProgramState(h.Document, ref info);
        IPixGenericPipeline pipeline = PixApiExtensionsGpuCaptureResources.GetGpuProgram<IPixGenericPipeline>(programState);
        IPixResourceViewsAtEvent viewsAtEvent = PixApiExtensionsGpuCaptureResources.GetResourceViews(pipeline);
        // PIX's accessed-resource update removes root constants from this collection. Their
        // values are bound state, so detach them before asking PIX to augment resource accesses.
        var rootConstants = new List<RootConstantsDto>();
        var rootConstantCoverage = new List<object>();
        for (uint i = 0; i < viewsAtEvent.GetCount(); i++)
        {
            PIX_RESOURCE_VIEW_TYPE viewType = PIX_RESOURCE_VIEW_TYPE.PIX_RESOURCE_NONE;
            _IPixResourceViews_Extensions.GetType(viewsAtEvent, i, ref viewType);
            if (viewType != PIX_RESOURCE_VIEW_TYPE.PIX_RESOURCE_ROOT_CONSTANT) continue;
            try
            {
                var constants = PixApiExtensionsGpuCaptureResources.GetResourceView<IPixRootConstants>(viewsAtEvent, i);
                D3D12_ROOT_CONSTANTS desc = PixApiExtensionsGpuCaptureResources.GetRootConstantsDesc(constants);
                var values = new List<RootConstantValueDto>();
                for (uint v = 0; v < desc.Num32BitValues; v++)
                {
                    uint value = 0;
                    constants.GetValue(v, ref value);
                    values.Add(new(v, value, $"0x{value:X8}"));
                }
                IPixResourceViewBindings nativeBindings = PixApiExtensionsGpuCaptureResources.GetResourceBindings(constants);
                var bindings = new List<BindingDto>();
                for (uint b = 0; b < nativeBindings.GetCount(); b++) bindings.Add(ReadBinding(nativeBindings, b, h, eventRef));
                rootConstants.Add(new RootConstantsDto(desc.ShaderRegister, desc.RegisterSpace, desc.Num32BitValues, values, bindings));
            }
            catch (Exception ex) { rootConstantCoverage.Add(PixErrors.Unavailable("rootConstants", ex)); }
        }
        if (!h.AccessedResourcesGathered) throw new McpException("Accessed resources have not been prepared.");
        h.GetAnalysis().GetAccessedResources(viewsAtEvent);
        var byResource = new Dictionary<string, (object resource, List<object> views)>();
        var unattached = new List<object>();
        uint count = viewsAtEvent.GetCount();
        ValidateViewIndex(viewIndex, count);
        (int o, int l) = Paging.Normalize(viewOffset, viewLimit);
        uint first = viewIndex ?? (uint)o;
        int returned = 0;
        for (uint i = first; i < count && returned < l; i++)
        {
            if (viewIndex.HasValue && i != viewIndex.Value) break;
            object dto = ViewDto(viewsAtEvent, i, includeResource: true, bindingOffset, bindingLimit, h, eventRef);
            returned++;
            IPixD3D12Resource? res = null;
            try
            {
                var d3dView = PixApiExtensionsGpuCaptureResources.GetResourceView<IPixD3D12ResourceView>(viewsAtEvent, i);
                res = PixApiExtensionsGpuCaptureResources.GetD3D12Resource(d3dView);
            }
            catch { }
            if (res is null) { unattached.Add(dto); continue; }
            string key = Interop.Hex(res.GetApiObjectId());
            if (!byResource.TryGetValue(key, out var entry))
            {
                entry = (ResourceDto(res, includeViews: false, includeDesc: true, h: h), new List<object>());
                byResource[key] = entry;
            }
            entry.views.Add(dto);
        }
        int? next = !viewIndex.HasValue && (long)o + returned < count ? o + returned : null;
        return new EventResourcesDto(eventRef, EventNavigation.MarkerPath(h.AllEvents(queueIndex), eventIndex),
            h.DescribeEvent(queueIndex, record), count, checked((int)first), returned, next,
            byResource.Values.Select(v => new ResourceGroupDto(v.resource, v.views)).ToArray(), unattached)
        {
            RootConstants = rootConstants,
            RootConstantCoverage = rootConstantCoverage,
            NextCalls = next.HasValue ? [new("pix_gpu_event_resources", new { eventRef,
                viewOffset = next.Value, viewLimit, bindingOffset, bindingLimit })] : [],
        };
    }

    [McpServerTool(Name = "pix_gpu_resource_uses", ReadOnly = true), Description("Find observed bindings of a resource, with event references, marker paths, shader stages, registers and binding locations. This is binding/access evidence, not proof of pixel provenance. Missing binding events are reported in coverage.")]
    public static async Task<string> ResourceUses(PixSession session, JobManager jobs, ResourceRef resourceRef,
        int offset = 0, int limit = Paging.DefaultLimit, int? queueIndex = null,
        [Description(Tools.ReadyWaitDescription)] double waitSeconds = Tools.DefaultReadyWaitSeconds,
        [Description(EventScope.Description)] EventRef? scope = null,
        CancellationToken cancellationToken = default)
    {
        ReferenceValidation.Resource(session, resourceRef);
        ReferenceValidation.Page(offset, limit);
        queueIndex = EventScope.ResolveQueue(session, resourceRef.Handle, queueIndex, scope);
        if (queueIndex.HasValue) session.Get<GpuCaptureHandle>(resourceRef.Handle).Queue(queueIndex.Value);
        // Normalize without touching the PIX worker so concurrent callers can immediately
        // join an active index job. The preparation validates resource existence before replay.
        string key = Interop.Hex(Tools.ParseId(resourceRef.ApiObjectId, "apiObjectId"));
        return await Tools.RunWhenReady(session, jobs, "pix_gpu_resource_uses", resourceRef.Handle,
            GpuCaptureHandle.ResourceUsesPreparation(resourceRef.Handle, key), h =>
            {
                FindResource(h, key);
                ResourceUsesSnapshot snapshot = h.ResourceUseFallbackIndex?.For(new(resourceRef.Handle, key)) ?? h.ResourceUseSnapshots[key];
                ResourceUseDto[] rows = snapshot.Items.Where(i => (!queueIndex.HasValue || i.Binding.EventRef?.QueueIndex == queueIndex.Value)
                    && EventScope.Contains(h, i.Binding.EventRef, scope)).ToArray();
                ResourceUseDto[] items = rows.Skip(offset).Take(limit).ToArray();
                int? next = offset + (long)items.Length < rows.Length ? offset + items.Length : null;
                return new ResourceUsesDto(new(resourceRef.Handle, key), snapshot.Evidence, rows.Length, offset, items.Length,
                    next, items, snapshot.Coverage)
                {
                    NextCalls = next.HasValue ? [new("pix_gpu_resource_uses", new { resourceRef,
                        offset = next.Value, limit, queueIndex, scope })] : [],
                };
            }, waitSeconds, cancellationToken).ConfigureAwait(false);
    }

    internal static ResourceUsesDto QueryResourceUses(GpuCaptureHandle h, ResourceRef resourceRef,
        int offset = 0, int limit = Paging.DefaultLimit, int? queueIndex = null)
    {
        (int o, int l) = Paging.Normalize(offset, limit);
        if (queueIndex.HasValue) h.Queue(queueIndex.Value);
        IPixD3D12Resource resource = FindResource(h, resourceRef.ApiObjectId);
        IPixResourceViewsForResource views = PixApiExtensionsGpuCaptureResources.GetD3D12ResourceViews(resource);
        var items = new List<ResourceUseDto>();
        var coverage = new List<object>();
        long total = 0;
        for (uint i = 0; i < views.GetCount(); i++)
        {
            try
            {
                PIX_RESOURCE_VIEW_TYPE viewType = PIX_RESOURCE_VIEW_TYPE.PIX_RESOURCE_NONE;
                _IPixResourceViews_Extensions.GetType(views, i, ref viewType);
                var view = PixApiExtensionsGpuCaptureResources.GetResourceView<IPixResourceView>(views, i);
                IPixResourceViewBindings bindings = PixApiExtensionsGpuCaptureResources.GetResourceBindings(view);
                for (uint b = 0; b < bindings.GetCount(); b++)
                {
                    BindingDto binding = ReadBinding(bindings, b, h);
                    if (binding.Unavailable || binding.EventRef is null)
                        coverage.Add(new { viewIndex = i, bindingIndex = b, unavailable = true, reason = binding.Reason });
                    if (queueIndex.HasValue && binding.EventRef?.QueueIndex != queueIndex.Value) continue;
                    if (total >= o && items.Count < l) items.Add(new(resourceRef, i, Json.EnumName(viewType), binding));
                    total++;
                }
            }
            catch (Exception ex) { coverage.Add(new { viewIndex = i, unavailable = true, reason = PixErrors.Describe(ex) }); }
        }
        int? next = (long)o + items.Count < total ? o + items.Count : null;
        return new(resourceRef, "observedBinding", total, o, items.Count, next, items, coverage)
        {
            NextCalls = next.HasValue ? [new("pix_gpu_resource_uses", new { resourceRef, offset = next.Value, limit, queueIndex })] : [],
        };
    }

    internal static ResourceUsesSnapshot BuildResourceUseSnapshot(GpuCaptureHandle h, ResourceRef resourceRef, Job job)
    {
        if (h.ResourceUseFallbackIndex is not null) return h.ResourceUseFallbackIndex.For(resourceRef);
        job.AddMessage("Reading native resource bindings...");
        var nativeRows = new List<ResourceUseDto>();
        int offset = 0;
        ResourceUsesDto page;
        do
        {
            job.ThrowIfCancellationRequested();
            page = QueryResourceUses(h, resourceRef, offset, Paging.MaxLimit);
            nativeRows.AddRange(page.Items);
            if (page.Coverage.Count > 0 || page.Items.Any(i => i.Binding.EventRef is null)) break;
            offset = page.NextOffset ?? 0;
        } while (page.NextOffset.HasValue);
        if (page.Coverage.Count == 0 && nativeRows.All(i => i.Binding.EventRef is not null))
            return new(nativeRows, [], "nativeBinding");

        // Some PIX Preview builds return an empty PIX_EVENT_INFO for every native binding.
        // Per-event collections still provide an exact event context; build that index once
        // in a preparation job instead of fabricating identities from the zero-filled struct.
        job.AddMessage("Native binding events unavailable; indexing per-event resource collections and captured API arguments...");
        var rows = new List<ResourceUseDto>();
        var coverage = new List<object>
        {
            new { feature = "nativeBindingEvents", state = "unavailable", resourceRef, nativeBindingCount = page.Total,
                reason = "PIX returned incomplete binding events. Navigation uses per-event draw/dispatch collections and explicit API object arguments; resource-view-only API arguments may be absent." },
        };
        foreach (QueueEntry queue in h.Queues)
        {
            EventRecord[] events = h.AllEvents(queue.Index);
            foreach (EventRecord record in events)
            {
                job.ThrowIfCancellationRequested();
                var eventRef = new EventRef(h.Id, queue.Index, record.Index);
                uint argumentIndex = 0;
                foreach (var (parameter, objectId) in ResourceArguments(record.ApiCallData))
                    rows.Add(new(new(h.Id, Interop.Hex(objectId)), null, "API_ARGUMENT",
                        new BindingDto(argumentIndex++, "API_PARAMETER", eventRef,
                            EventNavigation.MarkerPath(events, record.Index), new { parameter, apiCall = record.Name })
                        { EventSource = "capturedApiArgument" }) { ViewIndexScope = "none" });
                if (!Tools.MatchesKind(record, "drawOrDispatch")) continue;
                try
                {
                    PIX_EVENT_INFO info = h.EventInfo(queue.Index, record.Index);
                    IPixProgramState state = PixApiExtensionsGpuCapture.GetProgramState(h.Document, ref info);
                    IPixGenericPipeline pipeline = PixApiExtensionsGpuCaptureResources.GetGpuProgram<IPixGenericPipeline>(state);
                    IPixResourceViewsAtEvent views = PixApiExtensionsGpuCaptureResources.GetResourceViews(pipeline);
                    h.GetAnalysis().GetAccessedResources(views);
                    for (uint i = 0; i < views.GetCount(); i++)
                    {
                        IPixD3D12Resource? resource;
                        try
                        {
                            IPixD3D12ResourceView view = PixApiExtensionsGpuCaptureResources.GetResourceView<IPixD3D12ResourceView>(views, i);
                            resource = PixApiExtensionsGpuCaptureResources.GetD3D12Resource(view);
                        }
                        catch { continue; } // Samplers and root constants have no backing resource.
                        if (resource is null) continue;
                        var observedResource = new ResourceRef(h.Id, Interop.Hex(resource.GetApiObjectId()));
                        PIX_RESOURCE_VIEW_TYPE type = PIX_RESOURCE_VIEW_TYPE.PIX_RESOURCE_NONE;
                        _IPixResourceViews_Extensions.GetType(views, i, ref type);
                        IPixResourceView resourceView = PixApiExtensionsGpuCaptureResources.GetResourceView<IPixResourceView>(views, i);
                        IPixResourceViewBindings bindings = PixApiExtensionsGpuCaptureResources.GetResourceBindings(resourceView);
                        var nativeBindings = new List<BindingDto>();
                        for (uint b = 0; b < bindings.GetCount(); b++) nativeBindings.Add(ReadBinding(bindings, b, h));
                        // The event-scoped collection proves the view is present here. Keep
                        // native binding records separate; their empty events are not repaired
                        // by guessing that every global binding belongs to this event.
                        rows.Add(new(observedResource, i, Json.EnumName(type),
                            new BindingDto(0, "VIEW_OBSERVED", eventRef, EventNavigation.MarkerPath(events, record.Index),
                                new { nativeBindings }) { EventSource = "eventScopedView" }) { ViewIndexScope = "event" });
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { coverage.Add(new { eventRef, unavailable = true, reason = PixErrors.Describe(ex) }); }
            }
        }
        job.ThrowIfCancellationRequested();
        h.ResourceUseFallbackIndex = new ResourceUseIndex(rows, coverage);
        return h.ResourceUseFallbackIndex.For(resourceRef);
    }

    private static readonly Regex ObjectArgument = new("<(?<parameter>[A-Za-z_][A-Za-z0-9_]*)>obj#(?<id>[0-9]+)</\\k<parameter>>",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    internal static IReadOnlyList<string> ResourceArgumentNames(string apiCallData, ulong resourceId)
        => ResourceArguments(apiCallData).Where(a => a.ObjectId == resourceId).Select(a => a.Parameter).ToArray();

    internal static IEnumerable<(string Parameter, ulong ObjectId)> ResourceArguments(string apiCallData)
    {
        foreach (Match match in ObjectArgument.Matches(apiCallData))
            if (ulong.TryParse(match.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id))
                yield return (match.Groups["parameter"].Value, id);
    }
}
