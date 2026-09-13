namespace PixMcp.Pix;

public sealed record PageResult<T>(long Total, int Offset, int Count, int? NextOffset,
    IReadOnlyList<T> Items, object? Extra);

public sealed record EventDto(int QueueIndex, uint Index, uint? GpuId, uint? ParentIndex,
    string Name, string? ApiCallData, uint CommandListId, string? Color)
{
    public EventRef? EventRef { get; init; }
    public IReadOnlyList<string>? MarkerPath { get; init; }
}

/// <summary>
/// Job status snapshot. <c>resultState</c> is available (read <c>resultRef</c>), evicted (storage pressure removed the
/// result), retentionFailed (the job succeeded but its result could not be retained; see <c>resultError</c>) or none.
/// <c>origin</c> is the tool call that started the job, repeated in <c>nextCalls</c> when the result must be recomputed.
/// </summary>
public sealed record JobDto(string JobId, string Kind, string Description, string Status, float Progress,
    DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, double? ElapsedSeconds,
    IReadOnlyList<string> Messages, ErrorDto? Error, string? ResultRef, bool CancellationRequested,
    IReadOnlyList<ToolCallDto> NextCalls, string ResultState = "none", ErrorDto? ResultError = null, ToolCallDto? Origin = null, string? PartialResultRef = null);

/// <summary>An executable follow-up call. Cost is a best-effort hint: cached, query, replay, pixtool or job.</summary>
public sealed record ToolCallDto(string Tool, object Arguments, string? Cost = null);
public sealed record ErrorDto(string Code, string Message, string? Hresult, bool Retryable, IReadOnlyList<ToolCallDto> NextCalls);
public sealed record DeferredResultDto(bool Deferred, string ResultRef, int TotalBytes, IReadOnlyList<ToolCallDto> NextCalls);
/// <summary>A window of a retained result. <c>projection</c> is set when fields/where shaped an array read.</summary>
public sealed record ResultReadDto(string ResultRef, string Pointer, string Kind, int Total, int Offset,
    int Count, int? NextOffset, object? Value, IReadOnlyList<ToolCallDto> NextCalls)
{
    public ProjectionDto? Projection { get; init; }
}
/// <summary>One child of an outlined container: its kind, element count, byte size, whether a values read would defer it, and a sample of its keys.</summary>
public sealed record ResultOutlineEntry(string Key, string Kind, int Total, long Bytes, bool Deferred, IReadOnlyList<string>? ItemKeys);
/// <summary>The shape of a retained value without its contents (pix_result_read mode=outline).</summary>
public sealed record ResultOutlineDto(string ResultRef, string Pointer, string Kind, int Total, long Bytes, int Offset, int Count,
    int? NextOffset, IReadOnlyList<ResultOutlineEntry> Entries, IReadOnlyList<ToolCallDto> NextCalls);

/// <summary>
/// Returned by query tools whose prerequisite (GPU analysis, timing, a counter set) is still being
/// prepared by a job when the inline wait elapses. Wait for <see cref="JobId"/> with pix_job_wait,
/// then repeat the call named by <see cref="Retry"/> with the same arguments.
/// </summary>
public sealed record PendingDto(bool Pending, string JobId, string Retry, string Message, JobDto Job,
    IReadOnlyList<ToolCallDto>? NextCalls = null);

/// <summary>
/// One section of a response whose prerequisite job is still running while the rest of the response (metadata that
/// needs no replay) is already filled in. Wait for <see cref="JobId"/>, then repeat the call named by <see cref="Retry"/>.
/// </summary>
public sealed record PendingSectionDto(bool Pending, string JobId, string Retry, string Message, JobDto Job,
    IReadOnlyList<ToolCallDto> NextCalls);
