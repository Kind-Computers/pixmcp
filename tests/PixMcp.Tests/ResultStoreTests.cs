using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using PixMcp.Pix;
using Xunit;
using ToolHelpers = PixMcp.Tools.Tools;

namespace PixMcp.Tests;

public sealed class ResultStoreTests
{
    [Fact]
    public void SnapshotsAreImmutableAndNestedWindowsReconstructAllValues()
    {
        var store = new ResultStore();
        var source = Enumerable.Range(0, 117).ToArray();
        string resultRef = store.Store(new { nested = new Dictionary<string, object> { ["a/b~c"] = source } });
        source[0] = 999;
        var actual = new List<int>();
        int offset = 0;
        do
        {
            ResultReadDto read = store.Read(resultRef, "/nested/a~1b~0c", offset, 13);
            JsonElement values = JsonSerializer.SerializeToElement(read.Value, Json.Options);
            actual.AddRange(values.EnumerateArray().Select(e => e.GetInt32()));
            if (!read.NextOffset.HasValue) break;
            Assert.Equal("pix_result_read", Assert.Single(read.NextCalls).Tool);
            offset = read.NextOffset.Value;
        } while (true);
        Assert.Equal(Enumerable.Range(0, 117), actual);
    }

    [Fact]
    public void LargeChildrenProvidePointersAndLateTextCanBeRead()
    {
        var store = new ResultStore();
        string code = new('x', 70000);
        code += "LATE_FUNCTION";
        string resultRef = store.Store(new { code });
        ResultReadDto root = store.Read(resultRef);
        JsonElement node = JsonSerializer.SerializeToElement(root.Value, Json.Options).GetProperty("code");
        Assert.True(node.GetProperty("deferred").GetBoolean());
        Assert.Equal("/code", node.GetProperty("pointer").GetString());
        ResultReadDto tail = store.Read(resultRef, "/code", 70000, 100);
        Assert.Equal("LATE_FUNCTION", tail.Value);
        Assert.Null(tail.NextOffset);
        Assert.True(Encoding.UTF8.GetByteCount(Json.Serialize(root)) < ResultStore.TargetBytes);
    }

    [Fact]
    public void TransientPruningDoesNotPruneJobResultsAndOwnersInvalidateBoth()
    {
        var store = new ResultStore();
        string transient = store.Store(new { n = 1 }, "gpu-1");
        string job = store.Store(new { n = 2 }, "gpu-1", "job-1");
        for (int i = 0; i < ResultStore.MaxTransientResults; i++) store.Store(new { i });
        Assert.Equal("result_expired", Assert.Throws<PixToolException>(() => store.Read(transient)).Detail.Code);
        Assert.NotNull(store.Read(job));
        store.InvalidateOwner("gpu-1");
        Assert.Equal("result_expired", Assert.Throws<PixToolException>(() => store.Read(job)).Detail.Code);
        string other = store.Store(new { }, jobId: "job-2");
        store.RemoveJob("job-2");
        Assert.Throws<PixToolException>(() => store.Read(other));
    }

    [Theory]
    [InlineData("/a/~2")]
    [InlineData("/a/~")]
    [InlineData("a")]
    [InlineData("/a/01")]
    [InlineData("/a/-1")]
    [InlineData("/missing")]
    public void InvalidPointersFailWithStableCode(string pointer)
    {
        var store = new ResultStore();
        string resultRef = store.Store(new { a = new[] { 1, 2 } });
        Assert.Equal("invalid_pointer", Assert.Throws<PixToolException>(() => store.Read(resultRef, pointer)).Detail.Code);
    }

    [Fact]
    public void OversizeResultsPreserveFullSnapshotsBeforeTransportTruncation()
    {
        using var worker = new PixWorker();
        using var session = new PixSession(worker, NullLogger<PixSession>.Instance);
        string source = new('界', 40000);
        string raw = "{\"code\":\"" + source + "\"}";
        var result = new CallToolResult { Content = [new TextContentBlock { Text = raw }] };
        StructuredToolResults.AddStructuredContent(result);
        StructuredToolResults.BoundResult(result, session, ["gpu-1"]);
        JsonElement deferred = result.StructuredContent!.Value;
        Assert.True(deferred.GetProperty("deferred").GetBoolean());
        Assert.Equal(Encoding.UTF8.GetByteCount(raw), deferred.GetProperty("totalBytes").GetInt32());
        string reference = deferred.GetProperty("resultRef").GetString()!;
        Assert.Equal(source[^10..], session.Results.Read(reference, "/code", source.Length - 10, 10).Value);
        session.Results.InvalidateOwner("gpu-1");
        Assert.Throws<PixToolException>(() => session.Results.Read(reference));
    }

    [Fact]
    public void HardLimitMeasuresUtf8Bytes()
    {
        string value = new('界', ToolHelpers.MaxResultBytes / 2);
        Assert.True(value.Length < ToolHelpers.MaxResultBytes);
        Assert.Equal("result_too_large", Assert.Throws<PixToolException>(() => ToolHelpers.EnsureByteBudget(value, "test")).Detail.Code);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(25)]
    public void StringWindowsReconstructAstralUnicodeAcrossEveryBoundary(int limit)
    {
        var store = new ResultStore();
        string source = "x🌎ab🙂🎮" + new string('x', 24) + "🎨done";
        string reference = store.Store(source);
        var text = new StringBuilder();
        int offset = 0;
        do
        {
            ResultReadDto read = store.Read(reference, offset: offset, limit: limit);
            // Go through the wire serializer: lone surrogates would otherwise be hidden by concatenation.
            string value = JsonSerializer.Deserialize<JsonElement>(Json.Serialize(read)).GetProperty("value").GetString()!;
            text.Append(value);
            if (!read.NextOffset.HasValue) break;
            offset = read.NextOffset.Value;
        } while (true);
        Assert.Equal(source, text.ToString());
        Assert.Throws<PixToolException>(() => store.Read(reference, offset: 2));
    }

