using Microsoft.PIX;
using Microsoft.PIX.Extension;
using Microsoft.PIX.Extension.DeviceConnection;
using Microsoft.PIX.Extension.GpuCapture;
using Microsoft.PIX.Extension.GpuCapture.Analysis;
using ModelContextProtocol;
using PixMcp.Tools;
using ConnDesc = Microsoft.PIX.Extension.DeviceConnection.PIX_CONNECTION_DESC;

namespace PixMcp.Pix.Handles;

/// <summary>A compact managed copy of PIX_EVENT_INFO (the PIX struct holds unmanaged string pointers).</summary>
public readonly record struct EventRecord(uint Index, uint GpuId, uint ParentIndex, string Name, string ApiCallData, uint CommandListId, uint Color)
{
    public static EventRecord From(uint index, PIX_EVENT_INFO e)
        => new(index, e.GpuId, e.ParentIndex, Interop.A(e.Name), Interop.A(e.ApiCallData), e.CommandListId, e.Color);

    public EventDto ToDto(int queueIndex) => new(queueIndex, Index,
        GpuId == uint.MaxValue ? null : GpuId, ParentIndex == uint.MaxValue ? null : ParentIndex,
        Name, string.IsNullOrEmpty(ApiCallData) ? null : ApiCallData, CommandListId, Color == 0 ? null : $"0x{Color:X8}");
}

public sealed class QueueEntry
{
    public required int Index { get; init; }
    public required IPixGpuCaptureQueueInfo Info { get; init; }
    public required uint Id { get; init; }
    public required string Name { get; init; }
    public required PIX_QUEUE_TYPE Type { get; init; }
    public required uint AdapterId { get; init; }
    public required string AdapterName { get; init; }
    public required uint EventCount { get; init; }
    public EventRecord[]? Cache { get; set; }

    public object ToDto() => new
    {
        queueIndex = Index,
        id = Id,
        name = Name,
        type = Type,
        adapterId = AdapterId,
        adapterName = AdapterName,
        vendor = GpuVendors.Name(GpuVendors.FromAdapterName(AdapterName)),
        eventCount = EventCount,
    };
}

public sealed record EventTimingRow(int QueueIndex, uint Index, uint GpuId, string Name, string ApiCallData, ulong TopStart, ulong TopDuration, ulong EopStart, ulong EopDuration);

public sealed record CounterInfo(uint Id, string Name, string Description, string DataType, string[] Groups)
{
    public PIX_FORMAT_SPECIFIER_TYPE FormatSpecifier { get; init; }
    /// <summary>Inferred unit (PIX exposes none); see <see cref="CounterUnits"/>.</summary>
    public UnitGuess Unit => _unit ??= CounterUnits.Infer(Name, Description, DataType);
    private UnitGuess? _unit;
}

public sealed record ExperimentInfo(Guid Guid, string Name, string Category, string HelpText, PIX_EXPERIMENT_SOURCE Source);

internal readonly record struct AnalysisOptions(ulong? Adapter = null, uint? PowerState = null, PIX_ANALYSIS_FLAGS? Flags = null)
{
    /// <summary>A request may leave settings unspecified or repeat the running ones; anything else is analysis_settings_conflict.</summary>
    internal void ValidateRunningRequest(AnalysisOptions requested, string? handle = null)
    {
        if (requested.Adapter.HasValue && requested.Adapter != Adapter ||
            requested.PowerState.HasValue && requested.PowerState != PowerState ||
            requested.Flags.HasValue && requested.Flags != Flags)
        {
            throw PixErrors.AnalysisSettingsConflict(handle);
        }
    }
}

public sealed partial class GpuCaptureHandle : PixHandle
{
    public const ulong TimingNone = ulong.MaxValue;

    public GpuCaptureHandle(string path, IPixGpuCaptureDocument document) : base(path)
    {
        Document = document;
        Queues = LoadQueues(document);
    }

    public override string Kind => "gpu";
    public IPixGpuCaptureDocument Document { get; private set; }
    public IReadOnlyList<QueueEntry> Queues { get; }

    // Analysis session state
    public IPixGpuCaptureAnalysis? Analysis { get; private set; }
    public bool AnalysisConnected { get; private set; }
    public bool AnalysisStarted { get; private set; }
    public List<(ulong Id, string Name)>? Adapters { get; private set; }
    public ulong? SelectedAdapter { get; set; }
    public uint? SelectedPowerState { get; set; }
    public PIX_ANALYSIS_FLAGS? SelectedFlags { get; set; }
    public DateTimeOffset? AnalysisStartedAt { get; private set; }
    /// <summary>
    /// Settings of the analysis-start job that is queued or running (set under <see cref="PixHandle.PreparationGate"/>).
    /// A later start request with different settings is rejected at join time instead of failing minutes later on the worker.
    /// </summary>
    internal AnalysisOptions? PendingAnalysisOptions { get; set; }

