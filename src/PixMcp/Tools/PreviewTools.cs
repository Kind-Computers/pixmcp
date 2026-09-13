using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using PixMcp.Pix.Handles;

namespace PixMcp.Tools;

public enum PreviewTarget { RenderTarget, Depth }

/// <summary>Optional CLI backend. The complete process lifetime executes on the PIX worker.</summary>
[McpServerToolType]
public static class PreviewTools
{
    internal const int MaxInlineBytes = 4 * 1024 * 1024;
    internal const int MaxArtifactBytes = 32 * 1024 * 1024;

    /// <summary>Base64 bytes an inline image may take: the hard response budget minus the JSON envelope and a margin.</summary>
    internal static int InlineImageBudget(int jsonBytes) => Math.Max(1024, Tools.MaxResultBytes - jsonBytes - 1024);

    /// <summary>
    /// Largest pix_gpu_preview_bytes page: three quarters of the inline budget minus the envelope, so a page never
    /// crosses the deferral threshold (1024..16384).
    /// </summary>
    internal static int MaxBytesPerPage => Math.Clamp((Math.Min(ResultStore.TargetBytes, Tools.MaxResultBytes) - 2048) * 3 / 4, 1024, 16384);
    private static readonly ConditionalWeakTable<PixSession, PreviewArtifacts> Artifacts = new();

    [McpServerTool(Name = "pix_gpu_preview", Title = "Render preview (pixtool)", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), Description("Starts a rendering-preview job using installed pixtool. Stop all connected GPU analyses first. Saves the selected RTV (default 0) or depth visualization. With markerName, uses the last child of that globally unique exact marker with the resource bound; otherwise uses the last event with it bound. Job result contains an artifactRef and image retrieval call.")]
    public static Task<string> Preview(PixSession session, JobManager jobs,
        [Description("Open GPU capture handle.")] string handle,
        [Description("Globally unique, case-sensitive exact PIX marker name; omit for the last bound instance in the capture.")] string? markerName = null,
        [Description("RenderTarget or Depth visualization. Default: RenderTarget.")] PreviewTarget target = PreviewTarget.RenderTarget,
        [Description("RTV index, 0 through 7. Ignored only when zero for Depth.")] int rtvIndex = 0,
        [Description("Process timeout in seconds, 1 through 3600. Default: 120.")] int timeoutSeconds = 120,
        [Description(Tools.WaitSecondsDescription)] double waitSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        ValidateSelection(markerName, target, rtvIndex, timeoutSeconds);
        session.Get<GpuCaptureHandle>(handle);
        return Tools.RunJob(jobs, "pix_gpu_preview", () => jobs.StartForHandle<GpuCaptureHandle>("preview", $"Preview {handle}", handle, (job, capture) =>
        {
            PixToolProcess.EnsureReplayAvailable(session);
            string executable = PixToolProcess.Executable("preview", PixDiscovery.InstallDir);
            if (markerName is not null)
            {
                var markers = capture.Queues.SelectMany(q => capture.AllEvents(q.Index))
                    .Where(e => e.GpuId == uint.MaxValue || Tools.MatchesKind(e, "marker")).Select(e => e.Name);
                ValidateMarker(markers, markerName);
            }
            job.ThrowIfCancellationRequested();
            string folder = Path.Combine(Path.GetTempPath(), "pixmcp-preview-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string output = Path.Combine(folder, "preview.png");
            string replayCapture = Path.Combine(folder, "capture" + Path.GetExtension(capture.Path));
            try
            {
                PixToolProcess.CopyCapture(capture.Path, replayCapture, job.ThrowIfCancellationRequested);
                ProcessStartInfo start = BuildStartInfo(executable, replayCapture, output, markerName, target, rtvIndex);
                PixToolProcess.PrepareArguments(start);
                job.AddMessage("Replaying with pixtool defaults; native analysis adapter and power settings do not apply.");
                RunProcess(start, TimeSpan.FromSeconds(timeoutSeconds), job.Cancellation.Token, job.AddMessage);
                job.ThrowIfCancellationRequested();
                if (!File.Exists(output)) throw new PixToolException(PixErrors.Codes.PreviewMissingOutput, "pixtool completed without producing a PNG.");
                long size = new FileInfo(output).Length;
                if (size > MaxArtifactBytes) throw new PixToolException(PixErrors.Codes.PreviewTooLarge, $"PNG exceeds the {MaxArtifactBytes} byte artifact limit.");
                byte[] png = File.ReadAllBytes(output);
                (uint width, uint height) = PngDimensions(png);
                string artifactRef = Artifacts.GetOrCreateValue(session).Add(capture.Id, png);
                return new
                {
                    handle = capture.Id, artifactRef, mimeType = "image/png", width, height, pngBytes = png.Length,
                    target, rtvIndex = target == PreviewTarget.RenderTarget ? rtvIndex : (int?)null,
                    selection = new { markerName, semantics = markerName is null ? "last event with the selected resource bound" : "last child of the exact marker with the selected resource bound" },
                    replay = new { source = "pixtool", runtimeVersion = PixDiscovery.Version, settings = "CLI defaults, local replay; independent of native analysis settings" },
                    inlineAvailable = true, originalInlineAvailable = png.Length <= MaxInlineBytes,
                    nextCalls = new[] { new ToolCallDto("pix_gpu_preview_image", new { artifactRef }) },
                };
            }
            finally
            {
                try { if (File.Exists(output)) File.Delete(output); if (File.Exists(replayCapture)) File.Delete(replayCapture); if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder); }
                catch (IOException ex) { job.AddMessage("Temporary preview cleanup failed: " + ex.Message); }
                catch (UnauthorizedAccessException ex) { job.AddMessage("Temporary preview cleanup failed: " + ex.Message); }
            }
        }), waitSeconds, cancellationToken);
    }

