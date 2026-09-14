using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PixMcp.Pix;
using PixMcp.Pix.Handles;
using PixMcp.Tools;
using Xunit;

namespace PixMcp.Tests;

/// <summary>
/// End-to-end tests against the real PIX API. They run only when PIX_TEST_CAPTURE points at a
/// .wpix GPU capture (and PIX Preview is installed); otherwise they are skipped.
/// </summary>
[Collection(GpuReplayCollection.Name)]
public class IntegrationTests : IDisposable
{
    private readonly PixWorker _worker = new();
    private readonly PixSession _session;
    private readonly JobManager _jobs;

    public IntegrationTests()
    {
        _session = new PixSession(_worker, NullLogger<PixSession>.Instance);
        _jobs = new JobManager(_worker, _session);
    }

    private static string? CapturePath => TestArtifacts.Capture;

    private static bool Available => TestArtifacts.PixInstalled && CapturePath is not null;

    [SkippableFact]
    public async Task FactoryLoads()
    {
        Skip.IfNot(PixDiscovery.InstallDir is not null, "PIX Preview not installed");
        JsonElement info = Parse(await SessionTools.Info(_session, _jobs, probe: true));
        Assert.True(info.GetProperty("pix").GetProperty("apiLoaded").GetBoolean());
        Assert.False(info.GetProperty("pix").TryGetProperty("probeError", out _));
        JsonElement pix = info.GetProperty("pix");
        Assert.Equal("match", pix.GetProperty("compatibility").GetProperty("state").GetString());
        Assert.Equal(pix.GetProperty("installVersion").GetProperty("build").GetString(), pix.GetProperty("builtAgainst").GetProperty("build").GetString());
        Assert.Equal(pix.GetProperty("build").GetString(), pix.GetProperty("loadedFileVersion").GetString());
        Assert.True(pix.GetProperty("loggerAttached").GetBoolean());
        Assert.False(pix.TryGetProperty("loggerError", out _));
        Assert.Contains(pix.GetProperty("apiSurface").EnumerateArray(), probe => probe.GetProperty("feature").GetString() == "bulkTimingReadback" && probe.GetProperty("present").GetBoolean());
        Assert.DoesNotContain(pix.GetProperty("apiSurface").EnumerateArray(), probe => probe.GetProperty("drift").GetBoolean());
    }

