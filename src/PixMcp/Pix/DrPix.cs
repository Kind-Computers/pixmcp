using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using PixMcp.Pix.Handles;
using ToolKinds = PixMcp.Tools.Tools;

namespace PixMcp.Pix;

/// <summary>One Dr. PIX metric row as PIX reports it.</summary>
internal sealed record DrPixRawMetric(string? Group, string Name, string? ValueLabel, object? Value, int Depth);

public sealed record DrPixExperimentDto(Guid Guid, string Name, string Category, string HelpText, string Source,
    [property: Description("basic, depthStencil, rasterization, executeIndirect, shaderCorrectness, debugBreak, memory, vendor or other.")] string Family,
    [property: Description("What a result of this experiment shows, and what it does not.")] string WhatItProves,
    [property: Description("False when the experiment ignores event ranges and always runs over the whole capture (observed on PIX 2606.18).")] bool SupportsRanges,
    string CostHint);

public sealed record DrPixRecordDto(string? Group, string Name, int Depth,
    [property: Description("Value per label such as 'Time in us', '% Faster' or 'Count'; percent strings become numbers and PIX navigation links become pixnavlink URIs.")]
    IReadOnlyDictionary<string, object?> Values);

public sealed record DrPixTimingDto(string? Group, string Baseline, string Experiment, double BaselineMs, double ExperimentMs,
    [property: Description("Baseline minus experiment; positive when the experiment is faster.")] double SavedMs,
    [property: Description("Percent of the baseline saved; negative when the experiment is slower.")] double? SavedPercent,
    [property: Description("detected (PIX marked the experiment row with % Faster) or inferredByOrder (two timing rows, baseline first).")] string Semantics);

public sealed record DrPixMessageDto(string Type, string Message);

public sealed record DrPixRangeDto(int Index, EventRef FirstEventRef, EventRef LastEventRef, uint FirstGpuId, uint LastGpuId, int WorkEvents, bool Swapped, string? MarkerPath);

public sealed record DrPixRunDto(int Run, string Experiment, Guid Guid, string Category, string Family, string WhatItProves, string Source, int RangeIndex, string Status,
    bool Succeeded,
    [property: Description("PIX reported that the experiment ignores sub-ranges and ran over the whole capture.")] bool RangeIgnored,
    DrPixTimingDto? Timing, IReadOnlyList<DrPixRecordDto> Records, IReadOnlyList<DrPixMessageDto> Messages)
{
    public object? Unavailable { get; init; }
}

public sealed record DrPixSavingDto(int Run, string Experiment, string Family, int RangeIndex, double BaselineMs, double ExperimentMs, double SavedMs, double? SavedPercent,
    string Implication);

public sealed record DrPixSummaryDto(DrPixSavingDto? BestSaving, IReadOnlyList<string> Ranking,
    [property: Description("Succeeded runs with timing rows that could not be paired into baseline and experiment.")] int UnknownSemantics,
    int FailedRuns, int RangeIgnoredRuns);

public sealed record DrPixTableDto(IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows);

public sealed record DrPixResultDto(string Handle, int RunsRequested, int RunsCompleted,
    [property: Description("True when the job stopped early and only finished runs are reported.")] bool Partial,
    bool WholeCapture, bool PerEvent, IReadOnlyList<DrPixRangeDto> Ranges, IReadOnlyList<DrPixRunDto> Runs,
    [property: Description("One row per run: experiment, family, range, status and timing columns.")] DrPixTableDto Table,
    IReadOnlyList<DrPixSavingDto> Savings, DrPixSummaryDto Summary, IReadOnlyList<string> Notes, IReadOnlyList<ToolCallDto> NextCalls)
{
    public ScopeDescriptionDto? Scope { get; init; }
    /// <summary>The GPU ids and event references of the range when exactly one range ran.</summary>
    public object? Range { get; init; }
}

/// <summary>Groups Dr. PIX experiments into families with a statement of what their results prove.</summary>
internal static class DrPixFamilies
{
    public static readonly string[] Names = ["basic", "depthStencil", "rasterization", "executeIndirect", "shaderCorrectness", "debugBreak", "memory", "vendor", "other"];

    /// <summary>Experiments PIX 2606.18 runs over the whole capture whatever range is passed (they say so in a warning).</summary>
    private static readonly HashSet<string> WholeCaptureOnly = new(StringComparer.OrdinalIgnoreCase) { "ExecuteIndirect Minimal Command Count", "Tight Resource Alignment" };

