using System.Text.Json;
using PixMcp.Pix;
using PixMcp.Pix.Handles;
using PixMcp.Tools;
using Xunit;
using static PixMcp.Tests.FrameSegmentationTests;

namespace PixMcp.Tests;

/// <summary>
/// pix_gpu_overview v2 from a synthetic capture: Frame > Triangle pass > (label, 3 draws), an ExecuteIndirect and a Clear under
/// Frame, a Present, and a Dispatch under a compute marker on a second queue.
/// </summary>
public sealed class OverviewTests
{
    private static readonly VendorIdentity Vendor = new(GpuVendor.Nvidia, "Test GPU", "captureQueueAdapter");

    private static OverviewQueueInput Queue(int index, string name, string type, EventRecord[] events, EventTimingRow[] rows, bool timing)
        => new(index, name, type, (uint)events.Length, events, EventNavigation.ChildCounts(events), timing ? TimingTree.Build(events, rows, index) : null, timing ? rows : null);

    private static OverviewInputs Inputs(bool timing = true, Func<int, uint, bool>? scope = null)
    {
        EventRecord[] graphics =
        [
            E(0, "Frame"), E(1, "Triangle pass", 0), E(2, "Hello", 1, 1),
            E(3, "DrawInstanced", 1, 2, "<DrawInstanced><VertexCountPerInstance>3</VertexCountPerInstance></DrawInstanced>"),
            E(4, "DrawInstanced", 1, 3, "x"), E(5, "DrawIndexedInstanced", 1, 4, "x"), E(6, "ExecuteIndirect", 0, 5, "x"),
            E(7, "ClearRenderTargetView", 0, 6, "x"), E(8, "Present", gpuId: 7, api: "x"),
        ];
        EventTimingRow[] graphicsRows =
        [
            Row(0, graphics[2], 11_000, 1_000), Row(0, graphics[3], 12_000, 8_000, 10_000), Row(0, graphics[4], 22_000, 3_000, 20_000),
            Row(0, graphics[5], 26_000, 14_000, 25_000), Row(0, graphics[6], 42_000, 6_000, 40_000), Row(0, graphics[7], 48_000, 2_000), Row(0, graphics[8], 50_000, 1_000),
        ];
        EventRecord[] compute = [E(0, "Wave compute"), E(1, "Dispatch", 0, 10, "x")];
        EventTimingRow[] computeRows = [Row(1, compute[1], 5_000, 30_000, 0)];
        return new OverviewInputs("gpu-1", "capture.wpix", Vendor,
            [Queue(0, "Graphics", "GRAPHICS", graphics, graphicsRows, timing), Queue(1, "Compute", "COMPUTE", compute, computeRows, timing)],
            new Dictionary<string, CapabilityDto>(), null, timing, scope ?? ((_, _) => true), null);
    }

    [Fact]
    public void RanksPassesAndWorkWithKindsHistogramAndCaptureFacts()
    {
        CaptureOverviewDto overview = OverviewBuilder.Build(Inputs(), new OverviewOptions(10));
        IReadOnlyDictionary<string, int> kinds = overview.Queues[0].Kinds;
        Assert.Equal(OverviewBuilder.KindOrder, kinds.Keys);
        Assert.Equal((4, 2, 1, 1, 1, 1, 2, 1, 0), (kinds["work"], kinds["draw"] - 1, kinds["executeIndirect"], kinds["clear"], kinds["present"], kinds["label"], kinds["marker"], overview.Queues[1].Kinds["dispatch"], kinds["copy"]));

        Assert.Equal(new[] { "Dispatch", "DrawIndexedInstanced", "DrawInstanced", "ExecuteIndirect", "DrawInstanced" }, overview.TopDraws.Select(d => d.Name));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, overview.TopDraws.Select(d => d.Eop.Rank!.Value));
        Assert.DoesNotContain(overview.TopDraws, d => d.Kind is "clear" or "present" or "label");
        Assert.Equal("<DrawInstanced><VertexCountPerInstance>3</VertexCountPerInstance></DrawInstanced>", overview.TopDraws.Single(d => d.EventRef.EventIndex == 3).Parameters!.Raw);
        Assert.All(overview.TopDraws.Where(d => d.Kind != "executeIndirect"), d => Assert.NotNull(d.Exec));

