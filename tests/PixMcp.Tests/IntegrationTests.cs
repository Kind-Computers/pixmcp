using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PixMcp.Pix;
using PixMcp.Tools;
using Xunit;

namespace PixMcp.Tests;

/// <summary>
/// End-to-end tests against the real PIX API. They run only when PIX_TEST_CAPTURE points at a
/// .wpix GPU capture (and PIX Preview is installed); otherwise they are skipped.
/// </summary>
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
        Assert.True(overview.GetProperty("queues")[0].GetProperty("eventKinds").GetProperty("label").GetInt32() >= 1);
        Assert.Equal(captureVendor.GetProperty("vendor").GetString(), overview.GetProperty("vendor").GetProperty("vendor").GetString());

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
        JsonElement joined = Parse(await InspectionTools.InspectEvent(_session, _jobs, eventRef, [InspectionSection.timing, InspectionSection.bindings], waitSeconds: 0));
        Assert.Equal(timingJob, joined.GetProperty("preparation").GetProperty("jobId").GetString());
        Assert.Single(_jobs.All.Where(j => j.Kind == "event-inspection"));
        Assert.Equal("succeeded", Parse(await SessionTools.JobWait(_jobs, timingJob, 600)).GetProperty("status").GetString());
        JsonElement complete = Parse(await InspectionTools.InspectEvent(_session, _jobs, eventRef, [InspectionSection.timing, InspectionSection.bindings], waitSeconds: 600));
        Assert.False(complete.TryGetProperty("preparation", out _));
        Assert.True(complete.GetProperty("timing").TryGetProperty("eopDurationNs", out _) || complete.GetProperty("timing").TryGetProperty("unavailable", out _));

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
        Assert.Contains(collected.GetProperty("extra").GetProperty("coverage")[0].GetProperty("readback").GetString(), new[] { "bulk", "perEvent" });
        Assert.All(collected.GetProperty("items").EnumerateArray(), row => Assert.Contains(row.GetProperty("rowKind").GetString(), new[] { "marker", "event" }));
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
        Assert.Equal(1, result.GetProperty("experimentsRun").GetInt32());
        JsonElement experimentResult = Assert.Single(result.GetProperty("results").EnumerateArray());
        Assert.Equal(experimentId, experimentResult.GetProperty("guid").GetString());
        Assert.True(experimentResult.GetProperty("succeeded").GetBoolean(), experimentResult.GetRawText());
        JsonElement range = result.GetProperty("range");
        Assert.Equal(gpuIds.Min(), range.GetProperty("firstGpuId").GetUInt32());
        Assert.Equal(gpuIds.Max(), range.GetProperty("lastGpuId").GetUInt32());
        Assert.Equal(gpuIds.Length, range.GetProperty("workEvents").GetInt32());
        Assert.False(range.GetProperty("swapped").GetBoolean());
        Assert.Equal(scope.EventIndex, result.GetProperty("scope").GetProperty("root").GetProperty("eventIndex").GetUInt32());
        await SessionTools.Close(_session, handle);
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
        Assert.Contains(result.GetProperty("queueMap").EnumerateArray(), q => q.GetProperty("method").GetString() != "none");
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
