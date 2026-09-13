using PixMcp.Pix;
using PixMcp.Pix.Handles;
using Xunit;

namespace PixMcp.Tests;

/// <summary>Event inspection over a synthetic queue whose draws have distinct TOP and EOP starts.</summary>
public sealed class EventInspectionTests
{
    private const ulong None = GpuCaptureHandle.TimingNone;
    private const string Draw = "<DrawInstanced><this>ID3D12GraphicsCommandList obj#11</this><VertexCountPerInstance>3</VertexCountPerInstance><InstanceCount>1</InstanceCount><StartVertexLocation>0</StartVertexLocation><StartInstanceLocation>0</StartInstanceLocation></DrawInstanced>";
    private const string Dispatch = "<Dispatch><this>ID3D12GraphicsCommandList obj#5</this><ThreadGroupCountX>4</ThreadGroupCountX><ThreadGroupCountY>1</ThreadGroupCountY><ThreadGroupCountZ>1</ThreadGroupCountZ></Dispatch>";

    // 0 Frame (marker)
    //   1 Pass (marker)
    //     2 DrawInstanced  TOP 100, EOP 105..115
    //     3 DrawInstanced  TOP 110, EOP 115..145 (starts 5 ns before draw 2 ends)
    //     4 DrawInstanced  TOP 150, EOP 150..155
    //   5 Dispatch         no TOP, EOP 160..180
    private static readonly EventRecord[] Events =
    [
        new(0, uint.MaxValue, uint.MaxValue, "Frame", "", 0, 0),
        new(1, uint.MaxValue, 0, "Pass", "", 0, 0),
        new(2, 10, 1, "DrawInstanced", Draw, 0, 0),
        new(3, 11, 1, "DrawInstanced", Draw, 0, 0),
        new(4, 12, 1, "DrawInstanced", Draw, 0, 0),
        new(5, 13, 0, "Dispatch", Dispatch, 0, 0),
    ];

    private static readonly EventTimingRow[] Rows =
    [
        new(0, 2, 10, "DrawInstanced", Draw, 100, 10, 105, 10),
        new(0, 3, 11, "DrawInstanced", Draw, 110, 30, 115, 30),
        new(0, 4, 12, "DrawInstanced", Draw, 150, 5, 150, 5),
        new(0, 5, 13, "Dispatch", Dispatch, None, None, 160, 20),
    ];

    private static EventTimingInspectionDto Inspect(uint index, EventRecord[]? events = null, EventTimingRow[]? rows = null)
    {
        events ??= Events;
        rows ??= Rows;
        return EventInspection.Timing(new EventInspectionInput("gpu-1", 0, events, EventNavigation.ChildCounts(events), rows, TimingTree.Build(events, rows),
            ApiCallParser.Parse(events[index].ApiCallData, events[index].Name)), index);
    }

    private static string[] Ids(IReadOnlyList<InsightDto> hints) => hints.Select(h => h.Id).ToArray();

    [Fact]
    public void DrawIsRankedAgainstQueueAndSiblingsAndSeesPipelining()
    {
        EventTimingInspectionDto draw = Inspect(3);
        Assert.Equal(TimingSemantics.Measured, draw.Semantics);
        Assert.Equal((30UL, (double?)66.67, (int?)1), (draw.Eop!.Ns, draw.Eop.PercentOfParent, draw.Eop.Rank));
        Assert.Equal((35UL, (string?)null), (draw.Exec!.Ns, draw.ExecReason));
        Assert.Equal(((ulong?)110, (ulong?)115, (ulong?)145), (draw.TopStartNs, draw.EopStartNs, draw.EopEndNs));
        Assert.Equal(((int?)1, 4, (int?)1), (draw.RankInQueue, draw.TimedWorkEvents, draw.RankInParent));
        Assert.Equal(new SiblingStatsDto(3, 3, 10, 30, 45, 66.67), draw.Siblings);
        Assert.Equal(((bool?)true, (long?)-5, new EventRef("gpu-1", 0, 2)), (draw.PipelinedWithPrevious, draw.PreviousGapNs, draw.PreviousWork));
        Assert.Equal(new PerWorkItemDto("vertices", 3, 10), draw.PerWorkItem);

        EventTimingInspectionDto last = Inspect(4);
        Assert.Equal(((bool?)false, (long?)5, new EventRef("gpu-1", 0, 3), (int?)4), (last.PipelinedWithPrevious, last.PreviousGapNs, last.PreviousWork, last.RankInQueue));
    }

