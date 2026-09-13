using Microsoft.PIX.Extension;
using Microsoft.PIX.Extension.GpuCapture;
using Microsoft.PIX;

namespace PixMcp.Pix.Handles;

/// <summary>A feature's state (supported, unsupported, notCollected, unknown), why, and the registry notes that apply to this capture's vendor.</summary>
public sealed record CapabilityDto(string State, string? Reason = null, IReadOnlyList<string>? Notes = null);
/// <summary>
/// Where replayed numbers came from: the PIX build, the analysis adapter (id, name, vendor), the capture's own vendor and
/// whether they differ (a capture replayed on another vendor's GPU measures that GPU, not the original).
/// </summary>
public sealed record ReplayProvenance(string Source, string PixVersion, string? Adapter,
    uint? PowerState, string? Flags, string TimingSemantics,
    string? AdapterName = null, string? Vendor = null, string? CaptureVendor = null, bool VendorMismatch = false, string? PixBuild = null);
public sealed record OccupancyCache(IPixGpuCaptureOccupancyData Data,
    IPixGpuCaptureOccupancyType[] Types, IPixGpuCaptureOccupancyStage[] Stages);

public sealed partial class GpuCaptureHandle
{
    internal OccupancyCache? OccupancyData { get; set; }
    internal object? HighFrequencyCatalog { get; set; }
    internal Dictionary<string, object> OptionalUnavailable { get; } = new();
    private readonly Dictionary<string, CapabilityDto> _capabilities = new();
    private VendorIdentity? _captureVendor;

    /// <summary>
    /// The vendor of the GPU the capture was taken on: the capture file's vendor id or device name (source captureFileInfo),
    /// else the queue adapter names. Reads the document, so it runs on the worker; the result is cached for off-worker readers.
    /// </summary>
    public VendorIdentity CaptureVendor()
    {
        if (_captureVendor is not null) return _captureVendor;
        VendorIdentity identity = VendorIdentity.None;
        try
        {
            IPixGpuCaptureFileInfo info = PixApiExtensionsGpuCapture.GetFileInfo<IPixGpuCaptureFileInfo>(Document);
            string? vendorId = null, gpu = null;
            ulong pairs = info.GetNumFileInfoStringPairs();
            for (ulong i = 0; i < pairs; i++)
            {
                PixApiExtensionsGpuCapture.GetFileInfoStringPair(info, i, out string name, out string value);
                if (name == "GPU Vendor ID") vendorId = value;
                else if (name is "GPU" or "UniqueAdapterName" && gpu is null) gpu = value;
            }
            string? device = gpu ?? Interop.WOrNull(info.GetCaptureDevice());
            GpuVendor fromId = GpuVendors.FromVendorId(vendorId);
            GpuVendor fromName = GpuVendors.FromAdapterName(device);
            if (fromId != GpuVendor.Unknown) identity = new(fromId, device, "captureFileInfo", Note: fromName != GpuVendor.Unknown && fromName != fromId ? $"vendor id {vendorId} disagrees with the device name" : null);
            else if (fromName != GpuVendor.Unknown) identity = new(fromName, device, "captureFileInfo");
        }
        catch (Exception) { /* fall back to queue adapter names */ }
        if (identity.Vendor == GpuVendor.Unknown)
        {
            VendorIdentity fromQueues = GpuVendors.FromAdapterNames(Queues.Select(q => q.AdapterName), "captureQueueAdapter");
            if (fromQueues.Vendor != GpuVendor.Unknown || identity == VendorIdentity.None) identity = fromQueues;
        }
        return _captureVendor = identity;
    }

    /// <summary>The cached capture vendor, or a queue-name guess when the document has not been read yet (safe off the worker).</summary>
    public VendorIdentity CachedCaptureVendor => _captureVendor ?? GpuVendors.FromAdapterNames(Queues.Select(q => q.AdapterName), "captureQueueAdapter");

    /// <summary>
    /// The vendor of the analysis adapter: the selected adapter, else the first enumerated one (PIX's default is assumed to
    /// be the first; <c>assumedDefault</c> says so). None before the analysis session connected.
    /// </summary>
    public VendorIdentity ReplayVendor()
    {
        if (Adapters is not { Count: > 0 } adapters) return VendorIdentity.None;
        (ulong Id, string Name) chosen = SelectedAdapter is ulong id && adapters.FirstOrDefault(a => a.Id == id) is { Name: not null } match ? match : adapters[0];
        bool assumed = !SelectedAdapter.HasValue || !adapters.Any(a => a.Id == SelectedAdapter.Value);
        return new(GpuVendors.FromAdapterName(chosen.Name), chosen.Name, "replayAdapter", assumed, assumed ? "PIX chose the adapter; the first enumerated adapter is assumed" : null);
    }

