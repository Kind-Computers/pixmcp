using System.ComponentModel;
using Microsoft.PIX;
using Microsoft.PIX.Extension;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using PixMcp.Pix.Handles;

namespace PixMcp.Tools;

[McpServerToolType]
public static class SubcaptureTools
{
    [McpServerTool(Name = "pix_gpu_subcapture", Title = "Cut a subcapture (pixtool)", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description("Spawns the installed pixtool to write a smaller .wpix holding the GPU work of one scope (recapture-region over its first and last GPU ids) and, by default, opens it as a new GPU capture handle; returns a job. Stop all connected GPU analyses first. The subcapture's totals and rollups describe that region plus the state setup pixtool adds, not the original frame.")]
    public static Task<string> Subcapture(PixSession session, JobManager jobs,
        [Description("Open GPU capture handle to cut from.")] string handle,
        [Description("Event whose subtree to keep (an EventRef object). Exclusive with markerPathPrefix; one of the two is required.")] EventRef? scope = null,
        [Description("Marker path prefix that must match exactly one marker subtree, e.g. 'Frame/Shadow'. Exclusive with scope.")] string? markerPathPrefix = null,
        [Description("Output .wpix path (default: <capture>.sub-<first>-<last>.wpix beside the source capture).")] string? outPath = null,
        [Description("Replace an existing output file (default false).")] bool overwrite = false,
        [Description("Open the subcapture as a new GPU capture handle (default true).")] bool open = true,
        [Description("Process timeout in seconds, 1 through 3600 (default 600).")] int timeoutSeconds = 600,
        [Description(Tools.WaitSecondsDescription)] double waitSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        if ((scope is null) == (markerPathPrefix is null))
            throw PixErrors.InvalidArguments("Pass exactly one of scope or markerPathPrefix.",
                [new ToolCallDto("pix_gpu_events", new { handle, kind = "marker" }, CostHints.Query)]);
        if (timeoutSeconds is < 1 or > 3600) throw PixErrors.InvalidArguments("timeoutSeconds must be 1 through 3600.");
        ScopeSelection selection = EventScope.Resolve(session, handle, null, scope, markerPathPrefix);
        GpuCaptureHandle source = session.Get<GpuCaptureHandle>(handle);
        if (outPath is not null) ValidateOutputPath(outPath, source.Path, overwrite, session.Results);
        return Tools.RunJob(jobs, "pix_gpu_subcapture", () => jobs.StartForHandle<GpuCaptureHandle>("subcapture", $"Subcapture of {handle}", handle, (job, capture) =>
        {
            IndexRange range = EventScope.ToIndexRange(capture, selection);
            ScopeDescriptionDto described = selection.Describe(capture);
            if (markerPathPrefix is not null && described.MatchedRootCount != 1)
                throw PixErrors.InvalidArguments($"markerPathPrefix matched {described.MatchedRootCount} marker subtrees; a subcapture needs exactly one. Pass one of them as scope.",
                    described.MatchedRoots.Take(3).Select(root => new ToolCallDto("pix_gpu_subcapture", new { handle, scope = root }, CostHints.PixTool)).ToArray());
            if (capture.GlobalIdMapping is { State: GlobalIdProbe.Mismatch } known) throw GlobalIdProbe.MismatchError(known);
            string destination = ValidateOutputPath(outPath ?? DefaultPath(capture.Path, range.FirstGpuId, range.LastGpuId), capture.Path, overwrite, session.Results);

            using PixToolRun run = PixToolRun.Start(session, capture, job, "subcapture");
            bool probe = capture.GlobalIdMapping is null;
            string eventList = run.PathFor("events.csv"), written = run.PathFor("subcapture.wpix");
            var commands = new List<PixToolCommand>();
            // The event list goes first: pixtool stops at the first failing command.
            if (probe) commands.Add(PixToolCommand.SaveEventList(eventList));
            commands.Add(PixToolCommand.RecaptureRegion(written, range.FirstGpuId, range.LastGpuId));
            job.AddMessage($"Recapturing GPU ids {range.FirstGpuId} through {range.LastGpuId}...");
            GlobalIdMappingDto? mapping = capture.GlobalIdMapping;
            try
            {
                run.Execute(commands, TimeSpan.FromSeconds(timeoutSeconds));
            }
            finally
            {
                if (probe) mapping = GlobalIdProbe.Record(capture, eventList, job.AddMessage);
            }
            if (mapping is { State: GlobalIdProbe.Mismatch }) throw GlobalIdProbe.MismatchError(mapping);

            if (!File.Exists(written) || new FileInfo(written).Length == 0)
                throw new PixToolException(PixErrors.Codes.SubcaptureMissingOutput, "pixtool completed without writing the subcapture.");
            IPixCaptureFileConverter converter = session.Factory.CreatePixCaptureFileConverter<IPixCaptureFileConverter>();
            PIX_GPU_CAPTURE_FILE_FORMAT format = _IPixCaptureFileConverter_Extensions.GetGpuCaptureFileFormat(converter, written);
            if (format != PIX_GPU_CAPTURE_FILE_FORMAT.PIX_GPU_CAPTURE_FILE_FORMAT_CURRENT)
                throw new PixToolException(PixErrors.Codes.SubcaptureInvalidOutput, $"PIX reads the subcapture pixtool wrote as {format}, not a current-format capture.");
            job.ThrowIfCancellationRequested();
            ValidateOutputPath(destination, capture.Path, overwrite, session.Results);
            File.Move(written, destination, overwrite);

            var origin = new SubcaptureOriginDto(capture.Id, capture.Path, scope, markerPathPrefix, range.FirstGpuId, range.LastGpuId);
            object? opened = null;
            string? derivedHandle = null;
            if (open)
            {
                IPixGpuCaptureDocument document = session.Factory.OpenGpuCaptureDocument<IPixGpuCaptureDocument>(destination);
                GpuCaptureHandle derived = session.Register(new GpuCaptureHandle(destination, document) { DerivedFrom = origin });
                opened = derived.Summary();
                derivedHandle = derived.Id;
            }
            return new
            {
                handle = capture.Id,
                path = destination,
                bytes = new FileInfo(destination).Length,
                range = range.ToDto(capture.Id),
                scope = described,
                globalIdMapping = mapping,
                derivedFrom = origin,
                gpuCapture = opened,
                semantics = "The subcapture holds the GPU work between the scope's first and last GPU ids plus the state setup pixtool adds, and its Global IDs restart at 1. Its totals, rollups and frame reports describe that region, not the original frame.",
                replay = new { source = "pixtool", runtimeVersion = PixDiscovery.Version, elapsedMs = (long)run.Elapsed.TotalMilliseconds },
                nextCalls = derivedHandle is null
                    ? new[] { new ToolCallDto("pix_gpu_open", new { path = destination }, CostHints.Query) }
                    : new[]
                    {
                        new ToolCallDto("pix_gpu_overview", new { handle = derivedHandle }, CostHints.Replay),
                        new ToolCallDto("pix_gpu_counters_list", new { handle = derivedHandle }, CostHints.Replay),
                        new ToolCallDto("pix_gpu_preview", new { handle = derivedHandle }, CostHints.PixTool),
                    },
            };
        }), waitSeconds, cancellationToken);
    }

    /// <summary>A new .wpix that is not the source capture, with the shared output-path rules (existing parent, no private storage, overwrite).</summary>
    internal static string ValidateOutputPath(string outPath, string sourcePath, bool overwrite, ResultStore? results)
    {
        if (string.IsNullOrWhiteSpace(outPath) || outPath.Any(c => c == '"' || char.IsControl(c)))
            throw PixErrors.InvalidArguments("outPath must be a nonempty path without quotes or control characters.");
        if (!string.Equals(Path.GetExtension(outPath), ".wpix", StringComparison.OrdinalIgnoreCase))
            throw PixErrors.InvalidArguments("outPath must end in .wpix.");
        string full = ServerPaths.Full(outPath);
        if (string.Equals(full, ServerPaths.Full(sourcePath), StringComparison.OrdinalIgnoreCase))
            throw PixErrors.InvalidArguments("outPath must differ from the source capture.");
        return Tools.PrepareOutputPath(full, overwrite, results);
    }

    internal static string DefaultPath(string sourcePath, uint firstGpuId, uint lastGpuId)
    {
        string full = ServerPaths.Full(sourcePath);
        return Path.Combine(Path.GetDirectoryName(full) ?? ".", $"{Path.GetFileNameWithoutExtension(full)}.sub-{firstGpuId}-{lastGpuId}.wpix");
    }
}
