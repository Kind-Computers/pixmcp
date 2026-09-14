using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PixMcp.Pix;
using PixMcp.Pix.StaticProfiling;
using PixMcp.Tools;
using Xunit;

namespace PixMcp.Tests;

/// <summary>
/// Static shader profiling against the offline compilers in the PIX install. Inline sources need only PIX; the capture path also needs
/// PIX_TEST_CAPTURE and PIX_TEST_ANALYSIS=1.
/// </summary>
[Collection(GpuReplayCollection.Name)]
public sealed class StaticShaderProfilingNativeTests : IDisposable
{
    private const string Compute =
        "RWStructuredBuffer<float> Output : register(u0);\n[RootSignature(\"UAV(u0)\")]\n[numthreads(8, 8, 1)]\nvoid main(uint3 id : SV_DispatchThreadID)\n{\n" +
        "    float sum = 0;\n    for (uint i = 0; i < (id.x & 7); i++) sum += sin(i * 0.5) * cos(id.y + i);\n#ifdef HEAVY\n" +
        "    for (int j = 0; j < 16; j++) sum += sqrt(abs(sum) + j);\n#endif\n    Output[id.x] = sum;\n}\n";
    private const string Vertex = "float4 main(uint id : SV_VertexID) : SV_Position { float x = (id & 1) ? 1 : -1; return float4(x, 0, 0, 1); }";
    private const string Pixel = "float4 main(float4 pos : SV_Position) : SV_Target { float v = 0; for (int i = 0; i < 8; i++) v += sin(pos.x * i); return float4(v, 0, 0, 1); }";
    private const string Broken = "[numthreads(1,1,1)] void main() { undefined_call(); }";

    private readonly PixWorker _worker = new();
    private readonly PixSession _session;
    private readonly JobManager _jobs;

    public StaticShaderProfilingNativeTests()
    {
        _session = new PixSession(_worker, NullLogger<PixSession>.Instance);
        _jobs = new JobManager(_worker, _session);
    }

    public void Dispose()
    {
        _jobs.Dispose();
        _session.Dispose();
        _worker.Dispose();
    }

    private static JsonElement Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private async Task<(JsonElement Job, JsonElement Result)> Profile(string target, params StaticShaderSource[] sources)
    {
        JsonElement job = Parse(await StaticShaderProfilingTools.StaticProfile(_session, _jobs, target, sources: sources, waitSeconds: 300));
        Assert.True(job.GetProperty("status").GetString() == "succeeded", job.GetRawText());
        return (job, _session.Results.ReadElement(job.GetProperty("resultRef").GetString()!, "", FullResultBytes));
    }

    private const int FullResultBytes = 64 * 1024 * 1024;

    /// <summary>A response that was deferred to a result snapshot, read back whole.</summary>
    private JsonElement Materialize(JsonElement response)
        => response.TryGetProperty("deferred", out JsonElement deferred) && deferred.GetBoolean()
            ? _session.Results.ReadElement(response.GetProperty("resultRef").GetString()!, "", FullResultBytes)
            : response;

    private static string Excerpt(JsonElement value) => value.GetRawText() is { Length: > 2000 } text ? text[..2000] : value.GetRawText();

    [SkippableFact]
    public async Task ListsIntelAndAmdTargetsWithTheShippedCompilers()
    {
        TestArtifacts.SkipUnlessPix();
        JsonElement page = Materialize(Parse(await StaticShaderProfilingTools.Targets(_session, limit: 1000)));
        JsonElement[] items = page.GetProperty("items").EnumerateArray().ToArray();
        Assert.Contains(items, t => t.GetProperty("vendor").GetString() == "intel" && t.TryGetProperty("architecture", out JsonElement a) && a.GetString() == "Xe2-HPG");
        Assert.Contains(items, t => t.GetProperty("vendor").GetString() == "amd");
        JsonElement extra = page.GetProperty("extra");
        Assert.Contains(extra.GetProperty("offlineCompilers").EnumerateArray(),
            c => c.GetProperty("file").GetString() == "igd12um64xe2.dll" && c.GetProperty("fileVersion").GetString()!.StartsWith("32.", StringComparison.Ordinal));
        Assert.Contains(extra.GetProperty("families").EnumerateArray(), f => f.GetProperty("exampleTarget").GetString() == "Xe2-HPG");
        Assert.Equal(2, extra.GetProperty("exampleCalls").GetArrayLength());
        JsonElement intel = Materialize(Parse(await StaticShaderProfilingTools.Targets(_session, vendor: "intel", limit: 1000)));
        Assert.All(intel.GetProperty("items").EnumerateArray(), t => Assert.Equal("intel", t.GetProperty("vendor").GetString()));
    }

