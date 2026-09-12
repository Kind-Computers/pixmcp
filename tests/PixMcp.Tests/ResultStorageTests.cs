using System.Text;
using System.Text.Json;
using PixMcp.Pix;
using Xunit;

namespace PixMcp.Tests;

public sealed class ResultStorageTests
{
    [Fact]
    public void RetainedBytesAreMeasuredAndOversizeSnapshotsSpill()
    {
        using var directory = new Temp();
        using var store = new ResultStore(32, 1024, directory.Path);
        string small = store.Store(new { n = 1 });
        string large = store.Store(new string('x', 128));
        Assert.Equal(7, store.Summary().MemoryBytes);
        Assert.Equal(130, store.Summary().DiskBytes);
        Assert.Equal(1, store.ReadElement(small).GetProperty("n").GetInt32());
        Assert.Equal(new string('x', 3), store.Read(large, offset: 125).Value);
        Assert.Equal(2, store.Summary().ResultCount);
    }

    [Fact]
    public void PressureEvictsTransientBeforeAnOlderFinishedJobAndThenWholeJobGroups()
    {
        using var directory = new Temp();
        using var store = new ResultStore(0, 200, directory.Path);
        string first = store.Store(new string('j', 40), jobId: "job-1");
        string second = store.Store(new string('k', 40), jobId: "job-1");
        store.MarkJobFinished("job-1");
        string transient = store.Store(new string('t', 40));
        var evicted = new List<string>();
        store.JobEvicted += job => { Assert.False(store.IsAvailable(first)); evicted.Add(job); };
        string replacement = store.Store(new string('r', 90));
        Assert.False(store.IsAvailable(transient));
        Assert.True(store.IsAvailable(first)); Assert.True(store.IsAvailable(second)); Assert.Empty(evicted);
        store.Store(new string('z', 130));
        Assert.False(store.IsAvailable(replacement));
        Assert.False(store.IsAvailable(first)); Assert.False(store.IsAvailable(second));
        Assert.Equal(new[] { "job-1" }, evicted);
        Assert.Equal(4, store.Summary().Evictions);
        Assert.InRange(store.Summary().DiskBytes, 0, 200);
    }

    [Fact]
    public void FinishedJobPressureUsesCompletionOrder()
    {
        using var directory = new Temp(); using var store = new ResultStore(0, 130, directory.Path);
        string older = store.Store(new string('a', 60), jobId: "older");
        string newer = store.Store(new string('b', 60), jobId: "newer");
        store.MarkJobFinished("newer"); store.MarkJobFinished("older");
        store.Store(new string('c', 20));
        Assert.True(store.IsAvailable(older)); Assert.False(store.IsAvailable(newer));
    }

    [Fact]
    public void DirectSerializationPreservesExplicitAndNewCaptureOwnersWithoutIntermediateJson()
    {
        using var directory = new Temp(); using var store = new ResultStore(0, 1024 * 1024, directory.Path);
        string reference = store.Store(new { gpuCapture = new { handle = "gpu-new", metadata = new string('x', 100000) } },
            jobId: "capture", owners: ["device-1"], operation: "gpu-capture");
        store.InvalidateOwner("gpu-new"); Assert.False(store.IsAvailable(reference));
        string another = store.Store(new { done = true }, owners: ["device-1"]);
        store.InvalidateOwner("device-1"); Assert.False(store.IsAvailable(another));
        Assert.Equal(0, store.Summary().DiskBytes);
    }

    [Fact]
    public void ClosingOwnerDuringSerializationRejectsPublicationAndReleasesStagingBytes()
    {
        using var directory = new Temp(); using var store = new ResultStore(0, 1024 * 1024, directory.Path);
        Assert.Equal("result_expired", Assert.Throws<PixToolException>(() => store.Store(new ClosingValue(store), owner: "gpu-1")).Detail.Code);
        Assert.Equal(0, store.Summary().ResultCount); Assert.Equal(0, store.Summary().DiskBytes);
    }
    private sealed class ClosingValue(ResultStore store)
    {
        public string Text { get { store.InvalidateOwner("gpu-1"); return new string('x', 100000); } }
    }

