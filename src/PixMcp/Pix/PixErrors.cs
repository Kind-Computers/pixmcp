using System.Runtime.InteropServices;
using ModelContextProtocol;

namespace PixMcp.Pix;

/// <summary>Structured failure. JSON in Message also survives SDK paths that flatten exceptions into text content.</summary>
public sealed class PixToolException : McpException
{
    public ErrorDto Detail { get; }
    public PixToolException(ErrorDto detail) : base(Json.Serialize(detail)) => Detail = detail;
    public PixToolException(string code, string message, bool retryable = false,
        IReadOnlyList<ToolCallDto>? nextCalls = null, int? hresult = null)
        : base(Json.Serialize(new ErrorDto(code, message, hresult.HasValue ? PixErrors.Hex(hresult.Value) : null, retryable, nextCalls ?? [])))
        => Detail = new(code, message, hresult.HasValue ? PixErrors.Hex(hresult.Value) : null, retryable, nextCalls ?? []);
}

public static class PixErrors
{
    /// <summary>
    /// Stable error codes. Every tool failure carries one of these; the factories below attach the recovery calls.
    /// <see cref="All"/> feeds the README table and the source-convention tests.
    /// </summary>
    public static class Codes
    {
        public const string InvalidArguments = "invalid_arguments";
        public const string InvalidReference = "invalid_reference";
        public const string InvalidPointer = "invalid_pointer";
        public const string UnknownHandle = "unknown_handle";
        public const string WrongHandleKind = "wrong_handle_kind";
        public const string UnknownJob = "unknown_job";
        public const string JobAlreadyFinished = "job_already_finished";
        public const string UnknownCounter = "unknown_counter";
        public const string ResultExpired = "result_expired";
        public const string ResultCapacityExceeded = "result_capacity_exceeded";
        public const string ResultTooLarge = "result_too_large";
        public const string WorkerBusy = "worker_busy";
        public const string ServerShuttingDown = "server_shutting_down";
        public const string AnalysisRequired = "analysis_required";
        public const string AnalysisActive = "analysis_active";
        public const string AnalysisSettingsConflict = "analysis_settings_conflict";
        public const string AnalysisIncompatible = "analysis_incompatible";
        public const string PreparationFailed = "preparation_failed";
        public const string PreparationUnavailable = "preparation_unavailable";
        public const string DeveloperModeRequired = "developer_mode_required";
        public const string UnsupportedFeature = "unsupported_feature";
        public const string UnavailableShaderData = "unavailable_shader_data";
        public const string InvalidState = "invalid_state";
        public const string PixUnavailable = "pix_unavailable";
        public const string PixError = "pix_error";
        public const string PixApiMismatch = "pix_api_mismatch";
        public const string ToolDisabled = "tool_disabled";
        public const string Cancelled = "cancelled";
        public const string Timeout = "timeout";
        public const string FileNotFound = "file_not_found";
        public const string FileExists = "file_exists";
        public const string OutputExists = "output_exists";
        public const string DirectoryNotFound = "directory_not_found";
        public const string CounterReadFailed = "counter_read_failed";
        public const string ImageTooLarge = "image_too_large";
        public const string InvalidImage = "invalid_image";
        public const string ArtifactExpired = "artifact_expired";
        public const string AmbiguousMarker = "ambiguous_marker";
        public const string UnsupportedSelection = "unsupported_selection";
        public const string PreviewMissingOutput = "preview_missing_output";
        public const string PreviewInvalidOutput = "preview_invalid_output";
        public const string PreviewTooLarge = "preview_too_large";
        public const string ExportMissingOutput = "export_missing_output";
        public const string PixToolUnavailable = "pixtool_unavailable";
        public const string PixToolStartFailed = "pixtool_start_failed";
        public const string PixToolTimeout = "pixtool_timeout";
        public const string PixToolFailed = "pixtool_failed";
        public const string GlobalIdMismatch = "global_id_mismatch";
        public const string SubcaptureMissingOutput = "subcapture_missing_output";
        public const string SubcaptureInvalidOutput = "subcapture_invalid_output";
        public const string BlobWindowTooLarge = "blob_window_too_large";
        public const string CsvFileNotFound = "csv_file_not_found";
        public const string CsvPassNotFound = "csv_pass_not_found";
        public const string PixdiffUnavailable = "pixdiff_unavailable";
        public const string PixdiffStartFailed = "pixdiff_start_failed";
        public const string PixdiffTimeout = "pixdiff_timeout";
        public const string PixdiffFailed = "pixdiff_failed";
        public const string PixdiffOutputTooLarge = "pixdiff_output_too_large";
        public const string CaptureTargetChanged = "capture_target_changed";
        public const string CaptureTargetUnsupported = "capture_target_unsupported";
        public const string CaptureTargetTerminated = "capture_target_terminated";
        public const string CaptureTargetNotReady = "capture_target_not_ready";
        public const string CaptureNotRunning = "capture_not_running";
        public const string CaptureFinalizationTimeout = "capture_finalization_timeout";
        public const string TimingSqlUnavailable = "timing_sql_unavailable";
        public const string TimingThreadLifetimeUnavailable = "timing_thread_lifetime_unavailable";
        public const string TimingSchemaUnsupported = "timing_schema_unsupported";
        public const string TimingRangeUnavailable = "timing_range_unavailable";
        public const string TimingQueryTimeout = "timing_query_timeout";
        public const string TimingQueryInvalidated = "timing_query_invalidated";
        public const string TimingQueryInterrupted = "timing_query_interrupted";
        public const string TimingCounterNotFound = "timing_counter_not_found";
        public const string TimingCaptureInvalid = "timing_capture_invalid";
        public const string TimingCaptureBusy = "timing_capture_busy";
        public const string SqlSyntaxError = "sql_syntax_error";
        public const string SqlExecutionError = "sql_execution_error";
        public const string SqlForbidden = "sql_forbidden";
        public const string SqlNotReadOnly = "sql_not_read_only";
        public const string SqlMultipleStatements = "sql_multiple_statements";
        public const string SqlMissingParameter = "sql_missing_parameter";
        public const string SqlInvalidParameter = "sql_invalid_parameter";
        public const string SqlTimeout = "sql_timeout";
        public const string SqlInterrupted = "sql_interrupted";
        public const string SqlTablesNotPopulated = "sql_tables_not_populated";
        public const string SqlCapacityExceeded = "sql_capacity_exceeded";
        public const string SqlStoreClosed = "sql_store_closed";
        public const string SqlStoreBusy = "sql_store_busy";
        public const string ToolError = "tool_error";

