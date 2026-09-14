using PixMcp.Pix;
using System.Runtime.InteropServices;
using Microsoft.PIX;
using ModelContextProtocol;
using PixMcp.Tools;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using PixMcp.Pix.Handles;
using Windows.Win32.Graphics.Dxgi.Common;

namespace PixMcp.Tests;

public class CaptureOptionsTests
{
    [Fact]
    public void SingleFrameCaptureReplacesPreviousMultiFrameOptions()
    {
        PIX_GPU_CAPTURE_OPTIONS current = default;
        var captured = new List<PIX_GPU_CAPTURE_OPTIONS>();
        void Apply(PIX_GPU_CAPTURE_OPTIONS options) => current = options;
        uint Capture()
        {
            captured.Add(current);
            return current.FrameCount;
        }

        uint first = DeviceTools.CaptureWithOptions(42, 3, Apply, Capture);
        uint second = DeviceTools.CaptureWithOptions(43, 1, Apply, Capture);

        Assert.Equal(3u, first);
        Assert.Equal(1u, second);
        Assert.Equal(new uint[] { 3, 1 }, captured.Select(o => o.FrameCount));
        Assert.Equal(new uint[] { 42, 43 }, captured.Select(o => o.TargetProcessId));
        Assert.All(captured, o => Assert.Equal(PIX_GPU_CAPTURE_DELIMITER.PIX_GPU_CAPTURE_DELIMITER_PRESENT, o.Delimiter));
    }

    [Fact]
    public void InvalidFrameCountDoesNotConfigureOrCapture()
    {
        bool configured = false;
        bool captured = false;

        McpException error = Assert.Throws<PixToolException>(() => DeviceTools.CaptureWithOptions(
            42, 0,
            _ => configured = true,
            () => captured = true));

        Assert.Contains("frameCount", error.Message);
        Assert.False(configured);
        Assert.False(captured);
    }

    [Fact]
    public void FailedConfigurationDoesNotCaptureWithStaleOptions()
    {
        var failure = new COMException("Cannot configure capture.", unchecked((int)0x80004005));
        bool captured = false;

        COMException error = Assert.Throws<COMException>(() => DeviceTools.CaptureWithOptions(
            42, 1,
            _ => throw failure,
            () => captured = true));

        Assert.Same(failure, error);
        Assert.False(captured);
    }

    [Fact]
    public void DelimiterAndCaptureKeyAreWrittenAndResetByLaterCaptures()
    {
        var written = new List<PIX_GPU_CAPTURE_OPTIONS>();
        DeviceTools.CaptureWithOptions(42, 2, written.Add, () => 0, default,
            GpuCaptureOptionNames.ParseDelimiter("CapturableRegion"), GpuCaptureOptionNames.ParseCaptureKey("f5"));
        DeviceTools.CaptureWithOptions(42, 1, written.Add, () => 0);

        Assert.Equal((PIX_GPU_CAPTURE_DELIMITER.PIX_GPU_CAPTURE_DELIMITER_CAPTURABLE_REGION, PIX_GPU_CAPTURE_KEY.PIX_GPU_CAPTURE_KEY_F5),
            (written[0].Delimiter, written[0].CaptureKey));
        Assert.Equal((PIX_GPU_CAPTURE_DELIMITER.PIX_GPU_CAPTURE_DELIMITER_PRESENT, PIX_GPU_CAPTURE_KEY.PIX_GPU_CAPTURE_KEY_NONE),
            (written[1].Delimiter, written[1].CaptureKey));
        Assert.Equal(PIX_GPU_CAPTURE_KEY.PIX_GPU_CAPTURE_KEY_F12, GpuCaptureOptionNames.ParseCaptureKey("F12"));
        Assert.Equal(("capturableRegion", "F5"), (GpuCaptureOptionNames.Name(written[0].Delimiter), GpuCaptureOptionNames.Name(written[0].CaptureKey)));
        Assert.Equal("invalid_arguments", Assert.Throws<PixToolException>(() => GpuCaptureOptionNames.ParseDelimiter("vsync")).Detail.Code);
        Assert.Equal("invalid_arguments", Assert.Throws<PixToolException>(() => GpuCaptureOptionNames.ParseCaptureKey("F13")).Detail.Code);
    }

    [Fact]
    public void ThumbnailsAreArtifactsThatExpireWithTheirOwningHandle()
    {
        using var worker = new PixWorker();
        using var session = new PixSession(worker, NullLogger<PixSession>.Instance);
        string owner = session.Register(new DeviceStandIn()).Id;

        CaptureThumbnailDto none = DeviceTools.Thumbnail(session, owner, []);
        Assert.False(none.Available);
        Assert.Contains("no screenshot", none.Reason);
        Assert.False(DeviceTools.Thumbnail(session, owner, [1, 2, 3]).Available);

        byte[] png = Png.Encode([10, 20, 30, 255, 40, 50, 60, 255], 2, 1, 8, DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM);
        CaptureThumbnailDto thumbnail = DeviceTools.Thumbnail(session, owner, png);
        Assert.True(thumbnail.Available, thumbnail.Reason);
        Assert.Equal((2u, 1u, png.Length, owner), (thumbnail.Width!.Value, thumbnail.Height!.Value, thumbnail.PngBytes, thumbnail.Owner));
        Assert.Equal("pix_gpu_preview_image", Assert.Single(thumbnail.NextCalls).Tool);
        Assert.Equal(png, PreviewTools.GetArtifact(session, thumbnail.ArtifactRef!));

        session.Close(owner);
        Assert.Equal(PixErrors.Codes.ArtifactExpired,
            Assert.Throws<PixToolException>(() => PreviewTools.GetArtifact(session, thumbnail.ArtifactRef!)).Detail.Code);
    }

    private sealed class DeviceStandIn : PixHandle
    {
        public DeviceStandIn() : base("device") { }
        public override string Kind => "device";
        public override void Close(List<string> warnings) { }
    }
}
