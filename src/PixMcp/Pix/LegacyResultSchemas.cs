using System.Text.Json;
using PixMcp.Pix.Handles;

namespace PixMcp.Pix;

/// <summary>Stable JSON fields of the low-level tools. Native description/diagnostic sections remain extensible.</summary>
internal static class LegacyResultSchemas
{
    internal static JsonElement? For(string tool) => tool switch
    {
        "pix_gpu_open" or "pix_gpu_info" => Schema<GpuCaptureInfo>(),
        "pix_gpu_event" => Schema<GpuEvent>(),
        "pix_gpu_queues" => Schema<PageResult<Queue>>(),
        "pix_gpu_api_objects" => Schema<PageResult<ApiObject>>(),
        "pix_gpu_analysis_status" => Schema<Analysis>(),
        "pix_gpu_analysis_adapters" => Schema<AnalysisAdapters>(),
        "pix_gpu_analysis_stop" => Schema<AnalysisStopped>(),
        "pix_gpu_screenshot" => Schema<Screenshot>(),
        "pix_gpu_heap" => Schema<Heap>(),
        "pix_handles" => Schema<PageResult<HandleSummary>>(),
        "pix_log" => Schema<PageResult<LogEntry>>(),
        "pix_close" => Schema<ClosedHandle>(),
        "pix_close_all" => Schema<PageResult<CloseResult>>(),
        "pix_info" => Schema<SessionInfo>(),
        "pix_timing_open" => Schema<TimingCapture>(),
        "pix_timing_save" => Schema<SavedResult>(),
        "pix_capture_format" => Schema<CaptureFormat>(),
        "pix_device_connect" or "pix_device_info" => Schema<DeviceInfo>(),
        "pix_device_launch" or "pix_device_attach" => Schema<ProcessTarget>(),
        "pix_device_processes" => Schema<PageResult<ProcessInfo>>(),
        "pix_device_counters" => Schema<PageResult<SystemCounter>>(),
        "pix_device_packaged_apps" => Schema<PageResult<PackagedApp>>(),
        "pix_device_timing_capture_start" => Schema<CaptureStarted>(),
        "pix_device_detach" => Schema<DetachedResult>(),
        "pix_device_d3d_settings" => Schema<D3dSettings>(),
        "pix_device_d3d_settings_set" => Schema<SettingChanged>(),
        "pix_dump_open" or "pix_dump_info" => Schema<DumpInfo>(),
        "pix_dump_shader_wave" => Schema<DumpWave>(),
        "pix_dump_shader_wave_data" => Schema<DumpWaveData>(),
        "pix_dump_shader_waves" => Schema<PageResult<DumpWaveData>>(),
        "pix_dump_queues" => Schema<PageResult<DumpQueue>>(),
        "pix_dump_events" => Schema<PageResult<DumpEvent>>(),
        "pix_dump_resources" => Schema<PageResult<DumpResource>>(),
        "pix_dump_journal" => Schema<PageResult<JournalEntry>>(),
        "pix_dump_page_faults" => Schema<PageResult<PageFault>>(),
        "pix_dump_shader_eval" => Schema<DumpEvaluation>(),
        "pix_dump_shader_variable" => Schema<DumpVariable>(),
        "pix_dump_breadcrumbs" => Schema<Breadcrumbs>(),
        "pix_dump_blobs" => Schema<BlobsResult>(),
        "pix_dump_gpu_state" => StructuredToolResults.AnyOf(Schema<GpuStateCatalog>(), Schema<GpuStateRows>()),
        _ => null,
    };

    private static JsonElement Schema<T>() => StructuredToolResults.Export<T>();

