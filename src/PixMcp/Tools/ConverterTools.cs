using System.ComponentModel;
using Microsoft.PIX;
using Microsoft.PIX.Extension;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using PixMcp.Pix;

namespace PixMcp.Tools;

[McpServerToolType]
public static class ConverterTools
{
    [McpServerTool(Name = "pix_capture_format", ReadOnly = true), Description("Detects the on-disk format of a GPU capture file: NO_FILE, INVALID_OR_CORRUPT, PRE2026 (needs upgrade) or 2026 (current).")]
    public static Task<string> Format(PixSession session, [Description("Path to a .wpix file.")] string path)
        => Tools.Run(session, "pix_capture_format", () =>
        {
            string full = Path.GetFullPath(path);
            IPixCaptureFileConverter converter = session.Factory.CreatePixCaptureFileConverter<IPixCaptureFileConverter>();
            PIX_GPU_CAPTURE_FILE_FORMAT format = _IPixCaptureFileConverter_Extensions.GetGpuCaptureFileFormat(converter, full);
            return new
            {
                path = full,
                format,
                isCurrent = format == PIX_GPU_CAPTURE_FILE_FORMAT.PIX_GPU_CAPTURE_FILE_FORMAT_CURRENT,
                needsUpgrade = format == PIX_GPU_CAPTURE_FILE_FORMAT.PIX_GPU_CAPTURE_FILE_FORMAT_PRE2026,
            };
        });

    [McpServerTool(Name = "pix_capture_upgrade"), Description("Upgrades an older (pre-2026) GPU capture file to the current format, writing a new file. Returns a job with progress.")]
    public static async Task<string> Upgrade(
        PixSession session,
        JobManager jobs,
        [Description("Path to the source .wpix file.")] string path,
        [Description("Output path (default: <name>_upgraded.wpix next to the source).")] string? outPath = null,
        [Description("Seconds to wait inline for completion (default 0).")] double waitSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string full = Tools.RequireFile(path, "Capture file");
            string target = string.IsNullOrWhiteSpace(outPath)
                ? Path.Combine(Path.GetDirectoryName(full) ?? ".", Path.GetFileNameWithoutExtension(full) + "_upgraded" + Path.GetExtension(full))
                : Path.GetFullPath(outPath);
            Job job = jobs.Start("upgrade", $"Upgrade {Path.GetFileName(full)}", j =>
            {
                IPixCaptureFileConverter converter = session.Factory.CreatePixCaptureFileConverter<IPixCaptureFileConverter>();
                PIX_GPU_CAPTURE_FILE_FORMAT source = _IPixCaptureFileConverter_Extensions.GetGpuCaptureFileFormat(converter, full);
                if (source == PIX_GPU_CAPTURE_FILE_FORMAT.PIX_GPU_CAPTURE_FILE_FORMAT_CURRENT)
                {
                    return new { path = full, format = source, upgraded = false, note = "Already in the current format." };
                }
                if (source != PIX_GPU_CAPTURE_FILE_FORMAT.PIX_GPU_CAPTURE_FILE_FORMAT_PRE2026)
                {
                    throw new McpException($"Capture format {Json.EnumName(source)} cannot be upgraded.");
                }
                _IPixCaptureFileConverter_Extensions.UpgradeGpuCaptureFile(converter, full, target, j.Sink);
                PIX_GPU_CAPTURE_FILE_FORMAT result = _IPixCaptureFileConverter_Extensions.GetGpuCaptureFileFormat(converter, target);
                return new { source = full, sourceFormat = source, path = target, format = result, upgraded = true };
            });
            return Json.Serialize(await jobs.WaitOrStatus(job, waitSeconds, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            throw PixErrors.ToMcp(ex, "pix_capture_upgrade");
        }
    }
}
