using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.PIX;
using PixMcp.Pix;
using PixMcp.Pix.Handles;
using ToolHelpers = PixMcp.Tools.Tools;
using Windows.Win32.Graphics.Dxgi.Common;
using Xunit;

namespace PixMcp.Tests;

public class DiscoveryTests
{
    [Theory]
    [InlineData("2606.18-preview", new[] { 2606, 18 })]
    [InlineData("2602.24.004-main", new[] { 2602, 24, 4 })]
    [InlineData("2606.9", new[] { 2606, 9 })]
    public void ParsesVersionFolders(string leaf, int[] expected)
    {
        Assert.Equal(expected, PixDiscovery.ParseVersion(leaf));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("2606")]
    [InlineData("26a6.18")]
    public void RejectsUnparseableFolders(string leaf)
    {
        Assert.Null(PixDiscovery.ParseVersion(leaf));
    }

    [Fact]
    public void ComparesNumericallyNotLexically()
    {
        Assert.True(PixDiscovery.CompareVersions(new[] { 2606, 18 }, new[] { 2606, 9 }) > 0);
        Assert.True(PixDiscovery.CompareVersions(new[] { 2606, 18 }, new[] { 2606, 18, 1 }) < 0);
        Assert.Equal(0, PixDiscovery.CompareVersions(new[] { 2606, 18, 0 }, new[] { 2606, 18 }));
    }

    [Fact]
    public void FindsInstallOnThisMachineOrReportsWhy()
    {
        string? dir = PixDiscovery.Find(out string? source, out string? error);
        if (dir is null)
        {
            Assert.False(string.IsNullOrEmpty(error));
        }
        else
        {
            Assert.True(File.Exists(Path.Combine(dir, PixDiscovery.MarkerDll)));
            Assert.False(string.IsNullOrEmpty(source));
        }
    }
}

public class JsonTests
{
    [Fact]
    public void TrimsEnumPrefixes()
    {
        Assert.Equal("GRAPHICS", Json.EnumName(PIX_QUEUE_TYPE.PIX_QUEUE_TYPE_GRAPHICS));
        Assert.Equal("CONSTANT_BUFFER_VIEW", Json.EnumName(PIX_RESOURCE_VIEW_TYPE.PIX_RESOURCE_CONSTANT_BUFFER_VIEW));
        Assert.Equal("R8G8B8A8_UNORM", Json.EnumName(DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM));
    }

    [Fact]
    public void SerializesEnumsAsTrimmedStringsAndOmitsNulls()
    {
        string json = Json.Serialize(new { type = PIX_QUEUE_TYPE.PIX_QUEUE_TYPE_COMPUTE, missing = (string?)null, value = 3 });
        Assert.Equal("{\"type\":\"COMPUTE\",\"value\":3}", json);
    }

    [Fact]
    public void PagingReportsNextOffset()
    {
        var page = Paging.Page(new[] { 1, 2, 3 }, total: 10, offset: 0, limit: 3);
        JsonElement e = JsonSerializer.Deserialize<JsonElement>(Json.Serialize(page));
        Assert.Equal(3, e.GetProperty("nextOffset").GetInt32());
        Assert.Equal(10, e.GetProperty("total").GetInt32());

        var last = Paging.Page(new[] { 9 }, total: 10, offset: 9, limit: 3);
        JsonElement l = JsonSerializer.Deserialize<JsonElement>(Json.Serialize(last));
        Assert.False(l.TryGetProperty("nextOffset", out _));
    }

    [Fact]
    public void NormalizesPagingArguments()
    {
        Assert.Equal((0, Paging.DefaultLimit), Paging.Normalize(null, null));
        Assert.Equal((0, 1), Paging.Normalize(-5, 0));
        Assert.Equal((7, Paging.MaxLimit), Paging.Normalize(7, 99999));
    }
}

public class EventFilterTests
{
    private static readonly EventRecord[] Events =
    {
        new(0, uint.MaxValue, uint.MaxValue, "Frame 1", "", 0, 0),
        new(1, 10, 0, "DrawIndexedInstanced", "DrawIndexedInstanced(36, 1, 0, 0, 0)", 1, 0),
        new(2, 11, 0, "Dispatch", "Dispatch(8, 8, 1)", 1, 0),
        new(3, 12, 0, "CopyResource", "", 1, 0),
        new(4, uint.MaxValue, 0, "Shadow pass", "", 1, 0xFF00FF00),
    };

