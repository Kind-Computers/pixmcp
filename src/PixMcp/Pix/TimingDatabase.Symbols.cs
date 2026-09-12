namespace PixMcp.Pix;

internal sealed partial class TimingDatabase
{
    private sealed record LoadedImage(long Id, uint ProcessId, ulong Address, ulong Size, long Start, long? End,
        string? Path, long? ModuleId);
    private List<LoadedImage> _loadedImages = [];
    private bool _hasSymbols, _hasSource;
    private readonly Dictionary<(long image, ulong address), TimingFunctionDto> _functions = new();
    private readonly Queue<(long image, ulong address)> _functionOrder = new();
    private readonly Dictionary<long, ulong[]?> _stackCache = new();
    private readonly Queue<long> _stackOrder = new();

    private void InitializeSymbols(uint? processId, long a, long b)
    {
        _functions.Clear(); _functionOrder.Clear(); _stackCache.Clear(); _stackOrder.Clear();
        bool images = Has("Images", "Id", "OSProcessId", "PELoadAddress", "LoadSize", "LoadTimestamp", "UnloadTimestamp", "FilePathId", "ModuleId")
            && Has("Strings", "Id", "Value");
        _hasSymbols = Has("FunctionInformation", "Id", "ModuleId", "Offset", "Size", "DecoratedNameId") && Has("SymbolStrings", "Id", "Value");
        _hasSource = _hasSymbols && Has("SourceLine", "Id", "ModuleId", "SourceFileId", "Offset", "Length", "LineStart")
            && Has("SourceFile", "Id", "SourceFileNameId");
        _loadedImages = images ? Rows("SELECT i.Id,i.OSProcessId,i.PELoadAddress,i.LoadSize,i.LoadTimestamp,i.UnloadTimestamp,s.Value,i.ModuleId FROM Images i LEFT JOIN Strings s ON s.Id=i.FilePathId WHERE ($pid IS NULL OR i.OSProcessId=$pid OR i.OSProcessId=0) AND i.LoadTimestamp < $end AND (i.UnloadTimestamp IS NULL OR i.UnloadTimestamp >= $start) ORDER BY i.LoadTimestamp DESC,i.Id", r =>
            new LoadedImage(r.GetInt64(0), checked((uint)r.GetInt64(1)), unchecked((ulong)r.GetInt64(2)), unchecked((ulong)r.GetInt64(3)),
                r.GetInt64(4), r.IsDBNull(5) ? null : r.GetInt64(5), Text(r, 6), r.IsDBNull(7) ? null : r.GetInt64(7)),
            ("$pid", processId), ("$start", a), ("$end", b)) : [];
    }

    private ulong[]? ReadStack(long stackId)
    {
        if (_stackCache.TryGetValue(stackId, out ulong[]? cached)) return cached;
        var rows = Rows("SELECT NumFrames,Addresses FROM Stacks WHERE Id=$id", r =>
            (frames: Number(r, 0), blob: r.IsDBNull(1) ? null : (byte[])r.GetValue(1)), ("$id", stackId));
        ulong[]? result = rows.Count == 1 ? DecodeStack(rows[0].frames, rows[0].blob) : null;
        _stackCache.Add(stackId, result); _stackOrder.Enqueue(stackId);
        if (_stackOrder.Count > 512) _stackCache.Remove(_stackOrder.Dequeue());
        return result;
    }


    private TimingFunctionDto ResolveFunction(uint pid, long timestamp, ulong address)
    {
        // Kernel images are associated with PID 0; user images are scoped by PID and load lifetime.
        LoadedImage? image = _loadedImages.FirstOrDefault(i => i.ProcessId == pid && Contains(i))
            ?? _loadedImages.FirstOrDefault(i => i.ProcessId == 0 && Contains(i));
        bool Contains(LoadedImage i) => i.Start <= timestamp && (!i.End.HasValue || timestamp < i.End.Value)
            && address >= i.Address && address - i.Address < i.Size;
        var cacheKey = (image?.Id ?? -(long)pid - 1, address);
        if (_functions.TryGetValue(cacheKey, out TimingFunctionDto? cached)) return cached;
        string hex = "0x" + address.ToString("X");
        ulong rva = image is null ? address : address - image.Address;
        string identity = image?.ModuleId is long moduleId ? "module-" + moduleId
            : image is not null ? "image-" + image.Id : "pid-" + pid;
        string key = identity + "/address-" + rva.ToString("X");
        string? functionName = null, functionOffset = null, sourceFile = null; int? sourceLine = null;
        if (_hasSymbols && image?.ModuleId is long module && rva <= long.MaxValue)
        {
            var matches = Rows("SELECT f.Id,f.Offset,s.Value FROM FunctionInformation f LEFT JOIN SymbolStrings s ON s.Id=f.DecoratedNameId WHERE f.ModuleId=$module AND f.Offset <= $offset AND f.Size>0 AND ($offset-f.Offset)<f.Size ORDER BY f.Offset DESC,f.Size,f.Id LIMIT 1",
                r => (id: r.GetInt64(0), offset: r.GetInt64(1), name: Text(r, 2)), ("$module", module), ("$offset", (long)rva));
            if (matches.Count == 1 && !string.IsNullOrWhiteSpace(matches[0].name))
            {
                key = identity + "/function-" + matches[0].id;
                functionName = matches[0].name; functionOffset = "0x" + matches[0].offset.ToString("X");
                if (_hasSource)
                {
                    var locations = Rows("SELECT s.Value,l.LineStart FROM SourceLine l JOIN SourceFile f ON f.Id=l.SourceFileId LEFT JOIN SymbolStrings s ON s.Id=f.SourceFileNameId WHERE l.ModuleId=$module AND l.Offset <= $offset AND l.Length>0 AND ($offset-l.Offset)<l.Length ORDER BY l.Offset DESC,l.Length,l.Id LIMIT 1",
                        r => (file: Text(r, 0), line: r.IsDBNull(1) ? (int?)null : r.GetInt32(1)), ("$module", module), ("$offset", (long)rva));
                    if (locations.Count == 1) (sourceFile, sourceLine) = locations[0];
                }
            }
        }
        var result = new TimingFunctionDto(key, hex, image?.Path, image?.ModuleId is long m ? Ns(m) : null,
            functionName, functionOffset, functionName is not null ? "resolved" : image is null ? "module_unknown" : "unresolved", sourceFile, sourceLine);
        _functions.Add(cacheKey, result);
        _functionOrder.Enqueue(cacheKey);
        if (_functionOrder.Count > 8192) _functions.Remove(_functionOrder.Dequeue());
        return result;
    }
}
