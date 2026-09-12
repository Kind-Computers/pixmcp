using System.ComponentModel;
using System.Text;
using Microsoft.PIX;
using Microsoft.PIX.Extension;
using Microsoft.PIX.Internal;
using Microsoft.PIX.Internal.Extension.PostmortemDump;
using Microsoft.PIX.Internal.Extension.ShaderDebugging;
using Microsoft.PIX.Internal.Extension.Shaders;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using PixMcp.Pix.Handles;

namespace PixMcp.Tools;

/// <summary>DirectX dump file (.dxdmp_preview, produced on TDR / device removal) tools. Experimental PIX API surface.</summary>
[McpServerToolType]
public static partial class DumpTools
{
    [McpServerTool(Name = "pix_dump_open"), Description("Opens a DirectX dump file (.dxdmp_preview, written when a GPU hang/TDR occurs) and returns a handle plus metadata: device error, error bucket, PIX's brief and detailed summaries, application, adapter, OS, CPU and memory info, and the queue list.")]
    public static Task<string> Open(PixSession session, [Description("Path to the .dxdmp_preview / .dxdmp file.")] string path, CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_dump_open", () =>
        {
            string full = Tools.RequireFile(path, "Dump file");
            IPixPostmortemDocument document = PixApiExtensionsPostmortemDump.OpenPostmortemDumpDocument(session.Factory, full, null!, null!, null!);
            DumpHandle handle = session.Register(new DumpHandle(full, document));
            handle.Metadata = Metadata(handle);
            return new { handle = handle.Id, path = full, metadata = handle.Metadata, queues = QueueList(handle) };
        }, cancellationToken);

    [McpServerTool(Name = "pix_dump_info", ReadOnly = true), Description("Metadata (device error, summaries, system info) and the plain queue list for an open dump file; the same payload pix_dump_open returned. Use pix_dump_queues for per-queue hardware status and fault/event counts.")]
    public static Task<string> Info(PixSession session, [Description("Dump handle")] string handle, CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_dump_info", () =>
        {
            DumpHandle h = session.Get<DumpHandle>(handle);
            return new { handle = h.Id, path = h.Path, metadata = h.Metadata ??= Metadata(h), queues = QueueList(h) };
        }, cancellationToken);

    private static object Metadata(DumpHandle h)
    {
        IPixPostmortemDocument d = h.Document;
        object? Try(Func<object?> f, string what)
        {
            try { return f(); }
            catch (Exception ex) { return PixErrors.Unavailable(what, ex); }
        }

        object? application = Try(() =>
        {
            PIX_APPLICATION_DESC desc = Internal_IPixPostmortemDocument_Extensions.GetApplicationDescription(d);
            return new
            {
                name = Interop.WOrNull(desc.D3DApplicationDesc.pName),
                exeFilename = Interop.WOrNull(desc.D3DApplicationDesc.pExeFilename),
                version = Interop.VersionString(desc.D3DApplicationDesc.Version),
                engineName = Interop.WOrNull(desc.D3DApplicationDesc.pEngineName),
                engineVersion = Interop.VersionString(desc.D3DApplicationDesc.EngineVersion),
                appId = desc.AppId,
            };
        }, "application");

        return new
        {
            version = Try(() => d.GetVersion(), "version"),
            guid = Try(() => PixApiExtensionsPostmortemDump.TryGetGuid(d, out _), "guid"),
            deviceErrorCode = Try(() => d.GetDeviceErrorCode(), "deviceErrorCode"),
            creationTime = Try(() => Interop.FileTime(Internal_IPixPostmortemDocument_Extensions.GetCreationTime(d)), "creationTime"),
            uploadedToWatson = Try(() => (bool)d.WasUploadedToWatson(), "uploadedToWatson"),
            liveDebuggingDone = Try(() => (bool)d.WasLiveDebuggingDone(), "liveDebuggingDone"),
            errorBucket = Try(() => Interop.WOrNull(d.GetDeviceErrorBucket()), "errorBucket"),
            documentationLink = Try(() => Interop.WOrNull(d.GetDocumentationLink()), "documentationLink"),
            gpuStatus = Try(() => Interop.WOrNull(d.GetGpuStatus()), "gpuStatus"),
            briefSummary = Try(() => Annotated(PixApiExtensionsPostmortemDump.TryGetBriefSummary(d, out _)), "briefSummary"),
            detailedSummary = Try(() => Annotated(PixApiExtensionsPostmortemDump.TryGetDetailedSummary(d, out _)), "detailedSummary"),
            application,
            adapter = Try(() => Reflect.ToObject(Internal_IPixPostmortemDocument_Extensions.GetAdapterInfo(d)), "adapter"),
            os = Try(() => Reflect.ToObject(Internal_IPixPostmortemDocument_Extensions.GetOsInfo(d)), "os"),
            cpu = Try(() => Reflect.ToObject(Internal_IPixPostmortemDocument_Extensions.GetCpuInfo(d)), "cpu"),
            cpuPackages = Try(() =>
            {
                uint count = d.GetCpuPackageCount();
                var list = new List<object?>();
                for (uint i = 0; i < count; i++)
                {
                    list.Add(Reflect.ToObject(PixApiExtensionsPostmortemDump.GetCpuPackageInfo(d, i)));
                }
                return list;
            }, "cpuPackages"),
            memory = Try(() => Reflect.ToObject(Internal_IPixPostmortemDocument_Extensions.GetSystemMemoryInfo(d)), "memory"),
            deviceConfiguration = Try(() => Reflect.ToObject(Internal_IPixPostmortemDocument_Extensions.GetDeviceConfigDesc(d)), "deviceConfiguration"),
            featureData = Try(() => Reflect.ToObject(Internal_IPixPostmortemDocument_Extensions.GetFeatureData(d)), "featureData"),
            driverOptions = Try(() => Reflect.ToObject(d.GetConfiguration()), "driverOptions"),
        };
    }

