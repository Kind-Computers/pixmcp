using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using PixMcp.Pix.Handles;

namespace PixMcp.Pix;

public sealed record BottleneckEvidenceDto(string Source, string Metric, double Value, string? Unit, string? UnitConfidence,
    [property: Description("event (aggregated over work events), marker (PIX's own marker round), derived (computed from other values) or series (a sampled series over the scope window).")] string RowKind,
    string? Note);

public sealed record BottleneckSecondaryDto(string Metric, double? Value, string Op, double Threshold, bool Satisfied);

public sealed record BottleneckRuleResultDto(string Id, string Limiter, double Weight, bool Satisfied, string Source, string Metric, double Value, string Op, double Threshold,
    BottleneckSecondaryDto? Secondary, string Note);

public sealed record BottleneckVerdictDto(
    [property: Description("pixelShading, vertexOrGeometry, rasterOrDepth, memoryBandwidth, cacheMiss, occupancyLatency, launchOverhead, syncIdle or unknown.")] string Limiter,
    [property: Description("high needs a score of 0.6, a 0.2 margin over the next limiter, two evidence sources, every requested stage this PIX build and GPU support, and a vendor block validated on hardware; medium needs 0.4; else low.")] string Confidence,
    [property: Description("1 - product(1 - weight) over the limiter's satisfied rules.")] double Score);

public sealed record BottleneckAlternativeDto(string Limiter, double Score, IReadOnlyList<string> RuleIds);

public sealed record BottleneckTimingDto(int WorkEvents, int TimedWorkEvents, double EopMs, double? ExecMs,
    [property: Description("First TOP start to last EOP end of the scope's timed events.")] double? WindowMs,
    [property: Description("Union of TOP-to-EOP windows of the scope's timed leaf events.")] double? BusyMs,
    double? IdlePercent, double? EopShareOfExecPercent, double? RootInclusiveMs, string? RootSemantics);

public sealed record BottleneckCoverageDto(string Source,
    [property: Description("available, partial, unavailable, unsupported, withheld, failed, notRun or notRequested.")] string State,
    string? Reason, IReadOnlyList<ToolCallDto> NextCalls);

public sealed record BottleneckRecommendationDto(string Text, IReadOnlyList<ToolCallDto> NextCalls);

public sealed record BottleneckRulesInfoDto(int Version, bool VendorValidated, IReadOnlyDictionary<string, bool> Validated, IReadOnlyList<string> SatisfiedIds);

public sealed record BottleneckDto(string Handle, ScopeDescriptionDto? Scope, string? MarkerPath, string Vendor, IReadOnlyList<string> EvidenceRequested,
    BottleneckTimingDto? Timing, BottleneckVerdictDto Verdict, IReadOnlyList<BottleneckAlternativeDto> Alternatives,
    [property: Description("Rule-bearing rows first; zero counter values and marker rounds equal to their event sum are only in detailRef, which holds every row.")] IReadOnlyList<BottleneckEvidenceDto> Evidence,
    [property: Description("Rules whose evidence was present, satisfied first; detailRef holds every result.")] IReadOnlyList<BottleneckRuleResultDto> RuleResults,
    string Implication, IReadOnlyList<BottleneckRecommendationDto> Recommendations, IReadOnlyList<BottleneckCoverageDto> Coverage, BottleneckRulesInfoDto Rules,
    IReadOnlyList<string> Notes, string? DetailRef, bool FromCache, IReadOnlyList<ToolCallDto> NextCalls)
{
    public ReplayProvenance? Provenance { get; init; }
}

internal sealed record BottleneckCondition(string Source, string Metric, string Op, double Threshold);

internal sealed record BottleneckRule(string Id, string Vendor, string Source, string Metric, string Op, double Threshold, BottleneckCondition? Secondary, string Limiter,
    double Weight, string Note);

internal sealed record BottleneckRuleSet(int Version, IReadOnlyDictionary<string, bool> Validated, IReadOnlyList<BottleneckRule> Rules,
    IReadOnlyDictionary<string, string[]>? Counters = null)
{
    /// <summary>Exact names of the vendor counters the counters stage collects so this vendor's rules have evidence; empty for vendors without a list.</summary>
    public IReadOnlyList<string> CountersFor(GpuVendor vendor)
        => Counters is not null && Counters.TryGetValue(GpuVendors.Name(vendor), out string[]? names) ? names : [];

    public bool IsValidated(GpuVendor vendor) => Validated.TryGetValue(GpuVendors.Name(vendor), out bool validated) && validated;
}

internal sealed record BottleneckClassification(BottleneckVerdictDto Verdict, IReadOnlyList<BottleneckAlternativeDto> Alternatives, IReadOnlyList<BottleneckRuleResultDto> Results,
    IReadOnlyList<string> SatisfiedIds, bool VendorValidated);

