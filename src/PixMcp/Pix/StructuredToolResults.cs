using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace PixMcp.Pix;

/// <summary>Adds typed schemas and matching structured/text JSON, continuations, and structured errors at the transport boundary.</summary>
internal static class StructuredToolResults
{
    private static readonly JsonElement ObjectSchema = JsonSerializer.SerializeToElement(new { type = "object" });
    private static readonly JsonElement EventSchema = Export<EventDto>("gpuId", "parentIndex", "apiCallData", "color");
    private static readonly JsonElement JobSchema = Export<JobDto>("startedAt", "finishedAt", "elapsedSeconds", "error", "result");
    private static readonly JsonElement ErrorSchema = Export<ErrorDto>();
    private static readonly JsonElement DeferredSchema = Export<DeferredResultDto>();
    private static readonly JsonElement ResultReadSchema = Export<ResultReadDto>();
    private static readonly JsonElement PageSchema = Export<PageResult<object>>("nextOffset", "extra");
    private static readonly JsonElement EventPageSchema = CreateEventPageSchema();
    private static readonly JsonElement JobsSchema = ArrayEnvelope(JsonNode.Parse(JobSchema.GetRawText())!);
    private static readonly JsonElement PendingSchema = CreatePendingSchema();
    private static readonly AsyncLocal<CallToolRequestParams?> CurrentRequest = new();

