using System.Globalization;
using Microsoft.PIX;
using Collection = Microsoft.PIX.PIX_EVENT_COLLECTION_LEVEL;
using PartType = Microsoft.PIX.PIX_TIMING_CAPTURE_OPTION_PART_TYPE;

namespace PixMcp.Pix;

/// <summary>Effective timing capture settings: the preset resolved under the explicit arguments. Collection levels are none, enabled or withStacks.</summary>
public sealed record TimingCaptureSettingsDto(bool CpuSamples, uint CpuSamplesPerSecond,
    bool CpuSampleStacks, bool ContextSwitches, bool ContextSwitchStacks, bool PixEvents,
    bool GpuTiming, bool GpuMemoryUsage, bool FileIo, uint MaxFileSizeMb,
    uint DurationSeconds, bool CaptureSysmonCounters,
    string VirtualAllocEvents = "none", string HeapAllocEvents = "none", string PixMemEvents = "none", string PageFaults = "none",
    bool FileIoStacks = false, bool TrackedFunctions = false, bool MergeKernelImages = false, bool StacksForAllProcesses = false,
    bool ClrData = false, bool Video = false, VideoSourceDto? VideoSource = null, bool IncludeCaptureEtl = false, bool Circular = false,
    bool ForceComPath = false, bool GpuOnlyEvents = false, bool MinimalInstrumentation = false, string Preset = "default");

/// <summary>The window or monitor a timing capture records video from; Id is the HWND or HMONITOR value in hex.</summary>
public sealed record VideoSourceDto(string Type, string Id);

/// <summary>One PIX_TIMING_CAPTURE_OPTION_PART as sent to PIX.</summary>
public sealed record TimingOptionPartDto(string Type, string Value);

/// <summary>A GPU capture's screenshot kept as a preview artifact, or the reason there is none.</summary>
public sealed record CaptureThumbnailDto(bool Available, string? ArtifactRef, uint? Width, uint? Height, int PngBytes, string Owner, string? Reason,
    IReadOnlyList<ToolCallDto> NextCalls);

/// <summary>Timing capture arguments as given; null leaves the choice to the preset, then to the server default.</summary>
internal sealed record TimingCaptureRequest(string? Preset = null, bool? CpuSamples = null, uint? CpuSamplesPerSecond = null, bool? CpuSampleStacks = null,
    bool? ContextSwitches = null, bool? ContextSwitchStacks = null, bool? PixEvents = null, bool? GpuTiming = null, bool? GpuMemoryUsage = null,
    bool? FileIo = null, bool? FileIoStacks = null, uint? MaxFileSizeMb = null, uint? DurationSeconds = null, bool? CaptureSysmonCounters = null,
    string? VirtualAllocEvents = null, string? HeapAllocEvents = null, string? PixMemEvents = null, string? PageFaults = null,
    bool? TrackedFunctions = null, bool? MergeKernelImages = null, bool? StacksForAllProcesses = null, bool? ClrData = null, bool? Video = null,
    string? VideoSourceType = null, string? VideoSourceId = null, bool? IncludeCaptureEtl = null, bool? Circular = null, bool? ForceComPath = null,
    bool? GpuOnlyEvents = null, bool? MinimalInstrumentation = null);

internal static class TimingCaptureOptions
{
    public static readonly string[] Presets = ["default", "memory", "fileIo", "gpuOnly", "minimal"];
    public static readonly string[] Levels = ["none", "enabled", "withStacks"];
    public static readonly string[] VideoSourceTypes = ["window", "monitor"];

