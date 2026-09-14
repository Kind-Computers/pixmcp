using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PixMcp.Pix;
using PixMcp.Pix.Handles;
using PixMcp.Tools;
using Xunit;

namespace PixMcp.Tests;

/// <summary>
/// Vendor behaviour observed on a machine with an RTX 4070 Ti, an Intel Arc B580 and an AMD Radeon iGPU (PIX 2606.18-preview),
/// replaying the fixtures captured on NVIDIA (PIX_TEST_CAPTURE, PIX_TEST_PERF_BASELINE) and, when set, on Intel and AMD
/// (PIX_TEST_INTEL_CAPTURE, PIX_TEST_AMD_CAPTURE and their _PERF_BASELINE). Gated by PIX_TEST_VENDOR_VALIDATION=1; a vendor
/// without an adapter or capture skips.
/// </summary>
[Collection(GpuReplayCollection.Name)]
public sealed class VendorValidationTests : IDisposable
{
    private const string Deprecated = "<deprecated - use pix3.h instead> ";
    private const string TrianglePass = Deprecated + "Frame/" + Deprecated + "Triangle pass";
    private readonly PixWorker _worker = new();
    private readonly PixSession _session;
    private readonly JobManager _jobs;

    public VendorValidationTests()
    {
        _session = new PixSession(_worker, NullLogger<PixSession>.Instance);
        _jobs = new JobManager(_worker, _session);
    }

    private static void RequireVendorValidation()
    {
        TestArtifacts.SkipUnlessPix();
        Skip.IfNot(TestArtifacts.AnalysisEnabled, "Set PIX_TEST_ANALYSIS=1 to replay on the GPU (Developer Mode required)");
        Skip.IfNot(TestArtifacts.VendorValidation, "Set PIX_TEST_VENDOR_VALIDATION=1 to replay the fixtures on every GPU vendor present");
    }

    private static string RequireCapture()
    {
        RequireVendorValidation();
        Skip.If(TestArtifacts.Capture is null, "Set PIX_TEST_CAPTURE to the NVIDIA-captured baseline fixture");
        return TestArtifacts.Capture!;
    }

    [SkippableTheory]
    [InlineData("intel")]
    [InlineData("amd")]
    public async Task CrossVendorReplayIsRefusedUntilIncompatibilitiesAreIgnored(string vendor)
    {
        string handle = Handle(await GpuCaptureTools.Open(_session, RequireCapture()));
        string adapterName = await AdapterOf(handle, vendor);

        JsonElement refused = Parse(await AnalysisTools.Start(_session, _jobs, handle, adapterName: vendor, waitSeconds: 600));
        Assert.Equal("failed", refused.GetProperty("status").GetString());
        JsonElement error = refused.GetProperty("error");
        Assert.Equal(PixErrors.Codes.AnalysisIncompatible, error.GetProperty("code").GetString());
        Assert.Equal("0x8ABC006B", error.GetProperty("hresult").GetString());
        JsonElement retry = error.GetProperty("nextCalls")[0];
        Assert.Equal("pix_gpu_analysis_start", retry.GetProperty("tool").GetString());
        string[] flags = retry.GetProperty("arguments").GetProperty("flags").EnumerateArray().Select(f => f.GetString()!).ToArray();
        Assert.Equal(["IGNORE_INCOMPATIBILITIES"], flags);

        JsonElement started = Parse(await AnalysisTools.Start(_session, _jobs, handle, adapterId: retry.GetProperty("arguments").GetProperty("adapterId").GetUInt64(),
            flags: flags, waitSeconds: 600));
        Assert.Equal("succeeded", started.GetProperty("status").GetString());
        ReplayProvenance provenance = _session.Get<GpuCaptureHandle>(handle).Provenance();
        Assert.Equal((vendor, adapterName, "nvidia", true), (provenance.Vendor, provenance.AdapterName, provenance.CaptureVendor, provenance.VendorMismatch));
        await SessionTools.Close(_session, handle);
    }

