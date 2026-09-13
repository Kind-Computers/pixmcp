using PixMcp.Pix;
using PixMcp.Pix.Handles;
using Xunit;

namespace PixMcp.Tests;

/// <summary>Each overview insight fires at its threshold and stays silent just below it; suggestions only name registered tools.</summary>
public sealed class InsightsTests
{
    private static readonly VendorIdentity Vendor = new(GpuVendor.Nvidia, "Test GPU", "captureQueueAdapter");

    private static CaptureOverviewDto Overview(IReadOnlyList<QueueOverviewDto>? queues = null, IReadOnlyList<OverviewPassDto>? passes = null,
        OverviewFramesDto? frames = null, ReplayProvenance? provenance = null)
        => new("gpu-1", new OverviewCaptureDto("c.wpix", 10, 1, Vendor, new OverviewFrameInfoDto(1, 0, "present", null), OverviewBuilder.Semantics),
            queues ?? [], new Dictionary<string, CapabilityDto>(), Metrics.Denominators, provenance, passes ?? [], [], null, frames, []);

    private static OverviewInsightFacts Facts(int work = 5, int timed = 5, ulong workEop = 100, ulong executeIndirect = 0, ulong[]? sorted = null,
        int barriers = 0, int markers = 3, bool experiments = false)
        => new(work, timed, workEop, executeIndirect, sorted ?? [10, 20, 30, 40, 50], barriers, markers, experiments);

    private static OverviewPassDto Pass(string name, ulong inclusive, ulong self, double spanPercent, int depth = 1, bool exceed = false, ulong overflow = 0, int children = 2)
        => new(new EventRef("gpu-1", 0, (uint)name.Length), Enumerable.Repeat("Frame", depth).ToArray(), name, "measured",
            new DurationDto(inclusive, Metrics.Ms(inclusive), spanPercent, null, null, 1), new DurationDto(self, Metrics.Ms(self), null, null, null, null), exceed, overflow, children, 1);

    private static QueueOverviewDto Queue(ulong busy, ulong span)
        => new(0, "Graphics", "GRAPHICS", 10, new Dictionary<string, int>(), new QueueTotals(0, busy, span, span - busy, busy, false, 3, 0, 0, span, "topEop"));

    private static OverviewFramesDto Frames(int count, double p50, double? p95)
        => new(count, false, Enumerable.Range(0, count).Select(i => new OverviewFrameDto(i, 0, 0, null, false, 1, (ulong)(i + 1), 0, null)).ToArray(),
            new OverviewFramePercentilesDto(p50, p95, p95, p95 ?? p50));

    private static string[] Ids(CaptureOverviewDto overview, OverviewInsightFacts? facts = null) => InsightRules.Evaluate(overview, facts ?? Facts()).Select(i => i.Id).ToArray();

    [Fact]
    public void QuietOverviewHasNoInsights()
        => Assert.Empty(Ids(Overview([Queue(90, 100)], [Pass("Shadow", 30, 5, 30)])));

    [Fact]
    public void EachRuleFiresAtItsThresholdAndNotBelow()
    {
        Assert.Equal(new[] { "queue_idle_high" }, Ids(Overview([Queue(50, 100)])));
        Assert.Empty(Ids(Overview([Queue(51, 100)])));

        Assert.Contains("single_pass_dominates", Ids(Overview(passes: [Pass("Lighting", 50, 1, 50)])));
        Assert.DoesNotContain("single_pass_dominates", Ids(Overview(passes: [Pass("Lighting", 50, 1, 49.9)])));
        Assert.DoesNotContain("single_pass_dominates", Ids(Overview(passes: [Pass("Frame", 90, 1, 90, depth: 0)])));

        Assert.Equal(new[] { "pass_self_time_high" }, Ids(Overview(passes: [Pass("Post", 100, 50, 10)])));
        Assert.Empty(Ids(Overview(passes: [Pass("Post", 100, 49, 10)])));
        Assert.Empty(Ids(Overview(passes: [Pass("Post", 100, 50, 9)])));

        ulong[] tail = [10, 10, 10, 10, 10, 10, 10, 10, 10, 50];
        Assert.Equal(new[] { "long_tail_draws" }, Ids(Overview(), Facts(work: 10, timed: 10, sorted: tail)));
        Assert.Empty(Ids(Overview(), Facts(work: 10, timed: 10, sorted: [10, 10, 10, 10, 10, 10, 10, 10, 10, 49])));
        Assert.Empty(Ids(Overview(), Facts(work: 9, timed: 9, sorted: tail[1..])));

        Assert.Equal(new[] { "no_markers" }, Ids(Overview(), Facts(markers: 0)));
        Assert.Equal(new[] { "executeindirect_heavy" }, Ids(Overview(), Facts(executeIndirect: 50)));
        Assert.Empty(Ids(Overview(), Facts(executeIndirect: 49)));
        Assert.Equal(new[] { "many_barriers" }, Ids(Overview(), Facts(work: 10, timed: 10, barriers: 20)));
        Assert.Empty(Ids(Overview(), Facts(work: 10, timed: 10, barriers: 19)));
        Assert.Empty(Ids(Overview(), Facts(work: 11, timed: 11, barriers: 20)));

        Assert.Equal(new[] { "untimed_events" }, Ids(Overview(), Facts(work: 5, timed: 4)));
        Assert.Equal(new[] { "children_exceed_measured" }, Ids(Overview(passes: [Pass("Shadow", 30, 5, 30, exceed: true, overflow: 5)])));
        Assert.Equal(new[] { "frame_variance_high" }, Ids(Overview(frames: Frames(5, 2, 3))));
        Assert.Empty(Ids(Overview(frames: Frames(5, 2, 2.9))));
        Assert.Empty(Ids(Overview(frames: Frames(4, 2, null))));
        Assert.Equal(new[] { "vendor_mismatch" }, Ids(Overview(provenance: new ReplayProvenance("gpuReplay", "2606.18", null, null, null, "s", "Arc", "intel", "nvidia", true))));
    }

