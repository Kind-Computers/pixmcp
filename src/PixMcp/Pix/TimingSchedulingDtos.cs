namespace PixMcp.Pix;

public sealed record TimingSubmissionDto(string SubmissionId, string SubmissionRef, string? QueueId, string? QueueName,
    string? ThreadRowId, uint? ProcessId, uint? ThreadId, string? ThreadName, string? SubmitNs,
    string? GpuBeginNs, string? GpuEndNs, string? LatencyNs, string? GpuDurationNs,
    TimingCapabilityDto GpuTiming, TimingCapabilityDto ThreadCorrelation, IReadOnlyList<ToolCallDto> NextCalls);
public sealed record TimingSubmissionsDto(string Handle, TimingRangeDto Provenance, PageResult<TimingSubmissionDto> Submissions,
    IReadOnlyList<ToolCallDto> NextCalls, string Selection = "submission timestamp in [start,end)",
    string Correlation = "recorded queue submission to GPU execution; not individual draws or named markers");

public sealed record TimingRecordedStackDto(string State, IReadOnlyList<TimingFunctionDto> Frames,
    string SymbolState, string? Reason = null, string StackOrder = "leafToCaller");
public sealed record TimingThreadSwitchDto(string TimestampNs, string Direction, int Core,
    uint PeerProcessId, uint PeerThreadId, int? WaitReasonCode, TimingRecordedStackDto Stack);
public sealed record TimingThreadSwitchesDto(string Handle, TimingRangeDto Provenance, TimingThreadDto Thread,
    string ThreadStartNs, string? ThreadEndNs, PageResult<TimingThreadSwitchDto> Switches, IReadOnlyList<ToolCallDto> NextCalls,
    string Interpretation = "recorded scheduling transitions; wait reason belongs to switch-out and does not identify a waited-on object or blocked duration");
