using System.Text.Json;
using PixMcp.Pix;
using PixMcp.Pix.Handles;
using Xunit;

namespace PixMcp.Tests;

/// <summary>Comparison totals, marker-path rollups, noise, provenance warnings, ordinal matching, binding sets and line diffs (PIX-free).</summary>
public sealed class ComparisonRollupTests
{
    private static readonly ReplayProvenance Provenance = new("gpuReplay", "test", "GPU 0", null, null, "EOP intervals; not frame time");

    private static ComparisonEvent Work(string handle, int queue, uint index, string name, string[] path, ulong ns, ulong spread, string kind = "draw")
        => new ComparisonEvent(new EventRef(handle, queue, index), path, name, kind, false, ns, TimingSemantics.Measured, null, new Dictionary<string, JsonElement>())
        { SpreadNs = spread, Samples = 2 };

    private static ComparisonSnapshot Capture(string handle, ulong graphicsBusy, ulong computeBusy, ReplayProvenance provenance, params (int Queue, ComparisonEvent Event)[] events)
        => new(handle,
        [
            new ComparisonQueue(0, "Graphics", "GRAPHICS", events.Where(e => e.Queue == 0).Select(e => e.Event).ToArray()) { BusyNs = graphicsBusy },
            new ComparisonQueue(1, "Compute", "COMPUTE", events.Where(e => e.Queue == 1).Select(e => e.Event).ToArray()) { BusyNs = computeBusy },
        ], provenance, []);

    private static (ComparisonSnapshot A, ComparisonSnapshot B) Pair(ReplayProvenance? candidateProvenance = null)
        => (Capture("a", 40, 5, Provenance,
                (0, Work("a", 0, 1, "ShadowDraw", ["Frame", "Shadow"], 10, 1)), (0, Work("a", 0, 2, "LightDraw", ["Frame", "Lighting"], 20, 5)),
                (0, Work("a", 0, 3, "LightDraw2", ["Frame", "Lighting"], 5, 2)), (1, Work("a", 1, 1, "Dispatch", ["Compute"], 5, 0, "dispatch"))),
            Capture("b", 60, 5, candidateProvenance ?? Provenance,
                (0, Work("b", 0, 7, "ShadowDraw", ["Frame", "Shadow"], 30, 1)), (0, Work("b", 0, 8, "LightDraw", ["Frame", "Lighting"], 20, 5)),
                (0, Work("b", 0, 9, "LightDraw2", ["Frame", "Lighting"], 8, 2)), (1, Work("b", 1, 4, "Dispatch", ["Compute"], 5, 0, "dispatch"))));

    [Fact]
    public void TotalsRollUpByMarkerPathAndInlineRowsSkipAncestorsAndDescendants()
    {
        var (a, b) = Pair();
        ComparisonResultDto result = CaptureComparison.Compare(a, b, options: new ComparisonOptions(RollupDepth: 2, Repeats: 2, RepeatMethod: "recollect"));
        ComparisonTotalsDto totals = result.Totals!;
        Assert.Equal((45UL, 65UL, 20m, (double?)44.44, (double?)100),
            (totals.BaselineBusyNs, totals.CandidateBusyNs, totals.DeltaNs, totals.DeltaPercent, totals.ExplainedByTopChangesPercent));
        Assert.Equal(2, totals.PerQueue.Count);
        Assert.Equal(new[] { ("Frame", 23m), ("Frame/Shadow", 20m), ("Frame/Lighting", 3m), ("Compute", 0m) }, result.ByMarkerPath!.Select(r => (r.MarkerPath, r.DeltaNs)));
        Assert.Equal((double?)100, result.ByMarkerPath![0].ShareOfTotalDelta);
        Assert.Equal(new[] { "Frame", "Compute" }, ComparisonRollup.Inline(result.ByMarkerPath, 25).Select(r => r.MarkerPath));
        Assert.Equal((2, "recollect", (ulong?)1), (result.Noise!.Repeats, result.Noise.RepeatMethod, result.Noise.NoiseFloorNs));
        Assert.All(result.Items, c => Assert.Equal(("draw", "high", false), (c.Kind, c.Confidence, c.BelowNoiseFloor)));
        Assert.Empty(result.Warnings!);
        Assert.False(result.ProvenanceMismatch);
    }

    [Fact]
    public void ProvenanceMismatchesWarnAndSingleRepeatsHaveNoNoiseFloor()
    {
        var (a, b) = Pair(Provenance with { Adapter = "GPU 1", PixBuild = "other" });
        ComparisonResultDto result = CaptureComparison.Compare(a, b);
        Assert.Equal(new[] { "adapter", "pixBuild" }, result.Warnings!.Select(w => w.Field));
        Assert.True(result.ProvenanceMismatch);
        Assert.Null(result.Noise!.NoiseFloorNs);
        Assert.NotNull(result.Noise.Warning);
        Assert.Empty(CaptureComparison.Compare(a, b, options: new(SameHandle: true)).Warnings!);
    }