    [SkippableTheory]
    [InlineData("intel")]
    [InlineData("amd")]
    public async Task NativeCapturesReplayOnTheirOwnVendorWithoutFlagsButNotOnNvidia(string vendor)
    {
        RequireVendorValidation();
        string? capture = vendor == "intel" ? TestArtifacts.IntelCapture : TestArtifacts.AmdCapture;
        Skip.If(capture is null, $"Set PIX_TEST_{vendor.ToUpperInvariant()}_CAPTURE to a baseline fixture captured on that GPU (capture_fixtures.py --adapter-name)");

        string handle = await OpenOn(capture!, vendor, captureVendor: vendor);
        ReplayProvenance provenance = _session.Get<GpuCaptureHandle>(handle).Provenance();
        Assert.Equal((vendor, vendor, false), (provenance.Vendor, provenance.CaptureVendor, provenance.VendorMismatch));
        JsonElement profile = await JobResult(await ShaderProfilingTools.Profile(_session, _jobs, handle, markerPathPrefix: TrianglePass, waitSeconds: 600));
        // Live profiling fails on Intel and is declined on AMD for their own captures too: not an artifact of cross-vendor replay.
        Assert.Equal(vendor == "intel" ? "failed" : "declined", profile.GetProperty("state").GetString());
        await SessionTools.Close(_session, handle);

        string reverse = Handle(await GpuCaptureTools.Open(_session, capture!));
        await AdapterOf(reverse, "nvidia");
        JsonElement refused = Parse(await AnalysisTools.Start(_session, _jobs, reverse, adapterName: "nvidia", waitSeconds: 600));
        Assert.Equal(PixErrors.Codes.AnalysisIncompatible, refused.GetProperty("error").GetProperty("code").GetString());
        await SessionTools.Close(_session, reverse);
    }

    [SkippableTheory]
    [InlineData("nvidia", "declined", false)]
    [InlineData("intel", "failed", true)]
    [InlineData("amd", "declined", false)]
    public async Task OptionalFeaturesMatchTheObservedVendorBehaviour(string vendor, string liveProfileState, bool bandwidthThroughPlugin)
    {
        string handle = await OpenOn(RequireCapture(), vendor);
        foreach (string json in new[] { await CountersTools.Occupancy(_session, _jobs, handle, maxPoints: 5, waitSeconds: 600),
                                         await CountersTools.HighFrequencyCounters(_session, _jobs, handle, waitSeconds: 600) })
        {
            JsonElement unavailable = Parse(json);
            Assert.True(unavailable.GetProperty("unavailable").GetBoolean());
            Assert.Equal(("unsupported", "0x80004001"), (unavailable.GetProperty("state").GetString(), unavailable.GetProperty("error").GetProperty("hresult").GetString()));
        }

        JsonElement profile = await JobResult(await ShaderProfilingTools.Profile(_session, _jobs, handle, markerPathPrefix: TrianglePass, waitSeconds: 600));
        Assert.Equal((liveProfileState, vendor), (profile.GetProperty("state").GetString(), profile.GetProperty("vendor").GetString()));

        JsonElement run = (await JobResult(await DrPixTools.Run(_session, _jobs, handle, experiments: ["Bandwidth"], markerPathPrefix: TrianglePass, waitSeconds: 900)))
            .GetProperty("runs")[0];
        Assert.Equal(bandwidthThroughPlugin, run.GetProperty("succeeded").GetBoolean());
        if (bandwidthThroughPlugin)
            Assert.Contains(run.GetProperty("records").EnumerateArray(), r => r.GetProperty("group").GetString() == "GTI Throughput");
        await SessionTools.Close(_session, handle);
    }

