using PixMcp.Pix;
using PixMcp.Pix.Handles;
using PixMcp.Tools;
using Xunit;

namespace PixMcp.Tests;

/// <summary>Rollups, group keys and shader identities over the timing tree fixture.</summary>
public sealed class RollupTests
{
    // 0 Frame (unmeasured marker)
    //   1 Shadow pass (unmeasured marker)
    //     2 Draw (10 ns)
    //     3 Draw (20 ns)
    //   4 Main pass (measured by PIX as 8 ns)
    //     5 Marker (unmeasured)
    //       6 Dispatch (5 ns)
    //     7 Draw (untimed)
    // 8 Present (1 ns, top level)
    private static readonly EventRecord[] Events =
    {
        new(0, uint.MaxValue, uint.MaxValue, "Frame", "", 0, 0),
        new(1, uint.MaxValue, 0, "Shadow pass", "", 0, 0),
        new(2, 1, 1, "DrawInstanced", "DrawInstanced(3)", 0, 0),
        new(3, 2, 1, "DrawInstanced", "DrawInstanced(6)", 0, 0),
        new(4, uint.MaxValue, 0, "Main pass", "", 0, 0),
        new(5, uint.MaxValue, 4, "Marker", "", 0, 0),
        new(6, 3, 5, "Dispatch", "Dispatch(1,1,1)", 0, 0),
        new(7, 4, 4, "DrawInstanced", "DrawInstanced(3)", 0, 0),
        new(8, 5, uint.MaxValue, "Present", "", 0, 0),
    };

    private static readonly EventTimingRow[] Rows =
    {
        Row(2, 10), Row(3, 20), Row(4, 8), Row(6, 5), Row(8, 1),
        new(0, 7, 4, "DrawInstanced", "DrawInstanced(3)", 0, 0, 0, GpuCaptureHandle.TimingNone),
    };

    private static EventTimingRow Row(uint index, ulong eop) => new(0, index, index, Events[index].Name, Events[index].ApiCallData, 0, eop, 0, eop);

    private static RollupInputs Inputs(Func<int, uint, bool>? inScope = null)
        => new("gpu-1", [new RollupQueueInput(0, Events, EventNavigation.ChildCounts(Events), TimingTree.Build(Events, Rows), Rows)], inScope ?? ((_, _) => true), inScope is null);

    private static RollupResult Run(RollupRequest request, RollupInputs? inputs = null) => Rollups.Compute(request, inputs ?? Inputs());

    private static ShaderInfoDto Shader(string key)
    {
        Assert.True(ShaderIdentity.TryParseShaderKey(key, out string stage, out string hash));
        return new ShaderInfoDto(null, 0, "1", stage, hash, null, null, null, null, 0, "0x0", []);
    }

    [Fact]
    public void MarkerDepthGroupsPixMarkerSpansAndReconcilesToTheSumOfRoots()
    {
        RollupResult depth1 = Run(new RollupRequest("markerDepth", 1));
        Assert.Equal(new[] { ("Frame", 38UL, TimingSemantics.Mixed), (GroupKeys.NoMarker, 1UL, Rollups.Remainder) }, depth1.Rows.Select(r => (r.Key, r.Sum!.Ns, r.Semantics)));
        Assert.True(depth1.Reconciles);

        RollupResult depth2 = Run(new RollupRequest("markerDepth", 2));
        Assert.Equal(new[] { ("Frame/Shadow pass", 30UL, TimingSemantics.DerivedSum), ("Frame/Main pass", 8UL, TimingSemantics.Measured), (GroupKeys.NoMarker, 1UL, Rollups.Remainder) },
            depth2.Rows.Select(r => (r.Key, r.Sum!.Ns, r.Semantics)));
        Assert.Equal(39UL, depth2.Rows.Aggregate(0UL, (sum, r) => sum + r.Sum!.Ns));
        Assert.Equal((new EventRef("gpu-1", 0, 1), (int?)1), (depth2.Rows[0].Representative, depth2.Rows[0].Sum!.Rank));
        Assert.Equal(new[] { "Frame", "Shadow pass" }, Assert.IsType<string[]>(depth2.Rows[0].KeyDetail));
        Assert.Contains(depth2.Rows[0].NextCalls, c => c.Tool == "pix_gpu_timing_tree");

        RollupResult scoped = Run(new RollupRequest("markerDepth", 2), Inputs((_, i) => i is 4 or 5 or 6 or 7));
        Assert.Equal(new[] { "Frame/Main pass" }, scoped.Rows.Select(r => r.Key));
        Assert.Null(scoped.Reconciles);
        Assert.Equal(3UL, Run(new RollupRequest("markerDepth", 2, Metric: "self")).Rows.Single(r => r.Key == "Frame/Main pass").Sum!.Ns);
        Assert.Equal(new[] { "Frame/Main pass/Marker" }, Run(new RollupRequest("markerDepth", 3)).Rows.Where(r => r.Semantics != Rollups.Remainder).Select(r => r.Key));
    }

