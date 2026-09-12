namespace PixMcp.Pix;

public sealed record TimingRangeDto(string StartNs, string EndNs, string ReliableStartNs, string ReliableEndNs,
    string Source = "recordedTimingCapture", string TimeUnit = "nanoseconds", string Interval = "[start,end)");
public sealed record TimingCapabilityDto(string State, string? Reason = null);
public sealed record TimingProcessDto(string ProcessRowId, uint ProcessId, string? Name, long ThreadCount, long SampleCount);
public sealed record TimingThreadDto(string ThreadRowId, uint ProcessId, uint ThreadId, string? Name, long SampleCount);
public sealed record TimingQueueDto(string QueueId, uint ProcessId, string? Name, string? Type);
public sealed record TimingOverviewDto(string Handle, TimingRangeDto Provenance,
    IReadOnlyDictionary<string, TimingCapabilityDto> Capabilities,
    PageResult<TimingProcessDto> Processes, PageResult<TimingThreadDto> Threads, PageResult<TimingQueueDto> Queues,
    long CounterCount, long SampleCount, long StackCount, long SymbolCount, IReadOnlyList<ToolCallDto> NextCalls);

public sealed record RecordedTimingEventDto(string EventId, string Domain, string? Name, string BeginNs, string EndNs,
    string DurationNs, string OverlapDurationNs, int Level, uint ProcessId, uint? ThreadId, string? ThreadName,
    string? QueueId, string? QueueName, string? ExecutionNs = null, string? StallNs = null, string? ExecutionTimingState = null);
public sealed record TimingEventsDto(string Handle, TimingRangeDto Provenance, PageResult<RecordedTimingEventDto> Events,
    TimingCapabilityDto CpuExecutionTiming,
    IReadOnlyList<ToolCallDto> NextCalls);
public sealed record TimingCounterDto(string CounterId, string? Name, IReadOnlyList<string> GroupPath,
    string? Description, string? Units, uint? ProcessId);
public sealed record TimingCountersDto(string Handle, PageResult<TimingCounterDto> Counters, IReadOnlyList<ToolCallDto> NextCalls);
public sealed record TimingCounterSampleDto(string TimestampNs, double Value);
public sealed record TimingCounterSamplesDto(string Handle, TimingRangeDto Provenance, TimingCounterDto Counter,
    PageResult<TimingCounterSampleDto> Samples, IReadOnlyList<ToolCallDto> NextCalls);

public sealed record TimingSampleSelection(uint? ProcessId, uint? ThreadId);
public sealed record TimingSampleCoverageDto(long TotalSamples, long SamplesWithStacks, long SamplesWithoutStacks,
    long InvalidStackSamples, long SamplesWithUnresolvedFrames, long ResolvedFrames, long UnresolvedFrames,
    string StackState = "available", string SymbolState = "available",
    string Metric = "sampleCount", string PercentageDenominator = "all selected CPU samples",
    string StackOrder = "callerToCallee");
public sealed record TimingFunctionDto(string Key, string Address, string? Module, string? ModuleId,
    string? Function, string? FunctionOffset, string SymbolState, string? SourceFile, int? SourceLine);
public sealed record TimingHotspotDto(TimingFunctionDto Function, long InclusiveSamples, long ExclusiveSamples,
    double InclusivePercent, double ExclusivePercent);
public sealed record TimingCallNodeDto(string NodeId, string? ParentNodeId, TimingFunctionDto? Function,
    long InclusiveSamples, long ExclusiveSamples, double Percent, int ChildCount);
public sealed record TimingSampleAnalysisDto(string Handle, TimingRangeDto Provenance, TimingSampleSelection Selection,
    TimingSampleCoverageDto Coverage, IReadOnlyList<TimingHotspotDto> Hotspots, IReadOnlyList<TimingCallNodeDto> Nodes);
public sealed record TimingHotspotsDto(string Handle, string ProfileRef, TimingRangeDto Provenance,
    TimingSampleCoverageDto Coverage, PageResult<TimingHotspotDto> Hotspots, IReadOnlyList<ToolCallDto> NextCalls);
public sealed record TimingCalltreeDto(string Handle, string ProfileRef, TimingRangeDto Provenance,
    TimingSampleCoverageDto Coverage, TimingCallNodeDto Parent, PageResult<TimingCallNodeDto> Children,
    IReadOnlyList<ToolCallDto> NextCalls);
