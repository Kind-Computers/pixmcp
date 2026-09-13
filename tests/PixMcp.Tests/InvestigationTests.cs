using System.Text.Json;
using PixMcp.Pix;
using PixMcp.Pix.Handles;
using PixMcp.Tools;
using Xunit;

namespace PixMcp.Tests;

public class InvestigationTests
{
    private static readonly ReplayProvenance Provenance = new("gpuReplay", "test", null, null, null, "EOP intervals; not frame time");
    private static ComparisonEvent Event(string handle, uint index, string name = "Draw", ulong? ns = 10,
        string? hash = null, IReadOnlyDictionary<string, JsonElement>? sections = null)
        => new(new(handle, 0, index), ["Frame", "Lighting"], name, "draw", false, ns, TimingSemantics.Measured, hash, sections ?? new Dictionary<string, JsonElement>());
    private static ComparisonSnapshot Snapshot(string handle, params ComparisonEvent[] events)
        => new(handle, [new(0, "Graphics", "DIRECT", events)], Provenance, []);

    [Fact]
    public void ReorderedEventIdsMatchByPathAndChangedShaderRemainsComparable()
    {
        var before = Event("gpu-1", 3, ns: 10, hash: "a", sections: new Dictionary<string, JsonElement> { ["shaders"] = JsonSerializer.SerializeToElement(new { hash = "a" }) });
        var after = Event("gpu-2", 999, ns: 25, hash: "b", sections: new Dictionary<string, JsonElement> { ["shaders"] = JsonSerializer.SerializeToElement(new { hash = "b" }) });
        var result = CaptureComparison.Compare(Snapshot("gpu-1", before), Snapshot("gpu-2", after));
        var change = Assert.Single(result.Items);
        Assert.Equal(15m, change.DeltaNs);
        Assert.Equal(0d, change.DeltaMs);
        Assert.Equal(150d, change.DeltaPercent);
        Assert.Equal(TimingSemantics.Measured, change.BaselineSemantics);
        Assert.Equal(after.EventRef, change.Candidate);
        Assert.Equal("/hash", Assert.Single(change.Fields).Path);
    }

    [Fact]
    public void RepeatedWorkIsAmbiguousUntilExplicitlyPaired()
    {
        var aa = new[] { Event("a", 1), Event("a", 2) }; var bb = new[] { Event("b", 4), Event("b", 5) };
        var result = CaptureComparison.Compare(Snapshot("a", aa), Snapshot("b", bb));
        Assert.Equal(0, result.MatchedCount);
        Assert.Equal(2, Assert.Single(result.Ambiguous).Baseline.Count);
        Assert.Empty(result.BaselineOnly);
        result = CaptureComparison.Compare(Snapshot("a", aa), Snapshot("b", bb), eventPairs: [new(aa[1].EventRef, bb[0].EventRef)]);
        Assert.Equal(2, result.MatchedCount);
        Assert.Empty(result.Ambiguous);
    }

    [Fact]
    public void ZeroBaselinesAndUnavailableTimingsHaveNoInventedPercent()
    {
        var result = CaptureComparison.Compare(Snapshot("a", Event("a", 1, ns: 0)), Snapshot("b", Event("b", 2, ns: 4)));
        Assert.Null(Assert.Single(result.Items).DeltaPercent);
        result = CaptureComparison.Compare(Snapshot("a", Event("a", 1, ns: null)), Snapshot("b", Event("b", 2, ns: null)));
        Assert.Empty(result.Items);
        Assert.Equal(1, result.MatchedCount);
    }

    [Fact]
    public void DuplicateAncestorMarkersKeepUniqueChildrenAmbiguous()
    {
        ComparisonEvent Marker(string h, uint i) => Event(h, i, "Lighting") with { IsMarker = true, Kind = "marker", MarkerPath = ["Frame"] };
        var a = Snapshot("a", Marker("a", 1), Marker("a", 2), Event("a", 3, "UniqueDraw", hash: "same"));
        var b = Snapshot("b", Marker("b", 8), Marker("b", 9), Event("b", 6, "UniqueDraw", hash: "same"));
        var result = CaptureComparison.Compare(a, b);
        Assert.Equal(0, result.MatchedCount);
        Assert.Equal(2, result.Ambiguous.Count);
        Assert.All(result.Ambiguous, item => Assert.Equal("markerPaths", item.Kind));
        result = CaptureComparison.Compare(a, b, eventPairs: [new(a.Queues[0].Events[2].EventRef, b.Queues[0].Events[2].EventRef)]);
        Assert.Equal(1, result.MatchedCount);
        Assert.Single(result.Ambiguous);
    }

    [Fact]
    public void MissingNativeSectionsDoNotBecomeStateChanges()
    {
        var a = new Dictionary<string, JsonElement> { ["pipeline"] = CaptureComparison.Normalize(new { state = new { unavailable = true, reason = "not supported" } }) };
        var b = new Dictionary<string, JsonElement> { ["pipeline"] = CaptureComparison.Normalize(new { state = new { depthEnable = true } }) };
        var result = CaptureComparison.Compare(Snapshot("a", Event("a", 1, sections: a)), Snapshot("b", Event("b", 9, sections: b)));
        Assert.Empty(result.Items);
    }

