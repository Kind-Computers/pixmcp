using System.Runtime.InteropServices;
using PixMcp.Pix;
using Xunit;

namespace PixMcp.Tests;

public sealed class ShaderProfileDescribeTests
{
    private static readonly ShaderRef PixelRef = new(new EventRef("gpu-1", 0, 5), 1);

    private static LiveProfileData Data() => new(
        [new LiveStallTypeInfo(0, "Texture Fetch Latency", "waiting on texture memory"), new LiveStallTypeInfo(1, "ALU Pipe Issue", null)],
        [
            new LiveShaderData(0, "PIXEL", "abcd",
            [
                new LiveInstructionData(0, 0, 100, [new LiveStallSample(0, 60)]),
                new LiveInstructionData(1, 4, 50, [new LiveStallSample(1, 5)]),
                new LiveInstructionData(2, 8, 10, []),
            ]),
            new LiveShaderData(1, "VERTEX", "ef01", [new LiveInstructionData(0, 0, 40, [new LiveStallSample(1, 4)])]),
            new LiveShaderData(2, "COMPUTE", null, [new LiveInstructionData(0, 0, 0, [])]),
        ]);

    private static IReadOnlyList<ShaderRef> References(string? hash, string stage)
        => hash == "abcd" && stage == "PIXEL" ? [PixelRef] : [];

    [Fact]
    public void TotalsBreakdownsClassificationAndHottestInstructions()
    {
        var (totals, shaders, keys, detail) = LiveProfileDescriber.Describe(Data(), new LiveProfileOptions("nvidia", 2, null, null), References);

        Assert.Equal((3, 200UL, 69UL, 131UL, 34.5), (totals.ShaderCount, totals.TotalSamples, totals.StalledSamples, totals.IssuingSamples, totals.StalledPercent));
        Assert.Equal(new[] { ("Texture Fetch Latency", 60UL, 30.0), ("ALU Pipe Issue", 9UL, 4.5) },
            totals.StallTotalsAcrossShaders.Select(s => (s.Type, s.Samples, s.Percent)));
        Assert.Equal(new[] { "hash:PIXEL:ABCD", "hash:VERTEX:EF01" }, keys);
        Assert.Equal(new[] { "PIXEL", "VERTEX", "COMPUTE" }, shaders.Select(s => s.Stage));

        LiveShaderSummaryDto pixel = shaders[0];
        // 65 / 160 = 40.625, which Math.Round takes to the even neighbour.
        Assert.Equal((160UL, 65UL, 95UL, 40.62), (pixel.Samples.Total, pixel.Samples.Stalled, pixel.Samples.Issuing, pixel.Samples.StalledPercent));
        Assert.Equal(pixel.Samples.Stalled, pixel.StallTotals.Aggregate(0UL, (sum, s) => sum + s.Samples));
        Assert.Equal(new uint[] { 0, 1 }, pixel.HottestInstructions.Select(i => i.Id));
        Assert.True(pixel.HottestTruncated);
        Assert.Equal(62.5, pixel.HottestInstructions[0].PercentOfShader);
        Assert.Equal("Texture Fetch Latency", Assert.Single(pixel.HottestInstructions[0].Stalls).Type);
        Assert.Equal(3, detail[pixel.Index].Instructions.Count);
        Assert.Equal($"/detail/{pixel.Index}", pixel.DetailPointer);
        StallClassificationDto memory = pixel.Classification!;
        Assert.Equal(("Texture Fetch Latency", 37.5, "memoryLatency", false, "heuristic"), (memory.DominantStall, memory.Percent, memory.Bound, memory.WeakSignal, memory.Basis));
        Assert.Contains("'texture'", memory.Rule);
        Assert.Equal(new[] { "pix_gpu_shader_code", "pix_gpu_shader_static_profile" }, pixel.NextCalls.Select(c => c.Tool));
        Assert.Equal(PixelRef, Assert.Single(pixel.ShaderRefs));

        LiveShaderSummaryDto vertex = shaders[1];
        Assert.Equal("instructionIssue", vertex.Classification!.Bound);
        Assert.True(vertex.Classification.WeakSignal);
        Assert.StartsWith("Weak signal (10%", vertex.Classification.Hint);
        Assert.Empty(vertex.NextCalls);
        Assert.False(vertex.HottestTruncated);

        LiveShaderSummaryDto compute = shaders[2];
        Assert.Null(compute.Classification);
        Assert.Null(compute.ShaderKey);
    }

