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

    private static string? CapturePath => Environment.GetEnvironmentVariable("PIX_TEST_CAPTURE");

    private static bool Available => PixDiscovery.InstallDir is not null && CapturePath is not null && File.Exists(CapturePath);

    [SkippableFact]
    public async Task FactoryLoads()
    {
        Skip.IfNot(PixDiscovery.InstallDir is not null, "PIX Preview not installed");
        JsonElement info = Parse(await SessionTools.Info(_session, _jobs, probe: true));
        Assert.True(info.GetProperty("pix").GetProperty("apiLoaded").GetBoolean());
        Assert.False(info.GetProperty("pix").TryGetProperty("probeError", out _));
    }

    [SkippableFact]
    public async Task OpensCaptureAndListsEvents()
    {
        Skip.IfNot(Available, "Set PIX_TEST_CAPTURE to a .wpix file");
        JsonElement opened = Parse(await GpuCaptureTools.Open(_session, CapturePath!));
        string handle = opened.GetProperty("handle").GetString()!;
        Assert.StartsWith("gpu-", handle);
        Assert.True(opened.GetProperty("queues").GetArrayLength() > 0);

        JsonElement events = Parse(await GpuCaptureTools.Events(_session, handle, queueIndex: 0, limit: 5));
        Assert.True(events.GetProperty("total").GetInt64() > 0);
        Assert.True(events.GetProperty("count").GetInt32() <= 5);

        JsonElement draws = Parse(await GpuCaptureTools.Events(_session, handle, kind: "drawOrDispatch", limit: 3));
        Assert.True(draws.GetProperty("count").GetInt32() >= 0);

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

        JsonElement timing = Parse(await CountersTools.TimingEvents(_session, handle, limit: 5));
        Assert.True(timing.GetProperty("total").GetInt64() >= 0);

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

        JsonElement counters = Parse(await CountersTools.CountersList(_session, handle)).GetProperty("counters");
        Skip.If(counters.GetArrayLength() == 0, "No hardware counters available on this GPU");
        uint counterId = counters[0].GetProperty("id").GetUInt32();

        await AnalysisTools.Stop(_session, handle);
        if (explicitRestart)
        {
            JsonElement started = Parse(await AnalysisTools.Start(_session, _jobs, handle, waitSeconds: 600));
            Assert.Equal("succeeded", started.GetProperty("status").GetString());
        }

        JsonElement collected = Parse(await CountersTools.CountersCollect(_session, handle, new[] { counterId }, limit: 5));
        Assert.Equal(counterId, collected.GetProperty("extra").GetProperty("counters")[0].GetProperty("id").GetUInt32());
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

        JsonElement experiments = Parse(await DrPixTools.Experiments(_session, handle));
        JsonElement experiment = experiments.EnumerateArray()
            .FirstOrDefault(e => e.GetProperty("name").GetString() == "1x1 Viewport");
        Skip.If(experiment.ValueKind == JsonValueKind.Undefined, "The 1x1 Viewport experiment is unavailable on this GPU");
        string experimentId = experiment.GetProperty("guid").GetString()!;

        await AnalysisTools.Stop(_session, handle);
        if (explicitRestart)
        {
            JsonElement started = Parse(await AnalysisTools.Start(_session, _jobs, handle, waitSeconds: 600));
            Assert.Equal("succeeded", started.GetProperty("status").GetString());
        }

        JsonElement run = Parse(await DrPixTools.Run(_session, _jobs, handle, new[] { experimentId }, waitSeconds: 600));
        Assert.Equal("succeeded", run.GetProperty("status").GetString());
        JsonElement result = run.GetProperty("result");
        Assert.Equal(1, result.GetProperty("experimentsRun").GetInt32());
        JsonElement experimentResult = Assert.Single(result.GetProperty("results").EnumerateArray());
        Assert.Equal(experimentId, experimentResult.GetProperty("guid").GetString());
        Assert.True(experimentResult.GetProperty("succeeded").GetBoolean());
        await SessionTools.Close(_session, handle);
    }

    private static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    public void Dispose()
    {
        _session.Dispose();
        _worker.Dispose();
    }
}