    [Fact]
    public void DuplicateQueuesRequireExplicitPairs()
    {
        var a = Snapshot("a", Event("a", 1)); var b = Snapshot("b", Event("b", 2));
        a = a with { Queues = [a.Queues[0] with { Name = "" }, a.Queues[0] with { Index = 1, Name = "", Events = [] }] };
        b = b with { Queues = [b.Queues[0] with { Name = "" }, b.Queues[0] with { Index = 1, Name = "", Events = [] }] };
        var result = CaptureComparison.Compare(a, b);
        Assert.Equal(0, result.MatchedCount);
        Assert.Equal("queues", Assert.Single(result.Ambiguous).Kind);
        result = CaptureComparison.Compare(a, b, eventPairs: [new(a.Queues[0].Events[0].EventRef, b.Queues[0].Events[0].EventRef)]);
        Assert.Equal(1, result.MatchedCount);
        Assert.Empty(result.Ambiguous);
        result = CaptureComparison.Compare(a, b, [new(0, 0), new(1, 1)]);
        Assert.Equal(1, result.MatchedCount);
    }

    [Fact]
    public void ShaderSetsIgnoreEnumerationOrderAndKeepIdentityNamedDefines()
    {
        object Shader(string stage, string id) => new { stage, defines = new Dictionary<string, string> { ["ID"] = id, ["index"] = "5" } };
        var a = CaptureComparison.NormalizeSet([Shader("VS", "1"), Shader("PS", "2")]);
        var b = CaptureComparison.NormalizeSet([Shader("PS", "2"), Shader("VS", "1")]);
        Assert.Equal(a.Select(e => e.GetRawText()), b.Select(e => e.GetRawText()));
        var changed = CaptureComparison.NormalizeSet([Shader("PS", "3"), Shader("VS", "1")]);
        Assert.False(a.Select(e => e.GetRawText()).SequenceEqual(changed.Select(e => e.GetRawText())));
        Assert.Equal("2", a.Single(e => e.GetProperty("stage").GetString() == "PS").GetProperty("defines").GetProperty("ID").GetString());
    }

    [Fact]
    public void PipelineNormalizationIgnoresCaptureIdentityButPreservesState()
    {
        var a = CaptureComparison.Normalize(new { apiObjectId = "0x1", desc = new { depthEnable = true }, resourceRef = new ResourceRef("a", "0x1") });
        var b = CaptureComparison.Normalize(new { apiObjectId = "0x9", desc = new { depthEnable = true }, resourceRef = new ResourceRef("b", "0x9"), nextCalls = new[] { new ToolCallDto("pix_gpu_resource", new { handle = "b" }) } });
        Assert.True(JsonElement.DeepEquals(a, b));
        Assert.False(JsonElement.DeepEquals(a, CaptureComparison.Normalize(new { desc = new { depthEnable = false } })));
    }

    [Fact]
    public void GlobalTreeBudgetAndContinuationReachLaterSiblings()
    {
        var events = new[]
        {
            new EventRecord(0, 0, uint.MaxValue, "Frame", "", 0, 0),
            new EventRecord(1, 1, 0, "A", "", 0, 0), new EventRecord(2, 2, 0, "B", "", 0, 0),
            new EventRecord(3, 3, 1, "C", "", 0, 0),
        };
        var rows = events.Select(e => new EventTimingRow(0, e.Index, e.GpuId, e.Name, "", 0, 1, 0, 10));
        var tree = TimingTree.Build(events, rows);
        var root = new EventRef("gpu-1", 0, 0);
        var first = CountersTools.BuildTimingTree("gpu-1", 0, tree, root, 0, 25, 4, 1, 0, 0, "inclusive", Provenance);
        Assert.Equal(1, first.ReturnedNodes);
        Assert.Equal(1, first.NextOffset);
        Assert.Equal(1u, Assert.Single(first.Children).Index);
        Assert.Single(first.Children[0].NextCalls);
        var next = CountersTools.BuildTimingTree("gpu-1", 0, tree, root, first.NextOffset!.Value, 25, 4, 1, 0, 0, "inclusive", Provenance);
        Assert.Equal(2u, Assert.Single(next.Children).Index);
        Assert.Null(next.NextOffset);
    }

    [Fact]
    public void CounterValuesKeepDifferentIdentitiesAndExactLargeIntegers()
    {
        var values = CounterQuery.Values([1, 2, 3], [ulong.MaxValue, 42UL, long.MinValue]);
        Assert.Equal("18446744073709551615", values["1"]);
        Assert.Equal(42UL, values["2"]);
        Assert.Equal("-9223372036854775808", values["3"]);
        Assert.Equal((decimal)ulong.MaxValue, CounterQuery.Number(ulong.MaxValue));
        Assert.Null(CounterQuery.Number(double.NaN));
    }
}
