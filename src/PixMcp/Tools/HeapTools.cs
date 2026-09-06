using System.ComponentModel;
using Microsoft.PIX;
using Microsoft.PIX.Extension;
using Microsoft.PIX.Extension.GpuCapture;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using PixMcp.Pix.Handles;

namespace PixMcp.Tools;

[McpServerToolType]
public static class HeapTools
{
    [McpServerTool(Name = "pix_gpu_heap", ReadOnly = true), Description("Details of one D3D12 heap in a GPU capture: heap description (type, CPU page property, memory pool, size, alignment, flags) and the placed resources it hosts, paged. Identify the heap by the index or apiObjectId from pix_gpu_api_objects(type: HEAP), or by exact name. No GPU analysis needed. Placed resources of pix_gpu_resource report heapId; pass it here as apiObjectId.")]
    public static Task<string> Heap(
        PixSession session,
        [Description("GPU capture handle")] string handle,
        [Description("Heap apiObjectId (hex like 0x1A2B or decimal), e.g. the heapId of a placed resource.")] string? apiObjectId = null,
        [Description("API object index from pix_gpu_api_objects.")] uint? index = null,
        [Description("Exact heap name.")] string? name = null,
        [Description("First placed resource (default 0).")] int offset = 0,
        [Description("Maximum placed resources (default 50, max 1000).")] int limit = 50,
        CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_gpu_heap", () =>
        {
            GpuCaptureHandle h = session.Get<GpuCaptureHandle>(handle);
            (int o, int l) = Paging.Normalize(offset, limit);
            IPixD3D12ApiObjectCollection objects = PixApiExtensionsGpuCapture.GetD3D12ApiObjects(h.Document);
            (ulong objectIndex, IPixD3D12Heap heap) = FindHeap(objects, apiObjectId, index, name);

            D3D12_HEAP_DESC desc = PixApiExtensionsGpuCapture.GetHeapDesc(heap);
            uint placedCount = heap.GetPlacedResourceCount();
            PageResult<object> placed = Paging.Collect(Enumerable.Range(0, (int)Math.Min(placedCount, int.MaxValue)), o, l, i =>
                Tools.Try(() => ResourceTools.ResourceDto(PixApiExtensionsGpuCapture.GetPlacedResource(heap, (uint)i), includeViews: false, includeDesc: true), $"placedResource[{i}]")!);
            return new
            {
                index = objectIndex,
                apiObjectId = Interop.Hex(heap.GetApiObjectId()),
                name = Interop.WOrNull(heap.GetName()),
                desc = Reflect.ToObject(desc),
                placedResourceCount = placedCount,
                placedResources = placed,
            };
        }, cancellationToken);

    private static (ulong Index, IPixD3D12Heap Heap) FindHeap(IPixD3D12ApiObjectCollection objects, string? apiObjectId, uint? index, string? name)
    {
        ulong count = objects.GetCount();
        if (index.HasValue)
        {
            if (index.Value >= count)
            {
                throw new McpException($"API object index {index} is out of range; the capture has {count} API object(s).");
            }
            PIX_API_OBJECT_TYPE type = PixApiExtensionsGpuCapture.GetApiObjectType(objects, index.Value);
            if (type != PIX_API_OBJECT_TYPE.PIX_API_OBJECT_TYPE_HEAP)
            {
                throw new McpException($"API object {index} is a {Json.EnumName(type)}, not a heap. List heaps with pix_gpu_api_objects(type: \"HEAP\").");
            }
            return (index.Value, PixApiExtensions.TryGet<IPixD3D12Heap>(objects, index.Value, out Exception ex) ?? throw new McpException($"Heap {index} could not be read: {PixErrors.Describe(ex)}"));
        }

        ulong? wantedId = string.IsNullOrEmpty(apiObjectId) ? null : Tools.ParseId(apiObjectId, "apiObjectId");
        if (wantedId is null && string.IsNullOrEmpty(name))
        {
            throw new McpException("Specify apiObjectId, index or name.");
        }
        for (ulong i = 0; i < count; i++)
        {
            if (PixApiExtensionsGpuCapture.GetApiObjectType(objects, i) != PIX_API_OBJECT_TYPE.PIX_API_OBJECT_TYPE_HEAP)
            {
                continue;
            }
            IPixD3D12Heap? heap = PixApiExtensions.TryGet<IPixD3D12Heap>(objects, i, out _);
            if (heap is null)
            {
                continue;
            }
            if (wantedId.HasValue ? heap.GetApiObjectId() == wantedId.Value : Interop.W(heap.GetName()) == name)
            {
                return (i, heap);
            }
        }
        throw new McpException(wantedId.HasValue ? $"No heap with apiObjectId {apiObjectId} was found." : $"No heap named '{name}' was found.");
    }
}