    /// <summary>Flattens an annotated string to markdown-style text, linking annotations to their context (queue/event/shader).</summary>
    private static string? Annotated(IPixAnnotatedString? annotated)
    {
        if (annotated is null)
        {
            return null;
        }
        string raw = Interop.W(annotated.GetString());
        IPixCollection? annotations = PixApiExtensionsAnnotatedString.TryGetAnnotations(annotated, out _);
        if (annotations is null || annotations.GetCount() == 0)
        {
            return raw;
        }

        var sb = new StringBuilder();
        int current = 0;
        foreach (IPixStringAnnotation annotation in Interop.Items<IPixStringAnnotation>(annotations))
        {
            PIX_STRING_ANNOTATION_RANGE range;
            try { range = PixApiExtensionsAnnotatedString.GetRange(annotation); }
            catch { continue; }
            int start = (int)Math.Min(range.StartIndex, int.MaxValue);
            int length = (int)Math.Min(range.Length, int.MaxValue);
            if (start < current || start + length > raw.Length)
            {
                continue;
            }
            if (start > current)
            {
                sb.Append(raw, current, start - current);
            }
            string link = AnnotationLink(annotation);
            sb.Append('[').Append(raw, start, length).Append(']');
            if (!string.IsNullOrEmpty(link))
            {
                sb.Append('(').Append(link).Append(')');
            }
            current = start + length;
        }
        if (current < raw.Length)
        {
            sb.Append(raw, current, raw.Length - current);
        }
        return sb.ToString();
    }

    private static string AnnotationLink(IPixStringAnnotation annotation)
    {
        try
        {
            switch (annotation.GetContextType())
            {
                case PIX_STRING_ANNOTATION_CONTEXT_TYPE.PIX_STRING_ANNOTATION_QUEUE:
                {
                    IPixPostmortemQueueInfo? queue = PixApiExtensionsAnnotatedString.TryGetContext<IPixPostmortemQueueInfo>(annotation, out _);
                    return queue is null ? "queue" : $"queue:{Interop.W(queue.GetName())}";
                }
                case PIX_STRING_ANNOTATION_CONTEXT_TYPE.PIX_STRING_ANNOTATION_EVENT:
                {
                    IPixPostmortemEvent? evt = PixApiExtensionsAnnotatedString.TryGetContext<IPixPostmortemEvent>(annotation, out _);
                    return evt is null ? "event" : $"event:{evt.GetId()}:{EventName(evt)}";
                }
                case PIX_STRING_ANNOTATION_CONTEXT_TYPE.PIX_STRING_ANNOTATION_SHADER:
                    return "shader";
                case PIX_STRING_ANNOTATION_CONTEXT_TYPE.PIX_STRING_ANNOTATION_RESOURCE:
                {
                    IPixPostmortemD3D12Resource? res = PixApiExtensionsAnnotatedString.TryGetContext<IPixPostmortemD3D12Resource>(annotation, out _);
                    return res is null ? "resource" : $"resource:{Interop.Hex(res.GetGpuVirtualAddress())}:{Interop.W(res.GetName())}";
                }
                case PIX_STRING_ANNOTATION_CONTEXT_TYPE.PIX_STRING_ANNOTATION_PAGE_FAULT:
                    return "pageFault";
                default:
                    return Json.EnumName(annotation.GetContextType()).ToLowerInvariant();
            }
        }
        catch
        {
            return string.Empty;
        }
    }

    private static List<IPixPostmortemQueueInfo> Queues(DumpHandle h)
        => ReadQueues(h).Items;

    private static DumpQueueRead<IPixPostmortemQueueInfo> ReadQueues(DumpHandle h)
        => ReadQueueCollection(h.Queues, () =>
        {
            IPixCollection? queues = PixApiExtensionsPostmortemDump.TryGetQueues(h.Document, out Exception? error);
            if (queues is null)
            {
                if (error is not null) throw error;
                return null;
            }
            return Interop.Items<IPixPostmortemQueueInfo>(queues);
        }, queues => h.Queues = queues);

    /// <summary>Only complete successful collections are cached; unavailable and absent reads remain retryable.</summary>
    internal static DumpQueueRead<T> ReadQueueCollection<T>(List<T>? cached, Func<IEnumerable<T>?> load, Action<List<T>> publish)
    {
        if (cached is not null) return new(cached, new("available", cached.Count));
        try
        {
            IEnumerable<T>? collection = load();
            if (collection is null) return new([], new("absent"));
            List<T> items = collection.ToList();
            publish(items);
            return new(items, new("available", items.Count));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new([], new("unavailable", Reason: PixErrors.Describe(ex))); }
    }

    private static object[] QueueList(DumpHandle h)
        => Queues(h).Select((q, i) => (object)new
        {
            queueIndex = i,
            id = q.GetId(),
            name = Interop.W(q.GetName()),
            type = q.GetType(),
            status = q.GetStatus(),
            hardwareStatusCount = q.GetHardwareStatusCount(),
        }).ToArray();

