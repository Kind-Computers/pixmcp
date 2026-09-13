using System.Text.Json;
using PixMcp.Pix;
using PixMcp.Pix.Handles;
using Xunit;

namespace PixMcp.Tests;

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
    [InlineData("Something", "Percentage of time the unit was busy.", "FLOAT32", "percent", "description", "medium")]
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
        (_, _, CounterInfo[] intel) = CounterCatalogs.Load("intel-xe2");
        Assert.All(intel.Where(c => c.Name.EndsWith("(%)")), c => Assert.Equal("high", c.Unit.UnitConfidence));
        Assert.Equal("count", intel.Single(c => c.Name == "XVE_SLM_READ_MESSAGE_COUNT").Unit.Unit);
    }
}

public sealed class CounterPresetsTests
{
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
    public void IntelUtilizationResolvesInPatternOrderAndListsUnmatchedPatterns()
    {
        (_, _, CounterInfo[] intel) = CounterCatalogs.Load("intel-xe2");
        PresetResolution resolution = CounterPresets.Resolve(GpuVendor.Intel, "utilization", intel)!;
        Assert.Equal("intel", resolution.Vendor);
        Assert.Equal("transcribed", resolution.Confidence);
        Assert.Equal("Xe GPU Utilization", resolution.Matches[0].Name);
        Assert.Equal("XVE Utilization (%)", resolution.Matches[1].Name);
        Assert.Equal("Xe GPU Utilization", resolution.Matches[0].MatchedBy);
        Assert.Empty(resolution.UnmatchedPatterns);
        Assert.False(resolution.Capped);
        PresetResolution stalls = CounterPresets.Resolve(GpuVendor.Intel, "stalls", intel)!;
        Assert.Contains("XVE_STALL_BARRIER", stalls.UnmatchedPatterns);
        Assert.Equal(4, stalls.Matches.Count);
        PresetResolution perStage = CounterPresets.Resolve(GpuVendor.Intel, "perStageAlu", intel)!;
        Assert.Equal(new[] { "XVE_INST_EXECUTED_ALU0_VS_UTILIZATION", "XVE_INST_EXECUTED_ALU0_PS_UTILIZATION", "XVE_INST_EXECUTED_ALU0_CS_UTILIZATION" }, perStage.Matches.Select(m => m.Name));
    }

    [Fact]
    public void NvidiaMachineOnlyResolvesTheD3dPresets()
    {
        (_, _, CounterInfo[] nvidia) = CounterCatalogs.Load("nvidia");
        PresetResolution stats = CounterPresets.Resolve(GpuVendor.Nvidia, "pipelineStatistics", nvidia)!;
        Assert.Equal("verified", stats.Confidence);
        Assert.Equal(11, stats.Matches.Count);
        Assert.Equal("IA Vertices", stats.Matches[0].Name);
        PresetResolution depth = CounterPresets.Resolve(GpuVendor.Nvidia, "depthOcclusion", nvidia)!;
        Assert.Equal(new[] { "Samples Submitted", "Samples Rendered", "Samples Rejected" }, depth.Matches.Select(m => m.Name));
        PresetResolution utilization = CounterPresets.Resolve(GpuVendor.Nvidia, "utilization", nvidia)!;
        Assert.Empty(utilization.Matches);
        Assert.Equal("unverified", utilization.Confidence);
        Assert.Equal(4, utilization.UnmatchedPatterns.Count);
        Assert.Null(CounterPresets.Resolve(GpuVendor.Nvidia, "nope", nvidia));
    }

    [Fact]
    public void CapReportsOmittedIdsAndUnknownVendorTriesEveryBlock()
    {
        (_, _, CounterInfo[] intel) = CounterCatalogs.Load("intel-xe2");
        PresetResolution capped = CounterPresets.Resolve(GpuVendor.Intel, "utilization", intel, cap: 3)!;
        Assert.True(capped.Capped);
        Assert.Equal(3, capped.Matches.Count);
        Assert.Equal(10, capped.OmittedIds.Count);
        Assert.Equal(3, capped.Ids.Length);
        PresetResolution unknown = CounterPresets.Resolve(GpuVendor.Unknown, "utilization", intel)!;
        Assert.Equal("unverified", unknown.Confidence);
        Assert.Equal("unknown", unknown.Vendor);
        Assert.True(unknown.Matches.Count >= 13, "the unknown vendor tries every block");
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
        Assert.Equal("unsupported", CompatibilityNotes.CapabilityState("systemMonitorHardwareCounters", GpuVendor.Intel));
        Assert.Equal("supported", CompatibilityNotes.CapabilityState("systemMonitorHardwareCounters", GpuVendor.Nvidia));
        Assert.Null(CompatibilityNotes.CapabilityState("systemMonitorHardwareCounters", GpuVendor.Unknown));
        Assert.Equal("unsupported", CompatibilityNotes.CapabilityState("dumpD3DState", GpuVendor.Unknown));
    }
}
