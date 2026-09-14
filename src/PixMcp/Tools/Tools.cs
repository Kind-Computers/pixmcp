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
internal sealed record Preparation<T>(string Key, string Kind, string Description, Func<T, bool> IsReady, Action<T, Job> Prepare) where T : PixHandle
{
    /// <summary>
    /// Keys of running jobs that make progress toward this preparation (a superset, or a job that starts the same
    /// replay). A caller joins such a job instead of queueing a second replay behind it; when it finishes and the
    /// state is still missing, the caller starts this preparation itself.
    /// </summary>
    public IReadOnlyList<string> JoinKeys { get; init; } = [];
    /// <summary>The canonical job payload (e.g. the analysis status or timing summary), identical whoever started the job.</summary>
    public Func<T, object?>? Result { get; init; }
}

/// <summary>Preparation keys shared between the tools that start replays and the tools that join them.</summary>
internal static class PreparationKeys
{
    public const string Analysis = "analysis", Timing = "timing", AccessedResources = "accessed-resources";
    /// <summary>pix_gpu_inspect_event's shared preparation: analysis, timing and accessed resources in one job.</summary>
    public const string Inspection = "inspection";
    /// <summary>pix_gpu_occupancy's preparation: timing first, then the timing pass's occupancy or a standalone replay.</summary>
    public const string Occupancy = "occupancy";
    /// <summary>Every job that starts analysis as its first step.</summary>
    public static readonly string[] StartingAnalysis = [Timing, AccessedResources, Inspection, Occupancy];
    public static readonly string[] CollectingTiming = [Inspection, Occupancy];
}

/// <summary>Shared plumbing for tool implementations.</summary>
internal static class Tools
{
    /// <summary>Default inline wait for tools that may first have to start analysis or collect data.</summary>
    public const double DefaultReadyWaitSeconds = 2;

    public const string IncludeProvenanceDescription = "Repeat the full provenance block even if this handle already returned it (default false: a repeated block is replaced by { provenanceRef, unchanged }).";

    public const string WaitSecondsDescription = "Seconds to wait inline for the job to finish before returning (default 0 = return the job immediately; poll pix_job_status or block with pix_job_wait).";
    public const string ReadyWaitDescription = "Seconds to wait for worker admission and prerequisite preparation (default 2; 0 admits only an idle worker). " +
        "Preparation still running returns pending/jobId; an occupied worker returns retryable worker_busy. Follow nextCalls. " +
        "Once the preparation has finished the query is admitted with at least one extra second, so a ready prerequisite is not lost to a busy queue. " +
        "Once a native query starts its execution time is outside this budget.";

    /// <summary>Admission budget kept for the query after its preparation finished, even when the caller's wait is exhausted.</summary>
    public const double PostPreparationAdmissionSeconds = 1.0;

    /// <summary>The queue-admission budget: the caller's remaining wait, raised to <see cref="PostPreparationAdmissionSeconds"/> after a preparation.</summary>
    internal static TimeSpan AdmissionBudget(TimeSpan remaining, bool afterPreparation)
        => afterPreparation && remaining < TimeSpan.FromSeconds(PostPreparationAdmissionSeconds) ? TimeSpan.FromSeconds(PostPreparationAdmissionSeconds) : remaining;

    /// <summary>Hard upper bound on UTF-8 JSON bytes (PIXMCP_MAX_RESULT_BYTES). Normal responses target the inline budget; snapshots preserve the complete result.</summary>
    public static int MaxResultBytes => ServerOptions.Current.MaxResultBytes;