    // Caches of expensive results
    public IPixGpuCaptureTiming? Timing { get; set; }
    public Dictionary<int, EventTimingRow[]> TimingRowsByQueue { get; internal set; } = new();
    /// <summary>How each queue's timing rows were read (bulk or per event), published with the rows and cleared with them.</summary>
    public Dictionary<int, ReadbackCoverage> TimingReadbackByQueue { get; } = new();
    public Dictionary<int, TimingTreeResult> TimingTreeByQueue { get; } = new();
    private readonly Dictionary<int, int[]> _childCounts = new();
    public IPixGpuCaptureCounters? Counters { get; set; }
    public List<CounterInfo>? CounterList { get; set; }
    public Dictionary<string, IPixGpuCaptureCounterData> CollectedCounters { get; } = new();
    public Dictionary<string, CounterCollectionCache> CounterCollections { get; } = new();
    public Dictionary<int, HfCollectionCache> HighFrequencyCollections { get; } = new();
    public IPixGpuCaptureDrPix? DrPix { get; set; }
    public List<ExperimentInfo>? Experiments { get; set; }

    private static List<QueueEntry> LoadQueues(IPixGpuCaptureDocument document)
    {
        var list = new List<QueueEntry>();
        IPixCollection queues = document.GetQueues();
        ulong count = queues.GetCount();
        for (ulong i = 0; i < count; i++)
        {
            IPixGpuCaptureQueueInfo info = queues.Get<IPixGpuCaptureQueueInfo>(i);
            list.Add(new QueueEntry
            {
                Index = (int)i,
                Info = info,
                Id = info.GetId(),
                Name = Interop.W(info.GetName()),
                Type = info.GetType(),
                AdapterId = info.GetAdapterId(),
                AdapterName = Interop.W(info.GetAdapterName()),
                EventCount = info.GetEventCount(),
            });
        }
        return list;
    }

    public QueueEntry Queue(int queueIndex)
    {
        if (queueIndex < 0 || queueIndex >= Queues.Count)
        {
            throw PixErrors.InvalidReference($"queueIndex {queueIndex} is out of range; the capture has {Queues.Count} queue(s) (0..{Queues.Count - 1}).",
                new ToolCallDto("pix_gpu_queues", new { handle = Id }, CostHints.Cached));
        }
        return Queues[queueIndex];
    }

    /// <summary>Fetches the live PIX_EVENT_INFO for an event (needed when passing events back into PIX).</summary>
    public PIX_EVENT_INFO EventInfo(int queueIndex, uint eventIndex)
    {
        QueueEntry queue = Queue(queueIndex);
        if (eventIndex >= queue.EventCount)
        {
            throw PixErrors.InvalidReference($"eventIndex {eventIndex} is out of range; queue {queueIndex} has {queue.EventCount} event(s).",
                new ToolCallDto("pix_gpu_events", new { handle = Id, queueIndex }, CostHints.Query));
        }
        return PixApiExtensionsGpuCapture.GetEvent(queue.Info, eventIndex);
    }

    public EventRecord Event(int queueIndex, uint eventIndex)
    {
        QueueEntry queue = Queue(queueIndex);
        if (queue.Cache is not null && eventIndex < queue.Cache.Length)
        {
            return queue.Cache[eventIndex];
        }
        return EventRecord.From(eventIndex, EventInfo(queueIndex, eventIndex));
    }

    /// <summary>The cached events of a queue, or null when they have not been materialised yet. Safe off the worker.</summary>
    public EventRecord[]? CachedEvents(int queueIndex) => Queue(queueIndex).Cache;

    /// <summary>Materializes every event of a queue once (needed for filtering/sorting); cached afterwards.</summary>
    public EventRecord[] AllEvents(int queueIndex)
    {
        QueueEntry queue = Queue(queueIndex);
        if (queue.Cache is null)
        {
            var records = new EventRecord[queue.EventCount];
            for (uint i = 0; i < queue.EventCount; i++)
            {
                records[i] = EventRecord.From(i, PixApiExtensionsGpuCapture.GetEvent(queue.Info, i));
            }
            queue.Cache = records;
        }
        return queue.Cache;
    }

    /// <summary>Finds the event with the given GPU id (searching all queues).</summary>
    public (int queueIndex, EventRecord record)? FindByGpuId(uint gpuId)
    {
        foreach (QueueEntry queue in Queues)
        {
            foreach (EventRecord record in AllEvents(queue.Index))
            {
                if (record.GpuId == gpuId)
                {
                    return (queue.Index, record);
                }
            }
        }
        return null;
    }