    [Fact]
    public void WorkRowsGroupByTheirNearestMarkerWithStatistics()
    {
        RollupResult byMarker = Run(new RollupRequest("marker"));
        Assert.Equal(new[] { "Shadow pass", "Marker", "Main pass" }, byMarker.Rows.Select(r => r.Key));
        RollupRowDto shadow = byMarker.Rows[0];
        Assert.Equal(("Shadow pass", 2, 2, 0, 30UL, TimingSemantics.DerivedSum), (shadow.Key, shadow.Count, shadow.Measured, shadow.Untimed, shadow.Sum!.Ns, shadow.Semantics));
        Assert.Equal(((ulong?)15, (ulong?)10, (ulong?)20, (ulong?)10, (ulong?)20), (shadow.AvgNs, shadow.MinNs, shadow.MaxNs, shadow.P50Ns, shadow.P95Ns));
        Assert.Equal(new EventRef("gpu-1", 0, 3), shadow.Representative);
        RollupRowDto main = byMarker.Rows[2];
        Assert.Equal((1, 0, 1, TimingSemantics.Untimed), (main.Count, main.Measured, main.Untimed, main.Semantics));
        Assert.Null(main.Sum);
        Assert.Equal(35UL, byMarker.PopulationNs);
        Assert.Null(byMarker.Reconciles);
        Assert.Equal(new[] { "Frame/Shadow pass", "Frame/Main pass/Marker", "Frame/Main pass" }, Run(new RollupRequest("markerPath")).Rows.Select(r => r.Key));
    }

    [Fact]
    public void KindApiQueueAndCommandListGroupingsCoverEveryNonMarkerEvent()
    {
        Assert.Equal(new[] { ("draw", 3, 30UL), ("dispatch", 1, 5UL), ("present", 1, 1UL) }, Run(new RollupRequest("kind")).Rows.Select(r => (r.Key, r.Count, r.Sum?.Ns ?? 0)));
        Assert.Equal(new[] { ("DrawInstanced", 3), ("Dispatch", 1) }, Run(new RollupRequest("api", Kind: "work")).Rows.Select(r => (r.Key, r.Count)));
        Assert.Equal(new[] { ("0", 5, 36UL) }, Run(new RollupRequest("queue")).Rows.Select(r => (r.Key, r.Count, r.Sum!.Ns)));
        Assert.Equal(new[] { "0" }, Run(new RollupRequest("commandList")).Rows.Select(r => r.Key));
        Assert.Contains(Run(new RollupRequest("kind")).Rows[0].NextCalls, c => c.Tool == "pix_gpu_timing_events");
    }

    [Fact]
    public void FiltersAndSortOrdersApplyAfterGrouping()
    {
        Assert.Equal(new[] { "Shadow pass" }, Run(new RollupRequest("marker", MinPercent: 100)).Rows.Select(r => r.Key));
        Assert.Equal(new[] { "Shadow pass" }, Run(new RollupRequest("marker", MinCount: 2)).Rows.Select(r => r.Key));
        Assert.Equal(new[] { "Main pass", "Marker", "Shadow pass" }, Run(new RollupRequest("marker", SortBy: "key", Descending: false)).Rows.Select(r => r.Key));
        Assert.Equal(new[] { "Shadow pass", "Main pass", "Marker" }, Run(new RollupRequest("marker", SortBy: "count")).Rows.Select(r => r.Key));
        Assert.Equal(new[] { "Main pass", "Marker", "Shadow pass" }, Run(new RollupRequest("marker", Descending: false)).Rows.Select(r => r.Key));
    }

