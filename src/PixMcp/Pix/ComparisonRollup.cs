using System.ComponentModel;
using System.Globalization;
using PixMcp.Pix.Handles;

namespace PixMcp.Pix;

public sealed record ComparisonQueueTotalsDto(int BaselineQueueIndex, int CandidateQueueIndex, string Name, ulong BaselineBusyNs, ulong CandidateBusyNs, decimal DeltaNs,
    double? DeltaPercent);

public sealed record ComparisonTotalsDto(
    [property: Description("Busy time (union of TOP-to-EOP windows of timed leaf events) inside the compared selection, summed over the baseline's queues.")] ulong BaselineBusyNs,
    ulong CandidateBusyNs, decimal DeltaNs, double DeltaMs, double? DeltaPercent,
    [property: Description("Summed delta of the largest work-event changes over the busy delta, clamped to 0-100; null when their signs differ or nothing changed.")] double? ExplainedByTopChangesPercent,
    [property: Description("EOP of timed work events only the baseline has.")] ulong BaselineOnlyNs,
    [property: Description("EOP of timed work events only the candidate has.")] ulong CandidateOnlyNs,
    string Semantics, IReadOnlyList<ComparisonQueueTotalsDto> PerQueue, string Denominator);

public sealed record MarkerPathDeltaDto(string MarkerPath, int Depth, ulong BaselineNs, ulong CandidateNs, decimal DeltaNs, double DeltaMs, double? DeltaPercent,
    [property: Description("This path's delta as a percent of the summed delta of every matched timed work event.")] double? ShareOfTotalDelta,
    int MatchedEvents,
    [property: Description("The delta is within the noise floor measured by repeats.")] bool BelowNoiseFloor);

public sealed record ComparisonNoiseDto(int Repeats,
    [property: Description("single, recollect (timing collected again on the running analysis) or restart (analysis restarted between collections); baseline/candidate when the sides differ.")] string RepeatMethod,
    [property: Description("Median over matched timed work events of the larger per-side spread (max minus min EOP across repeats).")] ulong? NoiseFloorNs,
    double? NoiseFloorMs, ulong? MaxSpreadNs, string? Warning);

public sealed record ComparisonWarningDto(string Code, string Field, string? Baseline, string? Candidate, string Severity, string Implication);

public sealed record ShaderCodeDiffDto(EventRef Baseline, EventRef Candidate, string Stage, string BaselineHash, string CandidateHash, int Added, int Removed, bool Truncated,
    IReadOnlyList<TextDiffLineDto> FirstDifferences, IReadOnlyList<ToolCallDto> NextCalls);

public sealed record ComparisonOptionsDto(EventRef? BaselineScope, EventRef? CandidateScope, string? MarkerPathPrefix, int? BaselineFrame, int? CandidateFrame,
    int RollupDepth, bool OrdinalMatching, bool IncludeDescriptorHeapIndices, bool ShaderCodeDiff, int Repeats);

/// <summary>Totals, marker-path rollups, noise, provenance warnings and shader code diffs over a matched comparison; pure.</summary>
internal static class ComparisonRollup
{
    public const int InlineMarkerPaths = 25, InlineCodeDiffs = 5;
    public const string NoMarker = "(no marker)";

    public static bool IsWork(string? kind) => kind is "draw" or "dispatch" or "executeIndirect";

