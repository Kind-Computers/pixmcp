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
    private static readonly JsonElement ResultReadSchema = AnyOf(Export<ResultReadDto>("value", "projection"), Export<ResultOutlineDto>("nextOffset"));
    /// <summary>tools/list schemas are rewritten once per tool and reused; keyed on the SDK's own schema text so a changed tool is not served stale.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Source, JsonElement Input, JsonElement Output)> ToolSchemaCache = new(StringComparer.Ordinal);
    /// <summary>How long clients may cache list results; the tool set is fixed for the life of the process.</summary>
    private static readonly TimeSpan ListTimeToLive = TimeSpan.FromHours(1);
    private static readonly JsonElement PageSchema = Export<PageResult<object>>("nextOffset", "extra");
    private static readonly JsonElement EventPageSchema = CreateEventPageSchema();
    private static readonly JsonElement JobsSchema = Export<PageResult<JobDto>>("nextOffset", "extra");
    private static readonly JsonElement PendingSchema = CreatePendingSchema();
    private static readonly JsonElement TableSchema = Export<TableDto>("handle", "extra", "nextOffset");
    /// <summary>Tools whose rows can be returned as a positional table (format=table).</summary>
    internal static readonly string[] TableTools =
    {
        "pix_gpu_events", "pix_gpu_timing_events", "pix_gpu_counters_read", "pix_gpu_timing_tree", "pix_gpu_shaders",
        "pix_gpu_resources", "pix_timing_events", "pix_timing_hotspots", "pix_gpu_rollup", "pix_gpu_bubbles",
    };
    private static readonly AsyncLocal<CallToolRequestParams?> CurrentRequest = new();

    internal static object? CurrentArguments() => CurrentRequest.Value?.Arguments;
    internal static string[] CurrentOwners() => Owners(CurrentRequest.Value?.Arguments).ToArray();
    /// <summary>Arguments larger than this are reduced to handles and short scalars in a job's origin.</summary>
    internal const int MaxOriginArgumentBytes = 2048;

    /// <summary>The tool call being served, as an executable call (its arguments capped), or null outside a tool call.</summary>
    internal static ToolCallDto? CurrentCall()
    {
        CallToolRequestParams? request = CurrentRequest.Value;
        if (request?.Name is not string name) return null;
        IDictionary<string, JsonElement> arguments = request.Arguments ?? new Dictionary<string, JsonElement>();
        JsonElement element = JsonSerializer.SerializeToElement(arguments);
        if (element.GetRawText().Length > MaxOriginArgumentBytes)
        {
            var kept = new Dictionary<string, JsonElement>();
            foreach ((string key, JsonElement value) in arguments)
                if (key == "handle" || key.EndsWith("Handle", StringComparison.Ordinal) || value.GetRawText().Length <= 64) kept[key] = value;
            kept["argumentsTruncated"] = JsonSerializer.SerializeToElement(true);
            element = JsonSerializer.SerializeToElement(kept);
        }
        return new ToolCallDto(name, element);
    }

    /// <summary>Makes <paramref name="request"/> the current call for the scope (the transport filter and tests).</summary>
    internal static IDisposable WithRequest(CallToolRequestParams? request)
    {
        CallToolRequestParams? previous = CurrentRequest.Value;
        CurrentRequest.Value = request;
        return new RequestScope(previous);
    }
    private sealed class RequestScope(CallToolRequestParams? previous) : IDisposable
    {
        public void Dispose() => CurrentRequest.Value = previous;
    }
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
            using IDisposable scope = WithRequest(request.Params);
            string tool = request.Params?.Name ?? "";
            // A client that sent a progress token receives notifications/progress while this call waits on a job.
            ClientProgress? progress = request.Params?.ProgressToken is ProgressToken token && request.Server is { } server
                ? new ClientProgress(value => server.NotifyProgressAsync(token, value, options: null, cancellationToken: CancellationToken.None))
                : null;
            using IDisposable progressScope = ProgressForwarding.Scope(progress);
            try
            {
                if (!Toolsets.Enabled(tool)) return ErrorResult(PixErrors.ToolDisabled(tool));
                ValidateArguments(tool, request.Params?.Arguments);
                CallToolResult result = await next(request, ct).ConfigureAwait(false);
                AddStructuredContent(result);
                PixSession? session = request.Services?.GetService<PixSession>();
                Annotate(result, session, request.Params?.Arguments);
                // Summary text mode: the budget counts the summary, and a deferred replacement is summarised again.
                bool summary = ServerOptions.Current.TextContent == ServerOptions.TextContentSummary;
                if (summary) TextSummary.Apply(result, tool);
                BoundResult(result, session, CurrentOwners(), tool == "pix_result_read", request.Params?.Name);
                if (summary) TextSummary.Apply(result, tool);
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return ErrorResult(ex);
            }
            finally
            {
                if (progress is not null) await progress.DrainAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
        });
        filters.AddListToolsFilter(next => async (request, ct) =>
        {
            ListToolsResult result = await next(request, ct).ConfigureAwait(false);
            Tool[] ordered = result.Tools.Where(t => Toolsets.Enabled(t.Name)).OrderBy(t => t.Name, StringComparer.Ordinal).ToArray();
            foreach (Tool tool in ordered)
            {
                // The SDK can share tool metadata across requests. Replace it atomically with immutable, memoised JSON.
                lock (tool)
                {
                    string source = tool.InputSchema.GetRawText();
                    if (!ToolSchemaCache.TryGetValue(tool.Name, out var cached) || (cached.Source != source && cached.Input.GetRawText() != source))
                    {
                        cached = (source, InputSchemaFor(tool.Name, tool.InputSchema), SchemaFor(tool.Name));
                        ToolSchemaCache[tool.Name] = cached;
                    }
                    tool.OutputSchema = cached.Output;
                    tool.InputSchema = cached.Input;
                }
            }
            result.Tools = ordered;
            result.TimeToLive = ListTimeToLive;
            result.CacheScope = CacheScope.Private;
            return result;
        });
        filters.AddListResourcesFilter(next => async (request, ct) =>
        {
            ListResourcesResult result = await next(request, ct).ConfigureAwait(false);
            result.Resources = result.Resources.OrderBy(r => r.Uri, StringComparer.Ordinal).ToArray();
            result.TimeToLive = ListTimeToLive;
            result.CacheScope = CacheScope.Private;
            return result;
        });
        filters.AddListResourceTemplatesFilter(next => async (request, ct) =>
        {
            ListResourceTemplatesResult result = await next(request, ct).ConfigureAwait(false);
            result.ResourceTemplates = result.ResourceTemplates.OrderBy(r => r.UriTemplate, StringComparer.Ordinal).ToArray();
            result.TimeToLive = ListTimeToLive;
            result.CacheScope = CacheScope.Private;
            return result;
        });
        filters.AddListPromptsFilter(next => async (request, ct) =>
        {
            ListPromptsResult result = await next(request, ct).ConfigureAwait(false);
            result.Prompts = result.Prompts.Where(p => Playbooks.Find(p.Name) is null || Playbooks.IsAvailable(p.Name))
                .OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
            result.TimeToLive = ListTimeToLive;
            result.CacheScope = CacheScope.Private;
            return result;
        });
        filters.AddGetPromptFilter(next => async (request, ct) =>
        {
            string name = request.Params?.Name ?? "";
            if (Playbooks.Find(name) is not null && !Playbooks.IsAvailable(name)) throw PixErrors.PromptDisabled(name);
            return await next(request, ct).ConfigureAwait(false);
        });
        filters.AddReadResourceFilter(next => async (request, ct) =>
        {
            // Resource bodies are live session state: never cacheable.
            ReadResourceResult result = await next(request, ct).ConfigureAwait(false);
            result.TimeToLive = TimeSpan.Zero;
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

    /// <summary>
    /// One JSON pass at the transport boundary: cost hints on every nextCalls entry and provenance dedup per handle.
    /// Skipped when the text cannot contain either, so ordinary results are not re-serialized.
    /// </summary>
    internal static void Annotate(CallToolResult result, PixSession? session, IDictionary<string, JsonElement>? arguments)
    {
        if (result.IsError == true || result.StructuredContent is not JsonElement value || value.ValueKind != JsonValueKind.Object) return;
        string raw = value.GetRawText();
        bool hasCalls = raw.Contains("\"nextCalls\"", StringComparison.Ordinal);
        bool hasProvenance = raw.Contains("rovenance\"", StringComparison.Ordinal);
        if (!hasCalls && !hasProvenance) return;
        JsonNode? node = JsonNode.Parse(raw);
        bool changed = hasCalls && CostHints.Annotate(node, session);
        if (hasProvenance && session is not null)
        {
            string[] owners = Owners(arguments).ToArray();
            bool include = arguments is not null && arguments.TryGetValue("includeProvenance", out JsonElement flag) && flag.ValueKind == JsonValueKind.True;
            string? Argument(string name) => arguments is not null && arguments.TryGetValue(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            string? OwnerFor(string key) => key switch
            {
                "baselineProvenance" => Argument("baselineHandle"),
                "candidateProvenance" => Argument("candidateHandle"),
                _ => Argument("handle") ?? owners.FirstOrDefault(),
            };
            changed |= ProvenanceDedup.Apply(node, include,
                OwnerFor,
                (owner, key) => session.TryGet<PixMcp.Pix.Handles.PixHandle>(owner)?.ProvenanceFingerprints.GetValueOrDefault(key),
                (owner, key, fingerprint) => { if (session.TryGet<PixMcp.Pix.Handles.PixHandle>(owner) is { } h) h.ProvenanceFingerprints[key] = fingerprint; });
        }
        if (changed) SetPayload(result, JsonSerializer.SerializeToElement(node, Json.Options));
    }

    internal static CallToolResult ErrorResult(Exception exception)
    {
        ErrorDto error = PixErrors.ToDto(exception);
        if (error.NextCalls.Any(c => c.Cost is null))
            error = error with { NextCalls = error.NextCalls.Select(c => c.Cost is null ? c with { Cost = CostHints.Default(c.Tool) } : c).ToArray() };
        string json = Json.Serialize(error);
        if (Encoding.UTF8.GetByteCount(json) > ResultStore.TargetBytes)
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

    internal static void BoundResult(CallToolResult result, PixSession? session, string[] owners, bool reading = false, string? operation = null, int? maxBytes = null)
    {
        if (result.StructuredContent is not JsonElement value) return;
        string json = value.GetRawText();
        int bytes = Encoding.UTF8.GetByteCount(json);
        if (session is not null && !reading && bytes > ResultStore.TargetBytes)
        {
            string resultRef = session.Results.StoreElement(value, owners, operation: operation);
            SetPayload(result, JsonSerializer.SerializeToElement(new DeferredResultDto(true, resultRef, bytes, ResultStore.DeferredCalls(resultRef)), Json.Options));
        }
        // The hard budget covers everything on the wire: structured JSON, every text block and every base64 image.
        long total = Encoding.UTF8.GetByteCount(result.StructuredContent!.Value.GetRawText());
        foreach (ContentBlock block in result.Content)
        {
            if (block is TextContentBlock text) total += Encoding.UTF8.GetByteCount(text.Text);
            else if (block is ImageContentBlock image) total += image.Data.Length;
        }
        int limit = maxBytes ?? Tools.Tools.MaxResultBytes;
        if (total > limit)
            throw new PixToolException(PixErrors.Codes.ResultTooLarge, $"tool result: response is {total:N0} UTF-8 bytes including text and image content, above {ServerOptions.MaxVariable}={limit:N0}. Request a smaller window or image.");
    }

    internal static JsonElement SchemaFor(string toolName)
    {
        JsonElement core = CoreSchemaFor(toolName);
        if (toolName == "pix_gpu_events") return AnyOf(core, TableSchema, Export<EventCountDto>(), Export<EventHistogramDto>(), DeferredSchema, ErrorSchema);
        return TableTools.Contains(toolName) ? AnyOf(core, TableSchema, DeferredSchema, ErrorSchema) : AnyOf(core, DeferredSchema, ErrorSchema);
    }

    /// <summary>The DTO schema with the named array properties also accepting a positional table (format = table on a composite response).</summary>
    private static JsonElement TableSections(JsonElement schema, params string[] properties)
    {
        JsonNode root = JsonNode.Parse(schema.GetRawText())!;
        if (root["properties"] is JsonObject declared)
            foreach (string name in properties)
                if (declared[name] is JsonNode original)
                    declared[name] = new JsonObject { ["anyOf"] = new JsonArray(original.DeepClone(), JsonNode.Parse(TableSchema.GetRawText())) };
        return JsonSerializer.SerializeToElement(root);
    }

    internal static JsonElement CoreSchemaFor(string toolName) => LegacyResultSchemas.For(toolName) ?? (toolName switch
    {
        "pix_gpu_analysis_start" or "pix_gpu_timing_prepare" or "pix_gpu_counters_prepare"
            or "pix_gpu_drpix_run" or "pix_gpu_bottleneck" or "pix_timing_resolve_symbols" or "pix_device_take_gpu_capture"
            or "pix_device_timing_capture_stop" or "pix_capture_upgrade" or "pix_gpu_shader_profile" or "pix_gpu_shader_static_profile" or "pix_gpu_compare" or "pix_gpu_preview"
            or "pix_gpu_export_cpp" or "pix_gpu_subcapture" or "pix_csv_compare" or "pix_gpu_sql_populate" or "pix_gpu_sql_export" or "pix_job_status" or "pix_job_wait" or "pix_job_cancel" => JobSchema,
        "pix_result_read" => ResultReadSchema,
        "pix_result_export" => Export<ResultExportDto>(),
        "pix_gpu_compare_changes" => Export<ComparisonChangesDto>(),
        "pix_csv_pass_candidates" => Export<CsvPassCandidatesDto>(),
        "pix_timing_overview" => AnyOf(Export<TimingOverviewDto>(), PendingSchema),
        "pix_timing_events" => AnyOf(Export<TimingEventsDto>(), PendingSchema),
        "pix_timing_submissions" => AnyOf(Export<TimingSubmissionsDto>(), PendingSchema),
        "pix_timing_thread_switches" => AnyOf(Export<TimingThreadSwitchesDto>(), PendingSchema),
        "pix_timing_gpu_summary" => AnyOf(Export<TimingGpuSummaryDto>(), PendingSchema),
        "pix_timing_tree" => AnyOf(Export<RecordedMarkerTreeDto>(), PendingSchema),
        "pix_timing_verdict" => AnyOf(Export<TimingVerdictDto>(), PendingSchema),
        "pix_correlate" => AnyOf(Export<CorrelationDto>(), PendingSchema),
        "pix_timing_counters_list" => AnyOf(Export<TimingCountersDto>(), PendingSchema),
        "pix_timing_counters_read" => AnyOf(Export<TimingCounterSamplesDto>(), PendingSchema),
        "pix_timing_hotspots" => AnyOf(Export<TimingHotspotsDto>(), PendingSchema),
        "pix_timing_calltree" => AnyOf(Export<TimingCalltreeDto>(), PendingSchema),
        "pix_timing_sql" => AnyOf(Export<Sql.SqlResultDto>(), PendingSchema),
        "pix_gpu_sql" => AnyOf(Export<Sql.SqlResultDto>(), PendingSchema),
        "pix_gpu_sql_tables" => Export<Sql.GpuSqlTablesDto>(),
        "pix_timing_schema" => AnyOf(Export<TimingSchemaDto>(), PendingSchema),
        "pix_jobs" => JobsSchema,
        "pix_shader_targets" => Export<PageResult<StaticProfiling.ShaderTargetDto>>("nextOffset", "extra"),
        "pix_gpu_events" => EventPageSchema,
        "pix_gpu_resources" => AnyOf(Export<PageResult<ResourceSummaryDto>>(), PendingSchema),
        "pix_gpu_resource_timeline" => AnyOf(Export<ResourceTimelineDto>(), PendingSchema),
        "pix_gpu_resource" => Export<ResourceDetailsDto>(),
        "pix_gpu_counters_read" => AnyOf(Export<PageResult<CounterValueRowDto>>(), Export<RollupDto>(), PendingSchema),
        "pix_gpu_timing_tree" => AnyOf(Export<TimingTreeDto>(), PendingSchema),
        "pix_gpu_pipeline_state" => AnyOf(Export<PipelineStateDto>(), PendingSchema),
        "pix_gpu_inspect_event" => AnyOf(Export<EventInspectionDto>(), PendingSchema),
        "pix_gpu_shader_code" => AnyOf(Export<ShaderCodeDto>(), PendingSchema),
        "pix_gpu_shader_diagnostics" => AnyOf(Export<ShaderDiagnosticsDto>(), PendingSchema),
        "pix_gpu_shader_search" => AnyOf(Export<ShaderSearchDto>(), PendingSchema),
        "pix_gpu_shaders" => AnyOf(Export<ShaderInventoryDto>(), PendingSchema),
        "pix_gpu_shader_uses" => AnyOf(Export<ShaderUsesDto>(), PendingSchema),
        "pix_gpu_rollup" => AnyOf(Export<RollupDto>(), PendingSchema),
        "pix_gpu_pipelines" => AnyOf(Export<PipelinesDto>(), PendingSchema),
        "pix_gpu_queue_overlap" => AnyOf(Export<QueueOverlapDto>(), PendingSchema),
        "pix_gpu_bubbles" => AnyOf(Export<BubblesDto>(), PendingSchema),
        "pix_gpu_event_resources" => AnyOf(Export<EventResourcesDto>(), PendingSchema),
        "pix_gpu_resource_uses" => AnyOf(Export<ResourceUsesDto>(), PendingSchema),
        "pix_gpu_overview" => AnyOf(Export<CaptureOverviewDto>(), TableSections(Export<CaptureOverviewDto>(), "topPasses", "topDraws"), PendingSchema),
        "pix_gpu_timing_events" => AnyOf(Export<PageResult<TimingEventDto>>(), PendingSchema),
        "pix_gpu_counters_list" => AnyOf(Export<PageResult<LegacyResultSchemas.CounterMetadata>>(), PendingSchema),
        "pix_dump_event" => Export<Tools.DumpEventResultDto>(),
        "pix_dump_triage" => Export<Tools.DumpTriageDto>(),
        "pix_gpu_preview_image" => Export<PreviewImageSchemaDto>(),
        "pix_gpu_preview_bytes" => Export<PreviewBytesSchemaDto>(),
        "pix_gpu_occupancy" => AnyOf(Export<LegacyResultSchemas.Occupancy>(), Export<LegacyResultSchemas.UnavailableResult>(), PendingSchema),
        "pix_gpu_hf_counters" => AnyOf(Export<LegacyResultSchemas.HighFrequency>(), Export<LegacyResultSchemas.UnavailableResult>(), PendingSchema),
        "pix_gpu_drpix_experiments" => AnyOf(Export<PageResult<DrPixExperimentDto>>("nextOffset", "extra"), PendingSchema),
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
        AllowProvenanceStubs(schema);
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

    private static readonly Lazy<JsonNode> StubSchema = new(() => JsonNode.Parse(Export<ProvenanceStubDto>().GetRawText())!);

    /// <summary>Provenance properties may come back as a stub once the handle has already returned the full block.</summary>
    private static void AllowProvenanceStubs(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj["properties"] is JsonObject properties)
                foreach (string key in ProvenanceDedup.Keys)
                    if (properties[key] is JsonObject original && original["anyOf"] is null)
                    {
                        properties.Remove(key);
                        properties[key] = new JsonObject { ["anyOf"] = new JsonArray(original, StubSchema.Value.DeepClone()) };
                    }
            foreach (JsonNode? child in obj.Select(p => p.Value).ToArray()) AllowProvenanceStubs(child);
        }
        else if (node is JsonArray array) foreach (JsonNode? child in array) AllowProvenanceStubs(child);
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

    internal static string[]? Choices(string tool, string name) => (tool, name) switch
    {
        ("pix_shader_targets", "vendor") => StaticProfiling.StaticTargets.Vendors,
        ("pix_gpu_analysis_start", "flags") => AnalysisFlags.Names,
        ("pix_device_take_gpu_capture", "delimiter") => GpuCaptureOptionNames.Delimiters,
        ("pix_device_take_gpu_capture", "captureKey") => GpuCaptureOptionNames.CaptureKeys,
        ("pix_device_timing_capture_start", "preset") => TimingCaptureOptions.Presets,
        ("pix_device_timing_capture_start", "virtualAllocEvents" or "heapAllocEvents" or "pixMemEvents" or "pageFaults") => TimingCaptureOptions.Levels,
        ("pix_device_timing_capture_start", "videoSourceType") => TimingCaptureOptions.VideoSourceTypes,
        (_, "codeType") => ["HLSL", "IL", "ISA"],
        ("pix_result_read", "mode") => ["values", "outline"],
        (_, "preset") => CounterPresets.Names.ToArray(),
        ("pix_gpu_resources", "type") => ["COMMITTED", "PLACED", "RESERVED"],
        ("pix_gpu_resources", "dimension") => ["BUFFER", "TEXTURE1D", "TEXTURE2D", "TEXTURE3D"],
        ("pix_gpu_api_objects", "type") => ["HEAP", "RESOURCE", "COMMAND_QUEUE", "COMMAND_ALLOCATOR"],
        ("pix_device_d3d_settings_set", "category") => ["debugLayer", "dred", "device"],
        ("pix_csv_compare", "stat") => ["mean", "median", "p95"],
        ("pix_gpu_sql_export", "format") => ["csv", "json"],
        ("pix_gpu_sql_tables", "detail") => ["summary", "full"],
        ("pix_gpu_rollup", "groupBy") => Rollups.GroupBys,
        ("pix_gpu_counters_read", "groupBy") => ["none", .. Rollups.GroupBys],
        ("pix_gpu_counters_read", "normalize") => CounterNormalization.Modes,
        ("pix_gpu_counters_read", "sortBy") => Tools.CountersTools.CounterSortKeys,
        ("pix_gpu_occupancy", "groupBy") => Tools.CountersTools.OccupancyGroupings,
        ("pix_gpu_hf_counters", "groupBy") => Tools.CountersTools.HfGroupings,
        ("pix_gpu_rollup", "metric") => Rollups.MetricNames,
        ("pix_gpu_rollup", "sortBy") => Rollups.SortKeys,
        ("pix_gpu_rollup", "normalize") => Rollups.Normalizations,
        ("pix_gpu_pipelines", "sortBy") => Tools.RollupTools.PipelineSortKeys,
        ("pix_gpu_bubbles", "sortBy") => Tools.QueueAnalysisTools.BubbleSortKeys,
        ("pix_gpu_resources", "sortBy") => Tools.ResourceTools.ResourceSortKeys,
        ("pix_gpu_resources", "usedAs") => ResourceAccess.UsedAs,
        ("pix_gpu_drpix_experiments", "family") => DrPixFamilies.Names,
        ("pix_gpu_compare_changes", "direction") => ComparisonResultQuery.Directions,
        ("pix_gpu_compare_changes", "sortBy") => ComparisonResultQuery.SortKeys,
        ("pix_gpu_compare_changes", "minConfidence") => ComparisonResultQuery.Confidences,
        ("pix_gpu_resource_uses" or "pix_gpu_resource_timeline", "access") => ResourceAccess.Classes,
        ("pix_gpu_resource_uses", "sortBy") => Tools.ResourceTools.ResourceUseSortKeys,
        ("pix_gpu_bubbles", "cause") => BubbleAnalysis.Causes,
        ("pix_gpu_shaders", "sortBy") => ShaderIndex.SortKeys,
        ("pix_gpu_events", "mode") => Tools.GpuCaptureTools.EventModes,
        ("pix_gpu_events", "bucketBy") => GroupKeys.BucketBys,
        ("pix_gpu_timing_events", "sortBy") => ["eopDuration", "topDuration", "eopStart", "index"],
        ("pix_gpu_timing_tree", "sortBy") => TimingTree.SortKeys,
        ("pix_timing_tree", "sortBy") => RecordedMarkerTree.SortKeys,
        ("pix_timing_verdict", "frameSource") => TimingDatabase.FrameSources,
        ("pix_gpu_overview", "format") => Shaping.Formats,
        (_, "format") when TableTools.Contains(tool) => Shaping.Formats,
        (_, "kind") when tool.StartsWith("pix_gpu_") => PixMcp.Tools.Tools.Kinds,
        ("pix_timing_events", "domain") => TimingDatabase.EventDomains,
        ("pix_timing_events", "orderBy") => ["start", "duration"],
        (_, "rangeMode") when tool.StartsWith("pix_timing_") => TimingDatabase.RangeModes,
        _ => null,
    };

    internal static (double? min, double? max) Bounds(string tool, string name) => (tool, name) switch
    {
        ("pix_dump_triage", "maxEvents") => (1, 50000),
        ("pix_gpu_shader_static_profile", "topN") => (1, 200),
        ("pix_gpu_shader_profile", "topN") => (1, 1000),
        ("pix_gpu_preview_bytes", "limit") => (1, Tools.PreviewTools.MaxBytesPerPage),
        ("pix_gpu_preview" or "pix_gpu_export_cpp" or "pix_gpu_subcapture" or "pix_csv_compare", "timeoutSeconds") => (1, 3600),
        ("pix_gpu_shader_search", "nodeIndex") => (0, null),
        ("pix_gpu_shader_search", "contextLines") => (0, 20),
        ("pix_timing_sql" or "pix_gpu_sql", "maxRows") => (1, Sql.SqlRequest.MaxMaxRows),
        ("pix_timing_sql" or "pix_gpu_sql", "maxBytes") => (Sql.SqlRequest.MinMaxBytes, Sql.SqlRequest.MaxMaxBytes),
        ("pix_timing_sql" or "pix_gpu_sql", "maxStringLength") => (Sql.SqlRequest.MinMaxStringLength, Sql.SqlRequest.MaxMaxStringLength),
        ("pix_timing_sql", "timeoutSeconds") => (0, 120),
        ("pix_gpu_sql", "timeoutSeconds") => (0, 600),
        ("pix_gpu_sql_export", "maxRows") => (1, null),
        ("pix_timing_gpu_summary", "limit") => (1, 100),
        ("pix_timing_tree", "depth") => (1, 8),
        ("pix_timing_tree", "minSelfNs") => (0, null),
        ("pix_timing_verdict", "maxFrames") => (2, 5000),
        ("pix_gpu_rollup" or "pix_gpu_counters_read", "depth") => (1, 16),
        ("pix_gpu_rollup", "minCount") => (1, null),
        ("pix_gpu_rollup", "minPercent") => (0, 1000),
        ("pix_gpu_queue_overlap" or "pix_gpu_bubbles", "minGapNs") => (0, null),
        ("pix_gpu_resources", "minBytes") => (0, null),
        ("pix_gpu_drpix_run", "maxRuns") => (1, 1000),
        ("pix_gpu_compare", "rollupDepth") => (1, 8),
        ("pix_gpu_compare", "repeats") => (1, 5),
        ("pix_gpu_bottleneck", "maxDrPixRuns") => (1, 8),
        (_, "efficiencyClass") => (0, 255),
        (_, "frameIndex") => (0, null),
        (_, "startLine") => (1, null),
        (_, "offset" or "queueIndex" or "shaderIndex" or "eventIndex" or "setIndex"
            or "pointOffset" or "sampleOffset" or "viewOffset" or "bindingOffset" or "nodeOffset") => (0, null),
        (_, "limit" or "lineCount" or "maxNodes" or "viewLimit" or "bindingLimit" or "nodeLimit") => (1, 1000),
        (_, "nodeIndex") => (-1, null),
        (_, "topN") => (1, Paging.MaxLimit),
        (_, "maxStringLength") => (Shaping.MinStringLength, Shaping.MaxStringLengthLimit),
        (_, "waitSeconds" or "timeoutSeconds") => (0, 3600),
        _ => (null, null),
    };

    internal static JsonElement InputSchemaFor(string tool, JsonElement original)
    {
        JsonNode schema = JsonNode.Parse(original.GetRawText())!;
        Apply(schema, true);
        return JsonSerializer.SerializeToElement(schema);
        void Apply(JsonNode? node, bool topLevel)
        {
            if (node is JsonObject obj && obj["properties"] is JsonObject properties)
                foreach ((string name, JsonNode? property) in properties)
                {
                    if (property is not JsonObject p) continue;
                    if (Choices(tool, name) is string[] values)
                    {
                        var choices = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
                        // An array parameter constrains its elements.
                        if (p["items"] is JsonObject items) items["enum"] = choices;
                        else p["enum"] = choices;
                    }
                    (double? min, double? max) = Bounds(tool, name);
                    if (min.HasValue) p["minimum"] = min.Value;
                    if (max.HasValue) p["maximum"] = max.Value;
                    if (topLevel && Examples(tool, name, p) is JsonNode[] examples) p["examples"] = new JsonArray(examples);
                    Apply(p, false);
                }
        }
    }

    /// <summary>Concrete example values for reference-shaped parameters, so the wire shape is learned from the schema, not from errors.</summary>
    internal static JsonNode[]? Examples(string tool, string name, JsonObject property)
    {
        string handle = tool.StartsWith("pix_timing_", StringComparison.Ordinal) ? "timing-1"
            : tool.StartsWith("pix_dump_", StringComparison.Ordinal) ? "dump-1"
            : tool.StartsWith("pix_device_", StringComparison.Ordinal) ? "device-1" : "gpu-1";
        JsonNode EventRef() => new JsonObject { ["handle"] = "gpu-1", ["queueIndex"] = 0, ["eventIndex"] = 42 };
        bool eventRefShaped = property["properties"] is JsonObject props && props["eventIndex"] is not null && props["queueIndex"] is not null;
        return name switch
        {
            "handle" or "baselineHandle" or "candidateHandle" or "gpuHandle" => [JsonValue.Create(handle)!],
            "timingHandle" => [JsonValue.Create("timing-1")!],
            "shaderRef" => [new JsonObject { ["eventRef"] = EventRef(), ["shaderIndex"] = 0 }],
            "shaderKey" => [JsonValue.Create("hash:PS:3f9a1c2e")!],
            "resourceRef" => [new JsonObject { ["handle"] = "gpu-1", ["apiObjectId"] = "0x1a2b" }],
            "markerPathPrefix" => [JsonValue.Create("Frame/Shadow")!],
            "format" => [JsonValue.Create("table")!],
            "kind" => [JsonValue.Create("work")!],
            "sql" when tool == "pix_timing_sql" => [JsonValue.Create("SELECT Core, COUNT(*) AS switches FROM ContextSwitch WHERE Timestamp >= $start AND Timestamp < $end GROUP BY Core ORDER BY switches DESC")!],
            "params" when tool == "pix_timing_sql" => [new JsonObject { ["pid"] = 61052 }],
            _ when eventRefShaped => [EventRef()],
            _ => null,
        };
    }

    internal static void ValidateArguments(string tool, IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null) return;
        foreach ((string name, JsonElement value) in arguments)
        {
            if (value.ValueKind == JsonValueKind.Null) continue;
            if (Choices(tool, name) is string[] choices)
            {
                if (value.ValueKind == JsonValueKind.String && !IsChoice(tool, name, value.GetString()!, choices))
                    throw new PixToolException(PixErrors.Codes.InvalidArguments, $"{name} must be one of: {string.Join(", ", choices)}.");
                if (value.ValueKind == JsonValueKind.Array)
                {
                    string[] invalid = value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String && !IsChoice(tool, name, e.GetString()!, choices))
                        .Select(e => e.GetString()!).ToArray();
                    if (invalid.Length > 0)
                    {
                        // The retry keeps every other argument and the recognised elements.
                        var retry = new JsonObject();
                        foreach ((string key, JsonElement argument) in arguments)
                        {
                            retry[key] = key != name ? JsonNode.Parse(argument.GetRawText())
                                : new JsonArray(argument.EnumerateArray().Where(e => e.ValueKind != JsonValueKind.String || !invalid.Contains(e.GetString()))
                                    .Select(e => JsonNode.Parse(e.GetRawText())).ToArray());
                        }
                        throw new PixToolException(PixErrors.Codes.InvalidArguments,
                            $"{name} elements must be one of: {string.Join(", ", choices)}; not recognised: {string.Join(", ", invalid)}.", false, [new ToolCallDto(tool, retry)]);
                    }
                }
            }
            (double? min, double? max) = Bounds(tool, name);
            if ((min.HasValue || max.HasValue) && value.ValueKind == JsonValueKind.Number)
            {
                double number = value.GetDouble();
                if (!double.IsFinite(number) || (min.HasValue && number < min.Value) || (max.HasValue && number > max.Value))
                    throw new PixToolException(PixErrors.Codes.InvalidArguments, $"{name} is outside its advertised bounds.");
            }
            if (value.ValueKind == JsonValueKind.Object)
                ValidateArguments(tool, value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value));
        }
    }

    /// <summary>Choice membership, case-insensitive; analysis flags also accept their PIX_ANALYSIS_FLAG_/PIX_ANALYSIS_ spellings.</summary>
    private static bool IsChoice(string tool, string name, string value, string[] choices)
        => choices.Contains((tool, name) == ("pix_gpu_analysis_start", "flags") ? AnalysisFlags.ShortName(value) : value, StringComparer.OrdinalIgnoreCase);

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
        uint? OriginalWidth, uint? OriginalHeight, uint? Width, uint? Height, ImageCrop? Crop, bool? Resized, bool AlphaIgnored);
    private sealed record PreviewBytesSchemaDto(string ArtifactRef, string MimeType, int Offset,
        int TotalBytes, int ReturnedBytes, string Base64, int? NextOffset, IReadOnlyList<ToolCallDto>? NextCalls);
}
