using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PixMcp.Pix;

/// <summary>
/// Seekable JSON navigation over trusted serializer output. Container scans and string decoding
/// consume a fixed byte buffer; even one enormous string never becomes a JsonDocument token.
/// </summary>
internal sealed class StoredJson(Stream stream, CancellationToken cancellationToken = default, string? reference = null)
{
    private const int MaxOutlineKeys = 25;
    internal readonly record struct Node(long Start, long End, JsonValueKind Kind, int Total)
    { internal long Bytes => End - Start; }
    private readonly byte[] _buffer = new byte[65536];
    private long _start;
    private int _position, _count;
    private long Position => _start + _position;
    private void Seek(long position)
    {
        if (position >= _start && position < _start + _count) { _position = (int)(position - _start); return; }
        stream.Position = position; _start = position; _position = _count = 0;
    }
    private int Peek()
    {
        if (_position < _count) return _buffer[_position];
        cancellationToken.ThrowIfCancellationRequested();
        _start += _count; _position = 0; stream.Position = _start; _count = stream.Read(_buffer);
        cancellationToken.ThrowIfCancellationRequested();
        return _count == 0 ? -1 : _buffer[0];
    }
    private int Take() { int value = Peek(); if (value >= 0) _position++; return value; }
    private void White() { while (Peek() is 9 or 10 or 13 or 32) Take(); }
    private void Require(int expected) { if (Take() != expected) throw new JsonException("Invalid retained JSON."); }
    internal static PixToolException InvalidPointer(string pointer) => new("invalid_pointer", $"JSON pointer '{pointer}' does not identify a value in the result.");
    internal static IEnumerable<string> PointerTokens(string pointer)
    {
        if (pointer.Length == 0) yield break;
        if (!pointer.StartsWith('/')) throw new PixToolException(PixErrors.Codes.InvalidPointer, "A JSON pointer must be empty or begin with '/'.");
        foreach (string encoded in pointer.Split('/').Skip(1))
        {
            for (int i = 0; i < encoded.Length; i++)
                if (encoded[i] == '~' && (i + 1 >= encoded.Length || encoded[++i] is not ('0' or '1')))
                    throw new PixToolException(PixErrors.Codes.InvalidPointer, "JSON pointer escapes must be ~0 or ~1.");
            yield return encoded.Replace("~1", "/").Replace("~0", "~");
        }
    }
    internal static string Escape(string key) => key.Replace("~", "~0").Replace("/", "~1");
    internal static bool ArrayIndex(string token, out int index)
    {
        index = -1;
        return token.Length > 0 && (token == "0" || token[0] != '0') &&
            int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out index);
    }
    internal Node Locate(string pointer)
    {
        (Node value, string resolved, string? failed) = LocateNearest(pointer);
        if (failed is null) return value;
        var keys = new List<string>();
        if (value.Kind is JsonValueKind.Array or JsonValueKind.Object)
            foreach (var child in Children(value)) { if (keys.Count >= MaxOutlineKeys) break; keys.Add(child.Key); }
        throw PixErrors.InvalidPointer(pointer, resolved, KindName(value.Kind), keys, value.Total, reference);
    }

    /// <summary>Walks a pointer as far as it resolves: the deepest located node, the pointer prefix that resolved, and the first token that did not.</summary>
    internal (Node Node, string Resolved, string? Failed) LocateNearest(string pointer)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string[] tokens = PointerTokens(pointer).ToArray();
        Seek(0); Node value = Scan(); var resolved = new StringBuilder();
        foreach (string token in tokens)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Node? found = null;
            if (value.Kind is JsonValueKind.Array or JsonValueKind.Object && !(value.Kind == JsonValueKind.Array && !ArrayIndex(token, out _)))
                foreach (var child in Children(value))
                    if (child.Key == token) { found = child.Value; if (value.Kind == JsonValueKind.Array) break; }
            if (found is null) return (value, resolved.ToString(), token);
            value = found.Value; resolved.Append('/').Append(Escape(token));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return (value, resolved.ToString(), null);
    }

    internal static string KindName(JsonValueKind kind) => kind.ToString().ToLowerInvariant();

    /// <summary>The shape of a value without its contents: one entry per child with kind, count, bytes and a key sample.</summary>
    internal ResultOutlineDto Outline(Node node, string resultRef, string pointer, int offset, int limit)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = new List<ResultOutlineEntry>();
        if (node.Kind is JsonValueKind.Array or JsonValueKind.Object)
            foreach (var child in Children(node).Skip(offset).Take(limit))
            {
                cancellationToken.ThrowIfCancellationRequested();
                entries.Add(new(child.Key, KindName(child.Value.Kind), child.Value.Total, child.Value.Bytes,
                    child.Value.Bytes > ResultStore.TargetBytes / 2, ItemKeys(child.Value)));
            }
        int? next = node.Kind is JsonValueKind.Array or JsonValueKind.Object && (long)offset + entries.Count < node.Total ? offset + entries.Count : null;
        return new(resultRef, pointer, KindName(node.Kind), node.Total, node.Bytes, offset, entries.Count, next, entries,
            next.HasValue ? [ResultStore.ReadCall(resultRef, pointer, next.Value, limit, "outline")] : []);
    }

    /// <summary>Property names of an object child, or of the first element of an array of objects (row shape), up to 25.</summary>
    private IReadOnlyList<string>? ItemKeys(Node child)
    {
        Node sample = child;
        if (child.Kind == JsonValueKind.Array)
        {
            if (child.Total == 0) return null;
            sample = Children(child).First().Value;
        }
        if (sample.Kind != JsonValueKind.Object) return null;
        var keys = new List<string>();
        foreach (var property in Children(sample)) { if (keys.Count >= MaxOutlineKeys) break; keys.Add(property.Key); }
        return keys;
    }

    /// <summary>
    /// Reads an array with server-side field selection and filtering. Every element is evaluated (elements above the
    /// hard budget are counted as unevaluated); projected items fill the page under the inline budget.
    /// Without predicates, oversized rows retain their array positions as deferred children.
    /// </summary>
    internal ResultReadDto Project(Node node, string resultRef, string pointer, int offset, int limit,
        IReadOnlyList<string>? fields, IReadOnlyList<WhereClause>? where)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node.Kind != JsonValueKind.Array)
            throw PixErrors.InvalidArguments($"fields and where apply to array values; '{pointer}' is {KindName(node.Kind)}.",
                [ResultStore.ReadCall(resultRef, pointer, mode: "outline")]);
        bool filtered = where is { Count: > 0 };
        var items = new JsonArray(); int scanned = 0, matched = 0, unevaluated = 0, used = 0; bool full = false;
        foreach (var child in Children(node))
        {
            cancellationToken.ThrowIfCancellationRequested();
            scanned++;
            string childPointer = pointer + "/" + child.Key;
            bool oversized = child.Value.Bytes > ServerOptions.Current.MaxResultBytes;
            JsonElement element = default;
            if (oversized)
            {
                unevaluated++;
                if (filtered) continue;
            }
            else
            {
                element = Element(child.Value, ServerOptions.Current.MaxResultBytes, resultRef, childPointer);
                if (filtered && !ResultProjection.Matches(element, where!)) continue;
            }
            matched++;
            if (matched <= offset || full) continue;
            JsonNode projected = oversized
                ? DeferredChild(child.Value, resultRef, childPointer)
                : fields is null ? JsonNode.Parse(element.GetRawText())! : ResultProjection.Select(element, fields);
            int bytes = Encoding.UTF8.GetByteCount(projected.ToJsonString());
            if (items.Count >= limit || (items.Count > 0 && used + bytes > ResultStore.TargetBytes - 4096)) { full = true; continue; }
            items.Add(projected); used += bytes;
        }
        int total = filtered ? matched : node.Total;
        int? next = (long)offset + items.Count < total ? offset + items.Count : null;
        return new(resultRef, pointer, "array", total, offset, items.Count, next, items,
            next.HasValue ? [ResultStore.ReadCall(resultRef, pointer, next.Value, limit, null, fields, where)] : [])
        { Projection = new(fields, where, scanned, matched, unevaluated) };
    }
    private static JsonNode DeferredChild(Node node, string resultRef, string pointer)
        => JsonSerializer.SerializeToNode(new { deferred = true, pointer,
            kind = KindName(node.Kind), nextCalls = new[] { ResultStore.ReadCall(resultRef, pointer) } }, Json.Options)!;
    private Node Scan()
    {
        White(); long start = Position; int first = Peek(); int total = 1;
        JsonValueKind kind;
        if (first == '"') { kind = JsonValueKind.String; total = SkipString(); }
        else if (first is '[' or '{')
        {
            kind = first == '[' ? JsonValueKind.Array : JsonValueKind.Object;
            Take(); White(); int close = first == '[' ? ']' : '}'; total = 0;
            while (Peek() != close)
            {
                if (total > 0) { Require(','); White(); }
                if (kind == JsonValueKind.Object) { SkipString(); White(); Require(':'); }
                Scan(); total = checked(total + 1); White();
            }
            Take();
        }
        else
        {
            kind = first switch { 't' => JsonValueKind.True, 'f' => JsonValueKind.False, 'n' => JsonValueKind.Null, _ => JsonValueKind.Number };
            while (Peek() is int next && next >= 0 && next is not (',' or ']' or '}' or 9 or 10 or 13 or 32)) Take();
            if (Position == start) throw new JsonException("Invalid retained JSON.");
        }
        return new(start, Position, kind, total);
    }
    private int SkipString()
    {
        Require('"'); int units = 0;
        while (true)
        {
            int next = Take();
            if (next == '"') return units;
            if (next < 0) throw new JsonException("Unterminated retained JSON string.");
            if (next == '\\') { if (Take() == 'u') for (int i = 0; i < 4; i++) Take(); units = checked(units + 1); }
            else if ((next & 0xc0) != 0x80) units = checked(units + (next >= 0xf0 ? 2 : 1));
        }
    }
    internal IEnumerable<(string Key, Node Value)> Children(Node parent)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long position = parent.Start + 1;
        for (int index = 0; index < parent.Total; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Seek(position); White(); if (index > 0) { Require(','); White(); }
            string key = index.ToString(CultureInfo.InvariantCulture);
            if (parent.Kind == JsonValueKind.Object)
            {
                var name = new StringBuilder();
                DecodeString(c => { if (name.Length >= 1024 * 1024) throw new PixToolException(PixErrors.Codes.ResultTooLarge, "A result property name exceeds the supported pointer size. Export its parent as JSON."); name.Append(c); });
                key = name.ToString(); White(); Require(':');
            }
            Node child = Scan(); position = child.End;
            cancellationToken.ThrowIfCancellationRequested();
            yield return (key, child);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }
    internal JsonElement Element(Node node, int maxBytes, string reference, string pointer)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node.Bytes > maxBytes) throw new PixToolException(PixErrors.Codes.ResultTooLarge, "The selected result value exceeds the requested materialization budget. Read bounded windows instead.",
            nextCalls: [ResultStore.ReadCall(reference, pointer, mode: "outline"), ResultStore.ReadCall(reference, pointer)]);
        byte[] data = new byte[(int)node.Bytes];
        stream.Position = node.Start;
        int position = 0;
        while (position < data.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = stream.Read(data.AsSpan(position, Math.Min(data.Length - position, _buffer.Length)));
            cancellationToken.ThrowIfCancellationRequested();
            if (read == 0) throw new EndOfStreamException();
            position += read;
        }
        cancellationToken.ThrowIfCancellationRequested();
        using JsonDocument document = JsonDocument.Parse(data);
        JsonElement result = document.RootElement.Clone();
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
    internal ResultReadDto Read(string reference, string pointer, int offset, int limit)
        => ReadNode(Locate(pointer), reference, pointer, offset, limit);
    internal ResultReadDto ReadNode(Node node, string reference, string pointer, int offset, int limit)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int count = Math.Min(limit, Math.Max(0, node.Total - offset)); object? content;
        if (node.Kind == JsonValueKind.String)
        {
            var window = new StringBuilder(limit + 3); int index = 0;
            int first = Math.Max(0, offset - 1);
            Seek(node.Start);
            DecodeString(c => { if (index >= first && (long)index <= (long)offset + limit) window.Append(c); index++; });
            int relative = offset - first;
            if (offset > 0 && offset < node.Total && char.IsLowSurrogate(window[relative]) && char.IsHighSurrogate(window[relative - 1]))
                throw new PixToolException(PixErrors.Codes.InvalidArguments, "The string offset splits a UTF-16 surrogate pair. Use the nextOffset returned by the preceding window.");
            if (count > 0 && offset + count < node.Total && char.IsHighSurrogate(window[relative + count - 1]) && char.IsLowSurrogate(window[relative + count]))
                count = count == 1 ? 2 : count - 1;
            content = count == 0 ? "" : window.ToString(relative, count);
        }
        else if (node.Kind is JsonValueKind.Array or JsonValueKind.Object)
        {
            var array = new JsonArray(); var obj = new JsonObject(); int used = 0, emitted = 0;
            foreach (var child in Children(node).Skip(offset).Take(count))
            {
                string childPointer = pointer + "/" + Escape(child.Key);
                long bytes = child.Value.Bytes + Encoding.UTF8.GetByteCount(child.Key) + 8;
                JsonNode? item;
                if (bytes > ResultStore.TargetBytes / 2)
                {
                    item = DeferredChild(child.Value, reference, childPointer);
                    bytes = Encoding.UTF8.GetByteCount(item!.ToJsonString()) + Encoding.UTF8.GetByteCount(child.Key) + 8;
                }
                else item = JsonNode.Parse(Element(child.Value, ResultStore.TargetBytes / 2, reference, childPointer).GetRawText());
                if (emitted > 0 && used + bytes > ResultStore.TargetBytes - 4096) break;
                if (node.Kind == JsonValueKind.Array) array.Add(item); else obj[child.Key] = item;
                used += (int)bytes; emitted++;
            }
            count = emitted; content = node.Kind == JsonValueKind.Array ? array : obj;
        }
        else content = offset == 0 ? Element(node, ResultStore.TargetBytes, reference, pointer) : null;
        int? nextOffset = (long)offset + count < node.Total ? offset + count : null;
        cancellationToken.ThrowIfCancellationRequested();
        return new(reference, pointer, node.Kind.ToString().ToLowerInvariant(), node.Total, offset, count, nextOffset, content,
            nextOffset.HasValue ? [ResultStore.ReadCall(reference, pointer, nextOffset.Value, limit)] : []);
    }
    private void DecodeString(Action<char> emit)
    {
        Require('"');
        while (true)
        {
            int next = Take();
            if (next == '"') return;
            if (next < 0) throw new JsonException("Unterminated retained JSON string.");
            if (next == '\\')
            {
                next = Take();
                if (next == 'u') { int value = 0; for (int i = 0; i < 4; i++) { int hex = Take(); value = value * 16 + (hex <= '9' ? hex - '0' : (hex | 32) - 'a' + 10); } emit((char)value); }
                else emit(next switch { 'b' => '\b', 'f' => '\f', 'n' => '\n', 'r' => '\r', 't' => '\t', _ => (char)next });
            }
            else if (next < 128) emit((char)next);
            else
            {
                int extra = next >= 0xf0 ? 3 : next >= 0xe0 ? 2 : 1;
                int value = next & (extra == 3 ? 7 : extra == 2 ? 15 : 31);
                for (int i = 0; i < extra; i++) value = (value << 6) | (Take() & 63);
                if (value <= 0xffff) emit((char)value);
                else { value -= 0x10000; emit((char)(0xd800 + (value >> 10))); emit((char)(0xdc00 + (value & 1023))); }
            }
        }
    }
    internal void Copy(Node node, Stream output)
    {
        cancellationToken.ThrowIfCancellationRequested();
        stream.Position = node.Start; long remaining = node.Bytes;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = stream.Read(_buffer, 0, (int)Math.Min(remaining, _buffer.Length));
            cancellationToken.ThrowIfCancellationRequested();
            if (count == 0) throw new EndOfStreamException();
            output.Write(_buffer, 0, count); remaining -= count;
        }
        cancellationToken.ThrowIfCancellationRequested();
    }
}
