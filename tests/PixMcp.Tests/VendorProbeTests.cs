using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PixMcp.Pix;
using PixMcp.Tools;
using Xunit;

namespace PixMcp.Tests;

/// <summary>
/// Evidence gathering, not assertions: with PIXMCP_VENDOR_PROBE_OUT set, replays the fixture captures on every non-WARP analysis
/// adapter and writes each tool answer (job results and deferred answers expanded) to &lt;dir&gt;/&lt;vendor&gt;-&lt;capture&gt;-&lt;step&gt;.json.
/// </summary>
[Collection(GpuReplayCollection.Name)]
public sealed class VendorProbeTests : IDisposable
{
    private const string Deprecated = "<deprecated - use pix3.h instead> ";
    // Intel B580 counters worth reading per event: busy and utilization lanes, occupancy, stalls, memory and sampler activity.
    private static readonly string[] IntelCore =
    [
        "GPU Busy", "Command Parser Render Engine Busy", "XVE Active", "XVE Stall", "XVE Threads Occupancy All",
        "XVE Inst Executed ALU0 All Utilization", "XVE Inst Executed ALU0 VS Utilization", "XVE Inst Executed ALU0 PS Utilization",
        "XVE Inst Executed ALU0 CS Utilization", "XVE Inst Executed ALU1 All Utilization", "XVE Inst Executed ALU2 All Utilization",
        "GPU Memory Active", "Sampler Active", "Sampler Memory Latency Stall", "Thread Dispatch Queue0 Stall", "PS Invocations",
    ];

    private readonly PixWorker _worker = new();
    private readonly PixSession _session;
    private readonly JobManager _jobs;
    private string _output = "";

    public VendorProbeTests()
    {
        _session = new PixSession(_worker, NullLogger<PixSession>.Instance);
        _jobs = new JobManager(_worker, _session);
    }

    [SkippableFact]
    public async Task DumpsVendorFactsForEveryAdapter()
    {
        string? output = Environment.GetEnvironmentVariable("PIXMCP_VENDOR_PROBE_OUT");
        Skip.If(string.IsNullOrWhiteSpace(output), "Set PIXMCP_VENDOR_PROBE_OUT to a directory to dump per-adapter probe results");
        string capture = TestArtifacts.RequireAnalysisCapture();
        _output = output!;
        Directory.CreateDirectory(_output);

        string probe = Handle(await GpuCaptureTools.Open(_session, capture));
        string adaptersJson = await AnalysisTools.Adapters(_session, probe);
        File.WriteAllText(Path.Combine(_output, "adapters.json"), adaptersJson);
        await SessionTools.Close(_session, probe);

        foreach (JsonElement adapter in Parse(adaptersJson).GetProperty("adapters").EnumerateArray())
        {
            string name = adapter.GetProperty("name").GetString()!;
            string vendor = adapter.GetProperty("vendor").GetString()!;
            if (vendor == "warp") continue;

            if (await StartOn(vendor, "baseline", capture, name) is string handle)
            {
                string triangle = Deprecated + "Frame/" + Deprecated + "Triangle pass";
                string compute = Deprecated + "Wave compute";
                await Step(vendor, "baseline", "status", () => AnalysisTools.Status(_session, handle));
                JsonElement? list = await Step(vendor, "baseline", "counters-list", () => CountersTools.CountersList(_session, _jobs, handle, limit: 1000, waitSeconds: 600));
                foreach (string preset in new[] { "utilization", "aluUtilization", "perStageAlu", "occupancy", "stalls", "cache", "memoryBandwidth", "fixedFunction" })
                    await Step(vendor, "baseline", "counters-" + preset, () => CountersTools.CountersCollect(_session, _jobs, handle, preset: preset, limit: 30, waitSeconds: 900));
                if (vendor == "intel" && Ids(list, IntelCore) is { Length: > 0 } ids)
                    foreach (int queue in new[] { 0, 2 })
                        await Step(vendor, "baseline", $"counters-core-q{queue}", () => CountersTools.CountersCollect(_session, _jobs, handle, counterIds: ids, queueIndex: queue, limit: 40, waitSeconds: 900));
                await Step(vendor, "baseline", "occupancy", () => CountersTools.Occupancy(_session, _jobs, handle, maxPoints: 20, waitSeconds: 600));
                await Step(vendor, "baseline", "hf", () => CountersTools.HighFrequencyCounters(_session, _jobs, handle, waitSeconds: 600));
                await Step(vendor, "baseline", "drpix-experiments", () => DrPixTools.Experiments(_session, _jobs, handle, waitSeconds: 600));
                await Step(vendor, "baseline", "shader-profile-compute", () => ShaderProfilingTools.Profile(_session, _jobs, handle, markerPathPrefix: compute, waitSeconds: 600));
                await Step(vendor, "baseline", "shader-profile-triangle", () => ShaderProfilingTools.Profile(_session, _jobs, handle, markerPathPrefix: triangle, waitSeconds: 600));
                await Step(vendor, "baseline", "drpix-bandwidth", () => DrPixTools.Run(_session, _jobs, handle, experiments: ["Bandwidth"], markerPathPrefix: triangle, waitSeconds: 900));
                await Step(vendor, "baseline", "bottleneck-triangle", () => BottleneckTools.Bottleneck(_session, _jobs, handle, markerPathPrefix: triangle,
                    evidence: ["timing", "counters", "occupancy", "hf", "drpix"], waitSeconds: 1200));
                await SessionTools.Close(_session, handle);
            }

            if (TestArtifacts.PerfBaseline is string perf && await StartOn(vendor, "perf", perf, name) is string perfHandle)
            {
                JsonElement? list = await Step(vendor, "perf", "counters-list", () => CountersTools.CountersList(_session, _jobs, perfHandle, limit: 1000, waitSeconds: 600));
                if (vendor == "intel" && Ids(list, IntelCore) is { Length: > 0 } ids)
                    await Step(vendor, "perf", "counters-core-q0", () => CountersTools.CountersCollect(_session, _jobs, perfHandle, counterIds: ids, queueIndex: 0,
                        kind: "marker", limit: 60, waitSeconds: 900));
                await Step(vendor, "perf", "bottleneck-lighting", () => BottleneckTools.Bottleneck(_session, _jobs, perfHandle, markerPathPrefix: "Frame/Lighting",
                    evidence: ["timing", "counters", "occupancy", "drpix"], waitSeconds: 1800));
                await Step(vendor, "perf", "bottleneck-shadow", () => BottleneckTools.Bottleneck(_session, _jobs, perfHandle, markerPathPrefix: "Frame/Shadow",
                    evidence: ["timing", "counters", "drpix"], waitSeconds: 1800));
                await SessionTools.Close(_session, perfHandle);
            }
        }
    }

