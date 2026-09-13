using System.Text.Json;
using PixMcp.Pix;
using Xunit;

namespace PixMcp.Tests;

/// <summary>pix_gpu_compare_changes filters over stored comparison rows, including rows that predate the new fields.</summary>
public sealed class ComparisonFilterTests
{
    private static object Row(uint index, decimal? ns, string[] path, string kind = "draw", string confidence = "high", bool belowNoise = false, int queue = 0,
        string[]? sections = null) => new
    {
        baseline = new EventRef("gpu-a", queue, index), candidate = new EventRef("gpu-b", queue, index + 10), name = "Draw", markerPath = path, matchMethod = "markerPath",
        deltaNs = ns, deltaPercent = (double?)ns, fields = (sections ?? []).Select(s => new { section = s, path = "/x" }).ToArray(), kind, confidence, belowNoiseFloor = belowNoise,
    };

    private static uint[] Ids(ComparisonChangesDto page) => page.Items.Select(i => i.GetProperty("baseline").GetProperty("eventIndex").GetUInt32()).ToArray();

    [Fact]
    public void FiltersNarrowByPathKindQueueSectionNoiseAndConfidence()
    {
        using var store = new ResultStore();
        string reference = store.Store(new
        {
            items = new object[]
            {
                Row(1, 20, ["Frame", "Lighting"]), Row(2, 5, ["Frame", "Shadow"], kind: "dispatch", queue: 1),
                Row(3, 0, ["Frame", "Lighting"], sections: ["shaders"]), Row(4, 3, ["Frame", "Lighting"], confidence: "low", belowNoise: true),
                new { baseline = new EventRef("gpu-a", 0, 5), candidate = new EventRef("gpu-b", 0, 15), deltaNs = (decimal?)7, deltaPercent = (double?)7, fields = Array.Empty<object>() },
            },
        });
        Assert.Equal(new uint[] { 1, 4, 3 }, Ids(ComparisonResultQuery.Read(store, reference, markerPathPrefix: "frame/lighting/")));
        Assert.Equal(new uint[] { 5, 2 }, Ids(ComparisonResultQuery.Read(store, reference, kind: "dispatch")));
        Assert.Equal(new uint[] { 2 }, Ids(ComparisonResultQuery.Read(store, reference, queueIndex: 1)));
        Assert.Equal(new uint[] { 3 }, Ids(ComparisonResultQuery.Read(store, reference, section: "SHADERS")));
        Assert.Equal(new uint[] { 3 }, Ids(ComparisonResultQuery.Read(store, reference, direction: "structural")));
        Assert.Equal(new uint[] { 1, 5, 2, 3 }, Ids(ComparisonResultQuery.Read(store, reference, excludeBelowNoise: true)));
        Assert.Equal(new uint[] { 1, 5, 2, 3 }, Ids(ComparisonResultQuery.Read(store, reference, minConfidence: "medium")));

        ComparisonChangesDto first = ComparisonResultQuery.Read(store, reference, limit: 1, markerPathPrefix: "Frame", minConfidence: "low");
        JsonElement next = JsonSerializer.SerializeToElement(Assert.Single(first.NextCalls).Arguments, Json.Options);
        Assert.Equal(("Frame", "low"), (next.GetProperty("markerPathPrefix").GetString(), next.GetProperty("minConfidence").GetString()));
        Assert.Equal("invalid_arguments", Assert.Throws<PixToolException>(() => ComparisonResultQuery.Read(store, reference, minConfidence: "sure")).Detail.Code);
    }
}
