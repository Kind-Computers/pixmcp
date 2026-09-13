using System.ComponentModel;
using System.Globalization;
using PixMcp.Pix.Handles;

namespace PixMcp.Pix;

/// <summary>A ratio column of pix_gpu_counters_read.</summary>
public sealed record CounterRatioSpec(
    [property: Description("Column name; derived[name] holds the ratio.")] string Name,
    [property: Description("Counter id of the numerator (from counterIds).")] uint NumeratorId,
    [property: Description("Counter id of the denominator (from counterIds).")] uint DenominatorId);

/// <summary>Pure counter arithmetic for pix_gpu_counters_read: normalization divisors, normalized values and ratios.</summary>
internal static class CounterNormalization
{
    public static readonly string[] Modes = ["none", "perMs", "perThreadGroup", "perPixel"];

    /// <summary>Percent, ratio, rate, boolean and bitmask counters keep their meaning only unnormalized.</summary>
    public static bool Normalizable(string unit)
        => !(unit.Contains("percent", StringComparison.OrdinalIgnoreCase) || unit.Contains("PerSecond", StringComparison.OrdinalIgnoreCase) || unit is "boolean" or "bitmask" or "ratio");

    public static double? ToDouble(object? value)
    {
        double? number = value switch
        {
            float f => f,
            double d => d,
            Half half => (double)half,
            _ => CounterQuery.Number(value) is decimal m ? (double)m : null,
        };
        return number is double n && double.IsFinite(n) ? n : null;
    }

    public static string Describe(string mode) => mode switch
    {
        "perMs" => "counter value / the event's inclusive replay EOP milliseconds",
        "perThreadGroup" => "counter value / thread groups launched by the dispatch (X*Y*Z)",
        "perPixel" => "counter value / pixels of the draw's largest bound render target (width*height at the view's mip slice times samples)",
        _ => "none",
    };

    /// <summary>The divisor for one row, or null with the reason it has none. <paramref name="pixels"/> is only called for draws.</summary>
    public static (double? Divisor, string? Reason) Divisor(string mode, string kind, DurationDto? eop, ApiCallDto? call, Func<(ulong Pixels, string? Reason)> pixels)
    {
        switch (mode)
        {
            case "perMs":
                return eop is { Ns: > 0 } ? (eop.Ns / 1e6, null) : (null, "The event has no replay EOP time.");
            case "perThreadGroup":
                return call is { WorkItemKind: "threadGroups", WorkItems: > 0 and < long.MaxValue } ? (call.WorkItems!.Value, null) : (null, "Only dispatches launch thread groups.");
            case "perPixel":
                if (kind is not ("draw" or "executeIndirect")) return (null, "Only draws render pixels.");
                (ulong count, string? reason) = pixels();
                return count > 0 ? (count, null) : (null, reason ?? "No render target is bound at this event.");
            default:
                return (null, null);
        }
    }

    /// <summary>Each counter's value divided by <paramref name="divisor"/>, keyed by counter id; null for non-normalizable units and missing values.</summary>
    public static IReadOnlyDictionary<string, double?> Normalize(IReadOnlyList<CounterInfo> counters, IReadOnlyList<object?> values, double divisor)
    {
        var result = new Dictionary<string, double?>();
        for (int i = 0; i < counters.Count; i++)
            result[counters[i].Id.ToString(CultureInfo.InvariantCulture)] = divisor > 0 && Normalizable(counters[i].Unit.Unit) && i < values.Count && ToDouble(values[i]) is double v
                ? Math.Round(v / divisor, 6) : null;
        return result;
    }

    public static double? Ratio(object? numerator, object? denominator)
        => ToDouble(numerator) is double n && ToDouble(denominator) is double d && d != 0 ? Math.Round(n / d, 6) : null;
}
