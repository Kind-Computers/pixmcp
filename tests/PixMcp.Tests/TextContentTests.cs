using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using PixMcp.Pix;
using Xunit;

namespace PixMcp.Tests;

public sealed class TextContentTests
{
    private const string Page = """{"total":120,"offset":0,"count":25,"nextOffset":25,"items":[{"a":1}],"nextCalls":[{"tool":"pix_gpu_events","arguments":{"handle":"gpu-1","offset":25}},{"tool":"pix_gpu_inspect_event","arguments":{}}]}""";

    private static JsonElement Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void TextContentModeParsesAndRejectsUnknownValues()
    {
        ServerOptions summary = ServerOptions.Parse(name => name == ServerOptions.TextContentVariable ? " Summary " : null);
        Assert.Empty(summary.Problems);
        Assert.Equal(ServerOptions.TextContentSummary, summary.TextContent);
        Assert.Equal(ServerOptions.FromEnvironment, summary.TextContentSource);
        Assert.Equal("summary", summary.Describe().TextContent!.Value);

        ServerOptions defaults = ServerOptions.Parse(_ => null);
        Assert.Equal(ServerOptions.TextContentFull, defaults.TextContent);
        Assert.Equal(ServerOptions.FromDefault, defaults.TextContentSource);

        ServerOptions bad = ServerOptions.Parse(name => name == ServerOptions.TextContentVariable ? "brief" : null);
        Assert.Equal(ServerOptions.TextContentFull, bad.TextContent);
        Assert.Contains(ServerOptions.TextContentVariable, Assert.Single(bad.Problems));
    }

    [Fact]
    public void SummaryNamesTheShapePagingPendingDeferredAndNextCalls()
    {
        string page = TextSummary.Build("pix_gpu_events", Parse(Page));
        Assert.StartsWith("pix_gpu_events: object result", page);
        Assert.Contains("total 120, offset 0, count 25, nextOffset 25", page);
        Assert.Contains("nextCalls: pix_gpu_events, pix_gpu_inspect_event", page);
        Assert.Contains("keys: total, offset, count, nextOffset, items, nextCalls.", page);
        Assert.EndsWith("Full result in structuredContent.", page);

        string pending = TextSummary.Build("pix_gpu_overview", Parse("""{"pending":true,"jobId":"job-3","tool":"pix_gpu_overview","message":"running"}"""));
        Assert.Contains("pending: pix_job_wait jobId job-3, then repeat the call", pending);
        string deferred = TextSummary.Build("pix_gpu_events", Parse("""{"deferred":true,"resultRef":"r-ABCDEFGHJK","bytes":123456,"nextCalls":[{"tool":"pix_result_read","arguments":{}}]}"""));
        Assert.Contains("deferred: read resultRef r-ABCDEFGHJK (123456 bytes) with pix_result_read", deferred);
        string partial = TextSummary.Build("pix_gpu_overview", Parse("""{"handle":"gpu-1","timing":{"pending":true,"jobId":"job-4"}}"""));
        Assert.Contains("sections pending: timing", partial);
        Assert.StartsWith("x: array result", TextSummary.Build("x", Parse("[1,2]")));
    }

    [Fact]
    public void SummaryStaysWithinTheByteBudget()
    {
        var wide = new JsonObject();
        for (int i = 0; i < 500; i++) wide["a_rather_long_property_name_number_" + i] = i;
        wide["nextCalls"] = new JsonArray(Enumerable.Range(0, 30).Select(i => (JsonNode?)new JsonObject { ["tool"] = "pix_some_quite_long_tool_name_" + i }).ToArray());
        wide["status"] = new string('é', 5000);
        JsonElement value = JsonSerializer.SerializeToElement(wide);
        foreach (string tool in new[] { "pix_gpu_overview", new string('x', 5000) })
        {
            string text = TextSummary.Build(tool, value);
            Assert.InRange(Encoding.UTF8.GetByteCount(text), 1, TextSummary.MaxBytes);
            Assert.EndsWith("Full result in structuredContent.", text);
        }
    }

    [Fact]
    public void ApplyReplacesTextButKeepsStructuredContentAndErrors()
    {
        JsonElement value = Parse(Page);
        var result = new CallToolResult { StructuredContent = value, Content = [new TextContentBlock { Text = value.GetRawText() }] };
        TextSummary.Apply(result, "pix_gpu_events");
        Assert.Equal(value.GetRawText(), result.StructuredContent!.Value.GetRawText());
        string text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.StartsWith("pix_gpu_events: object result", text);
        TextSummary.Apply(result, "pix_gpu_events");
        Assert.Equal(text, ((TextContentBlock)result.Content[0]).Text);

        CallToolResult error = StructuredToolResults.ErrorResult(PixErrors.InvalidArguments("limit must be positive."));
        string before = ((TextContentBlock)error.Content[0]).Text;
        TextSummary.Apply(error, "pix_gpu_events");
        Assert.Equal(before, ((TextContentBlock)error.Content[0]).Text);
    }
}
