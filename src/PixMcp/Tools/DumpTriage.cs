using System.ComponentModel;
using System.Globalization;
using Microsoft.PIX;
using Microsoft.PIX.Extension;
using Microsoft.PIX.Internal;
using Microsoft.PIX.Internal.Extension.PostmortemDump;
using Microsoft.PIX.Internal.Extension.ShaderDebugging;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using PixMcp.Pix.Handles;

namespace PixMcp.Tools;

/// <summary>Stable collection-index path within a dump queue, independent of duplicate native event IDs.</summary>
public sealed record DumpEventRef(
    [property: Description("Dump handle (from pix_dump_open).")] string Handle,
    [property: Description("Queue index in the dump (from pix_dump_queues).")] int QueueIndex,
    [property: Description("Path of child indices from the queue's root events to the event (from pix_dump_events).")] int[] EventPath)
{
    internal DumpEventRef Child(int index) => this with { EventPath = [.. EventPath, index] };
}

/// <summary>What a triage section read: available, absent, unavailable, truncated or unsupported.</summary>
public sealed record DumpTriageCoverage(string State, int? Count = null, string? Reason = null);
internal sealed record DumpQueueRead<T>(List<T> Items, DumpTriageCoverage Coverage);
public sealed record DumpTriageObservation(string Id, int Priority, string Kind, string Summary, object Evidence, ToolCallDto[] NextCalls);
public sealed record DumpEventResultDto(DumpEventRef EventRef, object Details, PageResult<object> Children);
public sealed record DumpTriageDto(string Handle, object Device, string Interpretation, DumpDiagnosisDto? Diagnosis,
    IReadOnlyDictionary<string, DumpTriageCoverage> Coverage, IReadOnlyDictionary<string, int> EventStatusCounts,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> EventStatusCountsByQueue, DumpJournalSummaryDto? Journal,
    PageResult<DumpTriageObservation> Observations, IReadOnlyList<ToolCallDto> NextCalls);