    [McpServerTool(Name = "pix_gpu_preview_image", Title = "Preview image", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Returns a preview or embedded screenshot as inline PNG. Optional crop uses original pixel coordinates, then maxDimension bounds the longest edge without upscaling. Set ignoreAlpha=true to view render-target RGB as opaque when stored alpha hides useful colors. Originals above 4 MiB automatically get a thumbnail. Original bytes remain available through pix_gpu_preview_bytes. Artifacts expire on capture close or cache eviction.")]
    public static async Task<CallToolResult> Image(PixSession session, [Description("Artifact reference returned by pix_gpu_preview or pix_gpu_screenshot.")] string artifactRef, [Description("Crop rectangle in original pixel coordinates { x, y, width, height }.")] ImageCrop? crop = null,
        [Description("Bound on the longest edge in pixels (1..4096); never upscales.")] int? maxDimension = null,
        [Description("Display stored RGB as opaque before crop/resize; default false preserves alpha. Original artifact bytes are unchanged.")] bool ignoreAlpha = false,
        CancellationToken cancellationToken = default)
    {
        byte[] original = GetArtifact(session, artifactRef);
        RenderedImage rendered = await ImageRenderer.Render(original, crop, maxDimension, ignoreAlpha, cancellationToken).ConfigureAwait(false);
        (rendered, bool budgetLimited) = await ImageRenderer.FitToBudget(rendered, original, InlineImageBudget(1024), cancellationToken).ConfigureAwait(false);
        return ImageResult(artifactRef, rendered, budgetLimited);
    }

    internal static CallToolResult ImageResult(string artifactRef, RenderedImage rendered, bool budgetLimited = false)
    {
        string json = Json.Serialize(new { artifactRef, mimeType = "image/png", pngBytes = rendered.Png.Length,
            originalWidth = rendered.OriginalWidth, originalHeight = rendered.OriginalHeight,
            width = rendered.Width, height = rendered.Height, crop = rendered.Crop, resized = rendered.Resized,
            budgetLimited, alphaIgnored = rendered.AlphaIgnored });
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = json }, ImageContentBlock.FromBytes(rendered.Png, "image/png")],
            StructuredContent = JsonSerializer.Deserialize<JsonElement>(json),
        };
    }

    [McpServerTool(Name = "pix_gpu_preview_bytes", Title = "Preview PNG bytes", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Retrieves PNG artifact bytes as paged base64. Decode each page separately and concatenate the decoded bytes. Available while the capture remains open and the artifact has not been evicted.")]
    public static string Bytes(PixSession session, [Description("Artifact reference returned by pix_gpu_preview or pix_gpu_screenshot.")] string artifactRef, [Description("First byte to return (default 0).")] int offset = 0, [Description("Maximum bytes per page (default 16384).")] int limit = 16384)
    {
        if (offset < 0 || limit < 1 || limit > 16384) throw new PixToolException(PixErrors.Codes.InvalidArguments, "offset must be nonnegative; limit must be 1 through 16384.");
        byte[] png = GetArtifact(session, artifactRef);
        limit = Math.Min(limit, MaxBytesPerPage);
        int count = Math.Min(limit, Math.Max(0, png.Length - offset));
        int? nextOffset = (long)offset + count < png.Length ? offset + count : null;
        return Json.Serialize(new { artifactRef, mimeType = "image/png", offset, totalBytes = png.Length, returnedBytes = count,
            base64 = count == 0 ? "" : Convert.ToBase64String(png, offset, count), nextOffset,
            nextCalls = nextOffset.HasValue ? new[] { new ToolCallDto("pix_gpu_preview_bytes", new { artifactRef, offset = nextOffset.Value, limit }) } : null });
    }

    private static byte[] GetArtifact(PixSession session, string artifactRef)
        => Artifacts.GetOrCreateValue(session).Get(artifactRef, id => session.TryGet<GpuCaptureHandle>(id) is not null);

    internal static string StoreArtifact(PixSession session, string handle, byte[] png)
        => Artifacts.GetOrCreateValue(session).Add(handle, png);

    internal static void ForgetCapture(PixSession session, string handle)
    {
        if (Artifacts.TryGetValue(session, out PreviewArtifacts? artifacts)) artifacts.Forget(handle);
    }

    internal static void ValidateSelection(string? markerName, PreviewTarget target, int rtvIndex, int timeoutSeconds)
    {
        if (markerName?.Any(c => c == '"' || char.IsControl(c)) == true)
            throw new PixToolException(PixErrors.Codes.UnsupportedSelection, "The pixtool parser cannot reliably represent marker names containing literal quotes or control characters. Omit markerName to inspect the last bound resource.");
        if (!Enum.IsDefined(target) || rtvIndex is < 0 or > 7 || target == PreviewTarget.Depth && rtvIndex != 0 || timeoutSeconds is < 1 or > 3600 || markerName is not null && string.IsNullOrWhiteSpace(markerName))
            throw new PixToolException(PixErrors.Codes.InvalidArguments, "Use a valid target, RTV 0 through 7 (0 for depth), nonempty marker, and timeoutSeconds 1 through 3600.");
    }

    internal static void ValidateMarker(IEnumerable<string> markers, string markerName)
    {
        int matches = markers.Count(name => name.Equals(markerName, StringComparison.Ordinal));
        if (matches != 1) throw new PixToolException(PixErrors.Codes.AmbiguousMarker, $"markerName must identify exactly one PIX marker across all queues; found {matches} exact matches.");
    }

    internal static ProcessStartInfo BuildStartInfo(string executable, string capture, string output, string? markerName, PreviewTarget target, int rtvIndex)
    {
        var start = PixToolProcess.StartInfo(executable, ["--output=quiet", "--log=off", "open-capture", capture, "save-resource", output]);
        start.ArgumentList.Add(target == PreviewTarget.Depth ? "--depth" : $"--rtv={rtvIndex}");
        if (markerName is not null) start.ArgumentList.Add("--marker=" + markerName);
        return start;
    }

    internal static string FormatPixToolArguments(IEnumerable<string> arguments)
        => PixToolProcess.FormatArguments(arguments);

    internal static void RunProcess(ProcessStartInfo start, TimeSpan timeout, CancellationToken cancellation, Action<string> diagnostic)
        => PixToolProcess.Run(start, timeout, cancellation, diagnostic, "preview");

    internal static (uint Width, uint Height) PngDimensions(byte[] png)
    {
        if (png.Length < 24 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) || !png.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            throw new PixToolException(PixErrors.Codes.PreviewInvalidOutput, "pixtool output does not contain a valid PNG header.");
        return (BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4)), BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4)));
    }
}

