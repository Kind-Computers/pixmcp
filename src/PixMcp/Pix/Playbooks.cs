using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PixMcp.Pix;

/// <summary>
/// One call in a playbook. <see cref="Arguments"/> is a JSON object template: a string value "$name" becomes the prompt argument
/// (JSON objects such as eventRef are embedded as objects; a missing argument becomes a readable placeholder), "$name?" drops the
/// property when the argument is absent, and a "$selection" property becomes scope (from the scope or eventRef argument) or
/// markerPathPrefix. Optional steps are left out when a toolset hides their tool.
/// </summary>
internal sealed record PlaybookStep(int Level, string Tool, string Arguments, string Why, bool Optional = false);

internal sealed record PlaybookArgument(string Name, string Description);

/// <summary>A step-by-step investigation served as an MCP prompt (Tools/PixPrompts.cs).</summary>
internal sealed record Playbook(string Name, string Title, string Description, string Goal, IReadOnlyList<PlaybookArgument> Arguments,
    IReadOnlyList<PlaybookStep> Steps, IReadOnlyList<string> Caveats, IReadOnlyList<string> ExampleQuestions)
{
    /// <summary>Tools of the non-optional steps: the prompt is hidden unless every one is registered and enabled.</summary>
    public IReadOnlyList<string> RequiredTools => Steps.Where(s => !s.Optional).Select(s => s.Tool).Distinct(StringComparer.Ordinal).ToArray();
}

/// <summary>
/// The investigation playbooks. Every step names a registered tool with arguments it accepts (pinned by PromptTests); prompts whose
/// tools PIXMCP_TOOLSETS hides are not listed.
/// </summary>
internal static class Playbooks
{
    internal const string GpuHandleArgument = "GPU capture handle returned by pix_gpu_open, e.g. gpu-1.";
    internal const string ScopeArgument = "eventRef JSON object of the scope root, e.g. {\"handle\":\"gpu-1\",\"queueIndex\":0,\"eventIndex\":12}; takes precedence over markerPathPrefix.";
    internal const string PrefixArgument = "Marker path prefix selecting the scope when no scope is given, e.g. Frame/Shadow.";
    internal const string EventArgument = "eventRef JSON object copied from a previous result, e.g. {\"handle\":\"gpu-1\",\"queueIndex\":0,\"eventIndex\":42}.";
    internal const string TimingHandleArgument = "Timing capture handle returned by pix_timing_open.";
    internal const string DumpHandleArgument = "DirectX dump handle returned by pix_dump_open.";
    internal const string BaselineHandleArgument = "Baseline GPU capture handle, e.g. gpu-1.";
    internal const string CandidateHandleArgument = "Candidate GPU capture handle, e.g. gpu-2.";
    internal const string AnyHandleArgument = "Timing capture handle (pix_timing_open) or GPU capture handle (pix_gpu_open).";

    /// <summary>The ladder levels named in every prompt and in docs/investigation-ladder.md.</summary>
    public const string Ladder = "Level 0 verdicts, Level 1 composites, Level 2 scoped enumerators, Level 3 raw SQL and result reads";

    private const string ReplayCaveat = "Replay timing measures this machine replaying the capture; it is not the application's frame latency on the original GPU.";

    private static readonly PlaybookArgument Handle = new("handle", GpuHandleArgument);
    private static readonly PlaybookArgument Scope = new("scope", ScopeArgument);
    private static readonly PlaybookArgument Prefix = new("markerPathPrefix", PrefixArgument);
    private static readonly PlaybookArgument Event = new("eventRef", EventArgument);

