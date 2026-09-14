using Microsoft.PIX;

namespace PixMcp.Pix;

/// <summary>
/// Replay adapter choice by name, and PIX's refusal to replay a capture on an adapter. Adapter ids derive from the adapter
/// LUID and change between boots, so callers name the adapter instead ("Arc B580", "intel").
/// </summary>
internal static class AdapterSelection
{
    /// <summary>
    /// The id of the one adapter <paramref name="adapterName"/> identifies: an exact name (case-insensitive), else the only name
    /// containing it, else the only adapter of the vendor it names. Anything else is invalid_arguments listing the adapters.
    /// </summary>
    public static ulong Resolve(IReadOnlyList<(ulong Id, string Name)> adapters, string adapterName, string handle)
    {
        string wanted = adapterName.Trim();
        ToolCallDto listing = new("pix_gpu_analysis_adapters", new { handle }, CostHints.Query);
        if (wanted.Length == 0)
            throw PixErrors.InvalidArguments("adapterName is empty; name an adapter from pix_gpu_analysis_adapters or a vendor (intel, amd, nvidia, warp).", [listing]);
        (ulong Id, string Name)[] matches = adapters.Where(a => string.Equals(a.Name.Trim(), wanted, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) matches = adapters.Where(a => a.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0 && GpuVendors.FromAdapterName(wanted) is var vendor && vendor != GpuVendor.Unknown)
            matches = adapters.Where(a => GpuVendors.FromAdapterName(a.Name) == vendor).ToArray();
        if (matches.Length == 1) return matches[0].Id;
        string known = adapters.Count == 0 ? "no adapters were enumerated" : "adapters: " + string.Join("; ", adapters.Select(a => $"{a.Name} (id {a.Id})"));
        string problem = matches.Length == 0
            ? $"No analysis adapter matches adapterName '{wanted}'"
            : $"adapterName '{wanted}' matches {matches.Length} adapters; use a longer name or adapterId";
        throw PixErrors.InvalidArguments($"{problem}; {known}.", [listing]);
    }

    /// <summary>
    /// PIX declining to replay a capture on the chosen adapter (0x8ABC006B, observed for an NVIDIA capture started on an Intel Arc
    /// without IGNORE_INCOMPATIBILITIES) as analysis_incompatible with a retry that adds the flag; null for any other failure or
    /// when the flag was already set. <paramref name="adapter"/> is the adapter passed to PIX (null when PIX chose),
    /// <paramref name="powerState"/> and <paramref name="flags"/> what the caller selected.
    /// </summary>
    public static PixToolException? IncompatibleStart(Exception ex, string handle, ulong? adapter, uint? powerState, PIX_ANALYSIS_FLAGS? flags,
        IReadOnlyList<(ulong Id, string Name)>? adapters, VendorIdentity capture)
    {
        if (PixErrors.HResultOf(ex) != PixErrors.E_PIX_ANALYSIS_INCOMPATIBLE) return null;
        if (flags is PIX_ANALYSIS_FLAGS selected && (selected & PIX_ANALYSIS_FLAGS.PIX_ANALYSIS_FLAG_IGNORE_INCOMPATIBILITIES) != 0) return null;
        string? adapterName = adapter is ulong id ? adapters?.FirstOrDefault(a => a.Id == id).Name : null;
        GpuVendor replayVendor = GpuVendors.FromAdapterName(adapterName);
        bool crossVendor = replayVendor != GpuVendor.Unknown && capture.Vendor != GpuVendor.Unknown && replayVendor != capture.Vendor;
        string target = adapterName is null ? (adapter is null ? "the adapter PIX chose" : $"adapter {adapter}") : $"{adapterName} ({GpuVendors.Name(replayVendor)})";
        string origin = capture.Vendor == GpuVendor.Unknown ? "" : $"; the capture was taken on {capture.AdapterName ?? GpuVendors.Name(capture.Vendor)} ({GpuVendors.Name(capture.Vendor)})";
        string[] retryFlags = (AnalysisFlags.Decode(flags)?.Names ?? []).Where(n => n != "NONE").Append("IGNORE_INCOMPATIBILITIES").ToArray();
        string message = $"PIX declined to replay this capture on {target}{origin} ({PixErrors.Hex(PixErrors.E_PIX_ANALYSIS_INCOMPATIBLE)}). "
            + (crossVendor
                ? "Replaying a capture on another vendor's GPU needs IGNORE_INCOMPATIBILITIES; the replayed numbers then describe that GPU, not the capture's."
                : "PIX treats the capture as incompatible with this adapter; IGNORE_INCOMPATIBILITIES replays it anyway.")
            + (adapter is null ? " The retry names no adapter, so the first enumerated adapter is used." : "");
        return new PixToolException(PixErrors.Codes.AnalysisIncompatible, message, false,
            [new ToolCallDto("pix_gpu_analysis_start", new { handle, adapterId = adapter, powerStateId = powerState, flags = retryFlags }, CostHints.Job),
             new ToolCallDto("pix_gpu_analysis_adapters", new { handle }, CostHints.Query)],
            PixErrors.E_PIX_ANALYSIS_INCOMPATIBLE);
    }
}