    /// <summary>
    /// Resolves the preset under the explicit arguments. memory adds the three allocation event families and GPU memory usage; fileIo records
    /// file I/O with stacks; gpuOnly and minimal drop CPU samples and context switches and set their option part (gpuOnly also records GPU memory).
    /// </summary>
    internal static TimingCaptureSettingsDto Resolve(TimingCaptureRequest r)
    {
        string preset = r.Preset is null ? "default" : Canonical(r.Preset, Presets, "preset");
        bool memory = preset == "memory", fileIo = preset == "fileIo", gpuOnly = preset == "gpuOnly", minimal = preset == "minimal";
        bool cpuSamples = r.CpuSamples ?? !(gpuOnly || minimal);
        bool fileIoOn = r.FileIo ?? fileIo;
        string memoryLevel = memory ? "enabled" : "none";
        VideoSourceDto? source = null;
        if (r.VideoSourceType is not null || r.VideoSourceId is not null)
        {
            if (r.VideoSourceType is null || r.VideoSourceId is null)
                throw PixErrors.InvalidArguments("videoSourceType and videoSourceId must be given together.");
            source = new(Canonical(r.VideoSourceType, VideoSourceTypes, "videoSourceType"),
                "0x" + ParseHandleValue(r.VideoSourceId).ToString("x", CultureInfo.InvariantCulture));
        }
        return new TimingCaptureSettingsDto(
            cpuSamples, r.CpuSamplesPerSecond ?? 1000, cpuSamples && (r.CpuSampleStacks ?? true),
            r.ContextSwitches ?? !(gpuOnly || minimal), r.ContextSwitchStacks ?? false, r.PixEvents ?? true,
            r.GpuTiming ?? true, r.GpuMemoryUsage ?? (memory || gpuOnly), fileIoOn, r.MaxFileSizeMb ?? 1024,
            r.DurationSeconds ?? 0, r.CaptureSysmonCounters ?? false,
            LevelOr(r.VirtualAllocEvents, "virtualAllocEvents", memoryLevel), LevelOr(r.HeapAllocEvents, "heapAllocEvents", memoryLevel),
            LevelOr(r.PixMemEvents, "pixMemEvents", memoryLevel), LevelOr(r.PageFaults, "pageFaults", "none"),
            r.FileIoStacks ?? (fileIo && fileIoOn), r.TrackedFunctions ?? false, r.MergeKernelImages ?? false, r.StacksForAllProcesses ?? false,
            r.ClrData ?? false, r.Video ?? false, source, r.IncludeCaptureEtl ?? false, r.Circular ?? false,
            r.ForceComPath ?? false, r.GpuOnlyEvents ?? gpuOnly, r.MinimalInstrumentation ?? minimal, preset);
    }