    [SkippableFact]
    public async Task OpensCaptureAndListsEvents()
    {
        Skip.IfNot(Available, "Set PIX_TEST_CAPTURE to a .wpix file");
        JsonElement opened = Parse(await GpuCaptureTools.Open(_session, CapturePath!));
        string handle = opened.GetProperty("handle").GetString()!;
        Assert.StartsWith("gpu-", handle);
        Assert.True(opened.GetProperty("queues").GetArrayLength() > 0);
        Assert.All(opened.GetProperty("queues").EnumerateArray(), queue => Assert.False(string.IsNullOrEmpty(queue.GetProperty("vendor").GetString())));

        JsonElement events = Parse(await GpuCaptureTools.Events(_session, handle, queueIndex: 0, limit: 5));
        Assert.True(events.GetProperty("total").GetInt64() > 0);
        Assert.True(events.GetProperty("count").GetInt32() <= 5);

        JsonElement draws = Parse(await GpuCaptureTools.Events(_session, handle, kind: "work", limit: 3));
        AssertPage(draws, 3);
        Assert.All(draws.GetProperty("items").EnumerateArray(), row =>
        {
            string name = row.GetProperty("name").GetString()!;
            string api = row.TryGetProperty("apiCallData", out JsonElement call) ? call.GetString()! : string.Empty;
            Assert.Contains(new[] { name, api }, s => s.StartsWith("Draw", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("Dispatch", StringComparison.OrdinalIgnoreCase));
        });

        JsonElement closed = Parse(await SessionTools.Close(_session, handle));
        Assert.Equal(handle, closed.GetProperty("closed").GetString());
    }

    [SkippableFact]
    public async Task StartsAnalysisAndCollectsTiming()
    {
        Skip.IfNot(Available && Environment.GetEnvironmentVariable("PIX_TEST_ANALYSIS") == "1", "Set PIX_TEST_ANALYSIS=1 to replay on the GPU");
        JsonElement opened = Parse(await GpuCaptureTools.Open(_session, CapturePath!));
        string handle = opened.GetProperty("handle").GetString()!;

        JsonElement started = Parse(await AnalysisTools.Start(_session, _jobs, handle, waitSeconds: 600));
        Assert.Equal("succeeded", started.GetProperty("status").GetString());

        JsonElement analysis = Parse(await AnalysisTools.Status(_session, handle));
        JsonElement replayVendor = analysis.GetProperty("replayVendor");
        string? adapterName = replayVendor.GetProperty("adapterName").GetString();
        Assert.Equal(GpuVendors.Name(GpuVendors.FromAdapterName(adapterName)), replayVendor.GetProperty("vendor").GetString());
        Assert.Equal("replayAdapter", replayVendor.GetProperty("source").GetString());
        JsonElement captureVendor = analysis.GetProperty("captureVendor");
        Assert.NotEqual("unknown", captureVendor.GetProperty("vendor").GetString());
        Assert.Contains(captureVendor.GetProperty("source").GetString(), new[] { "captureFileInfo", "captureQueueAdapter" });

        JsonElement timing;
        using (ServerOptions.Override(ServerOptions.With(verifyBulkReadback: true)))
            timing = Parse(await CountersTools.TimingEvents(_session, _jobs, handle, limit: 5, waitSeconds: 600));
        AssertPage(timing, 5);
        JsonElement readback = timing.GetProperty("extra").GetProperty("readback");
        Assert.True(readback.EnumerateObject().Any(), "every queue reports how its timing rows were read");
        foreach (JsonProperty queueReadback in readback.EnumerateObject())
        {
            Assert.Contains(queueReadback.Value.GetProperty("readback").GetString(), new[] { "bulk", "perEvent" });
            if (queueReadback.Value.GetProperty("readback").GetString() == "bulk")
            {
                JsonElement verify = queueReadback.Value.GetProperty("verify");
                Assert.Equal(0, verify.GetProperty("mismatches").GetInt32());
                Assert.True(verify.GetProperty("sampled").GetInt32() > 0);
            }
        }
        ulong[] durations = timing.GetProperty("items").EnumerateArray()
            .Select(row => row.GetProperty("eop").GetProperty("ns").GetUInt64()).ToArray();
        Assert.Equal(durations.OrderByDescending(value => value), durations);
        Assert.All(timing.GetProperty("items").EnumerateArray(), row =>
        {
            double percent = row.GetProperty("eop").GetProperty("percentOfQueueSpan").GetDouble();
            Assert.InRange(percent, 0, 100);
            Assert.False(string.IsNullOrEmpty(row.GetProperty("kind").GetString()));
        });

        JsonElement tree = Parse(await CountersTools.TimingTreeTool(_session, _jobs, handle, waitSeconds: 600));
        JsonElement queue = tree.GetProperty("queue");
        Assert.True(queue.GetProperty("busyNs").GetUInt64() <= queue.GetProperty("spanNs").GetUInt64());
        JsonElement frame = tree.GetProperty("children").EnumerateArray().Single(c => c.GetProperty("name").GetString()!.EndsWith("Frame"));
        Assert.NotEqual("untimed", frame.GetProperty("semantics").GetString());
        Assert.InRange(frame.GetProperty("inclusive").GetProperty("percentOfQueueSpan").GetDouble(), 0, 100);

        JsonElement overview = Parse(await InvestigationTools.Overview(_session, _jobs, handle, waitSeconds: 600));
        Assert.True(overview.GetProperty("topDraws").GetArrayLength() >= 3);
        Assert.All(overview.GetProperty("topDraws").EnumerateArray(), row => Assert.Contains(row.GetProperty("kind").GetString(), new[] { "draw", "dispatch", "executeIndirect" }));
        Assert.Contains(overview.GetProperty("topPasses").EnumerateArray(), row => row.GetProperty("name").GetString()!.EndsWith("Frame"));
        Assert.DoesNotContain(overview.GetProperty("topPasses").EnumerateArray(), row => row.GetProperty("name").GetString()!.EndsWith("Hello PixMcp!!!"));
        Assert.True(overview.GetProperty("queues")[0].GetProperty("kinds").GetProperty("label").GetInt32() >= 1);
        Assert.Equal(captureVendor.GetProperty("vendor").GetString(), overview.GetProperty("capture").GetProperty("adapter").GetProperty("vendor").GetString());
        Assert.Equal(1, overview.GetProperty("capture").GetProperty("frames").GetProperty("count").GetInt32());
        Assert.Equal(overview.GetProperty("topDraws").GetArrayLength(), overview.GetProperty("histogram").GetProperty("buckets").EnumerateArray().Sum(b => b.GetProperty("count").GetInt32()));
        JsonElement insights = overview.GetProperty("insights");
        Assert.Equal(insights.GetArrayLength(), insights.EnumerateArray().Select(i => i.GetProperty("id").GetString()).Distinct().Count());
        Assert.All(overview.GetProperty("topPasses").EnumerateArray(), row => Assert.True(row.GetProperty("workCount").GetInt32() >= 0));

        // Queue overlap and bubbles: two timed GPU queues give one pair; busy plus every gap fills each span; per-cause totals reconcile.
        Assert.Equal(1, overview.GetProperty("overlap").GetProperty("pairs").GetArrayLength());
        JsonElement overlapReport = Parse(await QueueAnalysisTools.QueueOverlap(_session, _jobs, handle, minGapNs: 0, waitSeconds: 600));
        Assert.Equal(1, overlapReport.GetProperty("pairs").GetArrayLength());
        Assert.Contains(overlapReport.GetProperty("unavailable").EnumerateArray(), u => u.GetProperty("reason").GetString() == "noTimedEvents");
        foreach (JsonElement busyQueue in overlapReport.GetProperty("queues").EnumerateArray())
            Assert.Equal(busyQueue.GetProperty("spanEndNs").GetUInt64() - busyQueue.GetProperty("spanStartNs").GetUInt64(),
                busyQueue.GetProperty("busy").GetProperty("ns").GetUInt64() + busyQueue.GetProperty("bubbleTotal").GetProperty("ns").GetUInt64());
        JsonElement gaps = Parse(await QueueAnalysisTools.Bubbles(_session, _jobs, handle, minGapNs: 0, limit: 1000, waitSeconds: 600));
        Assert.Equal(gaps.GetProperty("total").GetInt64(), gaps.GetProperty("items").GetArrayLength());
        Assert.Equal(gaps.GetProperty("perCause").EnumerateArray().Sum(c => (decimal)c.GetProperty("total").GetProperty("ns").GetUInt64()),
            gaps.GetProperty("items").EnumerateArray().Sum(b => (decimal)b.GetProperty("duration").GetProperty("ns").GetUInt64()));
        if (gaps.GetProperty("total").GetInt64() > 0)
        {
            EventRef afterGap = JsonSerializer.Deserialize<EventRef>(gaps.GetProperty("items")[0].GetProperty("after").GetRawText(), Json.Options)!;
            JsonElement inspectedAfterGap = Parse(await InspectionTools.InspectEvent(_session, _jobs, afterGap, [InspectionSection.timing], waitSeconds: 600));
            Assert.Equal(afterGap.EventIndex, inspectedAfterGap.GetProperty("eventRef").GetProperty("eventIndex").GetUInt32());
        }

        // Resources: estimated sizes, the capture-wide use index with access classes, and the back buffer's timeline with its barriers.
        JsonElement bySize = Parse(await ResourceTools.Resources(_session, _jobs, handle, sortBy: "estimatedBytes", limit: 10));
        JsonElement backBuffer = bySize.GetProperty("items").EnumerateArray().First(r => r.GetProperty("dimension").GetString() == "TEXTURE2D");
        Assert.Equal((640UL, 1_228_800UL), (backBuffer.GetProperty("width").GetUInt64(), backBuffer.GetProperty("estimatedBytes").GetUInt64()));
        EventRef trianglePass = JsonSerializer.Deserialize<EventRef>(overview.GetProperty("topPasses").EnumerateArray()
            .First(p => p.GetProperty("name").GetString()!.EndsWith("Triangle pass")).GetProperty("eventRef").GetRawText(), Json.Options)!;
        JsonElement passTargets = Parse(await ResourceTools.Resources(_session, _jobs, handle, scope: trianglePass, usedAs: "renderTarget", waitSeconds: 600));
        Assert.Equal(backBuffer.GetProperty("apiObjectId").GetString(), Assert.Single(passTargets.GetProperty("items").EnumerateArray()).GetProperty("apiObjectId").GetString());
        ResourceRef backBufferRef = JsonSerializer.Deserialize<ResourceRef>(backBuffer.GetProperty("resourceRef").GetRawText(), Json.Options)!;
        JsonElement writes = Parse(await ResourceTools.ResourceUses(_session, _jobs, backBufferRef, access: "write", waitSeconds: 600));
        Assert.True(writes.GetProperty("total").GetInt64() >= 1);
        Assert.All(writes.GetProperty("items").EnumerateArray(), u => Assert.Equal("RENDER_TARGET_VIEW", u.GetProperty("viewType").GetString()));
        JsonElement timeline = Parse(await ResourceTools.ResourceTimelineTool(_session, _jobs, backBufferRef, waitSeconds: 600));
        Assert.True(timeline.GetProperty("summary").GetProperty("writes").GetInt32() >= 1);
        Assert.Equal(2, timeline.GetProperty("summary").GetProperty("barriers").GetInt32());
        Assert.Contains(timeline.GetProperty("rows").EnumerateArray(), r => r.GetProperty("access").GetString() == "barrier" && r.GetProperty("stateAfter").GetString() == "RENDER_TARGET");
        Assert.DoesNotContain(timeline.GetProperty("insights").EnumerateArray(), i => i.GetProperty("id").GetString() == "written_never_read");

        // Rollups: kind sums equal the per-kind timing rows, and marker depth 1 reconciles to the queue's sum of roots.
        JsonElement byKind = Parse(await RollupTools.Rollup(_session, _jobs, handle, groupBy: "kind", queueIndex: 0, waitSeconds: 600));
        JsonElement allTiming = Parse(await CountersTools.TimingEvents(_session, _jobs, handle, queueIndex: 0, limit: 1000, waitSeconds: 600));
        Dictionary<string, ulong> timingKindSums = allTiming.GetProperty("items").EnumerateArray().GroupBy(r => r.GetProperty("kind").GetString()!)
            .ToDictionary(g => g.Key, g => g.Aggregate(0UL, (sum, r) => sum + r.GetProperty("eop").GetProperty("ns").GetUInt64()));
        Assert.NotEmpty(byKind.GetProperty("items").EnumerateArray());
        foreach (JsonElement group in byKind.GetProperty("items").EnumerateArray())
            if (group.TryGetProperty("sum", out JsonElement kindSum) && kindSum.ValueKind == JsonValueKind.Object)
                Assert.Equal(timingKindSums[group.GetProperty("key").GetString()!], kindSum.GetProperty("ns").GetUInt64());
        JsonElement depth1 = Parse(await RollupTools.Rollup(_session, _jobs, handle, groupBy: "markerDepth", queueIndex: 0, waitSeconds: 600));
        Assert.True(depth1.GetProperty("reconciles").GetBoolean());
        Assert.Equal(queue.GetProperty("sumOfRootsNs").GetUInt64(), depth1.GetProperty("items").EnumerateArray()
            .Aggregate(0UL, (sum, r) => sum + (r.TryGetProperty("sum", out JsonElement s) && s.ValueKind == JsonValueKind.Object ? s.GetProperty("ns").GetUInt64() : 0)));
        Assert.True(Parse(await RollupTools.Rollup(_session, _jobs, handle, groupBy: "api", format: "table", waitSeconds: 600)).GetProperty("rows").GetArrayLength() > 0);

        // Pipelines and shaders rank by replay time; a shaderKey from the inventory navigates to its uses.
        JsonElement pipelines = Parse(await RollupTools.Pipelines(_session, _jobs, handle, waitSeconds: 600));
        ulong[] pipelineTimes = pipelines.GetProperty("items").EnumerateArray()
            .Select(p => p.TryGetProperty("gpuTime", out JsonElement t) && t.ValueKind == JsonValueKind.Object ? t.GetProperty("ns").GetUInt64() : 0).ToArray();
        Assert.NotEmpty(pipelineTimes);
        Assert.Equal(pipelineTimes.OrderByDescending(v => v), pipelineTimes);
        // The fxc-built fixture exposes no shader hashes: its work events have no pipeline identity and group as one occurrence row.
        foreach (JsonElement pipeline in pipelines.GetProperty("items").EnumerateArray())
            Assert.Equal(pipeline.TryGetProperty("psoKey", out JsonElement key) && key.ValueKind == JsonValueKind.String ? "psoKey" : "occurrence",
                pipeline.GetProperty("identity").GetString());
        Assert.Equal(overview.GetProperty("queues").EnumerateArray().Sum(q => q.GetProperty("kinds").GetProperty("work").GetInt32()),
            pipelines.GetProperty("items").EnumerateArray().Sum(p => p.GetProperty("useCount").GetInt32()));
        JsonElement shadersByTime = Parse(await ShaderInventoryTools.Shaders(_session, _jobs, handle, sortBy: "gpuTime", waitSeconds: 600));
        double[] shaderTimes = shadersByTime.GetProperty("items").EnumerateArray()
            .Select(i => i.TryGetProperty("gpuTimeMs", out JsonElement t) && t.ValueKind == JsonValueKind.Number ? t.GetDouble() : 0).ToArray();
        Assert.Equal(shaderTimes.OrderByDescending(v => v), shaderTimes);
        Assert.Contains(shaderTimes, t => t > 0);
        string? shaderKey = shadersByTime.GetProperty("items").EnumerateArray()
            .Select(i => i.TryGetProperty("shaderKey", out JsonElement k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null).FirstOrDefault(k => k is not null);
        if (shaderKey is not null)
        {
            JsonElement usesByKey = Parse(await ShaderInventoryTools.Uses(_session, _jobs, shaderKey: shaderKey, handle: handle, waitSeconds: 600));
            Assert.True(usesByKey.GetProperty("total").GetInt64() > 0);
            Assert.Equal(shaderKey, usesByKey.GetProperty("shaderKey").GetString());
        }
        else
        {
            PixToolException unknownKey = await Assert.ThrowsAsync<PixToolException>(() => ShaderInventoryTools.Uses(_session, _jobs, shaderKey: "hash:PS:00", handle: handle, waitSeconds: 600));
            Assert.Equal(PixErrors.Codes.InvalidReference, unknownKey.Detail.Code);
        }

        // Event histograms bucket with the overview's kind classifier; count mode matches the page total.
        JsonElement histogram = Parse(await GpuCaptureTools.Events(_session, handle, queueIndex: 0, mode: "histogram"));
        JsonElement kinds = overview.GetProperty("queues")[0].GetProperty("kinds");
        Assert.NotEmpty(histogram.GetProperty("buckets").EnumerateArray());
        foreach (JsonElement bucket in histogram.GetProperty("buckets").EnumerateArray())
            Assert.Equal(kinds.GetProperty(bucket.GetProperty("key").GetString()!).GetInt32(), bucket.GetProperty("count").GetInt32());
        Assert.Equal(Parse(await GpuCaptureTools.Events(_session, handle, queueIndex: 0, limit: 1)).GetProperty("total").GetInt64(),
            Parse(await GpuCaptureTools.Events(_session, handle, queueIndex: 0, mode: "count")).GetProperty("total").GetInt64());

        // Occupancy after timing: the timing pass supplies it when PIX reports data, else one standalone replay; the event adds its own points.
        EventRef drawRef = JsonSerializer.Deserialize<EventRef>(overview.GetProperty("topDraws")[0].GetProperty("eventRef").GetRawText(), Json.Options)!;
        JsonElement occupancy = Parse(await CountersTools.Occupancy(_session, _jobs, handle, maxPoints: 5, waitSeconds: 600, eventRef: drawRef));
        if (!(occupancy.TryGetProperty("unavailable", out JsonElement occupancyUnavailable) && occupancyUnavailable.GetBoolean()))
        {
            string source = occupancy.GetProperty("source").GetString()!;
            Assert.Contains(source, new[] { "timingPass", "standaloneReplay" });
            if (occupancy.GetProperty("timingPassProbe").ValueKind == JsonValueKind.True) Assert.Equal("timingPass", source);
            Assert.All(occupancy.GetProperty("series").EnumerateArray(), s =>
            {
                if (s.GetProperty("peakPercent").ValueKind == JsonValueKind.Number) Assert.InRange(s.GetProperty("peakPercent").GetDouble(), 0, 100);
            });
            Assert.Equal(occupancy.GetProperty("series").GetArrayLength(), occupancy.GetProperty("event").GetProperty("series").GetArrayLength());
            JsonElement inspectedOccupancy = Parse(await InspectionTools.InspectEvent(_session, _jobs, drawRef, [InspectionSection.occupancy], waitSeconds: 600)).GetProperty("occupancy");
            Assert.Equal("available", inspectedOccupancy.GetProperty("state").GetString());
        }

        await SessionTools.Close(_session, handle);
    }

    [SkippableFact]
    public async Task PixtoolPreviewsExactEventsAndCutsASubcapture()
    {
        Skip.IfNot(Available && TestArtifacts.AnalysisEnabled, "Set PIX_TEST_CAPTURE and PIX_TEST_ANALYSIS=1 to replay on the GPU");
        string handle = Parse(await GpuCaptureTools.Open(_session, CapturePath!)).GetProperty("handle").GetString()!;
        JsonElement[] items = Parse(await GpuCaptureTools.Events(_session, handle, queueIndex: 0, limit: 100)).GetProperty("items").EnumerateArray().ToArray();
        EventRef RefOf(Func<string, bool> name) => JsonSerializer.Deserialize<EventRef>(
            items.First(e => name(e.GetProperty("name").GetString()!)).GetProperty("eventRef").GetRawText(), Json.Options)!;

        JsonElement draw = await PreviewResult(handle, RefOf(n => n == "DrawInstanced"));
        Assert.Equal("verified", draw.GetProperty("selection").GetProperty("globalIdMapping").GetString());
        Assert.False(draw.TryGetProperty("warning", out _));
        Assert.Equal((640u, 480u), (draw.GetProperty("width").GetUInt32(), draw.GetProperty("height").GetUInt32()));
        JsonElement clear = await PreviewResult(handle, RefOf(n => n == "ClearRenderTargetView"));
        Assert.NotEqual(PreviewTools.GetArtifact(_session, draw.GetProperty("artifactRef").GetString()!),
            PreviewTools.GetArtifact(_session, clear.GetProperty("artifactRef").GetString()!));
        Assert.Equal("supported", Parse(await GpuCaptureTools.GetInfo(_session, handle)).GetProperty("capabilities").GetProperty("exactEventPreview").GetProperty("state").GetString());

        string folder = Directory.CreateTempSubdirectory("pixmcp-subcapture-").FullName;
        string? derived = null;
        try
        {
            JsonElement job = Parse(await SubcaptureTools.Subcapture(_session, _jobs, handle, scope: RefOf(n => n.EndsWith("Triangle pass", StringComparison.Ordinal)),
                outPath: Path.Combine(folder, "triangle pass.wpix"), waitSeconds: 300));
            Assert.True(job.GetProperty("status").GetString() == "succeeded", job.GetRawText());
            JsonElement result = _session.Results.ReadElement(job.GetProperty("resultRef").GetString()!, "", 16 * 1024 * 1024);
            JsonElement origin = result.GetProperty("derivedFrom");
            Assert.True(origin.GetProperty("firstGpuId").GetUInt32() <= origin.GetProperty("lastGpuId").GetUInt32());
            derived = result.GetProperty("gpuCapture").GetProperty("handle").GetString()!;
            Assert.Equal(handle, Parse(await GpuCaptureTools.GetInfo(_session, derived)).GetProperty("derivedFrom").GetProperty("sourceHandle").GetString());
            Assert.Equal(3, Parse(await GpuCaptureTools.Events(_session, derived, queueIndex: 0, kind: "draw", mode: "count")).GetProperty("total").GetInt32());
        }
        finally
        {
            if (derived is not null) await SessionTools.Close(_session, derived);
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
        await SessionTools.Close(_session, handle);
    }

    private async Task<JsonElement> PreviewResult(string handle, EventRef eventRef)
    {
        JsonElement job = Parse(await PreviewTools.Preview(_session, _jobs, handle, eventRef: eventRef, waitSeconds: 300));
        Assert.True(job.GetProperty("status").GetString() == "succeeded", job.GetRawText());
        return _session.Results.ReadElement(job.GetProperty("resultRef").GetString()!, "", 16 * 1024 * 1024);
    }

    [SkippableFact]
    public async Task ExplicitAnalysisFlagsAreDecodedInStatusAndProvenance()
    {
        Skip.IfNot(Available && TestArtifacts.AnalysisEnabled, "Set PIX_TEST_CAPTURE and PIX_TEST_ANALYSIS=1 to replay on the GPU");
        string handle = Parse(await GpuCaptureTools.Open(_session, CapturePath!)).GetProperty("handle").GetString()!;
        JsonElement start = Parse(await AnalysisTools.Start(_session, _jobs, handle, flags: ["ignore_incompatibilities", "PIX_ANALYSIS_FLAG_DISABLE_GPU_PLUGINS"], waitSeconds: 600));
        Assert.True(start.GetProperty("status").GetString() == "succeeded", start.GetRawText());

        GpuCaptureHandle capture = _session.Get<GpuCaptureHandle>(handle);
        JsonElement status = Parse(Json.Serialize(capture.AnalysisStatus()));
        string[] expected = ["IGNORE_INCOMPATIBILITIES", "DISABLE_GPU_PLUGINS"];
        Assert.Equal("explicit", status.GetProperty("flagsSource").GetString());
        Assert.Equal(expected, status.GetProperty("flagsDecoded").GetProperty("names").EnumerateArray().Select(n => n.GetString()));
        Assert.Equal(expected, capture.Provenance().FlagsDecoded!.Names);
        await SessionTools.Close(_session, handle);
    }

    [SkippableFact]
    public async Task LiveShaderProfileSummarisesOrCachesAnUnsupportedMarker()
    {
        Skip.IfNot(Available && TestArtifacts.AnalysisEnabled, "Set PIX_TEST_CAPTURE and PIX_TEST_ANALYSIS=1 to replay on the GPU");
        string handle = Parse(await GpuCaptureTools.Open(_session, CapturePath!)).GetProperty("handle").GetString()!;
        JsonElement work = Parse(await GpuCaptureTools.Events(_session, handle, kind: "work", limit: 1));
        EventRef scope = JsonSerializer.Deserialize<EventRef>(work.GetProperty("items")[0].GetProperty("eventRef").GetRawText(), Json.Options)!;
        JsonElement first = Parse(await ShaderProfilingTools.Profile(_session, _jobs, handle, scope: scope, waitSeconds: 600));
        Assert.True(first.GetProperty("status").GetString() == "succeeded", first.GetRawText());
        JsonElement result = _session.Results.ReadElement(first.GetProperty("resultRef").GetString()!, "", 64 * 1024 * 1024);
        if (result.TryGetProperty("unavailable", out JsonElement unavailable) && unavailable.GetBoolean())
        {
            Assert.Contains(result.GetProperty("state").GetString(), new[] { "unsupported", "declined" });
            Assert.False(string.IsNullOrEmpty(result.GetProperty("vendor").GetString()));
            Assert.Contains(result.GetProperty("nextCalls").EnumerateArray(), c => c.GetProperty("tool").GetString() == "pix_shader_targets");
        }
        else
        {
            JsonElement totals = result.GetProperty("totals");
            Assert.True(totals.GetProperty("stalledSamples").GetUInt64() <= totals.GetProperty("totalSamples").GetUInt64());
            foreach (JsonElement shader in result.GetProperty("shaders").EnumerateArray())
            {
                JsonElement samples = shader.GetProperty("samples");
                Assert.Equal(samples.GetProperty("total").GetUInt64() - samples.GetProperty("stalled").GetUInt64(), samples.GetProperty("issuing").GetUInt64());
            }
        }
        // An identical request joins the retained job instead of replaying again.
        JsonElement second = Parse(await ShaderProfilingTools.Profile(_session, _jobs, handle, scope: scope, waitSeconds: 600));
        Assert.Equal(first.GetProperty("jobId").GetString(), second.GetProperty("jobId").GetString());
        await SessionTools.Close(_session, handle);
    }

    [SkippableFact]
    public async Task GpuSqlMaterialisesTheCaptureAndAnswersGuardedQueries()
    {
        Skip.IfNot(Available && Environment.GetEnvironmentVariable("PIX_TEST_ANALYSIS") == "1", "Set PIX_TEST_ANALYSIS=1 to replay on the GPU");
        string handle = Parse(await GpuCaptureTools.Open(_session, CapturePath!)).GetProperty("handle").GetString()!;

        // A statement over an unpopulated family names the populate call instead of replaying.
        PixToolException missing = await Assert.ThrowsAsync<PixToolException>(() => GpuSqlTools.Sql(_session, _jobs, handle, sql: "SELECT COUNT(*) FROM timing"));
        Assert.Equal(PixErrors.Codes.SqlTablesNotPopulated, missing.Detail.Code);
        Assert.Contains(missing.Detail.NextCalls, c => c.Tool == "pix_gpu_sql_populate");
        Assert.Empty(_jobs.All.Where(j => j.Kind == "gpu-sql-populate"));

        JsonElement populated = Parse(await GpuSqlTools.Populate(_session, _jobs, handle, tables: ["all"], waitSeconds: 600));
        Assert.True(populated.GetProperty("status").GetString() == "succeeded", populated.GetRawText());

        JsonElement work = Parse(await GpuSqlTools.Sql(_session, _jobs, handle, sql: "SELECT COUNT(*) AS n FROM v_work", waitSeconds: 600));
        Assert.Equal("gpusql", work.GetProperty("source").GetString());
        JsonElement overview = Parse(await InvestigationTools.Overview(_session, _jobs, handle, waitSeconds: 600));
        Assert.Equal(overview.GetProperty("queues").EnumerateArray().Sum(q => q.GetProperty("kinds").GetProperty("work").GetInt32()), work.GetProperty("rows")[0][0].GetInt32());
        JsonElement timingRows = Parse(await GpuSqlTools.Sql(_session, _jobs, handle, sql: "SELECT COUNT(*) FROM timing WHERE eop_ns IS NOT NULL", waitSeconds: 600));
        JsonElement timingEvents = Parse(await CountersTools.TimingEvents(_session, _jobs, handle, limit: 1, waitSeconds: 600));
        Assert.Equal(timingEvents.GetProperty("total").GetInt64(), timingRows.GetProperty("rows")[0][0].GetInt64());
        JsonElement passes = Parse(await GpuSqlTools.Sql(_session, _jobs, handle, query: "top_passes", waitSeconds: 600));
        Assert.Contains(passes.GetProperty("rows").EnumerateArray(), row => row[2].GetString()!.EndsWith("Frame"));
        JsonElement tables = Parse(await GpuSqlTools.TablesTool(_session, handle));
        Assert.All(tables.GetProperty("families").EnumerateArray().Where(f => f.GetProperty("family").GetString() is "core" or "timing" or "shaders" or "resources"),
            f => Assert.Equal("ready", f.GetProperty("state").GetString()));

        string csv = Path.Combine(Path.GetTempPath(), $"pixmcp-gpusql-{Guid.NewGuid():N}.csv");
        try
        {
            JsonElement exported = Parse(await GpuSqlTools.Export(_session, _jobs, handle, csv, sql: "SELECT queue_index, event_index, name FROM events ORDER BY queue_index, event_index", waitSeconds: 600));
            Assert.Equal("succeeded", exported.GetProperty("status").GetString());
            Assert.Equal("queue_index,event_index,name", File.ReadLines(csv).First());
        }
        finally { File.Delete(csv); }

        // After the analysis stops, replay families still answer and say they are stale.
        await AnalysisTools.Stop(_session, handle);
        JsonElement stale = Parse(await GpuSqlTools.Sql(_session, _jobs, handle, sql: "SELECT COUNT(*) FROM timing", waitSeconds: 600));
        Assert.Contains(stale.GetProperty("provenance").GetProperty("tableStates").EnumerateArray(),
            state => state.GetProperty("family").GetString() == "timing" && state.GetProperty("stale").GetBoolean());
        await SessionTools.Close(_session, handle);
    }

    private static ModelContextProtocol.Protocol.CallToolRequestParams Request(string tool, object arguments) => new()
    {
        Name = tool,
        Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(arguments))!,
    };

    [SkippableFact]
    public async Task DeferredEventPagesOutlineFilterAndExpireWithTheirOrigin()
    {
        Skip.IfNot(Available, "Set PIX_TEST_CAPTURE to a .wpix file");
        JsonElement opened = Parse(await GpuCaptureTools.Open(_session, CapturePath!));
        string handle = opened.GetProperty("handle").GetString()!;
        string json;
        using (StructuredToolResults.WithRequest(Request("pix_gpu_events", new { handle, limit = 1000 })))
            json = await GpuCaptureTools.Events(_session, handle, limit: 1000);
        JsonElement deferred = Parse(json);
        Assert.True(deferred.GetProperty("deferred").GetBoolean(), "the 1000-row event page should exceed the inline budget");
        string resultRef = deferred.GetProperty("resultRef").GetString()!;
        Assert.Equal("outline", deferred.GetProperty("nextCalls")[0].GetProperty("arguments").GetProperty("mode").GetString());

        JsonElement outline = Parse(ResultTools.Read(_session, resultRef, mode: "outline"));
        Assert.Equal("object", outline.GetProperty("kind").GetString());
        JsonElement items = outline.GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("key").GetString() == "items");
        Assert.Equal("array", items.GetProperty("kind").GetString());
        Assert.True(items.GetProperty("total").GetInt32() > 25);
        Assert.Contains("name", items.GetProperty("itemKeys").EnumerateArray().Select(k => k.GetString()));

        JsonElement filtered = Parse(ResultTools.Read(_session, resultRef, "/items", limit: 1000, fields: ["name", "eventRef/eventIndex"],
            where: [new WhereClause("name", "startsWith", JsonSerializer.SerializeToElement("Draw"))]));
        Assert.True(filtered.GetProperty("total").GetInt32() >= 3);
        Assert.Equal(filtered.GetProperty("total").GetInt32(), filtered.GetProperty("projection").GetProperty("matched").GetInt32());
        Assert.All(filtered.GetProperty("value").EnumerateArray(), row =>
        {
            Assert.StartsWith("Draw", row.GetProperty("name").GetString());
            Assert.True(row.TryGetProperty("eventRef/eventIndex", out _));
            Assert.Equal(2, row.EnumerateObject().Count());
        });

        await SessionTools.Close(_session, handle);
        PixToolException expired = Assert.Throws<PixToolException>(() => ResultTools.Read(_session, resultRef));
        Assert.Equal("result_expired", expired.Detail.Code);
        ToolCallDto origin = Assert.Single(expired.Detail.NextCalls);
        Assert.Equal("pix_gpu_events", origin.Tool);
        Assert.Equal(1000, ((JsonElement)origin.Arguments).GetProperty("limit").GetInt32());
    }

    [SkippableFact]
    public async Task OverlappingStartsShareOneJobAndJoinedInspectionsQueueNoSecondReplay()
    {
        Skip.IfNot(Available && TestArtifacts.AnalysisEnabled, "Set PIX_TEST_CAPTURE and PIX_TEST_ANALYSIS=1 to replay on the GPU");
        JsonElement opened = Parse(await GpuCaptureTools.Open(_session, CapturePath!));
        string handle = opened.GetProperty("handle").GetString()!;

        // Two overlapping starts: one job, and a start with different settings is refused while it runs.
        Task<string> first, second;
        using (StructuredToolResults.WithRequest(Request("pix_gpu_analysis_start", new { handle })))
        {
            first = AnalysisTools.Start(_session, _jobs, handle);
            second = AnalysisTools.Start(_session, _jobs, handle);
        }
        string firstJob = Parse(await first).GetProperty("jobId").GetString()!;
        Assert.Equal(firstJob, Parse(await second).GetProperty("jobId").GetString());
        PixToolException conflict = await Assert.ThrowsAsync<PixToolException>(() => AnalysisTools.Start(_session, _jobs, handle, flags: new[] { "PIX_ANALYSIS_ENABLE_DEBUG_LAYER" }));
        Assert.Equal(PixErrors.Codes.AnalysisSettingsConflict, conflict.Detail.Code);
        JsonElement waited = Parse(await SessionTools.JobWait(_jobs, firstJob, 600));
        Assert.Equal("succeeded", waited.GetProperty("status").GetString());
        Assert.Equal("available", waited.GetProperty("resultState").GetString());
        Assert.True(_session.Results.ReadElement(waited.GetProperty("resultRef").GetString()!).GetProperty("analysis").GetProperty("started").GetBoolean());
        Assert.Equal("pix_gpu_analysis_start", waited.GetProperty("origin").GetProperty("tool").GetString());

        // A cold [Timing] inspection starts the timing replay; [Timing, Bindings] joins it instead of queueing a second one.
        JsonElement draw = Parse(await GpuCaptureTools.Events(_session, handle, kind: "draw", limit: 1)).GetProperty("items")[0].GetProperty("eventRef");
        var eventRef = new EventRef(handle, draw.GetProperty("queueIndex").GetInt32(), draw.GetProperty("eventIndex").GetUInt32());
        JsonElement partial = Parse(await InspectionTools.InspectEvent(_session, _jobs, eventRef, [InspectionSection.timing], waitSeconds: 0));
        Assert.False(partial.TryGetProperty("pending", out _));
        string timingJob = partial.GetProperty("preparation").GetProperty("jobId").GetString()!;
        Assert.True(partial.GetProperty("timing").GetProperty("pending").GetBoolean());
        Assert.Equal(("draw", 3L), (partial.GetProperty("kind").GetString(), partial.GetProperty("parameters").GetProperty("workItems").GetInt64()));
        JsonElement joined = Parse(await InspectionTools.InspectEvent(_session, _jobs, eventRef, [InspectionSection.timing, InspectionSection.bindings], waitSeconds: 0));
        Assert.Equal(timingJob, joined.GetProperty("preparation").GetProperty("jobId").GetString());
        Assert.Single(_jobs.All.Where(j => j.Kind == "event-inspection"));
        Assert.Equal("succeeded", Parse(await SessionTools.JobWait(_jobs, timingJob, 600)).GetProperty("status").GetString());
        JsonElement complete = Parse(await InspectionTools.InspectEvent(_session, _jobs, eventRef, [InspectionSection.timing, InspectionSection.bindings], waitSeconds: 600));
        Assert.False(complete.TryGetProperty("preparation", out _));
        JsonElement completeTiming = complete.GetProperty("timing");
        Assert.True(completeTiming.GetProperty("eop").GetProperty("ns").GetUInt64() > 0);
        Assert.True(completeTiming.GetProperty("rankInQueue").GetInt32() >= 1);
        Assert.Contains(complete.GetProperty("parameters").GetProperty("arguments").EnumerateArray(), a =>
            a.GetProperty("name").GetString() is "VertexCountPerInstance" or "IndexCountPerInstance" && a.GetProperty("int64").GetInt64() == 3);

        // The default sections add targets and hints: the fixture draw renders into its 640x480 back buffer.
        string defaultAnswer = await InspectionTools.InspectEvent(_session, _jobs, eventRef, waitSeconds: 600);
        JsonElement inspected = Parse(defaultAnswer);
        JsonElement targets = inspected.GetProperty("targets");
        Assert.Equal("available", targets.GetProperty("state").GetString());
        Assert.Contains(targets.GetProperty("targets").EnumerateArray(), t => t.GetProperty("viewType").GetString() == "renderTarget"
            && t.GetProperty("width").GetUInt64() == 640 && t.GetProperty("height").GetUInt32() == 480 && t.GetProperty("nsPerMegapixel").ValueKind == JsonValueKind.Number);
        Assert.Equal(JsonValueKind.Array, inspected.GetProperty("hints").ValueKind);
        int defaultBytes = System.Text.Encoding.UTF8.GetByteCount(defaultAnswer);
        Assert.True(defaultBytes < 10_000, $"Default inspection is {defaultBytes} bytes: " + string.Join(", ",
            inspected.EnumerateObject().Select(p => $"{p.Name}={System.Text.Encoding.UTF8.GetByteCount(p.Value.GetRawText())}")));
        Assert.Equal("notCollected", Parse(await InspectionTools.InspectEvent(_session, _jobs, eventRef, [InspectionSection.counters], waitSeconds: 600))
            .GetProperty("counters").GetProperty("state").GetString());

        // A cold timing prepare hands its result over through pix_job_wait: available and readable.
        await AnalysisTools.Stop(_session, handle);
        string collectJob = Parse(await CountersTools.TimingCollect(_session, _jobs, handle)).GetProperty("jobId").GetString()!;
        JsonElement collected = Parse(await SessionTools.JobWait(_jobs, collectJob, 600));
        Assert.Equal("available", collected.GetProperty("resultState").GetString());
        Assert.True(_session.Results.ReadElement(collected.GetProperty("resultRef").GetString()!).GetProperty("queues").GetArrayLength() > 0);
        await SessionTools.Close(_session, handle);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CollectsCountersAfterStoppingAnalysis(bool explicitRestart)
    {
        Skip.IfNot(Available && Environment.GetEnvironmentVariable("PIX_TEST_ANALYSIS") == "1", "Set PIX_TEST_CAPTURE and PIX_TEST_ANALYSIS=1 to replay on the GPU");
        JsonElement opened = Parse(await GpuCaptureTools.Open(_session, CapturePath!));
        string handle = opened.GetProperty("handle").GetString()!;

        JsonElement list = Parse(await CountersTools.CountersList(_session, _jobs, handle, waitSeconds: 600));
        JsonElement counters = list.GetProperty("items");
        Skip.If(counters.GetArrayLength() == 0, "No hardware counters available on this GPU");
        uint counterId = counters[0].GetProperty("id").GetUInt32();
        Assert.All(counters.EnumerateArray(), row =>
        {
            Assert.False(string.IsNullOrEmpty(row.GetProperty("unit").GetString()));
            Assert.Contains(row.GetProperty("aggregationHint").GetString(), new[] { "sum", "avg", "none" });
        });
        Assert.NotEqual("unknown", list.GetProperty("extra").GetProperty("vendor").GetProperty("vendor").GetString());
        JsonElement pipelinePreset = list.GetProperty("extra").GetProperty("presets").EnumerateArray()
            .Single(p => p.GetProperty("preset").GetString() == "pipelineStatistics");

        await AnalysisTools.Stop(_session, handle);
        if (explicitRestart)
        {
            JsonElement started = Parse(await AnalysisTools.Start(_session, _jobs, handle, waitSeconds: 600));
            Assert.Equal("succeeded", started.GetProperty("status").GetString());
        }

        JsonElement collected = Parse(await CountersTools.CountersCollect(_session, _jobs, handle, new[] { counterId }, limit: 5, waitSeconds: 600));
        Assert.Equal(counterId, collected.GetProperty("extra").GetProperty("counters")[0].GetProperty("id").GetUInt32());
        Assert.Equal("exact", collected.GetProperty("extra").GetProperty("collection").GetProperty("source").GetString());
        Assert.Contains(collected.GetProperty("extra").GetProperty("coverage").EnumerateObject().First().Value[0].GetProperty("readback").GetString(), new[] { "bulk", "perEvent" });
        Assert.All(collected.GetProperty("items").EnumerateArray(), row => Assert.Contains(row.GetProperty("rowKind").GetString(), new[] { "marker", "event" }));
        // Rows join replay timing (collected in the same job), normalize per millisecond, and a grouped read starts no second counters job.
        Assert.Contains(collected.GetProperty("items").EnumerateArray(), row => row.TryGetProperty("eop", out JsonElement eop) && eop.ValueKind == JsonValueKind.Object);
        int countersJobs = _jobs.All.Count(j => j.Kind == "counters");
        JsonElement grouped = Parse(await CountersTools.CountersCollect(_session, _jobs, handle, new[] { counterId }, groupBy: "kind", waitSeconds: 600));
        Assert.Equal("kind", grouped.GetProperty("groupBy").GetString());
        Assert.All(grouped.GetProperty("items").EnumerateArray(), group => Assert.Equal(counterId, group.GetProperty("counters")[0].GetProperty("id").GetUInt32()));
        Assert.Equal(countersJobs, _jobs.All.Count(j => j.Kind == "counters"));
        JsonElement perMs = Parse(await CountersTools.CountersCollect(_session, _jobs, handle, new[] { counterId }, normalize: "perMs", sortBy: "eop", limit: 5, waitSeconds: 600));
        Assert.All(perMs.GetProperty("items").EnumerateArray(), row => Assert.True(row.TryGetProperty("normalized", out _) || row.TryGetProperty("normalizeReason", out _)));
        ulong[] eops = perMs.GetProperty("items").EnumerateArray().Select(row => row.TryGetProperty("eop", out JsonElement e) && e.ValueKind == JsonValueKind.Object ? e.GetProperty("ns").GetUInt64() : 0).ToArray();
        Assert.Equal(eops.OrderByDescending(v => v), eops);
        if (counters.GetArrayLength() > 1)
        {
            // {a} then {a,b} replays (a superset), while {b} after {a,b} is projected from it without a job.
            uint second = counters[1].GetProperty("id").GetUInt32();
            int before = _jobs.All.Count(j => j.Kind == "counters");
            JsonElement pair = Parse(await CountersTools.CountersCollect(_session, _jobs, handle, new[] { counterId, second }, limit: 5, waitSeconds: 600));
            Assert.Equal("exact", pair.GetProperty("extra").GetProperty("collection").GetProperty("source").GetString());
            Assert.Equal(before + 1, _jobs.All.Count(j => j.Kind == "counters"));
            JsonElement subset = Parse(await CountersTools.CountersCollect(_session, _jobs, handle, new[] { second }, limit: 5, waitSeconds: 600));
            Assert.Equal("projectedFromSuperset", subset.GetProperty("extra").GetProperty("collection").GetProperty("source").GetString());
            Assert.Equal(before + 1, _jobs.All.Count(j => j.Kind == "counters"));
        }
        if (pipelinePreset.GetProperty("matched").GetInt32() > 0)
        {
            // A preset resolves to ids on the worker and then runs the id-based path, so the explicit ids hit the same cache entry.
            int before = _jobs.All.Count(j => j.Kind == "counters");
            JsonElement byPreset = Parse(await CountersTools.CountersCollect(_session, _jobs, handle, preset: "pipelineStatistics", limit: 5, waitSeconds: 600));
            uint[] presetIds = pipelinePreset.GetProperty("counterIds").EnumerateArray().Select(id => id.GetUInt32()).ToArray();
            Assert.Equal(presetIds.OrderBy(id => id), byPreset.GetProperty("extra").GetProperty("counters").EnumerateArray().Select(c => c.GetProperty("id").GetUInt32()).OrderBy(id => id));
            Assert.True(byPreset.GetProperty("total").GetInt64() > 0, "pipeline statistics rows exist on every D3D12 GPU");
            Assert.Equal(before + 1, _jobs.All.Count(j => j.Kind == "counters"));
            JsonElement byIds = Parse(await CountersTools.CountersCollect(_session, _jobs, handle, presetIds, limit: 5, waitSeconds: 600));
            Assert.Equal("exact", byIds.GetProperty("extra").GetProperty("collection").GetProperty("source").GetString());
            Assert.Equal(before + 1, _jobs.All.Count(j => j.Kind == "counters"));
        }

        JsonElement hf = Parse(await CountersTools.HighFrequencyCounters(_session, _jobs, handle, waitSeconds: 600));
        if (hf.TryGetProperty("unavailable", out JsonElement unavailable) && unavailable.GetBoolean())
        {
            Assert.Equal("highFrequencyCounters", hf.GetProperty("feature").GetString());
            Assert.Contains(hf.GetProperty("state").GetString(), new[] { "unsupported", "unknown" });
        }
        else
        {
            Assert.All(hf.GetProperty("sets").EnumerateArray().SelectMany(set => set.GetProperty("counters").EnumerateArray()), counter =>
            {
                Assert.True(counter.TryGetProperty("description", out _));
                Assert.False(string.IsNullOrEmpty(counter.GetProperty("unit").GetString()));
            });
        }
        Assert.True(Parse(await AnalysisTools.Status(_session, handle)).GetProperty("started").GetBoolean());
        await SessionTools.Close(_session, handle);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunsDrPixAfterStoppingAnalysis(bool explicitRestart)
    {
        Skip.IfNot(Available && Environment.GetEnvironmentVariable("PIX_TEST_ANALYSIS") == "1", "Set PIX_TEST_CAPTURE and PIX_TEST_ANALYSIS=1 to replay on the GPU");
        JsonElement opened = Parse(await GpuCaptureTools.Open(_session, CapturePath!));
        string handle = opened.GetProperty("handle").GetString()!;

        JsonElement experiments = Parse(await DrPixTools.Experiments(_session, _jobs, handle, waitSeconds: 600));
        JsonElement experiment = experiments.GetProperty("items").EnumerateArray()
            .FirstOrDefault(e => e.GetProperty("name").GetString() == "1x1 Viewport");
        Skip.If(experiment.ValueKind == JsonValueKind.Undefined, "The 1x1 Viewport experiment is unavailable on this GPU");
        string experimentId = experiment.GetProperty("guid").GetString()!;

        await AnalysisTools.Stop(_session, handle);
        if (explicitRestart)
        {
            JsonElement started = Parse(await AnalysisTools.Start(_session, _jobs, handle, waitSeconds: 600));
            Assert.Equal("succeeded", started.GetProperty("status").GetString());
        }

        JsonElement markers = Parse(await GpuCaptureTools.Events(_session, handle, kind: "marker", nameContains: "Triangle pass", limit: 1));
        JsonElement markerRef = markers.GetProperty("items")[0].GetProperty("eventRef");
        var scope = new EventRef(handle, markerRef.GetProperty("queueIndex").GetInt32(), markerRef.GetProperty("eventIndex").GetUInt32());
        // The range spans every event with a GPU id inside the scope, including timed labels such as SetMarker.
        JsonElement scoped = Parse(await GpuCaptureTools.Events(_session, handle, scope: scope, limit: 100));
        uint[] gpuIds = scoped.GetProperty("items").EnumerateArray()
            .Where(e => e.TryGetProperty("gpuId", out _)).Select(e => e.GetProperty("gpuId").GetUInt32()).ToArray();
        Assert.NotEmpty(gpuIds);
        JsonElement run = Parse(await DrPixTools.Run(_session, _jobs, handle, new[] { experimentId }, scope: scope, waitSeconds: 600));
        Assert.Equal("succeeded", run.GetProperty("status").GetString());
        JsonElement result = Parse(ResultTools.Read(_session, run.GetProperty("resultRef").GetString()!)).GetProperty("value");
        Assert.Equal((1, 1, false), (result.GetProperty("runsRequested").GetInt32(), result.GetProperty("runsCompleted").GetInt32(), result.GetProperty("partial").GetBoolean()));
        JsonElement experimentResult = Assert.Single(result.GetProperty("runs").EnumerateArray());
        Assert.Equal(experimentId, experimentResult.GetProperty("guid").GetString());
        Assert.True(experimentResult.GetProperty("succeeded").GetBoolean(), experimentResult.GetRawText());
        JsonElement range = result.GetProperty("range");
        Assert.Equal(gpuIds.Min(), range.GetProperty("firstGpuId").GetUInt32());
        Assert.Equal(gpuIds.Max(), range.GetProperty("lastGpuId").GetUInt32());
        Assert.Equal(gpuIds.Length, range.GetProperty("workEvents").GetInt32());
        Assert.False(range.GetProperty("swapped").GetBoolean());
        Assert.Equal(scope.EventIndex, result.GetProperty("scope").GetProperty("root").GetProperty("eventIndex").GetUInt32());
        JsonElement timing = experimentResult.GetProperty("timing");
        Assert.Equal(("detected", "basic"), (timing.GetProperty("semantics").GetString(), experimentResult.GetProperty("family").GetString()));
        Assert.True(timing.GetProperty("baselineMs").GetDouble() > 0);
        Assert.Single(result.GetProperty("table").GetProperty("rows").EnumerateArray());
        Assert.Single(result.GetProperty("savings").EnumerateArray());

        // perEvent: one run per draw inside the pass, each over a single work event.
        int draws = Parse(await GpuCaptureTools.Events(_session, handle, scope: scope, kind: "work", limit: 100)).GetProperty("total").GetInt32();
        JsonElement perEvent = Parse(await DrPixTools.Run(_session, _jobs, handle, new[] { experimentId }, scope: scope, perEvent: true, waitSeconds: 600));
        Assert.Equal("succeeded", perEvent.GetProperty("status").GetString());
        JsonElement perEventResult = Parse(ResultTools.Read(_session, perEvent.GetProperty("resultRef").GetString()!)).GetProperty("value");
        Assert.Equal(draws, perEventResult.GetProperty("runsRequested").GetInt32());
        Assert.All(perEventResult.GetProperty("ranges").EnumerateArray(), r => Assert.Equal(r.GetProperty("firstGpuId").GetUInt32(), r.GetProperty("lastGpuId").GetUInt32()));
        await SessionTools.Close(_session, handle);
    }

    [SkippableFact]
    public async Task ComparesCapturesWithTotalsRollupsNoiseAndScopedSameHandleRuns()
    {
        Skip.IfNot(Available && Environment.GetEnvironmentVariable("PIX_TEST_ANALYSIS") == "1", "Set PIX_TEST_CAPTURE and PIX_TEST_ANALYSIS=1 to replay on the GPU");
        string candidatePath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(CapturePath!))!, "candidate.wpix");
        Skip.IfNot(File.Exists(candidatePath), "candidate.wpix next to PIX_TEST_CAPTURE is required");
        string baseline = Parse(await GpuCaptureTools.Open(_session, CapturePath!)).GetProperty("handle").GetString()!;
        string candidate = Parse(await GpuCaptureTools.Open(_session, candidatePath)).GetProperty("handle").GetString()!;
        try
        {
            JsonElement job = Parse(await InvestigationTools.Compare(_session, _jobs, baseline, candidate, repeats: 2, waitSeconds: 900));
            Assert.Equal("succeeded", job.GetProperty("status").GetString());
            JsonElement summary = Parse(ResultTools.Read(_session, job.GetProperty("resultRef").GetString()!)).GetProperty("value");
            Assert.True(summary.GetProperty("totals").GetProperty("baselineBusyNs").GetUInt64() > 0);
            Assert.NotEmpty(summary.GetProperty("byMarkerPath").EnumerateArray());
            JsonElement noise = summary.GetProperty("noise");
            Assert.Equal(2, noise.GetProperty("repeats").GetInt32());
            Assert.NotEqual("single", noise.GetProperty("repeatMethod").GetString());
            Assert.False(summary.GetProperty("provenanceMismatch").GetBoolean());

            EventRef[] roots = Parse(await GpuCaptureTools.Events(_session, candidate, kind: "marker", limit: 10)).GetProperty("items").EnumerateArray()
                .Select(m => JsonSerializer.Deserialize<EventRef>(m.GetProperty("eventRef").GetRawText(), Json.Options)!).ToArray();
            Assert.True(roots.Length >= 2, "The candidate capture needs two markers for a scoped same-handle comparison.");
            JsonElement scoped = Parse(await InvestigationTools.Compare(_session, _jobs, candidate, candidate, baselineScope: roots[0], candidateScope: roots[1], waitSeconds: 900));
            Assert.Equal("succeeded", scoped.GetProperty("status").GetString());
            Assert.True(_session.Get<PixMcp.Pix.Handles.GpuCaptureHandle>(candidate).AnalysisStarted);

            PixToolException active = await Assert.ThrowsAsync<PixToolException>(() => InvestigationTools.Compare(_session, _jobs, candidate, baseline, stopBaselineAnalysis: false));
            Assert.Equal("analysis_active", active.Detail.Code);
        }
        finally
        {
            await SessionTools.Close(_session, baseline);
            await SessionTools.Close(_session, candidate);
        }
    }

    [SkippableFact]
    public async Task ClassifiesTheBottleneckOfTheComputePassAndServesRepeatsFromCache()
    {
        Skip.IfNot(Available && Environment.GetEnvironmentVariable("PIX_TEST_ANALYSIS") == "1", "Set PIX_TEST_CAPTURE and PIX_TEST_ANALYSIS=1 to replay on the GPU");
        string handle = Parse(await GpuCaptureTools.Open(_session, CapturePath!)).GetProperty("handle").GetString()!;
        try
        {
            JsonElement dispatch = Parse(await GpuCaptureTools.Events(_session, handle, kind: "dispatch", limit: 1)).GetProperty("items")[0];
            var computePass = new EventRef(handle, dispatch.GetProperty("eventRef").GetProperty("queueIndex").GetInt32(), dispatch.GetProperty("parentIndex").GetUInt32());
            JsonElement job = Parse(await BottleneckTools.Bottleneck(_session, _jobs, handle, scope: computePass, waitSeconds: 600));
            Assert.Equal("succeeded", job.GetProperty("status").GetString());
            JsonElement result = Parse(ResultTools.Read(_session, job.GetProperty("resultRef").GetString()!)).GetProperty("value");
            Assert.Contains(result.GetProperty("verdict").GetProperty("limiter").GetString(), BottleneckRules.Limiters.Append("unknown"));
            Assert.True(result.GetProperty("evidence").GetArrayLength() >= 2, result.GetRawText());
            Assert.All(result.GetProperty("evidence").EnumerateArray(), row =>
            {
                Assert.False(string.IsNullOrEmpty(row.GetProperty("source").GetString()));
                Assert.False(string.IsNullOrEmpty(row.GetProperty("unit").GetString()));
            });
            Assert.Contains(result.GetProperty("coverage").EnumerateArray(), c => c.GetProperty("source").GetString() == "occupancy");
            Assert.False(string.IsNullOrEmpty(result.GetProperty("detailRef").GetString()));
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(result.GetRawText()) < 8192);
            JsonElement again = Parse(await BottleneckTools.Bottleneck(_session, _jobs, handle, scope: computePass, waitSeconds: 600));
            Assert.True(Parse(ResultTools.Read(_session, again.GetProperty("resultRef").GetString()!)).GetProperty("value").GetProperty("fromCache").GetBoolean());

            JsonElement pass = Parse(await GpuCaptureTools.Events(_session, handle, kind: "marker", nameContains: "Triangle pass", limit: 1)).GetProperty("items")[0];
            EventRef trianglePass = JsonSerializer.Deserialize<EventRef>(pass.GetProperty("eventRef").GetRawText(), Json.Options)!;
            JsonElement drpixJob = Parse(await BottleneckTools.Bottleneck(_session, _jobs, handle, scope: trianglePass, evidence: ["timing", "drpix"], maxDrPixRuns: 2, waitSeconds: 600));
            JsonElement drpix = Parse(ResultTools.Read(_session, drpixJob.GetProperty("resultRef").GetString()!)).GetProperty("value");
            Assert.Contains(drpix.GetProperty("evidence").EnumerateArray(), row => row.GetProperty("metric").GetString()!.StartsWith("drpix.1x1 Viewport", StringComparison.Ordinal));
        }
        finally
        {
            await SessionTools.Close(_session, handle);
        }
    }

