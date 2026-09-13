# Investigation ladder

pixmcp exposes more than a hundred tools, but most questions need one or two of them. Start at
the top of the ladder and stop as soon as the question is answered. Each level is cheaper to
read than the next one and points at it through `nextCalls`.

| Level | What it returns | GPU capture | Timing capture | DirectX dump |
|---|---|---|---|---|
| 0: verdicts | One answer with evidence, insights and nextCalls, sized for inline use | `pix_gpu_overview`, `pix_gpu_bottleneck` | `pix_timing_overview`, `pix_timing_verdict` | `pix_dump_triage` |
| 1: composites | Data joined for one question | `pix_gpu_inspect_event`, `pix_gpu_rollup`, `pix_gpu_pipelines`, `pix_gpu_queue_overlap`, `pix_gpu_resource_timeline`, `pix_gpu_compare`, `pix_gpu_compare_changes`, `pix_gpu_drpix_run` | `pix_timing_gpu_summary`, `pix_correlate` | |
| 2: scoped enumerators | Paged rows inside a scope or marker path | `pix_gpu_events`, `pix_gpu_timing_tree`, `pix_gpu_timing_events`, `pix_gpu_counters_read`, `pix_gpu_occupancy`, `pix_gpu_bubbles`, `pix_gpu_resources`, `pix_gpu_resource_uses`, `pix_gpu_shaders`, `pix_gpu_shader_uses` | `pix_timing_events`, `pix_timing_tree`, `pix_timing_hotspots`, `pix_timing_calltree`, `pix_timing_submissions`, `pix_timing_thread_switches` | `pix_dump_queues`, `pix_dump_events`, `pix_dump_journal`, `pix_dump_page_faults` |
| 3: raw | SQL, source code and full result reads | `pix_gpu_sql`, `pix_gpu_shader_code`, `pix_gpu_pipeline_state`, `pix_result_read` | `pix_timing_schema`, `pix_timing_sql`, `pix_result_read` | `pix_dump_event` |

## Rules at every level

- References (`eventRef`, `resourceRef`, `shaderRef`) are JSON objects. Copy them from results.
  `scope` is an eventRef that selects the event and its descendants; `markerPathPrefix` selects
  subtrees by marker path.
- `{ pending, jobId }` means a replay or query job is still running. Call `pix_job_wait` with that
  jobId, then repeat the call. A client that sends a progress token with the call receives
  `notifications/progress` while the call waits.
- A `resultRef` is read with `pix_result_read`; use `mode: outline` first on large results.
- Every `nextCalls` entry is an executable call with a cost hint: `cached`, `query`, `job`,
  `replay` or `pixtool`.
- Replay timing measures this machine replaying the capture. Nested marker sums and replay queue
  spans are not application frame latency.
- Counter rows for markers are PIX rounds collected separately. Never add them to event rows.
- Heuristic answers (`pix_gpu_bottleneck`, `pix_timing_verdict`, overview insights) always carry
  their thresholds and a confidence. Read them before acting.
- SQL is read-only and runs off the PIX thread. Put `ORDER BY` before paging with `offset`.

## Playbook prompts

The server lists these playbooks as MCP prompts. Each one renders numbered steps with exact JSON
calls, the ladder level of every call, what the numbers do not mean, and example questions.
Prompt arguments are strings; pass references as JSON text. Prompts whose tools are disabled by
`PIXMCP_TOOLSETS` are not listed.

| Prompt | Question | Arguments | First call |
|---|---|---|---|
| `pix_frame_budget` | Where does the GPU frame go? | handle, eventRef | `pix_gpu_overview` |
| `pix_regression` | What changed or regressed between two captures? | baselineHandle, handle, markerPathPrefix, eventRef | `pix_gpu_compare` |
| `pix_dispatch_slow` | Why is this dispatch or draw slow? | handle, eventRef | `pix_gpu_inspect_event` |
| `pix_cpu_vs_gpu` | Is the recorded run CPU, GPU or sync bound? | handle | `pix_timing_verdict` |
| `pix_crash_triage` | What caused the hang, crash or device removal? | handle | `pix_dump_triage` |
| `pix_async_overlap` | Do the GPU queues overlap? | handle, eventRef | `pix_gpu_overview` |
| `pix_bandwidth_hogs` | Which resources limit memory bandwidth? | handle, scope, markerPathPrefix | `pix_gpu_resources` |
| `pix_fill_vs_vertex` | Is a pass fill-rate or vertex bound? | handle, scope, markerPathPrefix, eventRef | `pix_gpu_bottleneck` |
| `pix_sql_investigation` | How do I answer this with SQL? | handle | `pix_timing_schema` or `pix_gpu_sql_tables` |
| `pix_gpu_bottleneck` | What limits this pass? | handle, scope, markerPathPrefix | `pix_gpu_bottleneck` |

## Smaller tool lists and text

`PIXMCP_TOOLSETS` limits the advertised tools to named toolsets (`gpu`, `timing`, `dump`,
`device`, `csv`, `gpusql`, `drpix`, `shader`; `session` is always on). A client that only works
on GPU captures can start the server with `PIXMCP_TOOLSETS=gpu,gpusql,drpix`.
`PIXMCP_TEXT_CONTENT=summary` sends a short text block next to the full structured result, for
clients that read `structuredContent`.