    /// <summary>
    /// Builds the PIX options. The sysmon part is always sent with its value; every other option part only when it is enabled, with its own
    /// union member set.
    /// </summary>
    internal static (PIX_TIMING_CAPTURE_OPTIONS Options, PIX_TIMING_CAPTURE_OPTION_PART[] Parts)
        Create(TimingCaptureSettingsDto settings)
    {
        if (settings.ContextSwitchStacks && !settings.ContextSwitches)
            throw new PixToolException(PixErrors.Codes.InvalidArguments, "contextSwitchStacks requires contextSwitches=true.");
        if (settings.FileIoStacks && !settings.FileIo)
            throw PixErrors.InvalidArguments("fileIoStacks requires fileIo=true.");
        if (settings.VideoSource is not null && !settings.Video)
            throw PixErrors.InvalidArguments("videoSourceType and videoSourceId require video=true.");

        static Collection Collect(bool enabled, bool stacks) => !enabled
            ? Collection.PIX_EVENT_COLLECTION_LEVEL_NONE
            : stacks ? Collection.PIX_EVENT_COLLECTION_LEVEL_ENABLED_WITH_STACKS
            : Collection.PIX_EVENT_COLLECTION_LEVEL_ENABLED;

        var options = new PIX_TIMING_CAPTURE_OPTIONS
        {
            CapturePixEvents = settings.PixEvents,
            CaptureContextSwitchAndReadyThread = Collect(settings.ContextSwitches, settings.ContextSwitchStacks),
            CaptureCpuSamples = Collect(settings.CpuSamples, settings.CpuSampleStacks),
            CaptureTrackedFunctions = settings.TrackedFunctions,
            CaptureGpuTiming = settings.GpuTiming,
            CaptureGpuMemoryUsage = settings.GpuMemoryUsage,
            MaximumCaptureFileSizeMb = settings.MaxFileSizeMb,
            CpuSamplesPerSecond = settings.CpuSamplesPerSecond,
            CaptureFileIO = Collect(settings.FileIo, settings.FileIoStacks),
            MergeKernelImages = settings.MergeKernelImages,
            CaptureStacksForAllProcesses = settings.StacksForAllProcesses,
            CaptureVirtualAllocEvents = ToLevel(settings.VirtualAllocEvents, "virtualAllocEvents"),
            CaptureHeapAllocEvents = ToLevel(settings.HeapAllocEvents, "heapAllocEvents"),
            CapturePixMemEvents = ToLevel(settings.PixMemEvents, "pixMemEvents"),
            CaptureDuration = settings.DurationSeconds,
        };

        static PIX_TIMING_CAPTURE_OPTION_PART Part(PartType type) => new() { Type = type };
        var parts = new List<PIX_TIMING_CAPTURE_OPTION_PART>();
        if (settings.Video)
        {
            var part = Part(PartType.PIX_TIMING_CAPTURE_OPTION_PART_TYPE_VIDEO);
            part.Id.CaptureVideo = true;
            parts.Add(part);
        }
        if (settings.IncludeCaptureEtl)
        {
            var part = Part(PartType.PIX_TIMING_CAPTURE_OPTION_PART_TYPE_INCLUDE_CAPTURE_ETL);
            part.Id.IncludeCaptureEtl = true;
            parts.Add(part);
        }
        if (settings.Circular)
        {
            var part = Part(PartType.PIX_TIMING_CAPTURE_OPTION_PART_TYPE_CIRCULAR);
            part.Id.Circular = true;
            parts.Add(part);
        }
        Collection pageFaults = ToLevel(settings.PageFaults, "pageFaults");
        if (pageFaults != Collection.PIX_EVENT_COLLECTION_LEVEL_NONE)
        {
            var part = Part(PartType.PIX_TIMING_CAPTURE_OPTION_PART_TYPE_PAGEFAULT);
            part.Id.CapturePageFaults = pageFaults;
            parts.Add(part);
        }
        var sysmon = Part(PartType.PIX_TIMING_CAPTURE_OPTION_PART_TYPE_CAPTURE_SYSMON_COUNTERS);
        sysmon.Id.CaptureSysmonCounters = settings.CaptureSysmonCounters;
        parts.Add(sysmon);
        if (settings.ClrData)
        {
            var part = Part(PartType.PIX_TIMING_CAPTURE_OPTION_PART_TYPE_CLRDATA);
            part.Id.CaptureClrData = true;
            parts.Add(part);
        }
        if (settings.VideoSource is VideoSourceDto source)
        {
            var video = new PIX_VIDEO_CAPTURE_SOURCE
            {
                Type = Canonical(source.Type, VideoSourceTypes, "videoSourceType") == "monitor"
                    ? PIX_VIDEO_CAPTURE_SOURCE_TYPE.PIX_VIDEO_CAPTURE_SOURCE_TYPE_MONITOR
                    : PIX_VIDEO_CAPTURE_SOURCE_TYPE.PIX_VIDEO_CAPTURE_SOURCE_TYPE_WINDOW,
            };
            video.Id.Value = ParseHandleValue(source.Id);
            var part = Part(PartType.PIX_TIMING_CAPTURE_OPTION_PART_TYPE_VIDEO_SOURCEID);
            part.Id.VideoSourceId = video;
            parts.Add(part);
        }
        if (settings.ForceComPath)
        {
            var part = Part(PartType.PIX_TIMING_CAPTURE_OPTION_PART_TYPE_FORCE_COM_PATH);
            part.Id.ForceComPath = true;
            parts.Add(part);
        }
        if (settings.GpuOnlyEvents)
        {
            var part = Part(PartType.PIX_TIMING_CAPTURE_OPTION_PART_TYPE_GPU_ONLY_EVENTS);
            part.Id.GpuOnlyEvents = true;
            parts.Add(part);
        }
        if (settings.MinimalInstrumentation)
        {
            var part = Part(PartType.PIX_TIMING_CAPTURE_OPTION_PART_TYPE_MINIMAL_INSTRUMENTATION);
            part.Id.MinimalInstrumentation = true;
            parts.Add(part);
        }
        return (options, parts.ToArray());
    }

