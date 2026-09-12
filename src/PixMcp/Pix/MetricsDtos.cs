namespace PixMcp.Pix;

public sealed record TimingEventDto(EventRef EventRef, IReadOnlyList<string> MarkerPath, int QueueIndex, uint Index,
    uint? GpuId, string Name, ulong? TopStartNs, ulong? TopDurationNs, ulong? EopStartNs, ulong? EopDurationNs);

public sealed record TimingBranchDto(EventRef EventRef, uint Index, string Name, uint? GpuId,
    ulong? MeasuredEopNs, ulong InclusiveEopNs, ulong SelfEopNs, double PercentOfQueue,
    bool HasOwnTiming, int TimedDescendants, int ChildCount, IReadOnlyList<TimingBranchDto> Children,
    bool ChildrenTruncated, IReadOnlyList<ToolCallDto> NextCalls);
public sealed record TimingTreeDto(string Handle, int QueueIndex, ulong TotalEopNs,
    int TimedEvents, int Offset, int ChildCount, IReadOnlyList<TimingBranchDto> Children,
    int? NextOffset, bool ChildrenTruncated, int ReturnedNodes, bool NodeBudgetExhausted,
    object Provenance, IReadOnlyList<ToolCallDto> NextCalls);
public sealed record CounterValueRowDto(EventRef EventRef, uint Index, uint? GpuId, string Name,
    IReadOnlyList<string> MarkerPath, IReadOnlyDictionary<string, object?> Values);

public static class CounterQuery
{
    private static bool IsFloating(object? value) => value is float or double or Half;
    private static double FloatingNumber(object? value)
        => value is Half half ? (double)half : Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);

    public static bool IsNumeric(object? value)
        => IsFloating(value) ? double.IsFinite(FloatingNumber(value)) : Number(value).HasValue;

    private static readonly IComparer<object?> NumericComparer = Comparer<object?>.Create((left, right) =>
    {
        bool leftNumeric = IsNumeric(left), rightNumeric = IsNumeric(right);
        if (!leftNumeric || !rightNumeric) return leftNumeric.CompareTo(rightNumeric);
        // Every row for one counter has the same PIX format. Integral formats stay decimal so
        // adjacent values above 2^53 remain distinct; floating formats retain their full exponent range.
        if (IsFloating(left) || IsFloating(right))
            return FloatingNumber(left).CompareTo(FloatingNumber(right));
        return Number(left)!.Value.CompareTo(Number(right)!.Value);
    });

    public static bool InRange(object? value, decimal? minimum, decimal? maximum)
    {
        if (!minimum.HasValue && !maximum.HasValue) return true;
        if (!IsNumeric(value)) return false;
        if (IsFloating(value))
        {
            double number = FloatingNumber(value);
            return (!minimum.HasValue || number >= (double)minimum.Value) && (!maximum.HasValue || number <= (double)maximum.Value);
        }
        decimal exact = Number(value)!.Value;
        return (!minimum.HasValue || exact >= minimum.Value) && (!maximum.HasValue || exact <= maximum.Value);
    }

    public static IEnumerable<CounterEventRow> ApplyNumericQuery(IEnumerable<CounterEventRow> rows, int valueIndex,
        bool descending, decimal? minimum, decimal? maximum)
    {
        if (valueIndex < 0) return rows;
        var presentFirst = rows.Where(row => InRange(row.Values[valueIndex], minimum, maximum))
            .OrderBy(row => IsNumeric(row.Values[valueIndex]) ? 0 : 1);
        return (descending ? presentFirst.ThenByDescending(row => row.Values[valueIndex], NumericComparer)
            : presentFirst.ThenBy(row => row.Values[valueIndex], NumericComparer)).ThenBy(row => row.Event.Index);
    }

    public static decimal? Number(object? value)
    {
        if (value is null or bool || value is string) return null;
        try { return Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture); }
        catch (Exception ex) when (ex is OverflowException or InvalidCastException or FormatException) { return null; }
    }

    public static Dictionary<string, object?> Values(IReadOnlyList<uint> ids, IReadOnlyList<object?> values)
    {
        var result = new Dictionary<string, object?>();
        for (int i = 0; i < ids.Count; i++)
            result[ids[i].ToString(System.Globalization.CultureInfo.InvariantCulture)] = values[i] switch
            {
                ulong n when n > 9007199254740991UL => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
                long n when n is > 9007199254740991L or < -9007199254740991L => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
                var value => value,
            };
        return result;
    }
}