    [SkippableFact]
    public async Task ScopeAndMarkerPathPrefixSelectTheSameEvents()
    {
        Skip.IfNot(Available, "Set PIX_TEST_CAPTURE to a .wpix file");
        JsonElement opened = Parse(await GpuCaptureTools.Open(_session, CapturePath!));
        string handle = opened.GetProperty("handle").GetString()!;

        JsonElement markers = Parse(await GpuCaptureTools.Events(_session, handle, kind: "marker", nameContains: "Triangle pass", limit: 5));
        JsonElement marker = markers.GetProperty("items")[0];
        JsonElement markerRef = marker.GetProperty("eventRef");
        var scope = new EventRef(handle, markerRef.GetProperty("queueIndex").GetInt32(), markerRef.GetProperty("eventIndex").GetUInt32());
        // Derive the prefix from the recorded path so legacy BeginEvent name prefixes never matter.
        string prefix = string.Join("/", marker.GetProperty("markerPath").EnumerateArray().Select(p => p.GetString()).Append(marker.GetProperty("name").GetString()));

        JsonElement byScope = Parse(await GpuCaptureTools.Events(_session, handle, scope: scope, limit: 1000));
        JsonElement byPrefix = Parse(await GpuCaptureTools.Events(_session, handle, markerPathPrefix: prefix, limit: 1000));
        static uint[] Indices(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("eventRef").GetProperty("eventIndex").GetUInt32()).ToArray();
        Assert.Equal(Indices(byScope), Indices(byPrefix));
        Assert.Contains(scope.EventIndex, Indices(byScope));
        JsonElement selection = byPrefix.GetProperty("extra").GetProperty("scope");
        Assert.Equal(markers.GetProperty("total").GetInt32(), selection.GetProperty("matchedRootCount").GetInt32());
        Assert.Equal(prefix, selection.GetProperty("markerPathPrefix").GetString());

        JsonElement none = Parse(await GpuCaptureTools.Events(_session, handle, markerPathPrefix: "No such marker/anywhere", limit: 10));
        Assert.Equal(0, none.GetProperty("total").GetInt32());
        Assert.Equal(0, none.GetProperty("extra").GetProperty("scope").GetProperty("matchedRootCount").GetInt32());

        await SessionTools.Close(_session, handle);
    }

