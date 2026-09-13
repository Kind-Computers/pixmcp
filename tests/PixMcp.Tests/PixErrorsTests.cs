using System.Runtime.InteropServices;
using System.Text.Json;
using PixMcp.Pix;
using PixMcp.Tools;
using Xunit;

namespace PixMcp.Tests;

public sealed class PixErrorsTests
{
    [Fact]
    public void ProjectedNotImplementedPreservesItsNativeUnsupportedEvidence()
    {
        const int notImplemented = unchecked((int)0x80004001);
        Exception exception = Assert.IsType<NotImplementedException>(Marshal.GetExceptionForHR(notImplemented));
        Assert.Equal(notImplemented, PixErrors.HResultOf(exception));
        ErrorDto detail = PixErrors.ToDto(exception);
        Assert.Equal("unsupported_feature", detail.Code);
        Assert.Equal("0x80004001", detail.Hresult);
        Assert.True(CountersTools.ExplicitlyUnsupported(exception));

        JsonElement unavailable = JsonSerializer.SerializeToElement(PixErrors.Unavailable("occupancy", exception), Json.Options);
        Assert.True(unavailable.GetProperty("unavailable").GetBoolean());
        Assert.Equal("unsupported", unavailable.GetProperty("state").GetString());
        Assert.Equal("0x80004001", unavailable.GetProperty("error").GetProperty("hresult").GetString());
    }

    [Fact]
    public void UnsupportedProjectionSurvivesMcpWrapping()
    {
        var wrapped = Assert.IsType<PixToolException>(PixErrors.ToMcp(new NotImplementedException(), "high-frequency counters"));
        Assert.Equal("unsupported_feature", wrapped.Detail.Code);
        Assert.Equal("0x80004001", wrapped.Detail.Hresult);
        Assert.Contains("high-frequency counters", wrapped.Detail.Message);
    }

    [Theory]
    [InlineData(unchecked((int)0x80004005))]
    [InlineData(unchecked((int)0x887A0005))]
    [InlineData(unchecked((int)0x8ABC0023))]
    public void OtherNativeFailuresDoNotBecomeUnsupportedCapabilities(int hresult)
    {
        var exception = new COMException("Native operation failed", hresult);
        Assert.False(CountersTools.ExplicitlyUnsupported(exception));
        Assert.NotEqual("unsupported_feature", PixErrors.ToDto(exception).Code);
        JsonElement unavailable = JsonSerializer.SerializeToElement(PixErrors.Unavailable("shaderProfiling", exception), Json.Options);
        Assert.Equal("unknown", unavailable.GetProperty("state").GetString());
        Assert.Equal(PixErrors.Hex(hresult), unavailable.GetProperty("error").GetProperty("hresult").GetString());
    }
}
