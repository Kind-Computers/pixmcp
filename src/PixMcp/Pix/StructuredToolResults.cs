using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace PixMcp.Pix;

/// <summary>Adds MCP structure at the transport boundary while keeping legacy JSON text intact.</summary>
internal static class StructuredToolResults
{
    private static readonly JsonElement ObjectSchema = JsonSerializer.SerializeToElement(new { type = "object" });
    private static readonly JsonElement EventSchema = Export<EventDto>("gpuId", "parentIndex", "apiCallData", "color");
    private static readonly JsonElement JobSchema = Export<JobDto>("startedAt", "finishedAt", "elapsedSeconds", "error", "result");
    private static readonly JsonElement PageSchema = Export<PageResult<object>>("nextOffset", "extra");
    private static readonly JsonElement EventPageSchema = CreateEventPageSchema();
    private static readonly JsonElement ArraySchema = ArrayEnvelope(new JsonObject());
    private static readonly JsonElement JobsSchema = ArrayEnvelope(JsonNode.Parse(JobSchema.GetRawText())!);
    private static readonly JsonElement PendingSchema = CreatePendingSchema();
    /// <summary>Tools that may answer with a page, or with a pending marker while their prerequisite job runs.</summary>
    private static readonly JsonElement PageOrPendingSchema = AnyOf(PageSchema, PendingSchema);
    private static readonly JsonElement ObjectOrPendingSchema = AnyOf(ObjectSchema, PendingSchema);
    private static readonly JsonElement ArrayOrPendingSchema = AnyOf(ArraySchema, PendingSchema);

    public static void Configure(IMcpRequestFilterBuilder filters)
    {
        filters.AddCallToolFilter(next => async (request, ct) =>
        {
            CallToolResult result = await next(request, ct).ConfigureAwait(false);
            AddStructuredContent(result);
            return result;
        });
        filters.AddListToolsFilter(next => async (request, ct) =>
        {
            ListToolsResult result = await next(request, ct).ConfigureAwait(false);
            foreach (Tool tool in result.Tools)
            {
                // The SDK can share tool metadata across requests. Publish each immutable schema once.
                lock (tool) tool.OutputSchema ??= SchemaFor(tool.Name);
            }
            return result;
        });
    }

    internal static void AddStructuredContent(CallToolResult result)
    {
        if (result.IsError == true) return;
        TextContentBlock[] text = result.Content.OfType<TextContentBlock>().Take(2).ToArray();
        if (text.Length != 1) return;
        // The SDK can populate structuredContent with the method's raw string return value.
        // Normalize that duplicate JSON string, but preserve independently supplied structure.
        if (result.StructuredContent is JsonElement existing &&
            (existing.ValueKind != JsonValueKind.String || existing.GetString() != text[0].Text)) return;
        try
        {
            using JsonDocument document = JsonDocument.Parse(text[0].Text);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                result.StructuredContent = document.RootElement.Clone();
            else if (document.RootElement.ValueKind == JsonValueKind.Array)
                result.StructuredContent = JsonSerializer.SerializeToElement(new { items = document.RootElement });
        }
        catch (JsonException)
        {
            // Leave non-JSON content unchanged; do not turn diagnostics into tool failures.
        }
    }

    internal static JsonElement SchemaFor(string toolName) => toolName switch
    {
        "pix_gpu_analysis_start" or "pix_gpu_timing_collect" or "pix_gpu_counters_start"
            or "pix_gpu_drpix_run" or "pix_timing_resolve_symbols" or "pix_device_take_gpu_capture"
            or "pix_device_timing_capture_stop" or "pix_capture_upgrade" or "pix_job_status" or "pix_job_wait" or "pix_job_cancel" => JobSchema,
        "pix_jobs" => JobsSchema,
        "pix_gpu_events" => EventPageSchema,
        "pix_gpu_api_objects" or "pix_gpu_resources" or "pix_device_processes" or "pix_device_counters"
            or "pix_dump_events" or "pix_dump_resources" or "pix_dump_shader_waves" or "pix_dump_journal" or "pix_dump_page_faults" => PageSchema,
        "pix_gpu_timing_events" or "pix_gpu_counters_collect" or "pix_gpu_counters_list" => PageOrPendingSchema,
        "pix_gpu_pipeline_state" or "pix_gpu_shader_code" or "pix_gpu_event_resources" or "pix_gpu_timing_tree"
            or "pix_gpu_occupancy" or "pix_gpu_hf_counters" => ObjectOrPendingSchema,
        "pix_gpu_drpix_experiments" => ArrayOrPendingSchema,
        "pix_handles" or "pix_close_all" or "pix_log" or "pix_gpu_queues" or "pix_dump_queues" => ArraySchema,
        _ => ObjectSchema,
    };

    /// <summary>True when the tool reports a still-running prerequisite job instead of its data (see <see cref="PendingDto"/>).</summary>
    internal static bool IsPending(JsonElement structured)
        => structured.ValueKind == JsonValueKind.Object && structured.TryGetProperty("pending", out JsonElement pending) && pending.ValueKind == JsonValueKind.True;

    private static JsonElement CreatePendingSchema()
    {
        JsonElement schema = Export<PendingDto>();
        JsonNode node = JsonNode.Parse(schema.GetRawText())!;
        node["properties"]!["job"] = JsonNode.Parse(JobSchema.GetRawText());
        return JsonSerializer.SerializeToElement(node);
    }

    private static JsonElement AnyOf(params JsonElement[] alternatives) => JsonSerializer.SerializeToElement(new JsonObject
    {
        ["type"] = "object",
        ["anyOf"] = new JsonArray(alternatives.Select(a => JsonNode.Parse(a.GetRawText())).ToArray()),
    });

    private static JsonElement Export<T>(params string[] optionalProperties)
    {
        var options = new JsonSerializerOptions(Json.Options) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        JsonNode schema = options.GetJsonSchemaAsNode(typeof(T), new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
        });
        schema["type"] = "object";
        // Null properties are omitted by our serializer, including nullable constructor parameters.
        if (schema["required"] is JsonArray required)
        {
            foreach (string name in optionalProperties)
            {
                for (int i = required.Count - 1; i >= 0; i--)
                    if (required[i]?.GetValue<string>() == name) required.RemoveAt(i);
            }
        }
        return JsonSerializer.SerializeToElement(schema);
    }

    private static JsonElement CreateEventPageSchema()
    {
        JsonNode schema = JsonNode.Parse(PageSchema.GetRawText())!;
        schema["properties"]!["items"]!["items"] = JsonNode.Parse(EventSchema.GetRawText());
        return JsonSerializer.SerializeToElement(schema);
    }

    private static JsonElement ArrayEnvelope(JsonNode itemSchema) => JsonSerializer.SerializeToElement(new JsonObject
    {
        ["type"] = "object",
        ["required"] = new JsonArray("items"),
        ["properties"] = new JsonObject
        {
            ["items"] = new JsonObject { ["type"] = "array", ["items"] = itemSchema },
        },
    });
}
