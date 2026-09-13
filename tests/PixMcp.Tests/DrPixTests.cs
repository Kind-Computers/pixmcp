using Microsoft.Extensions.Logging.Abstractions;
using PixMcp.Pix;
using PixMcp.Pix.Handles;
using PixMcp.Tools;
using Xunit;

namespace PixMcp.Tests;

/// <summary>Dr. PIX metric structuring, families, per-event ranges, summaries and partial job results (PIX-free).</summary>
public sealed class DrPixTests
{
    private static DrPixRawMetric M(string name, string? label, object? value, string? group = "Timings") => new(group, name, label, value, 0);

    private static readonly DrPixRawMetric[] Viewport =
    [
        M("Measured Duration", "Test", "Measured Duration"), M("Measured Duration", "Time in us", 4320L),
        M("1x1 Viewport", "Test", "1x1 Viewport"), M("1x1 Viewport", "Time in us", 3648L), M("1x1 Viewport", "% Faster", 15.555555555555555),
    ];

    [Fact]
    public void TimingExperimentsPairBaselineAndExperiment()
    {
        IReadOnlyList<DrPixRecordDto> records = DrPixMetrics.Records(Viewport);
        Assert.Equal(new[] { "Measured Duration", "1x1 Viewport" }, records.Select(r => r.Name));
        Assert.Equal(new[] { "Time in us" }, records[0].Values.Keys);
        DrPixTimingDto timing = DrPixMetrics.Timing(records)!;
        Assert.Equal(("Measured Duration", "1x1 Viewport", 4.32, 3.648, 0.672, (double?)15.56, "detected"),
            (timing.Baseline, timing.Experiment, timing.BaselineMs, timing.ExperimentMs, timing.SavedMs, timing.SavedPercent, timing.Semantics));
        Assert.Contains("15.56 %", DrPixMetrics.Implication("basic", timing));
    }

    [Fact]
    public void SlowerExperimentsAndOrderInferenceKeepTheirSign()
    {
        DrPixTimingDto earlyZ = DrPixMetrics.Timing(DrPixMetrics.Records([M("Default", "Test", "Default"), M("Default", "Time in us", 4096L),
            M("Force Early Z", "Test", "Force Early Z"), M("Force Early Z", "Time in us", 5120L), M("Force Early Z", "% Faster", -25L)]))!;
        Assert.Equal(((double?)-25, "detected"), (earlyZ.SavedPercent, earlyZ.Semantics));
        Assert.Contains("cost 25 % more", DrPixMetrics.Implication("depthStencil", earlyZ));
        DrPixTimingDto inferred = DrPixMetrics.Timing(DrPixMetrics.Records([M("Original", "Time (ns)", 10816L, "Varying"), M("Minimal", "Time (ns)", 8112L, "Varying")]))!;
        Assert.Equal(("inferredByOrder", 0.010816, (double?)25), (inferred.Semantics, inferred.BaselineMs, inferred.SavedPercent));
        Assert.Null(DrPixMetrics.Timing(DrPixMetrics.Records([M("Total Quads", "Count", 21969L, "Quad Histogram")])));
    }

    [Fact]
    public void TableStyleMetricsBecomeRecordsWithNormalizedValues()
    {
        IReadOnlyList<DrPixRecordDto> records = DrPixMetrics.Records(
        [
            M("0", "Result Id", "0", "DebugBreak Hits"), M("0", "Global Id", "<a href=\"pixnavlink:///4/7\"></a>", "DebugBreak Hits"), M("0", "Line Number", 0L, "DebugBreak Hits"),
            M("1", "Result Id", "1", "DebugBreak Hits"), M("Quad Efficiency", null, "Quad Efficiency", "Quad Histogram"), M("Quad Efficiency", "Count", "98.31%", "Quad Histogram"),
        ]);
        Assert.Equal(new[] { "0", "1", "Quad Efficiency" }, records.Select(r => r.Name));
        Assert.Equal(new[] { "Result Id", "Global Id", "Line Number" }, records[0].Values.Keys);
        Assert.Equal("pixnavlink:///4/7", records[0].Values["Global Id"]);
        Assert.Equal(98.31, records[2].Values["Count"]);
    }

    [Theory]
    [InlineData("1x1 Viewport", "Basic Information", "PIX", "basic")]
    [InlineData("ForceEarlyZ", "Depth/Stencil", "PIX", "depthStencil")]
    [InlineData("ExecuteIndirect Minimal Command Count", "ExecuteIndirect", "PIX", "executeIndirect")]
    [InlineData("NonUniformResourceIndex", "NonUniformResourceIndex", "PIX", "shaderCorrectness")]
    [InlineData("DebugBreak", "DebugBreak", "PIX", "debugBreak")]
    [InlineData("Quad Histogram", "Primitives and Rasterization", "PIX", "rasterization")]
    [InlineData("Vertex/Primitive Efficiency", "Primitives and Rasterization", "PIX", "rasterization")]
    [InlineData("Tight Resource Alignment", "Tight Resource Alignment", "PIX", "memory")]
    [InlineData("Vendor Stalls", "Vendor", "GPU_PLUGIN", "vendor")]
    [InlineData("Mystery", "Other", "PIX", "other")]
    public void ExperimentsMapToFamilies(string name, string category, string source, string family)
    {
        Assert.Equal(family, DrPixFamilies.Of(name, category, source).Family);
        Assert.Contains(family, DrPixFamilies.Names);
    }