    [Fact]
    public void LeasedOrUnfinishedJobsCannotBeEvictedAndCapacityFailurePublishesNothing()
    {
        using var directory = new Temp();
        using var store = new ResultStore(0, 80, directory.Path);
        string reference = store.Store(new string('x', 50), jobId: "job-1");
        Assert.Equal("result_capacity_exceeded", Assert.Throws<PixToolException>(() => store.Store(new string('y', 50))).Detail.Code);
        Assert.Equal(52, store.Summary().DiskBytes); Assert.Equal(1, store.Summary().ResultCount);
        store.MarkJobFinished("job-1");
        using (store.Acquire(reference))
        {
            Assert.False(store.CanRemoveJob("job-1")); Assert.False(store.TryRemoveJob("job-1"));
            Assert.Equal("result_capacity_exceeded", Assert.Throws<PixToolException>(() => store.Store(new string('y', 50))).Detail.Code);
        }
        Assert.True(store.TryRemoveJob("job-1")); Assert.Equal(0, store.Summary().DiskBytes);
    }

    [Fact]
    public void ExistingReaderSurvivesOwnerCloseAndDisposalWhileNewReadersExpire()
    {
        using var directory = new Temp();
        var store = new ResultStore(0, 1024, directory.Path);
        string reference = store.Store(new[] { 1, 2, 3 }, "gpu-1", "job-1");
        using (var reader = store.EnumerateArray(reference, "").GetEnumerator())
        {
            Assert.True(reader.MoveNext()); Assert.Equal(1, reader.Current.GetInt32());
            store.InvalidateOwner("gpu-1"); store.Dispose();
            Assert.False(store.IsAvailable(reference)); Assert.False(store.CanRemoveJob("job-1"));
            Assert.Equal("result_expired", Assert.Throws<PixToolException>(() => store.Read(reference)).Detail.Code);
            Assert.True(reader.MoveNext()); Assert.Equal(2, reader.Current.GetInt32());
            Assert.True(reader.MoveNext()); Assert.Equal(3, reader.Current.GetInt32());
            Assert.True(Directory.EnumerateDirectories(System.IO.Path.Combine(directory.Path, "pixmcp-results")).Any());
        }
        Assert.Equal(0, store.Summary().ActiveLeases); Assert.Equal(0, store.Summary().DiskBytes);
        Assert.Empty(Directory.EnumerateDirectories(System.IO.Path.Combine(directory.Path, "pixmcp-results")));
    }

    [Fact]
    public void GiantEscapedStringWindowsUseBoundedMemoryAndPreserveUtf16Offsets()
    {
        using var directory = new Temp();
        using var store = new ResultStore(0, 8 * 1024 * 1024, directory.Path);
        string prefix = new('x', 2 * 1024 * 1024);
        string suffix = "\U0001f30e\\\"\n\u754c";
        string reference = store.Store(new { rows = new object[] { new { text = prefix + suffix }, new { tail = 42 } } });
        store.Read(reference, "/rows/1/tail"); // Warm the bounded reader before measuring allocations.
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        ResultReadDto read = store.Read(reference, "/rows/0/text", prefix.Length, 1);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.Equal("\U0001f30e", read.Value); Assert.Equal(2, read.Count); Assert.Equal(prefix.Length + 2, read.NextOffset);
        Assert.InRange(bytes, 0, 256 * 1024);
        Assert.Equal(prefix.Length + suffix.Length, read.Total);
        Assert.Equal("invalid_arguments", Assert.Throws<PixToolException>(() => store.Read(reference, "/rows/0/text", prefix.Length + 1)).Detail.Code);
        Assert.Equal(suffix[2..], store.Read(reference, "/rows/0/text", prefix.Length + 2, 100).Value);
        JsonElement parent = JsonSerializer.SerializeToElement(store.Read(reference, "/rows").Value, Json.Options);
        Assert.True(parent[0].GetProperty("deferred").GetBoolean());
        Assert.Equal("/rows/0", parent[0].GetProperty("pointer").GetString());
        Assert.Equal(42, store.ReadElement(reference, "/rows/1/tail").GetInt32());
    }

