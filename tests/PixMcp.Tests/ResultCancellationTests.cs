using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PixMcp.Pix;
using PixMcp.Tools;
using Xunit;

namespace PixMcp.Tests;

public sealed class ResultCancellationTests
{
    [Fact]
    public void CancellationDuringInitialScanStopsBeforeReadingTheWholeSnapshot()
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(new { text = new string('x', 192 * 1024) });
        using var cancellation = new CancellationTokenSource();
        using var stream = new CancelOnReadStream(data, cancellation);
        stream.CancelAfterReads(3);
        var json = new StoredJson(stream, cancellation.Token);

        OperationCanceledException error = Assert.ThrowsAny<OperationCanceledException>(() => json.Locate("/text"));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(3, stream.ReadCalls);
        Assert.InRange(stream.BytesRead, 1, data.Length - 1);
    }

    [Theory]
    [InlineData("string-window")]
    [InlineData("element")]
    [InlineData("copy")]
    public void CancellationDuringDecodingMaterializationAndCopyStopsFurtherReads(string operation)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(new { text = new string('x', 192 * 1024) });
        using var cancellation = new CancellationTokenSource();
        using var stream = new CancelOnReadStream(data, cancellation);
        var json = new StoredJson(stream, cancellation.Token);
        StoredJson.Node node = json.Locate("/text");
        int beforeReads = stream.ReadCalls;
        long beforeBytes = stream.BytesRead;
        stream.CancelAfterReads(3);
        using var output = new MemoryStream();

        OperationCanceledException error = Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            switch (operation)
            {
                case "string-window": json.ReadNode(node, "result-1", "/text", 0, 25); break;
                case "element": json.Element(node, data.Length, "result-1", "/text"); break;
                case "copy": json.Copy(node, output); break;
                default: throw new InvalidOperationException();
            }
        });

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(3, stream.ReadCalls - beforeReads);
        Assert.InRange(stream.BytesRead - beforeBytes, 1, node.Bytes - 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellingBufferedEnumerationReleasesItsLeaseAndAnyInvalidatedStorage(bool invalidateOwner)
    {
        using var store = new ResultStore(0, 1024 * 1024);
        string reference = store.Store(new[] { 1, 2, 3 }, owner: "fake-1", jobId: "job-1");
        store.MarkJobFinished("job-1");
        using var cancellation = new CancellationTokenSource();
        using var reader = store.EnumerateArray(reference, "", cancellationToken: cancellation.Token).GetEnumerator();
        Assert.True(reader.MoveNext());
        Assert.Equal(1, reader.Current.GetInt32());
        Assert.Equal(1, store.Summary().ActiveLeases);
        Assert.False(store.CanRemoveJob("job-1"));
        if (invalidateOwner) store.InvalidateOwner("fake-1");
        Assert.True(store.Summary().DiskBytes > 0);

        cancellation.Cancel();
        OperationCanceledException error = Assert.ThrowsAny<OperationCanceledException>(() => reader.MoveNext());

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, store.Summary().ActiveLeases);
        Assert.True(store.CanRemoveJob("job-1"));
        if (invalidateOwner)
        {
            Assert.False(store.IsAvailable(reference));
            Assert.Equal(0, store.Summary().DiskBytes);
        }
        else
        {
            Assert.True(store.IsAvailable(reference));
            Assert.Equal(3, store.ReadElement(reference).GetArrayLength());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrecancelledStoreReadsAcquireNoLeaseAndLeaveSnapshotsUsable(bool spilled)
    {
        using var store = new ResultStore(spilled ? 0 : 1024, 1024);
        string reference = store.Store(new { values = new[] { 1, 2, 3 } });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Equal(cancellation.Token, Assert.ThrowsAny<OperationCanceledException>(() =>
            store.Read(reference, cancellationToken: cancellation.Token)).CancellationToken);
        Assert.Equal(cancellation.Token, Assert.ThrowsAny<OperationCanceledException>(() =>
            store.ReadElement(reference, cancellationToken: cancellation.Token)).CancellationToken);
        Assert.Equal(cancellation.Token, Assert.ThrowsAny<OperationCanceledException>(() =>
            store.EnumerateArray(reference, "/values", cancellationToken: cancellation.Token).ToArray()).CancellationToken);

        Assert.Equal(0, store.Summary().ActiveLeases);
        Assert.True(store.IsAvailable(reference));
        Assert.Equal(1, store.ReadElement(reference, "/values/0").GetInt32());
    }

    [Fact]
    public async Task CancellingPublicResultReadPreservesItsCompletedJob()
    {
        using var worker = new PixWorker();
        using var session = new PixSession(worker, NullLogger<PixSession>.Instance);
        var jobs = new JobManager(worker, session, () => null);
        Job job = jobs.Start("test", "managed result", _ => new { answer = 42 });
        await job.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        string reference = job.ResultRef!;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        OperationCanceledException error = Assert.ThrowsAny<OperationCanceledException>(() =>
            ResultTools.Read(session, reference, cancellationToken: cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.False(job.CancellationRequested);
        Assert.Equal(reference, job.ResultRef);
        Assert.Equal(0, session.Results.Summary().ActiveLeases);
        using JsonDocument retry = JsonDocument.Parse(ResultTools.Read(session, reference, "/answer"));
        Assert.Equal(42, retry.RootElement.GetProperty("value").GetInt32());
    }

    [Fact]
    public void CancellingComparisonReadPreservesItsDetachedResult()
    {
        using var store = new ResultStore(0, 1024 * 1024);
        string reference = store.Store(new { items = new[] { new
        {
            baseline = new EventRef("gpu-a", 0, 1), candidate = new EventRef("gpu-b", 0, 2),
            deltaNs = 10, deltaPercent = 5,
        } } });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        OperationCanceledException error = Assert.ThrowsAny<OperationCanceledException>(() =>
            ComparisonResultQuery.Read(store, reference, cancellationToken: cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, store.Summary().ActiveLeases);
        Assert.True(store.IsAvailable(reference));
        Assert.Single(ComparisonResultQuery.Read(store, reference).Items);
    }

    private sealed class CancelOnReadStream(byte[] data, CancellationTokenSource cancellation) : Stream
    {
        private readonly MemoryStream _inner = new(data, writable: false);
        private int _cancelAt = int.MaxValue;
        public int ReadCalls { get; private set; }
        public long BytesRead { get; private set; }
        public void CancelAfterReads(int count) => _cancelAt = ReadCalls + count;
        public override int Read(Span<byte> buffer)
        {
            int read = _inner.Read(buffer[..Math.Min(buffer.Length, 4096)]);
            ReadCalls++; BytesRead += read;
            if (ReadCalls == _cancelAt) cancellation.Cancel();
            return read;
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}