/// <summary>Scores limiters from evidence with the embedded heuristic rules; pure.</summary>
internal static class BottleneckRules
{
    public static readonly string[] Limiters = ["pixelShading", "vertexOrGeometry", "rasterOrDepth", "memoryBandwidth", "cacheMiss", "occupancyLatency", "launchOverhead", "syncIdle"];
    public static readonly string[] Ops = ["gt", "gte", "lt", "lte"];
    public static readonly string[] Sources = ["timing", "counters", "occupancy", "hf", "drpix"];
    public const double AlternativeMinScore = 0.15, HighScore = 0.6, HighMargin = 0.2, MediumScore = 0.4;
    private static readonly Lazy<BottleneckRuleSet> Embedded = new(() => Load(ReadEmbedded()));

    public static BottleneckRuleSet Default => Embedded.Value;

    private static string ReadEmbedded()
    {
        using Stream stream = typeof(BottleneckRules).Assembly.GetManifestResourceStream("PixMcp.Resources.bottleneck-rules.json")
            ?? throw new InvalidOperationException("The embedded bottleneck-rules.json resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static BottleneckRuleSet Load(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Dictionary<string, bool> validated = root.GetProperty("validated").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetBoolean(), StringComparer.OrdinalIgnoreCase);
        var rules = new List<BottleneckRule>();
        foreach (JsonElement r in root.GetProperty("rules").EnumerateArray())
        {
            BottleneckCondition? secondary = r.TryGetProperty("secondary", out JsonElement s)
                ? new(s.GetProperty("source").GetString()!, s.GetProperty("metric").GetString()!, s.GetProperty("op").GetString()!, s.GetProperty("threshold").GetDouble())
                : null;
            rules.Add(new(r.GetProperty("id").GetString()!, r.GetProperty("vendor").GetString()!, r.GetProperty("source").GetString()!, r.GetProperty("metric").GetString()!,
                r.GetProperty("op").GetString()!, r.GetProperty("threshold").GetDouble(), secondary, r.GetProperty("limiter").GetString()!, r.GetProperty("weight").GetDouble(),
                r.GetProperty("note").GetString()!));
        }
        Dictionary<string, string[]>? counters = root.TryGetProperty("counters", out JsonElement lists)
            ? lists.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(n => n.GetString()!).ToArray(), StringComparer.OrdinalIgnoreCase)
            : null;
        return new(root.GetProperty("version").GetInt32(), validated, rules, counters);
    }

    public static BottleneckClassification Classify(IReadOnlyList<BottleneckEvidenceDto> evidence, GpuVendor vendor, bool requestedEvidenceMissing, BottleneckRuleSet? rules = null)
    {
        BottleneckRuleSet set = rules ?? Default;
        string vendorName = GpuVendors.Name(vendor);
        var results = new List<BottleneckRuleResultDto>();
        var secondarySources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (BottleneckRule rule in set.Rules)
        {
            if (!rule.Vendor.Equals("any", StringComparison.OrdinalIgnoreCase) && !rule.Vendor.Equals(vendorName, StringComparison.OrdinalIgnoreCase)) continue;
            if (Find(evidence, rule.Source, rule.Metric) is not { } row) continue;
            bool satisfied = Holds(row.Value, rule.Op, rule.Threshold);
            BottleneckSecondaryDto? secondary = null;
            if (rule.Secondary is { } condition)
            {
                BottleneckEvidenceDto? other = Find(evidence, condition.Source, condition.Metric);
                bool holds = other is not null && Holds(other.Value, condition.Op, condition.Threshold);
                secondary = new(other?.Metric ?? condition.Metric, other?.Value, condition.Op, condition.Threshold, holds);
                satisfied &= holds;
                if (holds) secondarySources[rule.Id] = condition.Source;
            }
            results.Add(new(rule.Id, rule.Limiter, rule.Weight, satisfied, rule.Source, row.Metric, row.Value, rule.Op, rule.Threshold, secondary, rule.Note));
        }
        var scored = results.Where(r => r.Satisfied).GroupBy(r => r.Limiter)
            .Select(g => (Limiter: g.Key, Score: Math.Round(1 - g.Aggregate(1.0, (product, r) => product * (1 - r.Weight)), 3), Ids: g.Select(r => r.Id).ToArray(),
                Sources: g.Select(r => r.Source).Concat(g.Where(r => secondarySources.ContainsKey(r.Id)).Select(r => secondarySources[r.Id])).Distinct().Count()))
            .OrderByDescending(s => s.Score).ThenBy(s => Array.IndexOf(Limiters, s.Limiter)).ToList();
        bool validated = set.Validated.TryGetValue(vendorName, out bool v) && v;
        BottleneckVerdictDto verdict;
        if (scored.Count == 0) verdict = new("unknown", "low", 0);
        else
        {
            var top = scored[0];
            double margin = top.Score - (scored.Count > 1 ? scored[1].Score : 0);
            string confidence = top.Score >= HighScore && margin >= HighMargin && top.Sources >= 2 && validated && !requestedEvidenceMissing ? "high"
                : top.Score >= MediumScore ? "medium" : "low";
            verdict = new(top.Limiter, confidence, top.Score);
        }
        BottleneckAlternativeDto[] alternatives = scored.Skip(verdict.Limiter == "unknown" ? 0 : 1).Where(s => s.Score >= AlternativeMinScore)
            .Select(s => new BottleneckAlternativeDto(s.Limiter, s.Score, s.Ids)).ToArray();
        return new(verdict, alternatives, results, results.Where(r => r.Satisfied).Select(r => r.Id).ToArray(), validated);
    }