    [Fact]
    public void WholeCaptureOnlyExperimentsDoNotSupportRanges()
    {
        Assert.False(DrPixFamilies.SupportsRanges("Tight Resource Alignment"));
        Assert.True(DrPixFamilies.SupportsRanges("1x1 Viewport"));
    }

    [Fact]
    public void PerEventRangesKeepWorkEventsWithGpuIdsInsideTheScope()
    {
        EventRecord[] events =
        [
            FrameSegmentationTests.E(0, "Pass"), FrameSegmentationTests.E(1, "Hello", 0, 5), FrameSegmentationTests.E(2, "DrawInstanced", 0, 6, "x"),
            FrameSegmentationTests.E(3, "DrawInstanced", 0, api: "x"), FrameSegmentationTests.E(4, "Dispatch", gpuId: 7, api: "x"), FrameSegmentationTests.E(5, "ExecuteIndirect", 0, 8, "x"),
        ];
        Assert.Equal(new uint[] { 2, 5 }, DrPixRanges.WorkEvents(events, i => EventNavigation.IsWithin(events, i, 0)));
        Assert.Equal(new uint[] { 2, 4, 5 }, DrPixRanges.WorkEvents(events, _ => true));
    }

    [Fact]
    public void SummaryRanksSavingsAndCountsFailuresAndIgnoredRanges()
    {
        DrPixTimingDto viewport = DrPixMetrics.Timing(DrPixMetrics.Records(Viewport))!;
        var range = new DrPixRangeDto(0, new EventRef("gpu-1", 0, 15), new EventRef("gpu-1", 0, 21), 6, 9, 4, false, "Frame/Triangle pass");
        DrPixRunDto[] runs =
        [
            new(0, "1x1 Viewport", Guid.Empty, "Basic Information", "basic", "", "PIX", 0, "0x00000000", true, false, viewport, DrPixMetrics.Records(Viewport), []),
            new(1, "Tight Resource Alignment", Guid.Empty, "Tight Resource Alignment", "memory", "", "PIX", 0, "0x00000000", true, true, null, [], []),
            new(2, "Bandwidth", Guid.Empty, "Bandwidth", "memory", "", "PIX", 0, "0x80004005", false, false, null, [], []),
        ];
        var saving = new DrPixSavingDto(0, "1x1 Viewport", "basic", 0, viewport.BaselineMs, viewport.ExperimentMs, viewport.SavedMs, viewport.SavedPercent,
            DrPixMetrics.Implication("basic", viewport));
        DrPixSummaryDto summary = DrPixMetrics.Summary(runs, [saving]);
        Assert.Equal(("1x1 Viewport", 1, 1, 0), (summary.BestSaving!.Experiment, summary.FailedRuns, summary.RangeIgnoredRuns, summary.UnknownSemantics));
        Assert.Equal(new[] { "1x1 Viewport (range 0): 15.56 %" }, summary.Ranking);
        DrPixTableDto table = DrPixMetrics.Table(runs, [range]);
        Assert.Equal(3, table.Rows.Count);
        Assert.Equal(table.Columns.Count, table.Rows[0].Length);
        Assert.Equal(15.56, table.Rows[0][table.Columns.ToList().IndexOf("savedPercent")]);
    }

    [Fact]
    public void PartialSnapshotsReplaceEachOtherAndFinishingReleasesThem()
    {
        using var store = new ResultStore();
        var job = new Job("job-1", "drpix", "test", store);
        job.SetPartial(new { runs = 1 });
        string first = job.PartialResultRef!;
        job.SetPartial(new { runs = 2 });
        string second = job.PartialResultRef!;
        Assert.NotEqual(first, second);
        Assert.False(store.IsAvailable(first));
        Assert.True(store.IsAvailable(second));
        Assert.Equal(second, job.ToDto().PartialResultRef);
        job.Succeed(new { done = true });
        Assert.Null(job.PartialResultRef);
        Assert.False(store.IsAvailable(second));
        Assert.True(store.IsAvailable(job.ResultRef!));
    }

    [Fact]
    public void FailedJobsKeepTheirPartialResult()
    {
        using var store = new ResultStore();
        var job = new Job("job-2", "drpix", "test", store);
        job.Fail(new PartialResultException(new InvalidOperationException("replay lost"), new { partial = true, runsCompleted = 1 }));
        JobDto dto = job.ToDto();
        Assert.Equal(("failed", "available"), (dto.Status, dto.ResultState));
        Assert.Contains("replay lost", dto.Error!.Message);
        Assert.Equal("pix_result_read", Assert.Single(dto.NextCalls).Tool);
    }

    [Fact]
    public async Task RunNeedsOneRangeSourceKnownFamiliesAndABoundedRunCountBeforeAnyJob()
    {
        using var worker = new PixWorker();
        using var session = new PixSession(worker, NullLogger<PixSession>.Instance);
        var jobs = new JobManager(worker, session);
        await Assert.ThrowsAnyAsync<Exception>(() => DrPixTools.Run(session, jobs, "gpu-1"));
        await Assert.ThrowsAnyAsync<Exception>(() => DrPixTools.Run(session, jobs, "gpu-1", wholeCapture: true, families: ["nope"]));
        await Assert.ThrowsAnyAsync<Exception>(() => DrPixTools.Run(session, jobs, "gpu-1", wholeCapture: true, maxRuns: 0));
        Assert.Empty(jobs.All);
    }
}
