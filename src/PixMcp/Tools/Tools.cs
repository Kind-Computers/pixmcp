using ModelContextProtocol;
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
    public const double DefaultReadyWaitSeconds = 60;

    public const string WaitSecondsDescription = "Seconds to wait inline for the job to finish before returning (default 0 = return the job immediately; poll pix_job_status or block with pix_job_wait).";
    public const string ReadyWaitDescription = "Seconds to wait if GPU analysis (or the data this tool needs) still has to be prepared first (default 60; 0 = never wait). " +
                                               "If the wait elapses the result is { pending: true, jobId }: wait for the job with pix_job_wait, then repeat this call unchanged.";

    /// <summary>Upper bound on a serialized tool result (characters); larger results fail with guidance to page or filter. Override with PIXMCP_MAX_RESULT_BYTES.</summary>
    public static int MaxResultChars { get; } = int.TryParse(Environment.GetEnvironmentVariable("PIXMCP_MAX_RESULT_BYTES"), out int configured) && configured > 0 ? configured : 2 * 1024 * 1024;

    /// <summary>Serializes a tool result, refusing oversized payloads so a client never receives megabytes of JSON it cannot use.</summary>
    public static string Serialize(object? value, string context)
    {
        string json = Json.Serialize(value);
        if (json.Length > MaxResultChars)
        {
            throw new McpException($"{context}: the result is {json.Length:N0} characters, above the {MaxResultChars:N0} limit. Request less at once: use offset/limit, a filter such as nameContains, or a smaller max* argument (PIXMCP_MAX_RESULT_BYTES raises the limit).");
        }
        return json;
    }

    /// <summary>Reads an optional value; a failure becomes an { unavailable, feature, reason } marker instead of a silent null.</summary>
    public static object? Try<T>(Func<T?> read, string feature)
    {
        try { return read(); }
        catch (Exception ex) { return PixErrors.Unavailable(feature, ex); }
    }

    /// <summary>Runs <paramref name="work"/> on the PIX worker thread and serializes the result; PIX errors become McpExceptions.</summary>
    public static Task<string> Run(PixSession session, string context, Func<object?> work, CancellationToken cancellationToken = default)
        => PixErrors.Guard(context, async () => Serialize(await session.Run(work, cancellationToken).ConfigureAwait(false), context));

    /// <summary>Starts a job and returns its status, waiting inline up to <paramref name="waitSeconds"/>; failures to start become McpExceptions.</summary>
    public static Task<string> RunJob(JobManager jobs, string context, Func<Job> start, double waitSeconds, CancellationToken cancellationToken)
        => PixErrors.Guard(context, async () => Serialize(await jobs.WaitOrStatus(start(), waitSeconds, cancellationToken).ConfigureAwait(false), context));

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
            (bool ready, object? result, Job? existing) = await session.Run<(bool, object?, Job?)>(() =>
            {
                T h = session.Get<T>(handle);
                if (preparation.IsReady(h))
                {
                    return (true, query(h), null);
                }
                h.PreparationJobs.TryGetValue(preparation.Key, out Job? running);
                return (false, null, running);
            }, cancellationToken).ConfigureAwait(false);
            if (ready)
            {
                return Serialize(result, tool);
            }

            Job job = existing is { IsFinished: false }
                ? existing
                : StartPreparation(session, jobs, handle, preparation);

            if (waitSeconds > 0 && !job.IsFinished)
            {
                try { await job.WaitAsync(TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 0, 3600)), cancellationToken).ConfigureAwait(false); }
                catch (TimeoutException) { }
            }
            if (!job.IsFinished)
            {
                return Json.Serialize(new PendingDto(true, job.Id, tool,
                    $"{preparation.Description} is still running as {job.Id}. Wait for it with pix_job_wait, then call {tool} again with the same arguments.",
                    job.ToDto(includeResult: false)));
            }
            if (job.Status != JobStatus.Succeeded)
            {
                throw new McpException($"{tool}: {preparation.Description} {job.Status.ToString().ToLowerInvariant()} ({job.Id}): {job.Error ?? "no details"}");
            }

            return Serialize(await session.Run(() =>
            {
                T h = session.Get<T>(handle);
                if (!preparation.IsReady(h))
                {
                    throw new McpException($"{tool}: {preparation.Description} finished but the data is no longer available (analysis was stopped or the handle changed). Retry the call.");
                }
                return query(h);
            }, cancellationToken).ConfigureAwait(false), tool);
        });

    /// <summary>Starts a preparation job for a handle and registers it so other callers can join it.</summary>
    public static Job StartPreparation<T>(PixSession session, JobManager jobs, string handle, Preparation<T> preparation) where T : PixHandle
    {
        Job job = jobs.StartForHandle<T>(preparation.Kind, preparation.Description, handle, (j, h) =>
        {
            preparation.Prepare(h, j);
            return new { handle = h.Id, prepared = preparation.Key };
        });
        RegisterPreparation(session, handle, preparation.Key, job);
        return job;
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

    public const string KindDescription = "Optional event kind filter: draw, dispatch, drawOrDispatch, executeIndirect, copy, clear, barrier, present, marker (matched case-insensitively against the start of the event name or API call).";

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