    private static BottleneckEvidenceDto? Find(IReadOnlyList<BottleneckEvidenceDto> evidence, string source, string metricPattern)
    {
        var regex = new Regex(metricPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return evidence.Where(e => e.Source.Equals(source, StringComparison.OrdinalIgnoreCase) && regex.IsMatch(e.Metric))
            .OrderBy(e => e.RowKind == "marker" ? 1 : 0).FirstOrDefault();
    }

    private static bool Holds(double value, string op, double threshold) => op switch
    {
        "gt" => value > threshold,
        "gte" => value >= threshold,
        "lt" => value < threshold,
        "lte" => value <= threshold,
        _ => false,
    };

    public static string Implication(string limiter) => limiter switch
    {
        "pixelShading" => "Per-pixel work limits the scope: resolution, overdraw and pixel shader cost are the levers.",
        "vertexOrGeometry" => "Vertex and primitive processing limits the scope: vertex counts, tiny primitives and culling are the levers.",
        "rasterOrDepth" => "Rasterization and depth testing limit the scope: late depth testing, MSAA and tiny triangles are the levers.",
        "memoryBandwidth" => "Memory traffic limits the scope: large resources moved or sampled per event cost more than execution.",
        "cacheMiss" => "Cache misses limit the scope: data access patterns cost more than execution.",
        "occupancyLatency" => "Execution slots sit empty: the GPU waits on scheduling or dependencies rather than doing work.",
        "launchOverhead" => "Launching work costs more than doing it: many small dispatches or draws dominate.",
        "syncIdle" => "The GPU waits inside the scope: synchronisation, pending submissions or presents separate its work.",
        _ => "No rule fired on the collected evidence; gather more evidence before concluding what limits the scope.",
    };

    public static BottleneckRecommendationDto[] Recommendations(string limiter, string handle, EventRef? scope, string? prefix) => limiter switch
    {
        "pixelShading" => [new("Rank the scope's shaders by replay time and confirm with a 1x1 viewport run.",
            [new ToolCallDto("pix_gpu_shaders", new { handle, scope, markerPathPrefix = prefix, sortBy = "gpuTime" }),
             new ToolCallDto("pix_gpu_drpix_run", new { handle, scope, markerPathPrefix = prefix, families = new[] { "basic" } })])],
        "vertexOrGeometry" => [new("Read pipeline statistics per event to find the draws with the most vertices and primitives.",
            [new ToolCallDto("pix_gpu_counters_read", new { handle, scope, markerPathPrefix = prefix, preset = "pipelineStatistics" })])],
        "rasterOrDepth" => [new("Run the depth and rasterization experiments to separate late depth, MSAA and tiny-triangle cost.",
            [new ToolCallDto("pix_gpu_drpix_run", new { handle, scope, markerPathPrefix = prefix, families = new[] { "depthStencil", "rasterization" } })])],
        "memoryBandwidth" => [new("Rank the scope's resources by traffic and inspect the largest.",
            [new ToolCallDto("pix_gpu_resources", new { handle, scope, markerPathPrefix = prefix, sortBy = "traffic" })])],
        "cacheMiss" => [new("Read the cache counters per event.", [new ToolCallDto("pix_gpu_counters_read", new { handle, scope, markerPathPrefix = prefix, preset = "cache" })])],
        "occupancyLatency" => [new("Read occupancy for the scope's longest work events.", [new ToolCallDto("pix_gpu_occupancy", new { handle, scope, markerPathPrefix = prefix, groupBy = "event" })])],
        "launchOverhead" => [new("List the scope's dispatches by duration and batch small ones into fewer, larger dispatches.",
            [new ToolCallDto("pix_gpu_timing_events", new { handle, scope, markerPathPrefix = prefix, kind = "dispatch", sortBy = "eopDuration" })])],
        "syncIdle" => [new("Find the idle gaps inside the scope and what separates the work.",
            [new ToolCallDto("pix_gpu_bubbles", new { handle, scope, markerPathPrefix = prefix }), new ToolCallDto("pix_gpu_queue_overlap", new { handle, scope, markerPathPrefix = prefix })])],
        _ => [new("Add Dr. PIX evidence or inspect the scope's slowest work events.",
            [new ToolCallDto("pix_gpu_bottleneck", new { handle, scope, markerPathPrefix = prefix, evidence = new[] { "timing", "counters", "occupancy", "drpix" } }),
             new ToolCallDto("pix_gpu_timing_events", new { handle, scope, markerPathPrefix = prefix, sortBy = "eopDuration" })])],
    };
}