    [Fact]
    public void FiltersByKindAndName()
    {
        var draws = ToolHelpers.FilterEvents(Events, null, null, null, "draw", null, null, null).ToArray();
        Assert.Single(draws);
        Assert.Equal(1u, draws[0].Index);

        var both = ToolHelpers.FilterEvents(Events, null, null, null, "drawOrDispatch", null, null, null).ToArray();
        Assert.Equal(2, both.Length);

        var byName = ToolHelpers.FilterEvents(Events, "shadow", null, null, null, null, null, null).ToArray();
        Assert.Single(byName);
        Assert.Equal(4u, byName[0].Index);
    }

    [Fact]
    public void FiltersByGpuIdRangeSkippingCpuOnlyEvents()
    {
        var range = ToolHelpers.FilterEvents(Events, null, null, null, null, null, 11, 12).ToArray();
        Assert.Equal(new uint[] { 2, 3 }, range.Select(e => e.Index).ToArray());
    }

    [Fact]
    public void UnknownKindIsAnError()
    {
        Assert.Throws<ModelContextProtocol.McpException>(() => ToolHelpers.FilterEvents(Events, null, null, null, "bogus", null, null, null).ToArray());
    }

    [Fact]
    public void EventDtoMapsSentinels()
    {
        JsonElement e = JsonSerializer.Deserialize<JsonElement>(Json.Serialize(Events[0].ToDto(0)));
        Assert.False(e.TryGetProperty("gpuId", out _));
        Assert.False(e.TryGetProperty("parentIndex", out _));
        JsonElement d = JsonSerializer.Deserialize<JsonElement>(Json.Serialize(Events[4].ToDto(0)));
        Assert.Equal("0xFF00FF00", d.GetProperty("color").GetString());
    }
}

public class ToolHelperTests
{
    [Fact]
    public void ParsesHexAndDecimalIds()
    {
        Assert.Equal(0x1A2Bul, ToolHelpers.ParseId("0x1A2B", "id"));
        Assert.Equal(42ul, ToolHelpers.ParseId("42", "id"));
        Assert.Throws<ModelContextProtocol.McpException>(() => ToolHelpers.ParseId("zz", "id"));
    }

    [Fact]
    public void ParsesEnumsBySuffix()
    {
        Assert.Equal(PIX_ANALYSIS_FLAGS.PIX_ANALYSIS_ENABLE_DEBUG_LAYER, ToolHelpers.ParseEnum<PIX_ANALYSIS_FLAGS>("ENABLE_DEBUG_LAYER"));
        Assert.Equal(PIX_QUEUE_TYPE.PIX_QUEUE_TYPE_COPY, ToolHelpers.ParseEnum<PIX_QUEUE_TYPE>("copy"));
    }
}

public class PngTests
{
    [Fact]
    public void EncodesBgraAsValidPng()
    {
        const int w = 3, h = 2, pitch = 16;
        var pixels = new byte[h * pitch];
        // pixel (0,0) = red in BGRA
        pixels[0] = 0; pixels[1] = 0; pixels[2] = 255; pixels[3] = 255;
        byte[] png = Png.Encode(pixels, w, h, pitch, DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM);

        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(png, 12, 4));
        Assert.Equal(w, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)));
        Assert.Equal(h, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
        Assert.Equal("IEND", System.Text.Encoding.ASCII.GetString(png, png.Length - 8, 4));
    }

    [Fact]
    public void ReportsUnsupportedFormats()
    {
        Assert.True(Png.IsSupported(DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM_SRGB));
        Assert.False(Png.IsSupported(DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT));
    }
}

public class ReflectTests
{
    private struct Sample
    {
        public int A;
        public PIX_QUEUE_TYPE Type;
        public Guid Id;
    }

    [Fact]
    public void ConvertsStructsToDictionaries()
    {
        var value = new Sample { A = 7, Type = PIX_QUEUE_TYPE.PIX_QUEUE_TYPE_COPY, Id = Guid.Empty };
        var dict = Assert.IsType<Dictionary<string, object?>>(Reflect.ToObject(value));
        Assert.Equal(7, dict["a"]);
        Assert.Equal("COPY", dict["type"]);
        Assert.Equal(Guid.Empty.ToString(), dict["id"]);
    }
}
