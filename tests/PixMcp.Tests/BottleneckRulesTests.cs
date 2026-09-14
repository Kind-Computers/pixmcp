using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using PixMcp.Pix;
using PixMcp.Tools;
using Xunit;

namespace PixMcp.Tests;

/// <summary>Heuristic bottleneck rules: canonical evidence sets, confidence caps, vendor filtering and the embedded rule file (PIX-free).</summary>
public sealed class BottleneckRulesTests
{
    private static BottleneckEvidenceDto E(string source, string metric, double value, string unit = "percent") => new(source, metric, value, unit, "high", "derived", null);

    private static BottleneckVerdictDto Verdict(GpuVendor vendor, params BottleneckEvidenceDto[] evidence) => BottleneckRules.Classify(evidence, vendor, false).Verdict;

    [Fact]
    public void CanonicalEvidenceSetsYieldTheirLimiter()
    {
        Assert.Equal(("pixelShading", "medium", 0.88), Tuple(Verdict(GpuVendor.Nvidia, E("drpix", "drpix.1x1 Viewport.savedPercent", 70))));
        Assert.Equal(("vertexOrGeometry", "medium", 0.4), Tuple(Verdict(GpuVendor.Nvidia, E("counters", "counters.psInvocationsPerPrimitive", 1.2, "ratio"))));
        Assert.Equal(("memoryBandwidth", "medium", 0.7),
            Tuple(Verdict(GpuVendor.Intel, E("counters", "GPU Memory Active", 85), E("counters", "XVE Active", 30))));
        Assert.Equal(("occupancyLatency", "low", 0.3), Tuple(Verdict(GpuVendor.Amd, E("occupancy", "occupancy.averagePercent", 20))));
        Assert.Equal(("occupancyLatency", "medium", 0.7),
            Tuple(Verdict(GpuVendor.Intel, E("counters", "XVE Threads Occupancy All", 20), E("counters", "XVE Stall", 60))));
        Assert.Equal(("cacheMiss", "medium", 0.5), Tuple(Verdict(GpuVendor.Intel, E("counters", "counters.cacheHitPercent.ICache", 35))));
        // The Arc B580 calibration pass: full-screen lighting with a 1x1 viewport saving of 97 % and PS ALU0 utilization of 58 %.
        Assert.Equal(("pixelShading", "high", 0.94), Tuple(Verdict(GpuVendor.Intel, E("drpix", "drpix.1x1 Viewport.savedPercent", 97.12),
            E("counters", "XVE Inst Executed ALU0 PS Utilization", 58), E("counters", "XVE Inst Executed ALU0 VS Utilization", 0), E("timing", "timing.eopShareOfExecPercent", 24.32))));
        Assert.Equal(("launchOverhead", "medium", 0.6),
            Tuple(Verdict(GpuVendor.Nvidia, E("timing", "timing.smallDispatchEopPercent", 90), E("timing", "timing.smallDispatchExecToEop", 3, "ratio"))));
        Assert.Equal(("syncIdle", "medium", 0.7), Tuple(Verdict(GpuVendor.Nvidia, E("timing", "timing.idlePercent", 45))));
        Assert.Equal(("unknown", "low", 0.0), Tuple(Verdict(GpuVendor.Nvidia, E("timing", "timing.idlePercent", 5))));
    }

    private static (string, string, double) Tuple(BottleneckVerdictDto verdict) => (verdict.Limiter, verdict.Confidence, verdict.Score);

    [Fact]
    public void SecondaryConditionsAndVendorBlocksGateRules()
    {
        BottleneckClassification unmet = BottleneckRules.Classify([E("timing", "timing.smallDispatchEopPercent", 90), E("timing", "timing.smallDispatchExecToEop", 1.2, "ratio")], GpuVendor.Nvidia, false);
        BottleneckRuleResultDto launch = Assert.Single(unmet.Results, r => r.Id == "small_dispatch_launch");
        Assert.Equal((false, false), (launch.Satisfied, launch.Secondary!.Satisfied));
        BottleneckClassification wrongVendor = BottleneckRules.Classify([E("counters", "GPU Memory Utilization (%)", 85), E("counters", "XVE Utilization (%)", 30)], GpuVendor.Nvidia, false);
        Assert.Empty(wrongVendor.Results);
        Assert.Equal("unknown", wrongVendor.Verdict.Limiter);
    }