    /// <summary>The vendor replayed numbers describe: the replay adapter when known, else the capture's vendor.</summary>
    public VendorIdentity EffectiveVendor()
    {
        VendorIdentity replay = ReplayVendor();
        return replay.Vendor != GpuVendor.Unknown ? replay : CachedCaptureVendor;
    }

    public EventDto DescribeEvent(int queueIndex, EventRecord record) => record.ToDto(queueIndex) with
    {
        EventRef = new EventRef(Id, queueIndex, record.Index),
        MarkerPath = EventNavigation.MarkerPath(AllEvents(queueIndex), record.Index),
    };

    public void MarkCapability(string feature, string state, string? reason = null)
        => _capabilities[feature] = new(state, reason);

    /// <summary>A capability the server never wires: its state comes from a type probe of the loaded PIX assembly, naming the DLL version.</summary>
    private static CapabilityDto ProbedCapability(string feature, string what)
    {
        string dll = PixApiSurface.LoadedFileVersion ?? PixDiscovery.AssemblyFileVersion ?? "(unknown version)";
        return PixApiSurface.Has(feature) switch
        {
            true => new("unknown", $"PixApiCsExt.experimental {dll} exposes {what}, which this server does not use yet."),
            false => new("unsupported", $"Not exposed by PixApiCsExt.experimental {dll} (probed by type name)."),
            null => new("unknown", "The PIX assembly could not be probed: " + PixApiSurface.LoadError),
        };
    }

    /// <summary>
    /// Probed states win; features nothing probed take the compatibility registry's state for this capture's vendor
    /// (else unknown), and every feature with registry notes carries them.
    /// </summary>
    public IReadOnlyDictionary<string, CapabilityDto> CapabilitiesSnapshot()
    {
        GpuVendor vendor = EffectiveVendor().Vendor;
        var result = new Dictionary<string, CapabilityDto>(_capabilities)
        {
            ["events"] = new("supported"),
            ["resourceMetadata"] = new("supported"),
            ["timing"] = new(Timing is null ? "notCollected" : "supported"),
            ["counters"] = new(CounterList is null ? "unknown" : "supported"),
            ["resourceContents"] = ProbedCapability("resourceContents", "a general resource readback interface"),
            ["pixelHistory"] = ProbedCapability("pixelHistory", "pixel history"),
            ["captureShaderStepping"] = ProbedCapability("captureShaderStepping", "capture shader stepping"),
            ["preview"] = new(File.Exists(System.IO.Path.Combine(PixDiscovery.InstallDir ?? "", "pixtool.exe"))
                ? "supported" : "unsupported", "RTV/depth through pixtool; native analyses must be stopped first."),
            ["exactEventPreview"] = new("unsupported", "Native event to pixtool Global ID mapping has not been verified."),
        };
        foreach (string feature in new[] { "occupancy", "highFrequencyCounters", "shaderProfiling", "accessedResources", "systemMonitorHardwareCounters", "staticShaderProfiling", "drPixVendorExperiments" })
        {
            if (result.ContainsKey(feature)) continue;
            string? registry = CompatibilityNotes.CapabilityState(feature, vendor, PixDiscovery.Version);
            result[feature] = new(registry ?? "unknown", registry is null ? null : "from the compatibility registry (not probed)");
        }
        foreach (string feature in result.Keys.ToArray())
        {
            IReadOnlyList<string> notes = CompatibilityNotes.Texts(feature, vendor, PixDiscovery.Version);
            if (notes.Count > 0 && result[feature].Notes is null) result[feature] = result[feature] with { Notes = notes };
        }
        return result;
    }

    public ReplayProvenance Provenance()
    {
        VendorIdentity replay = ReplayVendor(), capture = CachedCaptureVendor;
        bool mismatch = replay.Vendor != GpuVendor.Unknown && capture.Vendor != GpuVendor.Unknown && replay.Vendor != capture.Vendor;
        return new("gpuReplay", PixDiscovery.Version ?? "unknown",
            SelectedAdapter?.ToString(System.Globalization.CultureInfo.InvariantCulture), SelectedPowerState,
            SelectedFlags is null ? null : Json.EnumName(SelectedFlags.Value),
            "EOP duration is the interval between successive completion timestamps (includes idle before the event). Execution duration is TOP-to-EOP and overlaps neighbours. derivedSum values are serialized sums of child EOP durations; mixed values add measured spans to derived sums and can over- or understate; compare percentOfQueueSpan. Replay queue spans and nested event sums are not application frame latency."
                + (mismatch ? " The capture was taken on a different GPU vendor than the replay adapter; these numbers describe the replay GPU." : ""),
            replay.AdapterName, GpuVendors.Name(replay.Vendor), GpuVendors.Name(capture.Vendor), mismatch, PixDiscovery.AssemblyFileVersion);
    }
}