    [Fact]
    public void ArrayEnumerationRejectsGiantSingleRowsWithAnExactContinuation()
    {
        using var directory = new Temp(); using var store = new ResultStore(0, 1024 * 1024, directory.Path);
        string reference = store.Store(new[] { new string('x', 10000) });
        PixToolException error = Assert.Throws<PixToolException>(() => store.EnumerateArray(reference, "", 100).ToArray());
        Assert.Equal("result_too_large", error.Detail.Code);
        Assert.Contains("/0", Json.Serialize(Assert.Single(error.Detail.NextCalls)));
        Assert.Equal(0, store.Summary().ActiveLeases);
    }

    [Fact]
    public void JsonExportsAreCompleteAtomicAndRequireAnExistingParent()
    {
        using var directory = new Temp(); using var store = new ResultStore(0, 1024 * 1024, directory.Path);
        string text = new string('x', 100000) + "\U0001f30e";
        string reference = store.Store(new { nested = new { text } });
        string output = System.IO.Path.Combine(directory.Path, "result.json");
        ResultExportDto result = store.Export(reference, output, "/nested");
        using (JsonDocument json = JsonDocument.Parse(File.ReadAllBytes(output))) Assert.Equal(text, json.RootElement.GetProperty("text").GetString());
        Assert.Equal(new FileInfo(output).Length, result.Bytes);
        Assert.Equal("file_exists", Assert.Throws<PixToolException>(() => store.Export(reference, output)).Detail.Code);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => store.Export(reference, output, overwrite: true, cancellationToken: cancellation.Token));
        using (JsonDocument json = JsonDocument.Parse(File.ReadAllBytes(output))) Assert.Equal(text, json.RootElement.GetProperty("text").GetString());
        store.Export(reference, output, "/nested/text", overwrite: true);
        Assert.Equal(text, JsonSerializer.Deserialize<string>(File.ReadAllText(output)));
        string missing = System.IO.Path.Combine(directory.Path, "missing", "out.json");
        Assert.Equal("directory_not_found", Assert.Throws<PixToolException>(() => store.Export(reference, missing)).Detail.Code);
        Assert.False(Directory.Exists(System.IO.Path.GetDirectoryName(missing)));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, ".pixmcp-export-*"));
    }

    [Fact]
    public void StartupReclaimsOnlyUnlockedRecognizedSessionDirectories()
    {
        using var directory = new Temp();
        string root = System.IO.Path.Combine(directory.Path, "pixmcp-results"); Directory.CreateDirectory(root);
        string stale = Session(root); File.WriteAllText(System.IO.Path.Combine(stale, ".owner"), "pixmcp-results-v1");
        File.WriteAllText(System.IO.Path.Combine(stale, Guid.NewGuid().ToString("N") + ".json"), "{}");
        string foreign = Session(root); File.WriteAllText(System.IO.Path.Combine(foreign, ".owner"), "pixmcp-results-v1");
        File.WriteAllText(System.IO.Path.Combine(foreign, "keep.txt"), "preserve");
        using var first = new ResultStore(0, 1024, directory.Path);
        string reference = first.Store(new { value = 1 });
        using var second = new ResultStore(0, 1024, directory.Path);
        Assert.False(Directory.Exists(stale)); Assert.True(Directory.Exists(foreign));
        Assert.Equal(1, first.ReadElement(reference).GetProperty("value").GetInt32());
        static string Session(string root) { string path = System.IO.Path.Combine(root, "session-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    }

    [Fact]
    public void ParallelSessionCreationAndDisposalCannotReclaimLiveStores()
    {
        using var directory = new Temp();
        Parallel.For(0, 100, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            using var store = new ResultStore(0, 1024, directory.Path);
            string reference = store.Store(new { i });
            Assert.Equal(i, store.ReadElement(reference).GetProperty("i").GetInt32());
        });
        Assert.Empty(Directory.EnumerateDirectories(System.IO.Path.Combine(directory.Path, "pixmcp-results")));
    }

    private sealed class Temp : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pixmcp-storage-tests-" + Guid.NewGuid().ToString("N"));
        internal Temp() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            string resolved = System.IO.Path.GetFullPath(Path);
            if (!resolved.StartsWith(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            Directory.Delete(resolved, recursive: true);
        }
    }
}
