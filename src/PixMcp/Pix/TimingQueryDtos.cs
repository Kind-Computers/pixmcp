namespace PixMcp.Pix;

public sealed record TimingRangeDto(string StartNs, string EndNs, string ReliableStartNs, string ReliableEndNs,
    string Source = "recordedTimingCapture", string TimeUnit = "nanoseconds", string Interval = "[start,end)")
{
    /// <summary>full (through the capture end) or reliable (through the stop timestamp); null on tools that predate rangeMode.</summary>
    public string? RangeMode { get; init; }
    /// <summary>The last recorded timestamp (CaptureFacts 3), when known.</summary>
    public string? CaptureEndNs { get; init; }
    /// <summary>How much of each recorded family the selected window covers.</summary>
    public TimingCoverageDto? Coverage { get; init; }
    /// <summary>What the window includes and excludes.</summary>
    public string? Note { get; init; }
}
/// <summary>Rows inside the selected window versus the whole capture, per recorded family; null when the capture lacks that family.</summary>
public sealed record TimingCoverageDto(TimingCoverageCountDto? ContextSwitches, TimingCoverageCountDto? CpuEvents, TimingCoverageCountDto? GpuSubmissions, TimingCoverageCountDto? GpuHardware);
public sealed record TimingCoverageCountDto(long InRange, long Total);
/// <summary>A recorded family or feature: available, empty, unsupported (or unresolved/not_applicable), why, and the capture-wide row count when known.</summary>
public sealed record TimingCapabilityDto(string State, string? Reason = null, long? Rows = null);
public sealed record TimingProcessDto(string ProcessRowId, uint ProcessId, string? Name, long ThreadCount, long SampleCount);
public sealed record TimingThreadDto(string ThreadRowId, uint ProcessId, uint ThreadId, string? Name, long SampleCount)
{
    public string? StartNs { get; init; }
    public string? EndNs { get; init; }
    public long? PixEventCount { get; init; }
    public long? ContextSwitchCount { get; init; }
    public long? MarkerCount { get; init; }
}
public sealed record TimingQueueDto(string QueueId, uint ProcessId, string? Name, string? Type)
{
    public string? AdapterName { get; init; }
    public string? BeginNs { get; init; }
    public string? EndNs { get; init; }
    public long? ApiExecutionCount { get; init; }
    public long? CommandListCount { get; init; }
    public long? MaxWorkLevel { get; init; }
}
/// <summary>A hardware (kernel) queue that carried GpuWorkRange rows: its adapter, how many ranges, and when its work began and ended.</summary>
public sealed record TimingHardwareQueueDto(string HardwareQueueId, string? Name, string? AdapterName, long WorkRanges, string? FirstNs, string? LastNs);
public sealed record TimingOverviewDto(string Handle, TimingRangeDto Provenance,
    IReadOnlyDictionary<string, TimingCapabilityDto> Capabilities,
    PageResult<TimingProcessDto> Processes, PageResult<TimingThreadDto> Threads, PageResult<TimingQueueDto> Queues,
    long CounterCount, long SampleCount, long StackCount, long SymbolCount, IReadOnlyList<ToolCallDto> NextCalls,
    PageResult<TimingHardwareQueueDto>? HardwareQueues = null)
{
    /// <summary>Capture, data-quality, GPU, frame, core, module and video-memory summaries plus insights, computed with the named query library.</summary>
    public TimingOverviewSectionsDto? Sections { get; init; }
}

public sealed record RecordedTimingEventDto(string EventId, string Domain, string? Name, string BeginNs, string EndNs,
    string DurationNs, string OverlapDurationNs, int Level, uint? ProcessId, uint? ThreadId, string? ThreadName,
    string? QueueId, string? QueueName, string? ExecutionNs = null, string? StallNs = null, string? ExecutionTimingState = null,
    string? Source = null, string? SubmitNs = null, string? SubmitLatencyNs = null, long? CommandListCount = null, string? SubmissionRef = null,
    string? HardwareQueueId = null, string? HardwareQueueName = null, int? OverlapLevel = null, long? Color = null, string? ExecutionTimingMethod = null);
/// <summary>A recorded family pix_timing_events considered: included when its tables exist, with the rows matching the filters in the window.</summary>
public sealed record TimingEventSourceDto(string Domain, string Source, string Table, string State, long Rows);
public sealed record TimingEventsDto(string Handle, TimingRangeDto Provenance, PageResult<RecordedTimingEventDto> Events,
    TimingCapabilityDto CpuExecutionTiming,
    IReadOnlyList<ToolCallDto> NextCalls, IReadOnlyList<TimingEventSourceDto>? Sources = null);
public sealed record TimingCounterDto(string CounterId, string? Name, IReadOnlyList<string> GroupPath,
    string? Description, string? Units, uint? ProcessId);
public sealed record TimingCountersDto(string Handle, PageResult<TimingCounterDto> Counters, IReadOnlyList<ToolCallDto> NextCalls);
public sealed record TimingCounterSampleDto(string TimestampNs, double Value);
public sealed record TimingCounterSamplesDto(string Handle, TimingRangeDto Provenance, TimingCounterDto Counter,
    PageResult<TimingCounterSampleDto> Samples, IReadOnlyList<ToolCallDto> NextCalls);

public sealed record TimingSampleSelection(uint? ProcessId, uint? ThreadId, long? EfficiencyClass = null);
public sealed record TimingSampleCoverageDto(long TotalSamples, long SamplesWithStacks, long SamplesWithoutStacks,
    long InvalidStackSamples, long SamplesWithUnresolvedFrames, long ResolvedFrames, long UnresolvedFrames,
    string StackState = "available", string SymbolState = "available",
    string Metric = "sampleCount", string PercentageDenominator = "all selected CPU samples",
    string StackOrder = "callerToCallee")
{
    /// <summary>Selected samples per core efficiency class; present only when the CPU has more than one class ("unknown" for cores without a class).</summary>
    public IReadOnlyDictionary<string, long>? SamplesByEfficiencyClass { get; init; }
}
public sealed record TimingFunctionDto(string Key, string Address, string? Module, string? ModuleId,
    string? Function, string? FunctionOffset, string SymbolState, string? SourceFile, int? SourceLine);
public sealed record TimingHotspotDto(TimingFunctionDto Function, long InclusiveSamples, long ExclusiveSamples,
    double InclusivePercent, double ExclusivePercent)
{
    /// <summary>Inclusive samples per core efficiency class; present only when the CPU has more than one class.</summary>
    public IReadOnlyDictionary<string, long>? InclusiveByEfficiencyClass { get; init; }
}
public sealed record TimingCallNodeDto(string NodeId, string? ParentNodeId, TimingFunctionDto? Function,
    long InclusiveSamples, long ExclusiveSamples, double Percent, int ChildCount);
public sealed record TimingSampleAnalysisDto(string Handle, TimingRangeDto Provenance, TimingSampleSelection Selection,
    TimingSampleCoverageDto Coverage, IReadOnlyList<TimingHotspotDto> Hotspots, IReadOnlyList<TimingCallNodeDto> Nodes);
public sealed record TimingHotspotsDto(string Handle, string ProfileRef, TimingRangeDto Provenance,
    TimingSampleCoverageDto Coverage, PageResult<TimingHotspotDto> Hotspots, IReadOnlyList<ToolCallDto> NextCalls);
public sealed record TimingCalltreeDto(string Handle, string ProfileRef, TimingRangeDto Provenance,
    TimingSampleCoverageDto Coverage, TimingCallNodeDto Parent, PageResult<TimingCallNodeDto> Children,
    IReadOnlyList<ToolCallDto> NextCalls);
