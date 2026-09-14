using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.PIX;
using PixMcp.Pix;
using Xunit;

namespace PixMcp.Tests;

public class AdapterSelectionTests
{
    // Adapter names and ids as pix_gpu_analysis_adapters listed them on the RTX 4070 Ti + Arc B580 + Radeon iGPU machine (PIX 2606.18).
    private static readonly (ulong Id, string Name)[] Adapters =
    [
        (115800, "NVIDIA GeForce RTX 4070 Ti"),
        (14344146, "Intel(R) Arc(TM) B580 Graphics"),
        (121312, "AMD Radeon(TM) Graphics"),
        (121026, "Microsoft Basic Render Driver"),
    ];

    private static readonly VendorIdentity NvidiaCapture = new(GpuVendor.Nvidia, "NVIDIA GeForce RTX 4070 Ti", "captureFileInfo");

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4070 Ti", 115800UL)]
    [InlineData("intel(r) arc(tm) b580 graphics", 14344146UL)]
    [InlineData("Arc(TM) B580", 14344146UL)]
    [InlineData("intel", 14344146UL)]
    [InlineData("AMD", 121312UL)]
    [InlineData("radeon", 121312UL)]
    [InlineData("nvidia", 115800UL)]
    [InlineData("warp", 121026UL)]
    public void ResolvesAdaptersByNamePartOrVendor(string name, ulong expected)
        => Assert.Equal(expected, AdapterSelection.Resolve(Adapters, name, "gpu-1"));

    [Theory]
    [InlineData("Graphics", "matches 2 adapters")]
    [InlineData("Qualcomm", "No analysis adapter matches")]
    [InlineData("  ", "adapterName is empty")]
    public void UnknownOrAmbiguousNamesListTheAdapters(string name, string expected)
    {
        PixToolException error = Assert.Throws<PixToolException>(() => AdapterSelection.Resolve(Adapters, name, "gpu-1"));
        Assert.Equal(PixErrors.Codes.InvalidArguments, error.Detail.Code);
        Assert.Contains(expected, error.Detail.Message);
        Assert.Equal("pix_gpu_analysis_adapters", Assert.Single(error.Detail.NextCalls).Tool);
    }

    [Fact]
    public void IdenticalAdapterNamesAreAmbiguous()
    {
        (ulong, string)[] twins = [(1, "Intel(R) Arc(TM) B580 Graphics"), (2, "Intel(R) Arc(TM) B580 Graphics")];
        PixToolException error = Assert.Throws<PixToolException>(() => AdapterSelection.Resolve(twins, "Intel(R) Arc(TM) B580 Graphics", "gpu-1"));
        Assert.Contains("matches 2 adapters; use a longer name or adapterId", error.Detail.Message);
        Assert.Contains("(id 1)", error.Detail.Message);
    }

    [Fact]
    public void CrossVendorRefusalRetriesWithIgnoreIncompatibilities()
    {
        var refusal = new COMException("declined", PixErrors.E_PIX_ANALYSIS_INCOMPATIBLE);
        PixToolException? error = AdapterSelection.IncompatibleStart(refusal, "gpu-1", 14344146, null,
            PIX_ANALYSIS_FLAGS.PIX_ANALYSIS_FLAG_DISABLE_GPU_PLUGINS, Adapters, NvidiaCapture);

        Assert.NotNull(error);
        Assert.Equal(PixErrors.Codes.AnalysisIncompatible, error.Detail.Code);
        Assert.Equal("0x8ABC006B", error.Detail.Hresult);
        Assert.False(error.Detail.Retryable);
        Assert.Contains("Intel(R) Arc(TM) B580 Graphics (intel)", error.Detail.Message);
        Assert.Contains("taken on NVIDIA GeForce RTX 4070 Ti (nvidia)", error.Detail.Message);
        Assert.Contains("another vendor's GPU", error.Detail.Message);
        ToolCallDto retry = error.Detail.NextCalls[0];
        Assert.Equal("pix_gpu_analysis_start", retry.Tool);
        JsonElement arguments = JsonDocument.Parse(Json.Serialize(retry.Arguments)).RootElement;
        Assert.Equal(14344146UL, arguments.GetProperty("adapterId").GetUInt64());
        Assert.False(arguments.TryGetProperty("powerStateId", out _));
        Assert.Equal(["DISABLE_GPU_PLUGINS", "IGNORE_INCOMPATIBILITIES"], arguments.GetProperty("flags").EnumerateArray().Select(f => f.GetString()!).ToArray());
        Assert.Equal("pix_gpu_analysis_adapters", error.Detail.NextCalls[1].Tool);
    }

    [Fact]
    public void RefusalWithoutAChosenAdapterSaysWhichAdapterTheRetryUses()
    {
        var refusal = new COMException("declined", PixErrors.E_PIX_ANALYSIS_INCOMPATIBLE);
        PixToolException error = AdapterSelection.IncompatibleStart(refusal, "gpu-1", null, null, null, Adapters, VendorIdentity.None)!;
        Assert.Contains("the adapter PIX chose", error.Detail.Message);
        Assert.Contains("first enumerated adapter", error.Detail.Message);
        JsonElement arguments = JsonDocument.Parse(Json.Serialize(error.Detail.NextCalls[0].Arguments)).RootElement;
        Assert.False(arguments.TryGetProperty("adapterId", out _));
        Assert.Equal("IGNORE_INCOMPATIBILITIES", Assert.Single(arguments.GetProperty("flags").EnumerateArray()).GetString());
    }

    [Fact]
    public void OtherFailuresAndFlaggedStartsAreNotMapped()
    {
        Assert.Null(AdapterSelection.IncompatibleStart(new COMException("failed", unchecked((int)0x80004005)), "gpu-1", 14344146, null, null, Adapters, NvidiaCapture));
        Assert.Null(AdapterSelection.IncompatibleStart(new COMException("declined", PixErrors.E_PIX_ANALYSIS_INCOMPATIBLE), "gpu-1", 14344146, null,
            PIX_ANALYSIS_FLAGS.PIX_ANALYSIS_FLAG_IGNORE_INCOMPATIBILITIES, Adapters, NvidiaCapture));
    }
}