    [Fact]
    public void ShaderAndPipelineGroupingsUseStableIdentities()
    {
        var shaderKeys = new Dictionary<uint, string[]> { [2] = ["hash:VS:A", "hash:PS:B"], [3] = ["hash:VS:A", "hash:PS:C"], [6] = ["hash:CS:D"] };
        RollupInputs inputs = Inputs() with
        {
            ShaderKeys = e => shaderKeys.GetValueOrDefault(e.EventIndex, []),
            PsoKey = e => shaderKeys.TryGetValue(e.EventIndex, out string[]? keys) ? ShaderIdentity.PsoKey(keys.Select(Shader)) : null,
        };
        RollupResult byShader = Run(new RollupRequest("shader"), inputs);
        Assert.Equal(new[] { ("hash:VS:A", 2, 30UL), ("hash:PS:C", 1, 20UL), ("hash:PS:B", 1, 10UL), ("hash:CS:D", 1, 5UL), (GroupKeys.IncompleteIdentity, 1, 0UL) },
            byShader.Rows.Select(r => (r.Key, r.Count, r.Sum?.Ns ?? 0)));
        Assert.Equal(35UL, byShader.PopulationNs);
        Assert.Contains(byShader.Notes, n => n.Contains("once per bound shader"));
        Assert.Contains(byShader.Rows[0].NextCalls, c => c.Tool == "pix_gpu_shader_uses");

        Assert.Equal(new[] { "pso:PS:C;VS:A", "pso:PS:B;VS:A", "pso:CS:D", GroupKeys.IncompleteIdentity }, Run(new RollupRequest("psoKey"), inputs).Rows.Select(r => r.Key));
    }

    [Fact]
    public void CounterAggregatesFollowUnitsAndMarkerDepthReadsPixMarkerRounds()
    {
        CounterInfo[] counters = [new(1, "GPU Utilization (%)", "", "FLOAT64", []), new(2, "Primitives", "", "UINT64", [])];
        var values = new Dictionary<uint, object?[]> { [1] = [90.0, 999UL], [2] = [50.0, 100UL], [3] = [70.0, 300UL] };
        RollupInputs withCounters = Inputs() with { Counters = counters, CounterValues = (_, i) => values.GetValueOrDefault(i) };

        RollupRowDto shadow = Run(new RollupRequest("marker", Metric: "counter", Normalize: "perMs"), withCounters).Rows[0];
        RollupCounterDto utilization = shadow.Counters![0], primitives = shadow.Counters[1];
        Assert.Equal(("avg", (double?)60, 2, "eventRows", (double?)null), (utilization.Aggregate, utilization.Value, utilization.Samples, utilization.ValueSource, utilization.PerMs));
        Assert.Equal(("sum", (double?)400), (primitives.Aggregate, primitives.Value));
        Assert.Equal(400 / (30 / 1e6), primitives.PerMs!.Value, 3);

        RollupRowDto round = Run(new RollupRequest("markerDepth", 2, Metric: "counter"), withCounters).Rows[0];
        Assert.Equal(("pixMarkerRound", (double?)90), (round.Counters![0].ValueSource, round.Counters[0].Value));
        Assert.Contains(Run(new RollupRequest("marker", Metric: "counter"), withCounters).Notes, n => n.Contains("playback rounds"));
    }

    [Fact]
    public void GroupKeysFollowMarkerAncestryAndEventKinds()
    {
        int[] children = EventNavigation.ChildCounts(Events);
        Assert.Equal("Marker", GroupKeys.Marker(Events, children, 6));
        Assert.Equal("Main pass", GroupKeys.Marker(Events, children, 7));
        Assert.Equal(GroupKeys.NoMarker, GroupKeys.Marker(Events, children, 8));
        Assert.Equal("Frame/Main pass/Marker", GroupKeys.MarkerPath(Events, 6));
        Assert.Equal(("present", "0", "Present"), (GroupKeys.Of("kind", 0, Events, children, 8), GroupKeys.Of("commandList", 0, Events, children, 8), GroupKeys.Of("api", 0, Events, children, 8)));
        Assert.Equal(new[] { 1, 2, 2, 2, 2, 3, 3, 2, 0 }, GroupKeys.MarkersAtOrAbove(Events, children));
    }

