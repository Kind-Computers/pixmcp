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
public static class DumpTools
{
    [McpServerTool(Name = "pix_dump_open"), Description("Opens a DirectX dump file (.dxdmp_preview, written when a GPU hang/TDR occurs) and returns a handle plus metadata: device error, error bucket, PIX's brief and detailed summaries, application, adapter, OS, CPU and memory info, and the queue list.")]
    public static Task<string> Open(PixSession session, [Description("Path to the .dxdmp_preview / .dxdmp file.")] string path)
        => Tools.Run(session, "pix_dump_open", () =>
        {
            string full = Tools.RequireFile(path, "Dump file");
            IPixPostmortemDocument document = PixApiExtensionsPostmortemDump.OpenPostmortemDumpDocument(session.Factory, full, null!, null!, null!);
            DumpHandle handle = session.Register(new DumpHandle(full, document));
            handle.Metadata = Metadata(handle);
            return new { handle = handle.Id, path = full, metadata = handle.Metadata, queues = QueueList(handle) };
        });

    [McpServerTool(Name = "pix_dump_info", ReadOnly = true), Description("Metadata and queue summary for an open dump file.")]
    public static Task<string> Info(PixSession session, [Description("Dump handle")] string handle)
        => Tools.Run(session, "pix_dump_info", () =>
        {
            DumpHandle h = session.Get<DumpHandle>(handle);
            return new { handle = h.Id, path = h.Path, metadata = h.Metadata ??= Metadata(h), queues = QueueList(h) };
        });

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
    {
        if (h.Queues is null)
        {
            IPixCollection? queues = PixApiExtensionsPostmortemDump.TryGetQueues(h.Document, out _);
            h.Queues = queues is null ? new() : Interop.Items<IPixPostmortemQueueInfo>(queues).ToList();
        }
        return h.Queues;
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

    [McpServerTool(Name = "pix_dump_queues", ReadOnly = true), Description("Queues in the dump with their status at dump time, hardware status fields (severity/values) and page fault counts.")]
    public static Task<string> DumpQueues(PixSession session, [Description("Dump handle")] string handle)
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
                object? faults;
                try
                {
                    IPixCollection? pf = PixApiExtensionsPostmortemDump.GetPageFaults(q);
                    faults = pf is null ? 0 : (object)pf.GetCount();
                }
                catch { faults = null; }
                object? eventCount;
                try
                {
                    IPixCollection? ev = PixApiExtensionsPostmortemDump.TryGetEvents(q, out _);
                    eventCount = ev?.GetCount();
                }
                catch { eventCount = null; }
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
        });

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

    private static object EventDto(IPixPostmortemEvent evt, int depth, int maxDepth, bool includeCorrelations, ref int budget)
    {
        object? shaders = null;
        object? resources = null;
        if (includeCorrelations)
        {
            try
            {
                IPixCollection? s = PixApiExtensionsPostmortemDump.TryGetCorrelatedShaders(evt, out _);
                if (s is not null && s.GetCount() > 0)
                {
                    shaders = Interop.Items<IPixShader>(s).Select(sh => ShaderSummary(sh)).ToArray();
                }
            }
            catch { }
            try
            {
                IPixCollection? r = PixApiExtensionsPostmortemDump.TryGetCorrelatedResources(evt, out _);
                if (r is not null && r.GetCount() > 0)
                {
                    resources = Interop.Items<IPixPostmortemD3D12Resource>(r).Select(res => new { name = Interop.WOrNull(res.GetName()), gpuVirtualAddress = Interop.Hex(res.GetGpuVirtualAddress()), sizeBytes = res.GetSizeBytes() }).ToArray();
                }
            }
            catch { }
        }

        object? children = null;
        ulong childCount = 0;
        try
        {
            IPixCollection? c = PixApiExtensionsPostmortemDump.TryGetChildEvents(evt, out _);
            if (c is not null)
            {
                childCount = c.GetCount();
                if (depth < maxDepth && childCount > 0 && budget > 0)
                {
                    var list = new List<object>();
                    foreach (IPixPostmortemEvent child in Interop.Items<IPixPostmortemEvent>(c))
                    {
                        if (budget-- <= 0)
                        {
                            list.Add(new { truncated = true });
                            break;
                        }
                        list.Add(EventDto(child, depth + 1, maxDepth, includeCorrelations, ref budget));
                    }
                    children = list;
                }
            }
        }
        catch { }

        return new
        {
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

    [McpServerTool(Name = "pix_dump_events", ReadOnly = true), Description("Command queue event history from the dump (D3D API calls, PIX markers, custom markers, driver events) as a tree, with completion status (IN_PROGRESS / POSSIBLY_COMPLETED marks work that was running when the GPU hung) and correlated shaders/resources.")]
    public static Task<string> Events(
        PixSession session,
        [Description("Dump handle")] string handle,
        [Description("Queue index from pix_dump_queues.")] int queueIndex,
        [Description("First root event (default 0).")] int offset = 0,
        [Description("Maximum root events (default 50).")] int limit = 50,
        [Description("Maximum child depth to expand (default 3).")] int maxDepth = 3,
        [Description("Maximum total events to expand including children (default 500).")] int maxEvents = 500,
        [Description("Include correlated shaders/resources (default true).")] bool includeCorrelations = true,
        [Description("Only include root events with this status: IN_PROGRESS, POSSIBLY_COMPLETED, COMPLETED, NOT_STARTED.")] string? status = null)
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
                return new { total = 0, items = Array.Empty<object>(), unavailable = ex is null ? null : PixErrors.Describe(ex) };
            }
            PIX_EVENT_STATUS? wanted = string.IsNullOrEmpty(status) ? null : Tools.ParseEnum<PIX_EVENT_STATUS>(status);

            int budget = Math.Clamp(maxEvents, 1, 5000);
            var page = new List<object>();
            long total = 0;
            foreach (IPixPostmortemEvent evt in Interop.Items<IPixPostmortemEvent>(events))
            {
                if (wanted.HasValue && evt.GetStatus() != wanted.Value)
                {
                    continue;
                }
                if (total >= o && page.Count < l && budget > 0)
                {
                    budget--;
                    page.Add(EventDto(evt, 0, Math.Clamp(maxDepth, 0, 16), includeCorrelations, ref budget));
                }
                total++;
            }
            return Paging.Page(page, total, o, l);
        });

    [McpServerTool(Name = "pix_dump_page_faults", ReadOnly = true), Description("GPU page faults recorded in the dump: faulting virtual address, type, access, timestamp, queue, and the resource allocation/free events around that address. Also includes DRED page fault data when present.")]
    public static Task<string> PageFaults(PixSession session, [Description("Dump handle")] string handle)
        => Tools.Run(session, "pix_dump_page_faults", () =>
        {
            DumpHandle h = session.Get<DumpHandle>(handle);
            var faults = new List<object>();
            IPixCollection? pf = PixApiExtensionsPostmortemDump.TryGetPageFaults(h.Document, out _);
            if (pf is not null)
            {
                foreach (IPixPostmortemPageFault fault in Interop.Items<IPixPostmortemPageFault>(pf))
                {
                    object? queue = null;
                    try
                    {
                        IPixPostmortemQueueInfo? q = PixApiExtensionsPostmortemDump.TryGetQueue(fault, out _);
                        queue = q is null ? null : new { id = q.GetId(), name = Interop.W(q.GetName()) };
                    }
                    catch { }
                    var resourceEvents = new List<object>();
                    try
                    {
                        IPixCollection? re = PixApiExtensionsPostmortemDump.TryGetResourceEvents(fault, out _);
                        if (re is not null)
                        {
                            foreach (IPixPostmortemResourceEvent e in Interop.Items<IPixPostmortemResourceEvent>(re))
                            {
                                object? res = null;
                                try
                                {
                                    IPixPostmortemD3D12Resource? r = PixApiExtensionsPostmortemDump.TryGetResource(e, out _);
                                    res = r is null ? null : new { name = Interop.WOrNull(r.GetName()), gpuVirtualAddress = Interop.Hex(r.GetGpuVirtualAddress()), sizeBytes = r.GetSizeBytes() };
                                }
                                catch { }
                                resourceEvents.Add(new { type = e.GetType(), timestampNs = e.GetTimestampInNs(), resource = res });
                            }
                        }
                    }
                    catch { }
                    faults.Add(new
                    {
                        id = fault.GetId(),
                        gpuVirtualAddress = Interop.Hex(fault.GetGpuVirtualAddress()),
                        type = fault.GetType(),
                        accessType = fault.GetAccessType(),
                        timestampNs = fault.GetTimestampInNs(),
                        queue,
                        resourceEvents,
                    });
                }
            }

            object? dred = null;
            try
            {
                DredPageFaultData? data = PixApiExtensionsPostmortemDump.TryGetDredPageFault(h.Document, out _);
                if (data.HasValue)
                {
                    dred = new
                    {
                        pageFaultVA = Interop.Hex(data.Value.PageFaultVA),
                        existingAllocations = data.Value.ExistingAllocations?.Select(a => new { name = a.ObjectName, type = a.AllocationType }).ToArray(),
                        freedAllocations = data.Value.FreedAllocations?.Select(a => new { name = a.ObjectName, type = a.AllocationType }).ToArray(),
                    };
                }
            }
            catch { }

            return new { pageFaults = faults, dred };
        });

    [McpServerTool(Name = "pix_dump_breadcrumbs", ReadOnly = true), Description("DRED auto-breadcrumb nodes from the dump: per command list, the recorded operations and how many completed, plus context strings. Shows where each command list was when the GPU hung.")]
    public static Task<string> Breadcrumbs(PixSession session,
        [Description("Dump handle")] string handle,
        [Description("Maximum ops to list per node (default 200, max 5000).")] int maxOps = 200,
        [Description("Node index to include; omit for all nodes.")] int? nodeIndex = null,
        [Description("First operation index. Omit to show the operations around the completion boundary; use 0 for the beginning.")] int? offset = null)
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
        });

    internal static (int Offset, int Count, int? NextOffset) BreadcrumbWindow(int total, long completed, int maxOps, int? offset)
    {
        int limit = Math.Clamp(maxOps, 1, 5000);
        int boundary = (int)Math.Clamp(completed, 0, total);
        int start = offset.HasValue ? Math.Max(0, offset.Value) : Math.Clamp(boundary - limit / 2, 0, Math.Max(0, total - limit));
        int take = Math.Min(limit, Math.Max(0, total - start));
        long next = (long)start + take;
        return (start, take, next < total ? (int)next : null);
    }

    [McpServerTool(Name = "pix_dump_resources", ReadOnly = true), Description("Resources known to the dump: name, GPU virtual address, size, dimensions, attributes, and their lifetime events (create/destroy/map...).")]
    public static Task<string> Resources(
        PixSession session,
        [Description("Dump handle")] string handle,
        [Description("First item (default 0).")] int offset = 0,
        [Description("Maximum items (default 100).")] int limit = Paging.DefaultLimit,
        [Description("Only resources whose name contains this text.")] string? nameContains = null,
        [Description("Include per-resource lifetime events (default false).")] bool includeEvents = false)
        => Tools.Run(session, "pix_dump_resources", () =>
        {
            DumpHandle h = session.Get<DumpHandle>(handle);
            (int o, int l) = Paging.Normalize(offset, limit);
            IPixCollection? resources = PixApiExtensionsPostmortemDump.TryGetResources(h.Document, out Exception ex);
            if (resources is null)
            {
                return new { total = 0, items = Array.Empty<object>(), unavailable = ex is null ? null : PixErrors.Describe(ex) };
            }
            var page = new List<object>();
            long total = 0;
            foreach (IPixPostmortemD3D12Resource r in Interop.Items<IPixPostmortemD3D12Resource>(resources))
            {
                string name = Interop.W(r.GetName());
                if (!Tools.Contains(name, nameContains))
                {
                    continue;
                }
                if (total >= o && page.Count < l)
                {
                    D3D12_RESOURCE_DESC2? desc = PixApiExtensionsPostmortemDump.TryGetDesc(r);
                    object? attributes = null;
                    try { attributes = PixApiExtensionsPostmortemDump.GetAttributes(r).Select(a => new { name = Interop.W(a.Name), description = Interop.WOrNull(a.Description), value = Interop.Value(a.Value) }).ToArray(); } catch { }
                    object? events = null;
                    if (includeEvents)
                    {
                        try
                        {
                            IPixCollection? ev = PixApiExtensionsPostmortemDump.TryGetEvents(r, out _);
                            events = ev is null ? null : Interop.Items<IPixPostmortemResourceEvent>(ev).Select(e => new { type = e.GetType(), timestampNs = e.GetTimestampInNs() }).ToArray();
                        }
                        catch { }
                    }
                    page.Add(new
                    {
                        name = string.IsNullOrEmpty(name) ? null : name,
                        apiObjectId = Interop.Hex(r.GetApiObjectId()),
                        gpuVirtualAddress = Interop.Hex(r.GetGpuVirtualAddress()),
                        sizeBytes = r.GetSizeBytes(),
                        type = r.GetType(),
                        desc = desc.HasValue ? ResourceTools.ResourceDesc(desc.Value) : null,
                        attributes,
                        events,
                    });
                }
                total++;
            }
            return Paging.Page(page, total, o, l);
        });

    [McpServerTool(Name = "pix_dump_gpu_state", ReadOnly = true), Description("GPU state tables captured at dump time (engine/queue registers, hardware status), as named tables with columns and (nested) rows.")]
    public static Task<string> GpuState(
        PixSession session,
        [Description("Dump handle")] string handle,
        [Description("Table index to expand; omit to list tables with row counts only.")] int? tableIndex = null,
        [Description("Maximum rows to return including nested rows (default 500).")] int maxRows = 500)
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
            int budget = Math.Clamp(maxRows, 1, 20000);
            var rows = new List<object>();
            IPixCollection? rootRows = PixApiExtensionsPostmortemDump.TryGetRows(table, out _);
            if (rootRows is not null)
            {
                foreach (IPixGpuStateTableRow row in Interop.Items<IPixGpuStateTableRow>(rootRows))
                {
                    if (budget-- <= 0)
                    {
                        rows.Add(new { truncated = true });
                        break;
                    }
                    rows.Add(RowDto(row, columns, ref budget));
                }
            }
            return new { tableIndex, name = Interop.W(table.GetName()), description = Interop.WOrNull(table.GetDescription()), columns, rows };
        });

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

    private static object RowDto(IPixGpuStateTableRow row, string[] columns, ref int budget)
    {
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
                    if (budget-- <= 0)
                    {
                        kids.Add(new { truncated = true });
                        break;
                    }
                    kids.Add(RowDto(child, columns, ref budget));
                }
                children = kids;
            }
        }
        catch { }
        return new { id = row.GetId(), name = Interop.W(row.GetName()), description = Interop.WOrNull(row.GetDescription()), values, children };
    }

    [McpServerTool(Name = "pix_dump_blobs", ReadOnly = true), Description("Application-provided blobs embedded in the dump (metadata id and size); optionally returns the bytes of one blob as base64.")]
    public static Task<string> Blobs(
        PixSession session,
        [Description("Dump handle")] string handle,
        [Description("Blob index to read; omit to list only.")] int? blobIndex = null,
        [Description("Maximum bytes to return (default 65536).")] int maxBytes = 65536)
        => Tools.Run(session, "pix_dump_blobs", () =>
        {
            DumpHandle h = session.Get<DumpHandle>(handle);
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
                ulong take = Math.Min(size, (ulong)Math.Clamp(maxBytes, 1, 16 * 1024 * 1024));
                byte[] bytes = PixApiExtensions.GetData(blob, take);
                data = new { blobIndex, sizeBytes = size, returnedBytes = bytes.Length, base64 = Convert.ToBase64String(bytes) };
            }
            return new
            {
                blobs = list.Select((b, i) => new { blobIndex = i, metadata = Interop.Hex(b.GetMetadata()), sizeBytes = b.GetSizeBytes() }).ToArray(),
                data,
            };
        });

    [McpServerTool(Name = "pix_dump_journal", ReadOnly = true), Description("D3D runtime journal entries recorded before the device removal (error codes, thread ids, messages).")]
    public static Task<string> Journal(PixSession session, [Description("Dump handle")] string handle, [Description("Maximum entries (default 200).")] int limit = 200)
        => Tools.Run(session, "pix_dump_journal", () =>
        {
            DumpHandle h = session.Get<DumpHandle>(handle);
            IPixCollection? entries = PixApiExtensionsPostmortemDump.TryGetD3DJournalEntries(h.Document, out Exception ex);
            if (entries is null)
            {
                return new { entries = Array.Empty<object>(), unavailable = ex is null ? null : PixErrors.Describe(ex) };
            }
            return new
            {
                total = entries.GetCount(),
                entries = Interop.Items<IPixD3DJournalEntry>(entries).Take(Math.Clamp(limit, 1, 5000))
                    .Select(e => new { code = Interop.Hex(e.GetCode()), threadId = e.GetThreadID(), tickCount = e.GetTickCount(), message = Interop.WOrNull(e.GetErrorMessage()) }).ToArray(),
            };
        });

    [McpServerTool(Name = "pix_dump_shader_waves", ReadOnly = true), Description("Shader debugging data captured at the hang: in-flight shader waves with stage, status, coordinates, instruction pointer, exceptions hit, offending source locations, and optionally lanes.")]
    public static Task<string> ShaderWaves(
        PixSession session,
        [Description("Dump handle")] string handle,
        [Description("First wave (default 0).")] int offset = 0,
        [Description("Maximum waves (default 50).")] int limit = 50,
        [Description("Include per-lane status and shader parameters (default false).")] bool includeLanes = false,
        [Description("Include offending code locations (default true).")] bool includeOffendingLocations = true)
        => Tools.Run(session, "pix_dump_shader_waves", () =>
        {
            DumpHandle h = session.Get<DumpHandle>(handle);
            IPixShaderDebuggingData? data = PixApiExtensionsPostmortemDump.TryGetShaderData(h.Document, out Exception ex);
            if (data is null)
            {
                return new { total = 0, items = Array.Empty<object>(), unavailable = ex is null ? "No shader debugging data in this dump." : PixErrors.Describe(ex) };
            }
            IPixCollection? waves = PixApiExtensionsShaderDebugging.TryGetWaves(data, out Exception wex);
            if (waves is null)
            {
                return new { total = 0, items = Array.Empty<object>(), unavailable = wex is null ? null : PixErrors.Describe(wex) };
            }
            (int o, int l) = Paging.Normalize(offset, limit);
            var page = new List<object>();
            long total = 0;
            foreach (IPixShaderWave wave in Interop.Items<IPixShaderWave>(waves))
            {
                if (total >= o && page.Count < l)
                {
                    page.Add(WaveDto(wave, includeLanes, includeOffendingLocations));
                }
                total++;
            }
            return Paging.Page(page, total, o, l);
        });

    private static object WaveDto(IPixShaderWave wave, bool includeLanes, bool includeOffending)
    {
        object? lanes = null;
        ulong? laneCount = null;
        try
        {
            IPixCollection? c = PixApiExtensionsShaderDebugging.TryGetLanes(wave, out _);
            if (c is not null)
            {
                laneCount = c.GetCount();
                if (includeLanes)
                {
                    lanes = Interop.Items<IPixShaderLane>(c).Take(256).Select(lane => new
                    {
                        index = lane.GetIndex(),
                        status = lane.GetStatus(),
                        shaderParams = Reflect.ToObject(PixApiExtensionsShaderDebugging.TryGetShaderParams(lane, out _)),
                    }).ToArray();
                }
            }
        }
        catch { }

        object? offending = null;
        if (includeOffending && wave is IPixPostmortemShaderWave pw)
        {
            try
            {
                IPixCollection? locs = PixApiExtensionsPostmortemDump.TryGetOffendingCodeLocations(pw, PIX_SHADER_CODE_TYPE.PIX_SHADER_CODE_TYPE_HLSL, out _);
                if (locs is not null)
                {
                    offending = Interop.Items<IPixOffendingShaderCodeLocation>(locs).Take(32).Select(loc => new
                    {
                        codeType = loc.GetCodeType(),
                        line = loc.GetLineNumber(),
                        statement = Interop.WOrNull(loc.GetStatement()),
                        reason = loc.GetReason(),
                        explanation = Interop.WOrNull(loc.GetExplanation()),
                    }).ToArray();
                }
            }
            catch { }
        }

        object? ip = null;
        try
        {
            IPixShaderCodeLocation? loc = PixApiExtensionsShaderDebugging.TryGetInstructionPointerCodeLocation(wave, PIX_SHADER_CODE_TYPE.PIX_SHADER_CODE_TYPE_HLSL, out _);
            ip = loc is null ? null : new { line = loc.GetLineNumber(), statement = Interop.WOrNull(loc.GetStatement()) };
        }
        catch { }

        return new
        {
            id = wave.GetId(),
            stage = wave.GetStage(),
            status = wave.GetStatus(),
            coordinates = Interop.WOrNull(wave.GetCoordinates()),
            instructionPointer = Interop.Hex(wave.GetInstructionPointer()),
            instructionPointerLocation = ip,
            exceptionsHit = wave.GetExceptionsHit(),
            laneCount,
            lanes,
            offendingLocations = offending,
        };
    }
}
