using System.Text.Json;
using Microsoft.PIX;
using PixMcp.Pix;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Xunit;

namespace PixMcp.Tests;

public class FixedArrayTests
{
    [Fact]
    public void PreservesEveryClearColorAndSamplerBorderComponent()
    {
        float[] expected = { -0.5f, 0f, 0.25f, 1f };
        var clear = new D3D12_CLEAR_VALUE { Format = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM };
        expected.AsSpan().CopyTo(clear.Anonymous.Color.AsSpan());
        var sampler = new D3D12_SAMPLER_DESC();
        expected.AsSpan().CopyTo(sampler.BorderColor.AsSpan());

        JsonElement clearJson = Serialize(clear);
        JsonElement samplerJson = Serialize(sampler);

        Assert.Equal(expected, clearJson.GetProperty("anonymous").GetProperty("color").EnumerateArray().Select(v => v.GetSingle()));
        Assert.Equal(expected, samplerJson.GetProperty("borderColor").EnumerateArray().Select(v => v.GetSingle()));
    }

    [Fact]
    public void PreservesUnsignedSamplerBorderComponents()
    {
        uint[] expected = { 0u, 1u, 0x80000000u, uint.MaxValue };
        var sampler = new D3D12_SAMPLER_DESC2();
        expected.AsSpan().CopyTo(sampler.Anonymous.UintBorderColor.AsSpan());

        JsonElement json = Serialize(sampler);

        Assert.Equal(expected, json.GetProperty("anonymous").GetProperty("uintBorderColor").EnumerateArray().Select(v => v.GetUInt32()));
    }

    [Fact]
    public void PreservesEveryVersionPart()
    {
        ushort[] expected = { 1, 2, 3, ushort.MaxValue };
        var version = new D3D12_VERSION_NUMBER();
        expected.AsSpan().CopyTo(version.VersionParts.AsSpan());

        JsonElement json = Serialize(version);

        Assert.Equal(expected, json.GetProperty("versionParts").EnumerateArray().Select(v => v.GetUInt16()));
    }

    [Fact]
    public void TrimsAdapterDescriptionAtFirstNull()
    {
        var adapter = new DXGI_ADAPTER_DESC3();
        "Test GPU\0ignored".AsSpan().CopyTo(adapter.Description.AsSpan());

        JsonElement json = Serialize(adapter);

        Assert.Equal("Test GPU", json.GetProperty("description").GetString());
    }

    [Fact]
    public void ConvertsEveryRenderTargetFormatToItsEnumName()
    {
        var formats = new D3D12_RT_FORMAT_ARRAY();
        formats.RTFormats.AsSpan()[0] = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM;
        formats.RTFormats.AsSpan()[7] = DXGI_FORMAT.DXGI_FORMAT_R32_FLOAT;

        JsonElement values = Serialize(formats).GetProperty("rtFormats");

        Assert.Equal(8, values.GetArrayLength());
        Assert.Equal("R8G8B8A8_UNORM", values[0].GetString());
        Assert.Equal("R32_FLOAT", values[7].GetString());
    }

    [Fact]
    public void ConvertsEveryBlendDescriptorAtItsActualDepth()
    {
        var blend = new D3D12_BLEND_DESC();
        blend.RenderTarget.AsSpan()[0].SrcBlend = D3D12_BLEND.D3D12_BLEND_SRC_ALPHA;
        blend.RenderTarget.AsSpan()[7].BlendEnable = true;
        blend.RenderTarget.AsSpan()[7].RenderTargetWriteMask = 15;

        JsonElement values = Serialize(blend).GetProperty("renderTarget");

        Assert.Equal(8, values.GetArrayLength());
        Assert.Equal("SRC_ALPHA", values[0].GetProperty("srcBlend").GetString());
        Assert.True(values[7].GetProperty("blendEnable").GetBoolean());
        Assert.Equal(15, values[7].GetProperty("renderTargetWriteMask").GetInt32());
    }

    private static JsonElement Serialize(object value)
        => JsonSerializer.Deserialize<JsonElement>(Json.Serialize(Reflect.ToObject(value)));
}
