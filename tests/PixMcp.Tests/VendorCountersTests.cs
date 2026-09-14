using System.Text.Json;
using PixMcp.Pix;
using PixMcp.Pix.Handles;
using Xunit;

namespace PixMcp.Tests;

/// <summary>Real pix_gpu_counters_list dumps: nvidia (RTX 4070 Ti), intel-arc-b580 and amd-radeon-igpu, all PIX 2606.18-preview.</summary>
internal static class CounterCatalogs
{
    public static (string AdapterName, string VendorId, CounterInfo[] Counters) Load(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "counter-catalogs", name + ".json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;
        CounterInfo[] counters = root.GetProperty("counters").EnumerateArray().Select(c => new CounterInfo(c.GetProperty("id").GetUInt32(),
            c.GetProperty("name").GetString()!, c.GetProperty("description").GetString()!, c.GetProperty("dataType").GetString()!,
            c.GetProperty("groups").EnumerateArray().Select(g => g.GetString()!).ToArray())).ToArray();
        return (root.GetProperty("adapterName").GetString()!, root.GetProperty("vendorId").GetString()!, counters);
    }
}

public sealed class GpuVendorTests
{
    [Theory]
    [InlineData("NVIDIA GeForce RTX 4070 Ti", GpuVendor.Nvidia)]
    [InlineData("AMD Radeon RX 7900 XTX", GpuVendor.Amd)]
    [InlineData("AMD Radeon(TM) Graphics", GpuVendor.Amd)]
    [InlineData("Intel(R) Arc(TM) B580 Graphics", GpuVendor.Intel)]
    [InlineData("Intel(R) Iris(R) Xe Graphics", GpuVendor.Intel)]
    [InlineData("Microsoft Basic Render Driver", GpuVendor.Warp)]
    [InlineData("Qualcomm(R) Adreno(TM) X1-85 GPU", GpuVendor.Qualcomm)]
    [InlineData("Graphics Queue 0 (Main Graphics Queue)", GpuVendor.Unknown)]
    [InlineData("", GpuVendor.Unknown)]
    [InlineData(null, GpuVendor.Unknown)]
    public void AdapterNamesMapToVendors(string? name, GpuVendor expected) => Assert.Equal(expected, GpuVendors.FromAdapterName(name));

    [Theory]
    [InlineData("10DE", GpuVendor.Nvidia)]
    [InlineData("0x1002", GpuVendor.Amd)]
    [InlineData("8086", GpuVendor.Intel)]
    [InlineData("1414", GpuVendor.Warp)]
    [InlineData("5143", GpuVendor.Qualcomm)]
    [InlineData("zzzz", GpuVendor.Unknown)]
    public void VendorIdsMapToVendors(string id, GpuVendor expected) => Assert.Equal(expected, GpuVendors.FromVendorId(id));

    [Fact]
    public void QueueAdapterNamesAgreeOrDisagree()
    {
        VendorIdentity one = GpuVendors.FromAdapterNames(["NVIDIA GeForce RTX 4070 Ti", "NVIDIA GeForce RTX 4070 Ti"], "captureQueueAdapter");
        Assert.Equal(GpuVendor.Nvidia, one.Vendor);
        Assert.Equal("captureQueueAdapter", one.Source);
        Assert.Null(one.Note);
        VendorIdentity split = GpuVendors.FromAdapterNames(["NVIDIA GeForce RTX 4070 Ti", "Intel(R) Arc(TM) B580 Graphics"], "captureQueueAdapter");
        Assert.Equal(GpuVendor.Unknown, split.Vendor);
        Assert.Contains("disagree", split.Note);
        Assert.Equal(GpuVendor.Unknown, GpuVendors.FromAdapterNames(["Graphics Queue 0"], "x").Vendor);
        Assert.Equal(VendorIdentity.None, GpuVendors.FromAdapterNames([null, ""], "x"));
        Assert.Equal("nvidia", GpuVendors.Name(GpuVendor.Nvidia));
    }
}