    [Fact]
    public void WarningsComeFirstAtMostEightAndCallsNameRegisteredTools()
    {
        CaptureOverviewDto busy = Overview([Queue(10, 100)],
            [Pass("Lighting", 60, 40, 60, exceed: true, overflow: 5)], Frames(6, 2, 4),
            new ReplayProvenance("gpuReplay", "2606.18", null, null, null, "s", "Arc", "intel", "nvidia", true));
        IReadOnlyList<InsightDto> insights = InsightRules.Evaluate(busy,
            Facts(work: 10, timed: 8, workEop: 100, executeIndirect: 60, sorted: [1, 1, 1, 1, 1, 1, 1, 1, 1, 9], barriers: 40, markers: 0, experiments: true));
        Assert.Equal(InsightRules.MaxInsights, insights.Count);
        Assert.Equal(new[] { "warning", "warning", "warning", "warning" }, insights.Take(4).Select(i => i.Severity));
        Assert.All(insights.Skip(4), i => Assert.Equal("info", i.Severity));
        Assert.All(insights.SelectMany(i => i.NextCalls), call => Assert.True(ToolRegistry.Accepts(call), call.Tool));

        InsightDto dominant = InsightRules.Evaluate(Overview(passes: [Pass("Lighting", 50, 1, 50)]), Facts(experiments: true)).Single(i => i.Id == "single_pass_dominates");
        Assert.Contains(dominant.NextCalls, c => c.Tool == "pix_gpu_drpix_run");
        Assert.DoesNotContain(InsightRules.Evaluate(Overview(passes: [Pass("Lighting", 50, 1, 50)]), Facts()).Single().NextCalls, c => c.Tool == "pix_gpu_drpix_run");
    }

    [Fact]
    public void RegistryKnowsToolsAndParameters()
    {
        Assert.True(ToolRegistry.Has("pix_gpu_overview", "frameIndex", "includeInsights"));
        Assert.True(ToolRegistry.Has("pix_gpu_bottleneck", "scope", "evidence"));
        Assert.False(ToolRegistry.Accepts(new ToolCallDto("pix_gpu_bottleneck", new { handle = "gpu-1", bogus = 1 })));
        Assert.False(ToolRegistry.Has("pix_gpu_overview", "noSuchParameter"));
        Assert.True(ToolRegistry.Accepts(new ToolCallDto("pix_gpu_timing_tree", new { handle = "gpu-1", scope = new EventRef("gpu-1", 0, 1), sortBy = "self", queueIndex = (int?)null })));
        Assert.False(ToolRegistry.Accepts(new ToolCallDto("pix_gpu_timing_tree", new { handle = "gpu-1", bogus = 1 })));
        Assert.True(ToolRegistry.Accepts(new ToolCallDto("pix_gpu_queue_overlap", new { handle = "gpu-1" })));
        Assert.False(ToolRegistry.Accepts(new ToolCallDto("pix_gpu_queue_overlap", new { handle = "gpu-1", bogus = 1 })));
    }

    [Fact]
    public void BuilderAttachesInsightsOnlyWithTiming()
    {
        EventRecord[] events = [FrameSegmentationTests.E(0, "DrawInstanced", gpuId: 0, api: "x"), FrameSegmentationTests.E(1, "DrawInstanced", gpuId: 1, api: "x")];
        EventTimingRow[] rows = [FrameSegmentationTests.Row(0, events[0], 0, 10)];
        OverviewInputs Inputs(bool timing) => new("gpu-1", "c.wpix", Vendor,
            [new OverviewQueueInput(0, "Graphics", "GRAPHICS", 2, events, EventNavigation.ChildCounts(events), timing ? TimingTree.Build(events, rows) : null, timing ? rows : null)],
            new Dictionary<string, CapabilityDto>(), null, timing, (_, _) => true, null);
        CaptureOverviewDto timed = OverviewBuilder.Build(Inputs(true), new OverviewOptions(10));
        Assert.Equal(new[] { "untimed_events", "no_markers" }, timed.Insights!.Select(i => i.Id));
        Assert.Null(OverviewBuilder.Build(Inputs(true), new OverviewOptions(10, IncludeInsights: false)).Insights);
        Assert.Null(OverviewBuilder.Build(Inputs(false), new OverviewOptions(10)).Insights);
    }
}
