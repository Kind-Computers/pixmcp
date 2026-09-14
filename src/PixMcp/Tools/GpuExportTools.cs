using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using PixMcp.Pix.Handles;

namespace PixMcp.Tools;

[McpServerToolType]
public static class GpuExportTools
{
    [McpServerTool(Name = "pix_gpu_export_cpp", Title = "Export capture to C++", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description("Exports a GPU capture to a standalone C++/CMake project using installed pixtool. Returns a job. outputDirectory must not exist and its parent must exist; files are never overwritten. Stop connected GPU analyses first. Partial output is retained on failure or cancellation and its path is reported in job diagnostics. Does not build or execute the exported project.")]
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
            throw new PixToolException(PixErrors.Codes.InvalidArguments, "timeoutSeconds must be between 1 and 3600.");
        string output = ValidateOutputDirectory(outputDirectory);
        var options = new GpuExportOptionsDto(useWinPixEventRuntime, useAgilitySdk, useReplayTimeExecuteIndirectBuffers);
        return Tools.RunJob(jobs, "pix_gpu_export_cpp", () => jobs.StartForHandle<GpuCaptureHandle>("export-cpp",
            $"Export {handle} to C++", handle, (job, capture) =>
        {
            ValidateOutputDirectory(output);
            using PixToolRun run = PixToolRun.Start(session, capture, job, "export");
            string cmake = ExportToDirectory(output,
                () => run.Execute([ExportCommand(output, options)], TimeSpan.FromSeconds(timeoutSeconds)),
                job.ThrowIfCancellationRequested, job.AddMessage);
            return new GpuExportResultDto(capture.Id, capture.Path, output, cmake, "pixtool", PixDiscovery.Version, options);
        }), waitSeconds, cancellationToken);
    }

    internal static string ValidateOutputDirectory(string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory) || outputDirectory.Any(c => c == '"' || char.IsControl(c)))
            throw new PixToolException(PixErrors.Codes.InvalidArguments, "outputDirectory must be a valid nonempty directory path without quotes or control characters.");
        string full;
        try { full = Path.TrimEndingDirectorySeparator(ServerPaths.Full(outputDirectory)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new PixToolException(PixErrors.Codes.InvalidArguments, "Invalid outputDirectory: " + ex.Message); }
        string? parent = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent) || Path.GetFileName(full).IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new PixToolException(PixErrors.Codes.InvalidArguments, "outputDirectory must name a new directory under an existing parent.");
        if (Path.Exists(full))
            throw new PixToolException(PixErrors.Codes.OutputExists, $"The output path already exists: {full}. Choose a new directory; export never overwrites files.");
        return full;
    }

    internal static ProcessStartInfo BuildStartInfo(string executable, string capture, string output, GpuExportOptionsDto options)
        => PixToolRunner.StartInfo(executable, capture, [ExportCommand(output, options)]);

    internal static PixToolCommand ExportCommand(string output, GpuExportOptionsDto options)
    {
        var arguments = new List<string> { output };
        if (options.UseWinPixEventRuntime) arguments.Add("--use-winpixeventruntime");
        if (options.UseAgilitySdk) arguments.Add("--use-agilitySdk");
        if (options.UseReplayTimeExecuteIndirectBuffers) arguments.Add("--use-replay-time-executeindirect-buffers");
        return new("export-to-cpp", arguments);
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
                throw new PixToolException(PixErrors.Codes.ExportMissingOutput, "pixtool completed without producing CMakeLists.txt.");
            return cmake;
        }
        catch
        {
            if (Path.Exists(output)) diagnostic("Partial C++ export retained at: " + output);
            throw;
        }
    }
}
