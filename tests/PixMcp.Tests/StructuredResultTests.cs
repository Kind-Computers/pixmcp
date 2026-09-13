using System.Text.Json;
using ModelContextProtocol.Protocol;
using PixMcp.Pix;
using Xunit;

namespace PixMcp.Tests;

public class StructuredResultTests
{
    [Fact]
    public void InlineBudgetComesFromServerOptionsAndDeferredResultsOfferOutlineFirst()
    {
        using var worker = new PixWorker();
        using var session = new PixSession(worker, Microsoft.Extensions.Logging.Abstractions.NullLogger<PixSession>.Instance);
        string json = Json.Serialize(new { handle = "gpu-1", text = new string('x', 10000) });
        using (ServerOptions.Override(ServerOptions.With(inlineResultBytes: 4096)))
        {
            var result = new CallToolResult { Content = [new TextContentBlock { Text = json }] };
            StructuredToolResults.AddStructuredContent(result);
            StructuredToolResults.BoundResult(result, session, ["gpu-1"]);
            JsonElement deferred = result.StructuredContent!.Value;
            Assert.True(deferred.GetProperty("deferred").GetBoolean());
            Assert.Equal("outline", deferred.GetProperty("nextCalls")[0].GetProperty("arguments").GetProperty("mode").GetString());
            Assert.False(deferred.GetProperty("nextCalls")[1].GetProperty("arguments").TryGetProperty("mode", out _));
            Assert.Matches("^r-[0-9a-z]{10}$", deferred.GetProperty("resultRef").GetString());
        }
        using (ServerOptions.Override(ServerOptions.With(inlineResultBytes: 200000)))
        {
            var result = new CallToolResult { Content = [new TextContentBlock { Text = json }] };
            StructuredToolResults.AddStructuredContent(result);
            StructuredToolResults.BoundResult(result, session, ["gpu-1"]);
            Assert.False(result.StructuredContent!.Value.TryGetProperty("deferred", out _));
        }
    }

    [Theory]
    [InlineData("{\"handle\":\"gpu-1\",\"count\":2}", false)]
    [InlineData("[]", true)]
    [InlineData("[{\"handle\":\"gpu-1\"}]", true)]
    public void AddsMatchingStructureAndNormalizesArrayText(string json, bool array)
    {
        var text = new TextContentBlock { Text = json };
        var result = new CallToolResult { Content = [text] };
        StructuredToolResults.AddStructuredContent(result);

        if (!array) Assert.Same(text, Assert.Single(result.Content));
        JsonElement structured = result.StructuredContent!.Value;
        Assert.Equal(JsonValueKind.Object, structured.ValueKind);
        JsonElement payload = array ? structured.GetProperty("items") : structured;
        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(json), payload));
        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(((TextContentBlock)result.Content[0]).Text), structured));
    }

    [Fact]
    public void NormalizesTheSdkStructuredJsonString()
    {
        const string json = "{\"handle\":\"gpu-1\"}";
        var result = new CallToolResult
        {
            Content = [new TextContentBlock { Text = json }],
            StructuredContent = JsonSerializer.SerializeToElement(json),
        };
        StructuredToolResults.AddStructuredContent(result);
        Assert.Equal(JsonValueKind.Object, result.StructuredContent!.Value.ValueKind);
        Assert.Equal("gpu-1", result.StructuredContent.Value.GetProperty("handle").GetString());
        Assert.Equal(json, ((TextContentBlock)result.Content[0]).Text);
    }

    [Fact]
    public void PreservesImageContentAndMetadata()
    {
        var image = new ImageContentBlock { Data = new byte[] { 1, 2, 3 }, MimeType = "image/png" };
        var metadata = new System.Text.Json.Nodes.JsonObject { ["test"] = true };
        var result = new CallToolResult
        {
            Content = [new TextContentBlock { Text = "{\"path\":\"frame.png\"}" }, image],
            Meta = metadata,
        };
        StructuredToolResults.AddStructuredContent(result);

        Assert.Same(image, result.Content[1]);
        Assert.Same(metadata, result.Meta);
        Assert.Equal("frame.png", result.StructuredContent!.Value.GetProperty("path").GetString());
    }

    [Fact]
    public void StructuresErrorsAndLeavesExistingStructuredResultsUntouched()
    {
        var error = new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = "{\"error\":\"failed\"}" }] };
        StructuredToolResults.AddStructuredContent(error);
        Assert.Equal("tool_error", error.StructuredContent!.Value.GetProperty("code").GetString());
        Assert.True(error.IsError);

        JsonElement existing = JsonSerializer.SerializeToElement(new { authoritative = true });
        var result = new CallToolResult { StructuredContent = existing, Content = [new TextContentBlock { Text = "{}" }] };
        StructuredToolResults.AddStructuredContent(result);
        Assert.True(JsonElement.DeepEquals(existing, result.StructuredContent!.Value));
    }

    [Theory]
    [InlineData("plain text")]
    [InlineData("null")]
    [InlineData("\"a string\"")]
    public void LeavesNonObjectNonArrayResultsUnchanged(string text)
    {
        var result = new CallToolResult { Content = [new TextContentBlock { Text = text }] };
        StructuredToolResults.AddStructuredContent(result);
        Assert.Null(result.StructuredContent);
    }

    [Fact]
    public void EventPageSchemaDescribesStableFieldsAndAllowsOmittedNulls()
    {
        JsonElement schema = StructuredToolResults.CoreSchemaFor("pix_gpu_events");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        string[] required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Contains("total", required);
        Assert.Contains("items", required);
        Assert.DoesNotContain("nextOffset", required);
        Assert.DoesNotContain("extra", required);

        JsonElement eventSchema = schema.GetProperty("properties").GetProperty("items").GetProperty("items");
        JsonElement properties = eventSchema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("queueIndex", out _));
        Assert.True(properties.TryGetProperty("gpuId", out _));
        string[] requiredEvent = eventSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Contains("name", requiredEvent);
        Assert.DoesNotContain("gpuId", requiredEvent);
        Assert.DoesNotContain("parentIndex", requiredEvent);
    }

    [Fact]
    public void JobSchemaIncludesCancellationRequestWithoutRequiringAnUnfinishedResult()
    {
        JsonElement schema = StructuredToolResults.CoreSchemaFor("pix_job_status");
        JsonElement properties = schema.GetProperty("properties");
        Assert.Equal("boolean", properties.GetProperty("cancellationRequested").GetProperty("type").GetString());
        string[] required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Contains("jobId", required);
        Assert.DoesNotContain("result", required);
        Assert.DoesNotContain("error", required);
        Assert.DoesNotContain("finishedAt", required);
    }
}