    /// <summary>Direct child counts per event of a queue; event metadata is immutable so this lives until close.</summary>
    public int[] ChildCounts(int queueIndex)
    {
        QueueEntry queue = Queue(queueIndex);
        if (!_childCounts.TryGetValue(queue.Index, out int[]? counts))
        {
            counts = EventNavigation.ChildCounts(AllEvents(queue.Index));
            _childCounts[queue.Index] = counts;
        }
        return counts;
    }

    /// <summary>Per-event inclusive GPU time and queue totals (needs timing rows); cached until analysis stops.</summary>
    public TimingTreeResult TimingTreeFor(int queueIndex)
    {
        QueueEntry queue = Queue(queueIndex);
        if (!TimingTreeByQueue.TryGetValue(queue.Index, out TimingTreeResult? tree))
        {
            EventTimingRow[] rows = TimingRowsByQueue.TryGetValue(queue.Index, out EventTimingRow[]? collected) ? collected : Array.Empty<EventTimingRow>();
            tree = TimingTree.Build(AllEvents(queue.Index), rows, queue.Index);
            TimingTreeByQueue[queue.Index] = tree;
        }
        return tree;
    }

    public TimingTreeNode[] TimingTreeNodes(int queueIndex) => TimingTreeFor(queueIndex).Nodes;

    public QueueTotals QueueTotals(int queueIndex) => TimingTreeFor(queueIndex).Totals;

    public IPixGpuCaptureAnalysis GetAnalysis() => Analysis ??= Document.GetAnalysis();

    internal bool ConfigureAnalysis(AnalysisOptions requested)
    {
        if (AnalysisStarted)
        {
            new AnalysisOptions(SelectedAdapter, SelectedPowerState, SelectedFlags).ValidateRunningRequest(requested, Id);
            return true;
        }
        SelectedAdapter = requested.Adapter ?? SelectedAdapter;
        SelectedPowerState = requested.PowerState ?? SelectedPowerState;
        SelectedFlags = requested.Flags ?? SelectedFlags;
        return false;
    }

    /// <summary>Connects the analysis session to the local PIX device (no replay yet).</summary>
    public IPixGpuCaptureAnalysis EnsureConnected(Job? job)
    {
        IPixGpuCaptureAnalysis analysis = GetAnalysis();
        if (!AnalysisConnected)
        {
            job?.AddMessage("Connecting analysis to the local GPU...");
            PixApiExtensionsGpuCaptureAnalysis.Connect(analysis, ConnDesc.CreateLocal(), job?.PixToken!);
            AnalysisConnected = true;
        }

        if (Adapters is null)
        {
            try { Adapters = LoadAdapters(analysis); }
            catch (Exception ex) { job?.AddMessage("Adapter enumeration unavailable: " + PixErrors.Describe(ex)); Adapters = new(); }
        }
        return analysis;
    }

    /// <summary>Connects to the local GPU and starts analysis if not already running. Progress goes to <paramref name="job"/> when given.</summary>
    public unsafe void EnsureAnalysisStarted(Job? job)
    {
        if (AnalysisStarted)
        {
            return;
        }

        job?.ThrowIfCancellationRequested();
        IPixGpuCaptureAnalysis analysis = EnsureConnected(job);

        job?.AddMessage("Starting analysis (replaying the capture on the GPU; Windows Developer Mode required)...");
        bool customized = SelectedAdapter.HasValue || SelectedPowerState.HasValue || SelectedFlags.HasValue;
        PIX_ANALYSIS_PARAMS? parameters = null;
        if (customized)
        {
            parameters = new PIX_ANALYSIS_PARAMS
            {
                Adapter = SelectedAdapter ?? (Adapters is { Count: > 0 } ? Adapters[0].Id : 0),
                PowerState = SelectedPowerState ?? 0,
                Flags = SelectedFlags ?? PIX_ANALYSIS_FLAGS.PIX_ANALYSIS_FLAG_NONE,
            };
        }
        InvokeStartAnalysis(parameters, job, (p, notifications, cancellation) =>
        {
            if (p is PIX_ANALYSIS_PARAMS explicitParameters)
                analysis.StartAnalysis(&explicitParameters, notifications!, cancellation!);
            else
                analysis.StartAnalysis(null, notifications!, cancellation!);
        });

        if (parameters is PIX_ANALYSIS_PARAMS effective)
        {
            SelectedAdapter = effective.Adapter;
            SelectedPowerState = effective.PowerState;
            SelectedFlags = effective.Flags;
        }
        AnalysisStarted = true;
        AnalysisStartedAt = DateTimeOffset.UtcNow;
        job?.AddMessage("Analysis started.");
    }

