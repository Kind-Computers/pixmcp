using System.Text.Json;
using System.Reflection;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using Xunit;

namespace PixMcp.Tests;

public sealed class OutputSchemaTests
{
    [Fact]
    public void EveryPublishedToolDescribesItsStableOuterFields()
    {
        string[] names = typeof(ServerHost).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()).Where(a => a?.Name is not null)
            .Select(a => a!.Name!).Distinct().ToArray();
        Assert.True(names.Length > 70);
        foreach (string name in names)
            Assert.True(HasShape(StructuredToolResults.CoreSchemaFor(name)), $"{name} is missing a stable output schema.");

        static bool HasShape(JsonElement schema)
            => schema.TryGetProperty("properties", out JsonElement properties) && properties.EnumerateObject().Any()
                || schema.TryGetProperty("anyOf", out JsonElement alternatives) && alternatives.EnumerateArray().All(HasShape);
    }

    [Fact]
    public void SchemasValidateNestedSuccessPendingDeferredAndErrorValues()
    {
        var reference = new EventRef("gpu-1", 0, 12);
        var evt = new EventDto(0, 12, null, null, "Draw", null, 0, null) { EventRef = reference, MarkerPath = ["Frame"] };
        var pipeline = new PipelineStateDto(reference, ["Frame"], evt, "GRAPHICS", null, null, null, null);
        Validate("pix_gpu_events", new PageResult<EventDto>(1, 0, 1, null, [evt], null));
        Validate("pix_gpu_event", new { @event = evt, parents = Array.Empty<EventDto>(), parentsTruncated = false,
            childCount = 0, children = Array.Empty<EventDto>(), childrenTruncated = false, nextCalls = Array.Empty<ToolCallDto>() });
        Validate("pix_gpu_pipeline_state", pipeline);
        Validate("pix_gpu_inspect_event", new EventInspectionDto(reference, ["Frame"], evt, null, pipeline, null, []));
        Validate("pix_gpu_shader_code", new ShaderCodeDto(new(reference, 0),
            new(new(reference, 0), 0, "0x1", "PS", null, null, null, null, null, 12, "0x0", ["HLSL"]),
            "HLSL", new(0, 0, 0, null, [], null), null, 1, 0, 0, null, null));
        Validate("pix_gpu_counters_collect", new PageResult<CounterValueRowDto>(1, 0, 1, null,
            [new(reference, 12, null, "Draw", ["Frame"], new Dictionary<string, object?> { ["3"] = 12UL })], null));
        var leaf = new TimingBranchDto(reference, 12, "Draw", null, null, 100, 100, 100, false, 0, 0, [], false, []);
        var branch = leaf with { Name = "Frame", Children = [leaf], ChildCount = 1, SelfEopNs = 0 };
        Validate("pix_gpu_timing_tree", new TimingTreeDto("gpu-1", 0, 100, 1, 0, 1, [branch], null, false, 2, false,
            new { source = "gpuReplay" }, []));
        var job = new Job("job-1", "timing", "Collect timings").ToDto();
        Validate("pix_job_status", job);
        Validate("pix_gpu_pipeline_state", new PendingDto(true, "job-1", "pix_gpu_pipeline_state", "Waiting", job,
            [new("pix_job_wait", new { jobId = "job-1" })]));
        Validate("pix_gpu_events", new DeferredResultDto(true, "result-1", 60000, [ResultStore.ReadCall("result-1")]));
        Validate("pix_gpu_events", new ErrorDto("invalid_arguments", "Invalid range", null, false, []));
        var store = new ResultStore();
        Validate("pix_result_read", store.Read(store.Store(new { nested = new[] { 1, 2 } })));
    }

    [Theory]
    [InlineData("pix_gpu_events", "{\"total\":1,\"offset\":0,\"count\":1,\"items\":[{\"queueIndex\":0,\"index\":12,\"name\":3,\"commandListId\":0}]}")]
    [InlineData("pix_job_status", "{\"jobId\":\"job-1\",\"status\":\"running\"}")]
    [InlineData("pix_result_read", "{\"resultRef\":123,\"pointer\":\"\",\"kind\":\"array\",\"total\":1,\"offset\":0,\"count\":1,\"value\":[1],\"nextCalls\":[]}")]
    public void SchemasRejectMissingRequiredAndIncorrectlyTypedNestedFields(string tool, string json)
    {
        JsonElement schema = StructuredToolResults.SchemaFor(tool);
        Assert.False(Matches(JsonSerializer.Deserialize<JsonElement>(json), schema, schema));
    }

    [Theory]
    [InlineData("pix_gpu_events")]
    [InlineData("pix_gpu_timing_tree")]
    [InlineData("pix_gpu_overview")]
    [InlineData("pix_gpu_pipeline_state")]
    [InlineData("pix_gpu_inspect_event")]
    [InlineData("pix_gpu_shader_code")]
    [InlineData("pix_gpu_resource")]
    [InlineData("pix_dump_triage")]
    [InlineData("pix_jobs")]
    [InlineData("pix_info")]
    [InlineData("pix_gpu_info")]
    public void EveryLocalSchemaReferenceResolvesAfterEnvelopeWrapping(string tool)
    {
        JsonElement root = StructuredToolResults.SchemaFor(tool);
        void Visit(JsonElement schema)
        {
            if (schema.ValueKind == JsonValueKind.Object)
            {
                if (schema.TryGetProperty("$ref", out JsonElement reference))
                {
                    string pointer = reference.GetString()!;
                    Assert.StartsWith("#", pointer);
                    Assert.NotEqual(JsonValueKind.Undefined, ResultStore.Resolve(root, pointer[1..]).ValueKind);
                }
                foreach (JsonProperty property in schema.EnumerateObject()) Visit(property.Value);
            }
            else if (schema.ValueKind == JsonValueKind.Array) foreach (JsonElement child in schema.EnumerateArray()) Visit(child);
        }
        Visit(root);
    }

    private static void Validate(string tool, object value)
    {
        JsonElement schema = StructuredToolResults.SchemaFor(tool);
        JsonElement json = JsonSerializer.SerializeToElement(value, Json.Options);
        Assert.True(Matches(json, schema, schema), $"{tool} output does not match advertised schema:\n{json}\n{schema}");
    }

    internal static void AssertMatches(JsonElement value, JsonElement schema)
        => Assert.True(Matches(value, schema, schema), $"Output does not match advertised schema:\n{value}\n{schema}");

    // Validate the structural vocabulary emitted by System.Text.Json's exporter. Formats remain
    // annotations, as in JSON Schema 2020-12. No external runtime is required by the test suite.
    private static bool Matches(JsonElement value, JsonElement schema, JsonElement root)
    {
        if (schema.ValueKind == JsonValueKind.True) return true;
        if (schema.ValueKind == JsonValueKind.False) return false;
        if (schema.TryGetProperty("$ref", out JsonElement reference) && !Matches(value, ResultStore.Resolve(root, reference.GetString()![1..]), root)) return false;
        if (schema.TryGetProperty("anyOf", out JsonElement any) && !any.EnumerateArray().Any(s => Matches(value, s, root))) return false;
        if (schema.TryGetProperty("allOf", out JsonElement all) && !all.EnumerateArray().All(s => Matches(value, s, root))) return false;
        if (schema.TryGetProperty("type", out JsonElement type))
        {
            string[] allowed = type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(t => t.GetString()!).ToArray() : [type.GetString()!];
            bool HasType(string name) => name switch
            {
                "null" => value.ValueKind == JsonValueKind.Null,
                "object" => value.ValueKind == JsonValueKind.Object,
                "array" => value.ValueKind == JsonValueKind.Array,
                "string" => value.ValueKind == JsonValueKind.String,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "number" => value.ValueKind == JsonValueKind.Number,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out decimal n) && decimal.Truncate(n) == n,
                _ => throw new InvalidOperationException("Unhandled schema type " + name),
            };
            if (!allowed.Any(HasType)) return false;
        }
        if (schema.TryGetProperty("enum", out JsonElement choices) && !choices.EnumerateArray().Any(v => JsonElement.DeepEquals(value, v))) return false;
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out JsonElement required) && required.EnumerateArray().Any(p => !value.TryGetProperty(p.GetString()!, out _))) return false;
            schema.TryGetProperty("properties", out JsonElement properties);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(property.Name, out JsonElement child))
                {
                    if (!Matches(property.Value, child, root)) return false;
                }
                else if (schema.TryGetProperty("additionalProperties", out JsonElement additional) && !Matches(property.Value, additional, root)) return false;
            }
        }
        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out JsonElement itemSchema) && !value.EnumerateArray().All(v => Matches(v, itemSchema, root))) return false;
        return true;
    }
}