        /// <summary>Codes that describe a transient condition the caller may retry after following nextCalls.</summary>
        public static readonly IReadOnlySet<string> Retryable = new HashSet<string>(StringComparer.Ordinal) { AnalysisActive, SqlStoreBusy, PixToolTimeout, PreparationUnavailable, ResultCapacityExceeded, SqlInterrupted, SqlTimeout, Timeout, TimingCaptureBusy, TimingQueryInterrupted, TimingQueryInvalidated, WorkerBusy };

        /// <summary>Every code, sorted; derived from the constants so nothing can be emitted that is not documented.</summary>
        public static readonly IReadOnlyList<string> All = typeof(Codes).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>What Guard knows about the GPU capture a failing call addressed: enough to tell "analysis not started" from other invalid states.</summary>
    public readonly record struct AnalysisContext(string Handle, bool AnalysisStarted);

    private static ToolCallDto Handles() => new("pix_handles", new { }, CostHints.Cached);

    public static PixToolException UnknownHandle(string handleId, IEnumerable<string> open)
    {
        string[] known = open.Order(StringComparer.Ordinal).ToArray();
        return new(Codes.UnknownHandle, $"Unknown handle '{handleId}'. Open handles: {(known.Length == 0 ? "none open" : string.Join(", ", known))}.", false, [Handles()]);
    }

    public static PixToolException HandleRequired()
        => new(Codes.InvalidArguments, "A handle id is required (e.g. the value returned by pix_gpu_open).", false, [Handles()]);