        OverviewPassDto frame = overview.TopPasses.Single(p => p.Name == "Frame"), triangle = overview.TopPasses.Single(p => p.Name == "Triangle pass");
        Assert.Equal((4, 3), (frame.WorkCount, frame.ChildCount));
        Assert.Equal((3, 4), (triangle.WorkCount, triangle.ChildCount));
        Assert.Equal(1, overview.TopPasses.Single(p => p.Name == "Wave compute").WorkCount);
        Assert.DoesNotContain(overview.TopPasses, p => p.Name == "Hello");
        Assert.True(frame.Inclusive.Ns >= triangle.Inclusive.Ns);
        Assert.True(frame.Self.Ns <= frame.Inclusive.Ns);
        Assert.Equal(Enumerable.Range(1, overview.TopPasses.Count), overview.TopPasses.Select(p => p.Inclusive.Rank!.Value));

        OverviewHistogramDto histogram = overview.Histogram!;
        Assert.Equal(new[] { "<10us", "10us-20us", "20us-50us" }, histogram.Buckets.Select(b => b.Label));
        Assert.Equal(overview.TopDraws.Count, histogram.Buckets.Sum(b => b.Count));
        Assert.Equal((3, 17_000UL, "<10us"), (histogram.Buckets[0].Count, histogram.Buckets[0].SumNs, histogram.Buckets[0].Label));
        Assert.Equal((1, 1, 20_000UL, (ulong?)50_000UL), (histogram.Buckets[1].Count, histogram.Buckets[2].Count, histogram.Buckets[2].MinNs, histogram.Buckets[2].MaxNs));
        Assert.Equal(overview.Queues.Sum(q => (decimal)q.Totals!.BusyNs), (decimal)histogram.DenominatorNs);
        Assert.Contains("queues [0, 1]", histogram.Denominator);

