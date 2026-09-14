namespace PixMcp.Pix.StaticProfiling;

public sealed record ControlFlowLoop(uint Header, IReadOnlySet<uint> Blocks);

/// <summary>Loops of a control flow graph. <c>LoopDepth</c> covers every reachable block (0 outside loops).</summary>
public sealed record ControlFlowAnalysis(IReadOnlyList<ControlFlowLoop> Loops, IReadOnlyDictionary<uint, int> LoopDepth, bool Irreducible, IReadOnlySet<uint> Reachable);

/// <summary>
/// Dominators and natural loops over a shader's basic blocks. The first block is the entry; successors that name no block are
/// ignored. A retreating edge whose target does not dominate its source marks an irreducible region (no loop is reported for it).
/// </summary>
public static class ControlFlow
{
    public static ControlFlowAnalysis Analyze(IReadOnlyList<(uint Id, IReadOnlyList<uint> Successors)> blocks)
    {
        var depth = new Dictionary<uint, int>();
        if (blocks.Count == 0) return new([], depth, false, new HashSet<uint>());
        var known = blocks.Select(b => b.Id).ToHashSet();
        Dictionary<uint, uint[]> successors = blocks.GroupBy(b => b.Id)
            .ToDictionary(g => g.Key, g => g.SelectMany(b => b.Successors).Where(known.Contains).Distinct().ToArray());
        uint entry = blocks[0].Id;

        // Iterative depth-first search: postorder and retreating edges.
        var state = new Dictionary<uint, byte> { [entry] = 1 };
        var postorder = new List<uint>();
        var retreating = new List<(uint From, uint To)>();
        var stack = new Stack<(uint Node, int Next)>();
        stack.Push((entry, 0));
        while (stack.Count > 0)
        {
            (uint node, int next) = stack.Pop();
            uint[] children = successors[node];
            if (next < children.Length)
            {
                stack.Push((node, next + 1));
                uint child = children[next];
                if (!state.TryGetValue(child, out byte visit)) { state[child] = 1; stack.Push((child, 0)); }
                else if (visit == 1) retreating.Add((node, child));
            }
            else
            {
                state[node] = 2;
                postorder.Add(node);
            }
        }
        var reachable = state.Keys.ToHashSet();
        var predecessors = reachable.ToDictionary(n => n, _ => new List<uint>());
        foreach (uint node in reachable)
            foreach (uint child in successors[node])
                if (reachable.Contains(child)) predecessors[child].Add(node);

        // Iterative dominators in reverse postorder.
        uint[] order = Enumerable.Reverse(postorder).ToArray();
        var dominators = reachable.ToDictionary(n => n, n => n == entry ? new HashSet<uint> { entry } : new HashSet<uint>(reachable));
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (uint node in order)
            {
                if (node == entry) continue;
                HashSet<uint>? next = null;
                foreach (uint predecessor in predecessors[node])
                {
                    if (next is null) next = new HashSet<uint>(dominators[predecessor]);
                    else next.IntersectWith(dominators[predecessor]);
                }
                next ??= [];
                next.Add(node);
                if (!next.SetEquals(dominators[node])) { dominators[node] = next; changed = true; }
            }
        }

        // Natural loops: an edge tail -> header where the header dominates the tail.
        var loops = new SortedDictionary<uint, HashSet<uint>>();
        foreach (uint tail in reachable)
        {
            foreach (uint header in successors[tail])
            {
                if (!reachable.Contains(header) || !dominators[tail].Contains(header)) continue;
                if (!loops.TryGetValue(header, out HashSet<uint>? body)) loops[header] = body = [header];
                var work = new Stack<uint>();
                if (body.Add(tail)) work.Push(tail);
                while (work.Count > 0)
                    foreach (uint predecessor in predecessors[work.Pop()])
                        if (body.Add(predecessor)) work.Push(predecessor);
            }
        }
        bool irreducible = retreating.Any(edge => !dominators[edge.From].Contains(edge.To));
        ControlFlowLoop[] found = loops.Select(kv => new ControlFlowLoop(kv.Key, kv.Value)).ToArray();
        foreach (uint node in reachable) depth[node] = found.Count(loop => loop.Blocks.Contains(node));
        return new(found, depth, irreducible, reachable);
    }
}
