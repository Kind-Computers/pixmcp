using ModelContextProtocol;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using PixMcp.Pix;
using PixMcp.Pix.Handles;

namespace PixMcp.Tools;

/// <summary>
/// Expensive per-handle state a query tool depends on (GPU analysis started, timing collected, a
/// counter set collected). <see cref="Tools.RunWhenReady{T}"/> prepares it in a job instead of
/// blocking the PIX thread inside the tool call.
/// </summary>
/// <param name="Key">Identifies the prepared state so concurrent callers share one job (e.g. "analysis").</param>
/// <param name="Kind">Job kind shown in pix_jobs.</param>
/// <param name="Description">Job description shown in pix_jobs.</param>
/// <param name="IsReady">True when the state exists on the handle (checked on the PIX thread).</param>
/// <param name="Prepare">Creates the state; runs inside the job on the PIX thread with the job for progress/cancellation.</param>
internal sealed record Preparation<T>(string Key, string Kind, string Description, Func<T, bool> IsReady, Action<T, Job> Prepare) where T : PixHandle;

/// <summary>Shared plumbing for tool implementations.</summary>
internal static class Tools
{
    /// <summary>Default inline wait for tools that may first have to start analysis or collect data.</summary>
    public const double DefaultReadyWaitSeconds = 2;

    public const string WaitSecondsDescription = "Seconds to wait inline for the job to finish before returning (default 0 = return the job immediately; poll pix_job_status or block with pix_job_wait).";
    public const string ReadyWaitDescription = "Seconds to wait for worker admission and prerequisite preparation (default 2; 0 admits only an idle worker). " +
        "Preparation still running returns pending/jobId; an occupied worker returns retryable worker_busy. Follow nextCalls. Once a native query starts its execution time is outside this budget.";

    /// <summary>Hard upper bound on UTF-8 JSON bytes. Normal responses target 32 KiB; snapshots preserve the complete result.</summary>
    public static int MaxResultBytes { get; } = int.TryParse(Environment.GetEnvironmentVariable("PIXMCP_MAX_RESULT_BYTES"), out int configured) && configured > 0 ? configured : 2 * 1024 * 1024;

