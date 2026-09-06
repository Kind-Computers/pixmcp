using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PixMcp.Pix;
using PixMcp.Pix.Handles;
using PixMcp.Tools;
using Xunit;

namespace PixMcp.Tests;

public class ResourceOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResourceSummariesRunOnTheOwningWorker(bool individual)
    {
        using var worker = new PixWorker();
        using var session = new PixSession(worker, NullLogger<PixSession>.Instance);
        SnapshotHandle handle = await session.Run(() => session.Register(new SnapshotHandle(worker)));

        string json = individual
            ? await PixResources.Handle(session, handle.Id)
            : await PixResources.Handles(session);

        using JsonDocument result = JsonDocument.Parse(json);
        JsonElement summary = individual ? result.RootElement : Assert.Single(result.RootElement.EnumerateArray());
        Assert.Equal(handle.Id, summary.GetProperty("handle").GetString());
    }

    private sealed class SnapshotHandle(PixWorker worker) : PixHandle("test-resource")
    {
        public override string Kind => "test";

        public override object Summary()
        {
            Assert.True(worker.IsOnWorkerThread, "Handle state must be read on its PIX worker.");
            return new { handle = Id };
        }

        public override void Close(List<string> warnings)
            => Assert.True(worker.IsOnWorkerThread);
    }
}
