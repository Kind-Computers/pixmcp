namespace PixMcp.Pix;

/// <summary>One recorded PIX event on a lane (a thread's PixCpuExecution or a queue's PixGpuExecution row).</summary>
internal readonly record struct RecordedMarkerRow(long RowId, long Begin, long End, int Level, string Name, long? ExecutionNs = null, long? StallNs = null);

/// <summary>
/// Recorded PIX events nested by Level and interval and aggregated by marker path. Inclusive and self time are clipped
/// to the window; occurrence durations, execution and stall describe complete occurrences. Pure: no SQLite or PIX types.
/// </summary>
internal sealed class RecordedMarkerTree
{
    public static readonly string[] SortKeys = ["inclusive", "self", "occurrences", "name", "firstStart"];

    internal sealed class PathNode(string path, string name, int depth, PathNode? parent)
    {
        public string Path { get; } = path;
        public string Name { get; } = name;
        /// <summary>Path segments; 1 for a top-level event, 0 for the synthetic root.</summary>
        public int Depth { get; } = depth;
        /// <summary>The parent path node; null for top-level paths and the root.</summary>
        public PathNode? Parent { get; } = parent;
        public Dictionary<string, PathNode> Children { get; } = new(StringComparer.Ordinal);
        public long Occurrences { get; set; }
        public long InclusiveNs { get; set; }
        /// <summary>Children clipped to their parent occurrence and the window; subtracted to get self time.</summary>
        public long ChildrenInsideNs { get; set; }
        /// <summary>Children clipped to the window only.</summary>
        public long ChildSumNs { get; set; }
        /// <summary>Child time outside the parent occurrence's interval (malformed nesting).</summary>
        public long ChildOverflowNs { get; set; }
        public List<long> Durations { get; } = [];
        /// <summary>Complete [begin, end) interval of every occurrence.</summary>
        public List<(long Begin, long End)> Intervals { get; } = [];
        public long ExecutionNs { get; set; }
        public long StallNs { get; set; }
        public long TimedOccurrences { get; set; }
        public long FirstStart { get; set; } = long.MaxValue;
        public long SlowestBegin { get; set; }
        public long SlowestEnd { get; set; }
        public long SelfNs => Math.Max(0, InclusiveNs - ChildrenInsideNs);
        public bool ChildrenExceedMeasured => ChildOverflowNs > 0 || ChildSumNs > InclusiveNs;
        public string ExecutionTiming => TimedOccurrences == 0 ? "unavailable" : TimedOccurrences == Occurrences ? "available" : "partial";

        /// <summary>The marker names from the top-level event down to this path.</summary>
        public IReadOnlyList<string> Segments()
        {
            var names = new List<string>();
            for (PathNode? node = this; node is not null; node = node.Parent) names.Add(node.Name);
            names.Reverse();
            return names;
        }
    }

    private readonly Dictionary<string, PathNode> _byPath = new(StringComparer.Ordinal);

    public PathNode Root { get; } = new("", "", 0, null);
    public long Rows { get; private set; }
    /// <summary>Top-level occurrences deeper than the lane's shallowest level: their parent was not read.</summary>
    public long Orphans { get; private set; }
    /// <summary>Occurrences that extend outside their parent's interval.</summary>
    public long MalformedNestings { get; private set; }
    public List<(long Begin, long End)> RootIntervals { get; } = [];

    public PathNode? Find(string path) => _byPath.GetValueOrDefault(path);

    /// <summary>Every path node below <paramref name="from"/> (default: the root), in no particular order.</summary>
    public IEnumerable<PathNode> Descendants(PathNode? from = null)
    {
        var pending = new Stack<PathNode>((from ?? Root).Children.Values);
        while (pending.Count > 0)
        {
            PathNode node = pending.Pop();
            yield return node;
            foreach (PathNode child in node.Children.Values) pending.Push(child);
        }
    }