    /// <summary>The option parts in wire vocabulary, echoed by the start response and named in start failures.</summary>
    internal static IReadOnlyList<TimingOptionPartDto> Describe(IEnumerable<PIX_TIMING_CAPTURE_OPTION_PART> parts)
        => parts.Select(p => p.Type switch
        {
            PartType.PIX_TIMING_CAPTURE_OPTION_PART_TYPE_VIDEO => new TimingOptionPartDto("video", Bool((bool)p.Id.CaptureVideo)),
            PartType.PIX_TIMING_CAPTURE_OPTION_PART_TYPE_INCLUDE_CAPTURE_ETL => new("includeCaptureEtl", Bool((bool)p.Id.IncludeCaptureEtl)),
            PartType.PIX_TIMING_CAPTURE_OPTION_PART_TYPE_CIRCULAR => new("circular", Bool((bool)p.Id.Circular)),
            PartType.PIX_TIMING_CAPTURE_OPTION_PART_TYPE_PAGEFAULT => new("pageFaults", LevelName(p.Id.CapturePageFaults)),
            PartType.PIX_TIMING_CAPTURE_OPTION_PART_TYPE_CAPTURE_SYSMON_COUNTERS => new("captureSysmonCounters", Bool((bool)p.Id.CaptureSysmonCounters)),
            PartType.PIX_TIMING_CAPTURE_OPTION_PART_TYPE_CLRDATA => new("clrData", Bool((bool)p.Id.CaptureClrData)),
            PartType.PIX_TIMING_CAPTURE_OPTION_PART_TYPE_VIDEO_SOURCEID => new("videoSource",
                (p.Id.VideoSourceId.Type == PIX_VIDEO_CAPTURE_SOURCE_TYPE.PIX_VIDEO_CAPTURE_SOURCE_TYPE_MONITOR ? "monitor" : "window")
                + ":0x" + p.Id.VideoSourceId.Id.Value.ToString("x", CultureInfo.InvariantCulture)),
            PartType.PIX_TIMING_CAPTURE_OPTION_PART_TYPE_FORCE_COM_PATH => new("forceComPath", Bool((bool)p.Id.ForceComPath)),
            PartType.PIX_TIMING_CAPTURE_OPTION_PART_TYPE_GPU_ONLY_EVENTS => new("gpuOnlyEvents", Bool((bool)p.Id.GpuOnlyEvents)),
            PartType.PIX_TIMING_CAPTURE_OPTION_PART_TYPE_MINIMAL_INSTRUMENTATION => new("minimalInstrumentation", Bool((bool)p.Id.MinimalInstrumentation)),
            _ => new(p.Type.ToString(), "unknown"),
        }).ToArray();

    private static string Bool(bool value) => value ? "true" : "false";

    private static string LevelName(Collection level) => level switch
    {
        Collection.PIX_EVENT_COLLECTION_LEVEL_ENABLED => "enabled",
        Collection.PIX_EVENT_COLLECTION_LEVEL_ENABLED_WITH_STACKS => "withStacks",
        _ => "none",
    };

    private static string LevelOr(string? value, string name, string fallback) => value is null ? fallback : Canonical(value, Levels, name);

    private static Collection ToLevel(string level, string name) => Canonical(level, Levels, name) switch
    {
        "enabled" => Collection.PIX_EVENT_COLLECTION_LEVEL_ENABLED,
        "withStacks" => Collection.PIX_EVENT_COLLECTION_LEVEL_ENABLED_WITH_STACKS,
        _ => Collection.PIX_EVENT_COLLECTION_LEVEL_NONE,
    };

    private static string Canonical(string value, string[] choices, string name)
        => choices.FirstOrDefault(c => c.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw PixErrors.InvalidArguments($"{name} must be one of: {string.Join(", ", choices)}.");

    private static ulong ParseHandleValue(string id)
    {
        string text = id.Trim();
        ulong value;
        bool parsed = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value)
            : ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        return parsed && value != 0 ? value : throw PixErrors.InvalidArguments("videoSourceId must be a nonzero HWND or HMONITOR value, decimal or 0x hex.");
    }
}

/// <summary>Wire names for PIX_GPU_CAPTURE_OPTIONS: the frame delimiter and the capture hotkey (both in header order).</summary>
internal static class GpuCaptureOptionNames
{
    public static readonly string[] Delimiters = ["present", "capturableRegion"];
    public static readonly string[] CaptureKeys = ["none", "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12"];

    public static PIX_GPU_CAPTURE_DELIMITER ParseDelimiter(string? value)
    {
        int index = Array.FindIndex(Delimiters, d => d.Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase));
        return index < 0 ? throw PixErrors.InvalidArguments($"delimiter must be one of: {string.Join(", ", Delimiters)}.") : (PIX_GPU_CAPTURE_DELIMITER)index;
    }

    public static PIX_GPU_CAPTURE_KEY ParseCaptureKey(string? value)
    {
        int index = Array.FindIndex(CaptureKeys, k => k.Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase));
        return index < 0 ? throw PixErrors.InvalidArguments($"captureKey must be one of: {string.Join(", ", CaptureKeys)}.") : (PIX_GPU_CAPTURE_KEY)index;
    }

    public static string Name(PIX_GPU_CAPTURE_DELIMITER delimiter) => (int)delimiter is >= 0 and < 2 ? Delimiters[(int)delimiter] : delimiter.ToString();

    public static string Name(PIX_GPU_CAPTURE_KEY key) => (int)key is >= 0 and < 13 ? CaptureKeys[(int)key] : key.ToString();
}