    public static PixToolException WrongHandleKind(string handleId, string actualKind, string neededKind)
        => new(Codes.WrongHandleKind, $"Handle '{handleId}' is a {actualKind} handle, but this tool needs a {neededKind} handle.", false, [Handles()]);

    /// <summary>An index or name that does not exist; <paramref name="listing"/> are the calls that enumerate the valid values.</summary>
    public static PixToolException InvalidReference(string message, params ToolCallDto[] listing)
        => new(Codes.InvalidReference, message, false, listing);

    public static PixToolException UnknownCounter(uint id, string handle)
        => new(Codes.UnknownCounter, $"Unknown counter id {id}; use pix_gpu_counters_list.", false, [new ToolCallDto("pix_gpu_counters_list", new { handle }, CostHints.Replay)]);

    public static PixToolException CounterReadFailed(string message, string handle)
        => new(Codes.CounterReadFailed, message, false,
            [new ToolCallDto("pix_gpu_analysis_stop", new { handle }, CostHints.Query), new ToolCallDto("pix_gpu_counters_list", new { handle }, CostHints.Replay)]);

    public static PixToolException FileNotFound(string what, string path) => new(Codes.FileNotFound, $"{what} not found: {path}");
    public static PixToolException FileExists(string path) => new(Codes.FileExists, $"Output already exists: {path}. Choose another path or set overwrite=true.");
    public static PixToolException DirectoryNotFound(string path) => new(Codes.DirectoryNotFound, $"Output parent directory does not exist: {path}.");

    public static PixToolException AnalysisRequired(string handle)
        => new(Codes.AnalysisRequired, "GPU analysis is not started for this capture; the call needs replayed state. Start it (or call a query tool, which starts it as a job).",
            false, [new ToolCallDto("pix_gpu_analysis_start", new { handle }, CostHints.Job)]);

    public static PixToolException AnalysisActive(string handle, string message)
        => new(Codes.AnalysisActive, message, true, [new ToolCallDto("pix_gpu_analysis_stop", new { handle }, CostHints.Query)]);

    public static PixToolException UnsupportedFeature(string message, IReadOnlyList<ToolCallDto>? nextCalls = null)
        => new(Codes.UnsupportedFeature, message, false, nextCalls);

    public static PixToolException InvalidState(string message, IReadOnlyList<ToolCallDto>? nextCalls = null)
        => new(Codes.InvalidState, message, false, nextCalls);

    public static PixToolException UnavailableShaderData(string message, IReadOnlyList<ToolCallDto>? nextCalls = null)
        => new(Codes.UnavailableShaderData, message, false, nextCalls);

    /// <summary>PIX failed or declined an operation; the HRESULT (when any) is preserved.</summary>
    public static PixToolException PixFailure(string message, Exception? inner = null)
        => new(Codes.PixError, inner is null ? message : $"{message}: {Describe(inner)}", false, null, HResultOf(inner));

    public static PixToolException PixUnavailable(string message)
        => new(Codes.PixUnavailable, message, false, [new ToolCallDto("pix_info", new { probe = true }, CostHints.Query)]);

    /// <summary>A pointer that stops resolving inside a retained result: names the nearest container, its keys and an outline call.</summary>
    public static PixToolException InvalidPointer(string pointer, string resolved, string kind, IReadOnlyList<string> keys, int total, string? resultRef)
    {
        string where = resolved.Length == 0 ? "the root" : $"'{resolved}'";
        string listing = keys.Count == 0 ? "" : $" Keys: {string.Join(", ", keys)}{(total > keys.Count ? $" ({keys.Count} of {total})" : "")}.";
        return new(Codes.InvalidPointer, $"JSON pointer '{pointer}' does not identify a value in the result; {where} is {kind} with {total} entries.{listing}",
            false, resultRef is null ? [] : [ResultStore.ReadCall(resultRef, resolved, mode: "outline")]);
    }

    public static PixToolException UnknownJob(string jobId, IEnumerable<string> known)
        => new(Codes.UnknownJob, $"Unknown job '{jobId}'. Known jobs: {string.Join(", ", known.OrderBy(k => k, StringComparer.Ordinal))}.", false,
            [new ToolCallDto("pix_jobs", new { }, CostHints.Cached)]);

