namespace PixMcp.Pix;

public sealed record PageResult<T>(long Total, int Offset, int Count, int? NextOffset,
    IReadOnlyList<T> Items, object? Extra);

public sealed record EventDto(int QueueIndex, uint Index, uint? GpuId, uint? ParentIndex,
    string Name, string? ApiCallData, uint CommandListId, string? Color)
{
    public EventRef? EventRef { get; init; }
    public IReadOnlyList<string>? MarkerPath { get; init; }
}

public sealed record JobDto(string JobId, string Kind, string Description, string Status, float Progress,
    DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, double? ElapsedSeconds,
    IReadOnlyList<string> Messages, ErrorDto? Error, string? ResultRef, bool CancellationRequested,
    IReadOnlyList<ToolCallDto> NextCalls);

public sealed record ToolCallDto(string Tool, object Arguments);
public sealed record ErrorDto(string Code, string Message, string? Hresult, bool Retryable, IReadOnlyList<ToolCallDto> NextCalls);
public sealed record DeferredResultDto(bool Deferred, string ResultRef, int TotalBytes, IReadOnlyList<ToolCallDto> NextCalls);
public sealed record ResultReadDto(string ResultRef, string Pointer, string Kind, int Total, int Offset,
    int Count, int? NextOffset, object? Value, IReadOnlyList<ToolCallDto> NextCalls);

/// <summary>
/// Returned by query tools whose prerequisite (GPU analysis, timing, a counter set) is still being
/// prepared by a job when the inline wait elapses. Wait for <see cref="JobId"/> with pix_job_wait,
/// then repeat the call named by <see cref="Retry"/> with the same arguments.
/// </summary>
public sealed record PendingDto(bool Pending, string JobId, string Retry, string Message, JobDto Job,
    IReadOnlyList<ToolCallDto>? NextCalls = null);
