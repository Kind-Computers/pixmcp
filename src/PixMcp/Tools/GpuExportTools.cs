using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using PixMcp.Pix.Handles;

namespace PixMcp.Tools;

[McpServerToolType]
public static class GpuExportTools
{
    [McpServerTool(Name = "pix_gpu_export_cpp"), Description("Exports a GPU capture to a standalone C++/CMake project using installed pixtool. Returns a job. outputDirectory must not exist and its parent must exist; files are never overwritten. Stop connected GPU analyses first. Partial output is retained on failure or cancellation and its path is reported in job diagnostics. Does not build or execute the exported project.")]
    public static Task<string> Export(PixSession session, JobManager jobs,
        [Description("Open GPU capture handle.")] string handle,
        [Description("New output directory under an existing parent. Existing directories, even empty ones, are refused.")] string outputDirectory,
        [Description("Include WinPixEventRuntime through NuGet. Setting true acknowledges and accepts the WinPixEventRuntime package license. Default false.")] bool useWinPixEventRuntime = false,
        [Description("Include the DirectX 12 Agility SDK through NuGet. Setting true acknowledges and accepts the Microsoft.Direct3D.D3D12 package license. Default false.")] bool useAgilitySdk = false,
        [Description("Use replay-time ExecuteIndirect argument buffers to keep nondeterministically generated arguments synchronized with other buffers. Default false uses capture-time copies.")] bool useReplayTimeExecuteIndirectBuffers = false,
        [Description("Process timeout in seconds, 1 through 3600; default 1800.")] int timeoutSeconds = 1800,
        [Description(Tools.WaitSecondsDescription)] double waitSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        session.Get<GpuCaptureHandle>(handle);
        if (timeoutSeconds is < 1 or > 3600)
            throw new PixToolException("invalid_arguments", "timeoutSeconds must be between 1 and 3600.");
        string output = ValidateOutputDirectory(outputDirectory);
        var options = new GpuExportOptionsDto(useWinPixEventRuntime, useAgilitySdk, useReplayTimeExecuteIndirectBuffers);
        return Tools.RunJob(jobs, "pix_gpu_export_cpp", () => jobs.StartForHandle<GpuCaptureHandle>("export-cpp",
            $"Export {handle} to C++", handle, (job, capture) =>
        {
            PixToolProcess.EnsureReplayAvailable(session);
            string executable = PixToolProcess.Executable("export", PixDiscovery.InstallDir);
            ValidateOutputDirectory(output);
            job.ThrowIfCancellationRequested();
            string folder = Path.Combine(Path.GetTempPath(), "pixmcp-export-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string replayCapture = Path.Combine(folder, "capture" + Path.GetExtension(capture.Path));
            try
            {
                PixToolProcess.CopyCapture(capture.Path, replayCapture, job.ThrowIfCancellationRequested);
                var start = BuildStartInfo(executable, replayCapture, output, options);
                PixToolProcess.PrepareArguments(start);
                job.AddMessage("Exporting with pixtool defaults; native analysis adapter and power settings do not apply.");
                string cmake = ExportToDirectory(output,
                    () => PixToolProcess.Run(start, TimeSpan.FromSeconds(timeoutSeconds), job.Cancellation.Token, job.AddMessage, "export"),
                    job.ThrowIfCancellationRequested, job.AddMessage);
                return new GpuExportResultDto(capture.Id, capture.Path, output, cmake, "pixtool", PixDiscovery.Version, options);
            }
            finally
            {
                try
                {
                    if (File.Exists(replayCapture)) File.Delete(replayCapture);
                    if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
                }
                catch (IOException ex) { job.AddMessage("Temporary export capture cleanup failed: " + ex.Message); }
                catch (UnauthorizedAccessException ex) { job.AddMessage("Temporary export capture cleanup failed: " + ex.Message); }
            }
        }), waitSeconds, cancellationToken);
    }

    internal static string ValidateOutputDirectory(string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory) || outputDirectory.Any(c => c == '"' || char.IsControl(c)))
            throw new PixToolException("invalid_arguments", "outputDirectory must be a valid nonempty directory path without quotes or control characters.");
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new PixToolException("invalid_arguments", "Invalid outputDirectory: " + ex.Message); }
        string? parent = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent) || Path.GetFileName(full).IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new PixToolException("invalid_arguments", "outputDirectory must name a new directory under an existing parent.");
        if (Path.Exists(full))
            throw new PixToolException("output_exists", $"The output path already exists: {full}. Choose a new directory; export never overwrites files.");
        return full;
    }

    internal static ProcessStartInfo BuildStartInfo(string executable, string capture, string output, GpuExportOptionsDto options)
    {
        var start = PixToolProcess.StartInfo(executable, ["--output=quiet", "--log=off", "open-capture", capture, "export-to-cpp", output]);
        if (options.UseWinPixEventRuntime) start.ArgumentList.Add("--use-winpixeventruntime");
        if (options.UseAgilitySdk) start.ArgumentList.Add("--use-agilitySdk");
        if (options.UseReplayTimeExecuteIndirectBuffers) start.ArgumentList.Add("--use-replay-time-executeindirect-buffers");
        return start;
    }

    internal static string ExportToDirectory(string outputDirectory, Action run, Action checkCancellation, Action<string> diagnostic)
    {
        string output = ValidateOutputDirectory(outputDirectory);
        checkCancellation();
        try
        {
            run();
            checkCancellation();
            string cmake = Path.Combine(output, "CMakeLists.txt");
            if (!File.Exists(cmake))
                throw new PixToolException("export_missing_output", "pixtool completed without producing CMakeLists.txt.");
            return cmake;
        }
        catch
        {
            if (Path.Exists(output)) diagnostic("Partial C++ export retained at: " + output);
            throw;
        }
    }
}