    /// <summary>Serializes a tool result, preserving large payloads in a managed snapshot when a session is available.</summary>
    public static string Serialize(object? value, string context, PixSession? session = null, string? owner = null)
    {
        string json = Json.Serialize(value);
        int bytes = Encoding.UTF8.GetByteCount(json);
        if (session is not null && bytes > Math.Min(ResultStore.TargetBytes, MaxResultBytes))
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            string resultRef = session.Results.StoreElement(doc.RootElement, owner is null ? StructuredToolResults.CurrentOwners() : [owner], operation: context);
            json = Json.Serialize(new DeferredResultDto(true, resultRef, bytes, [ResultStore.ReadCall(resultRef)]));
        }
        EnsureByteBudget(json, context);
        return json;
    }

    internal static void EnsureByteBudget(string json, string context)
    {
        int bytes = Encoding.UTF8.GetByteCount(json);
        if (bytes > MaxResultBytes)
            throw new PixToolException("result_too_large", $"{context}: response is {bytes:N0} UTF-8 bytes, above PIXMCP_MAX_RESULT_BYTES={MaxResultBytes:N0}. Request a smaller window.");
    }

    /// <summary>Reads an optional value; a failure becomes an { unavailable, feature, reason } marker instead of a silent null.</summary>
    public static object? Try<T>(Func<T?> read, string feature)
    {
        try { return read(); }
        catch (Exception ex) { return PixErrors.Unavailable(feature, ex); }
    }

    /// <summary>Runs <paramref name="work"/> on the PIX worker thread and serializes the result; PIX errors become McpExceptions.</summary>
    public static Task<string> Run(PixSession session, string context, Func<object?> work, CancellationToken cancellationToken = default)
        => PixErrors.Guard(context, async () => Serialize(await session.Run(work, cancellationToken, context).ConfigureAwait(false), context, session));

    /// <summary>Starts a job and returns its status, waiting inline up to <paramref name="waitSeconds"/>; failures to start become McpExceptions.</summary>
    public static Task<string> RunJob(JobManager jobs, string context, Func<Job> start, double waitSeconds, CancellationToken cancellationToken)
        => PixErrors.Guard(context, async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!double.IsFinite(waitSeconds) || waitSeconds is < 0 or > 3600)
                throw new PixToolException("invalid_arguments", "waitSeconds must be finite and between 0 and 3600.");
            return Serialize(await jobs.WaitOrStatus(start(), waitSeconds, cancellationToken).ConfigureAwait(false), context);
        });

    /// <summary>
    /// Runs <paramref name="query"/> on the PIX thread once <paramref name="preparation"/> is satisfied.
    /// If it is not, the preparation runs as a job (reusing one already running for the same key), the
    /// call waits up to <paramref name="waitSeconds"/>, and either completes the query or returns a
    /// <see cref="PendingDto"/> telling the caller which job to wait for before retrying. This keeps a
    /// multi-minute replay from blocking the request and every other PIX tool call behind it.
    /// </summary>
    public static Task<string> RunWhenReady<T>(
        PixSession session,
        JobManager jobs,
        string tool,
        string handle,
        Preparation<T> preparation,
        Func<T, object?> query,
        double waitSeconds,
        CancellationToken cancellationToken) where T : PixHandle
        => PixErrors.Guard(tool, async () =>
        {
            if (!double.IsFinite(waitSeconds) || waitSeconds is < 0 or > 3600)
                throw new PixToolException("invalid_arguments", "waitSeconds must be finite and between 0 and 3600.");
            long started = Stopwatch.GetTimestamp();
            TimeSpan Remaining() => TimeSpan.FromSeconds(Math.Max(0, waitSeconds - Stopwatch.GetElapsedTime(started).TotalSeconds));
            async Task<TResult> Admit<TResult>(Func<TResult> work)
            {
                try { return await session.Worker.RunWithAdmission(work, Remaining(), cancellationToken, tool).ConfigureAwait(false); }
                catch (PixToolException ex) when (ex.Detail.Code == "worker_busy")
                {
                    var next = new List<ToolCallDto>();
                    if (jobs.Running is Job active) next.Add(new("pix_job_wait", new { jobId = active.Id, timeoutSeconds = 2 }));
                    next.Add(new(tool, StructuredToolResults.CurrentArguments() ?? new { handle }));
                    WorkerSnapshot worker = session.Worker.Snapshot();
                    throw new PixToolException(ex.Detail with
                    {
                        Message = worker.Operation is null ? ex.Detail.Message
                            : $"The PIX worker is running '{worker.Operation}'. Retry when it finishes.",
                        NextCalls = next,
                    });
                }
            }
            // Joining a running preparation must not queue a readiness probe behind that very replay.
            T current = session.Get<T>(handle);
            current.PreparationJobs.TryGetValue(preparation.Key, out Job? existing);
            bool ready = false;
            object? result = null;
            if (existing is not { IsFinished: false })
            {
                (ready, result, existing) = await Admit<(bool, object?, Job?)>(() =>
                {
                    T h = session.Get<T>(handle);
                    if (preparation.IsReady(h)) return (true, query(h), null);
                    h.PreparationJobs.TryGetValue(preparation.Key, out Job? running);
                    return (false, null, running);
                }).ConfigureAwait(false);
            }
            if (ready)
            {
                return Serialize(result, tool, session, handle);
            }

            Job job = existing is { IsFinished: false }
                ? existing
                : StartPreparation(session, jobs, handle, preparation);

            if (waitSeconds > 0 && !job.IsFinished)
            {
                try { await job.WaitAsync(Remaining(), cancellationToken).ConfigureAwait(false); }
                catch (TimeoutException) { }
            }
            if (!job.IsFinished)
            {
                return Json.Serialize(new PendingDto(true, job.Id, tool,
                    $"{preparation.Description} is still running as {job.Id}. Wait for it with pix_job_wait, then call {tool} again with the same arguments.",
                    job.ToDto(),
                    [new("pix_job_wait", new { jobId = job.Id, timeoutSeconds = 2 }),
                     new(tool, StructuredToolResults.CurrentArguments() ?? new { handle })]));
            }
            if (job.Status != JobStatus.Succeeded)
            {
                ErrorDto detail = job.ErrorDetail ?? new("preparation_failed", job.Error ?? "No details", null, false, []);
                throw new PixToolException(detail with { Message = $"{tool}: {preparation.Description} {job.Status.ToString().ToLowerInvariant()} ({job.Id}): {detail.Message}" });
            }

            return Serialize(await Admit(() =>
            {
                T h = session.Get<T>(handle);
                if (!preparation.IsReady(h))
                {
                    throw new McpException($"{tool}: {preparation.Description} finished but the data is no longer available (analysis was stopped or the handle changed). Retry the call.");
                }
                return query(h);
            }).ConfigureAwait(false), tool, session, handle);
        });

    /// <summary>Starts a preparation job for a handle and registers it so other callers can join it.</summary>
    public static Job StartPreparation<T>(PixSession session, JobManager jobs, string handle, Preparation<T> preparation) where T : PixHandle
    {
        T current = session.Get<T>(handle);
        lock (current.PreparationJobs)
        {
            if (current.PreparationJobs.TryGetValue(preparation.Key, out Job? running) && !running.IsFinished) return running;
            Job job = jobs.StartForHandle<T>(preparation.Kind, preparation.Description, handle, (j, h) =>
            {
                preparation.Prepare(h, j);
                return new { handle = h.Id, prepared = preparation.Key };
            });
            current.PreparationJobs[preparation.Key] = job;
            return job;
        }
    }

    /// <summary>Records <paramref name="job"/> as the job preparing <paramref name="key"/> for the handle, so query tools wait for it instead of starting another.</summary>
    public static void RegisterPreparation(PixSession session, string handle, string key, Job job)
        => session.TryGet<PixHandle>(handle)?.PreparationJobs.AddOrUpdate(key, job, (_, current) => current.IsFinished ? job : current);

    public static string RequireFile(string path, string what)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new McpException($"A {what} path is required.");
        }
        string full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw new McpException($"{what} not found: {full}");
        }
        return full;
    }

    public static bool Contains(string haystack, string? needle)
        => string.IsNullOrEmpty(needle) || haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string[]> KindPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["draw"] = new[] { "Draw" },
        ["dispatch"] = new[] { "Dispatch" },
        ["drawOrDispatch"] = new[] { "Draw", "Dispatch" },
        ["executeIndirect"] = new[] { "ExecuteIndirect" },
        ["copy"] = new[] { "Copy" },
        ["clear"] = new[] { "Clear" },
        ["barrier"] = new[] { "ResourceBarrier", "Barrier" },
        ["present"] = new[] { "Present" },
        ["marker"] = new[] { "PIXBeginEvent", "BeginEvent", "PIXSetMarker", "SetMarker" },
    };

    public const string KindDescription = "Optional event kind filter: draw, dispatch, drawOrDispatch, executeIndirect, copy, clear, barrier, present, marker. API calls match case-insensitive name prefixes; marker also recognizes native PIX labels with no API call text.";

    public static bool MatchesKind(EventRecord e, string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return true;
        }
        if (!KindPrefixes.TryGetValue(kind.Trim(), out string[]? prefixes))
        {
            throw new McpException($"Unknown kind '{kind}'. Valid kinds: {string.Join(", ", KindPrefixes.Keys)}.");
        }
        // Native PIX marker events carry their label as Name and no API call text. BeginEvent
        // commonly has no GPU ID, but SetMarker can have one, so GPU ID is not a discriminator.
        if (kind.Trim().Equals("marker", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(e.ApiCallData))
            return true;
        foreach (string prefix in prefixes)
        {
            if (e.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || e.ApiCallData.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public static IEnumerable<EventRecord> FilterEvents(
        IEnumerable<EventRecord> events,
        string? nameContains,
        string? nameStartsWith,
        string? apiCallContains,
        string? kind,
        uint? parentIndex,
        uint? gpuIdMin,
        uint? gpuIdMax)
    {
        foreach (EventRecord e in events)
        {
            if (!Contains(e.Name, nameContains)) continue;
            if (!string.IsNullOrEmpty(nameStartsWith) && !e.Name.StartsWith(nameStartsWith, StringComparison.OrdinalIgnoreCase)) continue;
            if (!Contains(e.ApiCallData, apiCallContains)) continue;
            if (!MatchesKind(e, kind)) continue;
            if (parentIndex.HasValue && e.ParentIndex != parentIndex.Value) continue;
            if (gpuIdMin.HasValue && (e.GpuId == uint.MaxValue || e.GpuId < gpuIdMin.Value)) continue;
            if (gpuIdMax.HasValue && (e.GpuId == uint.MaxValue || e.GpuId > gpuIdMax.Value)) continue;
            yield return e;
        }
    }

    public static bool HasEventFilter(string? nameContains, string? nameStartsWith, string? apiCallContains, string? kind, uint? parentIndex, uint? gpuIdMin, uint? gpuIdMax)
        => !string.IsNullOrEmpty(nameContains) || !string.IsNullOrEmpty(nameStartsWith) || !string.IsNullOrEmpty(apiCallContains)
           || !string.IsNullOrEmpty(kind) || parentIndex.HasValue || gpuIdMin.HasValue || gpuIdMax.HasValue;

    /// <summary>Parses "0x1234" or "1234".</summary>
    public static ulong ParseId(string value, string what)
    {
        string v = value.Trim();
        if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && ulong.TryParse(v[2..], System.Globalization.NumberStyles.HexNumber, null, out ulong hex))
        {
            return hex;
        }
        if (ulong.TryParse(v, out ulong dec))
        {
            return dec;
        }
        throw new McpException($"Invalid {what} '{value}'; expected a decimal or 0x-prefixed hex number.");
    }

    /// <summary>Matches an enum member by full name or by suffix (case-insensitive), e.g. "ENABLE_DEBUG_LAYER".</summary>
    public static T ParseEnum<T>(string value) where T : struct, Enum
    {
        foreach (T member in Enum.GetValues<T>())
        {
            string name = member.ToString();
            if (name.Equals(value, StringComparison.OrdinalIgnoreCase) || name.EndsWith("_" + value, StringComparison.OrdinalIgnoreCase)
                || Json.EnumName(member).Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                return member;
            }
        }
        throw new McpException($"Unknown {typeof(T).Name} value '{value}'. Valid values: {string.Join(", ", Enum.GetValues<T>().Select(m => Json.EnumName(m)))}.");
    }
}
