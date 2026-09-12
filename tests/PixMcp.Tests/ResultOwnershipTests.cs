using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using PixMcp.Pix;
using PixMcp.Pix.Handles;
using PixMcp.Tools;
using Xunit;
using ToolHelpers = PixMcp.Tools.Tools;

namespace PixMcp.Tests;

public sealed class ResultOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryOwnerSourceUsesCanonicalIdentity(bool spilled)
    {
        using var store = new ResultStore(spilled ? 0 : 1024 * 1024, 1024 * 1024);
        store.RegisterJobOwners("job-1", [" gpu-1 ", "\tgpu-2\r\n"]);
        string[] references =
        [
            store.Store(new { value = 1 }, owner: " gpu-2 "),
            store.StoreElement(JsonSerializer.SerializeToElement(new { value = 2 }), owners: ["gpu-1", " gpu-2 "]),
            store.Store(new { value = 3 }, jobId: "job-1"),
            store.Store(new { gpuCapture = new { handle = " gpu-2 " } }, owner: "device-1", operation: "gpu-capture"),
            store.StoreElement(JsonSerializer.SerializeToElement(new { handle = " gpu-2 " }), operation: "pix_gpu_open"),
        ];
        string differentCase = store.Store(42, owner: "GPU-2");
        Assert.All(references, reference => Assert.True(store.IsAvailable(reference)));
        Assert.True(spilled ? store.Summary().DiskBytes > 0 : store.Summary().MemoryBytes > 0);

        store.InvalidateOwner("\tgpu-2 ");

        Assert.All(references, reference => AssertExpired(() => store.Read(reference)));
        Assert.True(store.IsAvailable(differentCase));
        AssertExpired(() => store.Store(new { late = true }, jobId: "job-1"));
        AssertExpired(() => store.Store(new { late = true }, owners: ["gpu-1", " gpu-2 "]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PaddedOwnerCannotPublishAfterClosingDuringSerialization(bool spilled)
    {
        using var store = new ResultStore(spilled ? 0 : 1024 * 1024, 1024 * 1024);
        AssertExpired(() => store.Store(new ClosingValue(store), owner: "\tgpu-1 "));
        Assert.Equal(0, store.Summary().ResultCount);
        Assert.Equal(0, store.Summary().MemoryBytes);
        Assert.Equal(0, store.Summary().DiskBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CanonicalClosePreservesExistingLeaseButRejectsNewReaders(bool spilled)
    {
        using var store = new ResultStore(spilled ? 0 : 1024 * 1024, 1024 * 1024);
        string reference = store.Store(new[] { 1, 2, 3 }, owner: " gpu-1 ", jobId: "job-1");
        using (var reader = store.EnumerateArray(reference, "").GetEnumerator())
        {
            Assert.True(reader.MoveNext());
            store.InvalidateOwner("gpu-1");
            AssertExpired(() => store.Read(reference));
            Assert.False(store.CanRemoveJob("job-1"));
            Assert.True(reader.MoveNext());
            Assert.Equal(2, reader.Current.GetInt32());
        }
        Assert.Equal(0, store.Summary().ActiveLeases);
        Assert.True(store.CanRemoveJob("job-1"));
        Assert.Equal(0, store.Summary().MemoryBytes);
        Assert.Equal(0, store.Summary().DiskBytes);
    }

    [Fact]
    public async Task PublicQueryAndJobResultsExpireAfterClosingPaddedHandle()
    {
        using var worker = new PixWorker();
        using var session = new PixSession(worker, NullLogger<PixSession>.Instance);
        var jobs = new JobManager(worker, session, () => null);
        FakeHandle capture = await worker.Run(() => session.Register(new FakeHandle()));
        string padded = "\t " + capture.Id + " \r\n";
        var preparation = new Preparation<FakeHandle>("ready", "ready", "Already ready", _ => true, (_, _) => throw new InvalidOperationException());
        string queryJson = await ToolHelpers.RunWhenReady(session, jobs, "pix_test", padded, preparation,
            _ => new { text = new string('x', ResultStore.TargetBytes + 1) }, 0, CancellationToken.None);
        string queryRef = JsonSerializer.Deserialize<JsonElement>(queryJson).GetProperty("resultRef").GetString()!;
        Job job = jobs.StartForHandle<FakeHandle>("test", "Padded owner", padded, (_, _) => 42);
        await job.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(JobStatus.Succeeded, job.Status);
        string jobRef = Assert.IsType<string>(job.ResultRef);

        // Exercise request-derived owners at the same transport-boundary entry point.
        string payload = Json.Serialize(new { text = new string('y', ResultStore.TargetBytes + 1) });
        var response = new CallToolResult { Content = [new TextContentBlock { Text = payload }] };
        StructuredToolResults.AddStructuredContent(response);
        StructuredToolResults.BoundResult(response, session, [padded]);
        string transportRef = response.StructuredContent!.Value.GetProperty("resultRef").GetString()!;
        Assert.Equal("xxx", JsonSerializer.Deserialize<JsonElement>(ResultTools.Read(session, queryRef, "/text", 0, 3)).GetProperty("value").GetString());
        Assert.Equal(42, JsonSerializer.Deserialize<JsonElement>(ResultTools.Read(session, jobRef)).GetProperty("value").GetInt32());

        await SessionTools.Close(session, capture.Id);

        foreach (string reference in new[] { queryRef, jobRef, transportRef })
            AssertExpired(() => ResultTools.Read(session, reference));
        Assert.Null(job.ToDto().ResultRef);
    }

    private static void AssertExpired(Action action)
        => Assert.Equal("result_expired", Assert.Throws<PixToolException>(action).Detail.Code);

    private sealed class ClosingValue(ResultStore store)
    {
        public string Text
        {
            get { store.InvalidateOwner("gpu-1"); return new string('x', 100000); }
        }
    }

    private sealed class FakeHandle() : PixHandle("fake")
    {
        public override string Kind => "fake";
        public override void Close(List<string> warnings) { }
    }
}