    internal static void InvokeStartAnalysis(PIX_ANALYSIS_PARAMS? parameters, Job? job,
        Action<PIX_ANALYSIS_PARAMS?, IPixGpuCaptureAnalysisNotifications?, IPixCancellationToken?> start)
    {
        job?.ThrowIfCancellationRequested();
        start(parameters, job?.Sink, job?.PixToken);
    }

    private static List<(ulong, string)> LoadAdapters(IPixGpuCaptureAnalysis analysis)
    {
        var list = new List<(ulong, string)>();
        IPixAdapters adapters = PixApiExtensionsGpuCaptureAnalysis.GetAdapters(analysis);
        ulong count = adapters.GetCount();
        for (ulong i = 0; i < count; i++)
        {
            PIX_ADAPTER adapter = PixApiExtensionsDeviceConnectionResults.GetAdapter(adapters, i);
            list.Add((adapter.Id, Interop.W(adapter.Name)));
        }
        return list;
    }

    public List<object> PowerStates(ulong adapterId)
    {
        var list = new List<object>();
        IPixGpuCaptureAnalysis analysis = GetAnalysis();
        var adapter = new PIX_ADAPTER { Id = adapterId };
        IPixPowerStates states = PixApiExtensionsGpuCaptureAnalysis.GetPowerStates(analysis, adapter);
        ulong count = states.GetCount();
        for (ulong i = 0; i < count; i++)
        {
            PIX_POWER_STATE state = PixApiExtensionsDeviceConnectionResults.GetPowerState(states, i);
            list.Add(new { id = state.Id, name = Interop.W(state.Name), description = Interop.WOrNull(state.Description) });
        }
        return list;
    }

    public void StopAnalysis(List<string> warnings)
    {
        if (Analysis is not null && AnalysisStarted)
        {
            try { Analysis.StopAnalysis(); }
            catch (Exception ex) { warnings.Add("StopAnalysis: " + PixErrors.Describe(ex)); }
        }
        if (Analysis is not null && AnalysisConnected)
        {
            try { Analysis.Disconnect(); }
            catch (Exception ex) { warnings.Add("Disconnect: " + PixErrors.Describe(ex)); }
        }
        AnalysisStarted = false;
        AnalysisStartedAt = null;
        AnalysisConnected = false;
        Adapters = null;
        Timing = null;
        TimingRowsByQueue.Clear();
        TimingReadbackByQueue.Clear();
        TimingTreeByQueue.Clear();
        Counters = null;
        CounterList = null;
        CollectedCounters.Clear();
        CounterCollections.Clear();
        HighFrequencyCollections.Clear();
        OccupancyData = null;
        HighFrequencyCatalog = null;
        OptionalUnavailable.Clear();
        _capabilities.Clear();
        ResetAccessedResources();
        ShaderIndex = null;
        DrPix = null;
        Experiments = null;
        Analysis = null;
        // Finished preparation jobs describe state that no longer exists; a running one is on this
        // same thread's queue and will re-check readiness itself.
        lock (PreparationGate)
        {
            foreach (KeyValuePair<string, Job> entry in PreparationJobs)
            {
                if (entry.Value.IsFinished) PreparationJobs.TryRemove(entry.Key, out _);
            }
            PendingAnalysisOptions = null;
        }
    }

    /// <summary>Preparation shared by every tool that needs GPU analysis (replay) to have started.</summary>
    internal static Preparation<GpuCaptureHandle> AnalysisPreparation(string handle)
        => new("analysis", "analysis", $"Start GPU analysis for {handle}", h => h.AnalysisStarted, (h, job) => h.EnsureAnalysisStarted(job))
        { JoinKeys = PreparationKeys.StartingAnalysis, Result = h => h.AnalysisStatus() };

    public object AnalysisStatus() => new
    {
        handle = Id,
        connected = AnalysisConnected,
        started = AnalysisStarted,
        startedAt = AnalysisStartedAt,
        adapters = Adapters?.Select(a => new { id = a.Id, name = a.Name }).ToArray(),
        selectedAdapter = SelectedAdapter,
        selectedPowerState = SelectedPowerState,
        flags = SelectedFlags,
        timingCollected = Timing is not null,
        countersCollected = CollectedCounters.Keys.ToArray(),
        replayVendor = ReplayVendor(),
        captureVendor = CachedCaptureVendor,
    };

    public override object Summary() => new
    {
        handle = Id,
        kind = Kind,
        path = Path,
        openedAt = OpenedAt,
        queues = Queues.Select(q => q.ToDto()).ToArray(),
        totalEvents = Queues.Sum(q => (long)q.EventCount),
        analysisStarted = AnalysisStarted,
    };

    public override void Close(List<string> warnings)
    {
        StopAnalysis(warnings);
        foreach (QueueEntry queue in Queues)
        {
            queue.Cache = null;
        }
        _childCounts.Clear();
        Document = null!;
    }
}
