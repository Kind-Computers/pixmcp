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
    [McpServerTool(Name = "pix_gpu_open"), Description("Opens a PIX GPU capture (.wpix) and returns a handle plus file info, application description and the list of queues. Opening is cheap; GPU analysis (needed for timing, counters, pipeline state, resources, Dr. PIX) is started on demand or via pix_gpu_analysis_start.")]
    public static Task<string> Open(PixSession session, [Description("Path to the .wpix GPU capture file.")] string path, CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_gpu_open", () =>
        {
            string full = Tools.RequireFile(path, "GPU capture file");
            IPixGpuCaptureDocument document = session.Factory.OpenGpuCaptureDocument<IPixGpuCaptureDocument>(full);
            GpuCaptureHandle handle = session.Register(new GpuCaptureHandle(full, document));
            return Info(handle);
        }, cancellationToken);

    [McpServerTool(Name = "pix_gpu_info", ReadOnly = true), Description("File info, application description, queues and analysis state for an open GPU capture.")]
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
            analysis = h.AnalysisStatus(),
        };
    }

    private static string FormatVersion(ulong v) => $"{(v >> 48) & 0xFFFF}.{(v >> 32) & 0xFFFF}.{(v >> 16) & 0xFFFF}.{v & 0xFFFF}";

    [McpServerTool(Name = "pix_gpu_queues", ReadOnly = true), Description("Lists the command queues in a GPU capture (index, id, name, type, adapter, event count).")]
    public static Task<string> Queues(PixSession session, [Description("GPU capture handle")] string handle, CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_gpu_queues", () => session.Get<GpuCaptureHandle>(handle).Queues.Select(q => q.ToDto()).ToArray(), cancellationToken);

    [McpServerTool(Name = "pix_gpu_events", ReadOnly = true), Description("Pages through the events of a GPU capture queue (or all queues when queueIndex is omitted), with optional filters. Events form a tree via parentIndex; gpuId identifies GPU work for timing/Dr. PIX ranges.")]
    public static Task<string> Events(
        PixSession session,
        [Description("GPU capture handle")] string handle,
        [Description("Queue index from pix_gpu_queues. Omit to search all queues.")] int? queueIndex = null,
        [Description("First item to return (default 0).")] int offset = 0,
        [Description("Maximum items to return (default 100, max 1000).")] int limit = Paging.DefaultLimit,
        [Description("Only events whose name contains this text (case-insensitive).")] string? nameContains = null,
        [Description("Only events whose name starts with this text.")] string? nameStartsWith = null,
        [Description("Only events whose API call data contains this text.")] string? apiCallContains = null,
        [Description(Tools.KindDescription)] string? kind = null,
        [Description("Only direct children of this event index; requires queueIndex (event indices are per queue).")] uint? parentIndex = null,
        [Description("Only events with gpuId >= this value.")] uint? gpuIdMin = null,
        [Description("Only events with gpuId <= this value.")] uint? gpuIdMax = null,
        CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_gpu_events", () =>
        {
            GpuCaptureHandle h = session.Get<GpuCaptureHandle>(handle);
            if (parentIndex.HasValue && !queueIndex.HasValue)
            {
                throw new McpException("parentIndex requires queueIndex: event indices are per queue, so a parent index alone is ambiguous across queues.");
            }
            (int o, int l) = Paging.Normalize(offset, limit);
            bool filtered = Tools.HasEventFilter(nameContains, nameStartsWith, apiCallContains, kind, parentIndex, gpuIdMin, gpuIdMax);

            if (queueIndex.HasValue && !filtered)
            {
                QueueEntry queue = h.Queue(queueIndex.Value);
                var page = new List<object>();
                for (uint i = (uint)o; i < queue.EventCount && page.Count < l; i++)
                {
                    page.Add(h.Event(queue.Index, i).ToDto(queue.Index));
                }
                return Paging.Page(page, queue.EventCount, o, l);
            }

            IEnumerable<int> queueIndices = queueIndex.HasValue ? new[] { queueIndex.Value } : h.Queues.Select(q => q.Index);
            var matches = new List<object>();
            long total = 0;
            foreach (int qi in queueIndices)
            {
                foreach (EventRecord e in Tools.FilterEvents(h.AllEvents(qi), nameContains, nameStartsWith, apiCallContains, kind, parentIndex, gpuIdMin, gpuIdMax))
                {
                    if (total >= o && matches.Count < l)
                    {
                        matches.Add(e.ToDto(qi));
                    }
                    total++;
                }
            }
            return Paging.Page(matches, total, o, l);
        }, cancellationToken);

    [McpServerTool(Name = "pix_gpu_event", ReadOnly = true), Description("Details for one event: its record, the chain of parent (marker) events, and its direct children.")]
    public static Task<string> Event(
        PixSession session,
        [Description("GPU capture handle")] string handle,
        [Description("Queue index")] int queueIndex,
        [Description("Event index within the queue")] uint eventIndex,
        [Description("Maximum direct children to include (default 50).")] int maxChildren = 50,
        CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_gpu_event", () =>
        {
            GpuCaptureHandle h = session.Get<GpuCaptureHandle>(handle);
            EventRecord[] all = h.AllEvents(queueIndex);
            if (eventIndex >= all.Length)
            {
                throw new McpException($"eventIndex {eventIndex} is out of range; queue {queueIndex} has {all.Length} event(s).");
            }
            EventRecord e = all[eventIndex];

            var parents = new List<object>();
            uint p = e.ParentIndex;
            int guard = 0;
            while (p != uint.MaxValue && p < all.Length && p != eventIndex && guard++ < 64)
            {
                parents.Add(all[p].ToDto(queueIndex));
                p = all[p].ParentIndex;
            }

            var children = new List<object>();
            int childCount = 0;
            foreach (EventRecord c in all)
            {
                if (c.ParentIndex == eventIndex && c.Index != eventIndex)
                {
                    if (children.Count < maxChildren)
                    {
                        children.Add(c.ToDto(queueIndex));
                    }
                    childCount++;
                }
            }

            return new { @event = e.ToDto(queueIndex), parents, childCount, children };
        }, cancellationToken);

    [McpServerTool(Name = "pix_gpu_api_objects", ReadOnly = true), Description("Lists D3D12 API objects recorded in the capture (heaps, resources, command queues, command allocators) with their ids and names.")]
    public static Task<string> ApiObjects(
        PixSession session,
        [Description("GPU capture handle")] string handle,
        [Description("First item (default 0).")] int offset = 0,
        [Description("Maximum items (default 100).")] int limit = Paging.DefaultLimit,
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

    [McpServerTool(Name = "pix_gpu_screenshot", Idempotent = true), Description("Saves the screenshot embedded in the GPU capture as a PNG file (and optionally returns it inline as an image).")]
    public static async Task<CallToolResult> Screenshot(
        PixSession session,
        [Description("GPU capture handle")] string handle,
        [Description("Output PNG path. Defaults to <capture>.screenshot.png next to the capture file.")] string? outPath = null,
        [Description("When true, also returns the PNG inline as image content (only if under 4 MB).")] bool inline = false)
    {
        try
        {
            (string json, byte[]? png) = await session.Run(() =>
            {
                GpuCaptureHandle h = session.Get<GpuCaptureHandle>(handle);
                IPixCapturedScreenshot screenshot = PixApiExtensionsGpuCapture.GetCapturedScreenshot(h.Document);
                PIX_CAPTURED_SCREENSHOT_INFO info = screenshot.GetInfo();
                byte[] pixels = PixApiExtensionsGpuCapture.GetPixelBytes(screenshot);
                if (!Png.IsSupported(info.Format))
                {
                    throw new McpException($"Screenshot format {Json.EnumName(info.Format)} is not supported by the PNG encoder ({info.Width}x{info.Height}, {pixels.Length} bytes).");
                }
                byte[] encoded = Png.Encode(pixels, (int)info.Width, (int)info.Height, (int)info.RowPitchBytes, info.Format);
                string target = string.IsNullOrWhiteSpace(outPath)
                    ? Path.Combine(Path.GetDirectoryName(h.Path) ?? ".", Path.GetFileNameWithoutExtension(h.Path) + ".screenshot.png")
                    : Path.GetFullPath(outPath);
                File.WriteAllBytes(target, encoded);
                string result = Json.Serialize(new
                {
                    path = target,
                    width = info.Width,
                    height = info.Height,
                    format = info.Format,
                    pngBytes = encoded.Length,
                });
                return (result, inline && encoded.Length < 4 * 1024 * 1024 ? encoded : null);
            }).ConfigureAwait(false);

            var content = new List<ContentBlock> { new TextContentBlock { Text = json } };
            if (png is not null)
            {
                content.Add(new ImageContentBlock { Data = png, MimeType = "image/png" });
            }
            return new CallToolResult { Content = content };
        }
        catch (Exception ex)
        {
            throw PixErrors.ToMcp(ex, "pix_gpu_screenshot");
        }
    }
}
