using System.ComponentModel;
using Microsoft.PIX;
using Microsoft.PIX.Extension;
using Microsoft.PIX.Extension.GpuCapture;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using PixMcp.Pix.Handles;

namespace PixMcp.Tools;

[McpServerToolType]
public static class GpuCaptureTools
{
    [McpServerTool(Name = "pix_gpu_open", Title = "Open GPU capture", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description("Opens a PIX GPU capture (.wpix / .wpix_preview) and returns a handle plus the same payload as pix_gpu_info (file info, application description, queues, analysis state). Opening is cheap; GPU analysis (needed for timing, counters, pipeline state, resources, Dr. PIX) is started on demand or via pix_gpu_analysis_start. Each open creates a new handle; reuse the handle instead of reopening.")]
    public static Task<string> Open(PixSession session, [Description("Path to the .wpix GPU capture file.")] string path, CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_gpu_open", () =>
        {
            string full = Tools.RequireFile(path, "GPU capture file");
            IPixGpuCaptureDocument document = session.Factory.OpenGpuCaptureDocument<IPixGpuCaptureDocument>(full);
            GpuCaptureHandle handle = session.Register(new GpuCaptureHandle(full, document));
            return Info(handle);
        }, cancellationToken);

    [McpServerTool(Name = "pix_gpu_info", Title = "GPU capture info", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("File info, application description, queues and analysis state for an already open GPU capture (the same payload pix_gpu_open returned).")]
    public static Task<string> GetInfo(PixSession session, [Description("GPU capture handle")] string handle, CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_gpu_info", () => Info(session.Get<GpuCaptureHandle>(handle)), cancellationToken);

    private static object Info(GpuCaptureHandle h)
    {
        object? fileInfo = null;
        try
        {
            IPixGpuCaptureFileInfo info = PixApiExtensionsGpuCapture.GetFileInfo<IPixGpuCaptureFileInfo>(h.Document);
            var pairs = new Dictionary<string, string>();
            ulong pairCount = info.GetNumFileInfoStringPairs();
            for (ulong i = 0; i < pairCount; i++)
            {
                PixApiExtensionsGpuCapture.GetFileInfoStringPair(info, i, out string name, out string value);
                pairs[name] = value;
            }
            fileInfo = new
            {
                captureTime = PixApiExtensionsGpuCapture.GetCaptureDateTime(info),
                captureDevice = Interop.W(info.GetCaptureDevice()),
                fileVersion = info.GetFileVersion(),
                compressedSize = info.GetCompressedSize(),
                uncompressedSize = info.GetUncompressedSize(),
                properties = pairs,
            };
        }
        catch (Exception ex)
        {
            fileInfo = PixErrors.Unavailable("fileInfo", ex);
        }

        object? application = null;
        try
        {
            if (h.Document.HasApplicationDescription())
            {
                PixApplicationDesc desc = PixApiExtensionsGpuCapture.GetApplicationDescription(h.Document);
                application = new
                {
                    name = desc.Name,
                    exeFilename = desc.ExeFilename,
                    version = FormatVersion(desc.Version),
                    engineName = desc.EngineName,
                    engineVersion = FormatVersion(desc.EngineVersion),
                    appId = desc.AppId,
                };
            }
        }
        catch (Exception ex)
        {
            application = PixErrors.Unavailable("applicationDescription", ex);
        }

        return new
        {
            handle = h.Id,
            path = h.Path,
            fileInfo,
            application,
            queues = h.Queues.Select(q => q.ToDto()).ToArray(),
            totalEvents = h.Queues.Sum(q => (long)q.EventCount),
            vendor = h.CaptureVendor(),
            analysis = h.AnalysisStatus(),
            capabilities = h.CapabilitiesSnapshot(),
        };
    }

    private static string FormatVersion(ulong v) => $"{(v >> 48) & 0xFFFF}.{(v >> 32) & 0xFFFF}.{(v >> 16) & 0xFFFF}.{v & 0xFFFF}";

    [McpServerTool(Name = "pix_gpu_queues", Title = "List capture queues", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Lists the command queues in a GPU capture (index, id, name, type, adapter, event count).")]
    public static Task<string> Queues(PixSession session, [Description("GPU capture handle")] string handle, CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_gpu_queues", () => SessionTools.Envelope(session.Get<GpuCaptureHandle>(handle).Queues.Select(q => q.ToDto()).ToArray()), cancellationToken);

    [McpServerTool(Name = "pix_gpu_events", Title = "List capture events", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Pages through the events of a GPU capture queue (or all queues when queueIndex is omitted), with optional filters. Events form a tree via parentIndex; gpuId identifies GPU work for timing/Dr. PIX ranges.")]
    public static Task<string> Events(
        PixSession session,
        [Description("GPU capture handle")] string handle,
        [Description("Queue index from pix_gpu_queues. Omit to search all queues.")] int? queueIndex = null,
        [Description("First item to return (default 0).")] int offset = 0,
        [Description("Maximum items to return (default 25, max 1000).")] int limit = Paging.DefaultLimit,
        [Description("Only events whose name contains this text (case-insensitive).")] string? nameContains = null,
        [Description("Only events whose name starts with this text.")] string? nameStartsWith = null,
        [Description("Only events whose API call data contains this text.")] string? apiCallContains = null,
        [Description(Tools.KindDescription)] string? kind = null,
        [Description("Only direct children of this event index; requires queueIndex (event indices are per queue).")] uint? parentIndex = null,
        [Description("Only events with gpuId >= this value.")] uint? gpuIdMin = null,
        [Description("Only events with gpuId <= this value.")] uint? gpuIdMax = null,
        [Description(EventScope.Description)] EventRef? scope = null,
        [Description(EventScope.PrefixDescription)] string? markerPathPrefix = null,
        [Description(Shaping.FormatDescription)] string format = "objects",
        [Description(Shaping.BriefDescription)] bool brief = false,
        [Description(Shaping.TopNDescription)] int? topN = null,
        [Description(Shaping.MaxStringLengthDescription)] int? maxStringLength = null,
        CancellationToken cancellationToken = default)
    {
        ScopeSelection selection = EventScope.Resolve(session, handle, queueIndex, scope, markerPathPrefix);
        ShapingOptions shaping = Shaping.Options(format, brief, topN, maxStringLength, offset);
        queueIndex ??= scope?.QueueIndex;
        return Tools.Run(session, "pix_gpu_events", () =>
        {
            GpuCaptureHandle h = session.Get<GpuCaptureHandle>(handle);
            if (parentIndex.HasValue && !queueIndex.HasValue)
            {
                throw new PixToolException(PixErrors.Codes.InvalidArguments, "parentIndex requires queueIndex: event indices are per queue, so a parent index alone is ambiguous across queues.");
            }
            (int o, int l) = Shaping.Window(shaping, offset, limit);
            ToolCallDto Call(int at, int? strings) => new("pix_gpu_events", new { handle, queueIndex, offset = at, limit = l, nameContains, nameStartsWith,
                apiCallContains, kind, parentIndex, gpuIdMin, gpuIdMax, scope, markerPathPrefix, format, brief, maxStringLength = strings });
            object Shape(List<EventDto> page, long total) => Shaping.Apply(page, total, o, l, shaping, RowShapes.Events, handle,
                selection.IsUnrestricted ? null : new { scope = selection.Describe(h) }, next => Call(next, maxStringLength), () => Call(o, Shaping.FullStringLength));
            bool filtered = !selection.IsUnrestricted || Tools.HasEventFilter(nameContains, nameStartsWith, apiCallContains, kind, parentIndex, gpuIdMin, gpuIdMax);

            if (queueIndex.HasValue && !filtered)
            {
                QueueEntry queue = h.Queue(queueIndex.Value);
                var page = new List<EventDto>();
                for (uint i = (uint)o; i < queue.EventCount && page.Count < l; i++)
                {
                    page.Add(h.DescribeEvent(queue.Index, h.Event(queue.Index, i)));
                }
                return Shape(page, queue.EventCount);
            }

            IEnumerable<int> queueIndices = queueIndex.HasValue ? new[] { queueIndex.Value } : h.Queues.Select(q => q.Index);
            var matches = new List<EventDto>();
            long total = 0;
            foreach (int qi in queueIndices)
            {
                foreach (EventRecord e in Tools.FilterEvents(h.AllEvents(qi), nameContains, nameStartsWith, apiCallContains, kind, parentIndex, gpuIdMin, gpuIdMax))
                {
                    if (!selection.Contains(h, qi, e.Index)) continue;
                    if (total >= o && matches.Count < l)
                    {
                        matches.Add(h.DescribeEvent(qi, e));
                    }
                    total++;
                }
            }
            return Shape(matches, total);
        }, cancellationToken);
    }

    [McpServerTool(Name = "pix_gpu_event", Title = "Event detail", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Inspect an event reference returned by event/timing queries, including its marker ancestors and direct children. Page further children with pix_gpu_events.")]
    public static Task<string> EventByRef(PixSession session, [Description("Event reference { handle, queueIndex, eventIndex } as returned by pix_gpu_events or pix_gpu_overview.")] EventRef eventRef, [Description("Maximum direct children to include (default 25, max 1000).")] int maxChildren = 25,
        CancellationToken cancellationToken = default)
        => Event(session, eventRef.Handle, eventRef.QueueIndex, eventRef.EventIndex, maxChildren: maxChildren, cancellationToken: cancellationToken);

    // Internal compatibility helper; the MCP surface accepts only an unambiguous EventRef.
    public static Task<string> Event(
        PixSession session,
        [Description("GPU capture handle")] string handle,
        [Description("Queue index (required unless gpuId is given).")] int? queueIndex = null,
        [Description("Event index within the queue (required unless gpuId is given).")] uint? eventIndex = null,
        [Description("Find the event by its gpuId instead; all queues are searched.")] uint? gpuId = null,
        [Description("Maximum direct children to include (default 50, max 1000).")] int maxChildren = 50,
        CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_gpu_event", () =>
        {
            GpuCaptureHandle h = session.Get<GpuCaptureHandle>(handle);
            int q;
            uint index;
            if (gpuId.HasValue)
            {
                (q, EventRecord found) = h.FindByGpuId(gpuId.Value) ?? throw PixErrors.InvalidReference($"No event with gpuId {gpuId} exists in any queue of {h.Id}.",
                    new ToolCallDto("pix_gpu_events", new { handle = h.Id, gpuIdMin = gpuId, gpuIdMax = gpuId }, CostHints.Query));
                index = found.Index;
            }
            else if (queueIndex.HasValue && eventIndex.HasValue)
            {
                q = queueIndex.Value;
                index = eventIndex.Value;
            }
            else
            {
                throw PixErrors.InvalidArguments("Specify queueIndex and eventIndex, or gpuId.");
            }
            EventRecord[] all = h.AllEvents(q);
            if (index >= all.Length)
            {
                throw PixErrors.InvalidReference($"eventIndex {index} is out of range; queue {q} has {all.Length} event(s).",
                    new ToolCallDto("pix_gpu_events", new { handle = h.Id, queueIndex = q }, CostHints.Query));
            }
            EventRecord e = all[index];

            const int maxParents = 64;
            var parents = new List<object>();
            var visited = new HashSet<uint> { index };
            uint p = e.ParentIndex;
            while (p < all.Length && parents.Count < maxParents && visited.Add(p))
            {
                parents.Add(h.DescribeEvent(q, all[p]));
                p = all[p].ParentIndex;
            }
            bool parentsTruncated = p < all.Length && !visited.Contains(p);

            int max = Math.Clamp(maxChildren, 0, Paging.MaxLimit);
            var children = new List<object>();
            int childCount = 0;
            foreach (EventRecord c in all)
            {
                if (c.ParentIndex == index && c.Index != index)
                {
                    if (children.Count < max)
                    {
                        children.Add(h.DescribeEvent(q, c));
                    }
                    childCount++;
                }
            }

            var nextCalls = new List<ToolCallDto>();
            if (childCount > children.Count) nextCalls.Add(new("pix_gpu_events", new { handle, queueIndex = q, parentIndex = index, offset = children.Count, limit = 25 }));
            if (parentsTruncated) nextCalls.Add(new("pix_gpu_event", new { eventRef = new EventRef(handle, q, p), maxChildren = 0 }));
            return new { @event = h.DescribeEvent(q, e), parents, parentsTruncated, childCount, children, childrenTruncated = childCount > children.Count, nextCalls };
        }, cancellationToken);

    [McpServerTool(Name = "pix_gpu_api_objects", Title = "List API objects", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Lists D3D12 API objects recorded in the capture (heaps, resources, command queues, command allocators) with their ids and names. For resource details use pix_gpu_resources / pix_gpu_resource.")]
    public static Task<string> ApiObjects(
        PixSession session,
        [Description("GPU capture handle")] string handle,
        [Description("First item (default 0).")] int offset = 0,
        [Description("Maximum items (default 25, max 1000).")] int limit = Paging.DefaultLimit,
        [Description("Filter by object type: HEAP, RESOURCE, COMMAND_QUEUE, COMMAND_ALLOCATOR.")] string? type = null,
        [Description("Only objects whose name contains this text.")] string? nameContains = null,
        CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_gpu_api_objects", () =>
        {
            GpuCaptureHandle h = session.Get<GpuCaptureHandle>(handle);
            (int o, int l) = Paging.Normalize(offset, limit);
            PIX_API_OBJECT_TYPE? wanted = string.IsNullOrEmpty(type) ? null : Tools.ParseEnum<PIX_API_OBJECT_TYPE>(type);

            IPixD3D12ApiObjectCollection objects = PixApiExtensionsGpuCapture.GetD3D12ApiObjects(h.Document);
            ulong count = objects.GetCount();
            var page = new List<object>();
            long total = 0;
            for (ulong i = 0; i < count; i++)
            {
                PIX_API_OBJECT_TYPE objectType = PixApiExtensionsGpuCapture.GetApiObjectType(objects, i);
                if (wanted.HasValue && objectType != wanted.Value)
                {
                    continue;
                }
                IPixD3D12ApiObject? obj = PixApiExtensions.TryGet<IPixD3D12ApiObject>(objects, i, out _);
                if (obj is null)
                {
                    continue;
                }
                string name = Interop.W(obj.GetName());
                if (!Tools.Contains(name, nameContains))
                {
                    continue;
                }
                if (total >= o && page.Count < l)
                {
                    page.Add(new { index = i, apiObjectId = Interop.Hex(obj.GetApiObjectId()), type = objectType, name });
                }
                total++;
            }
            return Paging.Page(page, total, o, l);
        }, cancellationToken);

    [McpServerTool(Name = "pix_gpu_screenshot", Title = "Capture screenshot", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), Description("The screenshot embedded in the GPU capture as PNG. Writes nothing unless outPath is set; the PNG is always retained as an artifact (artifactRef, read with pix_gpu_preview_image) and returned inline as image content when inline=true (shrunk to the response budget when needed). 8/10-bit UNORM swapchains are copied; HDR swapchains (R16G16B16A16_FLOAT/UNORM, R11G11B10_FLOAT) are tone-mapped to sRGB (toneMapped: true).")]
    public static async Task<CallToolResult> Screenshot(
        PixSession session,
        [Description("GPU capture handle")] string handle,
        [Description("Output PNG path; nothing is written when omitted (default). The parent directory must exist.")] string? outPath = null,
        [Description("When true, returns the image inline; large PNGs get a thumbnail sized to the response budget while the original remains retrievable (default false).")] bool inline = false,
        [Description("Replace an existing outPath file (default false: file_exists).")] bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var captured = await session.Run(() =>
            {
                GpuCaptureHandle h = session.Get<GpuCaptureHandle>(handle);
                IPixCapturedScreenshot screenshot = PixApiExtensionsGpuCapture.GetCapturedScreenshot(h.Document);
                PIX_CAPTURED_SCREENSHOT_INFO info = screenshot.GetInfo();
                byte[] pixels = PixApiExtensionsGpuCapture.GetPixelBytes(screenshot);
                if (!Png.IsSupported(info.Format))
                {
                    throw PixErrors.UnsupportedFeature($"Screenshot format {Json.EnumName(info.Format)} is not supported by the PNG encoder ({info.Width}x{info.Height}, {pixels.Length} bytes).");
                }
                return (Info: info, Pixels: pixels, CapturePath: h.Path);
            }, cancellationToken, "pix_gpu_screenshot").ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            // Encoding, image transforms and filesystem I/O need no native PIX objects.
            var screenshotInfo = captured.Info;
            byte[] encoded = Png.Encode(captured.Pixels, (int)screenshotInfo.Width, (int)screenshotInfo.Height, (int)screenshotInfo.RowPitchBytes, screenshotInfo.Format);
            string? target = null;
            if (!string.IsNullOrWhiteSpace(outPath))
            {
                target = Tools.PrepareOutputPath(outPath, overwrite, session.Results);
                await File.WriteAllBytesAsync(target, encoded, cancellationToken).ConfigureAwait(false);
            }
            string? artifactRef = encoded.Length <= PreviewTools.MaxArtifactBytes ? PreviewTools.StoreArtifact(session, handle, encoded) : null;
            RenderedImage? rendered = null; bool budgetLimited = false;
            if (inline)
            {
                rendered = await ImageRenderer.Render(encoded, cancellationToken: cancellationToken).ConfigureAwait(false);
                (rendered, budgetLimited) = await ImageRenderer.FitToBudget(rendered, encoded, PreviewTools.InlineImageBudget(2048), cancellationToken).ConfigureAwait(false);
            }
            string json = Json.Serialize(new
            {
                path = target, width = screenshotInfo.Width, height = screenshotInfo.Height, format = screenshotInfo.Format,
                toneMapped = Png.IsToneMapped(screenshotInfo.Format) ? true : (bool?)null, pngBytes = encoded.Length,
                artifactRef, artifactAvailable = artifactRef is not null,
                artifactUnavailable = artifactRef is null ? "Original PNG exceeds the 32 MiB artifact limit; use outPath for the complete file." : null,
                image = rendered is null ? null : new { width = rendered.Width, height = rendered.Height, resized = rendered.Resized, budgetLimited },
                nextCalls = artifactRef is null ? Array.Empty<ToolCallDto>() : [new ToolCallDto("pix_gpu_preview_image", new { artifactRef })],
            });
            var content = new List<ContentBlock> { new TextContentBlock { Text = json } };
            if (rendered is not null)
            {
                content.Add(ImageContentBlock.FromBytes(rendered.Png, "image/png"));
            }
            return new CallToolResult { Content = content, StructuredContent = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json) };
        }
        catch (Exception ex)
        {
            throw PixErrors.ToMcp(ex, "pix_gpu_screenshot");
        }
    }
}