    internal sealed record CounterMetadata(uint Id, string Name, string Description, string DataType, IReadOnlyList<string> Groups,
        string Unit, string UnitSource, string UnitConfidence, CounterRange? Range, string AggregationHint);
    internal sealed record CounterRange(double? Min, double? Max);
    internal sealed record UnavailableResult(bool Unavailable, string Feature, string Reason, string? State, ErrorDto? Error);
    internal sealed record Occupancy(ReplayProvenance Provenance, string TimeOrigin, IReadOnlyList<object> Types,
        IReadOnlyList<object> Stages, IReadOnlyList<OccupancySeries> Series, IReadOnlyList<string>? Notes);
    internal sealed record OccupancySeries(string Type, string Stage, string StageAbbreviation, uint MaxSlotsAvailable,
        ulong PointCount, uint PeakSlots, IReadOnlyList<OccupancyPoint> Points, int ReturnedPoints,
        string Sampling, IReadOnlyList<ToolCallDto> NextCalls);
    internal sealed record OccupancyPoint(ulong Index, double TimeNs, uint Slots);
    internal sealed record HighFrequency(ReplayProvenance Provenance, string TimeOrigin, ulong CounterCount,
        IReadOnlyList<object> Groups, IReadOnlyList<object> Sets, HighFrequencySet? Samples, IReadOnlyList<string>? Notes);
    internal sealed record HighFrequencySet(string Set, IReadOnlyList<HighFrequencyCounter> Counters);
    internal sealed record HighFrequencyCounter(string Counter, ulong? BatchId, ulong? SampleCount, double? Min,
        double? Max, double? Average, IReadOnlyList<HighFrequencyPoint>? Samples, int? ReturnedSamples,
        string? Sampling, IReadOnlyList<ToolCallDto>? NextCalls, bool? Unavailable, string? Reason,
        string? Description = null, string? Unit = null, string? UnitSource = null, string? UnitConfidence = null);
    internal sealed record HighFrequencyPoint(ulong Index, double TimeNs, double Value);
    private sealed record ItemsEnvelope<T>(IReadOnlyList<T> Items);
    private sealed record Queue(int QueueIndex, uint Id, string Name, string Type, uint AdapterId, string AdapterName, string Vendor, uint EventCount);
    private sealed record Adapter(ulong Id, string Name);
    private sealed record AdapterPowerStates(ulong Id, string Name, string Vendor, object? PowerStates);
    private sealed record Analysis(string Handle, bool Connected, bool Started, DateTimeOffset? StartedAt,
        IReadOnlyList<Adapter>? Adapters, ulong? SelectedAdapter, uint? SelectedPowerState, string? Flags,
        bool TimingCollected, IReadOnlyList<string> CountersCollected, VendorIdentity ReplayVendor, VendorIdentity CaptureVendor);
    private sealed record AnalysisAdapters(IReadOnlyList<AdapterPowerStates> Adapters, ulong? SelectedAdapter, uint? SelectedPowerState);
    private sealed record AnalysisStopped(bool Stopped, IReadOnlyList<string>? Warnings, Analysis Analysis);
    private sealed record GpuCaptureInfo(string Handle, string Path, object? FileInfo, object? Application,
        IReadOnlyList<Queue> Queues, long TotalEvents, VendorIdentity Vendor, Analysis Analysis, IReadOnlyDictionary<string, CapabilityDto> Capabilities);
    private sealed record GpuEvent(EventDto Event, IReadOnlyList<EventDto> Parents, bool ParentsTruncated,
        int ChildCount, IReadOnlyList<EventDto> Children, bool ChildrenTruncated, IReadOnlyList<ToolCallDto> NextCalls);
    private sealed record ApiObject(ulong Index, string ApiObjectId, string Type, string Name);
    private sealed record Screenshot(string? Path, uint Width, uint Height, string Format, bool? ToneMapped, int PngBytes,
        string? ArtifactRef, bool ArtifactAvailable, string? ArtifactUnavailable, ScreenshotImage? Image,
        IReadOnlyList<ToolCallDto> NextCalls);
    private sealed record ScreenshotImage(uint Width, uint Height, bool Resized);
    private sealed record Heap(ulong Index, string ApiObjectId, string? Name, object Desc, uint PlacedResourceCount, PageResult<object> PlacedResources);
    private sealed record HandleSummary(string Handle, string Kind, string Path, DateTimeOffset OpenedAt);
    private sealed record ClosedHandle(string Closed, string Kind, IReadOnlyList<string>? Warnings);
    private sealed record CloseResult(string Closed, string? Kind, IReadOnlyList<string>? Warnings, string? Error);
    private sealed record LogEntry(DateTimeOffset Time, string Severity, string Source, string SourceName, string Message);
    private sealed record PixInstall(string? InstallDir, string? Version, string? DiscoveredVia, string? DiscoveryError, bool ApiLoaded, string? ProbeError,
        string? Build, PixInstallVersion? InstallVersion, PixBuildReference BuiltAgainst, VerifiedRange VerifiedRange, PixCompatibility Compatibility, bool PickNewest,
        IReadOnlyList<ApiProbe> ApiSurface, string? ApiSurfaceError, string? LoadedFileVersion, bool? LoggerAttached, string? LoggerError,
        IReadOnlyList<CompatibilityNoteSummary> Notes);
    private sealed record VerifiedRange(string MinPreviewDate, string VerifiedVersion);
    private sealed record CompatibilityNoteSummary(string Id, string Feature, string Vendor, string Severity, string Text);
    private sealed record ServerProcess(int Pid, bool Is64Bit, string Runtime, string Os, string Version);
    private sealed record WorkerStatus(bool Busy, string? RunningJob, int QueuedCalls, string? Operation,
        DateTimeOffset? StartedAt, double? ElapsedSeconds);
    private sealed record SessionInfo(PixInstall Pix, bool? DeveloperModeEnabled, ServerProcess Process,
        WorkerStatus Worker, ResultStoreSummary Results, ServerOptionsSummary Options, IReadOnlyList<HandleSummary> Handles, IReadOnlyList<JobDto> Jobs,
        PixDiffInfoDto Pixdiff);
    private sealed record TimingCapture(string Handle, string Kind, string Path, DateTimeOffset OpenedAt,
        string CapturePath, string PixStoragePath, bool SymbolsResolved);
    private sealed record SavedResult(string Saved);
    private sealed record CaptureFormat(string Path, string Format, bool IsCurrent, bool NeedsUpgrade);
    private sealed record DeviceInfo(string Handle, object? Metrics, object? Adapters, IReadOnlyList<uint> LaunchedProcessIds,
        IReadOnlyList<CaptureTargetDto> Targets, string? TimingCaptureInProgress, IReadOnlyList<object> RecentEvents);
    private sealed record ProcessTarget(uint ProcessId, string? ProcessName, string UnsupportedReason, bool Capturable, bool Ready, string? Note);
    private sealed record ProcessInfo(uint ProcessId, string ExeName, string? FriendlyName, string? CommandLine,
        bool IsPackagedApp, string Architecture, string UnsupportedReason);
    private sealed record SystemCounter(uint Id, string Name, string? InternalName, string Group, string? Units,
        string? Description, double Min, double Max, bool IsDefault, string ProcessType);
    private sealed record PackagedApp(string PackageFullName, string ApplicationId, string? FriendlyName, string Architecture, string UnsupportedReason);
    private sealed record CaptureStarted(bool Started, string Path, TimingCaptureSettingsDto Settings);
    private sealed record DetachedResult(bool Detached, bool Terminated);
    private sealed record D3dSettings(IReadOnlyList<object?> DebugLayer, IReadOnlyList<object?> Dred, IReadOnlyList<object?> Device);
    private sealed record SettingChanged(bool Changed, string Category, object Result);
    private sealed record DumpInfo(string Handle, string Path, object Metadata, IReadOnlyList<object> Queues);
    private sealed record DumpQueue(int QueueIndex, ulong Id, string Name, string Type, string Status,
        object? HardwareStatus, object? PageFaultCount, object? RootEventCount);
    private sealed record DumpEvent(Tools.DumpEventRef EventRef, ulong Id, string Kind, string Name, string Type,
        string Status, bool IsGpuWork, object? CorrelatedShaders, object? CorrelatedResources,
        object? ChildCount, object? Children, bool? ChildrenTruncated, IReadOnlyList<ToolCallDto>? NextCalls);
    private sealed record DumpResource(string? Name, string ApiObjectId, string GpuVirtualAddress, ulong SizeBytes,
        string Type, object? Desc, object? Attributes, object? Events);
    private sealed record JournalEntry(string Code, uint ThreadId, ulong TickCount, string? Message);
    private sealed record PageFault(int FaultIndex, ulong Id, string GpuVirtualAddress, string Type, string AccessType,
        ulong TimestampNs, object? Queue, int ResourceEventCount, object? ResourceEvents, int ResourceEventsOffset,
        bool ResourceEventsTruncated, IReadOnlyList<ToolCallDto>? NextCalls);
    private sealed record DumpWave(int WaveIndex, ulong Id, string Stage, string Status, string? Coordinates,
        object ExceptionsHit, string CodeType, object? CallStack, object? Variables, int VariablesOffset,
        bool? VariablesTruncated, int? NextVariablesOffset, IReadOnlyList<ToolCallDto>? NextCalls);
    private sealed record DumpWaveData(int WaveIndex, ulong Id, string Stage, string Status, string? Coordinates,
        string InstructionPointer, object? InstructionPointerLocation, object ExceptionsHit, object? LaneCount,
        object? Lanes, int LanesOffset, bool? LanesTruncated, object? OffendingLocations, int LocationsOffset,
        bool? OffendingLocationsTruncated, IReadOnlyList<ToolCallDto> NextCalls);
    private sealed record DumpEvaluation(int WaveIndex, string Expression, string CodeType, string LaneMask, object Result);
    private sealed record DumpVariable(IReadOnlyList<int> VariablePath, string? Name, string? Location, object? Value,
        object? Components, ulong? ComponentCount, IReadOnlyList<ToolCallDto>? NextCalls);
    private sealed record Breadcrumbs(IReadOnlyList<BreadcrumbNode> Nodes, string? Unavailable);
    private sealed record BreadcrumbNode(int NodeIndex, string? CommandList, string? CommandQueue, ulong CompletedCount,
        int OpCount, int OpsOffset, int ReturnedOps, int? NextOpsOffset, bool OpsTruncated,
        IReadOnlyList<BreadcrumbOp>? Ops, IReadOnlyList<object>? Contexts);
    private sealed record BreadcrumbOp(int Index, string Op, bool Completed);
    private sealed record BlobsResult(IReadOnlyList<Blob> Blobs, BlobData? Data);
    private sealed record Blob(int BlobIndex, string Metadata, ulong SizeBytes);
    private sealed record BlobData(int BlobIndex, ulong SizeBytes, string? Path, int? Offset, int? ReturnedBytes,
        bool? Truncated, string? Base64, IReadOnlyList<ToolCallDto>? NextCalls);
    private sealed record GpuStateCatalog(IReadOnlyList<object> Tables, string? Unavailable);
    private sealed record GpuStateRows(int TableIndex, string Name, string? Description, IReadOnlyList<string> Columns, IReadOnlyList<object> Rows, int RowCount);
}
