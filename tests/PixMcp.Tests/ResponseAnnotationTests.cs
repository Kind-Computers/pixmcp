using System.Text.Json;
using System.Text.Json.Nodes;
using PixMcp.Pix;
using Xunit;

namespace PixMcp.Tests;

public sealed class ResponseAnnotationTests
{
    [Fact]
    public void CostHintsFollowTheStaticTableAndNeverOverwriteExplicitCosts()
    {
        Assert.Equal("cached", CostHints.Default("pix_result_read"));
        Assert.Equal("job", CostHints.Default("pix_job_wait"));
        Assert.Equal("pixtool", CostHints.Default("pix_gpu_preview"));
        Assert.Equal("replay", CostHints.Default("pix_gpu_timing_events"));
        Assert.Equal("query", CostHints.Default("pix_gpu_events"));
        Assert.Equal("query", CostHints.Default("pix_no_such_tool"));
        Assert.Equal("replay", CostHints.For("pix_gpu_timing_events", null, "gpu-1"));

        JsonNode root = JsonNode.Parse("""
            {"items":[{"nextCalls":[{"tool":"pix_gpu_timing_tree","arguments":{"handle":"gpu-1"}}]}],
             "nextCalls":[{"tool":"pix_result_read","arguments":{"resultRef":"r-1"}},{"tool":"pix_job_wait","arguments":{"jobId":"job-1"},"cost":"query"}]}
            """)!;
        Assert.True(CostHints.Annotate(root, null));
        Assert.Equal("cached", root["nextCalls"]![0]!["cost"]!.GetValue<string>());
        Assert.Equal("query", root["nextCalls"]![1]!["cost"]!.GetValue<string>()); // explicit cost kept
        Assert.Equal("replay", root["items"]![0]!["nextCalls"]![0]!["cost"]!.GetValue<string>());
        Assert.False(CostHints.Annotate(root, null)); // idempotent
    }

    [Fact]
    public void ErrorNextCallsCarryCostHints()
    {
        var error = new PixToolException("worker_busy", "busy", true, [new("pix_job_wait", new { jobId = "job-1" }), new("pix_gpu_events", new { handle = "gpu-1" })]);
        var result = StructuredToolResults.ErrorResult(error);
        JsonElement calls = ((JsonElement)result.StructuredContent!).GetProperty("nextCalls");
        Assert.Equal("job", calls[0].GetProperty("cost").GetString());
        Assert.Equal("query", calls[1].GetProperty("cost").GetString());
    }

    [Fact]
    public void ProvenanceIsReturnedOncePerHandleThenStubbedUntilItChanges()
    {
        var fingerprints = new Dictionary<(string, string), string>();
        string? Last(string owner, string key) => fingerprints.GetValueOrDefault((owner, key));
        void Record(string owner, string key, string fingerprint) => fingerprints[(owner, key)] = fingerprint;
        static JsonNode Response(string adapter) => JsonNode.Parse("{\"items\":[],\"extra\":{\"provenance\":{\"source\":\"gpuReplay\",\"adapter\":\"" + adapter + "\"}}}")!;

        JsonNode first = Response("0");
        Assert.True(ProvenanceDedup.Apply(first, false, _ => "gpu-1", Last, Record));
        JsonObject block = first["extra"]!["provenance"]!.AsObject();
        Assert.Equal("gpuReplay", block["source"]!.GetValue<string>());
        Assert.False(block["changed"]!.GetValue<bool>());
        string fingerprint = block["fingerprint"]!.GetValue<string>();
        Assert.Equal(8, fingerprint.Length);

        JsonNode second = Response("0");
        Assert.True(ProvenanceDedup.Apply(second, false, _ => "gpu-1", Last, Record));
        JsonObject stub = second["extra"]!["provenance"]!.AsObject();
        Assert.Equal("gpu-1#" + fingerprint, stub["provenanceRef"]!.GetValue<string>());
        Assert.True(stub["unchanged"]!.GetValue<bool>());
        Assert.Null(stub["source"]);

        JsonNode forced = Response("0");
        ProvenanceDedup.Apply(forced, true, _ => "gpu-1", Last, Record);
        Assert.Equal("gpuReplay", forced["extra"]!["provenance"]!["source"]!.GetValue<string>());

        JsonNode changed = Response("1");
        ProvenanceDedup.Apply(changed, false, _ => "gpu-1", Last, Record);
        Assert.True(changed["extra"]!["provenance"]!["changed"]!.GetValue<bool>());
        Assert.NotEqual(fingerprint, changed["extra"]!["provenance"]!["fingerprint"]!.GetValue<string>());

        JsonNode otherHandle = Response("1");
        ProvenanceDedup.Apply(otherHandle, false, _ => "gpu-2", Last, Record);
        Assert.False(otherHandle["extra"]!["provenance"]!["changed"]!.GetValue<bool>()); // first sight for gpu-2

        JsonNode unknownOwner = Response("1");
        Assert.False(ProvenanceDedup.Apply(unknownOwner, false, _ => null, Last, Record));
    }

