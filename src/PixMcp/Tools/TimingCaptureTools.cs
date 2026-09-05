using System.ComponentModel;
using Microsoft.PIX;
using Microsoft.PIX.Extension;
using Microsoft.PIX.Extension.TimingCapture;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using PixMcp.Pix.Handles;

namespace PixMcp.Tools;

[McpServerToolType]
public static class TimingCaptureTools
{
    [McpServerTool(Name = "pix_timing_open"), Description("Opens a PIX timing capture (.wpix) and returns a handle with the capture path and PixStorage path.")]
    public static Task<string> Open(PixSession session, [Description("Path to the timing capture .wpix file.")] string path)
        => Tools.Run(session, "pix_timing_open", () =>
        {
            string full = Tools.RequireFile(path, "Timing capture file");
            IPixTimingCaptureDocument document = session.Factory.OpenTimingCaptureDocument<IPixTimingCaptureDocument>(full);
            return session.Register(new TimingCaptureHandle(full, document)).Summary();
        });

    [McpServerTool(Name = "pix_timing_resolve_symbols"), Description("Resolves PDB symbols for a timing capture so CPU samples and callstacks show function names. Long-running; returns a job.")]
    public static async Task<string> ResolveSymbols(
        PixSession session,
        JobManager jobs,
        [Description("Timing capture handle")] string handle,
        [Description("Full PDB search path (directory or semicolon-separated list).")] string pdbSearchPath,
        [Description("Include kernel symbols (default false).")] bool includeKernelSymbols = false,
        [Description("Include source line data (default true).")] bool includeSourceData = true,
        [Description("Include type data (default false).")] bool includeTypeData = false,
        [Description("Also use _NT_SYMBOL_PATH (default true).")] bool useNtSymbolPath = true,
        [Description("Seconds to wait inline for completion (default 0).")] double waitSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            TimingCaptureHandle h = session.Get<TimingCaptureHandle>(handle);
            if (string.IsNullOrWhiteSpace(pdbSearchPath))
            {
                throw new McpException("pdbSearchPath is required.");
            }
            Job job = jobs.Start("symbols", $"Resolve symbols for {h.Id}", j =>
            {
                var settings = new TimingCaptureSymbolSettings
                {
                    IncludeKernelSymbols = includeKernelSymbols,
                    IncludeSourceData = includeSourceData,
                    IncludeTypeData = includeTypeData,
                    UseNTSymbolPath = useNtSymbolPath,
                };
                PixApiExtensionsTimingCapture.ResolveSymbols(h.Document, pdbSearchPath, settings, j.AddMessage, j.SetProgress);
                h.SymbolsResolved = true;
                return h.Summary();
            });
            return Json.Serialize(await jobs.WaitOrStatus(job, waitSeconds, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            throw PixErrors.ToMcp(ex, "pix_timing_resolve_symbols");
        }
    }

    [McpServerTool(Name = "pix_timing_save"), Description("Saves the timing capture (e.g. after resolving symbols), optionally to a new path.")]
    public static Task<string> Save(
        PixSession session,
        [Description("Timing capture handle")] string handle,
        [Description("New path to save as; omit to save in place.")] string? asPath = null)
        => Tools.Run(session, "pix_timing_save", () =>
        {
            TimingCaptureHandle h = session.Get<TimingCaptureHandle>(handle);
            if (string.IsNullOrWhiteSpace(asPath))
            {
                h.Document.Save();
                return new { saved = h.CapturePath };
            }
            string full = Path.GetFullPath(asPath);
            _IPixTimingCaptureDocument_Extensions.SaveAs(h.Document, full);
            return new { saved = full };
        });
}
