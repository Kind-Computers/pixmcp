using ModelContextProtocol;

namespace PixMcp.Pix;

/// <summary>Line windows use one-based indices; every returned line remains retrievable.</summary>
internal static class ShaderText
{
    internal static string[] Lines(string code) => code.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    internal static (string Code, int Count, int TotalLines, int? NextStartLine) Window(string code, int startLine, int lineCount)
    {
        ValidateWindow(startLine, lineCount);
        string[] lines = Lines(code);
        string[] selected = lines.Skip(startLine - 1).Take(lineCount).ToArray();
        int next = startLine + selected.Length;
        return (string.Join("\n", selected), selected.Length, lines.Length, next <= lines.Length ? next : null);
    }

    internal static void ValidateWindow(int startLine, int lineCount)
    {
        if (startLine < 1) throw PixErrors.InvalidArguments("startLine must be at least 1.");
        if (lineCount is < 1 or > 1000) throw PixErrors.InvalidArguments("lineCount must be between 1 and 1000.");
    }

    internal static IEnumerable<ShaderSearchMatchDto> Search(string code, string query, ulong nodeIndex,
        string? nodeName, int contextLines)
    {
        if (string.IsNullOrEmpty(query)) throw PixErrors.InvalidArguments("query must not be empty.");
        if (contextLines is < 0 or > 20) throw PixErrors.InvalidArguments("contextLines must be between 0 and 20.");
        string[] lines = Lines(code);
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            int start = Math.Max(0, i - contextLines);
            int end = Math.Min(lines.Length, i + contextLines + 1);
            yield return new(nodeIndex, nodeName, i + 1, start + 1, lines[start..end]);
        }
    }
}