    [McpServerTool(Name = "pix_dump_queues", ReadOnly = true), Description("Queues in the dump with their status at dump time, hardware status fields (severity/values), page fault counts and root event counts (more detail than the queue list in pix_dump_info). Feed queueIndex to pix_dump_events.")]
    public static Task<string> DumpQueues(PixSession session, [Description("Dump handle")] string handle, CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_dump_queues", () =>
        {
            DumpHandle h = session.Get<DumpHandle>(handle);
            return Queues(h).Select((q, i) =>
            {
                object? hw;
                try
                {
                    hw = PixApiExtensionsPostmortemDump.GetHardwareStatuses(q).Select(s => Reflect.ToObject(s)).ToArray();
                }
                catch (Exception ex) { hw = PixErrors.Unavailable("hardwareStatus", ex); }
                object? faults = Tools.Try(() => PixApiExtensionsPostmortemDump.GetPageFaults(q)?.GetCount() ?? 0, "pageFaultCount");
                object? eventCount = Tools.Try(() => PixApiExtensionsPostmortemDump.TryGetEvents(q, out _)?.GetCount(), "rootEventCount");
                return new
                {
                    queueIndex = i,
                    id = q.GetId(),
                    name = Interop.W(q.GetName()),
                    type = q.GetType(),
                    status = q.GetStatus(),
                    hardwareStatus = hw,
                    pageFaultCount = faults,
                    rootEventCount = eventCount,
                };
            }).ToArray();
        }, cancellationToken);

    private static string EventName(IPixPostmortemEvent evt)
    {
        try
        {
            return evt switch
            {
                IPixPostmortemD3D12ApiEvent api => Json.EnumName(api.GetName()),
                IPixPostmortemStringMarker sm => Interop.W(sm.GetPayload()),
                IPixPostmortemPixMarker pm => Interop.W(pm.GetName()),
                IPixPostmortemDriverEvent de => Interop.W(de.GetName()),
                IPixPostmortemCustomMarker cm => $"custom marker ({Json.EnumName(cm.GetSource())})",
                _ => Json.EnumName(evt.GetType()),
            };
        }
        catch
        {
            return "?";
        }
    }

    private static string EventKind(IPixPostmortemEvent evt) => evt switch
    {
        IPixPostmortemD3D12ApiEvent => "d3dApi",
        IPixPostmortemStringMarker => "stringMarker",
        IPixPostmortemBlobMarker => "blobMarker",
        IPixPostmortemIdentifierMarker => "identifierMarker",
        IPixPostmortemPixMarker => "pixMarker",
        IPixPostmortemDriverEvent => "driver",
        IPixPostmortemCustomMarker => "customMarker",
        _ => "event",
    };

    private static object EventDto(IPixPostmortemEvent evt, DumpEventRef eventRef, int depth, int maxDepth, bool includeCorrelations, ref int budget)
    {
        object? shaders = null;
        object? resources = null;
        if (includeCorrelations)
        {
            shaders = Tools.Try(() =>
            {
                IPixCollection? s = PixApiExtensionsPostmortemDump.TryGetCorrelatedShaders(evt, out _);
                return s is null || s.GetCount() == 0 ? null : Interop.Items<IPixShader>(s).Select(sh => ShaderSummary(sh)).ToArray();
            }, "correlatedShaders");
            resources = Tools.Try(() =>
            {
                IPixCollection? r = PixApiExtensionsPostmortemDump.TryGetCorrelatedResources(evt, out _);
                return r is null || r.GetCount() == 0 ? null : Interop.Items<IPixPostmortemD3D12Resource>(r).Select(res => new { name = Interop.WOrNull(res.GetName()), gpuVirtualAddress = Interop.Hex(res.GetGpuVirtualAddress()), sizeBytes = res.GetSizeBytes() }).ToArray();
            }, "correlatedResources");
        }

        object? children = null;
        object? childCount = null;
        bool childrenTruncated = false;
        try
        {
            IPixCollection? c = PixApiExtensionsPostmortemDump.TryGetChildEvents(evt, out _);
            if (c is not null)
            {
                ulong count = c.GetCount();
                childCount = count;
                if (depth < maxDepth && count > 0 && budget > 0)
                {
                    var list = new List<object>();
                    foreach (IPixPostmortemEvent child in Interop.Items<IPixPostmortemEvent>(c))
                    {
                        if (budget-- <= 0)
                        {
                            childrenTruncated = true;
                            break;
                        }
                        list.Add(EventDto(child, eventRef.Child(list.Count), depth + 1, maxDepth, includeCorrelations, ref budget));
                    }
                    children = list;
                }
                else if (count > 0)
                {
                    childrenTruncated = true;
                }
            }
        }
        catch (Exception ex) { childCount = PixErrors.Unavailable("children", ex); }

        return new
        {
            eventRef,
            id = evt.GetId(),
            kind = EventKind(evt),
            name = EventName(evt),
            type = evt.GetType(),
            status = evt.GetStatus(),
            isGpuWork = (bool)evt.IsGpuWork(),
            correlatedShaders = shaders,
            correlatedResources = resources,
            childCount,
            children,
            childrenTruncated = childrenTruncated ? true : (bool?)null,
            nextCalls = childrenTruncated ? new[] { new ToolCallDto("pix_dump_event", new { eventRef, offset = children is List<object> items ? items.Count : 0 }) } : null,
        };
    }

    private static object ShaderSummary(IPixShader shader)
    {
        string? hash = null;
        try
        {
            byte[]? bytes = PixApiExtensionsShaders.TryGetShaderHash(shader, out _);
            hash = bytes is { Length: > 0 } ? Convert.ToHexString(bytes).ToLowerInvariant() : null;
        }
        catch { }
        return new { id = shader.GetId(), stage = shader.GetStage(), hash, entry = Interop.WOrNull(shader.GetEntry()), target = Interop.WOrNull(shader.GetTarget()) };
    }

