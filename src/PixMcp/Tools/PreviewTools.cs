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

/// <summary>One image a preview saves.</summary>
public sealed record PreviewTargetSpec(
    [property: Description("RenderTarget or Depth visualization.")] PreviewTarget Target,
    [property: Description("RTV index, 0 through 7 (0 for Depth).")] int RtvIndex = 0);

/// <summary>A saved preview image and the artifact that holds it.</summary>
public sealed record PreviewImageDto(string ArtifactRef, PreviewTarget Target, int? RtvIndex, uint Width, uint Height, int PngBytes);

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

    [McpServerTool(Name = "pix_gpu_preview", Title = "Render preview (pixtool)", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), Description("Spawns the installed pixtool to replay a copy of the capture and save render targets as PNG artifacts; returns a job. Stop all connected GPU analyses first. eventRef selects that exact event (its GPU id is pixtool's Global ID, checked against the capture on first use), markerName the last child of a unique exact marker with the resource bound, and neither the last event with it bound. targets saves up to eight RTV or depth images in one replay.")]
    public static Task<string> Preview(PixSession session, JobManager jobs,
        [Description("Open GPU capture handle.")] string handle,
        [Description("Globally unique, case-sensitive exact PIX marker name; omit for the last bound instance in the capture. Exclusive with eventRef.")] string? markerName = null,
        [Description("RenderTarget or Depth visualization (default RenderTarget); ignored when targets is set.")] PreviewTarget target = PreviewTarget.RenderTarget,
        [Description("RTV index, 0 through 7 (default 0); ignored when targets is set.")] int rtvIndex = 0,
        [Description("Process timeout in seconds, 1 through 3600. Default: 120.")] int timeoutSeconds = 120,
        [Description(Tools.WaitSecondsDescription)] double waitSeconds = 0,
        [Description("Exact event whose bound targets to save, with their contents after the event; it needs a GPU id, as draws, dispatches, clears and SetMarker labels have. Exclusive with markerName.")] EventRef? eventRef = null,
        [Description("One to eight images saved in one replay, each { target, rtvIndex } (default: the single target and rtvIndex). pixtool stops at the first image it cannot save.")] PreviewTargetSpec[]? targets = null,
        CancellationToken cancellationToken = default)
    {
        PreviewTargetSpec[] specs = targets is { Length: > 0 } ? targets : [new PreviewTargetSpec(target, rtvIndex)];
        ValidateSelection(markerName, eventRef, targets ?? specs, timeoutSeconds);
        session.Get<GpuCaptureHandle>(handle);
        if (eventRef is not null)
        {
            if (eventRef.Handle != handle) throw PixErrors.InvalidArguments("eventRef must belong to handle.");
            ReferenceValidation.Event(session, eventRef);
        }
        return Tools.RunJob(jobs, "pix_gpu_preview", () => jobs.StartForHandle<GpuCaptureHandle>("preview", $"Preview {handle}", handle, (job, capture) =>
        {
            uint? globalId = null;
            if (markerName is not null)
            {
                var markers = capture.Queues.SelectMany(q => capture.AllEvents(q.Index))
                    .Where(e => e.GpuId == uint.MaxValue || Tools.MatchesKind(e, "marker")).Select(e => e.Name);
                ValidateMarker(markers, markerName);
            }
            if (eventRef is not null)
            {
                if (capture.GlobalIdMapping is { State: GlobalIdProbe.Mismatch } known) throw GlobalIdProbe.MismatchError(known, MarkerFallback(capture, eventRef));
                EventRecord record = capture.AllEvents(eventRef.QueueIndex)[eventRef.EventIndex];
                if (record.GpuId == uint.MaxValue)
                    throw PixErrors.InvalidArguments($"Event {eventRef.EventIndex} ('{record.Name}') has no GPU id, so pixtool cannot select it; pass a draw, dispatch or clear inside it, or its markerName.",
                        [new ToolCallDto("pix_gpu_events", new { handle, queueIndex = eventRef.QueueIndex, parentIndex = eventRef.EventIndex }, CostHints.Query)]);
                globalId = record.GpuId;
            }

            using PixToolRun run = PixToolRun.Start(session, capture, job, "preview");
            bool probe = eventRef is not null && capture.GlobalIdMapping is null;
            string eventList = run.PathFor("events.csv");
            string[] outputs = specs.Select((_, i) => run.PathFor($"preview-{i}.png")).ToArray();
            var commands = new List<PixToolCommand>();
            // The event list goes first: pixtool stops at the first failing command.
            if (probe) commands.Add(PixToolCommand.SaveEventList(eventList));
            commands.AddRange(specs.Select((spec, i) => PixToolCommand.SaveResource(outputs[i], spec.Target == PreviewTarget.Depth, spec.RtvIndex, markerName, globalId)));
            GlobalIdMappingDto? mapping = capture.GlobalIdMapping;
            try
            {
                run.Execute(commands, TimeSpan.FromSeconds(timeoutSeconds));
            }
            finally
            {
                if (probe) mapping = GlobalIdProbe.Record(capture, eventList, job.AddMessage);
            }
            if (eventRef is not null && mapping is { State: GlobalIdProbe.Mismatch }) throw GlobalIdProbe.MismatchError(mapping, MarkerFallback(capture, eventRef));

            var images = new List<PreviewImageDto>();
            for (int i = 0; i < specs.Length; i++)
            {
                job.ThrowIfCancellationRequested();
                if (!File.Exists(outputs[i])) throw new PixToolException(PixErrors.Codes.PreviewMissingOutput, $"pixtool completed without producing image {i}.");
                if (new FileInfo(outputs[i]).Length > MaxArtifactBytes)
                    throw new PixToolException(PixErrors.Codes.PreviewTooLarge, $"PNG {i} exceeds the {MaxArtifactBytes} byte artifact limit.");
                byte[] png = File.ReadAllBytes(outputs[i]);
                (uint width, uint height) = PngDimensions(png);
                string artifactRef = Artifacts.GetOrCreateValue(session).Add(capture.Id, png);
                images.Add(new PreviewImageDto(artifactRef, specs[i].Target, specs[i].Target == PreviewTarget.RenderTarget ? specs[i].RtvIndex : null, width, height, png.Length));
            }
            PreviewImageDto first = images[0];
            return new
            {
                handle = capture.Id, artifactRef = first.ArtifactRef, mimeType = "image/png", width = first.Width, height = first.Height, pngBytes = first.PngBytes,
                target = first.Target, rtvIndex = first.RtvIndex, images,
                selection = new
                {
                    eventRef, globalId, markerName,
                    semantics = eventRef is not null ? "the exact event (pixtool --global-id): the selected resource's contents after it"
                        : markerName is null ? "last event with the selected resource bound" : "last child of the exact marker with the selected resource bound",
                    globalIdMapping = eventRef is null ? null : mapping?.State,
                    compared = eventRef is null ? null : mapping?.Compared,
                },
                warning = eventRef is not null && mapping is not { State: GlobalIdProbe.Verified }
                    ? $"pixtool Global IDs could not be verified against this capture ({mapping?.FirstMismatch ?? "no event list"}); the image may come from a different event."
                    : null,
                replay = new
                {
                    source = "pixtool", runtimeVersion = PixDiscovery.Version, pixBuild = PixDiscovery.AssemblyFileVersion, elapsedMs = (long)run.Elapsed.TotalMilliseconds,
                    settings = "CLI defaults, local replay; independent of native analysis settings",
                },
                inlineAvailable = true, originalInlineAvailable = first.PngBytes <= MaxInlineBytes,
                nextCalls = images.Select(image => new ToolCallDto("pix_gpu_preview_image", new { artifactRef = image.ArtifactRef }, CostHints.Cached)).ToArray(),
            };
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

    /// <summary>An artifact lives while its owning handle (a GPU capture, or the device handle for capture thumbnails) stays open.</summary>
    internal static byte[] GetArtifact(PixSession session, string artifactRef)
        => Artifacts.GetOrCreateValue(session).Get(artifactRef, id => session.TryGet<PixHandle>(id) is not null);

    internal static string StoreArtifact(PixSession session, string handle, byte[] png)
        => Artifacts.GetOrCreateValue(session).Add(handle, png);

    internal static void ForgetCapture(PixSession session, string handle)
    {
        if (Artifacts.TryGetValue(session, out PreviewArtifacts? artifacts)) artifacts.Forget(handle);
    }

    internal static void ValidateSelection(string? markerName, PreviewTarget target, int rtvIndex, int timeoutSeconds)
        => ValidateSelection(markerName, null, [new PreviewTargetSpec(target, rtvIndex)], timeoutSeconds);

    internal static void ValidateSelection(string? markerName, EventRef? eventRef, IReadOnlyList<PreviewTargetSpec> targets, int timeoutSeconds)
    {
        if (markerName is not null && eventRef is not null)
            throw PixErrors.InvalidArguments("Pass markerName or eventRef, not both.");
        if (markerName?.Any(c => c == '"' || char.IsControl(c)) == true)
            throw new PixToolException(PixErrors.Codes.UnsupportedSelection, "The pixtool parser cannot reliably represent marker names containing literal quotes or control characters. Omit markerName to inspect the last bound resource.");
        if (targets.Count is < 1 or > 8)
            throw PixErrors.InvalidArguments("targets must list 1 through 8 images.");
        if (targets.Any(t => t is null || !Enum.IsDefined(t.Target) || t.RtvIndex is < 0 or > 7 || t.Target == PreviewTarget.Depth && t.RtvIndex != 0)
            || timeoutSeconds is < 1 or > 3600 || markerName is not null && string.IsNullOrWhiteSpace(markerName))
            throw new PixToolException(PixErrors.Codes.InvalidArguments, "Use a valid target, RTV 0 through 7 (0 for depth), nonempty marker, and timeoutSeconds 1 through 3600.");
    }

    internal static void ValidateMarker(IEnumerable<string> markers, string markerName)
    {
        int matches = markers.Count(name => name.Equals(markerName, StringComparison.Ordinal));
        if (matches != 1) throw new PixToolException(PixErrors.Codes.AmbiguousMarker, $"markerName must identify exactly one PIX marker across all queues; found {matches} exact matches.");
    }

    /// <summary>The preview of the nearest marker above an event, for when exact-event selection is unavailable.</summary>
    private static ToolCallDto MarkerFallback(GpuCaptureHandle capture, EventRef eventRef)
    {
        EventRecord[] events = capture.AllEvents(eventRef.QueueIndex);
        uint parent = events[eventRef.EventIndex].ParentIndex;
        for (int steps = 0; parent < events.Length && steps < events.Length; steps++, parent = events[parent].ParentIndex)
        {
            if (events[parent].GpuId == uint.MaxValue)
                return new ToolCallDto("pix_gpu_preview", new { handle = capture.Id, markerName = events[parent].Name }, CostHints.PixTool);
        }
        return new ToolCallDto("pix_gpu_preview", new { handle = capture.Id }, CostHints.PixTool);
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