    [SkippableFact]
    public async Task CompilesInlineComputeForXe2AndRdna4WithLoopsDefinesAndResourceUsage()
    {
        TestArtifacts.SkipUnlessPix();
        (_, JsonElement intel) = await Profile("Xe2-HPG", new StaticShaderSource("cs_6_0", Hlsl: Compute));
        Assert.True(intel.GetProperty("succeeded").GetBoolean(), Excerpt(intel));
        Assert.Equal("intel", intel.GetProperty("target").GetProperty("vendor").GetString());
        JsonElement coverage = intel.GetProperty("coverage");
        Assert.True(coverage.GetProperty("definesFormat").GetProperty("verified").GetBoolean());
        Assert.True(coverage.GetProperty("rootSignatureFromSource").GetBoolean());
        Assert.True(coverage.GetProperty("sourceMappingAvailable").GetBoolean());
        JsonElement shader = intel.GetProperty("shaders")[0];
        Assert.Equal("COMPUTE", shader.GetProperty("stage").GetString());
        int plain = shader.GetProperty("instructionCount").GetInt32();
        Assert.True(plain > 0);
        string graph = string.Join(" ", intel.GetProperty("detail")[0].GetProperty("blocks").EnumerateArray().Select(b => $"{b.GetProperty("id").GetUInt32()}->[{string.Join(",", b.GetProperty("successors").EnumerateArray().Select(s => s.GetUInt32()))}]"));
        Assert.True(shader.GetProperty("controlFlow").GetProperty("loopCount").GetInt32() >= 1, graph);
        Assert.True(shader.GetProperty("hotSpots").GetArrayLength() > 0);
        Assert.True(shader.GetProperty("instructionMix").GetProperty("estimatedFixedCycles").GetUInt64() > 0);
        Assert.True(intel.GetProperty("detail")[0].GetProperty("blocks").GetArrayLength() > 0);

        (_, JsonElement heavy) = await Profile("Xe2-HPG", new StaticShaderSource("cs_6_0", Hlsl: Compute, Defines: ["HEAVY"]));
        Assert.True(heavy.GetProperty("shaders")[0].GetProperty("instructionCount").GetInt32() > plain, Excerpt(heavy));

        (_, JsonElement amd) = await Profile("gfx1201", new StaticShaderSource("cs_6_0", Hlsl: Compute));
        Assert.True(amd.GetProperty("succeeded").GetBoolean(), Excerpt(amd));
        Assert.Equal("amd", amd.GetProperty("target").GetProperty("vendor").GetString());
        Assert.StartsWith("https://", amd.GetProperty("target").GetProperty("isaDocumentationLink").GetString());
        Assert.Contains(amd.GetProperty("shaders")[0].GetProperty("resourceUsage").GetProperty("entries").EnumerateArray(), e => e.GetProperty("name").GetString() == "VGPRs");
    }