    public static (string Family, string WhatItProves) Of(string name, string category, string source)
    {
        string text = name + " " + category;
        if (!source.Equals("PIX", StringComparison.OrdinalIgnoreCase))
            return ("vendor", "A GPU plugin experiment; its help text and messages say what it measures.");
        if (Has(name, "1x1 Viewport") || category.Equals("Basic Information", StringComparison.OrdinalIgnoreCase))
            return ("basic", "How much of the range's time is per-pixel work: the viewport shrinks to 1x1, so the saving approximates pixel shading, rasterization and output merger cost. It does not isolate one shader.");
        if (Has(text, "Early Z") || Has(text, "EarlyZ") || Has(category, "Depth"))
            return ("depthStencil", "Whether forcing early depth-stencil testing saves time; a negative saving means early testing costs more here. Shaders that write depth or discard behave differently under early testing.");
        if (Has(text, "ExecuteIndirect"))
            return ("executeIndirect", "Time saved if every ExecuteIndirect used the number of commands it executed as MaxCommandCount; runs over the whole capture.");
        if (Has(text, "NonUniformResourceIndex"))
            return ("shaderCorrectness", "Dynamic resource indices missing the NonUniformResourceIndex qualifier, whose results are undefined across a wave; only SM 6.0+ shaders can be instrumented.");
        if (Has(text, "DebugBreak"))
            return ("debugBreak", "DebugBreak() calls hit during execution (SM 6.10); only SM 6.0+ shaders can be instrumented.");
        if (Has(text, "Quad"))
            return ("rasterization", "Share of 2x2 pixel quads with fewer than four covered pixels; many partial quads mean tiny triangles and wasted pixel shading.");
        if (Has(text, "Primitive") || Has(text, "Raster"))
            return ("rasterization", "Primitives and draws thrown away by clipping, culling or depth testing after paying for vertex work.");
        if (Has(text, "Alignment"))
            return ("memory", "Memory saved if tight alignment were used on supported placed resources; runs over the whole capture.");
        if (Has(text, "Bandwidth") || Has(text, "Memory"))
            return ("memory", "Memory bandwidth or residency measured through the GPU plugin; unavailable when the plugin does not support it.");
        return ("other", "No server interpretation; read the experiment's help text.");
    }

    public static bool SupportsRanges(string name) => !WholeCaptureOnly.Contains(name);