public sealed class CounterUnitsTests
{
    [Theory]
    [InlineData("XVE Utilization (%)", "", "FLOAT32", "percent", "name", "high")]
    [InlineData("Xe GPU Utilization", "", "FLOAT32", "percent", "name", "medium")]
    [InlineData("Thread Dispatcher Starved", "", "FLOAT32", "percent", "name", "medium")]
    [InlineData("XVE_THREADS_OCCUPANCY_ALL", "", "FLOAT32", "percent", "name", "medium")]
    [InlineData("XVE_STALL_SBID", "", "FLOAT32", "percent", "name", "medium")]
    [InlineData("Something", "Percentage of time the unit was busy.", "FLOAT32", "percent", "description", "high")]
    [InlineData("XVE Stall", "INTEL: Percentage of time in which any thread loaded but not even a single pipe is active in XVE", "DEFAULT", "percent", "description", "high")]
    [InlineData("GPU Memory Byte Read Rate", "INTEL: Device local memory (HBM, GDDR, LPDDR, etc.) read bandwidth", "DEFAULT", "bytesPerSecond", "description", "medium")]
    [InlineData("XVE Inst Executed ALU0 PS", "INTEL: Number of execution slots taken by instructions executed by PS threads on ALU0 pipe", "DEFAULT", "count", "name", "medium")]
    [InlineData("L3 Miss", "INTEL: Number of Device Cache accesses which miss in the Device Cache cache", "DEFAULT", "count", "description", "medium")]
    [InlineData("L2 Read Bytes", "", "UINT64", "bytes", "name", "medium")]
    [InlineData("DRAM Throughput", "Bytes per second read from DRAM", "FLOAT32", "bytesPerSecond", "description", "medium")]
    [InlineData("SM Active Cycles", "", "UINT64", "cycles", "name", "medium")]
    [InlineData("Bytes per cycle", "", "FLOAT32", "ratio", "description", "low")]
    [InlineData("PS Invocations", "Number of times the Pixel Shader was executed", "DEFAULT", "count", "name", "medium")]
    [InlineData("XVE_LOAD_STORE_CACHE_READ_MESSAGE_COUNT", "", "UINT64", "count", "name", "medium")]
    [InlineData("Samples Rejected", "Difference between the number of resolved samples", "DEFAULT", "count", "name", "medium")]
    [InlineData("Enabled", "", "BOOL", "boolean", "format", "high")]
    [InlineData("Flags", "", "HEX32", "bitmask", "format", "high")]
    [InlineData("Mystery", "", "FLOAT32", "unknown", "none", "none")]
    public void UnitsAreInferredFromFormatNameThenDescription(string name, string description, string dataType, string unit, string source, string confidence)
    {
        UnitGuess guess = CounterUnits.Infer(name, description, dataType);
        Assert.Equal(unit, guess.Unit);
        Assert.Equal(source, guess.UnitSource);
        Assert.Equal(confidence, guess.UnitConfidence);
        if (unit == "percent") { Assert.Equal(0, guess.RangeMin); Assert.Equal(100, guess.RangeMax); Assert.Equal("avg", guess.AggregationHint); }
        if (unit is "count" or "bytes" or "cycles") Assert.Equal("sum", guess.AggregationHint);
    }

    [Fact]
    public void RealCatalogsGetUsableUnits()
    {
        (_, _, CounterInfo[] nvidia) = CounterCatalogs.Load("nvidia");
        Assert.Equal(22, nvidia.Length);
        Assert.All(nvidia, c => Assert.Equal("count", c.Unit.Unit));
        (_, _, CounterInfo[] amd) = CounterCatalogs.Load("amd-radeon-igpu");
        Assert.Equal(nvidia.Select(c => c.Name).Order(), amd.Select(c => c.Name).Order());

        (_, string vendorId, CounterInfo[] intel) = CounterCatalogs.Load("intel-arc-b580");
        Assert.Equal(GpuVendor.Intel, GpuVendors.FromVendorId(vendorId));
        Assert.Equal(281, intel.Length);
        Assert.Equal(259, intel.Count(c => c.Groups.All(g => g.StartsWith("INTEL: ", StringComparison.Ordinal))));
        CounterInfo[] percentages = intel.Where(c => c.Description.StartsWith("INTEL: Percentage of", StringComparison.Ordinal)).ToArray();
        Assert.True(percentages.Length > 70, $"{percentages.Length} Intel counters are percentages of time");
        Assert.All(percentages, c => Assert.Equal(("percent", "high"), (c.Unit.Unit, c.Unit.UnitConfidence)));
        Assert.All(intel.Where(c => c.Description.StartsWith("INTEL: Number of", StringComparison.Ordinal)), c => Assert.NotEqual("percent", c.Unit.Unit));
        Assert.Equal("bytesPerSecond", intel.Single(c => c.Name == "GPU Memory Byte Write Rate").Unit.Unit);
        Assert.Equal("count", intel.Single(c => c.Name == "XVE Inst Executed ALU0 PS").Unit.Unit);
    }
}

