using PixMcp.Pix;
using PixMcp.Tools;
using Xunit;

namespace PixMcp.Tests;

/// <summary>Resource size estimates, access classification, barrier parsing and resource timelines (PIX-free).</summary>
public sealed class ResourceAnalysisTests
{
    private const string FixtureBarrier = "<ResourceBarrier><this>ID3D12GraphicsCommandList obj#11</this><NumBarriers>1</NumBarriers><pBarriers><Type>D3D12_RESOURCE_BARRIER_TYPE_TRANSITION</Type>" +
        "<Flags>D3D12_RESOURCE_BARRIER_FLAG_NONE</Flags><Transition><pResource>obj#13</pResource><Subresource>4294967295</Subresource><StateBefore>D3D12_RESOURCE_STATE_COMMON</StateBefore>" +
        "<StateAfter>D3D12_RESOURCE_STATE_RENDER_TARGET</StateAfter></Transition></pBarriers></ResourceBarrier>";

    [Theory]
    [InlineData("TEXTURE2D", 640UL, 480U, (ushort)1, (ushort)1, "R8G8B8A8_UNORM", 1U, 1_228_800UL, "dimsMipsArraySamples")]
    [InlineData("TEXTURE2D", 640UL, 480U, (ushort)1, (ushort)1, "DXGI_FORMAT_R8G8B8A8_UNORM", 4U, 4_915_200UL, "dimsMipsArraySamples")]
    [InlineData("TEXTURE2D", 256UL, 256U, (ushort)1, (ushort)9, "BC7_UNORM", 1U, 87_408UL, "blockCompressed")]
    [InlineData("TEXTURE3D", 4UL, 4U, (ushort)4, (ushort)3, "R8_UNORM", 1U, 73UL, "dimsMipsArraySamples")]
    [InlineData("TEXTURE2D", 4UL, 4U, (ushort)6, (ushort)1, "R16G16B16A16_FLOAT", 1U, 768UL, "dimsMipsArraySamples")]
    [InlineData("TEXTURE2D", 8UL, 8U, (ushort)1, (ushort)0, "R8_UNORM", 1U, 85UL, "dimsMipsArraySamples")]
    [InlineData("BUFFER", 108UL, 1U, (ushort)1, (ushort)1, "UNKNOWN", 1U, 108UL, "bufferWidth")]
    public void SizeEstimatesCoverMipsArraysSamplesAndBlocks(string dimension, ulong width, uint height, ushort depthOrArraySize, ushort mips, string format, uint samples,
        ulong bytes, string method)
        => Assert.Equal(new ResourceSizeEstimate(bytes, method), FormatInfo.EstimateBytes(dimension, width, height, depthOrArraySize, mips, format, samples));

    [Fact]
    public void UnsizedFormatsHaveNoEstimateAndEveryDxgiFormatIsKnown()
    {
        Assert.Equal(new ResourceSizeEstimate(null, "unknownFormat"), FormatInfo.EstimateBytes("TEXTURE2D", 64, 64, 1, 1, "NV12", 1));
        // QueueEntry.Info is a PIX interface, so its assembly is the PIX managed API that defines D3D12_RESOURCE_DESC2.Format.
        System.Reflection.Assembly pix = typeof(PixMcp.Pix.Handles.QueueEntry).GetProperty("Info")!.PropertyType.Assembly;
        Type[] types;
        try { types = pix.GetTypes(); }
        catch (System.Reflection.ReflectionTypeLoadException ex) { types = ex.Types.OfType<Type>().ToArray(); }
        Type desc = types.Single(t => t.Name == "D3D12_RESOURCE_DESC2");
        Type formatType = desc.GetField("Format")?.FieldType ?? desc.GetProperty("Format")!.PropertyType;
        Assert.True(formatType.IsEnum);
        string[] missing = Enum.GetNames(formatType).Where(n => !FormatInfo.Known(n)).ToArray();
        Assert.True(missing.Length == 0, "Formats without an entry: " + string.Join(", ", missing));
    }

    [Theory]
    [InlineData("RENDER_TARGET_VIEW", "write")]
    [InlineData("DEPTH_STENCIL_VIEW", "write")]
    [InlineData("UNORDERED_ACCESS_VIEW", "readWrite")]
    [InlineData("SHADER_RESOURCE_VIEW", "read")]
    [InlineData("CONSTANT_BUFFER_VIEW", "read")]
    [InlineData("VERTEX_BUFFER_VIEW", "read")]
    [InlineData("INDEX_BUFFER_VIEW", "read")]
    [InlineData("SAMPLER", "unknown")]
    [InlineData("API_ARGUMENT", "unknown")]
    public void ViewTypesMapToAccess(string viewType, string access) => Assert.Equal(access, ResourceAccess.FromViewType(viewType));

