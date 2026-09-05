using System.Runtime.InteropServices;
using Microsoft.PIX;
using ModelContextProtocol;
using PixMcp.Tools;
using Xunit;

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

        McpException error = Assert.Throws<McpException>(() => DeviceTools.CaptureWithOptions(
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
}