public sealed class CounterPresetsTests
{
    private static readonly string[] VendorPresets = ["utilization", "aluUtilization", "perStageAlu", "occupancy", "stalls", "cache", "memoryBandwidth", "fixedFunction"];

    [Fact]
    public void PresetsLoadAndEveryVendorBlockParses()
    {
        Assert.Contains("utilization", CounterPresets.Names);
        Assert.Contains("pipelineStatistics", CounterPresets.Names);
        Assert.Equal(CounterPresets.Names.Count, CounterPresets.Names.Distinct().Count());
        Assert.All(CounterPresets.Names, name => Assert.False(string.IsNullOrWhiteSpace(CounterPresets.Description(name))));
        Assert.All(CounterPresets.Names, name => Assert.Contains(name, CounterPresets.NamesDescription));
    }

    [Fact]
    public void EveryIntelPatternMatchesTheArcB580Catalog()
    {
        (_, _, CounterInfo[] intel) = CounterCatalogs.Load("intel-arc-b580");
        Assert.All(VendorPresets, preset =>
        {
            PresetResolution resolution = CounterPresets.Resolve(GpuVendor.Intel, preset, intel, cap: 1000)!;
            Assert.Equal("verified", resolution.Confidence);
            Assert.Empty(resolution.UnmatchedPatterns);
            Assert.All(resolution.Matches, m => Assert.StartsWith("INTEL: ", intel.Single(c => c.Id == m.Id).Groups[0]));
        });
    }

    [Fact]
    public void IntelPresetsResolveInPatternOrderAndCap()
    {
        (_, _, CounterInfo[] intel) = CounterCatalogs.Load("intel-arc-b580");
        PresetResolution utilization = CounterPresets.Resolve(GpuVendor.Intel, "utilization", intel)!;
        Assert.Equal("intel", utilization.Vendor);
        Assert.Equal(["GPU Busy", "Command Parser Render Engine Busy", "Command Parser Compute Engine Busy", "Command Parser Copy Engine Busy", "XVE Active",
            "XVE Multiple Pipe Active", "Sampler Active", "GPU Memory Active", "L3 Busy"], utilization.Matches.Select(m => m.Name));
        Assert.False(utilization.Capped);

        PresetResolution perStage = CounterPresets.Resolve(GpuVendor.Intel, "perStageAlu", intel)!;
        Assert.True(perStage.Capped);
        Assert.Equal(16, perStage.Matches.Count);
        Assert.Equal(2, perStage.OmittedIds.Count);
        Assert.Equal(["XVE Inst Executed ALU0 VS Utilization", "XVE Inst Executed ALU0 PS Utilization", "XVE Inst Executed ALU0 CS Utilization"], perStage.Matches.Take(3).Select(m => m.Name));
        Assert.Equal("XVE Inst Executed ALU0 RT * Utilization", perStage.Matches[12].MatchedBy);

        PresetResolution stalls = CounterPresets.Resolve(GpuVendor.Intel, "stalls", intel)!;
        Assert.Equal((16, 4), (stalls.Matches.Count, stalls.OmittedIds.Count));
        Assert.Equal("XVE Stall", stalls.Matches[0].Name);
    }

    [Fact]
    public void D3dOnlyMachinesResolveOnlyTheD3dPresets()
    {
        foreach ((string catalog, GpuVendor vendor) in new[] { ("nvidia", GpuVendor.Nvidia), ("amd-radeon-igpu", GpuVendor.Amd) })
        {
            (_, _, CounterInfo[] counters) = CounterCatalogs.Load(catalog);
            PresetResolution stats = CounterPresets.Resolve(vendor, "pipelineStatistics", counters)!;
            Assert.Equal("verified", stats.Confidence);
            Assert.Equal(11, stats.Matches.Count);
            Assert.Equal("IA Vertices", stats.Matches[0].Name);
            PresetResolution depth = CounterPresets.Resolve(vendor, "depthOcclusion", counters)!;
            Assert.Equal(new[] { "Samples Submitted", "Samples Rendered", "Samples Rejected" }, depth.Matches.Select(m => m.Name));
            // Vendor globs such as *Raster* or *Utilization* never pick up the D3D runtime counters.
            Assert.All(VendorPresets, preset => Assert.Empty(CounterPresets.Resolve(vendor, preset, counters)!.Matches));
            Assert.Equal("unverified", CounterPresets.Resolve(vendor, "utilization", counters)!.Confidence);
        }
        (_, _, CounterInfo[] nvidia) = CounterCatalogs.Load("nvidia");
        Assert.Null(CounterPresets.Resolve(GpuVendor.Nvidia, "nope", nvidia));
    }

