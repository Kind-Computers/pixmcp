using System.ComponentModel;

namespace PixMcp.Pix;

public sealed record TextDiffLineDto(
    [property: Description("delete (only in the baseline) or insert (only in the candidate).")] string Op,
    [property: Description("1-based baseline line of a deleted line.")] int? BaselineLine,
    [property: Description("1-based candidate line of an inserted line.")] int? CandidateLine,
    string Text);

internal sealed record TextDiffResult(int Added, int Removed, int Unchanged, IReadOnlyList<TextDiffLineDto> FirstDifferences, bool Truncated);

/// <summary>Line diff (Myers, after trimming the common prefix and suffix) with caps on size and edit distance; pure.</summary>
internal static class TextDiff
{
    public const int MaxLines = 20_000, MaxEditDistance = 2_000, MaxReportedDifferences = 20, MaxLineLength = 200;

    public static TextDiffResult Lines(string baseline, string candidate)
    {
        string[] a = Split(baseline), b = Split(candidate);
        if (a.Length > MaxLines || b.Length > MaxLines) return Approximate(a, b);
        int prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix]) prefix++;
        int suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix]) suffix++;
        List<(char Op, int A, int B)>? script = Myers(a, prefix, a.Length - suffix, b, prefix, b.Length - suffix);
        if (script is null) return Approximate(a, b);
        int added = script.Count(s => s.Op == '+'), removed = script.Count(s => s.Op == '-');
        TextDiffLineDto[] first = script.Take(MaxReportedDifferences)
            .Select(s => s.Op == '-' ? new TextDiffLineDto("delete", s.A + 1, null, Cut(a[s.A])) : new TextDiffLineDto("insert", null, s.B + 1, Cut(b[s.B])))
            .ToArray();
        return new(added, removed, a.Length - removed, first, false);
    }

    /// <summary>Shortest edit script between a[aStart..aEnd) and b[bStart..bEnd); null when the edit distance exceeds the cap.</summary>
    private static List<(char Op, int A, int B)>? Myers(string[] a, int aStart, int aEnd, string[] b, int bStart, int bEnd)
    {
        int n = aEnd - aStart, m = bEnd - bStart, max = n + m;
        var edits = new List<(char Op, int A, int B)>();
        if (max == 0) return edits;
        int offset = max + 1;
        var v = new int[2 * max + 3];
        var trace = new List<int[]>();
        int found = -1;
        for (int d = 0; d <= max && d <= MaxEditDistance && found < 0; d++)
        {
            var snapshot = new int[2 * d + 1];
            for (int k = -d; k <= d; k += 2)
            {
                int x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]) ? v[offset + k + 1] : v[offset + k - 1] + 1;
                int y = x - k;
                while (x < n && y < m && a[aStart + x] == b[bStart + y])
                {
                    x++;
                    y++;
                }
                v[offset + k] = x;
                snapshot[k + d] = x;
                if (x >= n && y >= m)
                {
                    found = d;
                    break;
                }
            }
            trace.Add(snapshot);
        }
        if (found < 0) return null;
        int cx = n, cy = m;
        for (int d = found; d > 0; d--)
        {
            int[] previous = trace[d - 1];
            int step = d;
            int PreviousAt(int k) => previous[k + step - 1];
            int kHere = cx - cy;
            int prevK = kHere == -d || (kHere != d && PreviousAt(kHere - 1) < PreviousAt(kHere + 1)) ? kHere + 1 : kHere - 1;
            int prevX = PreviousAt(prevK), prevY = prevX - prevK;
            while (cx > prevX && cy > prevY)
            {
                cx--;
                cy--;
            }
            if (cx == prevX)
            {
                cy--;
                edits.Add(('+', aStart + cx, bStart + cy));
            }
            else
            {
                cx--;
                edits.Add(('-', aStart + cx, bStart + cy));
            }
        }
        edits.Reverse();
        return edits;
    }

    /// <summary>Counts by line multiset and reports the first differing line pair; used past the caps.</summary>
    private static TextDiffResult Approximate(string[] a, string[] b)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string line in a) counts[line] = counts.GetValueOrDefault(line) + 1;
        int common = 0;
        foreach (string line in b)
            if (counts.TryGetValue(line, out int count) && count > 0)
            {
                counts[line] = count - 1;
                common++;
            }
        int first = 0;
        while (first < a.Length && first < b.Length && a[first] == b[first]) first++;
        var differences = new List<TextDiffLineDto>();
        if (first < a.Length) differences.Add(new("delete", first + 1, null, Cut(a[first])));
        if (first < b.Length) differences.Add(new("insert", null, first + 1, Cut(b[first])));
        return new(b.Length - common, a.Length - common, common, differences, true);
    }

    private static string[] Split(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    private static string Cut(string line) => line.Length <= MaxLineLength ? line : line[..MaxLineLength] + "…";
}