    [Fact]
    public void ShaderAndPipelineKeysIgnoreCaseAndSlotOrder()
    {
        Assert.Equal("hash:PS:ABC", ShaderIdentity.ShaderKey("ps", "abc"));
        Assert.Null(ShaderIdentity.ShaderKey("PS", null));
        Assert.Null(ShaderIdentity.ShaderKey("PS", " "));
        Assert.Equal("pso:PS:B;VS:A", ShaderIdentity.PsoKey([Shader("hash:VS:A"), Shader("hash:PS:B")]));
        Assert.Equal(ShaderIdentity.PsoKey([Shader("hash:PS:B"), Shader("hash:VS:A")]), ShaderIdentity.PsoKey([Shader("hash:VS:A"), Shader("hash:PS:B")]));
        Assert.Null(ShaderIdentity.PsoKey([Shader("hash:VS:A"), new ShaderInfoDto(null, 1, "2", "PS", null, null, null, null, null, 0, "0x0", [])]));
        Assert.Null(ShaderIdentity.PsoKey([]));
        Assert.True(ShaderIdentity.TryParseShaderKey("HASH:ps:abc", out string stage, out string hash));
        Assert.Equal(("PS", "ABC"), (stage, hash));
        Assert.False(ShaderIdentity.TryParseShaderKey("pso:PS:B", out _, out _));
        Assert.False(ShaderIdentity.TryParseShaderKey("hash:PS", out _, out _));
        Assert.Equal(new[] { "hash:PS:B", "hash:VS:A" }, ShaderIdentity.ShaderKeysOf("pso:PS:B;VS:A"));
    }

    [Fact]
    public void ShaderIndexKeysEventsAndRanksTheInventoryByGpuTime()
    {
        EventRef draw = new("gpu-1", 0, 2), other = new("gpu-1", 0, 3), broken = new("gpu-1", 0, 4);
        static ShaderOccurrence Occurrence(EventRef e, int slot, string stage, string? hash)
            => new(new ShaderInfoDto(new ShaderRef(e, slot), slot, "1", stage, hash, null, null, null, null, 0, "0x0", []), ["Frame"]);
        var index = new ShaderIndex([Occurrence(draw, 0, "VS", "a"), Occurrence(draw, 1, "PS", "b"), Occurrence(other, 0, "VS", "a"), Occurrence(other, 1, "PS", "c"),
            Occurrence(broken, 0, "VS", "a")], [], [broken]);
        Assert.Equal(new[] { "hash:VS:A", "hash:PS:B" }, index.ShaderKeysAt(draw));
        Assert.Equal("pso:PS:B;VS:A", index.PsoKeyOf(draw));
        Assert.Null(index.PsoKeyOf(broken));
        Assert.Empty(index.ShadersAt(new EventRef("gpu-1", 0, 9)));
        Assert.Equal(new ShaderRef(draw, 0), index.FirstReference("hash:VS:A"));
        Assert.Null(index.FirstReference("hash:VS:ZZ"));

        var eop = new Dictionary<EventRef, ulong> { [draw] = 10, [other] = 50 };
        ShaderInventoryItemDto[] ranked = index.Inventory(null, null, null, _ => true, sortBy: "gpuTime", eop: e => eop.TryGetValue(e, out ulong ns) ? ns : null).ToArray();
        Assert.Equal(new[] { "hash:VS:A", "hash:PS:C", "hash:PS:B" }, ranked.Select(r => r.ShaderKey));
        Assert.Equal(((ulong?)60, 3), (ranked[0].GpuTimeNs, ranked[0].UseCount));
    }

    [Fact]
    public void CombinedPreparationsRunOnlyTheMissingParts()
    {
        int ranA = 0, ranB = 0;
        bool bReady = false;
        var a = new Preparation<GpuCaptureHandle>("timing", "timing", "A", _ => true, (_, _) => ranA++);
        var b = new Preparation<GpuCaptureHandle>("shader-index", "shader-index", "B", _ => bReady, (_, _) => { ranB++; bReady = true; }) { JoinKeys = ["inspection"] };
        Preparation<GpuCaptureHandle> both = PixMcp.Tools.Tools.Combine("gpu-1", [a, b]);
        Assert.Equal("timing+shader-index", both.Key);
        Assert.Equal(new[] { "timing", "shader-index", "inspection" }, both.JoinKeys);
        Assert.False(both.IsReady(null!));
        both.Prepare(null!, null!);
        Assert.Equal((0, 1), (ranA, ranB));
        Assert.True(both.IsReady(null!));
        Assert.Same(a, PixMcp.Tools.Tools.Combine("gpu-1", [a]));
    }
}