    [Fact]
    public void UnknownVendorTriesEveryBlock()
    {
        (_, _, CounterInfo[] intel) = CounterCatalogs.Load("intel-arc-b580");
        PresetResolution unknown = CounterPresets.Resolve(GpuVendor.Unknown, "utilization", intel)!;
        Assert.Equal("unverified", unknown.Confidence);
        Assert.Equal("unknown", unknown.Vendor);
        Assert.True(unknown.Matches.Count >= 9, "the unknown vendor tries every block");
        Assert.Equal(unknown.Matches.Count, unknown.Matches.Select(m => m.Id).Distinct().Count());
    }
}

public sealed class CompatibilityNotesTests
{
    [Fact]
    public void NotesLoadWithUniqueIdsAndFilterByVendorAndVersion()
    {
        Assert.True(CompatibilityNotes.All.Count >= 15);
        Assert.Equal(CompatibilityNotes.All.Count, CompatibilityNotes.All.Select(n => n.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(CompatibilityNotes.All, n => Assert.Contains(n.Severity, new[] { "info", "warning" }));
        IReadOnlyList<CompatibilityNote> intelStatic = CompatibilityNotes.For("staticShaderProfiling", GpuVendor.Intel, "2606.18-preview");
        Assert.Contains(intelStatic, n => n.Id == "staticShaderProfiling.intel.meshPipelines");
        Assert.DoesNotContain(intelStatic, n => n.Vendor == "amd");
        Assert.Empty(CompatibilityNotes.For("staticShaderProfiling", GpuVendor.Intel, "2603.25"));
        Assert.Contains(CompatibilityNotes.Texts("counters", GpuVendor.Nvidia), t => t.Contains("22"));
        Assert.Contains(CompatibilityNotes.Texts("counters", GpuVendor.Amd), t => t.Contains("playback rounds"));
        Assert.Contains(CompatibilityNotes.Texts("counters", GpuVendor.Intel), t => t.Contains("259"));
        Assert.Equal("unsupported", CompatibilityNotes.CapabilityState("systemMonitorHardwareCounters", GpuVendor.Intel));
        Assert.Equal("supported", CompatibilityNotes.CapabilityState("systemMonitorHardwareCounters", GpuVendor.Nvidia));
        Assert.Null(CompatibilityNotes.CapabilityState("systemMonitorHardwareCounters", GpuVendor.Unknown));
        Assert.Equal("unsupported", CompatibilityNotes.CapabilityState("dumpD3DState", GpuVendor.Unknown));
    }

    [Fact]
    public void HardwareObservationsFeedCapabilityStates()
    {
        foreach (GpuVendor vendor in new[] { GpuVendor.Nvidia, GpuVendor.Intel, GpuVendor.Amd })
        {
            Assert.Equal("unsupported", CompatibilityNotes.CapabilityState("occupancy", vendor, "2606.18-preview"));
            Assert.Equal("unsupported", CompatibilityNotes.CapabilityState("highFrequencyCounters", vendor, "2606.18-preview"));
        }
        // A later PIX build is not assumed to keep the 2606.18 gaps.
        Assert.Null(CompatibilityNotes.CapabilityState("highFrequencyCounters", GpuVendor.Nvidia, "2607.01"));
        Assert.Contains(CompatibilityNotes.For("analysisAdapters", GpuVendor.Unknown, "2606.18-preview"), n => n.Id == "analysisAdapters.crossVendorReplay");
        Assert.DoesNotContain(CompatibilityNotes.For("analysisFlags", GpuVendor.Unknown, "2606.18-preview"), n => n.Id.StartsWith("analysisAdapters.", StringComparison.Ordinal));
        Assert.Contains(CompatibilityNotes.Texts("liveShaderProfiling", GpuVendor.Intel, "2606.18-preview"), t => t.Contains("0x80004005"));
    }
}