    [Fact]
    public void InputSchemasCarryExamplesForReferenceShapedParameters()
    {
        JsonElement original = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                handle = new { type = "string" },
                eventRef = new { type = "object", properties = new { handle = new { type = "string" }, queueIndex = new { type = "integer" }, eventIndex = new { type = "integer" } } },
                scope = new { type = new[] { "object", "null" }, properties = new { handle = new { type = "string" }, queueIndex = new { type = "integer" }, eventIndex = new { type = "integer" } } },
                shaderRef = new { type = "object" },
                resourceRef = new { type = "object" },
                markerPathPrefix = new { type = "string" },
                format = new { type = "string" },
                kind = new { type = "string" },
                limit = new { type = "integer" },
            },
        });
        JsonElement schema = StructuredToolResults.InputSchemaFor("pix_gpu_timing_events", original);
        JsonElement properties = schema.GetProperty("properties");
        Assert.Equal("gpu-1", properties.GetProperty("handle").GetProperty("examples")[0].GetString());
        Assert.Equal(42, properties.GetProperty("eventRef").GetProperty("examples")[0].GetProperty("eventIndex").GetInt32());
        Assert.Equal(0, properties.GetProperty("scope").GetProperty("examples")[0].GetProperty("queueIndex").GetInt32());
        Assert.Equal(0, properties.GetProperty("shaderRef").GetProperty("examples")[0].GetProperty("shaderIndex").GetInt32());
        Assert.Equal("0x1a2b", properties.GetProperty("resourceRef").GetProperty("examples")[0].GetProperty("apiObjectId").GetString());
        Assert.Equal("Frame/Shadow", properties.GetProperty("markerPathPrefix").GetProperty("examples")[0].GetString());
        Assert.Equal("table", properties.GetProperty("format").GetProperty("examples")[0].GetString());
        Assert.Equal("work", properties.GetProperty("kind").GetProperty("examples")[0].GetString());
        Assert.False(properties.GetProperty("limit").TryGetProperty("examples", out _));
        // Nested handle properties get no examples; only top-level parameters do.
        Assert.False(properties.GetProperty("eventRef").GetProperty("properties").GetProperty("handle").TryGetProperty("examples", out _));
        Assert.Equal("table", properties.GetProperty("format").GetProperty("enum")[1].GetString());
        Assert.Equal("timing-1", StructuredToolResults.InputSchemaFor("pix_timing_events", original).GetProperty("properties").GetProperty("handle").GetProperty("examples")[0].GetString());
    }

    [Fact]
    public void OutputSchemasAllowTableRowsAndProvenanceStubs()
    {
        JsonElement events = StructuredToolResults.SchemaFor("pix_gpu_events");
        Assert.True(events.GetProperty("anyOf").GetArrayLength() >= 4);
        var table = new TableDto("gpu-1", [new("queueIndex", "integer", null, "Queue")], new TableLegend("positional", new Dictionary<string, RefRecipe>(), new Dictionary<string, string>()),
            [[0]], 1, 0, 1, null, false, 0, null, []);
        OutputSchemaTests.AssertMatches(JsonSerializer.SerializeToElement(table, Json.Options), events);
        JsonElement timing = StructuredToolResults.CoreSchemaFor("pix_timing_events");
        string text = timing.GetRawText();
        Assert.Contains("provenanceRef", text);
        var stubbed = new TimingEventsDto("timing-1", new TimingRangeDto("0", "1", "0", "1"), new PageResult<RecordedTimingEventDto>(0, 0, 0, null, [], null), new TimingCapabilityDto("available"), []);
        JsonNode node = JsonSerializer.SerializeToNode(stubbed, Json.Options)!;
        node["provenance"] = new JsonObject { ["provenanceRef"] = "timing-1#abcdef01", ["unchanged"] = true };
        OutputSchemaTests.AssertMatches(JsonSerializer.SerializeToElement(node), StructuredToolResults.SchemaFor("pix_timing_events"));
    }
}