    [SkippableFact]
    public async Task CorrelatesGpuCaptureWithTimingCaptureByQueueAndReportsUnmatchedNames()
    {
        Skip.IfNot(Available && TestArtifacts.AnalysisEnabled && TestArtifacts.TimingCapture is not null, "Set PIX_TEST_ANALYSIS=1 and PIX_TEST_TIMING_CAPTURE to correlate a replay with a recording");
        string gpuHandle = Parse(await GpuCaptureTools.Open(_session, CapturePath!)).GetProperty("handle").GetString()!;
        string timingHandle = Parse(await TimingCaptureTools.Open(_session, TestArtifacts.TimingCapture!)).GetProperty("handle").GetString()!;
        JsonElement result = Parse(await TimingCorrelationTools.Correlate(_session, _jobs, gpuHandle, timingHandle, waitSeconds: 600));
        Assert.Equal(TimingCorrelation.Identity, result.GetProperty("identity").GetString());
        JsonElement counts = result.GetProperty("counts");
        int gpuPaths = counts.GetProperty("gpuPaths").GetInt32();
        Assert.True(gpuPaths > 0);
        Assert.Equal(gpuPaths, counts.GetProperty("matched").GetInt32() + counts.GetProperty("unmatchedGpu").GetInt32());
        Assert.True(counts.GetProperty("recordedPaths").GetInt32() >= 2);
        // The fixture's GPU capture names its markers Frame/Triangle pass while its timing capture records Fixture Frame/Fixture CPU Work.
        Assert.Contains(result.GetProperty("unmatchedRecorded").EnumerateArray(), u => u.GetProperty("path").GetString() == "Fixture Frame");
        Assert.Contains(result.GetProperty("queueMap").EnumerateArray(), q => q.GetProperty("method").GetString() == "typeAndName");
    }

