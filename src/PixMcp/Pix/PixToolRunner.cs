using System.Diagnostics;
using System.Globalization;
using PixMcp.Pix.Handles;

namespace PixMcp.Pix;

/// <summary>One pixtool command after open-capture: its name and arguments (options as --name=value).</summary>
internal sealed record PixToolCommand(string Name, IReadOnlyList<string> Arguments)
{
    /// <summary>save-resource: an RTV slot or the depth visualization, at the exact event (Global ID), a marker's last child, or the last bound event.</summary>
    public static PixToolCommand SaveResource(string output, bool depth, int rtvIndex, string? markerName = null, uint? globalId = null)
    {
        var arguments = new List<string> { output, depth ? "--depth" : "--rtv=" + rtvIndex.ToString(CultureInfo.InvariantCulture) };
        if (globalId is uint id) arguments.Add("--global-id=" + id.ToString(CultureInfo.InvariantCulture));
        else if (markerName is not null) arguments.Add("--marker=" + markerName);
        return new("save-resource", arguments);
    }

    /// <summary>save-event-list: the first queue's events with their Global IDs, as CSV.</summary>
    public static PixToolCommand SaveEventList(string output) => new("save-event-list", [output]);

    /// <summary>recapture-region: a new capture holding the GPU work between two Global IDs (both inclusive).</summary>
    public static PixToolCommand RecaptureRegion(string output, uint firstGlobalId, uint lastGlobalId)
        => new("recapture-region", [output, "--start=" + firstGlobalId.ToString(CultureInfo.InvariantCulture), "--end=" + lastGlobalId.ToString(CultureInfo.InvariantCulture)]);
}

/// <summary>Where a capture written by pix_gpu_subcapture came from.</summary>
public sealed record SubcaptureOriginDto(string SourceHandle, string SourcePath, EventRef? Scope, string? MarkerPathPrefix, uint FirstGpuId, uint LastGpuId);

internal static class PixToolRunner
{
    /// <summary>One open-capture followed by every command, so a single pixtool process serves the whole request.</summary>
    public static IReadOnlyList<string> Arguments(string capture, IEnumerable<PixToolCommand> commands)
    {
        var arguments = new List<string> { "--output=quiet", "--log=off", "open-capture", capture };
        foreach (PixToolCommand command in commands)
        {
            arguments.Add(command.Name);
            arguments.AddRange(command.Arguments);
        }
        return arguments;
    }

    public static ProcessStartInfo StartInfo(string executable, string capture, IEnumerable<PixToolCommand> commands)
        => PixToolProcess.StartInfo(executable, Arguments(capture, commands));
}

/// <summary>
/// A pixtool replay of a private copy of a GPU capture, run on the PIX worker inside a job. The copy and every output live in a temporary
/// folder that Dispose removes, so callers read or move their outputs first. pixtool stops at the first failing command.
/// </summary>
internal sealed class PixToolRun : IDisposable
{
    private readonly string _executable;
    private readonly Job _job;

    private PixToolRun(string executable, string folder, string capturePath, string operation, Job job)
    {
        _executable = executable;
        Folder = folder;
        CapturePath = capturePath;
        Operation = operation;
        _job = job;
    }

    public string Folder { get; }
    public string CapturePath { get; }
    public string Operation { get; }
    public TimeSpan Elapsed { get; private set; }

    /// <summary>Checks that no analysis holds the GPU and pixtool exists, then copies the capture (the open document keeps the original locked).</summary>
    public static PixToolRun Start(PixSession session, GpuCaptureHandle capture, Job job, string operation)
    {
        PixToolProcess.EnsureReplayAvailable(session);
        string executable = PixToolProcess.Executable(operation, PixDiscovery.InstallDir);
        job.ThrowIfCancellationRequested();
        string folder = Path.Combine(Path.GetTempPath(), $"pixmcp-{operation}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var run = new PixToolRun(executable, folder, Path.Combine(folder, "capture" + Path.GetExtension(capture.Path)), operation, job);
        try
        {
            PixToolProcess.CopyCapture(capture.Path, run.CapturePath, job.ThrowIfCancellationRequested);
        }
        catch
        {
            run.Dispose();
            throw;
        }
        job.AddMessage("Replaying with pixtool defaults; native analysis adapter and power settings do not apply.");
        return run;
    }

    public string PathFor(string fileName) => Path.Combine(Folder, fileName);

    /// <summary>Runs every command in one pixtool process with a deadline; stdout and stderr go to the job diagnostics, bounded.</summary>
    public void Execute(IReadOnlyList<PixToolCommand> commands, TimeSpan timeout)
    {
        ProcessStartInfo start = PixToolRunner.StartInfo(_executable, CapturePath, commands);
        PixToolProcess.PrepareArguments(start);
        var clock = Stopwatch.StartNew();
        try
        {
            PixToolProcess.Run(start, timeout, _job.Cancellation.Token, _job.AddMessage, Operation);
        }
        finally
        {
            Elapsed = clock.Elapsed;
        }
        _job.ThrowIfCancellationRequested();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _job.AddMessage($"Temporary {Operation} cleanup failed: {ex.Message}");
        }
    }
}