    [Fact]
    public void AdditionalSnapshotsInheritJobCaptureOwnersWithoutWorkerExecutionContext()
    {
        var store = new ResultStore();
        store.RegisterJobOwners("job-1", ["gpu-1", "gpu-2"]);
        string reference = store.Store(new { complete = true }, jobId: "job-1");
        store.InvalidateOwner("gpu-2");
        Assert.Equal("result_expired", Assert.Throws<PixToolException>(() => store.Read(reference)).Detail.Code);
        Assert.Equal("result_expired", Assert.Throws<PixToolException>(() => store.Store(new { late = true }, jobId: "job-1")).Detail.Code);
    }

    [Theory]
    [InlineData("pix_gpu_open")]
    [InlineData("pix_timing_open")]
    [InlineData("pix_dump_open")]
    [InlineData("pix_device_connect")]
    public void NewlyOpenedHandleOwnsItsDeferredSummary(string operation)
    {
        using var worker = new PixWorker();
        using var session = new PixSession(worker, NullLogger<PixSession>.Instance);
        object payload = new { handle = "new-1", metadata = new string('x', ResultStore.TargetBytes + 1) };
        JsonElement deferred = JsonSerializer.Deserialize<JsonElement>(ToolHelpers.Serialize(payload, operation, session));
        string reference = deferred.GetProperty("resultRef").GetString()!;
        Assert.NotNull(session.Results.Read(reference));
        session.Results.InvalidateOwner("new-1");
        Assert.Equal("result_expired", Assert.Throws<PixToolException>(() => session.Results.Read(reference)).Detail.Code);
        Assert.Equal("result_expired", Assert.Throws<PixToolException>(() => ToolHelpers.Serialize(payload, operation, session)).Detail.Code);
    }

    [Theory]
    [InlineData("gpu-capture", "gpuCapture")]
    [InlineData("timing-capture-stop", "timingCapture")]
    public void CaptureJobResultExpiresWithItsNewCapture(string kind, string property)
    {
        var store = new ResultStore();
        var job = new Job("job-1", kind, "Capture", store, owner: "device-1");
        job.Succeed(new Dictionary<string, object> { [property] = new { handle = "new-1" } });
        string reference = job.ResultRef!;
        Assert.NotNull(store.Read(reference));
        store.InvalidateOwner("new-1");
        Assert.Equal("result_expired", Assert.Throws<PixToolException>(() => store.Read(reference)).Detail.Code);
    }

    [Fact]
    public void ArbitraryDataNamedHandleNeverCreatesSnapshotOwnership()
    {
        var store = new ResultStore();
        JsonElement payload = JsonSerializer.SerializeToElement(new { handle = "data", shader = new { handle = "nested-data" } });
        string reference = store.StoreElement(payload, operation: "pix_gpu_shader_code");
        store.InvalidateOwner("data");
        store.InvalidateOwner("nested-data");
        Assert.NotNull(store.Read(reference));
    }

    [Fact]
    public void StableErrorsSurviveSdkTextAndKeepMcpIsError()
    {
        var exception = new PixToolException("analysis_active", "Stop analysis first", true,
            [new("pix_gpu_analysis_stop", new { handle = "gpu-1" })]);
        var result = new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = exception.Message }] };
        StructuredToolResults.AddStructuredContent(result);
        Assert.True(result.IsError);
        JsonElement error = result.StructuredContent!.Value;
        Assert.Equal("analysis_active", error.GetProperty("code").GetString());
        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.Equal("gpu-1", error.GetProperty("nextCalls")[0].GetProperty("arguments").GetProperty("handle").GetString());
    }

    [Theory]
    [InlineData(unchecked((int)0x80004001), "unsupported")]
    [InlineData(unchecked((int)0x80070032), "unsupported")]
    [InlineData(unchecked((int)0x887A0004), "unsupported")]
    [InlineData(unchecked((int)0x8ABC0023), "unknown")]
    [InlineData(unchecked((int)0x8007139F), "unknown")]
    public void UnavailableStatusUsesExplicitUnsupportedEvidenceOnly(int hresult, string state)
    {
        JsonElement result = JsonSerializer.SerializeToElement(PixErrors.Unavailable("test", new System.Runtime.InteropServices.COMException("Unavailable", hresult)), Json.Options);
        Assert.Equal(state, result.GetProperty("state").GetString());
        Assert.Equal(PixErrors.Hex(hresult), result.GetProperty("error").GetProperty("hresult").GetString());
    }

    [Fact]
    public void InputEnumsAndBoundsAreAdvertisedAndRejectedBeforeExecution()
    {
        JsonElement original = JsonSerializer.SerializeToElement(new { type = "object", properties = new { codeType = new { type = "string" }, limit = new { type = "integer" } } });
        JsonElement schema = StructuredToolResults.InputSchemaFor("pix_gpu_shader_code", original).GetProperty("properties");
        Assert.Equal(3, schema.GetProperty("codeType").GetProperty("enum").GetArrayLength());
        Assert.Equal(1000, schema.GetProperty("limit").GetProperty("maximum").GetInt32());
        var arguments = new Dictionary<string, JsonElement> { ["limit"] = JsonSerializer.SerializeToElement(-1) };
        Assert.Equal("invalid_arguments", Assert.Throws<PixToolException>(() => StructuredToolResults.ValidateArguments("pix_gpu_events", arguments)).Detail.Code);
    }
}