    [SkippableFact]
    public async Task OverviewAnswersColdCapturesThenRanksPassesAndStaysWithinBudget()
    {
        Skip.IfNot(Available && TestArtifacts.AnalysisEnabled, "Set PIX_TEST_ANALYSIS=1 to replay on the GPU");
        string handle = Parse(await GpuCaptureTools.Open(_session, CapturePath!)).GetProperty("handle").GetString()!;
        JsonElement cold = Parse(await InvestigationTools.Overview(_session, _jobs, handle, waitSeconds: 0));
        Assert.True(cold.GetProperty("queues").GetArrayLength() > 0);
        Assert.True(cold.GetProperty("capture").GetProperty("eventTotal").GetInt64() > 0);
        if (cold.TryGetProperty("timing", out JsonElement pending) && pending.ValueKind == JsonValueKind.Object)
        {
            Assert.True(pending.GetProperty("pending").GetBoolean());
            await _jobs.Get(pending.GetProperty("jobId").GetString()!).WaitAsync(TimeSpan.FromSeconds(600), CancellationToken.None);
        }

        string full = await InvestigationTools.Overview(_session, _jobs, handle, waitSeconds: 600);
        JsonElement overview = Parse(full);
        Assert.True(overview.GetProperty("queues")[0].GetProperty("totals").GetProperty("busyMs").GetDouble() > 0);
        string[] passes = overview.GetProperty("topPasses").EnumerateArray().Select(p => p.GetProperty("name").GetString()!).ToArray();
        Assert.Contains(passes, name => name.EndsWith("Frame"));
        Assert.Contains(passes, name => name.EndsWith("Triangle pass"));
        Assert.DoesNotContain(overview.GetProperty("topDraws").EnumerateArray(), d => d.GetProperty("kind").GetString() is "clear" or "copy" or "present");
        int fullBytes = System.Text.Encoding.UTF8.GetByteCount(full);
        Assert.True(fullBytes < 12000, $"overview is {fullBytes} bytes");
        string brief = await InvestigationTools.Overview(_session, _jobs, handle, waitSeconds: 600, brief: true);
        string BreakDown(string json) => string.Join(", ", System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject().Select(p => $"{p.Key}={System.Text.Encoding.UTF8.GetByteCount(p.Value?.ToJsonString() ?? "null")}"));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(brief) < 8192, $"brief overview is {System.Text.Encoding.UTF8.GetByteCount(brief)} bytes: {BreakDown(brief)}; full: {BreakDown(full)}");