    [SkippableFact]
    public async Task CompilesGraphicsPipelinesAndReturnsCompileErrorsAsData()
    {
        TestArtifacts.SkipUnlessPix();
        (_, JsonElement graphics) = await Profile("Xe2-HPG", new StaticShaderSource("vs_6_0", Hlsl: Vertex), new StaticShaderSource("ps_6_0", Hlsl: Pixel));
        Assert.True(graphics.GetProperty("succeeded").GetBoolean(), Excerpt(graphics));
        Assert.Equal(new[] { "VERTEX", "PIXEL" }, graphics.GetProperty("shaders").EnumerateArray().Select(s => s.GetProperty("stage").GetString()));

        (JsonElement brokenJob, JsonElement broken) = await Profile("gfx1201", new StaticShaderSource("cs_6_0", Hlsl: Broken));
        Assert.False(broken.GetProperty("succeeded").GetBoolean());
        Assert.Equal("compile", broken.GetProperty("phase").GetString());
        JsonElement message = broken.GetProperty("compilerOutput").EnumerateArray().First(m => m.GetProperty("text").GetString()!.Contains("undeclared identifier", StringComparison.Ordinal));
        Assert.DoesNotContain("pixmcp-static-", message.GetProperty("text").GetString());
        (JsonElement again, _) = await Profile("gfx1201", new StaticShaderSource("cs_6_0", Hlsl: Broken));
        Assert.Equal(brokenJob.GetProperty("jobId").GetString(), again.GetProperty("jobId").GetString());

        (_, JsonElement unbound) = await Profile("Xe2-HPG", new StaticShaderSource("cs_6_0",
            Hlsl: "RWStructuredBuffer<float> O : register(u0);\n[numthreads(1,1,1)] void main(uint3 id : SV_DispatchThreadID) { O[id.x] = 1; }"));
        Assert.False(unbound.GetProperty("succeeded").GetBoolean());
        Assert.Contains(unbound.GetProperty("hints").EnumerateArray(), h => h.GetString()!.Contains("[RootSignature", StringComparison.Ordinal));

        JsonElement ambiguous = Parse(await StaticShaderProfilingTools.StaticProfile(_session, _jobs, "Xe2",
            sources: [new StaticShaderSource("cs_6_0", Hlsl: "[numthreads(1,1,1)] void main() {}")], waitSeconds: 300));
        Assert.Equal("failed", ambiguous.GetProperty("status").GetString());
        Assert.Equal("invalid_arguments", ambiguous.GetProperty("error").GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task CapturedShadersProfileOrExplainMissingHlsl()
    {
        Skip.IfNot(TestArtifacts.PixInstalled && TestArtifacts.Capture is not null && TestArtifacts.AnalysisEnabled, "Set PIX_TEST_CAPTURE and PIX_TEST_ANALYSIS=1 to replay a capture.");
        string handle = Parse(await GpuCaptureTools.Open(_session, TestArtifacts.Capture!)).GetProperty("handle").GetString()!;
        JsonElement events = Parse(await GpuCaptureTools.Events(_session, handle, kind: "work", limit: 1));
        EventRef eventRef = JsonSerializer.Deserialize<EventRef>(events.GetProperty("items")[0].GetProperty("eventRef").GetRawText(), Json.Options)!;
        JsonElement job = Parse(await StaticShaderProfilingTools.StaticProfile(_session, _jobs, "Xe2-HPG", shaderRef: new ShaderRef(eventRef, 0), waitSeconds: 600));
        if (job.GetProperty("status").GetString() == "succeeded")
        {
            JsonElement result = _session.Results.ReadElement(job.GetProperty("resultRef").GetString()!, "", FullResultBytes);
            Assert.Equal("capture", result.GetProperty("source").GetString());
            Assert.True(result.GetProperty("coverage").GetProperty("pipelineStateFromCapture").GetBoolean());
        }
        else
        {
            JsonElement error = job.GetProperty("error");
            Assert.True(error.GetProperty("code").GetString() == "unavailable_shader_data", job.GetRawText());
            Assert.Contains(error.GetProperty("nextCalls").EnumerateArray(), c => c.GetProperty("tool").GetString() == "pix_gpu_shader_diagnostics");
        }
        await SessionTools.Close(_session, handle);
    }
}