    public static ComparisonTotalsDto Totals(ComparisonSnapshot baseline, ComparisonSnapshot candidate, IReadOnlyList<(ComparisonQueue A, ComparisonQueue B)> queues,
        IReadOnlyList<EventChange> changes, IEnumerable<ComparisonEvent> baselineOnly, IEnumerable<ComparisonEvent> candidateOnly, int topChanges)
    {
        ulong busyA = Sum(baseline.Queues.Select(q => q.BusyNs ?? 0)), busyB = Sum(candidate.Queues.Select(q => q.BusyNs ?? 0));
        decimal delta = (decimal)busyB - busyA;
        decimal top = changes.Where(c => IsWork(c.Kind) && c.DeltaNs.HasValue).OrderByDescending(c => Math.Abs(c.DeltaNs!.Value)).Take(topChanges).Sum(c => c.DeltaNs!.Value);
        double? explained = delta == 0 || top == 0 || Math.Sign(top) != Math.Sign(delta) ? null : Math.Round(Math.Clamp((double)(100 * top / delta), 0, 100), 2);
        return new(busyA, busyB, delta, Math.Round((double)delta / 1e6, 3), busyA == 0 ? null : Math.Round((double)(100 * delta / busyA), 2), explained,
            Sum(baselineOnly.Where(e => IsWork(e.Kind)).Select(e => e.EopNs ?? 0)), Sum(candidateOnly.Where(e => IsWork(e.Kind)).Select(e => e.EopNs ?? 0)),
            "Busy time on each side's replay clock inside the compared selection; deltas are candidate minus baseline and are not application frame latency.",
            queues.Select(q =>
            {
                ulong a = q.A.BusyNs ?? 0, b = q.B.BusyNs ?? 0;
                return new ComparisonQueueTotalsDto(q.A.Index, q.B.Index, q.A.Name, a, b, (decimal)b - a, a == 0 ? null : Math.Round((double)(100 * ((decimal)b - a) / a), 2));
            }).ToArray(),
            "deltaPercent divides by the baseline busy time; explainedByTopChangesPercent divides the summed delta of the largest work-event changes by the busy delta");
    }

    /// <summary>Matched timed work events attributed to every marker-path prefix up to depth; unmarked events roll up under (no marker).</summary>
    public static IReadOnlyList<MarkerPathDeltaDto> ByMarkerPath(IReadOnlyList<(ComparisonEvent A, ComparisonEvent B)> pairs, int depth, ulong? noiseFloorNs)
    {
        var rows = new Dictionary<string, (int Depth, ulong A, ulong B, int Count)>(StringComparer.Ordinal);
        decimal total = 0;
        foreach ((ComparisonEvent a, ComparisonEvent b) in pairs)
        {
            ulong x = a.EopNs ?? 0, y = b.EopNs ?? 0;
            total += (decimal)y - x;
            string[] path = a.MarkerPath.Length == 0 ? [NoMarker] : a.MarkerPath;
            for (int d = 1; d <= Math.Min(depth, path.Length); d++)
            {
                string key = string.Join("/", path.Take(d));
                rows[key] = rows.TryGetValue(key, out var row) ? (d, row.A + x, row.B + y, row.Count + 1) : (d, x, y, 1);
            }
        }
        return rows.Select(kv =>
            {
                decimal delta = (decimal)kv.Value.B - kv.Value.A;
                return new MarkerPathDeltaDto(kv.Key, kv.Value.Depth, kv.Value.A, kv.Value.B, delta, Math.Round((double)delta / 1e6, 3),
                    kv.Value.A == 0 ? null : Math.Round((double)(100 * delta / kv.Value.A), 2), total == 0 ? null : Math.Round((double)(100 * delta / total), 2),
                    kv.Value.Count, noiseFloorNs is ulong floor && Math.Abs(delta) <= floor);
            })
            .OrderByDescending(r => Math.Abs(r.DeltaNs)).ThenBy(r => r.Depth).ThenBy(r => r.MarkerPath, StringComparer.Ordinal).ToArray();
    }

    /// <summary>The largest rows, skipping any row whose ancestor or descendant is already listed.</summary>
    public static IReadOnlyList<MarkerPathDeltaDto> Inline(IReadOnlyList<MarkerPathDeltaDto> rows, int limit)
    {
        var picked = new List<MarkerPathDeltaDto>();
        foreach (MarkerPathDeltaDto row in rows)
        {
            if (picked.Any(p => Nested(p.MarkerPath, row.MarkerPath) || Nested(row.MarkerPath, p.MarkerPath))) continue;
            picked.Add(row);
            if (picked.Count == limit) break;
        }
        return picked;
    }