    [Theory]
    [InlineData("pDstResource", "CopyResource", "copyDst")]
    [InlineData("pSrcResource", "CopyResource", "copySrc")]
    [InlineData("pDst/pResource", "CopyTextureRegion", "copyDst")]
    [InlineData("pSrc/pResource", "CopyTextureRegion", "copySrc")]
    [InlineData("pArgumentBuffer", "ExecuteIndirect", "read")]
    [InlineData("pBarriers/Transition/pResource", "ResourceBarrier", "barrier")]
    [InlineData("pBackBuffer", "Present", "read")]
    [InlineData("pBuffer", "SetComputeRootUnorderedAccessView", "readWrite")]
    [InlineData("pResource", "ClearUnorderedAccessViewFloat", "write")]
    [InlineData("pBuffer", "SetGraphicsRootShaderResourceView", "read")]
    [InlineData("pBuffer", "IASetVertexBuffers", "read")]
    [InlineData("pHeap", "CreatePlacedResource", "unknown")]
    public void ArgumentsMapToAccess(string path, string api, string access) => Assert.Equal(access, ResourceAccess.FromArgument(path, api));

    [Fact]
    public void ArgumentPathsFollowNestedElements()
    {
        Assert.Equal(new[] { ("pBarriers/Transition/pResource", 13UL) }, ResourceAccess.ArgumentPaths(FixtureBarrier));
        const string copy = "<CopyTextureRegion><pDst><pResource>obj#5</pResource><SubresourceIndex>0</SubresourceIndex></pDst><DstX>0</DstX><pSrc><pResource>obj#7</pResource></pSrc></CopyTextureRegion>";
        Assert.Equal(new[] { ("pDst/pResource", 5UL), ("pSrc/pResource", 7UL) }, ResourceAccess.ArgumentPaths(copy));
        Assert.Empty(ResourceAccess.ArgumentPaths("DrawInstanced(3, 1, 0, 0)"));
        Assert.Empty(ResourceAccess.ArgumentPaths(null));
    }

    [Fact]
    public void BarriersParseTransitionsUavAndAliasing()
    {
        BarrierParse fixture = ResourceAccess.ParseBarriers(FixtureBarrier);
        Assert.Equal("full", fixture.State);
        Assert.Equal(new ParsedBarrier("TRANSITION", 13, "COMMON", "RENDER_TARGET", 4294967295), Assert.Single(fixture.Barriers));
        const string mixed = "<ResourceBarrier><NumBarriers>3</NumBarriers><pBarriers><Transition><pResource>obj#1</pResource><StateBefore>D3D12_RESOURCE_STATE_RENDER_TARGET</StateBefore>" +
            "<StateAfter>D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE</StateAfter></Transition></pBarriers><pBarriers><UAV><pResource>obj#2</pResource></UAV></pBarriers>" +
            "<pBarriers><Aliasing><pOutputResource>obj#3</pOutputResource><pInputResource>obj#4</pInputResource></Aliasing></pBarriers></ResourceBarrier>";
        BarrierParse parsed = ResourceAccess.ParseBarriers(mixed);
        Assert.Equal("full", parsed.State);
        Assert.Equal(new[] { "TRANSITION", "UAV", "ALIASING", "ALIASING" }, parsed.Barriers.Select(b => b.Type));
        Assert.Equal(new ulong[] { 1, 2, 3, 4 }, parsed.Barriers.Select(b => b.ResourceId));
        Assert.Null(parsed.Barriers[0].Subresource);
        Assert.Equal("partial", ResourceAccess.ParseBarriers("<Barrier><Textures><pResource>obj#9</pResource></Textures><Transition><pResource>obj#8</pResource></Transition></Barrier>").State);
        Assert.Equal("none", ResourceAccess.ParseBarriers("<Barrier><Textures><pResource>obj#9</pResource></Textures></Barrier>").State);
        Assert.Equal("none", ResourceAccess.ParseBarriers("ResourceBarrier(1, 0x1234)").State);
        Assert.Equal("none", ResourceAccess.ParseBarriers("").State);
    }