    [McpServerTool(Name = "pix_dump_events", ReadOnly = true), Description("Command queue event history from the dump (D3D API calls, PIX markers, custom markers, driver events) as a tree of root events with nested children, with completion status (IN_PROGRESS / POSSIBLY_COMPLETED marks work that was running when the GPU hung) and correlated shaders/resources. Root events are paged with offset/limit; maxEvents bounds the expanded tree (childrenTruncated marks cut nodes, extra.eventBudgetExhausted the page).")]
    public static Task<string> Events(
        PixSession session,
        [Description("Dump handle")] string handle,
        [Description("Queue index from pix_dump_queues.")] int queueIndex,
        [Description("First root event (default 0).")] int offset = 0,
        [Description("Maximum root events (default 50, max 1000).")] int limit = 50,
        [Description("Maximum child depth to expand (default 3, max 16).")] int maxDepth = 3,
        [Description("Maximum total events to expand including children (default 500, max 5000).")] int maxEvents = 500,
        [Description("Include correlated shaders/resources (default true).")] bool includeCorrelations = true,
        [Description("Only include root events with this status: IN_PROGRESS, POSSIBLY_COMPLETED, COMPLETED, NOT_STARTED.")] string? status = null,
        CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_dump_events", () =>
        {
            DumpHandle h = session.Get<DumpHandle>(handle);
            List<IPixPostmortemQueueInfo> queues = Queues(h);
            if (queueIndex < 0 || queueIndex >= queues.Count)
            {
                throw new McpException($"queueIndex {queueIndex} is out of range; the dump has {queues.Count} queue(s).");
            }
            (int o, int l) = Paging.Normalize(offset, limit);
            IPixCollection? events = PixApiExtensionsPostmortemDump.TryGetEvents(queues[queueIndex], out Exception ex);
            if (events is null)
            {
                return Paging.Unavailable(o, l, "events", ex, "The dump has no event history for this queue.");
            }
            PIX_EVENT_STATUS? wanted = string.IsNullOrEmpty(status) ? null : Tools.ParseEnum<PIX_EVENT_STATUS>(status);

            int budget = Math.Clamp(maxEvents, 1, 5000);
            var page = new List<object>();
            long total = 0;
            bool exhausted = false;
            int rootIndex = -1;
            foreach (IPixPostmortemEvent evt in Interop.Items<IPixPostmortemEvent>(events))
            {
                rootIndex++;
                if (wanted.HasValue && evt.GetStatus() != wanted.Value)
                {
                    continue;
                }
                if (total >= o && page.Count < l)
                {
                    if (budget > 0)
                    {
                        budget--;
                        page.Add(EventDto(evt, new DumpEventRef(h.Id, queueIndex, [rootIndex]), 0, Math.Clamp(maxDepth, 0, 16), includeCorrelations, ref budget));
                    }
                    else
                    {
                        exhausted = true;
                    }
                }
                total++;
            }
            return Paging.Page(page, total, o, l, exhausted ? new { eventBudgetExhausted = true, maxEvents } : null);
        }, cancellationToken);

    [McpServerTool(Name = "pix_dump_page_faults", ReadOnly = true), Description("GPU page faults recorded in the dump, paged: faulting virtual address, type, access, timestamp, queue, and the resource allocation/free events around that address (each capped by maxResourceEvents). extra.dred carries DRED page fault data when present.")]
    public static Task<string> PageFaults(
        PixSession session,
        [Description("Dump handle")] string handle,
        [Description("First fault (default 0).")] int offset = 0,
        [Description("Maximum faults (default 25, max 1000).")] int limit = Paging.DefaultLimit,
        [Description("Maximum resource events listed per fault (default 50, max 1000).")] int maxResourceEvents = 50,
        [Description("First resource lifetime event per fault; follow nextCalls for later windows.")] int resourceEventsOffset = 0,
        CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_dump_page_faults", () =>
        {
            DumpHandle h = session.Get<DumpHandle>(handle);
            (int o, int l) = Paging.Normalize(offset, limit);
            int maxEvents = Math.Clamp(maxResourceEvents, 1, Paging.MaxLimit);
            if (resourceEventsOffset < 0) throw new PixToolException("invalid_arguments", "resourceEventsOffset must be nonnegative.");
            object? dred = Tools.Try(() =>
            {
                DredPageFaultData? data = PixApiExtensionsPostmortemDump.TryGetDredPageFault(h.Document, out _);
                return !data.HasValue ? null : new
                {
                    pageFaultVA = Interop.Hex(data.Value.PageFaultVA),
                    existingAllocations = data.Value.ExistingAllocations?.Select(a => new { name = a.ObjectName, type = a.AllocationType }).ToArray(),
                    freedAllocations = data.Value.FreedAllocations?.Select(a => new { name = a.ObjectName, type = a.AllocationType }).ToArray(),
                };
            }, "dred");
            IPixCollection? pf = PixApiExtensionsPostmortemDump.TryGetPageFaults(h.Document, out Exception ex);
            if (pf is null)
            {
                return Paging.Page(Array.Empty<object>(), 0, o, l, new { dred, unavailable = ex is not null, reason = ex is null ? null : PixErrors.Describe(ex) });
            }
            return Paging.Collect(Interop.Items<IPixPostmortemPageFault>(pf).Select((fault, index) => (fault, index)), o, l, entry =>
            {
                IPixPostmortemPageFault fault = entry.fault;
                object? queue = Tools.Try(() =>
                {
                    IPixPostmortemQueueInfo? q = PixApiExtensionsPostmortemDump.TryGetQueue(fault, out _);
                    return q is null ? null : new { id = q.GetId(), name = Interop.W(q.GetName()) };
                }, "queue");
                int resourceEventCount = 0;
                object? resourceEvents = Tools.Try(() =>
                {
                    IPixCollection? re = PixApiExtensionsPostmortemDump.TryGetResourceEvents(fault, out _);
                    if (re is null) return null;
                    resourceEventCount = (int)Math.Min(re.GetCount(), int.MaxValue);
                    return Interop.Items<IPixPostmortemResourceEvent>(re).Skip(resourceEventsOffset).Take(maxEvents).Select(e => new
                    {
                        type = e.GetType(),
                        timestampNs = e.GetTimestampInNs(),
                        resource = Tools.Try(() =>
                        {
                            IPixPostmortemD3D12Resource? r = PixApiExtensionsPostmortemDump.TryGetResource(e, out _);
                            return r is null ? null : new { name = Interop.WOrNull(r.GetName()), gpuVirtualAddress = Interop.Hex(r.GetGpuVirtualAddress()), sizeBytes = r.GetSizeBytes() };
                        }, "resource"),
                    }).ToArray();
                }, "resourceEvents");
                return new
                {
                    faultIndex = entry.index,
                    id = fault.GetId(),
                    gpuVirtualAddress = Interop.Hex(fault.GetGpuVirtualAddress()),
                    type = fault.GetType(),
                    accessType = fault.GetAccessType(),
                    timestampNs = fault.GetTimestampInNs(),
                    queue,
                    resourceEventCount,
                    resourceEvents,
                    resourceEventsOffset,
                    resourceEventsTruncated = resourceEventCount > (long)resourceEventsOffset + maxEvents,
                    nextCalls = resourceEventCount > (long)resourceEventsOffset + maxEvents ? new[] { new ToolCallDto("pix_dump_page_faults", new
                        { handle, offset = entry.index, limit = 1, maxResourceEvents, resourceEventsOffset = resourceEventsOffset + maxEvents }) } : null,
                };
            }, new { dred });
        }, cancellationToken);