public static partial class DumpTools
{
    [McpServerTool(Name = "pix_dump_event", Title = "Dump event detail", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Inspects one dump event using its stable eventRef and pages its immediate children. Use child eventRefs recursively to retrieve every descendant, including nodes cut from pix_dump_events.")]
    public static Task<string> Event(PixSession session,
        [Description("Unmodified eventRef returned by dump events or triage.")] DumpEventRef eventRef,
        [Description("First immediate child index.")] int offset = 0,
        [Description("Maximum immediate children, 1 through 1000. Default: 25.")] int limit = 25,
        [Description("Include correlated shaders and resources. Default: true.")] bool includeCorrelations = true,
        CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_dump_event", () =>
        {
            DumpHandle h = session.Get<DumpHandle>(eventRef.Handle);
            IPixPostmortemEvent evt = FindEvent(h, eventRef);
            (int o, int l) = Paging.Normalize(offset, limit);
            int budget = 1;
            object details = EventDto(evt, eventRef, 0, 0, includeCorrelations, ref budget);
            IPixCollection? children = PixApiExtensionsPostmortemDump.TryGetChildEvents(evt, out Exception ex);
            if (children is null && ex is not null) return new DumpEventResultDto(eventRef, details, Paging.Unavailable(o, l, "children", ex));
            var rows = new List<object>();
            ulong count = children?.GetCount() ?? 0;
            for (ulong i = (ulong)o; i < count && rows.Count < l; i++)
            {
                budget = 1;
                rows.Add(EventDto(children!.Get<IPixPostmortemEvent>(i), eventRef.Child(checked((int)i)), 0, 0, includeCorrelations, ref budget));
            }
            return new DumpEventResultDto(eventRef, details, Paging.Page(rows, checked((long)count), o, l));
        }, cancellationToken);

    private static IPixPostmortemEvent FindEvent(DumpHandle h, DumpEventRef reference)
    {
        List<IPixPostmortemQueueInfo> queues = Queues(h);
        if (reference.QueueIndex < 0 || reference.QueueIndex >= queues.Count || reference.EventPath is not { Length: > 0 } || reference.EventPath.Any(i => i < 0))
            throw new PixToolException(PixErrors.Codes.InvalidReference, "A dump eventRef needs a valid queueIndex and a nonempty path of nonnegative collection indices.", false,
                [new ToolCallDto("pix_dump_queues", new { handle = h.Id }, CostHints.Query)]);
        IPixCollection? collection = PixApiExtensionsPostmortemDump.TryGetEvents(queues[reference.QueueIndex], out _);
        IPixPostmortemEvent? result = null;
        for (int depth = 0; depth < reference.EventPath.Length; depth++)
        {
            int index = reference.EventPath[depth];
            if (collection is null || (ulong)index >= collection.GetCount())
                throw new PixToolException(PixErrors.Codes.InvalidReference, $"Dump eventRef path is out of range at depth {depth}.", false,
                    [new ToolCallDto("pix_dump_events", new { handle = h.Id, queueIndex = reference.QueueIndex }, CostHints.Query)]);
            result = collection.Get<IPixPostmortemEvent>((ulong)index);
            if (depth + 1 < reference.EventPath.Length) collection = PixApiExtensionsPostmortemDump.TryGetChildEvents(result, out _);
        }
        return result!;
    }

    [McpServerTool(Name = "pix_dump_triage", Title = "Triage dump", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Builds a deterministic evidence report for device removal: page faults with resource lifetime evidence, PIX's own (heuristic) diagnosis, D3D runtime journal errors, queue hardware status, shader exceptions, in-progress and possibly-completed events, fault or hang GPU state tables, and DRED breadcrumb boundaries. Traverses children even when their parent completed, up to maxEvents events. Returns ranked observations and explicit coverage; observations do not establish a causal diagnosis. Page with offset/limit.")]
    public static Task<string> Triage(PixSession session,
        [Description("Open dump handle.")] string handle,
        [Description("First ranked observation.")] int offset = 0,
        [Description("Maximum observations, default 10, maximum 1000.")] int limit = 10,
        [Description("Maximum events visited across every queue's event tree (default 5000, maximum 50000); a truncated walk reports truncated coverage and continuation calls.")] int maxEvents = TriageObservationBuilder.DefaultMaxEvents,
        CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_dump_triage", () =>
        {
            if (maxEvents is < 1 or > TriageObservationBuilder.MaxEvents)
                throw PixErrors.InvalidArguments($"maxEvents must be 1 through {TriageObservationBuilder.MaxEvents}.");
            DumpHandle h = session.Get<DumpHandle>(handle);
            (int o, int l) = Paging.Normalize(offset, limit);
            var coverage = new Dictionary<string, DumpTriageCoverage>();
            var observations = new List<DumpTriageObservation>();
            // Event-status and hardware-status observations wait for the correlation bonus, which needs every queue's evidence.
            var queueObservations = new List<(DumpTriageObservation Observation, int QueueIndex)>();
            var statusCounts = new Dictionary<string, int>();
            var statusCountsByQueue = new Dictionary<string, IReadOnlyDictionary<string, int>>();
            var faultingQueues = new HashSet<int>();
            var nextCalls = new List<ToolCallDto>();

            void Section(string name, Func<int?> read)
            {
                coverage[name] = ReadCoverage(read);
            }

            static long? Count(Func<ulong?> read)
            {
                try { return read() is ulong value ? (long)value : null; }
                catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
            }

            Section("pageFaults", () =>
            {
                IPixCollection? collection = PixApiExtensionsPostmortemDump.TryGetPageFaults(h.Document, out Exception ex);
                if (collection is null) { if (ex is not null) throw ex; return null; }
                int index = 0;
                foreach (IPixPostmortemPageFault fault in Interop.Items<IPixPostmortemPageFault>(collection))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int faultIndex = index++;
                    object? resources = Tools.Try(() =>
                    {
                        IPixCollection? events = PixApiExtensionsPostmortemDump.TryGetResourceEvents(fault, out Exception resourceError);
                        if (events is null) { if (resourceError is not null) throw resourceError; return null; }
                        return Interop.Items<IPixPostmortemResourceEvent>(events).Select(entry => new
                        {
                            type = entry.GetType(), timestampNs = entry.GetTimestampInNs(),
                            resource = Tools.Try(() =>
                            {
                                IPixPostmortemD3D12Resource? resource = PixApiExtensionsPostmortemDump.TryGetResource(entry, out Exception error);
                                if (resource is null) { if (error is not null) throw error; return null; }
                                return new { name = Interop.WOrNull(resource.GetName()), apiObjectId = Interop.Hex(resource.GetApiObjectId()),
                                    gpuVirtualAddress = Interop.Hex(resource.GetGpuVirtualAddress()), sizeBytes = resource.GetSizeBytes(),
                                    containsFaultAddress = AddressInResource(fault.GetGpuVirtualAddress(), resource.GetGpuVirtualAddress(), resource.GetSizeBytes()) };
                            }, "resource"),
                        }).ToArray();
                    }, "resourceLifetime");
                    observations.Add(new($"fault-{faultIndex}", 100, "pageFault", $"GPU page fault at {Interop.Hex(fault.GetGpuVirtualAddress())}.",
                        new { faultIndex, id = fault.GetId().ToString(), gpuVirtualAddress = Interop.Hex(fault.GetGpuVirtualAddress()), type = fault.GetType(),
                            accessType = fault.GetAccessType(), timestampNs = fault.GetTimestampInNs(), resourceLifetime = resources },
                        [new("pix_dump_page_faults", new { handle, offset = faultIndex, limit = 1 })]));
                }
                return index;
            });

            Section("dredPageFault", () =>
            {
                DredPageFaultData? data = PixApiExtensionsPostmortemDump.TryGetDredPageFault(h.Document, out Exception ex);
                if (!data.HasValue) { if (ex is not null) throw ex; return null; }
                if (data.Value.PageFaultVA != 0)
                    observations.Add(new("dred-page-fault", 95, "dredPageFault", $"DRED reports fault address {Interop.Hex(data.Value.PageFaultVA)}.",
                        new { gpuVirtualAddress = Interop.Hex(data.Value.PageFaultVA),
                            existingAllocations = data.Value.ExistingAllocations?.Select(a => new { name = a.ObjectName, type = a.AllocationType }).ToArray(),
                            freedAllocations = data.Value.FreedAllocations?.Select(a => new { name = a.ObjectName, type = a.AllocationType }).ToArray() },
                        [new("pix_dump_page_faults", new { handle, limit = 1 })]));
                return data.Value.PageFaultVA != 0 ? 1 : 0;
            });

            bool truncated = false;
            coverage["events"] = QueueEventCoverage(ReadQueues(h), queues =>
            {
                int visited = 0;
                foreach ((IPixPostmortemQueueInfo queue, int queueIndex) in queues.Select((q, i) => (q, i)))
                {
                    if (truncated)
                    {
                        coverage[$"queueEvents:{queueIndex}"] = new("truncated", 0, $"maxEvents ({maxEvents}) was reached before this queue.");
                        continue;
                    }
                    IPixCollection? roots = PixApiExtensionsPostmortemDump.TryGetEvents(queue, out Exception ex);
                    if (roots is null)
                    {
                        coverage[$"queueEvents:{queueIndex}"] = new(ex is null ? "absent" : "unavailable", Reason: ex is null ? null : PixErrors.Describe(ex));
                        continue;
                    }
                    var counts = new Dictionary<string, int>();
                    statusCountsByQueue[queueIndex.ToString(CultureInfo.InvariantCulture)] = counts;
                    int queueVisited = 0;
                    foreach (var entry in Walk(Interop.Items<IPixPostmortemEvent>(roots), (evt, path) =>
                    {
                        IPixCollection? children = PixApiExtensionsPostmortemDump.TryGetChildEvents(evt, out Exception childError);
                        if (children is null && childError is not null)
                            coverage[TriageObservationBuilder.EventChildrenCoverageKey(queueIndex, path)] = new("unavailable", Reason: PixErrors.Describe(childError));
                        return children is null ? [] : Interop.Items<IPixPostmortemEvent>(children);
                    }))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (visited >= maxEvents)
                        {
                            truncated = true;
                            nextCalls.AddRange(TriageObservationBuilder.WalkContinuation(handle, queueIndex, entry.Path[0], maxEvents));
                            break;
                        }
                        visited++;
                        queueVisited++;
                        string status = Json.EnumName(entry.Node.GetStatus());
                        statusCounts[status] = statusCounts.GetValueOrDefault(status) + 1;
                        counts[status] = counts.GetValueOrDefault(status) + 1;
                        int priority = EventPriority(status);
                        if (priority == 0) continue;
                        var eventRef = new DumpEventRef(handle, queueIndex, entry.Path);
                        queueObservations.Add((new($"event-{queueIndex}-{string.Join('-', entry.Path)}", priority, "eventStatus",
                            $"{EventName(entry.Node)} has status {status}.",
                            new { eventRef, status, name = EventName(entry.Node), isGpuWork = (bool)entry.Node.IsGpuWork(), queueName = Interop.W(queue.GetName()) },
                            [new("pix_dump_event", new { eventRef })]), queueIndex));
                    }
                    coverage[$"queueEvents:{queueIndex}"] = truncated
                        ? new("truncated", queueVisited, $"maxEvents ({maxEvents}) was reached in this queue.")
                        : new("available", queueVisited);
                }
                return visited;
            });
            if (truncated)
                coverage["events"] = new("truncated", coverage["events"].Count, $"The event walk stopped after maxEvents ({maxEvents}) events; nextCalls continue it.");

            Section("breadcrumbs", () =>
            {
                List<BreadcrumbNodeData>? nodes = PixApiExtensionsPostmortemDump.TryGetBreadcrumbNodes(h.Document, out Exception ex);
                if (nodes is null) { if (ex is not null) throw ex; return null; }
                for (int index = 0; index < nodes.Count; index++)
                {
                    BreadcrumbNodeData node = nodes[index];
                    int total = node.Ops?.Length ?? 0;
                    if (node.CompletedCount >= total) continue;
                    var window = BreadcrumbWindow(total, node.CompletedCount, 10, null);
                    observations.Add(new($"breadcrumb-{index}", 50, "breadcrumbBoundary", $"{node.CommandListName ?? "Command list"}: {node.CompletedCount} of {total} operations completed.",
                        new { nodeIndex = index, commandList = node.CommandListName, commandQueue = node.CommandQueueName, completedCount = node.CompletedCount, opCount = total,
                            ops = node.Ops?.Skip(window.Offset).Take(window.Count).Select((op, i) => new { index = window.Offset + i, op, completed = window.Offset + i < node.CompletedCount }).ToArray() },
                        [new("pix_dump_breadcrumbs", new { handle, nodeIndex = index })]));
                }
                return nodes.Count;
            });

            Section("shaderWaves", () =>
            {
                IPixShaderDebuggingData? data = PixApiExtensionsPostmortemDump.TryGetShaderData(h.Document, out Exception ex);
                if (data is null) { if (ex is not null) throw ex; return null; }
                IPixCollection? waves = PixApiExtensionsShaderDebugging.TryGetWaves(data, out Exception waveError);
                if (waves is null) { if (waveError is not null) throw waveError; return null; }
                int index = 0;
                foreach (IPixShaderWave wave in Interop.Items<IPixShaderWave>(waves))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int waveIndex = index++;
                    if (Convert.ToUInt64(wave.GetExceptionsHit()) == 0) continue;
                    observations.Add(new($"shader-wave-{waveIndex}", 80, "shaderException", $"Shader wave {waveIndex} reports {Json.EnumName(wave.GetExceptionsHit())}.",
                        WaveDto(wave, handle, waveIndex, false, true),
                        [new("pix_dump_shader_wave", new { handle, waveIndex })]));
                }
                return index;
            });

            DumpDiagnosisDto? diagnosis = null;
            Section("pixDiagnosis", () =>
            {
                diagnosis = h.Diagnosis ??= Diagnosis(h);
                DumpTriageObservation? observation = TriageObservationBuilder.Diagnosis(diagnosis, handle);
                if (observation is not null) observations.Add(observation);
                return observation is null ? 0 : 1;
            });

            DumpJournalSummaryDto? journal = null;
            Section("journal", () =>
            {
                IPixCollection? entries = PixApiExtensionsPostmortemDump.TryGetD3DJournalEntries(h.Document, out Exception ex);
                if (entries is null) { if (ex is not null) throw ex; return null; }
                int total = checked((int)entries.GetCount());
                var window = new List<JournalEntryData>();
                for (int i = Math.Max(0, total - TriageObservationBuilder.JournalWindow); i < total; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    IPixD3DJournalEntry entry = entries.Get<IPixD3DJournalEntry>((ulong)i);
                    window.Add(new(i, entry.GetCode(), entry.GetThreadID(), entry.GetTickCount(), Interop.WOrNull(entry.GetErrorMessage())));
                }
                (journal, List<DumpTriageObservation> found) = TriageObservationBuilder.Journal(window, total, handle);
                observations.AddRange(found);
                return total;
            });

            coverage["queueStatus"] = QueueEventCoverage(ReadQueues(h), queues =>
            {
                int statuses = 0;
                for (int queueIndex = 0; queueIndex < queues.Count; queueIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    IPixPostmortemQueueInfo queue = queues[queueIndex];
                    long? pageFaults = Count(() => PixApiExtensionsPostmortemDump.GetPageFaults(queue)?.GetCount() ?? 0);
                    long? rootEvents = Count(() => PixApiExtensionsPostmortemDump.TryGetEvents(queue, out _)?.GetCount());
                    if (pageFaults > 0) faultingQueues.Add(queueIndex);
                    List<HardwareStatusData> hardware;
                    try
                    {
                        hardware = PixApiExtensionsPostmortemDump.GetHardwareStatuses(queue)
                            .Select(s => new HardwareStatusData(s.Id, Interop.WOrNull(s.Name), Interop.WOrNull(s.Description), s.SeverityLevel, Reflect.ToObject(s.Value))).ToList();
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        coverage[$"queueStatus:{queueIndex}"] = new("unavailable", Reason: PixErrors.Describe(ex));
                        continue;
                    }
                    statuses += hardware.Count;
                    int index = queueIndex;
                    queueObservations.AddRange(TriageObservationBuilder.QueueStatus(index, Interop.W(queue.GetName()), hardware, pageFaults, rootEvents, handle)
                        .Select(observation => (observation, index)));
                }
                return statuses;
            });

            Section("gpuState", () =>
            {
                IPixCollection? tables = PixApiExtensionsPostmortemDump.TryGetGpuStateTables(h.Document, out Exception ex);
                if (tables is null) { if (ex is not null) throw ex; return null; }
                int index = 0;
                foreach (IPixGpuStateTable table in Interop.Items<IPixGpuStateTable>(tables))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int tableIndex = index++;
                    string name = Interop.W(table.GetName());
                    string? description = Interop.WOrNull(table.GetDescription());
                    if (!TriageObservationBuilder.IsInterestingGpuStateTable(name, description)) continue;
                    ulong rows = PixApiExtensionsPostmortemDump.TryGetRows(table, out _)?.GetCount() ?? 0;
                    if (rows > 0) observations.Add(TriageObservationBuilder.GpuStateTable(tableIndex, name, description, rows, handle));
                }
                return index;
            });

            coverage["d3dState"] = TriageObservationBuilder.D3DStateCoverage();

            var suspectQueues = new HashSet<int>(faultingQueues);
            foreach ((string queue, IReadOnlyDictionary<string, int> counts) in statusCountsByQueue)
            {
                if (counts.GetValueOrDefault("IN_PROGRESS") > 0) suspectQueues.Add(int.Parse(queue, CultureInfo.InvariantCulture));
            }
            observations.AddRange(queueObservations.Select(entry => TriageObservationBuilder.WithCorrelation(entry.Observation, suspectQueues.Contains(entry.QueueIndex))));

            DumpTriageObservation[] ranked = RankObservations(observations);
            if ((long)o + l < ranked.Length) nextCalls.Insert(0, new ToolCallDto("pix_dump_triage", new { handle, offset = o + l, limit = l, maxEvents }));
            return new DumpTriageDto(handle, h.Metadata ??= Metadata(h),
                "Observed evidence only. PIX's diagnosis is its own heuristic summary, and completion status, nearby resource lifetimes, journal errors, hardware status and shader exceptions do not independently prove the cause of device removal.",
                diagnosis, coverage, statusCounts, statusCountsByQueue, journal,
                Paging.Page(ranked.Skip(o).Take(l).ToArray(), ranked.Length, o, l), nextCalls);
        }, cancellationToken);