    public static PixToolException JobAlreadyFinished(string jobId, string status, string? resultRef)
    {
        var next = new List<ToolCallDto> { new("pix_job_status", new { jobId }, CostHints.Cached) };
        if (resultRef is not null) next.Add(ResultStore.ReadCall(resultRef));
        return new(Codes.JobAlreadyFinished, $"Job {jobId} already finished with status {status}.", false, next);
    }

    public static PixToolException ResultExpired(string resultRef, ToolCallDto? origin = null)
        => new(Codes.ResultExpired, $"Unknown or expired result '{resultRef}'. Repeat the originating tool call to collect a new result.", false,
            origin is null ? [] : [origin]);

    public static PixToolException ResultCapacity(ToolCallDto? origin = null, ResultStoreSummary? summary = null)
    {
        string usage = summary is null ? "" : $" Retained: {summary.MemoryBytes:N0} of {summary.MemoryLimitBytes:N0} memory bytes, {summary.DiskBytes:N0} of {summary.DiskLimitBytes:N0} disk bytes, {summary.ResultCount} results, {summary.ActiveLeases} active leases.";
        return new(Codes.ResultCapacityExceeded, $"Result retention capacity is exhausted.{usage} Finish active reads or exports, close unused captures, or raise {ServerOptions.MemoryVariable} / {ServerOptions.DiskVariable}.",
            true, origin is null ? [new ToolCallDto("pix_info", new { }, CostHints.Cached)] : [origin, new ToolCallDto("pix_info", new { }, CostHints.Cached)]);
    }

    public static PixToolException ServerShuttingDown(string operation)
        => new(Codes.ServerShuttingDown, $"The server is shutting down; '{operation}' was not started.");

    public static PixToolException InvalidArguments(string message, IReadOnlyList<ToolCallDto>? nextCalls = null)
        => new(Codes.InvalidArguments, message, false, nextCalls);

    /// <summary>A call to a tool whose toolset PIXMCP_TOOLSETS does not enable.</summary>
    public static PixToolException ToolDisabled(string tool)
        => new(Codes.ToolDisabled, $"{tool} belongs to toolset '{Toolsets.For(tool)}', which {ServerOptions.ToolsetsVariable} does not enable (enabled: {string.Join(", ", Toolsets.Describe().Enabled)}). Restart the server with that toolset listed to use it.",
            false, [new ToolCallDto("pix_info", new { }, CostHints.Cached)]);

    /// <summary>prompts/get for a playbook whose tools PIXMCP_TOOLSETS hides.</summary>
    public static PixToolException PromptDisabled(string prompt)
        => new(Codes.ToolDisabled, $"Prompt {prompt} needs tools that {ServerOptions.ToolsetsVariable} does not enable (enabled toolsets: {string.Join(", ", Toolsets.Describe().Enabled)}).",
            false, [new ToolCallDto("pix_info", new { }, CostHints.Cached)]);

    public static PixToolException AnalysisSettingsConflict(string? handle)
        => new(Codes.AnalysisSettingsConflict, "Analysis is already running with different or SDK-selected settings. Call pix_gpu_analysis_stop before requesting different settings.",
            false, handle is null ? [] : [new ToolCallDto("pix_gpu_analysis_stop", new { handle }, CostHints.Query)]);

    public static PixToolException PreparationUnavailable(string tool, string description, ToolCallDto? retry)
        => new(Codes.PreparationUnavailable, $"{tool}: {description} finished but the data is no longer available (analysis was stopped or the handle changed). Retry the call.",
            true, retry is null ? [] : [retry]);

    public const int E_PIX_DEVELOPER_MODE_NOT_ENABLED = unchecked((int)0x8ABC0000);
    public const int E_PIX_FEATURE_REQUIRES_DEVELOPER_MODE = unchecked((int)0x8ABC0001);
    /// <summary>PIX declined to start analysis on the chosen adapter; observed for an NVIDIA capture on an Intel Arc without IGNORE_INCOMPATIBILITIES.</summary>
    public const int E_PIX_ANALYSIS_INCOMPATIBLE = unchecked((int)0x8ABC006B);
    public const int E_NOT_VALID_STATE = unchecked((int)0x8007139F);
    /// <summary>E_ABORT: PIX reports an operation interrupted by its cancellation token this way.</summary>
    public const int E_ABORT = unchecked((int)0x80004004);
    /// <summary>HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED).</summary>
    public const int E_OPERATION_ABORTED = unchecked((int)0x800704C7);

