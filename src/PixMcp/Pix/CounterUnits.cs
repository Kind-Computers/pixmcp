using System.Text.RegularExpressions;

namespace PixMcp.Pix;

/// <summary>
/// The unit a counter is most likely reported in. PIX exposes no unit field, so this is inferred from the format
/// specifier and the name/description vocabulary vendors use; <c>unitSource</c> and <c>unitConfidence</c> say how.
/// <c>aggregationHint</c> tells rollups whether values add across events (sum), are rates or shares (avg), or neither.
/// </summary>
public sealed record UnitGuess(string Unit, string UnitSource, string UnitConfidence, double? RangeMin, double? RangeMax, string AggregationHint)
{
    public static readonly UnitGuess Unknown = new("unknown", "none", "none", null, null, "none");
}

public static class CounterUnits
{
    public const string Percent = "percent", Boolean = "boolean", Bitmask = "bitmask", Bytes = "bytes", BytesPerSecond = "bytesPerSecond",
        Cycles = "cycles", Nanoseconds = "ns", Count = "count", Ratio = "ratio", UnknownUnit = "unknown";

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly Regex PercentInName = new(@"\(\s*%\s*\)|\bpercent(age)?\b|%$", Options);
    private static readonly Regex PercentWords = new(@"\butili[sz]ation\b|\boccupancy\b|\bhit rate\b|\bmiss rate\b|\bbusy\b|\bstarved\b|\bstall(ed|s)?\b|\bidle\b|\bactive\b|\bbottleneck\b", Options);
    private static readonly Regex StrongCountWords = new(@"\bcounts?\b|\bnumber of\b", Options);
    private static readonly Regex PercentDescription = new(@"\bpercentage of\b|\bpercent of\b|\bfraction of\b", Options);
    private static readonly Regex BytesPerSecondPattern = new(@"\bbytes\s*(per|/)\s*(second|sec|s)\b|\bB/s\b|\bGB/s\b|\bMB/s\b|\bbandwidth\b", Options);
    private static readonly Regex BytesWord = new(@"\bbytes?\b|\bkb\b|\bmb\b|\bgb\b", Options);
    private static readonly Regex CyclesWord = new(@"\bcycles?\b|\bclocks?\b", Options);
    private static readonly Regex NanosecondsWord = new(@"\bnanoseconds?\b|\bns\b|\bmicroseconds?\b|\bmilliseconds?\b|\bduration\b|\btime\b", Options);
    private static readonly Regex CountWords = new(@"\bcounts?\b|\bnumber of\b|\binvocations?\b|\bprimitives?\b|\bvertices\b|\bsamples?\b|\bthreads?\b|\bwaves?\b|\bwarps?\b|\bmessages?\b|\binstructions?\b|\bexecuted\b|\brequests?\b|\bhits?\b|\bmisses\b", Options);

    /// <summary>
    /// Ordered rules: format (bool, hex/bitmask); "(%)" in the name; "percentage of" in the description (how the Intel
    /// plugin states units: "INTEL: Percentage of time ..."); bytes-per-second; bytes and cycles (both = ratio);
    /// utilization/occupancy/busy/starved/stall vocabulary in the name unless it is an explicit count; time words in the
    /// name; count words. Underscores in vendor names count as word separators.
    /// </summary>
    public static UnitGuess Infer(string? name, string? description, string? dataType)
    {
        string n = (name ?? "").Replace('_', ' '), d = (description ?? "").Replace('_', ' '), format = (dataType ?? "").ToUpperInvariant();
        if (format.StartsWith("BOOL", StringComparison.Ordinal)) return new(Boolean, "format", "high", 0, 1, "none");
        if (format.Contains("HEX", StringComparison.Ordinal) || format.Contains("BINARY", StringComparison.Ordinal) || format.Contains("BITMASK", StringComparison.Ordinal))
            return new(Bitmask, "format", "high", null, null, "none");
        if (PercentInName.IsMatch(n)) return new(Percent, "name", "high", 0, 100, "avg");
        if (PercentDescription.IsMatch(d)) return new(Percent, "description", "high", 0, 100, "avg");
        if (BytesPerSecondPattern.IsMatch(n)) return new(BytesPerSecond, "name", "medium", 0, null, "avg");
        if (BytesPerSecondPattern.IsMatch(d)) return new(BytesPerSecond, "description", "medium", 0, null, "avg");
        bool bytes = BytesWord.IsMatch(n) || BytesWord.IsMatch(d), cycles = CyclesWord.IsMatch(n) || CyclesWord.IsMatch(d);
        if (bytes && cycles) return new(Ratio, "description", "low", 0, null, "avg");
        if (bytes) return new(Bytes, BytesWord.IsMatch(n) ? "name" : "description", "medium", 0, null, "sum");
        if (cycles) return new(Cycles, CyclesWord.IsMatch(n) ? "name" : "description", "medium", 0, null, "sum");
        if (PercentWords.IsMatch(n) && !StrongCountWords.IsMatch(n)) return new(Percent, "name", "medium", 0, 100, "avg");
        if (PercentDescription.IsMatch(d)) return new(Percent, "description", "medium", 0, 100, "avg");
        if (NanosecondsWord.IsMatch(n)) return new(Nanoseconds, "name", "low", 0, null, "sum");
        if (CountWords.IsMatch(n)) return new(Count, "name", "medium", 0, null, "sum");
        if (CountWords.IsMatch(d)) return new(Count, "description", "medium", 0, null, "sum");
        if (format.StartsWith("FLOAT", StringComparison.Ordinal)) return new(UnknownUnit, "none", "none", null, null, "avg");
        return new(UnknownUnit, "none", "none", null, null, format.StartsWith("UINT", StringComparison.Ordinal) || format.StartsWith("INT", StringComparison.Ordinal) ? "sum" : "none");
    }
}
