using Microsoft.PIX;

namespace PixMcp.Pix;

public sealed record TimingCaptureSettingsDto(bool CpuSamples, uint CpuSamplesPerSecond,
    bool CpuSampleStacks, bool ContextSwitches, bool ContextSwitchStacks, bool PixEvents,
    bool GpuTiming, bool GpuMemoryUsage, bool FileIo, uint MaxFileSizeMb,
    uint DurationSeconds, bool CaptureSysmonCounters);

internal static class TimingCaptureOptions
{
    internal static (PIX_TIMING_CAPTURE_OPTIONS Options, PIX_TIMING_CAPTURE_OPTION_PART[] Parts)
        Create(TimingCaptureSettingsDto settings)
    {
        if (settings.ContextSwitchStacks && !settings.ContextSwitches)
            throw new PixToolException("invalid_arguments", "contextSwitchStacks requires contextSwitches=true.");

        static PIX_EVENT_COLLECTION_LEVEL Level(bool enabled, bool stacks) => !enabled
            ? PIX_EVENT_COLLECTION_LEVEL.PIX_EVENT_COLLECTION_LEVEL_NONE
            : stacks ? PIX_EVENT_COLLECTION_LEVEL.PIX_EVENT_COLLECTION_LEVEL_ENABLED_WITH_STACKS
            : PIX_EVENT_COLLECTION_LEVEL.PIX_EVENT_COLLECTION_LEVEL_ENABLED;

        var options = new PIX_TIMING_CAPTURE_OPTIONS
        {
            CapturePixEvents = settings.PixEvents,
            CaptureContextSwitchAndReadyThread = Level(settings.ContextSwitches, settings.ContextSwitchStacks),
            CaptureCpuSamples = Level(settings.CpuSamples, settings.CpuSampleStacks),
            CaptureTrackedFunctions = false,
            CaptureGpuTiming = settings.GpuTiming,
            CaptureGpuMemoryUsage = settings.GpuMemoryUsage,
            MaximumCaptureFileSizeMb = settings.MaxFileSizeMb,
            CpuSamplesPerSecond = settings.CpuSamplesPerSecond,
            CaptureFileIO = Level(settings.FileIo, false),
            MergeKernelImages = false,
            CaptureStacksForAllProcesses = false,
            CaptureVirtualAllocEvents = PIX_EVENT_COLLECTION_LEVEL.PIX_EVENT_COLLECTION_LEVEL_NONE,
            CaptureHeapAllocEvents = PIX_EVENT_COLLECTION_LEVEL.PIX_EVENT_COLLECTION_LEVEL_NONE,
            CapturePixMemEvents = PIX_EVENT_COLLECTION_LEVEL.PIX_EVENT_COLLECTION_LEVEL_NONE,
            CaptureDuration = settings.DurationSeconds,
        };
        var sysmon = new PIX_TIMING_CAPTURE_OPTION_PART
        {
            Type = PIX_TIMING_CAPTURE_OPTION_PART_TYPE.PIX_TIMING_CAPTURE_OPTION_PART_TYPE_CAPTURE_SYSMON_COUNTERS,
        };
        sysmon.Id.CaptureSysmonCounters = settings.CaptureSysmonCounters;
        return (options, [sysmon]);
    }
}
