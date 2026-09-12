using System.Buffers.Binary;
using Microsoft.Data.Sqlite;

namespace PixMcp.Pix;

internal sealed partial class TimingDatabase
{
    private sealed class SampleNode(string id, SampleNode? parent, TimingFunctionDto? function)
    {
        public string Id { get; } = id;
        public SampleNode? Parent { get; } = parent;
        public TimingFunctionDto? Function { get; } = function;
        public long Inclusive, Exclusive;
        public Dictionary<string, SampleNode> Children { get; } = new(StringComparer.Ordinal);
    }
    private sealed class Hotspot(TimingFunctionDto function)
    {
        public TimingFunctionDto Function { get; } = function;
        public long Inclusive, Exclusive;
    }

    internal TimingSampleAnalysisDto Samples(string handle, uint? processId, uint? threadId, long? start, long? end) => Guard<TimingSampleAnalysisDto>(() =>
    {
        var (a, b, provenance) = Range(start, end);
        Require("CpuSample", "Timestamp", "Core", "ProcThreadId");
        bool stacks = Has("Stacks", "Id", "NumFrames", "Addresses") &&
            Has("StackEvents", "OSThreadId", "StartTimestamp", "EndTimestamp", "StackEventData") && HasFunction("FindStackId", 2);
        InitializeSymbols(processId, a, b);
        var hotspots = new Dictionary<string, Hotspot>(StringComparer.Ordinal);
        var root = new SampleNode("root", null, null);
        var nodes = new List<SampleNode> { root };
        long samples = 0, withStacks = 0, invalidStacks = 0, unresolvedSamples = 0, resolvedFrames = 0, unresolvedFrames = 0;
        // PixStorage looks up StackEvents itself. Its first argument is the OS thread ID;
        // passing the serialized event blob coerces it to zero and silently reads idle stacks.
        string from = stacks ?
            "SELECT c.Timestamp,c.Core,c.ProcThreadId,NULLIF(FindStackId(c.ProcThreadId & 4294967295,c.Timestamp),0) FROM CpuSample c" :
            "SELECT c.Timestamp,c.Core,c.ProcThreadId,NULL FROM CpuSample c";
        string sql = from + " WHERE c.Timestamp >= $start AND c.Timestamp < $end AND ($pid IS NULL OR (c.ProcThreadId >> 32)=$pid) AND ($tid IS NULL OR (c.ProcThreadId & 4294967295)=$tid)" +
            " ORDER BY c.Timestamp,c.Core,c.ProcThreadId";
        using (SqliteCommand command = Command(sql, [("$start", a), ("$end", b), ("$pid", processId), ("$tid", threadId)]))
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                Check();
                samples++; root.Inclusive++;
                if (reader.IsDBNull(3)) { root.Exclusive++; continue; }
                ulong[]? addresses = ReadStack(reader.GetInt64(3));
                if (addresses is null || addresses.Length == 0) { invalidStacks++; root.Exclusive++; continue; }
                withStacks++;
                long timestamp = reader.GetInt64(0);
                uint pid = unchecked((uint)((ulong)reader.GetInt64(2) >> 32));
                TimingFunctionDto[] frames = addresses.Select(address => ResolveFunction(pid, timestamp, address)).ToArray();
                long unresolved = frames.LongCount(f => f.SymbolState != "resolved");
                resolvedFrames += frames.Length - unresolved; unresolvedFrames += unresolved;
                if (unresolved > 0) unresolvedSamples++;
                var visited = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < frames.Length; i++)
                {
                    TimingFunctionDto frame = frames[i];
                    if (!hotspots.TryGetValue(frame.Key, out Hotspot? hotspot)) hotspots.Add(frame.Key, hotspot = new(frame));
                    // Inclusive function counts count a recursive function once per sample.
                    if (visited.Add(frame.Key)) hotspot.Inclusive++;
                    if (i == 0) hotspot.Exclusive++;
                }
                SampleNode parent = root;
                // PIX's Stacks blobs store the sampled instruction first, followed by its callers.
                foreach (TimingFunctionDto frame in frames.Reverse())
                {
                    if (!parent.Children.TryGetValue(frame.Key, out SampleNode? node))
                    {
                        node = new("node-" + nodes.Count, parent, frame);
                        nodes.Add(node); parent.Children.Add(frame.Key, node);
                    }
                    node.Inclusive++; parent = node;
                }
                parent.Exclusive++;
            }
        }
        double Percent(long count) => samples == 0 ? 0 : count * 100d / samples;
        var rows = hotspots.Values.OrderByDescending(h => h.Inclusive).ThenByDescending(h => h.Exclusive).ThenBy(h => h.Function.Key, StringComparer.Ordinal)
            .Select(h => new TimingHotspotDto(h.Function, h.Inclusive, h.Exclusive, Percent(h.Inclusive), Percent(h.Exclusive))).ToArray();
        // Persist sibling order once so calltree pages can stream the snapshot with O(limit)
        // retained rows, even when a root has many distinct sampled callers.
        var tree = nodes.OrderBy(n => n.Parent?.Id, StringComparer.Ordinal).ThenByDescending(n => n.Inclusive)
            .ThenBy(n => n.Function?.Key, StringComparer.Ordinal)
            .Select(n => new TimingCallNodeDto(n.Id, n.Parent?.Id, n.Function, n.Inclusive, n.Exclusive, Percent(n.Inclusive), n.Children.Count)).ToArray();
        return new(handle, provenance, new(processId, threadId),
            new(samples, withStacks, samples - withStacks, invalidStacks, unresolvedSamples, resolvedFrames, unresolvedFrames,
                !stacks ? "unsupported" : withStacks > 0 ? "available" : "empty",
                !_hasSymbols ? "unsupported" : resolvedFrames == 0 ? "unresolved" : unresolvedFrames > 0 ? "partial" : "resolved"), rows, tree);
    });

    internal static ulong[]? DecodeStack(long count, byte[]? bytes)
    {
        if (bytes is null || count <= 0 || bytes.Length % 8 != 0 || count != bytes.Length / 8) return null;
        var addresses = new ulong[checked((int)count)];
        for (int i = 0; i < addresses.Length; i++) addresses[i] = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(i * 8, 8));
        return addresses;
    }
}
