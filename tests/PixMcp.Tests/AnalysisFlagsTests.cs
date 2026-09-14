using System.Text.Json;
using Microsoft.PIX;
using PixMcp.Pix;
using Xunit;

namespace PixMcp.Tests;

public sealed class AnalysisFlagsTests
{
    [Fact]
    public void EveryFlagParsesByShortAndFullName()
    {
        Assert.Equal(Enum.GetValues<PIX_ANALYSIS_FLAGS>().Length, AnalysisFlags.Names.Length);
        foreach (PIX_ANALYSIS_FLAGS flag in Enum.GetValues<PIX_ANALYSIS_FLAGS>())
        {
            Assert.Equal(flag, AnalysisFlags.Parse([flag.ToString()]));
            Assert.Equal(flag, AnalysisFlags.Parse([flag.ToString().ToLowerInvariant()]));
        }
        foreach (string name in AnalysisFlags.Names) Assert.NotNull(AnalysisFlags.Parse([name]));
        Assert.Equal(PIX_ANALYSIS_FLAGS.PIX_ANALYSIS_FLAG_IGNORE_INCOMPATIBILITIES | PIX_ANALYSIS_FLAGS.PIX_ANALYSIS_ENABLE_DEBUG_LAYER,
            AnalysisFlags.Parse([" ignore_incompatibilities", "PIX_ANALYSIS_ENABLE_DEBUG_LAYER", "NONE"]));
        Assert.Equal(PIX_ANALYSIS_FLAGS.PIX_ANALYSIS_FLAG_NONE, AnalysisFlags.Parse(["none"]));
        Assert.Null(AnalysisFlags.Parse([]));
        Assert.Null(AnalysisFlags.Parse(null));
    }

    [Fact]
    public void UnknownFlagsFailWithTheValidNamesAndARetryWithoutThem()
    {
        var error = Assert.Throws<PixToolException>(() => AnalysisFlags.Parse(["DISABLE_GPU_PLUGINS", "TURBO"], "gpu-1"));
        Assert.Equal("invalid_arguments", error.Detail.Code);
        Assert.Contains("'TURBO'", error.Detail.Message);
        Assert.Contains("FORCE_SET_APPLICATION_SPECIFIC_DRIVER_STATE", error.Detail.Message);
        ToolCallDto retry = Assert.Single(error.Detail.NextCalls!);
        Assert.Equal("pix_gpu_analysis_start", retry.Tool);
        string arguments = JsonSerializer.Serialize(retry.Arguments);
        Assert.Contains("DISABLE_GPU_PLUGINS", arguments);
        Assert.DoesNotContain("TURBO", arguments);
        Assert.Empty(Assert.Throws<PixToolException>(() => AnalysisFlags.Parse(["TURBO"])).Detail.NextCalls ?? []);
    }

    [Fact]
    public void DecodeNamesSelectedFlagsAndTreatsZeroLikeNone()
    {
        Assert.Null(AnalysisFlags.Decode(null));
        Assert.Equal("pixDefault", AnalysisFlags.Source(null));
        Assert.Equal("explicit", AnalysisFlags.Source(PIX_ANALYSIS_FLAGS.PIX_ANALYSIS_FLAG_NONE));
        Assert.Equal(["NONE"], AnalysisFlags.Decode(0)!.Names);
        Assert.Equal(["NONE"], AnalysisFlags.Decode(PIX_ANALYSIS_FLAGS.PIX_ANALYSIS_FLAG_NONE)!.Names);

        AnalysisFlagsDto two = AnalysisFlags.Decode(PIX_ANALYSIS_FLAGS.PIX_ANALYSIS_FLAG_IGNORE_INCOMPATIBILITIES | PIX_ANALYSIS_FLAGS.PIX_ANALYSIS_FLAG_DISABLE_GPU_PLUGINS)!;
        Assert.Equal((130u, 0u), (two.Raw, two.UnknownBits));
        Assert.Equal(["IGNORE_INCOMPATIBILITIES", "DISABLE_GPU_PLUGINS"], two.Names);
        Assert.All(two.Flags, f => Assert.False(string.IsNullOrWhiteSpace(f.Meaning)));
        Assert.Equal(0x1000u, AnalysisFlags.Decode((PIX_ANALYSIS_FLAGS)0x1002)!.UnknownBits);
    }

    [Fact]
    public void SchemaAdvertisesFlagItemsAndValidationChecksEveryElement()
    {
        JsonElement original = JsonDocument.Parse("""{"type":"object","properties":{"flags":{"type":["array","null"],"items":{"type":"string"}}}}""").RootElement;
        JsonElement flags = StructuredToolResults.InputSchemaFor("pix_gpu_analysis_start", original).GetProperty("properties").GetProperty("flags");
        Assert.Equal(AnalysisFlags.Names, flags.GetProperty("items").GetProperty("enum").EnumerateArray().Select(v => v.GetString()));
        Assert.False(flags.TryGetProperty("enum", out _));

        StructuredToolResults.ValidateArguments("pix_gpu_analysis_start", Arguments("""{"flags":["disable_gpu_plugins","PIX_ANALYSIS_ENABLE_DEBUG_LAYER"]}"""));
        var error = Assert.Throws<PixToolException>(() => StructuredToolResults.ValidateArguments("pix_gpu_analysis_start",
            Arguments("""{"handle":"gpu-1","flags":["DISABLE_GPU_PLUGINS","TURBO"]}""")));
        Assert.Equal("invalid_arguments", error.Detail.Code);
        Assert.Contains("TURBO", error.Detail.Message);
        ToolCallDto retry = Assert.Single(error.Detail.NextCalls!);
        string arguments = JsonSerializer.Serialize(retry.Arguments);
        Assert.Contains("gpu-1", arguments);
        Assert.Contains("DISABLE_GPU_PLUGINS", arguments);
        Assert.DoesNotContain("TURBO", arguments);
    }

    private static Dictionary<string, JsonElement> Arguments(string json)
        => JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
}