    [McpServerTool(Name = "pix_dump_breadcrumbs", ReadOnly = true), Description("DRED auto-breadcrumb nodes from the dump: per command list, the recorded operations and how many completed, plus context strings. Shows where each command list was when the GPU hung. Operations are windowed per node (opsOffset/returnedOps/nextOpsOffset, indices absolute): by default around the completion boundary, or from offset when given.")]
    public static Task<string> Breadcrumbs(PixSession session,
        [Description("Dump handle")] string handle,
        [Description("Maximum ops to list per node (default 200, max 5000).")] int maxOps = 200,
        [Description("Node index to include; omit for all nodes.")] int? nodeIndex = null,
        [Description("First operation index. Omit to show the operations around the completion boundary; use 0 for the beginning.")] int? offset = null,
        CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_dump_breadcrumbs", () =>
        {
            DumpHandle h = session.Get<DumpHandle>(handle);
            List<BreadcrumbNodeData>? nodes = PixApiExtensionsPostmortemDump.TryGetBreadcrumbNodes(h.Document, out Exception ex);
            if (nodes is null)
            {
                return new { nodes = Array.Empty<object>(), unavailable = ex is null ? null : PixErrors.Describe(ex) };
            }
            if (nodeIndex.HasValue && (nodeIndex < 0 || nodeIndex >= nodes.Count))
                throw new McpException($"nodeIndex {nodeIndex} is out of range; there are {nodes.Count} breadcrumb node(s).");
            return new
            {
                nodes = nodes.Select((n, i) => (Node: n, Index: i)).Where(n => !nodeIndex.HasValue || n.Index == nodeIndex.Value).Select(entry =>
                {
                    BreadcrumbNodeData n = entry.Node;
                    int count = n.Ops?.Length ?? 0;
                    var window = BreadcrumbWindow(count, n.CompletedCount, maxOps, offset);
                    return new
                    {
                        nodeIndex = entry.Index,
                        commandList = n.CommandListName,
                        commandQueue = n.CommandQueueName,
                        completedCount = n.CompletedCount,
                        opCount = count,
                        opsOffset = window.Offset,
                        returnedOps = window.Count,
                        nextOpsOffset = window.NextOffset,
                        opsTruncated = window.Count < count,
                        ops = n.Ops?.Skip(window.Offset).Take(window.Count).Select((op, i) => new { index = window.Offset + i, op, completed = window.Offset + i < n.CompletedCount }).ToArray(),
                        contexts = n.Contexts?.Select(c => new { breadcrumbIndex = c.BreadcrumbIndex, context = c.ContextString }).ToArray(),
                    };
                }).ToArray(),
            };
        }, cancellationToken);

    internal static (int Offset, int Count, int? NextOffset) BreadcrumbWindow(int total, long completed, int maxOps, int? offset)
    {
        int limit = Math.Clamp(maxOps, 1, 5000);
        int boundary = (int)Math.Clamp(completed, 0, total);
        int start = offset.HasValue ? Math.Max(0, offset.Value) : Math.Clamp(boundary - limit / 2, 0, Math.Max(0, total - limit));
        int take = Math.Min(limit, Math.Max(0, total - start));
        long next = (long)start + take;
        return (start, take, next < total ? (int)next : null);
    }