    [SkippableFact]
    public async Task IntelCountersMatchTheCatalogAndTheCalibrationPass()
    {
        string handle = await OpenOn(RequireCapture(), "intel");
        JsonElement list = Expand(await CountersTools.CountersList(_session, _jobs, handle, limit: 1000, waitSeconds: 600));
        HashSet<string> live = list.GetProperty("items").EnumerateArray().Select(c => c.GetProperty("name").GetString()!).ToHashSet(StringComparer.Ordinal);
        (_, _, CounterInfo[] committed) = CounterCatalogs.Load("intel-arc-b580");
        Assert.Empty(committed.Select(c => c.Name).Where(name => !live.Contains(name)));
        await SessionTools.Close(_session, handle);

        // The calibration holds whichever GPU took the perf capture.
        (string Capture, string Vendor)[] perfCaptures = new[] { (TestArtifacts.PerfBaseline, "nvidia"), (TestArtifacts.IntelPerfBaseline, "intel"), (TestArtifacts.AmdPerfBaseline, "amd") }
            .Where(p => p.Item1 is not null).Select(p => (p.Item1!, p.Item2)).ToArray();
        Skip.If(perfCaptures.Length == 0, "Set PIX_TEST_PERF_BASELINE (or PIX_TEST_INTEL_PERF_BASELINE / PIX_TEST_AMD_PERF_BASELINE) to check the Intel calibration pass");
        foreach ((string capture, string captureVendor) in perfCaptures)
        {
            string perf = await OpenOn(capture, "intel", captureVendor);
            JsonElement stages = Expand(await CountersTools.CountersCollect(_session, _jobs, perf, preset: "perStageAlu", queueIndex: 0, kind: "marker", limit: 25, waitSeconds: 900));
            string ps = stages.GetProperty("extra").GetProperty("counters").EnumerateArray()
                .Single(c => c.GetProperty("name").GetString() == "XVE Inst Executed ALU0 PS Utilization").GetProperty("id").GetUInt32().ToString(System.Globalization.CultureInfo.InvariantCulture);
            double PixelShaderUtilization(string marker) => stages.GetProperty("items").EnumerateArray().Where(r => r.GetProperty("name").GetString() == marker)
                .Select(r => r.GetProperty("values").GetProperty(ps)).Where(v => v.ValueKind == JsonValueKind.Number).Select(v => v.GetDouble()).DefaultIfEmpty(double.NaN).Max();
            Assert.InRange(PixelShaderUtilization("Lighting"), 40, 100);
            Assert.InRange(PixelShaderUtilization("Shadow"), 0, 20);

            JsonElement bottleneck = await JobResult(await BottleneckTools.Bottleneck(_session, _jobs, perf, markerPathPrefix: "Frame/Lighting",
                evidence: ["timing", "counters", "drpix"], waitSeconds: 1800));
            Assert.Equal(("pixelShading", "high"), (bottleneck.GetProperty("verdict").GetProperty("limiter").GetString(), bottleneck.GetProperty("verdict").GetProperty("confidence").GetString()));
            Assert.Contains("intel_ps_alu", bottleneck.GetProperty("rules").GetProperty("satisfiedIds").EnumerateArray().Select(id => id.GetString()));
            Assert.True(bottleneck.GetProperty("rules").GetProperty("vendorValidated").GetBoolean());
            await SessionTools.Close(_session, perf);
        }
    }

    /// <summary>Opens a capture and starts analysis on the vendor's adapter, adding IGNORE_INCOMPATIBILITIES when the capture comes from another vendor.</summary>
    private async Task<string> OpenOn(string capture, string vendor, string captureVendor = "nvidia")
    {
        string handle = Handle(await GpuCaptureTools.Open(_session, capture));
        await AdapterOf(handle, vendor);
        JsonElement started = Parse(await AnalysisTools.Start(_session, _jobs, handle, adapterName: vendor,
            flags: vendor == captureVendor ? null : ["IGNORE_INCOMPATIBILITIES"], waitSeconds: 600));
        Assert.Equal("succeeded", started.GetProperty("status").GetString());
        return handle;
    }

    private async Task<string> AdapterOf(string handle, string vendor)
    {
        JsonElement adapters = Parse(await AnalysisTools.Adapters(_session, handle)).GetProperty("adapters");
        string? name = adapters.EnumerateArray().Where(a => a.GetProperty("vendor").GetString() == vendor).Select(a => a.GetProperty("name").GetString()).FirstOrDefault();
        if (name is null) await SessionTools.Close(_session, handle);
        Skip.If(name is null, $"No {vendor} analysis adapter on this machine");
        return name!;
    }

    private async Task<JsonElement> JobResult(string json)
    {
        JsonElement job = Parse(json);
        Assert.Equal("succeeded", job.GetProperty("status").GetString());
        return await Task.FromResult(_session.Results.ReadElement(job.GetProperty("resultRef").GetString()!, "", 64 * 1024 * 1024));
    }

    private JsonElement Expand(string json)
    {
        JsonElement answer = Parse(json);
        return answer.TryGetProperty("deferred", out _) ? _session.Results.ReadElement(answer.GetProperty("resultRef").GetString()!, "", 64 * 1024 * 1024) : answer;
    }

    private static string Handle(string opened) => Parse(opened).GetProperty("handle").GetString()!;
    private static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    public void Dispose()
    {
        _session.Dispose();
        _worker.Dispose();
    }
}