    /// <summary>Serializes a tool result, preserving large payloads in a managed snapshot when a session is available.</summary>
    public static string Serialize(object? value, string context, PixSession? session = null, string? owner = null)
    {
        string json = Json.Serialize(value);
        int bytes = Encoding.UTF8.GetByteCount(json);
        if (session is not null && bytes > ResultStore.TargetBytes)
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            string resultRef = session.Results.StoreElement(doc.RootElement, owner is null ? StructuredToolResults.CurrentOwners() : [owner], operation: context);
            json = Json.Serialize(new DeferredResultDto(true, resultRef, bytes, ResultStore.DeferredCalls(resultRef)));
        }
        EnsureByteBudget(json, context);
        return json;
    }

    internal static void EnsureByteBudget(string json, string context)
    {
        int bytes = Encoding.UTF8.GetByteCount(json);
        if (bytes > MaxResultBytes)
            throw new PixToolException(PixErrors.Codes.ResultTooLarge, $"{context}: response is {bytes:N0} UTF-8 bytes, above {ServerOptions.MaxVariable}={MaxResultBytes:N0}. Request a smaller window.");
    }

    /// <summary>Reads an optional value; a failure becomes an { unavailable, feature, reason } marker instead of a silent null.</summary>
    public static object? Try<T>(Func<T?> read, string feature)
    {
        try { return read(); }
        catch (Exception ex) { return PixErrors.Unavailable(feature, ex); }
    }

    /// <summary>Runs <paramref name="work"/> on the PIX worker thread and serializes the result; PIX errors become McpExceptions.</summary>
    public static Task<string> Run(PixSession session, string context, Func<object?> work, CancellationToken cancellationToken = default)
        => PixErrors.Guard(context, async () => Serialize(await session.Run(work, cancellationToken, context).ConfigureAwait(false), context, session), session);

    /// <summary>Starts a job and returns its status, waiting inline up to <paramref name="waitSeconds"/>; failures to start become McpExceptions.</summary>
    public static Task<string> RunJob(JobManager jobs, string context, Func<Job> start, double waitSeconds, CancellationToken cancellationToken)
        => PixErrors.Guard(context, async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!double.IsFinite(waitSeconds) || waitSeconds is < 0 or > 3600)
                throw PixErrors.InvalidArguments("waitSeconds must be finite and between 0 and 3600.");
            return Serialize(await jobs.WaitOrStatus(start(), waitSeconds, cancellationToken).ConfigureAwait(false), context);
        }, jobs.Session);

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
        => RunWhenReadyOrPartial(session, jobs, tool, handle, preparation, null, query, null, waitSeconds, cancellationToken);

    /// <summary>
    /// Like <see cref="RunWhenReady{T}"/>, but when the preparation is still running after <paramref name="waitSeconds"/>
    /// the response is <paramref name="metadata"/> (cheap, replay-free data) merged with a <see cref="PendingSectionDto"/>
    /// through <paramref name="merge"/>, instead of a whole-response <see cref="PendingDto"/>. The metadata callback
    /// receives <c>onWorker=true</c> when it runs inside the admitted probe (it may touch PIX) and <c>false</c> when the
    /// call joins a running job (it must read caches only).
    /// </summary>
    public static Task<string> RunWhenReadyOrPartial<T>(
        PixSession session,
        JobManager jobs,
        string tool,
        string handle,
        Preparation<T> preparation,
        Func<T, bool, object?>? metadata,
        Func<T, object?> query,
        Func<object?, PendingSectionDto, object?>? merge,
        double waitSeconds,
        CancellationToken cancellationToken) where T : PixHandle
        => PixErrors.Guard(tool, async () =>
        {
            if (!double.IsFinite(waitSeconds) || waitSeconds is < 0 or > 3600)
                throw PixErrors.InvalidArguments("waitSeconds must be finite and between 0 and 3600.");
            long started = Stopwatch.GetTimestamp();
            TimeSpan Remaining() => TimeSpan.FromSeconds(Math.Max(0, waitSeconds - Stopwatch.GetElapsedTime(started).TotalSeconds));
            ToolCallDto Retry() => new(tool, StructuredToolResults.CurrentArguments() ?? new { handle }, CostHints.Cached);
            async Task<TResult> Admit<TResult>(Func<TResult> work, bool afterPreparation = false)
            {
                try { return await session.Worker.RunWithAdmission(work, AdmissionBudget(Remaining(), afterPreparation), cancellationToken, tool).ConfigureAwait(false); }
                catch (PixToolException ex) when (ex.Detail.Code == PixErrors.Codes.WorkerBusy)
                {
                    var next = new List<ToolCallDto>();
                    if (jobs.Running is Job active) next.Add(new("pix_job_wait", new { jobId = active.Id, timeoutSeconds = 2 }, CostHints.Job));
                    next.Add(Retry());
                    WorkerSnapshot worker = session.Worker.Snapshot();
                    throw new PixToolException(ex.Detail with
                    {
                        Message = worker.Operation is null ? ex.Detail.Message
                            : $"The PIX worker is running '{worker.Operation}'. Retry when it finishes.",
                        NextCalls = next,
                    });
                }
            }
            // Joining a running preparation (or a related job that makes progress toward it) must not queue a
            // readiness probe behind that very replay.
            T current = session.Get<T>(handle);
            Job? existing = FindPreparation(current, preparation);
            bool ready = false;
            object? result = null, partial = null;
            bool partialFromCache = false;
            if (existing is not { IsFinished: false })
            {
                (ready, result, existing, partial) = await Admit<(bool, object?, Job?, object?)>(() =>
                {
                    T h = session.Get<T>(handle);
                    if (preparation.IsReady(h)) return (true, query(h), null, null);
                    return (false, null, FindPreparation(h, preparation), metadata?.Invoke(h, true));
                }).ConfigureAwait(false);
            }
            else partialFromCache = true;
            if (ready)
            {
                return Serialize(result, tool, session, handle);
            }

            Job job = existing is { IsFinished: false } ? existing : StartPreparation(session, jobs, handle, preparation);
            // Round 0 may be a joined job that only makes progress; round 1 is this preparation's own job.
            for (int round = 0; ; round++)
            {
                if (waitSeconds > 0 && !job.IsFinished)
                {
                    try { await job.WaitAsync(Remaining(), cancellationToken).ConfigureAwait(false); }
                    catch (TimeoutException) { }
                }
                if (!job.IsFinished)
                {
                    string message = $"{job.Description} is still running as {job.Id}. Wait for it with pix_job_wait, then call {tool} again with the same arguments.";
                    ToolCallDto[] next =
                    [
                        new("pix_job_wait", new { jobId = job.Id, timeoutSeconds = 2 }, CostHints.Job),
                        Retry(),
                    ];
                    if (metadata is not null && merge is not null)
                    {
                        if (partialFromCache) partial = metadata(current, false);
                        object? merged = partial is null ? null : merge(partial, new PendingSectionDto(true, job.Id, tool, message, job.ToDto(), next));
                        if (merged is not null) return Serialize(merged, tool, session, handle);
                    }
                    return Json.Serialize(new PendingDto(true, job.Id, tool, message, job.ToDto(), next));
                }
                if (job.Status != JobStatus.Succeeded)
                {
                    ErrorDto detail = job.ErrorDetail ?? new(PixErrors.Codes.PreparationFailed, job.Error ?? "No details", null, false, []);
                    throw new PixToolException(detail with { Message = $"{tool}: {job.Description} {job.Status.ToString().ToLowerInvariant()} ({job.Id}): {detail.Message}" });
                }

                (ready, result) = await Admit<(bool, object?)>(() =>
                {
                    T h = session.Get<T>(handle);
                    return preparation.IsReady(h) ? (true, query(h)) : (false, null);
                }, afterPreparation: true).ConfigureAwait(false);
                if (ready) return Serialize(result, tool, session, handle);
                bool own = current.PreparationJobs.TryGetValue(preparation.Key, out Job? registered) && ReferenceEquals(registered, job);
                if (round == 0 && !own)
                {
                    job = StartPreparation(session, jobs, handle, preparation);
                    partialFromCache = true;
                    continue;
                }
                throw PixErrors.PreparationUnavailable(tool, preparation.Description, Retry());
            }
        }, session);

    /// <summary>The running job preparing <paramref name="preparation"/> for the handle: its own key first, then any related key it may join.</summary>
    internal static Job? FindPreparation<T>(T handle, Preparation<T> preparation) where T : PixHandle
    {
        if (handle.PreparationJobs.TryGetValue(preparation.Key, out Job? own) && !own.IsFinished) return own;
        foreach (string key in preparation.JoinKeys)
            if (handle.PreparationJobs.TryGetValue(key, out Job? related) && !related.IsFinished) return related;
        return null;
    }

    /// <summary>
    /// One preparation made of several: ready when every part is ready; its job runs each missing part in order. Callers join a
    /// running job of any part (or of a part's own join keys) before starting it.
    /// </summary>
    internal static Preparation<T> Combine<T>(string handle, IReadOnlyList<Preparation<T>> parts) where T : PixHandle
    {
        if (parts.Count == 1) return parts[0];
        return new Preparation<T>(string.Join("+", parts.Select(p => p.Key)), parts[0].Kind,
            $"Prepare {string.Join(", ", parts.Select(p => p.Key))} for {handle}",
            h => parts.All(p => p.IsReady(h)),
            (h, job) =>
            {
                foreach (Preparation<T> part in parts)
                    if (!part.IsReady(h)) part.Prepare(h, job);
            })
        { JoinKeys = parts.SelectMany(p => p.JoinKeys.Prepend(p.Key)).Distinct().ToArray() };
    }

    /// <summary>
    /// Starts the preparation job for a handle, or returns the running job it can join (same key, or a related key that
    /// makes progress toward it). Find-or-start runs under the handle's preparation gate, so parallel callers get one job.
    /// </summary>
    public static Job StartPreparation<T>(PixSession session, JobManager jobs, string handle, Preparation<T> preparation) where T : PixHandle
    {
        T current = session.Get<T>(handle);
        lock (current.PreparationGate)
        {
            if (FindPreparation(current, preparation) is { IsFinished: false } running) return running;
            // A close racing with this call must not register a job on a forgotten handle.
            session.Get<T>(handle);
            Job job = jobs.StartForHandle<T>(preparation.Kind, preparation.Description, handle, (j, h) =>
            {
                preparation.Prepare(h, j);
                return preparation.Result?.Invoke(h) ?? new { handle = h.Id, prepared = preparation.Key };
            });
            current.PreparationJobs[preparation.Key] = job;
            return job;
        }
    }

    /// <summary>
    /// Validates a user-supplied output path: never inside the private result storage, parent directory must exist,
    /// and an existing file is refused (file_exists) unless <paramref name="overwrite"/>. Returns the full path.
    /// </summary>
    public static string PrepareOutputPath(string? outPath, bool overwrite, ResultStore? results)
    {
        if (string.IsNullOrWhiteSpace(outPath)) throw PixErrors.InvalidArguments("outPath is required.");
        string full = ServerPaths.Full(outPath);
        if (results is not null && results.IsPrivatePath(full))
            throw PixErrors.InvalidArguments("Output cannot replace files in the server's private result storage.");
        string? directory = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) throw PixErrors.DirectoryNotFound(directory ?? full);
        if (!overwrite && File.Exists(full)) throw PixErrors.FileExists(full);
        return full;
    }

    public static string RequireFile(string path, string what)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw PixErrors.InvalidArguments($"A {what} path is required.");
        }
        string full = ServerPaths.Full(path);
        if (!File.Exists(full))
        {
            throw PixErrors.FileNotFound(what, full);
        }
        return full;
    }

    public static bool Contains(string haystack, string? needle)
        => string.IsNullOrEmpty(needle) || haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static readonly string[] WorkPrefixes = { "Draw", "Dispatch", "ExecuteIndirect" };
    private static readonly string[] MarkerPrefixes = { "PIXBeginEvent", "BeginEvent", "PIXSetMarker", "SetMarker" };
    private static readonly Dictionary<string, string[]> KindPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["work"] = WorkPrefixes,
        ["draw"] = new[] { "Draw" },
        ["dispatch"] = new[] { "Dispatch" },
        ["executeIndirect"] = new[] { "ExecuteIndirect" },
        ["copy"] = new[] { "Copy" },
        ["clear"] = new[] { "Clear" },
        ["resolve"] = new[] { "Resolve" },
        ["barrier"] = new[] { "ResourceBarrier", "Barrier" },
        ["present"] = new[] { "Present" },
        ["marker"] = MarkerPrefixes,
        ["label"] = Array.Empty<string>(),
    };

    /// <summary>API-call kinds in classification order; an event matching one of these never reads as a marker or label.</summary>
    private static readonly string[] ApiKinds = { "draw", "dispatch", "executeIndirect", "copy", "clear", "resolve", "barrier", "present" };

    /// <summary>Every accepted kind name, in the order tools document them.</summary>
    public static readonly string[] Kinds = { "work", "draw", "dispatch", "executeIndirect", "copy", "clear", "resolve", "barrier", "present", "marker", "label" };

    public const string KindDescription = "Optional event kind filter: work (draw, dispatch and executeIndirect together), draw, dispatch, executeIndirect, copy, clear, resolve, barrier, present, marker, label. API calls match case-insensitive name prefixes. marker is a PIX event or a native label with no API call text that has children or no GPU id; label is a timed leaf label (GPU id, no children) such as a SetMarker.";

    private static PixToolException UnknownKind(string kind)
        => new("invalid_arguments", $"Unknown kind '{kind}'. Valid kinds: {string.Join(", ", Kinds)}.");

    private static string CanonicalKind(string kind)
    {
        string trimmed = kind.Trim();
        return Kinds.FirstOrDefault(k => k.Equals(trimmed, StringComparison.OrdinalIgnoreCase)) ?? throw UnknownKind(kind);
    }

    /// <summary>Rejects an unknown kind before any expensive work; blank means "no filter".</summary>
    public static void ValidateKind(string? kind)
    {
        if (!string.IsNullOrWhiteSpace(kind)) CanonicalKind(kind);
    }

    private static bool MatchesPrefix(EventRecord e, string[] prefixes)
    {
        foreach (string prefix in prefixes)
        {
            if (e.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || e.ApiCallData.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>An event that carries API call text, an API-shaped name such as "Foo(1, 2)", or a known API prefix.</summary>
    private static bool IsApiShaped(EventRecord e)
    {
        if (!string.IsNullOrEmpty(e.ApiCallData) || e.Name.EndsWith(')')) return true;
        foreach (string kind in ApiKinds)
            if (MatchesPrefix(e, KindPrefixes[kind])) return true;
        return false;
    }

    /// <summary>
    /// A PIX marker: an explicit BeginEvent/SetMarker call, or a native label with no API call text. When the caller
    /// knows whether the event has children, a childless label with a GPU id is a timed <c>label</c>, not a marker.
    /// </summary>
    public static bool IsMarker(EventRecord e, bool? hasChildren = null)
        => MatchesPrefix(e, MarkerPrefixes) || (!IsApiShaped(e) && (e.GpuId == uint.MaxValue || hasChildren != false));

    /// <summary>A timed leaf label: no API call text, a GPU id, and (known to have) no children.</summary>
    public static bool IsLabel(EventRecord e, bool? hasChildren = null)
        => !MatchesPrefix(e, MarkerPrefixes) && !IsApiShaped(e) && e.GpuId != uint.MaxValue && hasChildren == false;

    /// <summary>True when the event is of <paramref name="kind"/>; blank kinds match everything; unknown kinds are invalid_arguments.</summary>
    public static bool MatchesKind(EventRecord e, string? kind, bool? hasChildren = null)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return true;
        }
        return CanonicalKind(kind) switch
        {
            "marker" => IsMarker(e, hasChildren),
            "label" => IsLabel(e, hasChildren),
            var canonical => MatchesPrefix(e, KindPrefixes[canonical]),
        };
    }

    /// <summary>The single kind bucket of an event: API kinds first, then marker, label, or other.</summary>
    public static string Classify(EventRecord e, bool? hasChildren = null)
    {
        foreach (string kind in ApiKinds)
            if (MatchesPrefix(e, KindPrefixes[kind])) return kind;
        if (IsMarker(e, hasChildren)) return "marker";
        if (IsLabel(e, hasChildren)) return "label";
        return "other";
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
        ValidateKind(kind);
        EventRecord[] all = events as EventRecord[] ?? events.ToArray();
        HashSet<uint>? parents = string.IsNullOrWhiteSpace(kind) ? null : EventNavigation.ParentSet(all);
        foreach (EventRecord e in all)
        {
            if (!Contains(e.Name, nameContains)) continue;
            if (!string.IsNullOrEmpty(nameStartsWith) && !e.Name.StartsWith(nameStartsWith, StringComparison.OrdinalIgnoreCase)) continue;
            if (!Contains(e.ApiCallData, apiCallContains)) continue;
            if (parents is not null && !MatchesKind(e, kind, parents.Contains(e.Index))) continue;
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
        throw PixErrors.InvalidArguments($"Invalid {what} '{value}'; expected a decimal or 0x-prefixed hex number.");
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
        throw PixErrors.InvalidArguments($"Unknown {typeof(T).Name} value '{value}'. Valid values: {string.Join(", ", Enum.GetValues<T>().Select(m => Json.EnumName(m)))}.");
    }
}
