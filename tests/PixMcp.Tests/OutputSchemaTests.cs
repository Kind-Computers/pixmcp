using System.Text.Json;
using System.Reflection;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using Xunit;
using PixMcp.Pix.Handles;

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
        Validate("pix_gpu_inspect_event", new EventInspectionDto(reference, ["Frame"], evt, "draw", ApiCallParser.Parse("DrawInstanced(3, 1, 0, 0)"), null, pipeline, null, []));
        var timing = new EventTimingInspectionDto("measured", Metrics.Duration(20, null, 40, 1), null, "TOP timing is unavailable for this event.", null, 100, 120, 1, 3, 1,
            new SiblingStatsDto(2, 2, 20, 20, 40, 50), false, 10, new EventRef("gpu-1", 0, 11), new PerWorkItemDto("vertices", 3, 6.7), Metrics.Denominators);
        Validate("pix_gpu_inspect_event", new EventInspectionDto(reference, ["Frame"], evt, "draw", ApiCallParser.Parse("DrawInstanced(3, 1, 0, 0)"), timing, pipeline, null, [])
        {
            Targets = new EventTargetsDto("available", [new EventTargetDto("renderTarget", new ResourceRef("gpu-1", "0x16"), "BackBuffer", "R8G8B8A8_UNORM", 640, 480, 1, 0, 307200, 65.1)], null, "boundViews"),
            Counters = new EventCountersDto("notCollected", [], "No counter collection is cached."),
            Occupancy = new InspectionSectionStateDto("notJoined", "Use pix_gpu_occupancy."),
            Hints = new[] { new InsightDto("pipelined", "info", "Overlaps.", new Dictionary<string, object?> { ["previousGapNs"] = -5L }, "Compare EOP durations.", []) },
        });
        Validate("pix_gpu_shader_code", new ShaderCodeDto(new(reference, 0),
            new(new(reference, 0), 0, "0x1", "PS", null, null, null, null, null, 12, "0x0", ["HLSL"]),
            "HLSL", new(0, 0, 0, null, [], null), null, 1, 0, 0, null, null));
        Validate("pix_gpu_counters_read", new PageResult<CounterValueRowDto>(1, 0, 1, null,
            [new(reference, 12, null, "Draw", ["Frame"], new Dictionary<string, object?> { ["3"] = 12UL })], null));
        var totals = new QueueTotals(0, 100, 100, 0, 100, false, 1, 0, 0, 100, "eopOnly");
        var leaf = new TimingBranchDto(reference, 12, "Draw", null, TimingSemantics.Measured, Metrics.Duration(100, totals, null, 1),
            Metrics.Duration(100, totals), 100, 0, false, 0, 0, false, 0, 100, true, 0, 0, [], false, []);
        var branch = leaf with { Name = "Frame", Semantics = TimingSemantics.DerivedSum, Children = [leaf], ChildCount = 1, Self = Metrics.Duration(0, totals) };
        Validate("pix_gpu_timing_tree", new TimingTreeDto("gpu-1", 0, null, "inclusive", totals, Metrics.Denominators, 1, 0, 0, 0, 1, [branch],
            null, false, 2, false, new { source = "gpuReplay" }, []));
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

    [Fact]
    public void TutorialToolsDescribeNestedCoverageAndNavigation()
    {
        var range = new TimingRangeDto("100", "200", "100", "300");
        var calls = Array.Empty<ToolCallDto>();
        var submission = new TimingSubmissionDto("1", "submission:timing-1:0:1", "2", "Graphics", "3", 42, 7,
            "Render", "110", "150", "190", "40", "40", new("available"), new("available"), calls);
        Validate("pix_timing_submissions", new TimingSubmissionsDto("timing-1", range,
            new(1, 0, 1, null, [submission], null), calls));
        var transition = new TimingThreadSwitchDto("120", "switchOut", 0, 42, 8, 5,
            new("missing", [], "unavailable", "No recorded stack."));
        Validate("pix_timing_thread_switches", new TimingThreadSwitchesDto("timing-1", range,
            new("3", 42, 7, "Render", 1), "0", null, new(1, 0, 1, null, [transition], null), calls));
        var shader = new ShaderRef(new("gpu-1", 0, 4), 0);
        Validate("pix_gpu_shader_diagnostics", new ShaderDiagnosticsDto(shader, "1", "PS", new("absent", null),
            [new("HLSL", "absent", 0), new("ISA", "unavailable", null, "Driver unavailable", "unsupported_feature")], [], calls));
        var pass = new CsvPassDto("GPU/BasePass", 1, 2, 1, 100, true, true, 10, 10);
        Validate("pix_csv_pass_candidates", new CsvPassCandidatesDto("result-1", pass.Name, "gpu-1", "BasePass",
            "name-based candidates", false, pass, 0, 0, 0, null, [], null, calls));
        Validate("pix_device_timing_capture_start", new { started = true, path = "capture.wpix",
            settings = new TimingCaptureSettingsDto(true, 1000, true, true, true, true, true, false, false, 1024, 0, true),
            optionParts = new[] { new TimingOptionPartDto("captureSysmonCounters", "true") }, notes = new[] { "note" } });
        var job = new Job("job-1", "export-cpp", "Export frame").ToDto();
        Validate("pix_gpu_export_cpp", job);
        Validate("pix_csv_compare", job);
    }

    [Fact]
    public void RecordedTimingAnalysisSchemasValidate()
    {
        var range = new TimingRangeDto("0", "10000", "0", "5000") { RangeMode = "full" };
        var stats = new RecordedStatsDto(2, 10, 10, 12, 12, 0, 0, 0, 0);
        RecordedLaneTotalsDto totals = RecordedTiming.Totals([(100L, 300L), (200L, 400L)], 0, 1000)!;
        var queue = new TimingGpuQueueRollupDto("1", "Graphics", "Direct", null, 3, 2, new Dictionary<string, long> { ["zeroDuration"] = 1 }, totals, stats, stats,
            [new("10", 42, 7, "Render", 3, 2, stats)], [new("submission:timing-1:0:1", "10", "90", "100", 200, 0.0, 10)]);
        Validate("pix_timing_gpu_summary", new TimingGpuSummaryDto("timing-1", range, 42, "selection", [queue],
            [new("45", "3D", null, 2, totals, 1)], [new("1", "Monitor", 2, 16.667, 16.667, 16.667, 16.667, 60)],
            new("empty", 0, 0, 0, "reason"), TimingDatabase.GpuSummaryDenominators, ["note"], [], [new("pix_timing_submissions", new { handle = "timing-1" })]));
        var node = new RecordedTreeNodeDto("Frame", "Frame", 1, 2, RecordedTiming.Duration(200, 200, 200, null, 1), RecordedTiming.Duration(50, 200, 200), 170, true, 20,
            RecordedTiming.Stats([100L, 100L])!, 170, 30, "available", 3, "0", "0", "100");
        Validate("pix_timing_tree", new RecordedMarkerTreeDto("timing-1", range, "cpu", new("thread", "10", "Render", 42, 7, totals, 7, 2, 0, 1), null, 2, "inclusive", null,
            new(1, 0, 1, null, [node], null), false, null, new("available"), TimingDatabase.MarkerTreeDenominators, "interpretation", ["note"], []));
        var frame = new TimingFrameVerdictDto(0, "0", 100, 0.0, 10, 90, 5, 5, 0, 1, 0.0, 0.0, null, "cpuBound");
        Validate("pix_timing_verdict", new TimingVerdictDto("timing-1", range, 42, new("vsync", "VSync lane 1", "requested", 5, 5, false, null),
            new("10", 42, 7, "Render", 4, "mostSubmissions"),
            new("cpuBound", 100, "low", new Dictionary<string, long> { ["cpuBound"] = 5 }, 10, 90, 5, 5, 0, stats, stats, "implication"),
            [frame], new(1, 0, 1, null, [frame], null), [new("blocked", 6, "UserRequest", 5, 0.0, 1, 1)], [new("1", "Graphics", 10)],
            new("available", 9, 2, 4, 3, 100), new("unavailable", "none"), VerdictRules.RulesDto, VerdictRules.Semantics, []));
        var correlation = new CorrelationRowDto("Frame/Lighting", new EventRef("gpu-1", 0, 7), 2, 400, 0.0, 0.0, "measured", "Frame/Lighting", "cpu", 1, 50, stats, ["threadRowId 10"],
            "pathMatch", ["legacyPixPrefix"], "medium", 1, 10, 10, 0.25, []);
        Validate("pix_correlate", new CorrelationDto("gpu-1", "timing-1", TimingCorrelation.Identity, null, new ReplayProvenance("replay", "2606.18", null, null, null, "semantics"),
            range, 42, new(4, 6, false, 5, 7, false, 3, 2, 1, 1, 2), new(1, 0, 1, null, [correlation], null),
            [new("GpuOnly", 1, 0.0, new EventRef("gpu-1", 1, 2), null)], [new("Frame", 2, 0.0, null, "cpu")],
            [new(0, "Graphics", "GRAPHICS", "1", "Graphics", "Direct", "typeAndName", "medium")], TimingDatabase.CorrelationSemantics, [], []));
    }

    [Fact]
    public void DumpTriageSectionsMatchTheAdvertisedSchema()
    {
        var observation = new global::PixMcp.Tools.DumpTriageObservation("journal-3", 70, "runtimeError", "summary", new { code = "0x887A0006" },
            [new ToolCallDto("pix_dump_journal", new { handle = "dump-1", offset = 3, limit = 1 })]);
        Validate("pix_dump_triage", new global::PixMcp.Tools.DumpTriageDto("dump-1", new { }, "interpretation",
            new global::PixMcp.Tools.DumpDiagnosisDto("D3D12_DEVICE_ERROR_CODE_HANG", "bucket", "status", "brief", true, null),
            new Dictionary<string, global::PixMcp.Tools.DumpTriageCoverage> { ["events"] = new("truncated", 1, "maxEvents"), ["d3dState"] = new("unsupported", Reason: "note") },
            new Dictionary<string, int> { ["IN_PROGRESS"] = 1 },
            new Dictionary<string, IReadOnlyDictionary<string, int>> { ["0"] = new Dictionary<string, int> { ["IN_PROGRESS"] = 1 } },
            new global::PixMcp.Tools.DumpJournalSummaryDto(4, 4, 1, 3),
            Paging.Page(new[] { observation }, 1, 0, 10),
            [new ToolCallDto("pix_dump_triage", new { handle = "dump-1", maxEvents = 20000 })]));
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
