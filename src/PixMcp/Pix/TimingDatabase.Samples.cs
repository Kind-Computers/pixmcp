using System.Buffers.Binary;
using Microsoft.Data.Sqlite;

namespace PixMcp.Pix;

internal sealed partial class TimingDatabase
{
    private sealed record LoadedImage(long Id, uint ProcessId, ulong Address, ulong Size, long Start, long? End,
        string? Path, long? ModuleId);
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
        bool images = Has("Images", "Id", "OSProcessId", "PELoadAddress", "LoadSize", "LoadTimestamp", "UnloadTimestamp", "FilePathId", "ModuleId")
            && Has("Strings", "Id", "Value");
        bool symbols = Has("FunctionInformation", "Id", "ModuleId", "Offset", "Size", "DecoratedNameId") && Has("SymbolStrings", "Id", "Value");
        bool source = symbols && Has("SourceLine", "Id", "ModuleId", "SourceFileId", "Offset", "Length", "LineStart")
            && Has("SourceFile", "Id", "SourceFileNameId");
        List<LoadedImage> loaded = images ? Rows("SELECT i.Id,i.OSProcessId,i.PELoadAddress,i.LoadSize,i.LoadTimestamp,i.UnloadTimestamp,s.Value,i.ModuleId FROM Images i LEFT JOIN Strings s ON s.Id=i.FilePathId WHERE ($pid IS NULL OR i.OSProcessId=$pid OR i.OSProcessId=0) AND i.LoadTimestamp < $end AND (i.UnloadTimestamp IS NULL OR i.UnloadTimestamp >= $start) ORDER BY i.LoadTimestamp DESC,i.Id", r =>
            new LoadedImage(r.GetInt64(0), checked((uint)r.GetInt64(1)), unchecked((ulong)r.GetInt64(2)), unchecked((ulong)r.GetInt64(3)),
                r.GetInt64(4), r.IsDBNull(5) ? null : r.GetInt64(5), Text(r, 6), r.IsDBNull(7) ? null : r.GetInt64(7)),
            ("$pid", processId), ("$start", a), ("$end", b)) : [];
        var functions = new Dictionary<(long image, ulong address), TimingFunctionDto>();
        var functionOrder = new Queue<(long image, ulong address)>();
        var stackCache = new Dictionary<long, ulong[]?>();
        var stackOrder = new Queue<long>();
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
                ulong[]? addresses = Stack(reader.GetInt64(3));
                if (addresses is null || addresses.Length == 0) { invalidStacks++; root.Exclusive++; continue; }
                withStacks++;
                long timestamp = reader.GetInt64(0);
                uint pid = unchecked((uint)((ulong)reader.GetInt64(2) >> 32));
                TimingFunctionDto[] frames = addresses.Select(address => Resolve(pid, timestamp, address)).ToArray();
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
                !symbols ? "unsupported" : resolvedFrames == 0 ? "unresolved" : unresolvedFrames > 0 ? "partial" : "resolved"), rows, tree);

        ulong[]? Stack(long stackId)
        {
            if (stackCache.TryGetValue(stackId, out ulong[]? cached)) return cached;
            var rows = Rows("SELECT NumFrames,Addresses FROM Stacks WHERE Id=$id", r =>
                (frames: Number(r, 0), blob: r.IsDBNull(1) ? null : (byte[])r.GetValue(1)), ("$id", stackId));
            ulong[]? result = rows.Count == 1 ? DecodeStack(rows[0].frames, rows[0].blob) : null;
            stackCache.Add(stackId, result); stackOrder.Enqueue(stackId);
            if (stackOrder.Count > 512) stackCache.Remove(stackOrder.Dequeue());
            return result;
        }

        TimingFunctionDto Resolve(uint pid, long timestamp, ulong address)
        {
            // Kernel images are associated with PID 0; user images are scoped by PID and load lifetime.
            LoadedImage? image = loaded.FirstOrDefault(i => i.ProcessId == pid && Contains(i))
                ?? loaded.FirstOrDefault(i => i.ProcessId == 0 && Contains(i));
            bool Contains(LoadedImage i) => i.Start <= timestamp && (!i.End.HasValue || timestamp < i.End.Value)
                && address >= i.Address && address - i.Address < i.Size;
            var cacheKey = (image?.Id ?? -(long)pid - 1, address);
            if (functions.TryGetValue(cacheKey, out TimingFunctionDto? cached)) return cached;
            string hex = "0x" + address.ToString("X");
            ulong rva = image is null ? address : address - image.Address;
            string identity = image?.ModuleId is long moduleId ? "module-" + moduleId
                : image is not null ? "image-" + image.Id : "pid-" + pid;
            string key = identity + "/address-" + rva.ToString("X");
            string? functionName = null, functionOffset = null, sourceFile = null; int? sourceLine = null;
            if (symbols && image?.ModuleId is long module && rva <= long.MaxValue)
            {
                var matches = Rows("SELECT f.Id,f.Offset,s.Value FROM FunctionInformation f LEFT JOIN SymbolStrings s ON s.Id=f.DecoratedNameId WHERE f.ModuleId=$module AND f.Offset <= $offset AND f.Size>0 AND ($offset-f.Offset)<f.Size ORDER BY f.Offset DESC,f.Size,f.Id LIMIT 1",
                    r => (id: r.GetInt64(0), offset: r.GetInt64(1), name: Text(r, 2)), ("$module", module), ("$offset", (long)rva));
                if (matches.Count == 1 && !string.IsNullOrWhiteSpace(matches[0].name))
                {
                    key = identity + "/function-" + matches[0].id;
                    functionName = matches[0].name; functionOffset = "0x" + matches[0].offset.ToString("X");
                    if (source)
                    {
                        var locations = Rows("SELECT s.Value,l.LineStart FROM SourceLine l JOIN SourceFile f ON f.Id=l.SourceFileId LEFT JOIN SymbolStrings s ON s.Id=f.SourceFileNameId WHERE l.ModuleId=$module AND l.Offset <= $offset AND l.Length>0 AND ($offset-l.Offset)<l.Length ORDER BY l.Offset DESC,l.Length,l.Id LIMIT 1",
                            r => (file: Text(r, 0), line: r.IsDBNull(1) ? (int?)null : r.GetInt32(1)), ("$module", module), ("$offset", (long)rva));
                        if (locations.Count == 1) (sourceFile, sourceLine) = locations[0];
                    }
                }
            }
            var result = new TimingFunctionDto(key, hex, image?.Path, image?.ModuleId is long m ? Ns(m) : null,
                functionName, functionOffset, functionName is not null ? "resolved" : image is null ? "module_unknown" : "unresolved", sourceFile, sourceLine);
            functions.Add(cacheKey, result);
            functionOrder.Enqueue(cacheKey);
            if (functionOrder.Count > 8192) functions.Remove(functionOrder.Dequeue());
            return result;
        }
    });

    internal static ulong[]? DecodeStack(long count, byte[]? bytes)
    {
        if (bytes is null || count <= 0 || bytes.Length % 8 != 0 || count != bytes.Length / 8) return null;
        var addresses = new ulong[checked((int)count)];
        for (int i = 0; i < addresses.Length; i++) addresses[i] = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(i * 8, 8));
        return addresses;
    }
}