    private static bool Has(string text, string part) => text.Contains(part, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Structures Dr. PIX metrics: records per (group, name), baseline and experiment timings, savings, a summary and a pivot table.</summary>
internal static class DrPixMetrics
{
    public const string FasterLabel = "% Faster";
    private static readonly Regex NavLink = new("href=\"(?<uri>pixnavlink:[^\"]*)\"", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Consecutive rows with the same group and name form one record; a header row (label Test or none, value equal to the
    /// name) is dropped and every other row adds its value under its label.
    /// </summary>
    public static IReadOnlyList<DrPixRecordDto> Records(IReadOnlyList<DrPixRawMetric> metrics)
    {
        var records = new List<(string? Group, string Name, int Depth, Dictionary<string, object?> Values)>();
        foreach (DrPixRawMetric m in metrics)
        {
            if (records.Count == 0 || records[^1].Group != m.Group || records[^1].Name != m.Name) records.Add((m.Group, m.Name, m.Depth, new(StringComparer.Ordinal)));
            if ((m.ValueLabel is null || m.ValueLabel == "Test") && m.Value is string header && header == m.Name) continue;
            Dictionary<string, object?> values = records[^1].Values;
            string label = m.ValueLabel ?? "value";
            values[values.ContainsKey(label) ? $"{label} ({values.Count})" : label] = Normalize(m.Value);
        }
        return records.Select(r => new DrPixRecordDto(r.Group, r.Name, r.Depth, r.Values)).ToArray();
    }

    public static object? Normalize(object? value)
    {
        if (value is not string text) return value;
        string trimmed = text.Trim();
        if (trimmed.EndsWith('%') && double.TryParse(trimmed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double percent)) return percent;
        try
        {
            Match link = NavLink.Match(trimmed);
            return link.Success ? link.Groups["uri"].Value : text;
        }
        catch (RegexMatchTimeoutException)
        {
            return text;
        }
    }

    public static double? Number(object? value) => value switch
    {
        null or string or bool => null,
        IConvertible convertible => Convert.ToDouble(convertible, CultureInfo.InvariantCulture),
        _ => null,
    };

    /// <summary>Nanoseconds per unit of a timing label (Time in us, Time (ns), Time in ms), or null for other labels.</summary>
    public static double? TimeScale(string label)
    {
        if (!label.StartsWith("Time", StringComparison.OrdinalIgnoreCase)) return null;
        string l = label.ToLowerInvariant();
        if (l.Contains("ns")) return 1;
        if (l.Contains("us") || l.Contains("µs")) return 1e3;
        if (l.Contains("ms")) return 1e6;
        if (l.Contains("(s)") || l.EndsWith(" s") || l.Contains("second")) return 1e9;
        return null;
    }

    /// <summary>The first group with a baseline and an experiment timing: the % Faster row is the experiment, else two rows in order.</summary>
    public static DrPixTimingDto? Timing(IReadOnlyList<DrPixRecordDto> records)
    {
        foreach (IGrouping<string?, DrPixRecordDto> group in records.GroupBy(r => r.Group))
        {
            var timed = group.Select(r => (Record: r, Ns: TimeNs(r))).Where(t => t.Ns.HasValue).ToList();
            if (timed.Count < 2) continue;
            int experiment = timed.FindIndex(t => t.Record.Values.ContainsKey(FasterLabel));
            int baseline;
            string semantics;
            if (experiment >= 0)
            {
                baseline = timed.FindIndex(t => !t.Record.Values.ContainsKey(FasterLabel));
                if (baseline < 0) continue;
                semantics = "detected";
            }
            else if (timed.Count == 2)
            {
                (baseline, experiment, semantics) = (0, 1, "inferredByOrder");
            }
            else continue;
            double baselineMs = timed[baseline].Ns!.Value / 1e6, experimentMs = timed[experiment].Ns!.Value / 1e6;
            double? saved = timed[experiment].Record.Values.TryGetValue(FasterLabel, out object? faster) && Number(faster) is double reported ? reported
                : baselineMs > 0 ? 100 * (baselineMs - experimentMs) / baselineMs : null;
            return new(group.Key, timed[baseline].Record.Name, timed[experiment].Record.Name, Math.Round(baselineMs, 6), Math.Round(experimentMs, 6),
                Math.Round(baselineMs - experimentMs, 6), saved is double s ? Math.Round(s, 2) : null, semantics);
        }
        return null;
    }

    private static double? TimeNs(DrPixRecordDto record)
    {
        foreach ((string label, object? value) in record.Values)
            if (TimeScale(label) is double scale && Number(value) is double number) return number * scale;
        return null;
    }

    public static string Implication(string family, DrPixTimingDto timing)
    {
        double p = timing.SavedPercent ?? 0;
        return family switch
        {
            "basic" => p > 0 ? $"Per-pixel work accounts for about {F(p)} % of this range; resolution, overdraw and pixel shader cost are the levers."
                : "Per-pixel work is not a measurable part of this range.",
            "depthStencil" => p > 0 ? $"Early depth testing would save {F(p)} %; if the shaders neither write depth nor discard, force it (for example with [earlydepthstencil])."
                : $"Early depth testing would cost {F(-p)} % more here; keep late testing.",
            "executeIndirect" => p > 0 ? $"MaxCommandCount values matching the executed commands would save {F(p)} % of ExecuteIndirect time."
                : "MaxCommandCount already matches the executed commands.",
            _ => p > 0 ? $"{timing.Experiment} saves {F(p)} % against {timing.Baseline}." : $"{timing.Experiment} does not save time against {timing.Baseline}.",
        };
    }

    public static DrPixSummaryDto Summary(IReadOnlyList<DrPixRunDto> runs, IReadOnlyList<DrPixSavingDto> savings)
    {
        DrPixSavingDto[] ranked = savings.Where(s => s.SavedPercent is not null).OrderByDescending(s => s.SavedPercent).ThenBy(s => s.Run).ToArray();
        return new(ranked.FirstOrDefault(), ranked.Select(s => $"{s.Experiment} (range {s.RangeIndex}): {F(s.SavedPercent!.Value)} %").ToArray(),
            runs.Count(r => r.Succeeded && r.Timing is null && r.Records.Any(record => record.Values.Keys.Any(k => TimeScale(k) is not null))),
            runs.Count(r => !r.Succeeded), runs.Count(r => r.RangeIgnored));
    }

    public static DrPixTableDto Table(IReadOnlyList<DrPixRunDto> runs, IReadOnlyList<DrPixRangeDto> ranges)
        => new(["run", "experiment", "family", "rangeIndex", "queueIndex", "firstEventIndex", "lastEventIndex", "status", "baselineMs", "experimentMs", "savedPercent", "rangeIgnored"],
            runs.Select(r =>
            {
                DrPixRangeDto range = ranges[r.RangeIndex];
                return new object?[] { r.Run, r.Experiment, r.Family, r.RangeIndex, range.FirstEventRef.QueueIndex, range.FirstEventRef.EventIndex, range.LastEventRef.EventIndex,
                    r.Status, r.Timing?.BaselineMs, r.Timing?.ExperimentMs, r.Timing?.SavedPercent, r.RangeIgnored };
            }).ToArray());

    private static string F(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}

internal static class DrPixRanges
{
    /// <summary>Draw, dispatch and ExecuteIndirect events with a GPU id that the predicate keeps, in event order.</summary>
    public static uint[] WorkEvents(EventRecord[] events, Func<uint, bool> inScope)
    {
        var result = new List<uint>();
        for (uint i = 0; i < events.Length; i++)
            if (events[i].GpuId != uint.MaxValue && ToolKinds.MatchesKind(events[i], "work") && inScope(i)) result.Add(i);
        return result.ToArray();
    }
}