    [Fact]
    public void DispatchWithoutTopTimingAndMarkersExplainMissingExecution()
    {
        EventTimingInspectionDto dispatch = Inspect(5);
        Assert.Null(dispatch.Exec);
        Assert.Equal("TOP timing is unavailable for this event.", dispatch.ExecReason);
        Assert.Equal(((int?)2, (int?)2, 2), (dispatch.RankInQueue, dispatch.RankInParent, dispatch.Siblings!.Count));
        Assert.Equal(new PerWorkItemDto("threadGroups", 4, 5), dispatch.PerWorkItem);
        Assert.Equal(((long?)5, new EventRef("gpu-1", 0, 4)), (dispatch.PreviousGapNs, dispatch.PreviousWork));

        EventTimingInspectionDto pass = Inspect(1);
        Assert.Equal((TimingSemantics.DerivedSum, 45UL, (int?)null), (pass.Semantics, pass.Eop!.Ns, pass.RankInQueue));
        Assert.Equal((double?)69.23, pass.Eop.PercentOfParent);
        Assert.Equal("Derived from children: a marker has no TOP timing of its own.", pass.ExecReason);
        Assert.Null(pass.PipelinedWithPrevious);
        Assert.Null(pass.PerWorkItem);
    }

    [Fact]
    public void AncestorsAreNeverThePreviousWorkEvent()
    {
        EventRecord[] events = [.. Events, new(6, 14, 0, "ExecuteIndirect", "ExecuteIndirect(obj#4, 16, obj#5, 0, NULL, 0)", 0, 0), new(7, 15, 6, "DrawInstanced", Draw, 0, 0)];
        EventTimingRow[] rows = [.. Rows, new(0, 6, 14, "ExecuteIndirect", "", 190, 110, 190, 110), new(0, 7, 15, "DrawInstanced", Draw, 200, 10, 200, 10)];
        EventTimingInspectionDto child = Inspect(7, events, rows);
        Assert.Equal((new EventRef("gpu-1", 0, 5), (long?)20, (bool?)false), (child.PreviousWork, child.PreviousGapNs, child.PipelinedWithPrevious));
    }

    [Fact]
    public void TargetsCountPixelsAtTheMipSliceAndCostPerMegapixel()
    {
        Assert.Equal(307_200UL, EventInspection.PixelCount(640, 480, 1, 0));
        Assert.Equal(307_200UL, EventInspection.PixelCount(640, 480, 4, 1));
        Assert.Equal(1UL, EventInspection.PixelCount(1, 1, 1, 5));
        Assert.Equal(0UL, EventInspection.PixelCount(0, 480, 1, 0));
        var target = new EventTargetDto("renderTarget", null, "RT", "R8G8B8A8_UNORM", 1000, 1000, 1, 0, 1_000_000, null);
        Assert.Equal((double?)30_000, EventInspection.WithCost(target, Metrics.Duration(30_000, null)).NsPerMegapixel);
        Assert.Null(EventInspection.WithCost(target, null).NsPerMegapixel);
        Assert.Null(EventInspection.WithCost(target with { PixelCount = 0 }, Metrics.Duration(30_000, null)).NsPerMegapixel);
    }

    [Fact]
    public void CountersComeFromCachedSetsWithUnitsAndPerMilliseconds()
    {
        var eventRef = new EventRef("gpu-1", 0, 3);
        EventCountersDto none = EventInspection.Counters(eventRef, [], 0, "event", null);
        Assert.Equal("notCollected", none.State);
        Assert.NotEmpty(none.NextCalls);
        Assert.All(none.NextCalls, call => Assert.True(ToolRegistry.Accepts(call), call.Tool));

        CounterInfo[] counters = [new(1, "GPU Utilization (%)", "", "FLOAT64", []), new(2, "Primitives", "", "UINT64", [])];
        EventCountersDto available = EventInspection.Counters(eventRef, [new("a", CounterCollectionCache.Exact, counters, [50.0, 1200UL], true)], 2, "event",
            Metrics.Duration(2_000_000, null));
        EventCounterSetDto set = Assert.Single(available.Sets);
        Assert.Equal(("available", 1, "event", true), (available.State, available.OmittedSets, set.RowKind, set.HasData));
        Assert.Equal(("percent", (double?)null), (set.Values[0].Unit, set.Values[0].PerMs));
        Assert.Equal((double?)600, set.Values[1].PerMs);
        Assert.NotNull(EventInspection.Counters(eventRef, [new("a", CounterCollectionCache.Exact, counters, null, false)], 1, "marker", null).Reason);
    }