internal sealed class PreviewArtifacts
{
    private readonly Dictionary<string, (string Handle, byte[] Bytes)> _entries = new();
    private readonly Queue<string> _order = new();
    private long _bytes;
    internal string Add(string handle, byte[] bytes)
    {
        if (bytes.Length > PreviewTools.MaxArtifactBytes)
            throw new PixToolException(PixErrors.Codes.ImageTooLarge, "The image exceeds the 32 MiB artifact limit.");
        lock (_entries)
        {
            string id = "preview-" + Guid.NewGuid().ToString("N");
            _entries[id] = (handle, bytes); _order.Enqueue(id); _bytes += bytes.Length;
            while (_entries.Count > 50 || _bytes > 64 * 1024 * 1024)
                if (_entries.Remove(_order.Dequeue(), out var removed)) _bytes -= removed.Bytes.Length;
            return id;
        }
    }
    internal byte[] Get(string id, Func<string, bool> isOpen)
    {
        lock (_entries)
        {
            foreach (string expired in _entries.Where(entry => !isOpen(entry.Value.Handle)).Select(entry => entry.Key).ToArray())
                if (_entries.Remove(expired, out var removed)) _bytes -= removed.Bytes.Length;
            CompactOrder();
            if (_entries.TryGetValue(id, out var entry)) return entry.Bytes;
            throw new PixToolException(PixErrors.Codes.ArtifactExpired, "The preview artifact is unknown, was evicted, or its capture has closed. Run pix_gpu_preview again.");
        }
    }
    internal void Forget(string handle)
    {
        lock (_entries)
        {
            foreach (string id in _entries.Where(entry => entry.Value.Handle == handle).Select(entry => entry.Key).ToArray())
                if (_entries.Remove(id, out var removed)) _bytes -= removed.Bytes.Length;
            CompactOrder();
        }
    }

    private void CompactOrder()
    {
        string[] active = _order.Where(_entries.ContainsKey).ToArray();
        _order.Clear();
        foreach (string id in active) _order.Enqueue(id);
    }
}