    private static bool Nested(string ancestor, string path) => path == ancestor || path.StartsWith(ancestor + "/", StringComparison.Ordinal);

    public static ComparisonNoiseDto Noise(int repeats, string method, IEnumerable<ulong> spreads)
    {
        ulong[] sorted = spreads.Order().ToArray();
        if (repeats < 2 || sorted.Length == 0)
            return new(repeats, method, null, null, null, "One replay per side: deltas carry no noise floor; pass repeats=2 or more to measure replay variance.");
        ulong floor = sorted[(sorted.Length - 1) / 2];
        return new(repeats, method, floor, Metrics.Ms(floor), sorted[^1],
            method.Contains("restart", StringComparison.Ordinal) ? "Timing was collected again by restarting analysis, so spreads include analysis start-up variance." : null);
    }

    public static IReadOnlyList<ComparisonWarningDto> ProvenanceWarnings(ReplayProvenance a, ReplayProvenance b, bool sameHandle)
    {
        var warnings = new List<ComparisonWarningDto>();
        if (sameHandle) return warnings;
        void Check(string field, string? x, string? y, string severity, string implication)
        {
            if (!string.Equals(x, y, StringComparison.Ordinal)) warnings.Add(new("provenance_mismatch", field, x, y, severity, implication));
        }
        const string Adapter = "The captures replayed on different adapters; timing deltas mix GPU differences with content changes.";
        Check("adapter", a.Adapter, b.Adapter, "warning", Adapter);
        Check("adapterName", a.AdapterName, b.AdapterName, "warning", Adapter);
        Check("vendor", a.Vendor, b.Vendor, "warning", Adapter);
        Check("powerState", a.PowerState?.ToString(CultureInfo.InvariantCulture), b.PowerState?.ToString(CultureInfo.InvariantCulture), "warning",
            "Replay power states differ; the GPU may run at a different performance level in each replay.");
        Check("flags", a.Flags, b.Flags, "info", "Analysis flags differ; plugins or driver state may change replay behaviour.");
        Check("pixBuild", a.PixBuild, b.PixBuild, "info", "Different PIX builds replayed the captures.");
        return warnings;
    }

    /// <summary>One HLSL line diff per distinct (stage, baseline hash, candidate hash) among matched events whose shader hash changed.</summary>
    public static IReadOnlyList<ShaderCodeDiffDto> CodeDiffs(IEnumerable<(ComparisonEvent A, ComparisonEvent B)> pairs, IReadOnlyDictionary<string, string>? baselineHlsl,
        IReadOnlyDictionary<string, string>? candidateHlsl)
    {
        var result = new List<ShaderCodeDiffDto>();
        if (baselineHlsl is null || candidateHlsl is null) return result;
        var seen = new HashSet<(string, string, string)>();
        foreach ((ComparisonEvent a, ComparisonEvent b) in pairs)
        {
            if (a.Shaders is null || b.Shaders is null) continue;
            foreach (ComparisonShader shader in a.Shaders)
            {
                ComparisonShader? other = b.Shaders.FirstOrDefault(s => s.Stage == shader.Stage);
                if (other is null || other.Hash == shader.Hash || !seen.Add((shader.Stage, shader.Hash, other.Hash))) continue;
                if (!baselineHlsl.TryGetValue(shader.Hash, out string? before) || !candidateHlsl.TryGetValue(other.Hash, out string? after)) continue;
                TextDiffResult diff = TextDiff.Lines(before, after);
                result.Add(new(a.EventRef, b.EventRef, shader.Stage, shader.Hash, other.Hash, diff.Added, diff.Removed, diff.Truncated, diff.FirstDifferences,
                    [new ToolCallDto("pix_gpu_shader_code", new { shaderRef = new ShaderRef(b.EventRef, other.Index) })]));
            }
        }
        return result;
    }

    private static ulong Sum(IEnumerable<ulong> values) => values.Aggregate(0UL, (sum, value) => sum + value);
}