    /// <summary>PIX's own diagnosis of the dump; each field is null when PIX cannot provide it. Cached on the handle and shared with pix_dump_info.</summary>
    internal static DumpDiagnosisDto Diagnosis(DumpHandle h)
    {
        IPixPostmortemDocument d = h.Document;
        static string? Read(Func<string?> read)
        {
            try { return read(); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
        }
        return new DumpDiagnosisDto(
            Read(() => Json.EnumName(d.GetDeviceErrorCode())),
            Read(() => Interop.WOrNull(d.GetDeviceErrorBucket())),
            Read(() => Interop.WOrNull(d.GetGpuStatus())),
            Read(() => Annotated(PixApiExtensionsPostmortemDump.TryGetBriefSummary(d, out _))),
            Read(() => PixApiExtensionsPostmortemDump.TryGetDetailedSummary(d, out _) is null ? null : "available") is not null,
            Read(() => Interop.WOrNull(d.GetDocumentationLink())));
    }

    internal static bool AddressInResource(ulong address, ulong start, ulong size) => address >= start && address - start < size;
    internal static DumpTriageCoverage QueueEventCoverage<T>(DumpQueueRead<T> queues, Func<IReadOnlyList<T>, int> readEvents)
        => queues.Coverage.State == "available" ? ReadCoverage(() => readEvents(queues.Items)) : queues.Coverage;

    internal static DumpTriageCoverage ReadCoverage(Func<int?> read)
    {
        try
        {
            int? count = read();
            return new(count.HasValue ? "available" : "absent", count);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new("unavailable", Reason: PixErrors.Describe(ex)); }
    }
    internal static int EventPriority(string status) => status switch { "IN_PROGRESS" => 60, "POSSIBLY_COMPLETED" => 40, "UNKNOWN" => 20, _ => 0 };
    internal static DumpTriageObservation[] RankObservations(IEnumerable<DumpTriageObservation> observations)
        => observations.OrderByDescending(o => o.Priority).ThenBy(o => o.Id, StringComparer.Ordinal).ToArray();

    /// <summary>Iterative pre-order traversal; completion status never prunes descendants.</summary>
    internal static IEnumerable<(T Node, int[] Path)> Walk<T>(IEnumerable<T> roots, Func<T, IEnumerable<T>> children)
        => Walk(roots, (node, _) => children(node));

    /// <summary>Iterative pre-order traversal that hands the children callback each node's collection path.</summary>
    internal static IEnumerable<(T Node, int[] Path)> Walk<T>(IEnumerable<T> roots, Func<T, int[], IEnumerable<T>> children)
    {
        var stack = new Stack<(IEnumerator<T> Enumerator, int[] Parent, int Index)>();
        stack.Push((roots.GetEnumerator(), [], -1));
        try
        {
            while (stack.Count > 0)
            {
                var level = stack.Pop();
                if (!level.Enumerator.MoveNext()) { level.Enumerator.Dispose(); continue; }
                int index = level.Index + 1;
                T node = level.Enumerator.Current;
                stack.Push((level.Enumerator, level.Parent, index));
                int[] path = [.. level.Parent, index];
                yield return (node, path);
                stack.Push((children(node, path).GetEnumerator(), path, -1));
            }
        }
        finally { foreach (var level in stack) level.Enumerator.Dispose(); }
    }
}
