using Microsoft.PIX;
using PixMcp.Pix;
using Xunit;

namespace PixMcp.Tests;

public sealed class TimingCaptureOptionsTests
{
    private static TimingCaptureSettingsDto Defaults => new(true, 1000, true, true, false,
        true, true, false, false, 1024, 0, false);

    [Fact]
    public void TutorialOptionsRecordSwitchStacksAndSysmonWithoutOtherCostlyEvents()
    {
        var (options, parts) = TimingCaptureOptions.Create(Defaults with
            { ContextSwitchStacks = true, CaptureSysmonCounters = true });
        Assert.Equal(PIX_EVENT_COLLECTION_LEVEL.PIX_EVENT_COLLECTION_LEVEL_ENABLED_WITH_STACKS,
            options.CaptureContextSwitchAndReadyThread);
        Assert.Equal(PIX_EVENT_COLLECTION_LEVEL.PIX_EVENT_COLLECTION_LEVEL_ENABLED_WITH_STACKS, options.CaptureCpuSamples);
        Assert.Equal(PIX_TIMING_CAPTURE_OPTION_PART_TYPE.PIX_TIMING_CAPTURE_OPTION_PART_TYPE_CAPTURE_SYSMON_COUNTERS,
            Assert.Single(parts).Type);
        Assert.True((bool)parts[0].Id.CaptureSysmonCounters);
        Assert.False((bool)options.CaptureGpuMemoryUsage);
        Assert.Equal(PIX_EVENT_COLLECTION_LEVEL.PIX_EVENT_COLLECTION_LEVEL_NONE, options.CaptureFileIO);
    }

    [Fact]
    public void DefaultsPreserveUnstackedSwitchesAndExplicitlyDisableSysmon()
    {
        var (options, parts) = TimingCaptureOptions.Create(Defaults);
        Assert.Equal(PIX_EVENT_COLLECTION_LEVEL.PIX_EVENT_COLLECTION_LEVEL_ENABLED, options.CaptureContextSwitchAndReadyThread);
        Assert.False((bool)Assert.Single(parts).Id.CaptureSysmonCounters);
        var disabled = TimingCaptureOptions.Create(Defaults with { ContextSwitches = false, CpuSamples = false });
        Assert.Equal(PIX_EVENT_COLLECTION_LEVEL.PIX_EVENT_COLLECTION_LEVEL_NONE, disabled.Options.CaptureContextSwitchAndReadyThread);
        Assert.Equal(PIX_EVENT_COLLECTION_LEVEL.PIX_EVENT_COLLECTION_LEVEL_NONE, disabled.Options.CaptureCpuSamples);
    }

    [Fact]
    public void StackRequestRequiresContextSwitchCollection()
    {
        var error = Assert.Throws<PixToolException>(() => TimingCaptureOptions.Create(Defaults with
            { ContextSwitches = false, ContextSwitchStacks = true }));
        Assert.Equal("invalid_arguments", error.Detail.Code);
    }
}
