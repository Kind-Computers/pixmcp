using System.ComponentModel;
using System.Text.Json;
using Microsoft.PIX;
using Microsoft.PIX.Extension;
using Microsoft.PIX.Extension.GpuCapture;
using Microsoft.PIX.Extension.GpuCapture.Resources;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using PixMcp.Pix.Handles;

namespace PixMcp.Tools;

public static partial class ResourceTools
{
    public static readonly string[] ResourceSortKeys = ["index", "estimatedBytes", "name", "traffic"];
    public static readonly string[] ResourceUseSortKeys = ["event", "access", "viewType"];
    private static readonly string[] ResourceTypes = ["COMMITTED", "PLACED", "RESERVED"];
    private static readonly string[] ResourceDimensions = ["BUFFER", "TEXTURE1D", "TEXTURE2D", "TEXTURE3D"];

    [McpServerTool(Name = "pix_gpu_resources", Title = "List resources", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Replays the capture on the local GPU if analysis is not started. Only scope, markerPathPrefix, usedAs and sortBy=traffic need that, because they read the capture-wide resource-use index; every other call lists capture metadata immediately. Lists the D3D12 resources (buffers and textures) with dimensions, format, estimated size and heap kind, filterable and sortable, with totals by dimension, format and heap kind. Use pix_gpu_resource for one resource's details and pix_gpu_resource_timeline for its uses across the capture.")]
    public static Task<string> Resources(
        PixSession session,
        JobManager jobs,
        [Description("GPU capture handle")] string handle,
        [Description("First item (default 0).")] int offset = 0,
        [Description("Maximum items (default 25, max 1000).")] int limit = Paging.DefaultLimit,
        [Description("Only resources whose name contains this text (case-insensitive).")] string? nameContains = null,
        [Description("Filter by allocation type: COMMITTED, PLACED, RESERVED.")] string? type = null,
        [Description("Filter by dimension: BUFFER, TEXTURE1D, TEXTURE2D, TEXTURE3D.")] string? dimension = null,
        [Description(Shaping.FormatDescription)] string format = "objects",
        [Description(Shaping.BriefDescription)] bool brief = false,
        [Description(Shaping.TopNDescription)] int? topN = null,
        [Description(Shaping.MaxStringLengthDescription)] int? maxStringLength = null,
        [Description("Only the resource with exactly this debug name (case-sensitive).")] string? name = null,
        [Description("Only resources whose DXGI format contains this text, such as R8G8B8A8 or BC7 (case-insensitive).")] string? pixelFormat = null,
        [Description("Only resources whose flags contain this text, such as ALLOW_RENDER_TARGET (case-insensitive).")] string? flags = null,
        [Description("Only resources whose estimated size is at least this many bytes.")] long? minBytes = null,
        [Description("index (default), estimatedBytes, name or traffic (estimated bytes times the events that read or write the resource; needs the resource-use index).")] string sortBy = "index",
        [Description("Reverse the order (default: largest first for estimatedBytes and traffic, ascending for index and name).")] bool? descending = null,
        [Description(EventScope.Description + " Keeps resources used inside the selection and adds extra.scopeSummary (needs the resource-use index).")] EventRef? scope = null,
        [Description(EventScope.PrefixDescription + " Needs the resource-use index.")] string? markerPathPrefix = null,
        [Description("Keep resources used as any, read, write, readWrite, copySrc, copyDst, barrier, renderTarget, depthStencil or uav (needs the resource-use index).")] string? usedAs = null,
        [Description("Add extra.totals over the filtered resources (default true).")] bool includeTotals = true,
        [Description(Tools.ReadyWaitDescription)] double waitSeconds = Tools.DefaultReadyWaitSeconds,
        CancellationToken cancellationToken = default)
    {
        ShapingOptions shaping = Shaping.Options(format, brief, topN, maxStringLength, offset);
        ReferenceValidation.Page(offset, limit);
        string sort = RollupTools.Canonical(sortBy, ResourceSortKeys, "sortBy");
        string? used = usedAs is null ? null : RollupTools.Canonical(usedAs, ResourceAccess.UsedAs, "usedAs");
        string? wantedType = type is null ? null : RollupTools.Canonical(type, ResourceTypes, "type");
        string? wantedDimension = dimension is null ? null : RollupTools.Canonical(dimension, ResourceDimensions, "dimension");
        if (minBytes < 0) throw PixErrors.InvalidArguments("minBytes must be nonnegative.");
        bool desc = descending ?? sort is "estimatedBytes" or "traffic";
        string? formatFilter = pixelFormat is null ? null : FormatInfo.Normalize(pixelFormat.Trim());
        bool Matches(ResourceSummaryDto r) => Tools.Contains(r.Name ?? "", nameContains) && (name is null || r.Name == name)
            && (wantedType is null || string.Equals(r.Type, wantedType, StringComparison.OrdinalIgnoreCase))
            && (wantedDimension is null || string.Equals(r.Dimension, wantedDimension, StringComparison.OrdinalIgnoreCase))
            && (formatFilter is null || r.Format.Contains(formatFilter, StringComparison.OrdinalIgnoreCase))
            && (flags is null || r.Flags.Contains(flags.Trim(), StringComparison.OrdinalIgnoreCase))
            && (minBytes is null || r.EstimatedBytes >= (ulong)minBytes.Value);

        object Query(GpuCaptureHandle h, ScopeSelection? selection)
        {
            List<ResourceSummaryDto> rows = ResourceSummaries(h).Where(Matches).ToList();
            ResourceScopeSummaryDto? scopeSummary = null;
            object[]? coverage = null;
            if (selection is not null)
            {
                ResourceUseIndex index = h.ResourceUses.CaptureWideIndex ?? throw new InvalidOperationException("The resource-use index preparation did not publish its index.");
                ResourceUseDto[] inSelection = index.Rows.Where(r => selection.Contains(h, r.Binding.EventRef)).ToArray();
                Dictionary<string, ResourceUseDto[]> byResource = inSelection.GroupBy(r => r.ResourceRef.ApiObjectId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
                bool requireUse = !selection.IsUnrestricted || used is not null;
                rows = rows.Where(r => !requireUse || byResource.TryGetValue(r.ApiObjectId, out ResourceUseDto[]? uses) && (used is null || uses.Any(u => UsedAsMatches(u, used))))
                    .Select(r => r with { TrafficBytes = r.EstimatedBytes is ulong bytes ? bytes * (ulong)TouchEvents(byResource.GetValueOrDefault(r.ApiObjectId, [])) : null })
                    .ToList();
                if (!selection.IsUnrestricted) scopeSummary = ScopeSummary(h, selection, inSelection);
                coverage = index.Coverage.Take(5).ToArray();
            }
            rows = sort switch
            {
                "estimatedBytes" => (desc ? rows.OrderByDescending(r => r.EstimatedBytes ?? 0) : rows.OrderBy(r => r.EstimatedBytes ?? 0)).ThenBy(r => r.Index).ToList(),
                "traffic" => (desc ? rows.OrderByDescending(r => r.TrafficBytes ?? 0) : rows.OrderBy(r => r.TrafficBytes ?? 0)).ThenBy(r => r.Index).ToList(),
                "name" => (desc ? rows.OrderByDescending(r => r.Name ?? "", StringComparer.OrdinalIgnoreCase) : rows.OrderBy(r => r.Name ?? "", StringComparer.OrdinalIgnoreCase))
                    .ThenBy(r => r.Index).ToList(),
                _ => (desc ? rows.OrderByDescending(r => r.Index) : rows.OrderBy(r => r.Index)).ToList(),
            };
            (int o, int l) = Shaping.Window(shaping, offset, limit);
            ResourceSummaryDto[] page = rows.Skip(o).Take(l).ToArray();
            ToolCallDto Call(int at, int? strings) => new("pix_gpu_resources", new { handle, offset = at, limit = l, nameContains, type, dimension, format, brief,
                maxStringLength = strings, name, pixelFormat, flags, minBytes, sortBy = sort, descending = desc, scope, markerPathPrefix, usedAs = used, includeTotals });
            object extra = new { totals = includeTotals ? Totals(rows) : null, sizeNote = FormatInfo.Note, scope = selection?.DescribeOrNull(h), usedAs = used, scopeSummary,
                indexCoverage = coverage };
            return Shaping.Apply(page, rows.Count, o, l, shaping, RowShapes.Resources, handle, extra, next => Call(next, maxStringLength), () => Call(o, Shaping.FullStringLength));
        }

        if (scope is null && markerPathPrefix is null && used is null && sort != "traffic")
            return Tools.Run(session, "pix_gpu_resources", () => Query(session.Get<GpuCaptureHandle>(handle), null), cancellationToken);
        ScopeSelection selection = EventScope.Resolve(session, handle, scope?.QueueIndex, scope, markerPathPrefix);
        return Tools.RunWhenReady(session, jobs, "pix_gpu_resources", handle, GpuCaptureHandle.ResourceUseIndexPreparation(handle), h => Query(h, selection),
            waitSeconds, cancellationToken);
    }

    [McpServerTool(Name = "pix_gpu_resource_timeline", Title = "Resource timeline", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Replays the capture on the local GPU if analysis is not started. One resource's uses in capture order: every event that binds it as a view, copies it, passes it to ExecuteIndirect or transitions it with a barrier, with the access class (read, write, readWrite, copySrc, copyDst, barrier), barrier states, phases of consecutive same-access uses per queue, summary counts, an upper-bound traffic estimate and insights (read before write, a write then read with no barrier, written but never read, bound as render target and shader resource in one pass). Joins replay timing only when it is already collected.")]
    public static Task<string> ResourceTimelineTool(PixSession session, JobManager jobs,
        [Description("Resource reference { handle, apiObjectId } as returned by pix_gpu_resources or pix_gpu_event_resources.")] ResourceRef resourceRef,
        [Description(EventScope.Description)] EventRef? scope = null,
        [Description(EventScope.PrefixDescription)] string? markerPathPrefix = null,
        [Description("Only uses on this queue; omit for every queue.")] int? queueIndex = null,
        [Description("Only rows with this access: read, write, readWrite, copySrc, copyDst, barrier or unknown.")] string? access = null,
        [Description("Include barrier rows (default true).")] bool includeBarriers = true,
        [Description("Add each event's EOP duration when replay timing is already collected (default true; never collects timing).")] bool includeTiming = true,
        [Description("First row to return (default 0).")] int offset = 0,
        [Description("Maximum rows to return (default 50, max 1000).")] int limit = 50,
        [Description(Tools.ReadyWaitDescription)] double waitSeconds = Tools.DefaultReadyWaitSeconds,
        [Description(Tools.IncludeProvenanceDescription)] bool includeProvenance = false,
        CancellationToken cancellationToken = default)
    {
        ReferenceValidation.Resource(session, resourceRef);
        ReferenceValidation.Page(offset, limit);
        string? accessName = access is null ? null : RollupTools.Canonical(access, ResourceAccess.Classes, "access");
        if (queueIndex.HasValue) session.Get<GpuCaptureHandle>(resourceRef.Handle).Queue(queueIndex.Value);
        ScopeSelection selection = EventScope.Resolve(session, resourceRef.Handle, scope?.QueueIndex ?? queueIndex, scope, markerPathPrefix);
        string key = Interop.Hex(Tools.ParseId(resourceRef.ApiObjectId, "apiObjectId"));
        string handle = resourceRef.Handle;
        return Tools.RunWhenReady(session, jobs, "pix_gpu_resource_timeline", handle, GpuCaptureHandle.ResourceUseIndexPreparation(handle), h =>
        {
            FindResource(h, key);
            ResourceSummaryDto resource = ResourceSummaries(h).FirstOrDefault(r => string.Equals(r.ApiObjectId, key, StringComparison.OrdinalIgnoreCase))
                ?? throw PixErrors.InvalidReference($"Resource {key} has no readable description.", new ToolCallDto("pix_gpu_resources", new { handle }, CostHints.Cached));
            ResourceUseIndex index = h.ResourceUses.CaptureWideIndex ?? throw new InvalidOperationException("The resource-use index preparation did not publish its index.");
            var eventsByQueue = new Dictionary<int, EventRecord[]>();
            EventRecord[] Events(int q) => eventsByQueue.TryGetValue(q, out EventRecord[]? cached) ? cached : eventsByQueue[q] = h.AllEvents(q);

            var uses = new List<TimelineUse>();
            foreach (ResourceUseDto row in index.For(new ResourceRef(handle, key)).Items)
            {
                if (row.Binding.EventRef is not EventRef at || !selection.Contains(h, at) || (queueIndex.HasValue && at.QueueIndex != queueIndex.Value)) continue;
                string rowAccess = AccessOf(row);
                if ((!includeBarriers && rowAccess == "barrier") || (accessName is not null && rowAccess != accessName)) continue;
                EventRecord[] events = Events(at.QueueIndex);
                if (at.EventIndex >= events.Length) continue;
                EventRecord e = events[at.EventIndex];
                (string? before, string? after) = BarrierStates(row);
                uses.Add(new(at.QueueIndex, at.EventIndex, e.GpuId == uint.MaxValue ? null : e.GpuId, e.Name, string.Join("/", EventNavigation.MarkerPath(events, at.EventIndex)),
                    rowAccess, row.ViewType, row.Evidence ?? row.Binding.EventSource ?? "nativeBinding", row.Stage, before, after));
            }

            var orderKeys = new Dictionary<int, ulong[]>();
            ulong OrderKey(int q, uint i)
            {
                if (!orderKeys.TryGetValue(q, out ulong[]? keys))
                {
                    EventRecord[] events = Events(q);
                    keys = new ulong[events.Length];
                    ulong next = ulong.MaxValue;
                    for (int j = events.Length - 1; j >= 0; j--)
                    {
                        if (events[j].GpuId != uint.MaxValue) next = events[j].GpuId;
                        keys[j] = next;
                    }
                    orderKeys[q] = keys;
                }
                return i < keys.Length ? keys[i] : ulong.MaxValue;
            }
            bool PresentAfter(int q, uint i)
            {
                EventRecord[] events = Events(q);
                for (long j = (long)i + 1; j < events.Length; j++)
                    if (Tools.MatchesKind(events[j], "present")) return true;
                return false;
            }
            var eop = new Dictionary<(int, uint), ulong>();
            if (includeTiming && h.Timing is not null)
                foreach (int q in uses.Select(u => u.QueueIndex).Distinct())
                    foreach (EventTimingRow row in h.TimingRowsByQueue.GetValueOrDefault(q, []))
                        if (row.EopDuration != GpuCaptureHandle.TimingNone) eop.TryAdd((q, row.Index), row.EopDuration);

            ResourceTimelineResult result = ResourceTimeline.Build(new TimelineInputs(handle, resource.Name ?? key, resource.EstimatedBytes, uses, OrderKey, PresentAfter,
                (q, i) => eop.TryGetValue((q, i), out ulong duration) ? duration : null));
            ResourceTimelineRowDto[] page = result.Rows.Skip(offset).Take(limit).ToArray();
            int? next = offset + (long)page.Length < result.Rows.Count ? offset + page.Length : null;
            var calls = new List<ToolCallDto>();
            if (next.HasValue)
                calls.Add(new("pix_gpu_resource_timeline", new { resourceRef, scope, markerPathPrefix, queueIndex, access = accessName, includeBarriers, includeTiming, offset = next.Value, limit }));
            if (result.Summary.FirstWrite is EventRef firstWrite)
            {
                calls.Add(new("pix_gpu_inspect_event", new { eventRef = firstWrite }));
                calls.Add(new("pix_gpu_counters_read", new { handle, preset = "memoryBandwidth", scope = firstWrite }));
            }
            if (result.Rows.FirstOrDefault(r => r.Access == "barrier") is { } barrierRow)
                calls.Add(new("pix_gpu_events", new { handle, kind = "barrier", queueIndex = barrierRow.QueueIndex }));
            return new ResourceTimelineDto(resource, ResourceTimeline.Semantics + " " + FormatInfo.Note, result.Summary, result.Phases, result.Insights, result.Rows.Count, offset,
                page.Length, next, page, index.Coverage.Take(5).ToArray(), calls.Where(ToolRegistry.Accepts).ToArray())
            { Scope = selection.DescribeOrNull(h), Provenance = h.Provenance() };
        }, waitSeconds, cancellationToken);
    }

    /// <summary>
    /// Indexes every resource use the event collections show: views bound at each draw and dispatch, captured API object
    /// arguments (copies, indirect buffers) and barrier arguments with their states. Publishes the index for the scope
    /// (capture-wide when null) and returns it.
    /// </summary>
    internal static ResourceUseIndex BuildFallbackIndex(GpuCaptureHandle h, Job job, EventRef? scope, ResourceRef? nativeResource = null, long nativeBindingCount = 0)
    {
        var rows = new List<ResourceUseDto>();
        var coverage = new List<object>
        {
            nativeResource is null
                ? new { feature = "nativeBindingEvents", state = "notQueried", scope,
                    reason = "The resource-use index reads per-event draw and dispatch view collections, captured API object arguments and barrier arguments; resource-view-only API arguments may be absent." }
                : (object)new { feature = "nativeBindingEvents", state = "unavailable", resourceRef = nativeResource, nativeBindingCount, scope,
                    reason = "PIX returned incomplete binding events. Navigation uses per-event draw/dispatch collections and explicit API object arguments; resource-view-only API arguments may be absent." },
        };
        var scan = new List<(QueueEntry Queue, EventRecord[] Events, EventRecord[] Selected)>();
        foreach (QueueEntry queue in h.Queues.Where(q => scope is null || q.Index == scope.QueueIndex))
        {
            job.ThrowIfCancellationRequested();
            EventRecord[] events = h.AllEvents(queue.Index);
            scan.Add((queue, events, ResourceUseScanEvents(queue.Index, events, scope)));
        }
        long total = scan.Sum(q => (long)q.Selected.Length);
        long completed = 0;
        int barrierEvents = 0, barriersParsed = 0, barriersPartial = 0, barriersUnattributed = 0;
        var progressTimer = System.Diagnostics.Stopwatch.StartNew();
        job.SetProgress(0);
        job.AddMessage($"Indexing {total} event(s)" + (scope is null ? " across the capture." : $" within queue {scope.QueueIndex} event {scope.EventIndex} and descendants."));
        foreach (var (queue, events, selected) in scan)
        {
            foreach (EventRecord record in selected)
            {
                job.ThrowIfCancellationRequested();
                if (progressTimer.Elapsed.TotalSeconds >= 2)
                {
                    job.SetProgress(total == 0 ? 0 : (float)completed / total);
                    job.AddMessage($"Indexed {completed}/{total} event(s); reading queue {queue.Index} event {record.Index}.");
                    progressTimer.Restart();
                }
                completed++;
                var eventRef = new EventRef(h.Id, queue.Index, record.Index);
                string[] markerPath = EventNavigation.MarkerPath(events, record.Index);
                uint? gpuId = record.GpuId == uint.MaxValue ? null : record.GpuId;
                bool barrierEvent = Tools.MatchesKind(record, "barrier");
                BarrierParse? barriers = null;
                if (barrierEvent)
                {
                    barriers = ResourceAccess.ParseBarriers(record.ApiCallData);
                    barrierEvents++;
                    if (barriers.State == "full") barriersParsed++;
                    else if (barriers.State == "partial") barriersPartial++;
                    else barriersUnattributed++;
                }
                uint argumentIndex = 0;
                foreach (var (path, objectId) in ResourceAccess.ArgumentPaths(record.ApiCallData))
                {
                    ParsedBarrier? barrier = barriers?.Barriers.FirstOrDefault(b => b.ResourceId == objectId);
                    rows.Add(new ResourceUseDto(new(h.Id, Interop.Hex(objectId)), null, "API_ARGUMENT",
                        new BindingDto(argumentIndex++, "API_PARAMETER", eventRef, markerPath, new
                        {
                            parameter = path, apiCall = record.Name, barrierType = barrier?.Type, stateBefore = barrier?.StateBefore, stateAfter = barrier?.StateAfter,
                            subresource = barrier?.Subresource,
                        })
                        { EventSource = "capturedApiArgument" })
                    {
                        ViewIndexScope = "none", QueueIndex = queue.Index, EventIndex = record.Index, GpuId = gpuId,
                        Access = barrierEvent ? "barrier" : ResourceAccess.FromArgument(path, record.Name), Evidence = barrierEvent ? "barrierArgument" : "capturedApiArgument",
                    });
                }
                if (!Tools.MatchesKind(record, "work")) continue;
                try
                {
                    PIX_EVENT_INFO info = h.EventInfo(queue.Index, record.Index);
                    IPixProgramState state = PixApiExtensionsGpuCapture.GetProgramState(h.Document, ref info);
                    IPixGenericPipeline pipeline = PixApiExtensionsGpuCaptureResources.GetGpuProgram<IPixGenericPipeline>(state);
                    IPixResourceViewsAtEvent views = PixApiExtensionsGpuCaptureResources.GetResourceViews(pipeline);
                    h.GetAnalysis().GetAccessedResources(views);
                    for (uint i = 0; i < views.GetCount(); i++)
                    {
                        job.ThrowIfCancellationRequested();
                        IPixD3D12Resource? resource;
                        try
                        {
                            IPixD3D12ResourceView view = PixApiExtensionsGpuCaptureResources.GetResourceView<IPixD3D12ResourceView>(views, i);
                            resource = PixApiExtensionsGpuCaptureResources.GetD3D12Resource(view);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch { continue; } // Samplers and root constants have no backing resource.
                        if (resource is null) continue;
                        var observedResource = new ResourceRef(h.Id, Interop.Hex(resource.GetApiObjectId()));
                        PIX_RESOURCE_VIEW_TYPE type = PIX_RESOURCE_VIEW_TYPE.PIX_RESOURCE_NONE;
                        _IPixResourceViews_Extensions.GetType(views, i, ref type);
                        IPixResourceView resourceView = PixApiExtensionsGpuCaptureResources.GetResourceView<IPixResourceView>(views, i);
                        IPixResourceViewBindings bindings = PixApiExtensionsGpuCaptureResources.GetResourceBindings(resourceView);
                        var nativeBindings = new List<BindingDto>();
                        for (uint b = 0; b < bindings.GetCount(); b++)
                        {
                            job.ThrowIfCancellationRequested();
                            nativeBindings.Add(ReadBinding(bindings, b, h));
                        }
                        // The event-scoped collection proves the view is present here. Native binding records stay separate;
                        // their empty events are not repaired by guessing that every global binding belongs to this event.
                        string viewType = Json.EnumName(type);
                        rows.Add(new ResourceUseDto(observedResource, i, viewType,
                            new BindingDto(0, "VIEW_OBSERVED", eventRef, markerPath, new { nativeBindings }) { EventSource = "eventScopedView" })
                        {
                            ViewIndexScope = "event", QueueIndex = queue.Index, EventIndex = record.Index, GpuId = gpuId,
                            Access = ResourceAccess.FromViewType(viewType), Evidence = "eventScopedView", Stage = StageOf(nativeBindings),
                        });
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { coverage.Add(new { eventRef, unavailable = true, reason = PixErrors.Describe(ex) }); }
            }
        }
        coverage.Add(new
        {
            feature = "barriers", barrierEvents, parsed = barriersParsed, partial = barriersPartial, unattributed = barriersUnattributed,
            note = "Transition, UAV and aliasing barriers are parsed from element-form call text; enhanced barriers stay unattributed.",
        });
        job.ThrowIfCancellationRequested();
        var index = new ResourceUseIndex(rows, coverage);
        h.ResourceUses.PublishFallback(scope, index);
        job.SetProgress(1);
        job.AddMessage($"Indexed {completed}/{total} event(s), retaining {rows.Count} resource-use observation(s).");
        return index;
    }

    /// <summary>Every readable resource summary with size estimates, built once per handle (capture metadata; no replay).</summary>
    internal static List<ResourceSummaryDto> ResourceSummaries(GpuCaptureHandle h) => h.ResourceSummaryCache ??= AllResourceSummaries(h);

    internal static ResourceSummaryDto WithEstimate(ResourceSummaryDto r)
    {
        ResourceSizeEstimate size = FormatInfo.EstimateBytes(r.Dimension, r.Width, r.Height, r.DepthOrArraySize, r.MipLevels, r.Format, r.SampleCount);
        return r with
        {
            EstimatedBytes = size.Bytes, EstimateMethod = size.Method,
            HeapKind = r.Type?.ToUpperInvariant() switch { "COMMITTED" => "committed", "PLACED" => "placed", "RESERVED" => "reserved", _ => "unknown" },
        };
    }

    /// <summary>The row's access class: recorded by the index, else implied by its view type.</summary>
    internal static string AccessOf(ResourceUseDto row) => row.Access ?? ResourceAccess.FromViewType(row.ViewType);

    /// <summary>Fills access, event coordinates, evidence and stage on rows that predate them (native binding rows).</summary>
    internal static ResourceUseDto Enrich(ResourceUseDto row)
        => row.Access is not null ? row : row with
        {
            Access = ResourceAccess.FromViewType(row.ViewType), QueueIndex = row.Binding.EventRef?.QueueIndex, EventIndex = row.Binding.EventRef?.EventIndex,
            Evidence = row.Binding.EventSource ?? "nativeBinding", Stage = StageOf([row.Binding]),
        };

    internal static bool UsedAsMatches(ResourceUseDto row, string usedAs) => usedAs switch
    {
        "any" => true,
        "renderTarget" => row.ViewType == "RENDER_TARGET_VIEW",
        "depthStencil" => row.ViewType == "DEPTH_STENCIL_VIEW",
        "uav" => row.ViewType == "UNORDERED_ACCESS_VIEW",
        _ => AccessOf(row) == usedAs,
    };

    private static ResourceUseDto[] SortUses(IEnumerable<ResourceUseDto> rows, string sort) => sort switch
    {
        "access" => rows.OrderBy(r => Array.IndexOf(ResourceAccess.Classes, AccessOf(r))).ThenBy(r => r.Binding.EventRef?.QueueIndex ?? int.MaxValue)
            .ThenBy(r => r.Binding.EventRef?.EventIndex ?? uint.MaxValue).ToArray(),
        "viewType" => rows.OrderBy(r => r.ViewType, StringComparer.Ordinal).ThenBy(r => r.Binding.EventRef?.QueueIndex ?? int.MaxValue)
            .ThenBy(r => r.Binding.EventRef?.EventIndex ?? uint.MaxValue).ToArray(),
        _ => rows.OrderBy(r => r.Binding.EventRef?.QueueIndex ?? int.MaxValue).ThenBy(r => r.Binding.EventRef?.EventIndex ?? uint.MaxValue).ToArray(),
    };

    private static int TouchEvents(IEnumerable<ResourceUseDto> uses)
        => uses.Where(u => ResourceAccess.Reads(AccessOf(u)) || ResourceAccess.Writes(AccessOf(u))).Select(u => u.Binding.EventRef).OfType<EventRef>().Distinct().Count();

    private static ResourceTotalsDto Totals(IReadOnlyList<ResourceSummaryDto> rows)
    {
        static ResourceBucketDto Bucket(IEnumerable<ResourceSummaryDto> group)
        {
            ResourceSummaryDto[] items = group.ToArray();
            return new(items.Length, items.Aggregate(0UL, (sum, r) => sum + (r.EstimatedBytes ?? 0)));
        }
        var formats = rows.GroupBy(r => r.Format).Select(g => (Format: g.Key, Bucket: Bucket(g)))
            .OrderByDescending(f => f.Bucket.EstimatedBytes).ThenByDescending(f => f.Bucket.Count).ThenBy(f => f.Format, StringComparer.Ordinal).ToList();
        var byFormat = formats.Take(8).Select(f => new ResourceFormatBucketDto(f.Format, f.Bucket.Count, f.Bucket.EstimatedBytes)).ToList();
        if (formats.Count > 8)
            byFormat.Add(new("other", formats.Skip(8).Sum(f => f.Bucket.Count), formats.Skip(8).Aggregate(0UL, (sum, f) => sum + f.Bucket.EstimatedBytes)));
        return new(rows.Count, rows.Aggregate(0UL, (sum, r) => sum + (r.EstimatedBytes ?? 0)), rows.Count(r => r.EstimatedBytes is null),
            rows.GroupBy(r => r.Dimension).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => Bucket(g)),
            byFormat, rows.GroupBy(r => r.HeapKind ?? "unknown").OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => Bucket(g)));
    }

    private static ResourceScopeSummaryDto ScopeSummary(GpuCaptureHandle h, ScopeSelection selection, IReadOnlyList<ResourceUseDto> uses)
    {
        int work = 0;
        foreach (int q in selection.Queues(h))
        {
            EventRecord[] events = h.AllEvents(q);
            for (uint i = 0; i < events.Length; i++)
                if (Tools.MatchesKind(events[i], "work") && selection.Contains(q, events, i)) work++;
        }
        Dictionary<string, ResourceSummaryDto> byId = ResourceSummaries(h).GroupBy(s => s.ApiObjectId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        ResourceTargetSummaryDto[] Targets(string viewType) => uses.Where(u => u.ViewType == viewType).GroupBy(u => u.ResourceRef.ApiObjectId, StringComparer.OrdinalIgnoreCase)
            .Select(g => byId.TryGetValue(g.Key, out ResourceSummaryDto? s)
                ? new ResourceTargetSummaryDto(s.ResourceRef, s.Name, s.Width, s.Height, s.Format, s.SampleCount, g.Select(u => u.Binding.EventRef).OfType<EventRef>().Distinct().Count())
                : null)
            .OfType<ResourceTargetSummaryDto>().OrderByDescending(t => t.Events).ToArray();
        return new(work, Targets("RENDER_TARGET_VIEW"), Targets("DEPTH_STENCIL_VIEW"), uses.Count(u => AccessOf(u) == "unknown"));
    }

    private static (string? Before, string? After) BarrierStates(ResourceUseDto row)
    {
        if (AccessOf(row) != "barrier" || row.Binding.Detail is null) return (null, null);
        JsonElement detail = JsonSerializer.SerializeToElement(row.Binding.Detail, Json.Options);
        return (Text(detail, "stateBefore"), Text(detail, "stateAfter"));
    }

    internal static string? StageOf(IEnumerable<BindingDto> bindings)
    {
        foreach (BindingDto binding in bindings)
            if (binding.Detail is not null && Text(JsonSerializer.SerializeToElement(binding.Detail, Json.Options), "stage") is string stage) return stage;
        return null;
    }

    private static string? Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