    public static RecordedMarkerTree Build(IReadOnlyList<RecordedMarkerRow> rows, long windowStart, long windowEnd)
    {
        var tree = new RecordedMarkerTree();
        int shallowest = rows.Count == 0 ? 0 : rows.Min(r => r.Level);
        var open = new Stack<(RecordedMarkerRow Row, PathNode Node)>();
        foreach (RecordedMarkerRow row in rows.OrderBy(r => r.Begin).ThenBy(r => r.Level).ThenBy(r => r.End).ThenBy(r => r.RowId))
        {
            tree.Rows++;
            // A zero-length event at its parent's end instant (a marker closed by the capture end) still belongs to that parent.
            while (open.Count > 0 && (open.Peek().Row.End < row.Begin || open.Peek().Row.End == row.Begin && row.End > row.Begin || open.Peek().Row.Level >= row.Level))
                open.Pop();
            PathNode parentNode = tree.Root;
            if (open.Count > 0)
            {
                (RecordedMarkerRow parent, parentNode) = open.Peek();
                parentNode.ChildrenInsideNs += Clip(Math.Max(row.Begin, parent.Begin), Math.Min(row.End, parent.End), windowStart, windowEnd);
                parentNode.ChildSumNs += Clip(row.Begin, row.End, windowStart, windowEnd);
                long overflow = Math.Max(0, row.End - parent.End) + Math.Max(0, parent.Begin - row.Begin);
                if (overflow > 0)
                {
                    parentNode.ChildOverflowNs += overflow;
                    tree.MalformedNestings++;
                }
            }
            else
            {
                if (row.Level > shallowest) tree.Orphans++;
                tree.RootIntervals.Add((row.Begin, row.End));
            }

            if (!parentNode.Children.TryGetValue(row.Name, out PathNode? node))
            {
                bool topLevel = ReferenceEquals(parentNode, tree.Root);
                string path = topLevel ? row.Name : parentNode.Path + "/" + row.Name;
                node = new PathNode(path, row.Name, parentNode.Depth + 1, topLevel ? null : parentNode);
                parentNode.Children[row.Name] = node;
                tree._byPath.TryAdd(path, node);
            }
            long duration = Math.Max(0, row.End - row.Begin);
            node.Occurrences++;
            node.InclusiveNs += Clip(row.Begin, row.End, windowStart, windowEnd);
            if (node.Durations.Count == 0 || duration > node.SlowestEnd - node.SlowestBegin)
            {
                node.SlowestBegin = row.Begin;
                node.SlowestEnd = row.End;
            }
            node.Durations.Add(duration);
            node.Intervals.Add((row.Begin, row.End));
            node.FirstStart = Math.Min(node.FirstStart, row.Begin);
            if (row.ExecutionNs is long execution && row.StallNs is long stall)
            {
                node.TimedOccurrences++;
                node.ExecutionNs += execution;
                node.StallNs += stall;
            }
            open.Push((row, node));
        }
        return tree;
    }

    /// <summary>
    /// Pre-order listing of <paramref name="parent"/>'s descendants up to <paramref name="depth"/> levels, siblings in
    /// <paramref name="sortBy"/> order with a 1-based rank. A path whose self time is below <paramref name="minSelfNs"/>
    /// is listed only when one of its descendants is.
    /// </summary>
    public List<(PathNode Node, int Rank)> Flatten(PathNode parent, int depth, string sortBy, long minSelfNs)
    {
        return Collect(parent, 1);

        List<(PathNode Node, int Rank)> Collect(PathNode node, int level)
        {
            var listed = new List<(PathNode Node, int Rank)>();
            if (level > depth) return listed;
            int rank = 0;
            foreach (PathNode child in Sort(node.Children.Values, sortBy))
            {
                List<(PathNode Node, int Rank)> below = Collect(child, level + 1);
                if (child.SelfNs < minSelfNs && below.Count == 0) continue;
                listed.Add((child, ++rank));
                listed.AddRange(below);
            }
            return listed;
        }
    }

    public static IEnumerable<PathNode> Sort(IEnumerable<PathNode> nodes, string sortBy) => sortBy switch
    {
        "self" => nodes.OrderByDescending(n => n.SelfNs).ThenByDescending(n => n.InclusiveNs).ThenBy(n => n.Name, StringComparer.Ordinal),
        "occurrences" => nodes.OrderByDescending(n => n.Occurrences).ThenByDescending(n => n.InclusiveNs).ThenBy(n => n.Name, StringComparer.Ordinal),
        "name" => nodes.OrderBy(n => n.Name, StringComparer.Ordinal).ThenBy(n => n.FirstStart),
        "firstStart" => nodes.OrderBy(n => n.FirstStart).ThenBy(n => n.Name, StringComparer.Ordinal),
        _ => nodes.OrderByDescending(n => n.InclusiveNs).ThenByDescending(n => n.SelfNs).ThenBy(n => n.Name, StringComparer.Ordinal),
    };

    private static long Clip(long begin, long end, long windowStart, long windowEnd) => Math.Max(0, Math.Min(end, windowEnd) - Math.Max(begin, windowStart));
}
