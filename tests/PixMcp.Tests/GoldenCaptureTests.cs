using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using PixMcp.Pix;
using PixMcp.Pix.Handles;
using PixMcp.Tools;
using Xunit;

namespace PixMcp.Tests;

/// <summary>
/// Golden event dumps of the checked-in fixture captures (tests/artifacts/{baseline,candidate}.wpix, enabled by PIX_TEST_CAPTURE):
/// index, GPU id, parent, name, API call text, command list and classified kind for every event of every queue. The header
/// pins the capture bytes, the fixture sources and the PIX build, so a regenerated fixture fails with golden_header_mismatch
/// instead of a confusing event diff. PIXMCP_UPDATE_GOLDEN=1 rewrites the goldens.
/// </summary>
[Collection(GpuReplayCollection.Name)]
public sealed class GoldenCaptureTests : IDisposable
{
    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly PixWorker _worker = new();
    private readonly PixSession _session;

    public GoldenCaptureTests() => _session = new PixSession(_worker, NullLogger<PixSession>.Instance);

    public void Dispose()
    {
        _session.Dispose();
        _worker.Dispose();
    }

    public static TheoryData<string> Captures => new() { "baseline", "candidate" };

    [SkippableTheory]
    [MemberData(nameof(Captures))]
    public async Task EventDumpMatchesTheGolden(string name)
    {
        TestArtifacts.SkipUnlessPix();
        Skip.If(TestArtifacts.Capture is null, "Set PIX_TEST_CAPTURE to run the golden capture tests");
        string? capture = TestArtifacts.Artifact(name + ".wpix");
        Skip.If(capture is null, $"tests/artifacts/{name}.wpix has not been generated (scripts/capture_fixtures.py)");

        // Hash before opening: PIX holds the capture without read sharing while a handle is open.
        JsonObject header = Header(name, capture!);
        string handle = JsonSerializer.Deserialize<JsonElement>(await GpuCaptureTools.Open(_session, capture!)).GetProperty("handle").GetString()!;
        GpuCaptureHandle gpu = _session.Get<GpuCaptureHandle>(handle);
        var actual = new JsonObject { ["header"] = header, ["queues"] = await Queues(gpu) };
        string path = Path.Combine(TestArtifacts.Root!, "tests", "PixMcp.Tests", "Fixtures", "golden", name + ".events.json");
        if (Environment.GetEnvironmentVariable("PIXMCP_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Format(actual));
            return;
        }
        Assert.True(File.Exists(path), $"{path} is missing; run the test with PIXMCP_UPDATE_GOLDEN=1.");
        JsonNode expected = JsonNode.Parse(File.ReadAllText(path))!;
        string[] mismatches = expected["header"]!.AsObject().Select(p => p.Key)
            .Where(key => expected["header"]![key]?.ToJsonString() != header[key]?.ToJsonString())
            .Select(key => $"{key} {header[key]?.ToJsonString()} != golden {expected["header"]![key]?.ToJsonString()}").ToArray();
        Assert.True(mismatches.Length == 0, "golden_header_mismatch: " + string.Join("; ", mismatches) + ". The fixture changed; review and regenerate with PIXMCP_UPDATE_GOLDEN=1.");
        string? difference = FirstDifference(expected["queues"]!.AsArray(), actual["queues"]!.AsArray());
        Assert.True(difference is null, $"{name} events differ from the golden: {difference}");
    }

    private async Task<JsonArray> Queues(GpuCaptureHandle gpu)
    {
        var queues = new JsonArray();
        foreach (QueueEntry queue in gpu.Queues)
        {
            EventRecord[] events = await _worker.Run(() => gpu.AllEvents(queue.Index));
            var hasChildren = new bool[events.Length];
            foreach (EventRecord e in events)
                if (e.ParentIndex < events.Length) hasChildren[e.ParentIndex] = true;
            var rows = new JsonArray();
            foreach (EventRecord e in events)
                rows.Add(new JsonArray(e.Index, e.GpuId == uint.MaxValue ? null : e.GpuId, e.ParentIndex == uint.MaxValue ? null : e.ParentIndex,
                    e.Name, e.ApiCallData, e.CommandListId, PixMcp.Tools.Tools.Classify(e, hasChildren[e.Index])));
            queues.Add(new JsonObject { ["index"] = queue.Index, ["events"] = rows });
        }
        return queues;
    }

    private static JsonObject Header(string name, string capture)
    {
        string fixture = Path.Combine(TestArtifacts.Root!, "tests", "D3D12TestApp");
        return new JsonObject
        {
            ["captureSha256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(capture))),
            ["mainCppSha256"] = SourceSha256(Path.Combine(fixture, "main.cpp")),
            ["buildCmdSha256"] = SourceSha256(Path.Combine(fixture, "build.cmd")),
            ["pixBuild"] = PixDiscovery.InstallVersion?.Build,
            ["fixtureFlags"] = "--variant " + name,
        };
    }

    /// <summary>SHA-256 of a text source with LF line endings (scripts/capture_fixtures.py source_sha256 computes the same).</summary>
    private static string SourceSha256(string path)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal))));

    private static string? FirstDifference(JsonArray expected, JsonArray actual)
    {
        if (expected.Count != actual.Count) return $"{actual.Count} queues, golden has {expected.Count}";
        for (int q = 0; q < expected.Count; q++)
        {
            JsonArray want = expected[q]!["events"]!.AsArray(), got = actual[q]!["events"]!.AsArray();
            for (int i = 0; i < Math.Min(want.Count, got.Count); i++)
                if (want[i]!.ToJsonString() != got[i]!.ToJsonString())
                    return $"queue {q} event {i}: {got[i]!.ToJsonString(Relaxed)} != golden {want[i]!.ToJsonString(Relaxed)}";
            if (want.Count != got.Count) return $"queue {q} has {got.Count} events, golden has {want.Count}";
        }
        return null;
    }

    /// <summary>Header indented, one event per line, so fixture changes review as line diffs.</summary>
    private static string Format(JsonObject golden)
    {
        var text = new StringBuilder("{\n  \"header\": ");
        text.Append(golden["header"]!.ToJsonString(new JsonSerializerOptions(Relaxed) { WriteIndented = true }).Replace("\n", "\n  ", StringComparison.Ordinal));
        text.Append(",\n  \"queues\": [\n");
        JsonArray queues = golden["queues"]!.AsArray();
        for (int q = 0; q < queues.Count; q++)
        {
            JsonArray events = queues[q]!["events"]!.AsArray();
            text.Append("    {\"index\": ").Append(queues[q]!["index"]!.ToJsonString()).Append(", \"events\": [\n");
            for (int i = 0; i < events.Count; i++)
                text.Append("      ").Append(events[i]!.ToJsonString(Relaxed)).Append(i + 1 < events.Count ? ",\n" : "\n");
            text.Append("    ]}").Append(q + 1 < queues.Count ? ",\n" : "\n");
        }
        return text.Append("  ]\n}\n").ToString();
    }
}
