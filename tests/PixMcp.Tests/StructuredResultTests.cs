using System.Text.Json;
using ModelContextProtocol.Protocol;
using PixMcp.Pix;
using Xunit;

namespace PixMcp.Tests;

public class StructuredResultTests
{
    [Theory]
    [InlineData("{\"handle\":\"gpu-1\",\"count\":2}", false)]
    [InlineData("[]", true)]
    [InlineData("[{\"handle\":\"gpu-1\"}]", true)]
    public void AddsStructureWithoutChangingLegacyText(string json, bool array)
    {
        var text = new TextContentBlock { Text = json };
        var result = new CallToolResult { Content = [text] };
        StructuredToolResults.AddStructuredContent(result);

        Assert.Same(text, Assert.Single(result.Content));
        Assert.Equal(json, text.Text);
        JsonElement structured = result.StructuredContent!.Value;
        Assert.Equal(JsonValueKind.Object, structured.ValueKind);
        JsonElement payload = array ? structured.GetProperty("items") : structured;
        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(json), payload));
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
    public void LeavesErrorsAndExistingStructuredResultsUntouched()
    {
        var error = new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = "{\"error\":\"failed\"}" }] };
        StructuredToolResults.AddStructuredContent(error);
        Assert.Null(error.StructuredContent);
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
        JsonElement schema = StructuredToolResults.SchemaFor("pix_gpu_events");
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
        JsonElement schema = StructuredToolResults.SchemaFor("pix_job_status");
        JsonElement properties = schema.GetProperty("properties");
        Assert.Equal("boolean", properties.GetProperty("cancellationRequested").GetProperty("type").GetString());
        string[] required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Contains("jobId", required);
        Assert.DoesNotContain("result", required);
        Assert.DoesNotContain("error", required);
        Assert.DoesNotContain("finishedAt", required);
    }
}
