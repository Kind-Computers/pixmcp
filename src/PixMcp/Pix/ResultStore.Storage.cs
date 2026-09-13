namespace PixMcp.Pix;

public sealed partial class ResultStore
{
    public ResultExportDto Export(string resultRef, string outPath, string pointer = "", bool overwrite = false, CancellationToken cancellationToken = default)
    {
        string path = Tools.Tools.PrepareOutputPath(outPath, overwrite, this);
        using Lease lease = Acquire(resultRef); using Stream source = lease.Open();
        var json = new StoredJson(source, cancellationToken, resultRef);
        StoredJson.Node node = json.Locate(pointer);
        string directory = Path.GetDirectoryName(path)!;
        string temporary = Path.Combine(directory, ".pixmcp-export-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            long bytes;
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536))
            {
                json.Copy(node, output);
                output.Flush(flushToDisk: true); bytes = output.Length;
            }
            cancellationToken.ThrowIfCancellationRequested();
            try { File.Move(temporary, path, overwrite); }
            catch (IOException) when (!overwrite && File.Exists(path)) { throw PixErrors.FileExists(path); }
            return new(resultRef, pointer, path, bytes);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>Reclaims only our direct session children after obtaining their exclusive ownership lock.</summary>
    private static void ReclaimAbandoned(string root)
    {
        foreach (string candidate in Directory.EnumerateDirectories(root, "session-*").Take(64))
        {
            string name = Path.GetFileName(candidate);
            if (name.Length != 40 || !Guid.TryParseExact(name[8..], "N", out _)) continue;
            string directory = Path.GetFullPath(candidate);
            if (!string.Equals(Path.GetDirectoryName(directory), root, StringComparison.OrdinalIgnoreCase)) continue;
            string owner = Path.Combine(directory, ".owner");
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                if (!File.Exists(owner) || (File.GetAttributes(owner) & FileAttributes.ReparsePoint) != 0) continue;
                using (var ownership = new FileStream(owner, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete))
                {
                    Span<byte> signature = new byte[17];
                    if (ownership.Read(signature) != signature.Length || !signature.SequenceEqual("pixmcp-results-v1"u8)) continue;
                    string[] files = Directory.EnumerateFiles(directory).Take(257).ToArray();
                    if (files.Length > 256 || Directory.EnumerateDirectories(directory).Any() || files.Any(file =>
                        (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0 ||
                        Path.GetFileName(file) != ".owner" && (Path.GetExtension(file) != ".json" || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out _)))) continue;
                    foreach (string file in files)
                        if (file != owner && string.Equals(Path.GetDirectoryName(Path.GetFullPath(file)), directory, StringComparison.OrdinalIgnoreCase)) File.Delete(file);
                    File.Delete(owner);
                }
                Directory.Delete(directory);
            }
            catch (IOException) { /* A live session or another reclaimer owns it. */ }
            catch (UnauthorizedAccessException) { /* Leave inaccessible sessions for their owner. */ }
        }
    }

    /// <summary>Staging counts toward the byte budgets, including before publication.</summary>
    private sealed class StagingStream(ResultStore store, string? jobId) : Stream
    {
        private MemoryStream? _memory = new();
        private FileStream? _file;
        private string? _path;
        private long _length;
        private bool _committed;
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            lock (store._gate)
            {
                store.ThrowIfDisposed();
                if (_file is null && store._memoryBytes <= store._memoryLimit - buffer.Length)
                { _memory!.Write(buffer); store._memoryBytes += buffer.Length; _length += buffer.Length; return; }
                if (_file is null)
                {
                    if (!store.MakeDiskRoom(_length + buffer.Length, jobId))
                    {
                        while (store._memoryBytes > store._memoryLimit - buffer.Length)
                            if (!store.EvictOne(jobId)) throw store.Capacity();
                        _memory!.Write(buffer); store._memoryBytes += buffer.Length; _length += buffer.Length; return;
                    }
                    Directory.CreateDirectory(store._directory);
                    string path = Path.Combine(store._directory, Guid.NewGuid().ToString("N") + ".json");
                    var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536);
                    try { _memory!.Position = 0; _memory.CopyTo(file); }
                    catch { file.Dispose(); File.Delete(path); throw; }
                    _path = path; _file = file;
                    store._diskBytes += _length; store._memoryBytes -= _length;
                    _memory!.Dispose(); _memory = null;
                }
                if (!store.MakeDiskRoom(buffer.Length, jobId)) throw store.Capacity();
                _file.Write(buffer); store._diskBytes += buffer.Length; _length += buffer.Length;
            }
        }
        internal Snapshot Commit(string id, HashSet<string> owners, string? job, long sequence, ToolCallDto? origin)
        {
            _file?.Dispose(); _file = null;
            byte[]? bytes = _memory?.ToArray(); _memory?.Dispose(); _memory = null;
            _committed = true;
            return new(id, bytes, _path, _length, owners, job, sequence, origin);
        }
        internal Stream OpenRead()
        {
            _file?.Flush();
            return _memory is not null ? new MemoryStream(_memory.GetBuffer(), 0, checked((int)_memory.Length), writable: false)
                : new FileStream(_path!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536);
        }
        protected override void Dispose(bool disposing)
        {
            lock (store._gate)
            {
                _file?.Dispose(); _memory?.Dispose();
                if (!_committed)
                {
                    if (_path is null) store._memoryBytes -= _length;
                    else { File.Delete(_path); store._diskBytes -= _length; }
                    _committed = true; store.CleanDirectory();
                }
            }
            base.Dispose(disposing);
        }
        public override void Flush() => _file?.Flush();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