        JsonElement table = Parse(await InvestigationTools.Overview(_session, _jobs, handle, waitSeconds: 600, format: "table"));
        Assert.Equal(overview.GetProperty("topDraws").GetArrayLength(), table.GetProperty("topDraws").GetProperty("rows").GetArrayLength());
        JsonElement firstFrame = Parse(await InvestigationTools.Overview(_session, _jobs, handle, waitSeconds: 600, frameIndex: 0));
        Assert.Equal(overview.GetProperty("topDraws").GetArrayLength(), firstFrame.GetProperty("topDraws").GetArrayLength());
        await Assert.ThrowsAsync<PixToolException>(() => InvestigationTools.Overview(_session, _jobs, handle, waitSeconds: 600, frameIndex: 99));

        JsonElement trianglePass = overview.GetProperty("topPasses").EnumerateArray().First(p => p.GetProperty("name").GetString()!.EndsWith("Triangle pass"));
        EventRef triangle = JsonSerializer.Deserialize<EventRef>(trianglePass.GetProperty("eventRef").GetRawText(), Json.Options)!;
        JsonElement scoped = Parse(await InvestigationTools.Overview(_session, _jobs, handle, waitSeconds: 600, scope: triangle));
        Assert.NotEmpty(scoped.GetProperty("topDraws").EnumerateArray());
        Assert.All(scoped.GetProperty("topDraws").EnumerateArray(), d => Assert.Equal("draw", d.GetProperty("kind").GetString()));
        Assert.True(scoped.GetProperty("topDraws").GetArrayLength() < overview.GetProperty("topDraws").GetArrayLength());
        await SessionTools.Close(_session, handle);
    }

    private static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static void AssertPage(JsonElement page, int limit)
    {
        int count = page.GetProperty("count").GetInt32();
        long total = page.GetProperty("total").GetInt64();
        int offset = page.GetProperty("offset").GetInt32();
        Assert.Equal(count, page.GetProperty("items").GetArrayLength());
        Assert.InRange(count, 0, limit);
        Assert.True(total >= count);
        if (offset + count < total)
        {
            Assert.Equal(offset + count, page.GetProperty("nextOffset").GetInt32());
        }
        else
        {
            Assert.False(page.TryGetProperty("nextOffset", out _));
        }
    }

    public void Dispose()
    {
        _session.Dispose();
        _worker.Dispose();
    }
}