    private static TimelineUse U(uint index, string access, string viewType = "RENDER_TARGET_VIEW", string markerPath = "Frame/Pass", int queue = 0, string? before = null,
        string? after = null, uint? gpuId = null)
        => new(queue, index, gpuId ?? index, $"E{index}", markerPath, access, viewType, "eventScopedView", null, before, after);

    private static ResourceTimelineResult Build(IReadOnlyList<TimelineUse> uses, bool presentAfter = false, ulong? bytes = 100, Func<int, uint, ulong?>? eop = null)
        => ResourceTimeline.Build(new TimelineInputs("gpu-1", "Target", bytes, uses,
            (q, i) => uses.First(u => u.QueueIndex == q && u.EventIndex == i).GpuId is uint gpu ? gpu : ulong.MaxValue, (_, _) => presentAfter, eop ?? ((_, _) => null)));

    [Fact]
    public void PhasesSummaryAndTrafficFollowCaptureOrder()
    {
        TimelineUse[] uses =
        [
            U(22, "barrier", "API_ARGUMENT", before: "RENDER_TARGET", after: "COMMON"), U(19, "write"), U(10, "barrier", "API_ARGUMENT", "Frame", before: "COMMON", after: "RENDER_TARGET"),
            U(20, "write"), U(21, "write"),
        ];
        ResourceTimelineResult result = Build(uses, presentAfter: true, eop: (_, i) => i is 19 or 20 ? 1000UL : null);
        Assert.Equal(new uint[] { 10, 19, 20, 21, 22 }, result.Rows.Select(r => r.EventIndex));
        Assert.Equal(new[] { "barrier", "write", "barrier" }, result.Phases.Select(p => p.Access));
        Assert.Equal((3, (ulong?)2000), (result.Phases[1].Events, result.Phases[1].EopNs));
        Assert.Equal((0, 3, 2, (ulong?)300), (result.Summary.Reads, result.Summary.Writes, result.Summary.Barriers, result.Summary.EstimatedTrafficBytes));
        Assert.Equal(19U, result.Summary.FirstWrite!.EventIndex);
        Assert.Empty(result.Insights);
        Assert.Equal("written_never_read", Assert.Single(Build(uses, presentAfter: false).Insights).Id);
    }

    [Fact]
    public void InsightsFlagReadBeforeWriteMissingBarriersAndFeedbackLoops()
    {
        Assert.Equal("read_before_write", Assert.Single(Build([U(5, "read", "SHADER_RESOURCE_VIEW", "Frame/A"), U(8, "write", markerPath: "Frame/B")]).Insights).Id);

        Assert.Equal("missing_barrier_between_write_and_read", Assert.Single(Build(
            [U(1, "barrier", "API_ARGUMENT", "Frame", before: "COMMON", after: "RENDER_TARGET"), U(3, "write", markerPath: "Frame/B"), U(6, "read", "SHADER_RESOURCE_VIEW", "Frame/C")]).Insights).Id);
        Assert.Empty(Build([U(1, "barrier", "API_ARGUMENT", "Frame", after: "RENDER_TARGET"), U(3, "write", markerPath: "Frame/B"),
            U(4, "barrier", "API_ARGUMENT", "Frame", after: "PIXEL_SHADER_RESOURCE"), U(6, "read", "SHADER_RESOURCE_VIEW", "Frame/C")]).Insights);

        InsightDto loop = Assert.Single(Build([U(3, "write", markerPath: "Frame/Pass"), U(4, "read", "SHADER_RESOURCE_VIEW", "Frame/Pass")]).Insights);
        Assert.Equal(("bound_as_rtv_and_srv_same_pass", "warning"), (loop.Id, loop.Severity));
    }

    [Fact]
    public void RowsFromSeveralQueuesInterleaveByGpuId()
    {
        ResourceTimelineResult result = Build([U(4, "read", "SHADER_RESOURCE_VIEW", queue: 0, gpuId: 30), U(2, "readWrite", "UNORDERED_ACCESS_VIEW", queue: 1, gpuId: 20),
            U(9, "read", "SHADER_RESOURCE_VIEW", queue: 0, gpuId: 10)]);
        Assert.Equal(new[] { (0, 9U), (1, 2U), (0, 4U) }, result.Rows.Select(r => (r.QueueIndex, r.EventIndex)));
        Assert.Equal(new[] { 1, 2, 3 }, result.Rows.Select(r => r.Ordinal));
        Assert.Equal(2, result.Phases.Count);
    }
}