    public static string Hex(int hresult) => $"0x{hresult:X8}";

    /// <summary>The loaded PIX assembly lacks a type or member this build binds against (version drift); pix_info shows the compatibility verdict.</summary>
    public static PixToolException ApiMismatch(Exception ex)
        => new(Codes.PixApiMismatch, ApiMismatchMessage(ex), false, [new ToolCallDto("pix_info", new { probe = true }, CostHints.Cached)]);

    private static string ApiMismatchMessage(Exception ex)
        => $"The loaded PIX assembly does not match the build this server was compiled against ({ex.GetType().Name}: {ex.Message}). " +
           $"{PixDiscovery.Compatibility.Message} Install PIX {PixDiscovery.VerifiedVersion} or rebuild the server against the installed PIX; pix_info.pix.compatibility has the details.";

    public static int? HResultOf(Exception ex) => ex switch
    {
        COMException com => com.HResult,
        ExternalException ext => ext.ErrorCode,
        // COM interop projects E_NOTIMPL to this managed exception, not COMException.
        // Preserve its HRESULT so optional native capabilities can cache unsupported.
        NotImplementedException notImplemented => notImplemented.HResult,
        _ => null,
    };

    public static ErrorDto ToDto(Exception ex, string? context = null, AnalysisContext? analysis = null)
    {
        if (ex is PixToolException tool) return tool.Detail;
        int? hr = HResultOf(ex);
        string code = hr switch
        {
            E_PIX_DEVELOPER_MODE_NOT_ENABLED or E_PIX_FEATURE_REQUIRES_DEVELOPER_MODE => Codes.DeveloperModeRequired,
            // E_NOT_VALID_STATE on a capture whose analysis is not started means "replay first"; otherwise the object is in another state.
            E_NOT_VALID_STATE => analysis is { AnalysisStarted: false } ? Codes.AnalysisRequired : Codes.InvalidState,
            unchecked((int)0x80004001) or unchecked((int)0x80004002) or unchecked((int)0x80070032) or unchecked((int)0x887A0004) => Codes.UnsupportedFeature,
            _ => ex switch
            {
                MissingMemberException or TypeLoadException or EntryPointNotFoundException or BadImageFormatException => Codes.PixApiMismatch,
                ArgumentException or FormatException => Codes.InvalidArguments,
                FileNotFoundException => Codes.FileNotFound,
                OperationCanceledException => Codes.Cancelled,
                TimeoutException => Codes.Timeout,
                _ => Codes.PixError,
            },
        };
        IReadOnlyList<ToolCallDto> next = code switch
        {
            Codes.AnalysisRequired => [new ToolCallDto("pix_gpu_analysis_start", new { handle = analysis!.Value.Handle }, CostHints.Job)],
            Codes.DeveloperModeRequired => [new ToolCallDto("pix_info", new { }, CostHints.Cached)],
            Codes.PixApiMismatch => [new ToolCallDto("pix_info", new { probe = true }, CostHints.Cached)],
            _ => [],
        };
        string described = code == Codes.PixApiMismatch ? ApiMismatchMessage(ex) : Describe(ex);
        return new(code, (context is null ? "" : context + ": ") + described,
            hr.HasValue ? Hex(hr.Value) : null, Codes.Retryable.Contains(code), next);
    }

    /// <summary>The GPU capture addressed by the current call, when the session knows it; used to map E_NOT_VALID_STATE.</summary>
    public static AnalysisContext? CurrentAnalysis(PixSession? session)
    {
        if (session is null) return null;
        foreach (string owner in StructuredToolResults.CurrentOwners())
            if (session.TryGet<Handles.GpuCaptureHandle>(owner) is { } capture) return new(capture.Id, capture.AnalysisStarted);
        return null;
    }