    internal static object? CurrentArguments() => CurrentRequest.Value?.Arguments;
    internal static string[] CurrentOwners() => Owners(CurrentRequest.Value?.Arguments).ToArray();
    private static IEnumerable<string> Owners(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null) yield break;
        foreach ((string key, JsonElement value) in arguments)
        {
            if ((key == "handle" || key.EndsWith("Handle", StringComparison.Ordinal)) && value.ValueKind == JsonValueKind.String)
                yield return value.GetString()!;
            else if (value.ValueKind == JsonValueKind.Object)
                foreach (string owner in Owners(value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value))) yield return owner;
        }
    }

    public static void Configure(IMcpRequestFilterBuilder filters)
    {
        filters.AddCallToolFilter(next => async (request, ct) =>
        {
            CallToolRequestParams? previous = CurrentRequest.Value;
            CurrentRequest.Value = request.Params;
            try
            {
                try
                {
                    ValidateArguments(request.Params?.Name ?? "", request.Params?.Arguments);
                    CallToolResult result = await next(request, ct).ConfigureAwait(false);
                    AddStructuredContent(result);
                    BoundResult(result, request.Services?.GetService<PixSession>(), CurrentOwners(), request.Params?.Name == "pix_result_read", request.Params?.Name);
                    return result;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    return ErrorResult(ex);
                }
            }
            finally { CurrentRequest.Value = previous; }
        });
        filters.AddListToolsFilter(next => async (request, ct) =>
        {
            ListToolsResult result = await next(request, ct).ConfigureAwait(false);
            foreach (Tool tool in result.Tools)
            {
                // The SDK can share tool metadata across requests. Replace it atomically with immutable JSON.
                lock (tool)
                {
                    tool.OutputSchema = SchemaFor(tool.Name);
                    tool.InputSchema = InputSchemaFor(tool.Name, tool.InputSchema);
                }
            }
            return result;
        });
    }

    internal static void AddStructuredContent(CallToolResult result)
    {
        TextContentBlock[] text = result.Content.OfType<TextContentBlock>().Take(2).ToArray();
        if (text.Length != 1) return;
        if (result.IsError == true)
        {
            JsonElement error;
            try
            {
                error = JsonSerializer.Deserialize<JsonElement>(text[0].Text);
                if (error.ValueKind != JsonValueKind.Object || !error.TryGetProperty("code", out _))
                    error = JsonSerializer.SerializeToElement(new ErrorDto("tool_error", text[0].Text, null, false, []), Json.Options);
            }
            catch (JsonException) { error = JsonSerializer.SerializeToElement(new ErrorDto("tool_error", text[0].Text, null, false, []), Json.Options); }
            SetPayload(result, error);
            return;
        }
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
                SetPayload(result, JsonSerializer.SerializeToElement(new { items = document.RootElement }));
        }
        catch (JsonException)
        {
            // Leave non-JSON content unchanged; do not turn diagnostics into tool failures.
        }
    }

    internal static CallToolResult ErrorResult(Exception exception)
    {
        ErrorDto error = PixErrors.ToDto(exception);
        string json = Json.Serialize(error);
        if (Encoding.UTF8.GetByteCount(json) > Math.Min(ResultStore.TargetBytes, Tools.Tools.MaxResultBytes))
        {
            // A broken/very small output budget must still produce a protocol-level tool error.
            // Do not re-enter snapshotting or the size guard while reporting their own failure.
            error = error with { Message = "Tool failed; diagnostic details exceed the response budget.", NextCalls = [] };
            json = Json.Serialize(error);
        }
        return new() { IsError = true, StructuredContent = JsonSerializer.SerializeToElement(error, Json.Options),
            Content = [new TextContentBlock { Text = json }] };
    }

    private static void SetPayload(CallToolResult result, JsonElement value)
    {
        result.StructuredContent = value.Clone();
        result.Content = result.Content.Select(c => c is TextContentBlock ? new TextContentBlock { Text = value.GetRawText() } : c).ToList();
    }

    internal static void BoundResult(CallToolResult result, PixSession? session, string[] owners, bool reading = false, string? operation = null)
    {
        if (result.StructuredContent is not JsonElement value) return;
        string json = value.GetRawText();
        int bytes = Encoding.UTF8.GetByteCount(json);
        if (session is not null && !reading && bytes > Math.Min(ResultStore.TargetBytes, Tools.Tools.MaxResultBytes))
        {
            string resultRef = session.Results.StoreElement(value, owners, operation: operation);
            SetPayload(result, JsonSerializer.SerializeToElement(new DeferredResultDto(true, resultRef, bytes, [ResultStore.ReadCall(resultRef)]), Json.Options));
        }
        Tools.Tools.EnsureByteBudget(result.StructuredContent!.Value.GetRawText(), "tool result");
    }

    internal static JsonElement SchemaFor(string toolName)
    {
        JsonElement core = CoreSchemaFor(toolName);
        return AnyOf(core, DeferredSchema, ErrorSchema);
    }

    internal static JsonElement CoreSchemaFor(string toolName) => LegacyResultSchemas.For(toolName) ?? (toolName switch
    {
        "pix_gpu_analysis_start" or "pix_gpu_timing_collect" or "pix_gpu_counters_start"
            or "pix_gpu_drpix_run" or "pix_timing_resolve_symbols" or "pix_device_take_gpu_capture"
            or "pix_device_timing_capture_stop" or "pix_capture_upgrade" or "pix_gpu_shader_profile" or "pix_gpu_compare" or "pix_gpu_preview"
            or "pix_gpu_export_cpp" or "pix_csv_compare" or "pix_job_status" or "pix_job_wait" or "pix_job_cancel" => JobSchema,
        "pix_result_read" => ResultReadSchema,
        "pix_result_export" => Export<ResultExportDto>(),
        "pix_gpu_compare_changes" => Export<ComparisonChangesDto>(),
        "pix_csv_pass_candidates" => Export<CsvPassCandidatesDto>(),
        "pix_timing_overview" => AnyOf(Export<TimingOverviewDto>(), PendingSchema),
        "pix_timing_events" => AnyOf(Export<TimingEventsDto>(), PendingSchema),
        "pix_timing_submissions" => AnyOf(Export<TimingSubmissionsDto>(), PendingSchema),
        "pix_timing_thread_switches" => AnyOf(Export<TimingThreadSwitchesDto>(), PendingSchema),
        "pix_timing_counters_list" => AnyOf(Export<TimingCountersDto>(), PendingSchema),
        "pix_timing_counters_read" => AnyOf(Export<TimingCounterSamplesDto>(), PendingSchema),
        "pix_timing_hotspots" => AnyOf(Export<TimingHotspotsDto>(), PendingSchema),
        "pix_timing_calltree" => AnyOf(Export<TimingCalltreeDto>(), PendingSchema),
        "pix_jobs" => JobsSchema,
        "pix_gpu_events" => EventPageSchema,
        "pix_gpu_resources" => Export<PageResult<ResourceSummaryDto>>(),
        "pix_gpu_resource" => Export<ResourceDetailsDto>(),
        "pix_gpu_counters_collect" => AnyOf(Export<PageResult<CounterValueRowDto>>(), PendingSchema),
        "pix_gpu_timing_tree" => AnyOf(Export<TimingTreeDto>(), PendingSchema),
        "pix_gpu_pipeline_state" => AnyOf(Export<PipelineStateDto>(), PendingSchema),
        "pix_gpu_inspect_event" => AnyOf(Export<EventInspectionDto>(), PendingSchema),
        "pix_gpu_shader_code" => AnyOf(Export<ShaderCodeDto>(), PendingSchema),
        "pix_gpu_shader_diagnostics" => AnyOf(Export<ShaderDiagnosticsDto>(), PendingSchema),
        "pix_gpu_shader_search" => AnyOf(Export<ShaderSearchDto>(), PendingSchema),
        "pix_gpu_shaders" => AnyOf(Export<ShaderInventoryDto>(), PendingSchema),
        "pix_gpu_shader_uses" => AnyOf(Export<ShaderUsesDto>(), PendingSchema),
        "pix_gpu_event_resources" => AnyOf(Export<EventResourcesDto>(), PendingSchema),
        "pix_gpu_resource_uses" => AnyOf(Export<ResourceUsesDto>(), PendingSchema),
        "pix_gpu_overview" => AnyOf(Export<CaptureOverviewDto>(), PendingSchema),
        "pix_gpu_timing_events" => AnyOf(Export<PageResult<TimingEventDto>>(), PendingSchema),
        "pix_gpu_counters_list" => AnyOf(Export<PageResult<LegacyResultSchemas.CounterMetadata>>(), PendingSchema),
        "pix_dump_event" => Export<Tools.DumpEventResultDto>(),
        "pix_dump_triage" => Export<Tools.DumpTriageDto>(),
        "pix_gpu_preview_image" => Export<PreviewImageSchemaDto>(),
        "pix_gpu_preview_bytes" => Export<PreviewBytesSchemaDto>(),
        "pix_gpu_occupancy" => AnyOf(Export<LegacyResultSchemas.Occupancy>(), Export<LegacyResultSchemas.UnavailableResult>(), PendingSchema),
        "pix_gpu_hf_counters" => AnyOf(Export<LegacyResultSchemas.HighFrequency>(), Export<LegacyResultSchemas.UnavailableResult>(), PendingSchema),
        "pix_gpu_drpix_experiments" => AnyOf(ArrayEnvelope(JsonNode.Parse(Export<Handles.ExperimentInfo>().GetRawText())!), PendingSchema),
        _ => ObjectSchema,
    });

    /// <summary>True when the tool reports a still-running prerequisite job instead of its data (see <see cref="PendingDto"/>).</summary>
    internal static bool IsPending(JsonElement structured)
        => structured.ValueKind == JsonValueKind.Object && structured.TryGetProperty("pending", out JsonElement pending) && pending.ValueKind == JsonValueKind.True;

    private static JsonElement CreatePendingSchema()
        => Export<PendingDto>();

    internal static JsonElement AnyOf(params JsonElement[] alternatives) => JsonSerializer.SerializeToElement(new JsonObject
    {
        ["type"] = "object",
        ["anyOf"] = new JsonArray(alternatives.Select((a, i) => Rebase(JsonNode.Parse(a.GetRawText())!, $"#/anyOf/{i}")).ToArray()),
    });

    private static JsonNode Rebase(JsonNode node, string prefix)
    {
        void Visit(JsonNode? child)
        {
            if (child is JsonObject obj)
            {
                if (obj["$ref"] is JsonValue reference && reference.TryGetValue<string>(out string? path) && path.StartsWith('#'))
                    obj["$ref"] = prefix + path[1..];
                foreach (JsonNode? nested in obj.Select(p => p.Value).ToArray()) Visit(nested);
            }
            else if (child is JsonArray array) foreach (JsonNode? nested in array) Visit(nested);
        }
        Visit(node);
        return node;
    }

    internal static JsonElement Export<T>(params string[] optionalProperties)
    {
        var options = new JsonSerializerOptions(Json.Options) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        JsonNode schema = options.GetJsonSchemaAsNode(typeof(T), new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = (context, node) =>
            {
                if (node is JsonObject obj && obj["required"] is JsonArray required && context.TypeInfo.Kind == JsonTypeInfoKind.Object)
                    foreach (JsonPropertyInfo property in context.TypeInfo.Properties.Where(p => p.IsGetNullable))
                        for (int i = required.Count - 1; i >= 0; i--)
                            if (required[i]?.GetValue<string>() == property.Name) required.RemoveAt(i);
                return node;
            },
        });
        schema["type"] = "object";
        RemoveNullableRequirements(schema);
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

    private static void RemoveNullableRequirements(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj["properties"] is JsonObject properties && obj["required"] is JsonArray required)
                for (int i = required.Count - 1; i >= 0; i--)
                {
                    string name = required[i]!.GetValue<string>();
                    JsonNode? type = properties[name] is JsonObject property ? property["type"] : null;
                    if (type is JsonArray alternatives && alternatives.Any(t => t?.GetValue<string>() == "null")) required.RemoveAt(i);
                }
            foreach (JsonNode? child in obj.Select(p => p.Value).ToArray()) RemoveNullableRequirements(child);
        }
        else if (node is JsonArray array) foreach (JsonNode? child in array) RemoveNullableRequirements(child);
    }

    private static string[]? Choices(string tool, string name) => (tool, name) switch
    {
        (_, "codeType") => ["HLSL", "IL", "ISA"],
        ("pix_gpu_resources", "type") => ["COMMITTED", "PLACED", "RESERVED"],
        ("pix_gpu_resources", "dimension") => ["BUFFER", "TEXTURE1D", "TEXTURE2D", "TEXTURE3D"],
        ("pix_gpu_api_objects", "type") => ["HEAP", "RESOURCE", "COMMAND_QUEUE", "COMMAND_ALLOCATOR"],
        ("pix_device_d3d_settings_set", "category") => ["debugLayer", "dred", "device"],
        ("pix_csv_compare", "stat") => ["mean", "median", "p95"],
        ("pix_gpu_timing_events", "sortBy") => ["eopDuration", "topDuration", "eopStart", "index"],
        (_, "kind") when tool.StartsWith("pix_gpu_") => ["draw", "dispatch", "drawOrDispatch", "executeIndirect", "copy", "clear", "barrier", "present", "marker"],
        _ => null,
    };

    private static (double? min, double? max) Bounds(string tool, string name) => (tool, name) switch
    {
        ("pix_gpu_preview_bytes", "limit") => (1, 16384),
        ("pix_gpu_preview" or "pix_gpu_export_cpp" or "pix_csv_compare", "timeoutSeconds") => (1, 3600),
        ("pix_gpu_shader_search", "nodeIndex") => (0, null),
        ("pix_gpu_shader_search", "contextLines") => (0, 20),
        (_, "startLine") => (1, null),
        (_, "offset" or "queueIndex" or "shaderIndex" or "eventIndex" or "setIndex"
            or "pointOffset" or "sampleOffset" or "viewOffset" or "bindingOffset" or "nodeOffset") => (0, null),
        (_, "limit" or "lineCount" or "maxNodes" or "viewLimit" or "bindingLimit" or "nodeLimit") => (1, 1000),
        (_, "nodeIndex") => (-1, null),
        (_, "waitSeconds" or "timeoutSeconds") => (0, 3600),
        _ => (null, null),
    };

    internal static JsonElement InputSchemaFor(string tool, JsonElement original)
    {
        JsonNode schema = JsonNode.Parse(original.GetRawText())!;
        Apply(schema);
        return JsonSerializer.SerializeToElement(schema);
        void Apply(JsonNode? node)
        {
            if (node is JsonObject obj && obj["properties"] is JsonObject properties)
                foreach ((string name, JsonNode? property) in properties)
                {
                    if (property is not JsonObject p) continue;
                    if (Choices(tool, name) is string[] values) p["enum"] = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
                    (double? min, double? max) = Bounds(tool, name);
                    if (min.HasValue) p["minimum"] = min.Value;
                    if (max.HasValue) p["maximum"] = max.Value;
                    Apply(p);
                }
        }
    }

    internal static void ValidateArguments(string tool, IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null) return;
        foreach ((string name, JsonElement value) in arguments)
        {
            if (value.ValueKind == JsonValueKind.Null) continue;
            if (Choices(tool, name) is string[] choices && value.ValueKind == JsonValueKind.String &&
                !choices.Contains(value.GetString(), StringComparer.OrdinalIgnoreCase))
                throw new PixToolException("invalid_arguments", $"{name} must be one of: {string.Join(", ", choices)}.");
            (double? min, double? max) = Bounds(tool, name);
            if ((min.HasValue || max.HasValue) && value.ValueKind == JsonValueKind.Number)
            {
                double number = value.GetDouble();
                if (!double.IsFinite(number) || (min.HasValue && number < min.Value) || (max.HasValue && number > max.Value))
                    throw new PixToolException("invalid_arguments", $"{name} is outside its advertised bounds.");
            }
            if (value.ValueKind == JsonValueKind.Object)
                ValidateArguments(tool, value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value));
        }
    }

    private static JsonElement CreateEventPageSchema()
    {
        JsonNode schema = JsonNode.Parse(PageSchema.GetRawText())!;
        schema["properties"]!["items"]!["items"] = Rebase(JsonNode.Parse(EventSchema.GetRawText())!, "#/properties/items/items");
        return JsonSerializer.SerializeToElement(schema);
    }

    private static JsonElement ArrayEnvelope(JsonNode itemSchema) => JsonSerializer.SerializeToElement(new JsonObject
    {
        ["type"] = "object",
        ["required"] = new JsonArray("items"),
        ["properties"] = new JsonObject
        {
            ["items"] = new JsonObject { ["type"] = "array", ["items"] = Rebase(itemSchema, "#/properties/items/items") },
        },
    });

    // The preview tools return mixed MCP content, so these describe only their JSON metadata.
    private sealed record PreviewImageSchemaDto(string ArtifactRef, string MimeType, int PngBytes,
        uint? OriginalWidth, uint? OriginalHeight, uint? Width, uint? Height, ImageCrop? Crop, bool? Resized);
    private sealed record PreviewBytesSchemaDto(string ArtifactRef, string MimeType, int Offset,
        int TotalBytes, int ReturnedBytes, string Base64, int? NextOffset, IReadOnlyList<ToolCallDto>? NextCalls);
}