        Assert.Equal(("capture.wpix", 11L, 2, 1, (int?)0, "present"),
            (overview.Capture.Path, overview.Capture.EventTotal, overview.Capture.QueueCount, overview.Capture.Frames.Count, overview.Capture.Frames.PresentQueueIndex, overview.Capture.Frames.FrameAssignment));
        Assert.Null(overview.Frames);
        QueuePairOverlapDto pair = Assert.Single(overview.Overlap!.Pairs);
        Assert.Equal((0, 1, "overlapping", 25_000UL), (pair.QueueA, pair.QueueB, pair.Verdict, pair.Overlap.Ns));
        Assert.Null(overview.Overlap.Queues);
        Assert.Equal("nvidia", JsonSerializer.SerializeToElement(overview.Capture.Adapter, Json.Options).GetProperty("vendor").GetString());
        Assert.Contains(overview.NextCalls, c => c.Tool == "pix_gpu_timing_tree");
        OutputSchemaTests.AssertMatches(JsonSerializer.SerializeToElement(overview, Json.Options), StructuredToolResults.SchemaFor("pix_gpu_overview"));
    }

    [Fact]
    public void ScopeFrameIndexLimitAndMetadataOnly()
    {
        CaptureOverviewDto scoped = OverviewBuilder.Build(Inputs(scope: (q, i) => q == 0 && i is >= 1 and <= 5), new OverviewOptions(10));
        Assert.Equal(new uint[] { 5, 3, 4 }, scoped.TopDraws.Select(d => d.EventRef.EventIndex));
        Assert.Equal(new[] { "Triangle pass" }, scoped.TopPasses.Select(p => p.Name));
        Assert.Contains("queue 0", scoped.Histogram!.Denominator);

        Assert.Equal(2, OverviewBuilder.Build(Inputs(), new OverviewOptions(2)).TopDraws.Count);
        Assert.Equal(5, OverviewBuilder.Build(Inputs(), new OverviewOptions(10, 0)).TopDraws.Count);
        Assert.Equal("invalid_arguments", Assert.Throws<PixToolException>(() => OverviewBuilder.Build(Inputs(), new OverviewOptions(10, 1))).Detail.Code);

        CaptureOverviewDto metadata = OverviewBuilder.Build(Inputs(timing: false), new OverviewOptions(10));
        Assert.Empty(metadata.TopDraws);
        Assert.Empty(metadata.TopPasses);
        Assert.Null(metadata.Histogram);
        Assert.Null(metadata.Queues[0].Totals);
        Assert.Null(metadata.Denominators);
        Assert.Equal((4, "indexOnly"), (metadata.Queues[0].Kinds["work"], metadata.Capture.Frames.FrameAssignment));

        OverviewInputs uncached = Inputs() with { Queues = [Inputs().Queues[0], new OverviewQueueInput(1, "Compute", "COMPUTE", 2, null, null, null, null)] };
        CaptureOverviewDto partial = OverviewBuilder.Build(uncached, new OverviewOptions(10));
        Assert.Empty(partial.Queues[1].Kinds);
        Assert.Equal(11L, partial.Capture.EventTotal);
    }

    [Fact]
    public void ScopedMetadataWaitsForRequiredCachesAndUsesSnapshotEventsWhenReady()
    {
        OverviewInputs inputs = Inputs(timing: false);
        var selection = new ScopeSelection(inputs.Handle, null, null, "Frame/Triangle pass", ["Frame", "Triangle pass"]);
        OverviewInputs cold = inputs with { Queues = inputs.Queues.Select(q => q with { Events = null, ChildCounts = null }).ToArray() };
        Assert.Null(OverviewBuilder.BuildScoped(cold, new OverviewOptions(10), selection));

        OverviewInputs partial = inputs with { Queues = [inputs.Queues[0], cold.Queues[1]] };
        Assert.Null(OverviewBuilder.BuildScoped(partial, new OverviewOptions(10), selection));
        var root = new ScopeSelection(inputs.Handle, null, new EventRef(inputs.Handle, 0, 1), null, null);
        CaptureOverviewDto rootOnly = Assert.IsType<CaptureOverviewDto>(OverviewBuilder.BuildScoped(partial, new OverviewOptions(10), root));
        Assert.Equal(root.Root, rootOnly.Scope!.Root);
        Assert.Empty(rootOnly.Queues[1].Kinds);

        CaptureOverviewDto ready = Assert.IsType<CaptureOverviewDto>(OverviewBuilder.BuildScoped(inputs, new OverviewOptions(10), selection));
        Assert.Equal(new EventRef(inputs.Handle, 0, 1), Assert.Single(ready.Scope!.MatchedRoots));
        Assert.Equal(1, ready.Scope.MatchedRootCount);
        ToolCallDto next = Assert.Single(ready.NextCalls);
        Assert.Equal(selection.MarkerPathPrefix, JsonSerializer.SerializeToElement(next.Arguments).GetProperty("markerPathPrefix").GetString());

        var unrestricted = new ScopeSelection(inputs.Handle, null, null, null, null);
        Assert.NotNull(OverviewBuilder.BuildScoped(cold, new OverviewOptions(10), unrestricted));
        Assert.Null(OverviewBuilder.BuildScoped(partial, new OverviewOptions(10, 1), unrestricted));
        Assert.Equal(PixErrors.Codes.InvalidArguments, Assert.Throws<PixToolException>(() => OverviewBuilder.BuildScoped(inputs, new OverviewOptions(10, 1), unrestricted)).Detail.Code);
    }

    [Fact]
    public void BriefDropsExplanationsButKeepsRankedRows()
    {
        CaptureOverviewDto overview = OverviewBuilder.Build(Inputs(), new OverviewOptions(10)) with
        {
            Capabilities = new Dictionary<string, CapabilityDto> { ["timing"] = new("supported", "long reason", ["note"]) },
        };
        JsonElement brief = JsonSerializer.SerializeToElement(InvestigationTools.Present(overview, Shaping.Options("objects", true, null, null, 0)), Json.Options);
        Assert.False(brief.GetProperty("capabilities").GetProperty("timing").TryGetProperty("reason", out _));
        Assert.Equal("supported", brief.GetProperty("capabilities").GetProperty("timing").GetProperty("state").GetString());
        Assert.False(brief.TryGetProperty("denominators", out JsonElement denominators) && denominators.ValueKind != JsonValueKind.Null);
        Assert.False(brief.GetProperty("queues")[0].GetProperty("kinds").TryGetProperty("copy", out _));
        Assert.Equal(3, brief.GetProperty("histogram").GetProperty("buckets").GetArrayLength());
        Assert.Equal(overview.TopDraws.Count, brief.GetProperty("topDraws").GetArrayLength());
        Assert.All(brief.GetProperty("topDraws").EnumerateArray(), d => Assert.False(d.TryGetProperty("parameters", out JsonElement p) && p.ValueKind != JsonValueKind.Null));
        OutputSchemaTests.AssertMatches(brief, StructuredToolResults.SchemaFor("pix_gpu_overview"));
    }

    [Fact]
    public void TableFormatTurnsRankedSectionsIntoPositionalTables()
    {
        CaptureOverviewDto overview = OverviewBuilder.Build(Inputs(), new OverviewOptions(10));
        JsonElement json = JsonSerializer.SerializeToElement(InvestigationTools.Present(overview, Shaping.Options("table", false, null, null, 0)), Json.Options);
        JsonElement draws = json.GetProperty("topDraws");
        string[] columns = draws.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()!).ToArray();
        Assert.Equal(overview.TopDraws.Count, draws.GetProperty("rows").GetArrayLength());
        Assert.Contains("eop.ns", columns);
        Assert.Equal(columns.Length, draws.GetProperty("rows")[0].GetArrayLength());
        Assert.Equal("EventRef", draws.GetProperty("legend").GetProperty("refs").GetProperty("eventRef").GetProperty("kind").GetString());
        Assert.Equal(overview.TopPasses.Count, json.GetProperty("topPasses").GetProperty("rows").GetArrayLength());
        Assert.Equal(overview.Queues.Count, json.GetProperty("queues").GetArrayLength());
        OutputSchemaTests.AssertMatches(json, StructuredToolResults.SchemaFor("pix_gpu_overview"));

        JsonElement brief = JsonSerializer.SerializeToElement(InvestigationTools.Present(overview, Shaping.Options("table", true, null, null, 0)), Json.Options);
        Assert.True(brief.GetProperty("topDraws").GetProperty("columns").GetArrayLength() < columns.Length);
        Assert.Equal("invalid_arguments", Assert.Throws<PixToolException>(() => Shaping.Options("grid", false, null, null, 0)).Detail.Code);
    }

    [Fact]
    public void MultiFrameCapturesReportPerFrameBusyAndPercentiles()
    {
        var inputs = new OverviewInputs("gpu-1", "multi.wpix", Vendor,
            [Queue(0, "Graphics", "GRAPHICS", Graphics, GraphicsRows, true), Queue(1, "Compute", "COMPUTE", Compute, ComputeRows, true)],
            new Dictionary<string, CapabilityDto>(), null, true, (_, _) => true, null);
        CaptureOverviewDto overview = OverviewBuilder.Build(inputs, new OverviewOptions(10));
        OverviewFramesDto frames = overview.Frames!;
        Assert.Equal((4, false), (frames.Total, frames.Truncated));
        Assert.Equal(new ulong[] { 10, 10, 15, 15 }, frames.PerFrame.Select(f => f.BusyNs));
        Assert.Equal(new[] { 2, 1, 2, 2 }, frames.PerFrame.Select(f => f.WorkEvents));
        Assert.Equal(new ulong?[] { 12, 20, 20, null }, frames.PerFrame.Select(f => f.WindowNs));
        Assert.Equal((true, (double?)null), (frames.PerFrame[3].Partial, frames.Percentiles.P95BusyMs));
        Assert.Contains(overview.NextCalls, c => c.Tool == "pix_gpu_overview" && JsonSerializer.SerializeToElement(c.Arguments).GetProperty("frameIndex").GetInt32() == 2);

        CaptureOverviewDto third = OverviewBuilder.Build(inputs, new OverviewOptions(10, 2));
        Assert.Equal(new[] { (0, 4u), (1, 2u) }, third.TopDraws.Select(d => (d.EventRef.QueueIndex, d.EventRef.EventIndex)).Order());
        Assert.Equal(2, third.Capture.Frames.SelectedFrame);
        Assert.DoesNotContain(third.NextCalls, c => c.Tool == "pix_gpu_overview");
    }
}