    /// <summary>True for the HRESULTs PIX uses to report an interrupted (cancelled) operation.</summary>
    public static bool IsCancellationHResult(int hresult) => hresult is E_ABORT or E_OPERATION_ABORTED;

    public static bool IsUnsupportedHResult(int? hresult) => hresult is unchecked((int)0x80004001)
        or unchecked((int)0x80004002) or unchecked((int)0x80070032) or unchecked((int)0x887A0004);

    /// <summary>True when an exception means the driver or PIX build does not support the feature: an unsupported HRESULT or the unsupported_feature code.</summary>
    public static bool IsUnsupported(Exception ex)
        => ex is PixToolException { Detail.Code: Codes.UnsupportedFeature } || IsUnsupportedHResult(HResultOf(ex)) || ToDto(ex).Code == Codes.UnsupportedFeature;

    /// <summary>
    /// True when PIX declined the operation with one of its own facility codes (other than the Developer Mode codes). That alone does not
    /// establish that the feature is unsupported.
    /// </summary>
    public static bool IsDeclined(Exception ex)
        => HResultOf(ex) is int hr && IsPixFacility(hr) && hr != E_PIX_DEVELOPER_MODE_NOT_ENABLED && hr != E_PIX_FEATURE_REQUIRES_DEVELOPER_MODE;

    /// <summary>PIX reports its own failures with facility 0xABC (0x8ABC0000..0x8ABCFFFF).</summary>
    public static bool IsPixFacility(int hresult) => ((uint)hresult & 0xFFFF0000) == 0x8ABC0000;

    /// <summary>Human-readable description with HRESULT and, where relevant, remediation.</summary>
    public static string Describe(Exception ex)
    {
        if (ex is PixToolException tool) return tool.Detail.Message;
        int? hr = HResultOf(ex);
        string message = ex.Message;
        if (hr is null)
        {
            return $"{ex.GetType().Name}: {message}";
        }

        string text = $"PIX API call failed ({Hex(hr.Value)}): {message}";
        if (hr == E_PIX_DEVELOPER_MODE_NOT_ENABLED || hr == E_PIX_FEATURE_REQUIRES_DEVELOPER_MODE)
        {
            text += " Windows Developer Mode is required for this operation. Enable it in Settings > Privacy & security > For developers, " +
                    "or run: reg add \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\AppModelUnlock\" /v AllowDevelopmentWithoutDevLicense /t REG_DWORD /d 1 /f";
        }
        else if (hr == E_NOT_VALID_STATE)
        {
            text += " (E_NOT_VALID_STATE: the object is not in a state that supports this call, e.g. pipeline state not bound for this event, or analysis not started.)";
        }
        else if (IsPixFacility(hr.Value))
        {
            text += " (PIX-specific error: PIX declined the operation. This alone does not establish that the feature is unsupported.)";
        }
        return text;
    }

    /// <summary>Wraps any exception into an McpException so the client sees a useful tool error.</summary>
    public static McpException ToMcp(Exception ex, string context, AnalysisContext? analysis = null)
    {
        if (ex is McpException mcp)
        {
            return mcp;
        }
        ErrorDto detail = ToDto(ex, context, analysis);
        return new PixToolException(detail.Code, detail.Message, detail.Retryable, detail.NextCalls, HResultOf(ex));
    }

    /// <summary>
    /// Runs <paramref name="work"/>, converting failures to McpExceptions. Cancellation propagates unchanged so the transport
    /// reports it as such. With a <paramref name="session"/>, E_NOT_VALID_STATE on a capture without analysis maps to analysis_required.
    /// </summary>
    public static async Task<T> Guard<T>(string context, Func<Task<T>> work, PixSession? session = null)
    {
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw ToMcp(ex, context, CurrentAnalysis(session));
        }
    }

    /// <summary>Returns an "unavailable" marker instead of failing, for optional PIX features.</summary>
    public static object Unavailable(string feature, Exception ex) => new
    {
        unavailable = true,
        feature,
        reason = Describe(ex),
        state = IsUnsupported(ex) ? "unsupported" : "unknown",
        error = ToDto(ex),
    };
}