    [Fact]
    public void OrdinalMatchingPairsEqualCountsWithLowConfidence()
    {
        ComparisonEvent Draw(string h, uint i, ulong ns) => Work(h, 0, i, "Draw", ["Frame"], ns, 0);
        ComparisonSnapshot a = Capture("a", 20, 0, Provenance, (0, Draw("a", 1, 10)), (0, Draw("a", 2, 10)));
        ComparisonSnapshot b = Capture("b", 25, 0, Provenance, (0, Draw("b", 5, 10)), (0, Draw("b", 6, 15)));
        Assert.Single(CaptureComparison.Compare(a, b).Ambiguous);
        ComparisonResultDto ordinal = CaptureComparison.Compare(a, b, options: new(OrdinalMatching: true));
        Assert.Equal(2, ordinal.MatchedCount);
        EventChange change = Assert.Single(ordinal.Items);
        Assert.Equal(("ordinalWithinPath", "low", 6U), (change.MatchMethod, change.Confidence, change.Candidate.EventIndex));
    }

    [Fact]
    public void BindingsCompareAsSetsAndSoftFieldsOnlyOnRequest()
    {
        JsonElement Resources(object bindings, bool soft = false) => CaptureComparison.Normalize(new { views = new[] { new { type = "SRV", bindings } } }, soft);
        ComparisonResultDto Diff(JsonElement x, JsonElement y) => CaptureComparison.Compare(
            Capture("a", 0, 0, Provenance, (0, Work("a", 0, 1, "Draw", ["Frame"], 10, 0) with { Sections = new Dictionary<string, JsonElement> { ["resources"] = x } })),
            Capture("b", 0, 0, Provenance, (0, Work("b", 0, 1, "Draw", ["Frame"], 10, 0) with { Sections = new Dictionary<string, JsonElement> { ["resources"] = y } })));
        var slot0 = new { register = 0, space = 0, descriptorHeapIndex = 3, eventSource = "eventScopedView" };
        var slot1 = new { register = 1, space = 0, descriptorHeapIndex = 4, eventSource = "eventScopedView" };
        var moved = new { register = 0, space = 0, descriptorHeapIndex = 9, eventSource = "capturedApiArgument" };
        var rebound = new { register = 2, space = 0, descriptorHeapIndex = 3, eventSource = "eventScopedView" };
        Assert.Empty(Diff(Resources(new[] { slot0, slot1 }), Resources(new[] { slot1, slot0 })).Items);
        Assert.Empty(Diff(Resources(new[] { slot0 }), Resources(new[] { moved })).Items);
        Assert.NotEmpty(Diff(Resources(new[] { slot0 }, soft: true), Resources(new[] { moved }, soft: true)).Items);
        FieldChange field = Assert.Single(Assert.Single(Diff(Resources(new[] { slot0 }), Resources(new[] { rebound })).Items).Fields);
        Assert.Equal("/views/0/bindings/0/register", field.Path);
    }

    [Fact]
    public void LineDiffsCountEditsReportFirstDifferencesAndCapLargeInputs()
    {
        TextDiffResult diff = TextDiff.Lines("a\nb\nc\nd", "a\nx\nc\nd\ne");
        Assert.Equal((1, 2, 3, false), (diff.Removed, diff.Added, diff.Unchanged, diff.Truncated));
        Assert.Equal(3, diff.FirstDifferences.Count);
        Assert.Contains(diff.FirstDifferences, d => d is { Op: "delete", BaselineLine: 2, Text: "b" });
        Assert.Contains(diff.FirstDifferences, d => d is { Op: "insert", CandidateLine: 2, Text: "x" });
        Assert.Contains(diff.FirstDifferences, d => d is { Op: "insert", CandidateLine: 5, Text: "e" });
        TextDiffResult same = TextDiff.Lines("same\r\ntext", "same\ntext");
        Assert.Equal((0, 0, 2), (same.Added, same.Removed, same.Unchanged));
        Assert.True(TextDiff.Lines(string.Join("\n", Enumerable.Range(0, TextDiff.MaxLines + 1)), "x").Truncated);
    }

    [Fact]
    public void CodeDiffsCoverEachChangedStageHashOnce()
    {
        ComparisonEvent Shaded(string h, uint i, string hash) => Work(h, 0, i, "Draw" + i, ["Frame"], 10, 0) with { Shaders = [new ComparisonShader("PS", hash, 1)] };
        ComparisonSnapshot a = Capture("a", 0, 0, Provenance, (0, Shaded("a", 1, "old")), (0, Shaded("a", 2, "old"))) with
            { HlslByHash = new Dictionary<string, string> { ["old"] = "float4 main() {\nreturn 0;\n}" } };
        ComparisonSnapshot b = Capture("b", 0, 0, Provenance, (0, Shaded("b", 1, "new")), (0, Shaded("b", 2, "new"))) with
            { HlslByHash = new Dictionary<string, string> { ["new"] = "float4 main() {\nreturn 1;\n}" } };
        ShaderCodeDiffDto codeDiff = Assert.Single(CaptureComparison.Compare(a, b).CodeDiffs!);
        Assert.Equal(("PS", "old", "new", 1, 1), (codeDiff.Stage, codeDiff.BaselineHash, codeDiff.CandidateHash, codeDiff.Added, codeDiff.Removed));
        Assert.Equal("pix_gpu_shader_code", Assert.Single(codeDiff.NextCalls).Tool);
    }
}