    [Fact]
    public void HighConfidenceNeedsTwoSourcesAMarginAValidatedVendorAndEveryRequestedStage()
    {
        const string Json = """
        {
          "version": 7,
          "validated": { "nvidia": true },
          "rules": [
            { "id": "fill", "vendor": "any", "source": "drpix", "metric": "^drpix\\.1x1 Viewport\\.savedPercent$", "op": "gt", "threshold": 60, "limiter": "pixelShading", "weight": 0.8, "note": "n" },
            { "id": "pipe", "vendor": "nvidia", "source": "counters", "metric": "^ALU Pipe Activity\\(%\\)$", "op": "gt", "threshold": 70, "limiter": "pixelShading", "weight": 0.4, "note": "n" },
            { "id": "idle", "vendor": "any", "source": "timing", "metric": "^timing\\.idlePercent$", "op": "gt", "threshold": 30, "limiter": "syncIdle", "weight": 0.3, "note": "n" }
          ]
        }
        """;
        BottleneckRuleSet rules = BottleneckRules.Load(Json);
        BottleneckEvidenceDto[] evidence = [E("drpix", "drpix.1x1 Viewport.savedPercent", 80), E("counters", "ALU Pipe Activity(%)", 90), E("timing", "timing.idlePercent", 40)];
        BottleneckClassification high = BottleneckRules.Classify(evidence, GpuVendor.Nvidia, false, rules);
        Assert.Equal(("pixelShading", "high"), (high.Verdict.Limiter, high.Verdict.Confidence));
        Assert.Equal("syncIdle", Assert.Single(high.Alternatives).Limiter);
        Assert.Equal("medium", BottleneckRules.Classify(evidence, GpuVendor.Nvidia, true, rules).Verdict.Confidence);
        Assert.Equal("medium", BottleneckRules.Classify(evidence[..1].Append(evidence[2]).ToArray(), GpuVendor.Nvidia, false, rules).Verdict.Confidence);
        Assert.Equal("medium", BottleneckRules.Classify(evidence, GpuVendor.Amd, false, rules).Verdict.Confidence);
    }

    [Fact]
    public void EmbeddedRulesAreWellFormed()
    {
        BottleneckRuleSet rules = BottleneckRules.Default;
        Assert.Equal(rules.Rules.Count, rules.Rules.Select(r => r.Id).Distinct().Count());
        Assert.All(rules.Rules, rule =>
        {
            Assert.Contains(rule.Limiter, BottleneckRules.Limiters);
            Assert.Contains(rule.Op, BottleneckRules.Ops);
            Assert.Contains(rule.Source, BottleneckRules.Sources);
            Assert.InRange(rule.Weight, 0.01, 1);
            _ = new Regex(rule.Metric);
            if (rule.Secondary is { } secondary)
            {
                Assert.Contains(secondary.Op, BottleneckRules.Ops);
                Assert.Contains(secondary.Source, BottleneckRules.Sources);
                _ = new Regex(secondary.Metric);
            }
        });
        Assert.All(BottleneckRules.Limiters.Append("unknown"), limiter =>
        {
            Assert.False(string.IsNullOrEmpty(BottleneckRules.Implication(limiter)));
            Assert.NotEmpty(Assert.Single(BottleneckRules.Recommendations(limiter, "gpu-1", null, "Frame")).NextCalls);
        });
    }

    [Fact]
    public async Task BottleneckNeedsAScopeKnownEvidenceAndABoundedDrPixRunCountBeforeAnyJob()
    {
        using var worker = new PixWorker();
        using var session = new PixSession(worker, NullLogger<PixSession>.Instance);
        var jobs = new JobManager(worker, session);
        await Assert.ThrowsAnyAsync<Exception>(() => BottleneckTools.Bottleneck(session, jobs, "gpu-1"));
        await Assert.ThrowsAnyAsync<Exception>(() => BottleneckTools.Bottleneck(session, jobs, "gpu-1", markerPathPrefix: "Frame", evidence: ["vibes"]));
        await Assert.ThrowsAnyAsync<Exception>(() => BottleneckTools.Bottleneck(session, jobs, "gpu-1", markerPathPrefix: "Frame", maxDrPixRuns: 9));
        await Assert.ThrowsAnyAsync<Exception>(() => BottleneckTools.Bottleneck(session, jobs, "gpu-1", markerPathPrefix: "Frame", preset: "nope"));
        Assert.Empty(jobs.All);
    }
}