    private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Placeholders for template values that are not prompt arguments (copied from earlier answers).</summary>
    private static readonly IReadOnlyDictionary<string, string> Placeholders = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["fullResultRef"] = "<fullResultRef from the pix_gpu_compare result>",
        ["resourceRef"] = "<resourceRef object copied from a pix_gpu_resources row>",
        ["shaderRef"] = "<shaderRef object copied from the inspect shaders or pipeline section>",
        ["threadRowId"] = "<render thread row id from the verdict>",
        ["startNs"] = "<startNs of the worst frame>",
        ["endNs"] = "<endNs of the worst frame>",
    };

    public static readonly IReadOnlyList<Playbook> All =
    [
        new("pix_frame_budget", "Where the GPU frame goes",
            "Frame budget playbook for a GPU capture: queue totals, top passes and draws, per-pass rollup, queue overlap and the costliest event.",
            "Explain where GPU replay time goes in this capture and which pass or draw to optimise first.",
            [Handle, Event],
            [
                new(0, "pix_gpu_overview", """{"handle":"$handle"}""",
                    "Queue busy, idle and span totals, top passes and work events with percentages, frames and insights with evidence. If timing is { pending, jobId }, wait for the job and repeat."),
                new(1, "pix_gpu_rollup", """{"handle":"$handle","groupBy":"markerDepth","depth":2}""",
                    "GPU time per pass two marker levels deep; the groups, including (no marker), reconcile to the queue's sumOfRootsNs."),
                new(1, "pix_gpu_queue_overlap", """{"handle":"$handle"}""",
                    "Whether the queues ran in parallel during replay, the critical path and the largest idle gaps."),
                new(2, "pix_gpu_timing_tree", """{"handle":"$handle","sortBy":"self","depth":2}""",
                    "Self time separates a pass's own work from its children; childrenExceedMeasured flags nodes whose children outlast the measured span."),
                new(1, "pix_gpu_inspect_event", """{"eventRef":"$eventRef"}""",
                    "Rank, siblings, draw parameters, render targets and hints for the costliest pass or draw (copy its eventRef from topPasses or topDraws)."),
                new(1, "pix_gpu_pipelines", """{"handle":"$handle","sortBy":"gpuTime"}""",
                    "Pipeline states (shader combinations) ranked by GPU time."),
            ],
            [
                ReplayCaveat,
                "Nested marker durations overlap: add siblings, never a parent and its children.",
                "percentOfQueueSpan divides by the replayed queue span and percentOfQueueSum by the sum of root events; they differ when roots overlap.",
                "A derivedSum or mixed node was not measured by PIX; its duration is the sum of its timed children.",
            ],
            ["Which pass dominates this frame?", "How much of the graphics queue span is idle?", "Which draws sit in the long tail?"]),

        new("pix_regression", "Find a GPU regression between two captures",
            "Regression playbook: compare a baseline and a candidate capture, filter the saved comparison, check noise and inspect the regressed event.",
            "Find which passes, events, bindings or shaders got slower or changed between the baseline and the candidate, and whether the change is above the noise floor.",
            [new("baselineHandle", BaselineHandleArgument), new("handle", CandidateHandleArgument), new("markerPathPrefix", "Optional marker path prefix that focuses the comparison, e.g. Frame/Lighting."), new("eventRef", "eventRef JSON object of the regressed event on the candidate.")],
            [
                new(1, "pix_gpu_compare", """{"baselineHandle":"$baselineHandle","candidateHandle":"$handle","markerPathPrefix":"$markerPathPrefix?","waitSeconds":30}""",
                    "One job that replays both captures: totals, deltas per marker path, top changes, noise and provenance warnings. Read the job's resultRef and keep its fullResultRef."),
                new(1, "pix_gpu_compare_changes", """{"fullResultRef":"$fullResultRef","direction":"regressions","excludeBelowNoise":true}""",
                    "Regressions above the noise floor, filtered from the saved comparison without replay."),
                new(1, "pix_gpu_compare_changes", """{"fullResultRef":"$fullResultRef","direction":"structural"}""",
                    "Added, removed and changed events, bindings and shaders."),
                new(1, "pix_gpu_compare", """{"baselineHandle":"$baselineHandle","candidateHandle":"$handle","markerPathPrefix":"$markerPathPrefix?","repeats":3,"waitSeconds":30}""",
                    "Repeats the timing collection to measure noise before a small delta is trusted."),
                new(1, "pix_gpu_inspect_event", """{"eventRef":"$eventRef"}""",
                    "Parameters, targets and hints of the regressed event on the candidate."),
            ],
            [
                "A delta below noise.noiseFloorNs is not evidence of a regression.",
                "provenance_mismatch warnings (adapter, vendor, flags, PIX build) make timing deltas incomparable.",
                "Changes matched by ordinalWithinPath have low confidence; check matchMethod and confidence.",
                ReplayCaveat,
            ],
            ["What got slower in the candidate?", "Did a shader or binding change in the lighting pass?", "Is the 3 % regression real or noise?"]),

        new("pix_dispatch_slow", "Why is this dispatch or draw slow",
            "Event playbook: inspect one dispatch or draw, classify its limiter, read counters and occupancy, run Dr. PIX experiments and read its shader.",
            "Explain why one work event (dispatch, draw or ExecuteIndirect) costs what it does and what would reduce that cost.",
            [Handle, new("eventRef", "eventRef JSON object of the dispatch or draw, e.g. {\"handle\":\"gpu-1\",\"queueIndex\":2,\"eventIndex\":5}.")],
            [
                new(1, "pix_gpu_inspect_event", """{"eventRef":"$eventRef","sections":["timing","pipeline","targets","hints"]}""",
                    "EOP and execution time, rank in the queue and parent, work items per call, render targets and hints such as tiny_dispatch."),
                new(0, "pix_gpu_bottleneck", """{"handle":"$handle","scope":"$eventRef"}""",
                    "Limiter classification with evidence rows, alternatives and a confidence."),
                new(2, "pix_gpu_counters_read", """{"handle":"$handle","preset":"utilization","scope":"$eventRef"}""",
                    "Vendor utilisation counters for the event, with units."),
                new(2, "pix_gpu_occupancy", """{"handle":"$handle","eventRef":"$eventRef"}""",
                    "Execution-unit occupancy during the event window, when the driver reports it."),
                new(1, "pix_gpu_drpix_run", """{"handle":"$handle","scope":"$eventRef","families":["basic"]}""",
                    "Dr. PIX experiments replay the event with work disabled and report what each saves.", Optional: true),
                new(3, "pix_gpu_shader_code", """{"shaderRef":"$shaderRef"}""",
                    "The shader source of the event."),
                new(1, "pix_gpu_shader_static_profile", """{"shaderRef":"$shaderRef","target":"Xe2-HPG","waitSeconds":60}""",
                    "Static profile of the same shader for an Intel Xe2 target (or an AMD family from pix_shader_targets): instruction mix, register pressure and loop hot spots.", Optional: true),
            ],
            [
                "Fewer than 64 thread groups with EOP far above execution time usually means launch overhead, not shader cost.",
                "exec is null when PIX recorded no start of execution; EOP alone includes queueing.",
                "Marker counter rows come from separate PIX rounds; never add them to event rows.",
                "Bottleneck confidence stays capped until the rules are validated for the vendor.",
            ],
            ["Why is the wave dispatch slow?", "Is this draw limited by pixel shading?", "Is this dispatch too small to be worth launching?"]),

        new("pix_cpu_vs_gpu", "Is the recorded run CPU, GPU or sync bound",
            "Timing capture playbook: frame verdict and overview, recorded GPU queue summary, then CPU hotspots or thread waits of the worst frame.",
            "Decide whether the recorded application was limited by CPU work, GPU work, synchronisation or presentation, and find the evidence.",
            [new("handle", TimingHandleArgument)],
            [
                new(0, "pix_timing_verdict", """{"handle":"$handle"}""",
                    "Per-frame CPU, GPU busy and render-thread wait shares with a dominant verdict, its rules and thresholds."),
                new(0, "pix_timing_overview", """{"handle":"$handle"}""",
                    "Data quality (dropped events), GPU queues, frames, cores, VRAM and insights."),
                new(1, "pix_timing_gpu_summary", """{"handle":"$handle"}""",
                    "Per-queue busy and idle time, submit latency and VSync intervals."),
                new(2, "pix_timing_hotspots", """{"handle":"$handle","startNs":"$startNs","endNs":"$endNs"}""",
                    "Sampled CPU hotspots inside the worst CPU-bound frame (copy its window from worstFrames)."),
                new(2, "pix_timing_thread_switches", """{"handle":"$handle","threadRowId":"$threadRowId"}""",
                    "Scheduling transitions and wait reasons of the render thread in a sync-bound frame."),
                new(2, "pix_timing_submissions", """{"handle":"$handle"}""",
                    "Recorded command-list submissions with CPU submit and GPU execution times."),
            ],
            [
                "The verdict is a heuristic (heuristic = true); read its rules and thresholds before acting on it.",
                "CPU samples are statistical counts, not exact CPU time.",
                "Wait reason names are probable decodings of KWAIT_REASON codes.",
                "rangeMode full covers the whole capture; reliable stops where PIX guarantees complete data.",
            ],
            ["Is this game CPU or GPU bound?", "Why do some frames spike?", "Is the render thread waiting on the GPU?"]),

        new("pix_crash_triage", "Triage a GPU crash or device removal",
            "DirectX dump playbook: ranked triage evidence, dump metadata, queue status, runtime journal and page faults.",
            "Find the most likely cause of the GPU hang, crash or device removal recorded in a DirectX dump file.",
            [new("handle", DumpHandleArgument)],
            [
                new(0, "pix_dump_triage", """{"handle":"$handle"}""",
                    "Ranked observations with evidence and coverage across the dump's sources."),
                new(2, "pix_dump_info", """{"handle":"$handle"}""",
                    "Dump metadata and queues."),
                new(2, "pix_dump_queues", """{"handle":"$handle"}""",
                    "Per-queue status, hardware status, page-fault and root-event counts."),
                new(2, "pix_dump_journal", """{"handle":"$handle"}""",
                    "The D3D runtime journal, where failures carry the high bit."),
                new(2, "pix_dump_page_faults", """{"handle":"$handle"}""",
                    "Page faults with the resources and events near them."),
            ],
            [
                "Triage ranking is heuristic, and PIX's own summary is a heuristic too.",
                "An in-progress event on a faulted queue is a suspect, not proof of the cause.",
                "DirectX dump files are an experimental PIX feature; missing sections appear in coverage rather than as errors.",
            ],
            ["Why was the device removed?", "Which queue hung and what was it executing?", "Did a page fault hit a freed resource?"]),

        new("pix_async_overlap", "Do the GPU queues overlap",
            "Async compute playbook: queue overlap verdicts, idle bubbles with causes and the events that explain them.",
            "Check whether the graphics, compute and copy queues run in parallel and why they sit idle between events.",
            [Handle, Event],
            [
                new(0, "pix_gpu_overview", """{"handle":"$handle"}""",
                    "Queue busy and idle totals plus the overlap summary and insights such as async_not_overlapping."),
                new(1, "pix_gpu_queue_overlap", """{"handle":"$handle"}""",
                    "Pairwise overlap verdicts, solo busy time, the critical path and the largest gaps."),
                new(2, "pix_gpu_bubbles", """{"handle":"$handle","sortBy":"duration"}""",
                    "Idle gaps between consecutive events ranked by duration, with the attributed cause and evidence event."),
                new(1, "pix_gpu_inspect_event", """{"eventRef":"$eventRef"}""",
                    "The evidence event of the largest bubble (build its eventRef from the bubble row)."),
                new(2, "pix_gpu_events", """{"handle":"$handle","kind":"barrier","mode":"count","bucketBy":"markerPath"}""",
                    "How many resource barriers each pass issues."),
            ],
            [
                "Replay may serialise queues and adds its own synchronisation: overlap is an upper bound and bubbles a lower bound.",
                "A bubble's cause is inferred from the events between the two timed rows.",
                ReplayCaveat,
            ],
            ["Is async compute actually running in parallel?", "Why is the compute queue idle?", "Does the copy queue block graphics?"]),

        new("pix_bandwidth_hogs", "Find memory bandwidth hogs",
            "Resource playbook: largest resources, heaviest read traffic in a scope, one resource's timeline and bandwidth counters.",
            "Find the resources whose size and traffic most likely limit memory bandwidth in a pass.",
            [Handle, Scope, Prefix],
            [
                new(2, "pix_gpu_resources", """{"handle":"$handle","sortBy":"estimatedBytes","includeTotals":true}""",
                    "Resources by estimated size with totals by dimension, format and heap kind (metadata only, no replay)."),
                new(2, "pix_gpu_resources", """{"handle":"$handle","$selection":null,"usedAs":"read","sortBy":"traffic"}""",
                    "Resources read in the scope ranked by traffic; this builds the resource use index by replay."),
                new(1, "pix_gpu_resource_timeline", """{"resourceRef":"$resourceRef"}""",
                    "Reads, writes, copies and barriers of one resource in order, with insights such as written_never_read."),
                new(2, "pix_gpu_counters_read", """{"handle":"$handle","preset":"memoryBandwidth","$selection":null}""",
                    "Memory bandwidth counters for the scope's work events, when the vendor exposes them."),
                new(0, "pix_gpu_bottleneck", """{"handle":"$handle","$selection":null}""",
                    "Whether the evidence points at memoryBandwidth rather than shading."),
            ],
            [
                "estimatedBytes ignores alignment and tiling.",
                "estimatedTrafficBytes is an upper bound that assumes a full resource touch per use.",
                "Counter presets are best effort per vendor; unmatched patterns are listed rather than guessed.",
            ],
            ["Which textures dominate memory in the lighting pass?", "Is this pass memory-bandwidth bound?", "Is any render target written but never read?"]),

        new("pix_fill_vs_vertex", "Is a pass fill-rate or vertex bound",
            "Shading playbook: bottleneck classification with Dr. PIX evidence, shader cost rollup, per-stage counters and render-target size.",
            "Decide whether a pass is limited by pixel shading and fill rate or by vertex and geometry work.",
            [Handle, Scope, Prefix, new("eventRef", "eventRef JSON object of a representative draw in the pass.")],
            [
                new(0, "pix_gpu_bottleneck", """{"handle":"$handle","$selection":null,"evidence":["timing","counters","drpix"]}""",
                    "Limiter classification including Dr. PIX savings evidence."),
                new(1, "pix_gpu_drpix_run", """{"handle":"$handle","$selection":null,"families":["basic","rasterization"]}""",
                    "1x1 viewport, MSAA and early-Z experiments over the scope with the saving of each run.", Optional: true),
                new(1, "pix_gpu_rollup", """{"handle":"$handle","groupBy":"shader","$selection":null}""",
                    "GPU time by shader in the scope."),
                new(2, "pix_gpu_counters_read", """{"handle":"$handle","preset":"perStageAlu","$selection":null}""",
                    "Per-stage execution-unit utilisation, when the vendor exposes it."),
                new(1, "pix_gpu_inspect_event", """{"eventRef":"$eventRef","sections":["timing","targets","hints"]}""",
                    "Render-target size and nsPerMegapixel of a representative draw."),
            ],
            [
                "A 1x1 viewport saving measures pixel shading and rasterisation together.",
                "Dr. PIX savings are measured on this machine's replay only.",
                "nsPerMegapixel uses the bound render-target size, not the pixels a draw covers.",
            ],
            ["Is the lighting pass fill-rate bound?", "Would a smaller render target help?", "Is vertex work the real cost?"]),

        new("pix_sql_investigation", "Answer a question with SQL",
            "SQL playbook for timing and GPU captures: read the schema, run a named query, then adapt its SQL.",
            "Answer a question that no composite tool answers by querying the capture with read-only SQL.",
            [new("handle", AnyHandleArgument)],
            [
                new(3, "pix_timing_schema", """{"handle":"$handle"}""",
                    "Timing capture: tables, column docs, capabilities, bound parameters and named queries.", Optional: true),
                new(3, "pix_timing_sql", """{"handle":"$handle","query":"gpu_busy_per_queue"}""",
                    "Timing capture: a named query; the result echoes its expanded SQL to adapt.", Optional: true),
                new(3, "pix_timing_sql", """{"handle":"$handle","sql":"SELECT ... WHERE Begin >= $start AND Begin < $end ORDER BY ...","params":{}}""",
                    "Timing capture: the adapted query, bound to the pre-bound window.", Optional: true),
                new(3, "pix_gpu_sql_tables", """{"handle":"$handle"}""",
                    "GPU capture: families with their population state and cost, tables, views and named queries.", Optional: true),
                new(3, "pix_gpu_sql_populate", """{"handle":"$handle","tables":["core","timing"],"waitSeconds":60}""",
                    "GPU capture: materialise only the families the query needs (timing replays the capture).", Optional: true),
                new(3, "pix_gpu_sql", """{"handle":"$handle","query":"top_passes"}""",
                    "GPU capture: a named query; adapt its SQL to the question.", Optional: true),
            ],
            [
                "Never add child counter rows into marker rows; PIX collects marker values in separate rounds.",
                "Replay spans and nested marker sums are not frame latency.",
                "Put ORDER BY before paging with offset; unordered continuations are unstable.",
                "Populate counters, hf and psos only when a query needs them: each replays the capture.",
                "Timing SQL times are nanoseconds in the capture clock; ProcThreadId packs pid << 32 | tid.",
            ],
            ["Which thread submits the most command lists?", "Which shader costs the most time per dispatch work item?", "How many frames missed VSync?"]),

        new("pix_gpu_bottleneck", "Classify what limits a GPU pass",
            "Bottleneck playbook: one-call limiter verdict, fuller evidence, then the counters, occupancy and events behind it.",
            "Classify what limits a pass (pixel shading, vertex work, raster or depth, memory bandwidth, cache, occupancy, launch overhead or sync idle) and check the evidence.",
            [Handle, Scope, Prefix],
            [
                new(0, "pix_gpu_bottleneck", """{"handle":"$handle","$selection":null}""",
                    "Verdict, alternatives and evidence from timing, counters and occupancy; follow the recommendations' nextCalls."),
                new(0, "pix_gpu_bottleneck", """{"handle":"$handle","$selection":null,"evidence":["timing","counters","occupancy","drpix"],"maxDrPixRuns":3}""",
                    "Adds Dr. PIX savings, which replays the scope several times."),
                new(2, "pix_gpu_counters_read", """{"handle":"$handle","preset":"utilization","$selection":null,"groupBy":"marker"}""",
                    "The utilisation counters behind the verdict, grouped by marker."),
                new(2, "pix_gpu_occupancy", """{"handle":"$handle","$selection":null,"groupBy":"event"}""",
                    "Occupancy per work event in the scope."),
                new(2, "pix_gpu_timing_tree", """{"handle":"$handle","$selection":null,"sortBy":"topStart"}""",
                    "The scope's events in start order, to see gaps and pipelining."),
            ],
            [
                "High confidence needs two evidence sources, a clear margin and a validated vendor; no vendor is validated yet, so expect medium or low.",
                "Evidence rows list every rule threshold and whether it held; unknown means no rule fired.",
                "Marker counter rows are PIX rounds and are reported separately from event rows.",
                ReplayCaveat,
            ],
            ["What limits the lighting pass?", "Is this compute pass occupancy or latency bound?", "Why is the shadow pass slow on this GPU?"]),
    ];

    public static Playbook? Find(string name) => All.FirstOrDefault(p => p.Name == name);

    /// <summary>Listed when every required tool is registered and enabled and at least one step can run.</summary>
    public static bool IsAvailable(Playbook playbook)
        => playbook.RequiredTools.All(t => ToolRegistry.Has(t)) && playbook.Steps.Any(s => ToolRegistry.Has(s.Tool));

    public static bool IsAvailable(string name) => Find(name) is { } playbook && IsAvailable(playbook);

    /// <summary>The prompt text: goal, arguments, numbered steps with exact JSON calls and ladder levels, caveats and example questions.</summary>
    public static string Render(string name, IReadOnlyDictionary<string, string?> arguments)
    {
        Playbook playbook = Find(name) ?? throw new ArgumentException($"Unknown playbook {name}.", nameof(name));
        var text = new StringBuilder();
        text.Append("# ").AppendLine(playbook.Title).AppendLine();
        text.Append("Goal: ").AppendLine(playbook.Goal).AppendLine();
        if (playbook.Arguments.Count > 0)
        {
            text.AppendLine("Arguments:");
            foreach (PlaybookArgument argument in playbook.Arguments)
                text.Append("- ").Append(argument.Name).Append(": ").AppendLine(Given(arguments, argument.Name) ?? $"not given ({argument.Description})");
            text.AppendLine();
        }
        text.Append("Steps (investigation ladder: ").Append(Ladder).AppendLine("). Run them in order and stop once the question is answered:");
        int number = 0;
        foreach (PlaybookStep step in playbook.Steps)
        {
            if (!ToolRegistry.Has(step.Tool)) continue;
            ToolCallDto call = Call(playbook, step, arguments);
            var rendered = new JsonObject { ["tool"] = step.Tool, ["arguments"] = JsonNode.Parse(((JsonElement)call.Arguments).GetRawText()) };
            text.Append(++number).Append(". Level ").Append(step.Level).Append(", ").Append(step.Tool).Append(": ").AppendLine(step.Why);
            text.Append("   ").AppendLine(rendered.ToJsonString(Readable));
        }
        text.AppendLine();
        text.AppendLine("After every answer, follow its nextCalls (exact arguments with a cost hint). { pending, jobId } means pix_job_wait with that jobId, then repeat the call; a resultRef is read with pix_result_read.");
        text.AppendLine();
        text.AppendLine("What these numbers do not mean:");
        foreach (string caveat in playbook.Caveats) text.Append("- ").AppendLine(caveat);
        text.AppendLine();
        text.AppendLine("Example questions:");
        foreach (string question in playbook.ExampleQuestions) text.Append("- ").AppendLine(question);
        return text.ToString();
    }

    /// <summary>The executable call of one step with the prompt arguments substituted.</summary>
    internal static ToolCallDto Call(Playbook playbook, PlaybookStep step, IReadOnlyDictionary<string, string?> arguments)
    {
        JsonObject template = JsonNode.Parse(step.Arguments)!.AsObject();
        var result = new JsonObject();
        foreach ((string key, JsonNode? value) in template)
        {
            if (key == "$selection")
            {
                if ((Given(arguments, "scope") ?? Given(arguments, "eventRef")) is string scope) result["scope"] = Embed(scope);
                else result["markerPathPrefix"] = Given(arguments, "markerPathPrefix") ?? Placeholder(playbook, "markerPathPrefix");
                continue;
            }
            if (value is JsonValue scalar && scalar.TryGetValue(out string? text) && text.StartsWith('$'))
            {
                bool optional = text.EndsWith('?');
                string name = text[1..].TrimEnd('?');
                if (Given(arguments, name) is string given) result[key] = Embed(given);
                else if (!optional) result[key] = Placeholder(playbook, name);
                continue;
            }
            result[key] = value?.DeepClone();
        }
        return new ToolCallDto(step.Tool, JsonSerializer.SerializeToElement(result));
    }

    private static string? Given(IReadOnlyDictionary<string, string?> arguments, string name)
    {
        if (arguments.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)) return value.Trim();
        if (name != "handle") return null;
        // A handle can be read from a reference argument.
        foreach (string source in new[] { "eventRef", "scope" })
            if (arguments.TryGetValue(source, out string? reference) && Embed(reference ?? "") is JsonObject parsed
                && parsed["handle"] is JsonValue handle && handle.TryGetValue(out string? id) && !string.IsNullOrWhiteSpace(id))
                return id;
        return null;
    }

    private static JsonNode Embed(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try { if (JsonNode.Parse(trimmed) is JsonNode parsed) return parsed; }
            catch (JsonException) { }
        }
        return JsonValue.Create(value)!;
    }

    private static string Placeholder(Playbook playbook, string name)
        => playbook.Arguments.FirstOrDefault(a => a.Name == name) is { } argument ? $"<{argument.Description.TrimEnd('.')}>"
            : Placeholders.TryGetValue(name, out string? text) ? text : $"<{name}>";
}
