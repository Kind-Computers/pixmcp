using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PixMcp.Pix;
using PixMcp.Pix.Sql;
using PixMcp.Tools;
using Xunit;

namespace PixMcp.Tests;

/// <summary>
/// The real PixStorage tier (PIX_TEST_TIMING_CAPTURE with pixstorage.dll): pix_timing_schema must agree with the golden census
/// written by scripts/pixstorage_schema.py, writes are refused without touching the file, and caps return exact continuations.
/// </summary>
[Collection(GpuReplayCollection.Name)]
public sealed class TimingNativeSchemaTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("pixmcp-timing-native-schema-");

    public void Dispose() => _directory.Delete(recursive: true);

    private string CopyCapture()
    {
        string path = Path.Combine(_directory.FullName, "capture.wpix");
        File.Copy(TestArtifacts.RequireTimingCapture(), path);
        return path;
    }

    private static async Task<T> WithHandle<T>(string path, Func<PixSession, JobManager, string, Task<T>> work)
    {
        using var worker = new PixWorker();
        using var session = new PixSession(worker, NullLogger<PixSession>.Instance);
        using var jobs = new JobManager(worker, session, () => null);
        string handle = JsonSerializer.Deserialize<JsonElement>(await TimingCaptureTools.Open(session, path)).GetProperty("handle").GetString()!;
        return await work(session, jobs, handle);
    }

    [SkippableFact]
    public void SchemaMatchesTheGoldenCensusTableByTableAndColumnByColumn()
    {
        string path = CopyCapture();
        using JsonDocument golden = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pixstorage-schema-2606.18.json")));
        // The schema implementation behind pix_timing_schema, without the response budget that defers a 113-object listing.
        JsonElement schema;
        using (var db = new TimingDatabase(path, Path.Combine(PixDiscovery.InstallDir!, "pixstorage.dll")))
            schema = JsonSerializer.SerializeToElement(db.Schema("timing-native", new TimingSchemaOptions(IncludeQueries: false, Limit: 1000),
                db.SqlBindings(null, null, TimingDatabase.RangeModeFull), TimingQueryLibrary.All), Json.Options);
        Dictionary<string, JsonElement> listed = schema.GetProperty("tables").GetProperty("items").EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!);

        var problems = new List<string>();
        void Compare(string name, string kind, IEnumerable<string> expected)
        {
            if (!listed.TryGetValue(name, out JsonElement table)) { problems.Add($"missing {kind}: {name}"); return; }
            if (table.GetProperty("kind").GetString() != kind) problems.Add($"{name}: kind {table.GetProperty("kind").GetString()} != {kind}");
            IEnumerable<string> actual = table.GetProperty("columns").EnumerateArray().Select(c =>
                $"{c.GetProperty("name").GetString()} {c.GetProperty("type").GetString()} hidden={c.GetProperty("hidden").GetBoolean()}");
            if (!actual.SequenceEqual(expected)) problems.Add($"{name}: columns [{string.Join(", ", actual)}] != [{string.Join(", ", expected)}]");
        }
        foreach (JsonProperty table in golden.RootElement.GetProperty("tables").EnumerateObject())
            Compare(table.Name, "table", table.Value.GetProperty("columns").EnumerateArray().Select(c =>
                $"{c.GetProperty("name").GetString()} {c.GetProperty("type").GetString()} hidden={c.GetProperty("hidden").GetInt32() != 0}"));
        foreach (JsonProperty module in golden.RootElement.GetProperty("virtualTables").EnumerateObject())
            Compare(module.Name, "virtual", module.Value.EnumerateArray().Select(c =>
                $"{c.GetProperty("name").GetString()} {c.GetProperty("type").GetString()} hidden={c.GetProperty("hidden").GetInt32() != 0}"));
        int expectedObjects = golden.RootElement.GetProperty("tables").EnumerateObject().Count() + golden.RootElement.GetProperty("virtualTables").EnumerateObject().Count();
        problems.AddRange(listed.Keys.Where(name => !golden.RootElement.GetProperty("tables").TryGetProperty(name, out _)
            && !golden.RootElement.GetProperty("virtualTables").TryGetProperty(name, out _)).Select(name => "not in the golden census: " + name));
        Assert.True(problems.Count == 0, "PixStorage schema drift (regenerate with scripts/pixstorage_schema.py after reviewing):\n" + string.Join("\n", problems));
        Assert.Equal(expectedObjects, listed.Count);
        Assert.Equal(golden.RootElement.GetProperty("indexes").EnumerateObject().Count(), schema.GetProperty("census").GetProperty("indexes").GetInt32());
    }

    [SkippableFact]
    public async Task WritesAreRefusedAndTheCaptureStaysByteIdentical()
    {
        string path = CopyCapture();
        byte[] before = SHA256.HashData(File.ReadAllBytes(path));
        string[] codes = await WithHandle(path, async (session, jobs, handle) =>
        {
            var result = new List<string>();
            foreach (string statement in new[] { "INSERT INTO Strings(Id, Value) VALUES(999999999, 'x')", "DELETE FROM CaptureFacts", "CREATE TABLE Scratch(x)", "UPDATE Threads SET SampleCount = 0" })
                result.Add((await Assert.ThrowsAsync<PixToolException>(() => TimingSqlTools.Sql(session, jobs, handle, sql: statement, waitSeconds: 60))).Detail.Code);
            return result.ToArray();
        });
        Assert.All(codes, code => Assert.Equal("sql_forbidden", code));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
        Assert.False(File.Exists(path + "-wal"));
    }

    [SkippableFact]
    public async Task RowCapsAndTimeoutsReturnExactContinuations()
    {
        string path = CopyCapture();
        await WithHandle(path, async (session, jobs, handle) =>
        {
            const string sql = "SELECT Id, Value FROM Strings ORDER BY Id";
            JsonElement first = JsonSerializer.Deserialize<JsonElement>(await TimingSqlTools.Sql(session, jobs, handle, sql: sql, maxRows: 5, waitSeconds: 60));
            Assert.True(first.GetProperty("hasMore").GetBoolean());
            Assert.Equal("maxRows", first.GetProperty("truncationReason").GetString());
            Assert.Equal(5, first.GetProperty("nextOffset").GetInt32());
            JsonElement continuation = first.GetProperty("nextCalls").EnumerateArray().Single(c => c.GetProperty("tool").GetString() == "pix_timing_sql");
            Assert.Equal(5, continuation.GetProperty("arguments").GetProperty("offset").GetInt32());
            Assert.Equal(5, continuation.GetProperty("arguments").GetProperty("maxRows").GetInt32());
            JsonElement second = JsonSerializer.Deserialize<JsonElement>(await TimingSqlTools.Sql(session, jobs, handle, sql: sql, maxRows: 5, offset: 5, waitSeconds: 60));
            Assert.True(second.GetProperty("rows")[0][0].GetInt64() > first.GetProperty("rows")[4][0].GetInt64());

            PixToolException timeout = await Assert.ThrowsAsync<PixToolException>(() => TimingSqlTools.Sql(session, jobs, handle,
                sql: "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c) SELECT COUNT(*) FROM c", timeoutSeconds: 0.05, waitSeconds: 60));
            Assert.Equal("sql_timeout", timeout.Detail.Code);
            Assert.True(timeout.Detail.Retryable);
            return 0;
        });
    }
}