    /// <summary>Opens <paramref name="capture"/> and starts analysis on the adapter, adding IGNORE_INCOMPATIBILITIES only when a plain start is refused.</summary>
    private async Task<string?> StartOn(string vendor, string label, string capture, string adapterName)
    {
        string handle = Handle(await GpuCaptureTools.Open(_session, capture));
        if (Status(await Step(vendor, label, "start", () => AnalysisTools.Start(_session, _jobs, handle, adapterName: adapterName, waitSeconds: 600))) == "succeeded") return handle;
        if (Status(await Step(vendor, label, "start-ignore", () => AnalysisTools.Start(_session, _jobs, handle, adapterName: adapterName,
                flags: ["IGNORE_INCOMPATIBILITIES"], waitSeconds: 600))) == "succeeded") return handle;
        await SessionTools.Close(_session, handle);
        return null;
    }

    /// <summary>Writes one tool answer (or its structured error), expanding a job result or deferred answer, and returns what was written.</summary>
    private async Task<JsonElement?> Step(string vendor, string label, string step, Func<Task<string>> call)
    {
        string text;
        try { text = await call(); }
        catch (PixToolException ex) { text = Json.Serialize(new { thrown = ex.Detail }); }
        catch (Exception ex) { text = Json.Serialize(new { exception = ex.GetType().Name, message = ex.Message }); }
        JsonElement? answer = null;
        try
        {
            JsonElement parsed = Parse(text);
            answer = parsed;
            if (parsed.TryGetProperty("resultRef", out JsonElement resultRef) && resultRef.GetString() is string reference
                && (parsed.TryGetProperty("deferred", out _) || parsed.TryGetProperty("status", out JsonElement status) && status.GetString() == "succeeded"))
            {
                JsonElement result = _session.Results.ReadElement(reference, "", 64 * 1024 * 1024);
                answer = parsed.TryGetProperty("deferred", out _) ? result : JsonSerializer.SerializeToElement(new { job = parsed, result });
                text = JsonSerializer.Serialize(answer, new JsonSerializerOptions { WriteIndented = true });
            }
        }
        catch (Exception ex) when (ex is JsonException or PixToolException) { }
        File.WriteAllText(Path.Combine(_output, $"{vendor}-{label}-{step}.json"), text);
        return answer;
    }

    private static string? Status(JsonElement? answer)
        => answer is JsonElement a && a.ValueKind == JsonValueKind.Object
            ? a.TryGetProperty("job", out JsonElement job) ? job.GetProperty("status").GetString()
            : a.TryGetProperty("status", out JsonElement status) ? status.GetString() : null
            : null;

    private static uint[] Ids(JsonElement? list, IEnumerable<string> names)
    {
        if (list is not JsonElement page || !page.TryGetProperty("items", out JsonElement items)) return [];
        var byName = items.EnumerateArray().ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("id").GetUInt32(), StringComparer.Ordinal);
        return names.Where(byName.ContainsKey).Select(n => byName[n]).ToArray();
    }

    private static string Handle(string opened) => Parse(opened).GetProperty("handle").GetString()!;
    private static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    public void Dispose()
    {
        _session.Dispose();
        _worker.Dispose();
    }
}
