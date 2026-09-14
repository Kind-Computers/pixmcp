using System.Runtime.InteropServices;
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

    [Fact]
    public void DefaultsReproduceTheEarlierStructByteForByte()
    {
        var earlier = new PIX_TIMING_CAPTURE_OPTIONS
        {
            CapturePixEvents = true,
            CaptureContextSwitchAndReadyThread = PIX_EVENT_COLLECTION_LEVEL.PIX_EVENT_COLLECTION_LEVEL_ENABLED,
            CaptureCpuSamples = PIX_EVENT_COLLECTION_LEVEL.PIX_EVENT_COLLECTION_LEVEL_ENABLED_WITH_STACKS,
            CaptureTrackedFunctions = false,
            CaptureGpuTiming = true,
            CaptureGpuMemoryUsage = false,
            MaximumCaptureFileSizeMb = 1024,
            CpuSamplesPerSecond = 1000,
            CaptureFileIO = PIX_EVENT_COLLECTION_LEVEL.PIX_EVENT_COLLECTION_LEVEL_NONE,
            MergeKernelImages = false,
            CaptureStacksForAllProcesses = false,
            CaptureVirtualAllocEvents = PIX_EVENT_COLLECTION_LEVEL.PIX_EVENT_COLLECTION_LEVEL_NONE,
            CaptureHeapAllocEvents = PIX_EVENT_COLLECTION_LEVEL.PIX_EVENT_COLLECTION_LEVEL_NONE,
            CapturePixMemEvents = PIX_EVENT_COLLECTION_LEVEL.PIX_EVENT_COLLECTION_LEVEL_NONE,
            CaptureDuration = 0,
        };
        TimingCaptureSettingsDto resolved = TimingCaptureOptions.Resolve(new TimingCaptureRequest());
        Assert.Equal(Defaults, resolved);
        var (options, parts) = TimingCaptureOptions.Create(resolved);
        Assert.Equal(Bytes(earlier), Bytes(options));
        Assert.Equal(new[] { "captureSysmonCounters=false" }, TimingCaptureOptions.Describe(parts).Select(p => p.Type + "=" + p.Value));
    }

    [Fact]
    public void MemoryPresetSetsTheThreeAllocationLevelsWithoutExtraParts()
    {
        TimingCaptureSettingsDto memory = TimingCaptureOptions.Resolve(new TimingCaptureRequest(Preset: "Memory"));
        var (options, parts) = TimingCaptureOptions.Create(memory);
        Assert.Equal("memory", memory.Preset);
        Assert.Equal(PIX_EVENT_COLLECTION_LEVEL.PIX_EVENT_COLLECTION_LEVEL_ENABLED, options.CaptureVirtualAllocEvents);
        Assert.Equal(PIX_EVENT_COLLECTION_LEVEL.PIX_EVENT_COLLECTION_LEVEL_ENABLED, options.CaptureHeapAllocEvents);
        Assert.Equal(PIX_EVENT_COLLECTION_LEVEL.PIX_EVENT_COLLECTION_LEVEL_ENABLED, options.CapturePixMemEvents);
        Assert.True((bool)options.CaptureGpuMemoryUsage);
        Assert.Equal(PIX_TIMING_CAPTURE_OPTION_PART_TYPE.PIX_TIMING_CAPTURE_OPTION_PART_TYPE_CAPTURE_SYSMON_COUNTERS, Assert.Single(parts).Type);
    }

    [Fact]
    public void FileIoPresetRecordsStacksAndExplicitArgumentsOverridePresets()
    {
        var (fileIo, _) = TimingCaptureOptions.Create(TimingCaptureOptions.Resolve(new TimingCaptureRequest(Preset: "fileIo")));
        Assert.Equal(PIX_EVENT_COLLECTION_LEVEL.PIX_EVENT_COLLECTION_LEVEL_ENABLED_WITH_STACKS, fileIo.CaptureFileIO);

        TimingCaptureSettingsDto gpuOnly = TimingCaptureOptions.Resolve(new TimingCaptureRequest(Preset: "gpuOnly", CpuSamples: true));
        Assert.Equal((true, false, true, true), (gpuOnly.CpuSamples, gpuOnly.ContextSwitches, gpuOnly.GpuOnlyEvents, gpuOnly.GpuMemoryUsage));
        Assert.Equal("gpuOnlyEvents", Assert.Single(TimingCaptureOptions.Describe(TimingCaptureOptions.Create(gpuOnly).Parts), p => p.Type != "captureSysmonCounters").Type);

        var (overridden, _) = TimingCaptureOptions.Create(TimingCaptureOptions.Resolve(
            new TimingCaptureRequest(Preset: "memory", HeapAllocEvents: "WITHSTACKS", VirtualAllocEvents: "none")));
        Assert.Equal(PIX_EVENT_COLLECTION_LEVEL.PIX_EVENT_COLLECTION_LEVEL_ENABLED_WITH_STACKS, overridden.CaptureHeapAllocEvents);
        Assert.Equal(PIX_EVENT_COLLECTION_LEVEL.PIX_EVENT_COLLECTION_LEVEL_NONE, overridden.CaptureVirtualAllocEvents);
        Assert.Equal(PIX_EVENT_COLLECTION_LEVEL.PIX_EVENT_COLLECTION_LEVEL_ENABLED, overridden.CapturePixMemEvents);
    }

    [Fact]
    public void EveryOptionPartCarriesItsUnionMember()
    {
        TimingCaptureSettingsDto settings = TimingCaptureOptions.Resolve(new TimingCaptureRequest(Video: true, VideoSourceType: "monitor", VideoSourceId: "0x1234",
            IncludeCaptureEtl: true, Circular: true, PageFaults: "withStacks", ClrData: true, ForceComPath: true, GpuOnlyEvents: true,
            MinimalInstrumentation: true, CaptureSysmonCounters: true));
        var (_, parts) = TimingCaptureOptions.Create(settings);
        Assert.Equal(Enumerable.Range(1, 10).Select(i => (PIX_TIMING_CAPTURE_OPTION_PART_TYPE)i), parts.Select(p => p.Type));
        Assert.True((bool)parts[0].Id.CaptureVideo);
        Assert.True((bool)parts[1].Id.IncludeCaptureEtl);
        Assert.True((bool)parts[2].Id.Circular);
        Assert.Equal(PIX_EVENT_COLLECTION_LEVEL.PIX_EVENT_COLLECTION_LEVEL_ENABLED_WITH_STACKS, parts[3].Id.CapturePageFaults);
        Assert.True((bool)parts[4].Id.CaptureSysmonCounters);
        Assert.True((bool)parts[5].Id.CaptureClrData);
        Assert.Equal(PIX_VIDEO_CAPTURE_SOURCE_TYPE.PIX_VIDEO_CAPTURE_SOURCE_TYPE_MONITOR, parts[6].Id.VideoSourceId.Type);
        Assert.Equal(0x1234UL, parts[6].Id.VideoSourceId.Id.Value);
        Assert.True((bool)parts[7].Id.ForceComPath);
        Assert.True((bool)parts[8].Id.GpuOnlyEvents);
        Assert.True((bool)parts[9].Id.MinimalInstrumentation);
        Assert.Equal(new VideoSourceDto("monitor", "0x1234"), settings.VideoSource);
        Assert.Equal(new[] { "video=true", "includeCaptureEtl=true", "circular=true", "pageFaults=withStacks", "captureSysmonCounters=true", "clrData=true",
            "videoSource=monitor:0x1234", "forceComPath=true", "gpuOnlyEvents=true", "minimalInstrumentation=true" },
            TimingCaptureOptions.Describe(parts).Select(p => p.Type + "=" + p.Value));
        Assert.Equal(new VideoSourceDto("window", "0x2a"),
            TimingCaptureOptions.Resolve(new TimingCaptureRequest(Video: true, VideoSourceType: "Window", VideoSourceId: "42")).VideoSource);
    }

    [Fact]
    public void InvalidSettingsFailWithInvalidArguments()
    {
        TimingCaptureRequest[] requests =
        [
            new(FileIoStacks: true),
            new(VideoSourceType: "monitor", VideoSourceId: "1"),
            new(Video: true, VideoSourceType: "monitor"),
            new(Video: true, VideoSourceType: "screen", VideoSourceId: "1"),
            new(Video: true, VideoSourceType: "window", VideoSourceId: "0"),
            new(Video: true, VideoSourceType: "window", VideoSourceId: "0xZZ"),
            new(PageFaults: "sometimes"),
            new(Preset: "turbo"),
            new(ContextSwitches: false, ContextSwitchStacks: true),
        ];
        foreach (TimingCaptureRequest request in requests)
        {
            PixToolException error = Assert.Throws<PixToolException>(() => TimingCaptureOptions.Create(TimingCaptureOptions.Resolve(request)));
            Assert.Equal("invalid_arguments", error.Detail.Code);
        }
    }

    private static byte[] Bytes<T>(T value) where T : unmanaged => MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)).ToArray();
}