    [Fact]
    public void FiltersNarrowTheShaderListButNotTheTotals()
    {
        var (totals, byStage, _, detail) = LiveProfileDescriber.Describe(Data(), new LiveProfileOptions("amd", 25, "vertex", null), References);
        Assert.Equal(200UL, totals.TotalSamples);
        Assert.Equal("VERTEX", Assert.Single(byStage).Stage);
        Assert.Single(detail);
        Assert.Equal("/detail/0", byStage[0].DetailPointer);

        var (_, byHash, _, _) = LiveProfileDescriber.Describe(Data(), new LiveProfileOptions("amd", 25, "PIXEL", "ABCD"), References);
        LiveShaderSummaryDto pixel = Assert.Single(byHash);
        Assert.Contains(pixel.NextCalls, c => c.Tool == "pix_gpu_shader_static_profile" && System.Text.Json.JsonSerializer.Serialize(c.Arguments).Contains("gfx1201"));
        Assert.Empty(LiveProfileDescriber.Describe(Data(), new LiveProfileOptions("amd", 25, "PIXEL", "none"), References).Shaders);
    }

    [Fact]
    public void ClassificationFallsBackForUnknownStallNames()
    {
        StallClassificationDto unknown = LiveProfileDescriber.Classify(new Dictionary<string, ulong> { ["Mystery"] = 90, ["Other"] = 5 }, 100)!;
        Assert.Equal(("Mystery", "unclassified", false), (unknown.DominantStall, unknown.Bound, unknown.WeakSignal));
        Assert.Equal("dependencies", LiveProfileDescriber.Classify(new Dictionary<string, ulong> { ["Barrier Wait"] = 50 }, 100)!.Bound);
        Assert.Equal("controlFlow", LiveProfileDescriber.Classify(new Dictionary<string, ulong> { ["Branch Divergence"] = 50 }, 100)!.Bound);
        Assert.Null(LiveProfileDescriber.Classify(new Dictionary<string, ulong>(), 100));
        Assert.Null(LiveProfileDescriber.Classify(new Dictionary<string, ulong> { ["Barrier Wait"] = 0 }, 100));
        Assert.Null(LiveProfileDescriber.Classify(new Dictionary<string, ulong> { ["Barrier Wait"] = 5 }, 0));
    }

    [Fact]
    public void UnsupportedDetectionCoversHResultsAndErrorCodes()
    {
        Assert.True(PixErrors.IsUnsupported(new COMException("not implemented", unchecked((int)0x80004001))));
        Assert.True(PixErrors.IsUnsupported(new COMException("no interface", unchecked((int)0x80004002))));
        Assert.True(PixErrors.IsUnsupported(PixErrors.UnsupportedFeature("The driver cannot profile shaders.")));
        Assert.False(PixErrors.IsUnsupported(new InvalidOperationException("boom")));
        Assert.False(PixErrors.IsUnsupported(PixErrors.InvalidArguments("bad")));
        Assert.True(PixErrors.IsDeclined(new COMException("declined", unchecked((int)0x8ABC0007))));
        Assert.False(PixErrors.IsDeclined(new COMException("developer mode", PixErrors.E_PIX_DEVELOPER_MODE_NOT_ENABLED)));
        Assert.False(PixErrors.IsDeclined(new COMException("not implemented", unchecked((int)0x80004001))));
    }
}
