using Microsoft.Data.Sqlite;
using PixMcp.Pix;
using PixMcp.Pix.Sql;
using Xunit;

namespace PixMcp.Tests;

public sealed class DocumentGateTests : IDisposable
{
    private const string CteBomb = "WITH RECURSIVE x(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM x) SELECT count(*) FROM x";
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("pixmcp-document-gate-");
    private string Path => System.IO.Path.Combine(_directory.FullName, "document.sqlite");

    public DocumentGateTests()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE T(x INTEGER); INSERT INTO T VALUES(1);";
        command.ExecuteNonQuery();
    }

    public void Dispose() => _directory.Delete(recursive: true);

    [Fact]
    public async Task WriterInterruptsARunningReaderAndProceeds()
    {
        var gate = new DocumentGate();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        (int generation, CancellationToken invalidation) = gate.Snapshot();
        Task<SqlResultDto> reader = Task.Run(() => gate.Read(generation, CancellationToken.None, () =>
        {
            using var db = new Db(Path, invalidation);
            entered.SetResult();
            return SqlQuery.Execute(db, new SqlRequest(CteBomb) { TimeoutSeconds = 120 }, "fixture");
        }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        int written = await Task.Run(() => gate.Write(() => 42)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(42, written);
        PixToolException error = await Assert.ThrowsAsync<PixToolException>(() => reader);
        Assert.Equal("timing_query_invalidated", error.Detail.Code);
        Assert.True(error.Detail.Retryable);
        Assert.Equal(generation + 1, gate.Generation);
    }

    [Fact]
    public async Task StaleReadersFailFastAndNewReadersWaitForTheWriter()
    {
        var gate = new DocumentGate();
        (int stale, _) = gate.Snapshot();
        using var release = new ManualResetEventSlim();
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task writer = Task.Run(() => gate.Write(() => { writing.SetResult(); release.Wait(TimeSpan.FromSeconds(10)); return 0; }));
        await writing.Task.WaitAsync(TimeSpan.FromSeconds(5));

        PixToolException error = await Assert.ThrowsAsync<PixToolException>(() => Task.Run(() => gate.Read(stale, CancellationToken.None, () => 1)).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("timing_query_invalidated", error.Detail.Code);

        (int current, _) = gate.Snapshot();
        Task<int> waiting = Task.Run(() => gate.Read(current, CancellationToken.None, () => 7));
        await Task.Delay(300);
        Assert.False(waiting.IsCompleted);
        release.Set();
        await writer.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(7, await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task WriterReportsBusyWhenAReaderIgnoresInterruptionAndCloseForcesThrough()
    {
        var gate = new DocumentGate();
        using var release = new ManualResetEventSlim();
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        (int generation, _) = gate.Snapshot();
        // Managed work that never observes the invalidation token keeps the read lock.
        Task<int> reader = Task.Run(() => gate.Read(generation, CancellationToken.None, () => { reading.SetResult(); release.Wait(TimeSpan.FromSeconds(10)); return 1; }));
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            PixToolException busy = await Assert.ThrowsAsync<PixToolException>(() => Task.Run(() => gate.Write(() => 0, TimeSpan.FromMilliseconds(200))));
            Assert.Equal("timing_capture_busy", busy.Detail.Code);
            Assert.True(busy.Detail.Retryable);

            bool ran = false;
            bool exclusive = await Task.Run(() => gate.WriteOrForce(() => ran = true, TimeSpan.FromMilliseconds(200)));
            Assert.False(exclusive);
            Assert.True(ran);
        }
        finally { release.Set(); }
        Assert.Equal(1, await reader.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(gate.WriteOrForce(() => { }, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task CancelledReaderStopsWaitingBehindAWriter()
    {
        var gate = new DocumentGate();
        using var release = new ManualResetEventSlim();
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task writer = Task.Run(() => gate.Write(() => { writing.SetResult(); release.Wait(TimeSpan.FromSeconds(10)); return 0; }));
        await writing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        (int current, _) = gate.Snapshot();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(() => gate.Read(current, cancel.Token, () => 1)).WaitAsync(TimeSpan.FromSeconds(3)));
        release.Set();
        await writer.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void InvalidationEndsTheGenerationAndCancelsItsToken()
    {
        var gate = new DocumentGate();
        (int generation, CancellationToken token) = gate.Snapshot();
        gate.RequireGeneration(generation);
        Assert.Equal(generation + 1, gate.Invalidate());
        Assert.True(token.IsCancellationRequested);
        Assert.False(gate.Snapshot().Invalidation.IsCancellationRequested);
        Assert.Equal("timing_query_invalidated", Assert.Throws<PixToolException>(() => gate.RequireGeneration(generation)).Detail.Code);
    }

    private sealed class Db(string path, CancellationToken invalidation)
        : ReadOnlySqlite(path, CancellationToken.None, invalidation, 120, _ => { });
}