    [McpServerTool(Name = "pix_dump_resources", ReadOnly = true), Description("Resources known to the dump, paged: name, GPU virtual address, size, dimensions, attributes, and optionally their lifetime events (create/destroy/map...). To correlate a fault address use pix_dump_page_faults.")]
    public static Task<string> Resources(
        PixSession session,
        [Description("Dump handle")] string handle,
        [Description("First item (default 0).")] int offset = 0,
        [Description("Maximum items (default 25, max 1000).")] int limit = Paging.DefaultLimit,
        [Description("Only resources whose name contains this text (case-insensitive).")] string? nameContains = null,
        [Description("Include per-resource lifetime events (default false).")] bool includeEvents = false,
        CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_dump_resources", () =>
        {
            DumpHandle h = session.Get<DumpHandle>(handle);
            (int o, int l) = Paging.Normalize(offset, limit);
            IPixCollection? resources = PixApiExtensionsPostmortemDump.TryGetResources(h.Document, out Exception ex);
            if (resources is null)
            {
                return Paging.Unavailable(o, l, "resources", ex, "The dump lists no resources.");
            }
            IEnumerable<(IPixPostmortemD3D12Resource Resource, string Name)> matching = Interop.Items<IPixPostmortemD3D12Resource>(resources)
                .Select(r => (Resource: r, Name: Interop.W(r.GetName())))
                .Where(r => Tools.Contains(r.Name, nameContains));
            return Paging.Collect(matching, o, l, item =>
            {
                IPixPostmortemD3D12Resource r = item.Resource;
                D3D12_RESOURCE_DESC2? desc = PixApiExtensionsPostmortemDump.TryGetDesc(r);
                return new
                {
                    name = string.IsNullOrEmpty(item.Name) ? null : item.Name,
                    apiObjectId = Interop.Hex(r.GetApiObjectId()),
                    gpuVirtualAddress = Interop.Hex(r.GetGpuVirtualAddress()),
                    sizeBytes = r.GetSizeBytes(),
                    type = r.GetType(),
                    desc = desc.HasValue ? ResourceTools.ResourceDesc(desc.Value) : null,
                    attributes = Tools.Try(() => PixApiExtensionsPostmortemDump.GetAttributes(r).Select(a => new { name = Interop.W(a.Name), description = Interop.WOrNull(a.Description), value = Interop.Value(a.Value) }).ToArray(), "attributes"),
                    events = !includeEvents ? null : Tools.Try(() =>
                    {
                        IPixCollection? ev = PixApiExtensionsPostmortemDump.TryGetEvents(r, out _);
                        return ev is null ? null : Interop.Items<IPixPostmortemResourceEvent>(ev).Select(e => new { type = e.GetType(), timestampNs = e.GetTimestampInNs() }).ToArray();
                    }, "events"),
                };
            });
        }, cancellationToken);

    [McpServerTool(Name = "pix_dump_gpu_state", ReadOnly = true), Description("GPU state tables captured at dump time (engine/queue registers, hardware status), as named tables with columns and (nested) rows.")]
    public static Task<string> GpuState(
        PixSession session,
        [Description("Dump handle")] string handle,
        [Description("Table index to expand; omit to list tables with row counts only.")] int? tableIndex = null,
        [Description("Maximum rows to return inline including nested rows (default 500, max 20000); larger tables are preserved in a complete retrievable result snapshot.")] int maxRows = 500,
        CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_dump_gpu_state", () =>
        {
            DumpHandle h = session.Get<DumpHandle>(handle);
            IPixCollection? tables = PixApiExtensionsPostmortemDump.TryGetGpuStateTables(h.Document, out Exception ex);
            if (tables is null)
            {
                return new { tables = Array.Empty<object>(), unavailable = ex is null ? null : PixErrors.Describe(ex) };
            }
            var list = Interop.Items<IPixGpuStateTable>(tables).ToList();
            if (!tableIndex.HasValue)
            {
                return new
                {
                    tables = list.Select((t, i) => new
                    {
                        tableIndex = i,
                        id = t.GetId(),
                        name = Interop.W(t.GetName()),
                        description = Interop.WOrNull(t.GetDescription()),
                        columns = Columns(t),
                        rootRowCount = PixApiExtensionsPostmortemDump.TryGetRows(t, out _)?.GetCount(),
                    }).ToArray(),
                };
            }
            if (tableIndex.Value < 0 || tableIndex.Value >= list.Count)
            {
                throw new McpException($"tableIndex {tableIndex} is out of range; there are {list.Count} table(s).");
            }
            IPixGpuStateTable table = list[tableIndex.Value];
            string[] columns = Columns(table);
            int rowCount = 0;
            var rows = new List<object>();
            IPixCollection? rootRows = PixApiExtensionsPostmortemDump.TryGetRows(table, out _);
            if (rootRows is not null)
            {
                foreach (IPixGpuStateTableRow row in Interop.Items<IPixGpuStateTableRow>(rootRows))
                {
                    rows.Add(RowDto(row, columns, ref rowCount, cancellationToken));
                }
            }
            return GpuStateResult(session.Results, h.Id,
                new { tableIndex, name = Interop.W(table.GetName()), description = Interop.WOrNull(table.GetDescription()), columns, rowCount, rows }, rowCount, maxRows);
        }, cancellationToken);

    private static string[] Columns(IPixGpuStateTable table)
    {
        uint count = table.GetColumnCount();
        var columns = new string[count];
        for (uint i = 0; i < count; i++)
        {
            try { columns[i] = PixApiExtensionsPostmortemDump.GetColumnName(table, i); }
            catch { columns[i] = $"column{i}"; }
        }
        return columns;
    }

    internal static object GpuStateResult(ResultStore results, string handle, object fullTable, int rowCount, int maxRows)
    {
        if (rowCount <= Math.Clamp(maxRows, 1, 20000)) return fullTable;
        string resultRef = results.Store(fullTable, owner: handle);
        return new DeferredResultDto(true, resultRef, Encoding.UTF8.GetByteCount(Json.Serialize(fullTable)), [ResultStore.ReadCall(resultRef)]);
    }