    [Fact]
    public void HintsFireAtTheirThresholdsWithCallableFollowUps()
    {
        var draw = new EventRef("gpu-1", 0, 3);
        EventTimingInspectionDto timing = Inspect(3);
        IReadOnlyList<InsightDto> hints = InspectionHints.Evaluate(draw, "draw", 1, timing, ApiCallParser.Parse(Draw), null);
        Assert.Equal(new[] { "pipelined", "dominant_in_parent" }, Ids(hints));
        Assert.All(hints, h => Assert.NotEmpty(h.NextCalls));
        Assert.All(hints.SelectMany(h => h.NextCalls), call => Assert.True(ToolRegistry.Accepts(call), call.Tool));

        // Dominance needs half of the timed siblings' time and at least three siblings.
        Assert.DoesNotContain("dominant_in_parent", Ids(InspectionHints.Evaluate(draw, "draw", 1, timing with { Siblings = timing.Siblings! with { ThisPercentOfSiblings = 49.99 } }, null, null)));
        Assert.DoesNotContain("dominant_in_parent", Ids(InspectionHints.Evaluate(draw, "draw", 1, timing with { Siblings = timing.Siblings! with { Count = 2 } }, null, null)));

        // Work items: zero is a warning and sorts first; dispatches below 64 thread groups are tiny.
        IReadOnlyList<InsightDto> zero = InspectionHints.Evaluate(draw, "draw", 1, timing, ApiCallParser.Parse("DrawInstanced(0, 1, 0, 0)"), null);
        Assert.Equal(("zero_work", "warning"), (zero[0].Id, zero[0].Severity));
        Assert.Equal(new[] { "tiny_dispatch" }, Ids(InspectionHints.Evaluate(draw, "dispatch", 0, null, ApiCallParser.Parse("Dispatch(63, 1, 1)"), null)));
        Assert.Empty(InspectionHints.Evaluate(draw, "dispatch", 0, null, ApiCallParser.Parse("Dispatch(8, 8, 1)"), null));

        // Pipeline latency: TOP-to-EOP-end at least four times the EOP time and 100 us longer.
        EventTimingInspectionDto waited = timing with { Exec = Metrics.Duration(500_000, null), Eop = Metrics.Duration(100_000, null), PipelinedWithPrevious = false, Siblings = null };
        Assert.Equal(new[] { "pipeline_latency" }, Ids(InspectionHints.Evaluate(draw, "draw", 1, waited, null, null)));
        Assert.Empty(InspectionHints.Evaluate(draw, "draw", 1, waited with { Exec = Metrics.Duration(399_999, null) }, null, null));

        // Large render targets: 1920x1080 pixels (samples included) for draws only.
        static EventTargetsDto Targets(ulong width, uint height)
            => new("available", [new EventTargetDto("renderTarget", null, "RT", "R8G8B8A8_UNORM", width, height, 1, 0, EventInspection.PixelCount(width, height, 1, 0), null)]);
        Assert.Equal(new[] { "large_rt_fill" }, Ids(InspectionHints.Evaluate(draw, "draw", null, null, null, Targets(1920, 1080))));
        Assert.Empty(InspectionHints.Evaluate(draw, "draw", null, null, null, Targets(1919, 1080)));
        Assert.Empty(InspectionHints.Evaluate(draw, "dispatch", null, null, null, Targets(3840, 2160)));
    }

    [Fact]
    public void InspectionBindingsDropTheRepeatedEventAndResourceDetail()
    {
        var eventRef = new EventRef("gpu-1", 0, 3);
        var evt = new EventDto(0, 3, 11, 1, "DrawInstanced", Draw, 0, null) { EventRef = eventRef, MarkerPath = ["Frame"] };
        var resource = new ResourceDetailsDto(new ResourceRef("gpu-1", "0xD"), "0xD", "BackBuffer 1", "COMMITTED", "RESOURCE_STATE",
            new { dimension = "TEXTURE2D", alignment = 0, width = 640, height = 480, format = "R8G8B8A8_UNORM", sampleCount = 1, sampleQuality = 0, layout = "UNKNOWN" },
            null, "STATE_COMMON", null, Array.Empty<string>(), new { kind = "committed" }, null);
        var view = new { index = 1, type = "RENDER_TARGET_VIEW", resource = new { apiObjectId = "0xD" }, bindings = new[] { new { index = 0 } }, nextCalls = Array.Empty<ToolCallDto>() };
        var bindings = new EventResourcesDto(eventRef, ["Frame"], evt, 1, 0, 1, null, [new ResourceGroupDto(resource, [view])], [])
        { RootConstantCoverage = [new { unavailable = true }] };

        System.Text.Json.Nodes.JsonObject summary = PixMcp.Tools.InspectionTools.SummarizeBindings(bindings);
        Assert.False(summary.ContainsKey("event") || summary.ContainsKey("eventRef") || summary.ContainsKey("markerPath"));
        Assert.True(summary.ContainsKey("rootConstantCoverage"));
        System.Text.Json.Nodes.JsonObject group = summary["resources"]![0]!.AsObject();
        System.Text.Json.Nodes.JsonObject kept = group["resource"]!.AsObject();
        Assert.Equal(new[] { "resourceRef", "apiObjectId", "name", "type", "desc" }, kept.Select(p => p.Key));
        Assert.Equal(new[] { "dimension", "width", "height", "format", "sampleCount" }, kept["desc"]!.AsObject().Select(p => p.Key));
        Assert.Equal(new[] { "index", "type", "bindings" }, group["views"]![0]!.AsObject().Select(p => p.Key));
    }
}