    private static object RowDto(IPixGpuStateTableRow row, string[] columns, ref int rowCount, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        rowCount++;
        var values = new Dictionary<string, object?>();
        try
        {
            List<PIX_VALUE> list = PixApiExtensionsPostmortemDump.GetValues(row);
            for (int i = 0; i < list.Count; i++)
            {
                values[i < columns.Length ? columns[i] : $"column{i}"] = Interop.Value(list[i]);
            }
        }
        catch { }
        object? children = null;
        try
        {
            IPixCollection? c = PixApiExtensionsPostmortemDump.TryGetChildRows(row, out _);
            if (c is not null && c.GetCount() > 0)
            {
                var kids = new List<object>();
                foreach (IPixGpuStateTableRow child in Interop.Items<IPixGpuStateTableRow>(c))
                {
                    kids.Add(RowDto(child, columns, ref rowCount, cancellationToken));
                }
                children = kids;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return new { id = row.GetId(), name = Interop.W(row.GetName()), description = Interop.WOrNull(row.GetDescription()), values, children };
    }

    [McpServerTool(Name = "pix_dump_blobs", ReadOnly = false), Description("Application-provided blobs embedded in the dump (metadata id and size). With blobIndex, returns bytes written to outPath (whole blob) or a retrievable base64 byte window. Native reads start at the beginning of the blob, so later byte windows require reading their preceding bytes as well.")]
    public static Task<string> Blobs(
        PixSession session,
        [Description("Dump handle")] string handle,
        [Description("Blob index to read; omit to list only.")] int? blobIndex = null,
        [Description("Maximum bytes to return inline as base64 (default 65536, max 1 MB); larger blobs should go to outPath.")] int maxBytes = 65536,
        [Description("File to write the selected blob to (whole blob, no size cap); nothing is returned inline then.")] string? outPath = null,
        [Description("First byte to return inline. Does not affect whole-blob outPath export.")] int offset = 0,
        CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_dump_blobs", () =>
        {
            DumpHandle h = session.Get<DumpHandle>(handle);
            if (offset < 0) throw new PixToolException("invalid_arguments", "offset must be nonnegative.");
            IPixCollection? blobs = PixApiExtensionsPostmortemDump.GetApplicationBlobs(h.Document);
            if (blobs is null)
            {
                return new { blobs = Array.Empty<object>() };
            }
            var list = Interop.Items<IPixApplicationBlob>(blobs).ToList();
            object? data = null;
            if (blobIndex.HasValue)
            {
                if (blobIndex.Value < 0 || blobIndex.Value >= list.Count)
                {
                    throw new McpException($"blobIndex {blobIndex} is out of range; there are {list.Count} blob(s).");
                }
                IPixApplicationBlob blob = list[blobIndex.Value];
                ulong size = blob.GetSizeBytes();
                if (!string.IsNullOrWhiteSpace(outPath))
                {
                    string target = Path.GetFullPath(outPath);
                    File.WriteAllBytes(target, PixApiExtensions.GetData(blob, size));
                    data = new { blobIndex, sizeBytes = size, path = target };
                }
                else
                {
                    var window = ReadBlobWindow(size, offset, maxBytes, prefix => PixApiExtensions.GetData(blob, prefix));
                    data = new { blobIndex, sizeBytes = size, offset, returnedBytes = window.Bytes.Length, truncated = window.NextOffset.HasValue,
                        base64 = Convert.ToBase64String(window.Bytes),
                        nextCalls = window.NextOffset.HasValue ? new[] { new ToolCallDto("pix_dump_blobs", new { handle, blobIndex, maxBytes, offset = window.NextOffset.Value }) } : null };
                }
            }
            return new
            {
                blobs = list.Select((b, i) => new { blobIndex = i, metadata = Interop.Hex(b.GetMetadata()), sizeBytes = b.GetSizeBytes() }).ToArray(),
                data,
            };
        }, cancellationToken);

    internal static (byte[] Bytes, int? NextOffset) ReadBlobWindow(ulong size, int offset, int maxBytes, Func<ulong, byte[]> readPrefix)
    {
        if (offset < 0) throw new PixToolException("invalid_arguments", "offset must be nonnegative.");
        int cap = Math.Clamp(maxBytes, 1, 1024 * 1024);
        int take = (int)Math.Min(size > (ulong)offset ? size - (ulong)offset : 0, (ulong)cap);
        long end = (long)offset + take;
        if (end > int.MaxValue) throw new PixToolException("blob_window_too_large", "This window exceeds managed array indexing. Export the complete blob with outPath.");
        byte[] bytes = take == 0 ? [] : readPrefix((ulong)end).AsSpan(offset, take).ToArray();
        return (bytes, (ulong)end < size ? (int)end : null);
    }

    [McpServerTool(Name = "pix_dump_journal", ReadOnly = true), Description("D3D runtime journal entries recorded before the device removal (error codes, thread ids, messages), paged with offset/limit.")]
    public static Task<string> Journal(
        PixSession session,
        [Description("Dump handle")] string handle,
        [Description("First entry (default 0).")] int offset = 0,
        [Description("Maximum entries (default 200, max 1000).")] int limit = 200,
        CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_dump_journal", () =>
        {
            DumpHandle h = session.Get<DumpHandle>(handle);
            (int o, int l) = Paging.Normalize(offset, limit);
            IPixCollection? entries = PixApiExtensionsPostmortemDump.TryGetD3DJournalEntries(h.Document, out Exception ex);
            if (entries is null)
            {
                return Paging.Unavailable(o, l, "journal", ex, "The dump contains no D3D journal.");
            }
            return Paging.Collect(Interop.Items<IPixD3DJournalEntry>(entries), o, l,
                e => new { code = Interop.Hex(e.GetCode()), threadId = e.GetThreadID(), tickCount = e.GetTickCount(), message = Interop.WOrNull(e.GetErrorMessage()) });
        }, cancellationToken);

    [McpServerTool(Name = "pix_dump_shader_waves", ReadOnly = true), Description("Shader debugging data captured at the hang, paged: in-flight shader waves with stage, status, coordinates, instruction pointer, exceptions hit, offending HLSL locations (first 32), and optionally lanes (first 256).")]
    public static Task<string> ShaderWaves(
        PixSession session,
        [Description("Dump handle")] string handle,
        [Description("First wave (default 0).")] int offset = 0,
        [Description("Maximum waves (default 50, max 1000).")] int limit = 50,
        [Description("Include per-lane status and shader parameters (default false; first 256 lanes).")] bool includeLanes = false,
        [Description("Include offending code locations (default true).")] bool includeOffendingLocations = true,
        CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_dump_shader_waves", () =>
        {
            DumpHandle h = session.Get<DumpHandle>(handle);
            (int o, int l) = Paging.Normalize(offset, limit);
            IPixShaderDebuggingData? data = PixApiExtensionsPostmortemDump.TryGetShaderData(h.Document, out Exception ex);
            if (data is null)
            {
                return Paging.Unavailable(o, l, "shaderDebuggingData", ex, "No shader debugging data in this dump.");
            }
            IPixCollection? waves = PixApiExtensionsShaderDebugging.TryGetWaves(data, out Exception wex);
            if (waves is null)
            {
                return Paging.Unavailable(o, l, "waves", wex, "The shader debugging data lists no waves.");
            }
            return Paging.Collect(Interop.Items<IPixShaderWave>(waves).Select((wave, index) => (wave, index)), o, l,
                entry => WaveDto(entry.wave, h.Id, entry.index, includeLanes, includeOffendingLocations));
        }, cancellationToken);

    internal static object WaveDto(IPixShaderWave wave, string handle, int waveIndex, bool includeLanes, bool includeOffending,
        int lanesOffset = 0, int maxLanes = 256, int locationsOffset = 0, int maxOffending = 32)
    {
        object? lanes = null;
        object? laneCount = null;
        bool lanesTruncated = false;
        try
        {
            IPixCollection? c = PixApiExtensionsShaderDebugging.TryGetLanes(wave, out _);
            if (c is not null)
            {
                ulong count = c.GetCount();
                laneCount = count;
                if (includeLanes)
                {
                    lanesTruncated = count > (ulong)((long)lanesOffset + maxLanes);
                    lanes = Interop.Items<IPixShaderLane>(c).Skip(lanesOffset).Take(maxLanes).Select(lane => new
                    {
                        index = lane.GetIndex(),
                        status = lane.GetStatus(),
                        shaderParams = Reflect.ToObject(PixApiExtensionsShaderDebugging.TryGetShaderParams(lane, out _)),
                    }).ToArray();
                }
            }
        }
        catch (Exception ex) { laneCount = PixErrors.Unavailable("lanes", ex); }

        object? offending = null;
        bool offendingTruncated = false;
        if (includeOffending && wave is IPixPostmortemShaderWave pw)
        {
            offending = Tools.Try(() =>
            {
                IPixCollection? locs = PixApiExtensionsPostmortemDump.TryGetOffendingCodeLocations(pw, PIX_SHADER_CODE_TYPE.PIX_SHADER_CODE_TYPE_HLSL, out _);
                if (locs is null) return null;
                offendingTruncated = locs.GetCount() > (ulong)((long)locationsOffset + maxOffending);
                return Interop.Items<IPixOffendingShaderCodeLocation>(locs).Skip(locationsOffset).Take(maxOffending).Select(loc => new
                {
                    codeType = loc.GetCodeType(),
                    line = loc.GetLineNumber(),
                    statement = Interop.WOrNull(loc.GetStatement()),
                    reason = loc.GetReason(),
                    explanation = Interop.WOrNull(loc.GetExplanation()),
                }).ToArray();
            }, "offendingLocations");
        }

        object? ip = Tools.Try(() =>
        {
            IPixShaderCodeLocation? loc = PixApiExtensionsShaderDebugging.TryGetInstructionPointerCodeLocation(wave, PIX_SHADER_CODE_TYPE.PIX_SHADER_CODE_TYPE_HLSL, out _);
            return loc is null ? null : new { line = loc.GetLineNumber(), statement = Interop.WOrNull(loc.GetStatement()) };
        }, "instructionPointerLocation");

        return new
        {
            waveIndex,
            id = wave.GetId(),
            stage = wave.GetStage(),
            status = wave.GetStatus(),
            coordinates = Interop.WOrNull(wave.GetCoordinates()),
            instructionPointer = Interop.Hex(wave.GetInstructionPointer()),
            instructionPointerLocation = ip,
            exceptionsHit = wave.GetExceptionsHit(),
            laneCount,
            lanes,
            lanesOffset,
            lanesTruncated = lanesTruncated ? true : (bool?)null,
            offendingLocations = offending,
            locationsOffset,
            offendingLocationsTruncated = offendingTruncated ? true : (bool?)null,
            nextCalls = new[]
            {
                lanesTruncated ? new ToolCallDto("pix_dump_shader_wave_data", new { handle, waveIndex, lanesOffset = lanesOffset + maxLanes, lanesLimit = maxLanes, includeOffendingLocations = false }) : null,
                offendingTruncated ? new ToolCallDto("pix_dump_shader_wave_data", new { handle, waveIndex, locationsOffset = locationsOffset + maxOffending, locationsLimit = maxOffending, includeLanes = false }) : null,
            }.Where(call => call is not null).ToArray(),
        };
    }
}
