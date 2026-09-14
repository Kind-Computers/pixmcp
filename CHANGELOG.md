# Changelog

All notable changes to pixmcp are recorded here. The 2.0 roadmap breaks the wire contract
deliberately; every break is listed under Changed or Removed so clients can migrate in one pass.

## [Unreleased] - 2.0.0

The version is 2.0.0 from the Phase 0 (contract and correctness) commit on; later roadmap phases
add to this section until the release is tagged.

### Added

- `DurationDto` (`ns`, `ms`, `percentOfQueueSpan`, `percentOfQueueSum`, `percentOfParent`, `rank`)
  and per-queue `QueueTotals` (`busyNs`, `spanNs`, `idleNs`, `sumOfRootsNs`, `rootsOverlap`,
  timed/untimed counts) on `pix_gpu_timing_tree`, `pix_gpu_timing_events`, `pix_gpu_overview`
  and the timing-collect job summary, plus a `denominators` object naming every denominator.
- Timing-tree `semantics` (`measured`, `derivedSum`, `mixed`, `untimed`), `childSumEopNs`,
  `childrenExceedMeasured`, `childOverflowNs`, `untimedChildren`, `repaired`, `topStartNs` and
  `executionNs`; `repairedLinks` and `untimedEvents` on the tree response.
- `pix_gpu_timing_tree` `sortBy` (`inclusive`, `self`, `childCount`, `index`, `topStart`) and
  `minSelfNs`; a `pix_gpu_timing_events` nextCall (ordered by `eopStart`) on nodes whose children
  exceed the measured span.
- Event kinds `work` (draw, dispatch, executeIndirect), `resolve` and `label`; overview kind
  counts include `resolve`, `label` and `other`.
- `pix_gpu_overview` per-queue replay `totals`, ranked `topPasses`/`topDraws` carrying `kind`,
  `semantics`, `eop` and `exec`, and targeted nextCalls (`pix_gpu_timing_tree` scoped to the top
  pass sorted by self time).
- `pix_gpu_overview` v2: a `capture` block (path, event total, queue count, `adapter` vendor,
  `frames` delimited by Present calls with the presenting queue and frame assignment), every
  event kind per queue, `topPasses` with `self` time, child overflow, `childCount` and
  `workCount`, `topDraws` with the captured call text in `parameters.raw`, a work-event EOP
  `histogram` (non-empty 1-2-5 buckets, 10 us to 10 ms), per-frame busy time with nearest-rank percentiles
  on multi-frame captures, and `frameIndex` to restrict passes, work events and the histogram to
  one frame.
- `pix_gpu_overview.insights`: up to eight findings, warnings first (`vendor_mismatch`,
  `children_exceed_measured`, `untimed_events`, `frame_variance_high`, `queue_idle_high`,
  `single_pass_dominates`, `pass_self_time_high`, `long_tail_draws`, `no_markers`,
  `executeindirect_heavy`, `many_barriers`) with evidence, implication and follow-up calls; a
  reflected tool registry drops any suggestion that names a tool or parameter this build lacks.
- `pix_gpu_inspect_event` v2: `kind`; `parameters` (the API call parsed into named arguments,
  XML element or positional form, with `workItems`); timing in context (`rankInQueue`,
  `rankInParent`, `siblings`, `exec`, `perWorkItem`, `pipelinedWithPrevious`, `previousGapNs`);
  `targets` (bound render targets and depth-stencil views with pixel counts and EOP nanoseconds
  per megapixel); `counters` from collections already cached on the handle; `occupancy` and `hf`
  pointers to the tools that collect them; and `hints` (`zero_work`, `tiny_dispatch`,
  `pipeline_latency`, `pipelined`, `dominant_in_parent`, `large_rt_fill`) with evidence,
  implication and follow-up calls.
- `pix_gpu_rollup`: one-call aggregation of replay timing, and optionally counters, by marker,
  marker path, marker depth, shader, pipeline, kind, queue, command list or API name. Groups carry
  duration sums, avg/min/max, nearest-rank p50/p95, semantics and a representative event; marker
  depth adds a `(no marker)` remainder that reconciles to the sum of roots; `format = table` works.
- `pix_gpu_pipelines` ranks pipelines by replay time or use count. Stable `shaderKey`
  (`hash:STAGE:HASH`) and `psoKey` identities; `pix_gpu_shaders` `sortBy` with `gpuTimeMs` and
  `shaderKey`; `pix_gpu_shader_uses` by `shaderKey`; `pix_gpu_events` `mode = count` and
  `mode = histogram` with `bucketBy`.
- `pix_gpu_counters_read` v2: rows from every queue with the event's replay `eop`/`exec` durations
  (`includeTiming`, default true), `normalize` (`perMs`, `perThreadGroup`, `perPixel`) with
  `normalizeReason`, `derived` ratio columns, `sortBy` (`index`, `name`, `eop`, `counter`), and
  `groupBy` returning rollup counter aggregates over the same collected set.
- GPU capture SQL: `pix_gpu_sql_populate` materialises families (`core`, `timing`, `shaders`,
  `counters`, `resources`, `resourceUses`, `psos`) into a private per-handle SQLite store;
  `pix_gpu_sql` runs guarded read-only statements or named library queries with pre-bound scope
  parameters; `pix_gpu_sql_tables` documents families, tables, views and queries; and
  `pix_gpu_sql_export` streams rows to CSV or JSON. New error codes `sql_tables_not_populated`,
  `sql_capacity_exceeded`, `sql_store_closed` and `sql_store_busy`, and the variable
  `PIXMCP_GPUSQL_MAX_BYTES`. Occupancy, high-frequency counter and Dr. PIX families are not
  materialised yet.
- `pix_gpu_occupancy` and `pix_gpu_hf_counters` read occupancy and high-frequency samples collected
  with the timing pass when PIX provides them (`source`, `timingPassProbe`, `forceStandalone`), add
  time-weighted window statistics with a `clockCheck`, per-event occupancy points (`eventRef`),
  `groupBy` event and marker rows, `setName`, and a heuristic `utilizationRanking`;
  `pix_gpu_inspect_event` `occupancy` and `hf` sections report collected data for the event. On the
  NVIDIA development machine PIX reports both as not implemented (0x80004001), so unit tests alone
  cover the window statistics.
- `pix_gpu_queue_overlap` and `pix_gpu_bubbles`: per-queue busy, idle and solo-busy replay time,
  pairwise overlap verdicts, the critical-path queue, and idle gaps with the events around them,
  their likely cause and totals per cause; `pix_gpu_overview` gains an `overlap` section and an
  `async_not_overlapping` insight.
- `pix_gpu_resource_timeline` and resource sizing: `pix_gpu_resources` gains `estimatedBytes`,
  `estimateMethod`, `heapKind`, `trafficBytes`, filters (`name`, `pixelFormat`, `flags`, `minBytes`,
  `usedAs`, `scope`, `markerPathPrefix`), `sortBy` and `extra.totals`/`extra.scopeSummary`; resource
  uses carry `access`, `evidence`, `stage` and event coordinates, barrier arguments carry their
  states, and `pix_gpu_resource_uses` filters by `access` and sorts. The GPU SQL `resources` table
  gains `estimated_bytes`, `estimate_method` and `heap_kind`.
- Dr. PIX overhaul: experiment `family`, `whatItProves` and `supportsRanges`; `pix_gpu_drpix_run`
  gains `categories`, `families`, `perEvent`, `maxRuns` and one range per matching marker, and
  returns structured records, baseline/experiment timing pairs, savings with implications, a pivot
  table, a summary and notes. Jobs publish `partialResultRef` while running and keep finished runs
  as a partial result when cancelled or failed.
- Regression workflow: `pix_gpu_compare` gains scoped and frame comparisons (including two scopes
  of one capture), busy `totals`, `byMarkerPath` rollups, `repeats` with a noise floor, provenance
  mismatch `warnings`, HLSL `codeDiffs`, ordinal matching with match `confidence`, and binding
  comparison as sets; `pix_gpu_compare_changes` filters by marker path, kind, queue, section,
  structural changes, noise and confidence.
- `pix_gpu_bottleneck`: one-call limiter classification for a scope from timing, counters,
  occupancy, high-frequency counters and Dr. PIX evidence scored by the embedded heuristic
  `bottleneck-rules.json`, with confidence, alternatives, evidence, rule results, recommendations,
  coverage and a detail snapshot; results are cached until analysis stops.
- `pix_gpu_overview` `format = table` returns `topPasses` and `topDraws` as positional tables with
  columns and a legend; the output schema accepts both shapes for those sections. `brief = true`
  also drops capability reasons and notes, denominators and zero kind counts (the fixture's brief
  overview is under 8 KB, the default under 12 KB).
- Shaping parameters `format` (`objects`|`table`), `brief`, `topN` and `maxStringLength` on
  `pix_gpu_events`, `pix_gpu_timing_events`, `pix_gpu_timing_tree` (table = pre-order flatten
  with `parentEventIndex`/`depth`), `pix_gpu_counters_read`, `pix_gpu_shaders`,
  `pix_gpu_resources`, `pix_timing_events` and `pix_timing_hotspots`; `brief` and
  `maxStringLength` on `pix_gpu_overview` and `pix_gpu_inspect_event`. Table responses carry
  `columns`, positional `rows`, a `legend` with reference recipes, `truncatedRows` and
  `truncatedStrings`; output schemas accept both shapes.
- `cost` hints (`cached`, `query`, `replay`, `pixtool`, `job`) on every `nextCalls` entry,
  including error recovery calls.
- Provenance dedup: the first response per handle carries the block plus `fingerprint` and
  `changed`; a repeated block becomes `{ provenanceRef, unchanged: true }` unless the block
  changed or the call passes the new `includeProvenance = true` parameter.
- Section-level pending: on a cold capture `pix_gpu_overview` returns queues, kinds and
  capabilities with `timing: { pending, jobId, retry, job, nextCalls }`, and
  `pix_gpu_inspect_event` returns the event record and marker path with pending `timing`,
  `pipeline` and `bindings` sections plus a `preparation` section; the top level is not
  `pending`.
- Input-schema `examples` for `handle`, `eventRef`-shaped parameters, `scope`, `shaderRef`,
  `shaderKey`, `resourceRef`, `markerPathPrefix`, `format` and `kind`.
- Job status carries `resultState` (`available`, `evicted`, `retentionFailed`, `none`),
  `resultError` and `origin` (the tool call that started the job). A job whose result could not
  be retained stays `succeeded` with `resultState = retentionFailed`; an evicted result is
  reported as `evicted` instead of silently dropping `resultRef`. `nextCalls` repeat the origin
  call whenever the result must be recomputed.
- Stable error codes with recovery calls: `unknown_job` (lists jobs), `job_already_finished`
  (status and result read), `server_shutting_down`, `analysis_settings_conflict` (offers
  `pix_gpu_analysis_stop`), `preparation_unavailable` (retryable, repeats the call) and the
  existing `result_expired`, `result_capacity_exceeded` (now retryable, carries the originating
  call and `pix_info`), `invalid_arguments`.
- A finished job's result is protected from storage-pressure eviction for 30 seconds, so
  `pix_job_wait` can always hand it over; the store fails the newcomer with
  `result_capacity_exceeded` instead.
- Query tools whose preparation just finished are admitted to the worker with at least one extra
  second, so a ready prerequisite is no longer lost to a busy queue as `worker_busy`.
- One preparation gate per handle: parallel callers of the same replay get one job;
  `pix_gpu_analysis_start`, `pix_gpu_timing_prepare` and `pix_gpu_counters_prepare` return the
  canonical job whoever started it (analysis status, timing summary, counter summary), and a
  start with different adapter/flags while one is queued or running fails immediately with
  `analysis_settings_conflict`. Query tools join any running job that makes progress toward
  their preparation (analysis, timing, resources, another inspection variant) instead of
  queueing a second replay behind it.
- `pix_gpu_inspect_event` prepares one job per set of needed state (`inspection`,
  `inspection:timing`, `inspection:bindings`, `inspection:bindings,timing`); an empty or unknown
  `sections` value is `invalid_arguments`.
- Shutdown fails queued worker calls with `server_shutting_down` instead of running them; a
  native call still running five seconds later is logged and abandoned.
- Native tests resolve `PIX_TEST_CAPTURE` and `PIX_TEST_TIMING_CAPTURE` relative to the
  repository root when the value is not absolute.
- `pix_result_read` `mode=outline` (shape without values: kinds, counts, bytes, key samples),
  `fields` (server-side field selection over array elements) and `where` (up to 8 ANDed
  clauses with eq/ne/gt/ge/lt/le/in/contains/startsWith/exists); projected reads report
  `projection { fields, where, scanned, matched, unevaluated }` and `total` becomes the
  matched count. Deferred results offer the outline call first.
- `invalid_pointer` names the nearest container, its keys (25 of N) and an outline call;
  `result_expired` carries the originating tool call for the last 256 expired results;
  `result_capacity_exceeded` reports usage and names the budget variables.
- `ServerOptions`: `PIXMCP_INLINE_RESULT_BYTES` (default 32768, 1024..max),
  `PIXMCP_MAX_RESULT_BYTES`, `PIXMCP_RESULT_MEMORY_BYTES`, `PIXMCP_RESULT_DISK_BYTES` and
  `PIXMCP_RESULT_DIR` are validated once at startup (one message per bad variable, exit 1
  before protocol output) and reported by `pix_info.options` with their sources.
- `tools/list`, `resources/list`, `resources/templates/list` and `prompts/list` are sorted
  by name, memoised per tool and carry `ttlMs` (one hour) with `cacheScope = private`;
  resource reads carry `ttlMs = 0`.
- Vendor identity (`GpuVendor`: nvidia, amd, intel, qualcomm, warp, unknown) on queues,
  `pix_gpu_info.vendor`, `pix_gpu_overview.capture.adapter`, analysis status (`replayVendor`,
  `captureVendor`), analysis and device adapter lists, and replay provenance (`adapterName`,
  `vendor`, `captureVendor`, `vendorMismatch`, `pixBuild`).
- Counter units: every counter reports `unit`, `unitSource`, `unitConfidence`, `range` and
  `aggregationHint` inferred from its format, name and description; high-frequency listings
  carry descriptions and units.
- Counter presets: `pix_gpu_counters_list.preset` filter and `extra.presets`;
  `pix_gpu_counters_prepare` / `pix_gpu_counters_read` accept `preset` instead of `counterIds`
  (resolved for the capture's vendor after analysis is prepared; zero matches is
  `invalid_arguments` with a list call). Presets: utilization, aluUtilization, perStageAlu,
  occupancy, stalls, cache, memoryBandwidth, fixedFunction, pipelineStatistics, depthOcclusion.
- Compatibility registry (`Resources/compatibility-notes.json`): capabilities gain
  `systemMonitorHardwareCounters`, `staticShaderProfiling` and `drPixVendorExperiments`, unprobed
  states come from the registry, every capability carries `notes`, `pix_info.pix.notes` and
  `pix_info.pix.build` (the PIX assembly file version), and the counter, occupancy and
  high-frequency tools attach `extra.notes` for the capture's vendor.
- Timing and counter rows are read with PIX's event-indexed bulk API (`GetQueueDataCount` /
  `GetQueueData`) and fall back to the per-event API when the counts disagree or the bulk call
  is unavailable; every queue reports `readback` (`bulk` or `perEvent`), the reason, call counts
  and failed reads (timing-prepare summary, `pix_gpu_timing_events.extra.readback`,
  `pix_gpu_counters_read.extra.coverage`). One failed cell nulls one value instead of discarding
  the queue; eight consecutive same-HRESULT failures probe the per-event API once so a dataless
  run continues while an unreadable counter is marked unavailable. `PIXMCP_VERIFY_BULK_READBACK=1`
  compares 50 sampled bulk rows per queue against the per-event API and reports mismatches in the
  job messages and `readback.verify`.
- A counter set that is a subset of an already collected, fully materialised set is projected
  from it without a replay (`extra.collection { key, source: exact | projectedFromSuperset,
  supersetKey, replayed }`); supersets still replay.
- Counter rows carry `rowKind` (`marker` = PIX's own measurement over the marker span, collected
  in separate playback rounds; `event` = leaf) and `descendantDataEvents`; the response states
  the rounds rule (`extra.rollupSemantics`, `rollupAvailable`) and warns when a page mixes both
  kinds.
- Every tool declares `title`, `openWorldHint = false` (except `pix_device_connect`) and explicit
  `readOnlyHint`/`destructiveHint`/`idempotentHint` values; tools that may replay another
  handle, spawn pixtool or write files (`pix_gpu_compare`, `pix_gpu_preview`,
  `pix_gpu_screenshot`, `pix_gpu_export_cpp`) are no longer read-only, and the 17 query tools
  that start analysis as a job say so in their first sentence.
- `pix_gpu_compare.stopBaselineAnalysis` (default true); false fails fast with `analysis_active`
  instead of stopping the baseline's analysis.
- Every tool parameter and every property of a parameter record (`EventRef`, `ShaderRef`,
  `ResourceRef`, `QueuePair`, `EventPair`, `DumpEventRef`, `ImageCrop`, `WhereClause`) carries a
  description; descriptions of non-trivial defaults name the default.
- `Tools.PrepareOutputPath`: every file-writing tool (`pix_gpu_screenshot`, `pix_dump_blobs`,
  `pix_timing_save`, `pix_capture_upgrade`, `pix_result_export`) refuses the private result
  storage, requires an existing parent directory, reports `file_exists` unless `overwrite=true`.
- The hard response budget now counts text blocks and base64 image bytes; inline images are
  shrunk to the budget (`budgetLimited`), `pix_gpu_preview_bytes` pages never cross the
  deferral threshold, and MCP resources (`pix://handles`, `pix://jobs`, the new
  `pix://results/{resultRef}`) return the same envelopes and error objects as the tools.
- Every error now carries a registered code (`PixErrors.Codes`, documented in the README
  table): `unknown_handle`, `wrong_handle_kind`, `invalid_reference` (out-of-range queue,
  event, shader, resource, heap, view, node, wave, table, blob and experiment ids, with the
  listing call in `nextCalls`), `unknown_counter`, `counter_read_failed`, `analysis_required`
  (E_NOT_VALID_STATE on a capture whose analysis is not started, with the start call),
  `unavailable_shader_data`, `pix_unavailable`, `file_not_found` and `invalid_arguments` for
  every argument check that used to surface as a bare `pix_error`.

- PIX version gating. `Directory.Build.props` is the single source of `PixPreviewMinDate`
  (2606.15) and `PixVerifiedVersion` (2606.18-preview); the build reads the install's
  `version.xml` and assembly file version into `AssemblyMetadata`, fails when several installs
  are eligible unless `/p:PixMcpPickNewestPix=true`, and fails on an install newer than the
  verified one unless `/p:PixMcpAllowUnverifiedPix=true`. At runtime discovery errors on
  ambiguity unless `PIXMCP_PIX_PICK_NEWEST=1`; `pix_info.pix` reports `installVersion`,
  `builtAgainst`, `verifiedRange`, `compatibility` (`match`, `newerUnverified`,
  `olderThanBuild`, `mismatch`, `unknown`), `apiSurface` type probes, `loggerAttached` and
  `loggerError`; the server exits with code 2 on `mismatch` or `olderThanBuild` unless
  `PIXMCP_PIX_STRICT=0` (and on `newerUnverified` with `PIXMCP_PIX_STRICT=1`).
- `pix_api_mismatch` is raised for `MissingMethodException`, `MissingFieldException`,
  `TypeLoadException`, `EntryPointNotFoundException` and `BadImageFormatException` from the
  PIX assembly, with a `pix_info { probe: true }` nextCall. Capability states
  `resourceContents`, `pixelHistory` and `captureShaderStepping` come from type probes of the
  loaded assembly (naming its file version) instead of hard-coded verdicts.
- `pix_timing_hotspots` and `pix_timing_calltree` take `efficiencyClass` (samples taken on cores
  of that `PhysicalCores.EfficiencyClass`); on heterogeneous CPUs coverage reports
  `samplesByEfficiencyClass` and each hotspot `inclusiveByEfficiencyClass`. A capture without
  core classes rejects the filter with `timing_schema_unsupported`, and an unrecorded class with
  `invalid_arguments` listing the recorded ones.
- `pix_timing_overview.sections`: `capture` (analysis-relevant CaptureData keys), `dataQuality`, `gpu`,
  `frames`, `cores`, `modules` and `vram` summaries computed with the named query library, `insights` (up to eight findings with
  evidence, implication and follow-up calls) and `unavailable` (sections the capture cannot supply).
- `pix_timing_sql` and `pix_timing_schema`: read-only SQL over a timing capture's PixStorage
  database with pre-bound window and fact parameters (`rangeMode` full or reliable), documented
  columns, joins and caveats (embedded `pixstorage-docs.json`, observed on 2606.18), capability
  probes, capture facts, and a named query library: `capture_facts`, `dropped_data`,
  `module_symbols`, `core_efficiency`, `gpu_busy_per_queue`, `submit_latency_per_thread`,
  `gpu_hardware_queues`, `frames_vsync`, `frames_present`, `thread_summary`,
  `context_switches_per_thread`, `context_switch_waits`, `ready_thread_latency`,
  `cpu_execution_rollup`, `cpu_markers`, `counters_bucketed`, `vram_budget`, `file_io_summary` and
  `memory_summary`. Named query parameters can be required; percentiles use the nearest-rank method.
- `pix_timing_gpu_summary`: recorded GPU work per API command queue without replay. Each queue
  reports valid submissions and `invalidReasons`, `totals` (busy as the union of execution
  intervals clipped to the window, idle, span, summed execution, `busyPercentOfWindow`),
  submit-latency and execution statistics (nearest-rank p50/p95), `topSubmittingThreads` and
  `longestExecutions` with `submissionRef`. The response adds hardware queue busy time, VSync pacing
  per monitor and `cpuGpuCausality` (whether CPU marker to GPU work links were recorded).
- `pix_timing_tree`: a thread's recorded PIX CPU events (`threadRowId`) or a queue's GPU-side
  events (`queueId`) nested by recorded level and interval and aggregated by marker path, with
  occurrences, clipped inclusive and self `DurationDto`s (shares of lane span, top-level sum and
  parent), occurrence duration statistics, execution/stall sums from `PixCpuExecutionTimes`,
  malformed-nesting flags, `parentPath`/`depth`/`sortBy`/`minSelfNs`, paging, and follow-ups to the
  slowest occurrence. `pix_timing_overview` suggests it for the thread with the most PIX events, and
  `pix_timing_gpu_summary` for the busiest queue when GPU-side markers exist.
- `pix_timing_verdict` (experimental until a bottleneck fixture validates its thresholds): recorded
  frames from GpuFrame presents, the render thread's repeated top-level PIX event, VSync markers or
  submission cadence, with per-frame GPU busy time and render-thread on-CPU, blocked and
  ready-not-running time rebuilt from context switches and ready events. Ordered heuristic rules
  (`gpuBound`, `unknown`, `presentBound`, `cpuBound`, `syncBound`, `waitBound`, `contended`,
  `balanced`) are returned with their thresholds; the response adds the dominant verdict and
  confidence, frame and ready-latency statistics, the longest frames, wait reasons with probable
  KWAIT_REASON names, per-queue busy shares, a VRAM budget check, a paged per-frame table and
  follow-up calls. `pix_timing_gpu_summary` suggests it.
- `pix_correlate`: a GPU capture's timed marker paths joined to a timing capture's recorded PIX
  marker paths by name (full path, then a unique leaf; case, whitespace, legacy PIX prefix and
  trailing numbers normalized and reported), with replayed inclusive EOP time, recorded occurrence
  statistics, submissions inside the recorded occurrences, the emitting threads' blocked and ready
  time, `ratioRecordedToReplay`, unmatched paths on both sides, a queue map and an explicit
  `identity` statement. Results are owned by both handles. The checked-in fixture captures record
  different marker names on the GPU and timing sides, so native validation covers unmatched lists
  and queue mapping until the fixture emits shared GPU marker names.
- Read-only SQL engine (`Pix/Sql`): `ReadOnlySqlite` (private read-only connection, `query_only`,
  statement budgets, interruption), `SqlStatementGuard` (SQLite limits plus an authorizer that
  allows only SELECT, READ, RECURSIVE and non-file functions and records the tables read) and
  `SqlQuery` (one statement, SQLite's read-only verdict, named `$`/`@`/`:` parameters, server
  pre-bound values, JSON-safe cells, maxRows/maxBytes/maxStringLength truncation with an exact
  offset continuation, `countTotal`, `EXPLAIN QUERY PLAN`). New error codes `sql_syntax_error`,
  `sql_execution_error`, `sql_forbidden`, `sql_not_read_only`, `sql_multiple_statements`,
  `sql_missing_parameter`, `sql_invalid_parameter`, `sql_timeout` and `sql_interrupted`. The
  tools that expose it arrive with R11.
- `scripts/check_versions.py` (hosted CI) keeps README, CLAUDE.md and the sources on the
  verified PIX version; `.github/pull_request_template.md` asks for a CHANGELOG entry.
- Static shader profiling for AMD and Intel targets (R25): `pix_shader_targets` lists the
  offline-compiler targets of the PIX install, and `pix_gpu_shader_static_profile` compiles inline
  HLSL or a captured shader (with its pipeline state and root signature) as a job whose result
  summarises instruction mix, register pressure, loops, loop-weighted hot spots, source lines and
  compiler resource usage, with every instruction under `/detail`. Compile errors are data. The
  `staticShaderProfiling` capability reports `supported` whenever the API is present. Intel's offline compiler plugin writes `ASD_CompilationReport_*.md` files into the working
  directory, so the server points it at `%TEMP%/pixmcp-compiler-reports` while PIX loads and runs vendor
  compilers and resolves client paths against the directory it started in.
- Ten playbook prompts (`pix_frame_budget`, `pix_regression`, `pix_dispatch_slow`,
  `pix_cpu_vs_gpu`, `pix_crash_triage`, `pix_async_overlap`, `pix_bandwidth_hogs`,
  `pix_fill_vs_vertex`, `pix_sql_investigation`, `pix_gpu_bottleneck`) with exact JSON calls,
  ladder levels, caveats and example questions, plus `docs/investigation-ladder.md`.
- Progress notifications: a tool call that carries a progress token receives
  `notifications/progress` while it waits on a job (at most four per second, with a final report).
- `PIXMCP_TOOLSETS` limits the advertised tools to named toolsets (`gpu`, `timing`, `dump`,
  `device`, `csv`, `gpusql`, `drpix`, `shader`; `session` is always on). A disabled tool fails
  with `tool_disabled`, prompts that need it are hidden (`prompts/get` fails with the same code),
  and `pix_info.toolsets` reports the configuration.
- `PIXMCP_TEXT_CONTENT=summary` sends a text block of at most 512 bytes with each successful
  result while `structuredContent` stays complete; `pix_info.textContent` reports the mode.
- Capture and replay options (R27). `pix_gpu_analysis_start` documents and validates the eight
  `PIX_ANALYSIS_FLAGS` plus `NONE` (short or prefixed names, `items.enum` in the input schema), and
  analysis status and replay provenance decode them (`flagsDecoded`, `flagsSource`).
  `pix_device_timing_capture_start` gains `preset`, the allocation event levels, page faults, file
  I/O stacks, tracked functions, kernel image merging, all-process stacks and the remaining option
  parts (video and its source, CLR data, capture ETL, circular, COM path, GPU-only events, minimal
  instrumentation), and returns `optionParts` and notes. `pix_device_take_gpu_capture` gains
  `delimiter`, `captureKey` and a screenshot `thumbnail` preview artifact.
- pixtool integration (R28): `pix_gpu_preview` selects an exact event with `eventRef` (pixtool
  `--global-id`, checked against the capture's GPU ids on first use and reported as
  `selection.globalIdMapping`) and saves up to eight `targets` from one replay. The new
  `pix_gpu_subcapture` cuts a scope into a smaller `.wpix` with `recapture-region` and opens it with
  `derivedFrom`. pixtool jobs share one runner that copies the capture once and chains every command
  after a single `open-capture`.
- Dump triage completeness (R29): `pix_dump_triage` adds PIX's diagnosis, D3D runtime journal
  failures, queue hardware status, fault and hang GPU state tables, per-queue event status counts, a
  `maxEvents` walk budget with truncated coverage and continuations, a correlation bonus for queues
  with page faults or in-progress events, and `d3dState` coverage (unsupported). `pix_dump_info`
  returns the shared `diagnosis`, and `pix_dump_queues` rows add `maxHardwareSeverity`.

### Changed

- `pix_gpu_compare` refuses a capture compared with itself unless the scopes or frames differ;
  bindings no longer count as capture identity (a rebound register is a field change, reordering
  and descriptor-heap indices are not); equal-length arrays diff by position, so field paths name
  the changed element.
- `pix_gpu_drpix_run` results list `runs[]` (with `records`, `timing` and `rangeIgnored`) and
  `ranges[]` instead of `results[]` with flat metrics and `experimentsRun`;
  `pix_gpu_drpix_experiments` items are `DrPixExperimentDto` rows with families.
- `pix_gpu_resources` replays the capture when `scope`, `markerPathPrefix`, `usedAs` or
  `sortBy = traffic` is given (it builds the capture-wide resource-use index); other calls stay
  metadata only. Resource-use rows for captured API arguments report `parameter` as the element path
  (for example `pDst/pResource`).
- `pix_gpu_occupancy` collects timing first and uses the timing pass's occupancy when PIX reports it,
  replaying a standalone occupancy collection only as a fallback; `pix_gpu_hf_counters` does the
  same for a counter set. Inspection `occupancy` and `hf` sections are `notCollected` until that
  data exists instead of `notJoined`.
- `pix_gpu_counters_read`: `queueIndex` is optional and omitted means every queue (was queue 0);
  `extra.coverage` is keyed by queue index; `orderByCounterId` is replaced by `sortBy = counter`
  with `sortCounterId`, and `minValue`/`maxValue` filter on `filterCounterId`. `pix_gpu_shader_uses`
  takes `shaderRef` or `shaderKey` with `handle`.
- `pix_gpu_inspect_event`: sections add `targets`, `counters`, `occupancy`, `hf` and `hints`, and
  the default adds `targets` and `hints`. `timing` is a ranked object with `eop` and `exec`
  durations instead of four raw nanosecond fields, and provenance moves to the top level.
  Pending sections are `{ pending, jobId }` with the job in `preparation`. Every inspection
  shares one `inspection` preparation key; the `inspection:timing`, `inspection:bindings` and
  `inspection:bindings,timing` keys are gone.
- One scope convention: `scope` (`EventRef`, event and descendants) and `markerPathPrefix`
  (every marker subtree whose path starts with the prefix) on `pix_gpu_events`,
  `pix_gpu_timing_events`, `pix_gpu_timing_tree`, `pix_gpu_counters_read`, `pix_gpu_occupancy`,
  `pix_gpu_hf_counters`, `pix_gpu_drpix_run`, `pix_gpu_shader_profile`, `pix_gpu_resource_uses`,
  `pix_gpu_shaders`, `pix_gpu_shader_uses` and `pix_gpu_overview`; responses echo the resolved
  selection (`scope`/`extra.scope` with `matchedRoots` and `matchedRootCount`).
- `pix_gpu_drpix_run` takes `scope`/`markerPathPrefix`/`queueIndex` or `wholeCapture=true`
  (no whole-capture default); results carry `range` (first/last event references, GPU ids,
  `workEvents`, `swapped`) and per-result `firstGpuId`/`lastGpuId`. The range handed to PIX is
  the first and last GPU event of the subtree, ordered by moment handle.
- `pix_gpu_shader_profile` takes `handle` plus `scope`/`markerPathPrefix`/`queueIndex`; results
  carry `range` and `scope` instead of `firstEventRef`/`lastEventRef`.
- `pix_gpu_occupancy` and `pix_gpu_hf_counters` accept a selection and report its replay-clock
  `windowNs` once timing is collected (the series stay capture-wide until the occupancy item).
- `pix_gpu_timing_tree.parentIndex` is replaced by `scope` (an `EventRef` whose children form
  the first level); `queueIndex` is optional and defaults to the scope's queue.
- Timing-tree responses: `totalEopNs` is now `queue.sumOfRootsNs`; `inclusiveEopNs`,
  `selfEopNs` and `percentOfQueue` are replaced by the `inclusive` and `self` `DurationDto`s.
- `pix_gpu_timing_events` rows: `eopDurationNs` is replaced by `eop` and `exec` `DurationDto`s
  (`topStartNs`, `topDurationNs`, `eopStartNs` and `eopEndNs` remain raw timestamps); rows gain
  `kind`.
- `pix_gpu_overview` `topPasses`/`topDraws` rows: `eopDurationNs` and `derived` are replaced by
  `eop`, `exec`, `kind` and `semantics`.
- `pix_gpu_overview` v2 shape: `queues[].eventKinds` is `kinds` (every kind listed, including
  `work`), `queues[].timing` is `totals`, the top-level `vendor` moved to `capture.adapter`,
  `topPasses` rows carry `inclusive` and `self` instead of `eop`, `exec` and `kind`, and
  `topDraws` rows add `parameters`.
- `pix_gpu_compare` items: `baselineDerived`/`candidateDerived` are replaced by
  `baselineSemantics`/`candidateSemantics`; `deltaMs` is added.
- The timing-collect job summary reports `totals` per queue instead of `sumEopDurationNs`,
  `spanNs` and `timedEvents` (the sum double-counted markers).
- Marker classification: an event with empty API call text is a marker only when its name is not
  API-shaped and it has children or no GPU id; timed leaf labels are `label` and no longer rank
  as passes.
- Unknown `kind` and `sortBy` values raise `invalid_arguments` (was a bare `pix_error`).
- Replay provenance `timingSemantics` explains `derivedSum` and `mixed` values.
- `pix_gpu_counters_read` no longer takes `firstEventIndex`/`lastEventIndex`; shaping
  parameters replace the ad hoc row filters. `pix_gpu_overview` and `pix_gpu_inspect_event`
  return a partial answer instead of a whole-response `{ pending }` while their preparation
  runs (clients that tested the top-level `pending` flag must check the sections).
- `ToolCallDto` gains an optional `cost` string; clients that compared `nextCalls` entries
  structurally must ignore it.
- Unknown job ids and cancelling a finished job report `unknown_job` / `job_already_finished`
  (was `pix_error`). A job whose result was evicted keeps its status; clients that inferred
  eviction from a missing `resultRef` should read `resultState`.
- Counter metadata no longer carries `unitReason` (replaced by `unitSource`/`unitConfidence`);
  `counterIds` is optional on `pix_gpu_counters_prepare` and `pix_gpu_counters_read` (exactly
  one of `counterIds` or `preset`).
- Renamed: `pix_gpu_timing_collect` is `pix_gpu_timing_prepare`, `pix_gpu_counters_start` is
  `pix_gpu_counters_prepare`, `pix_gpu_counters_collect` is `pix_gpu_counters_read`
  (prepare = start a replay job, read = page cached rows). `pix_gpu_inspect_event.sections`
  values are lowercase (`timing`, `pipeline`, `shaders`, `bindings`; the old casing still binds).
- `pix_gpu_screenshot` writes nothing unless `outPath` is set (it used to write
  `<capture>.screenshot.png` by default) and takes `overwrite`; `pix_dump_blobs`,
  `pix_timing_save` and `pix_capture_upgrade` take `overwrite` and report `file_exists`.
- `pix_handles`, `pix_jobs`, `pix_log`, `pix_close_all`, `pix_gpu_queues`, `pix_dump_queues` and
  `pix_gpu_drpix_experiments` return the standard `{ total, offset, count, items }` page
  envelope instead of a bare array.
- Result ids are opaque (`r-` plus ten random base32 characters) instead of `result-N`;
  clients must not derive one id from another. A malformed `PIXMCP_RESULT_*_BYTES` value
  now fails startup instead of failing the first tool call.

- `pix_timing_events.domain` is `cpu`, `cpuMarkers`, `gpuMarkers`, `gpuSubmissions`, `gpuHardware` or
  `all`; `gpu` is removed because `PixGpuExecution` is empty unless the application emits GPU-side PIX
  events, while real GPU work is in `ApiQueueExecution` (`gpuSubmissions`) and `GpuWorkRange`
  (`gpuHardware`). Rows gain `source`, `submitNs`, `submitLatencyNs`, `commandListCount`,
  `submissionRef`, `hardwareQueueId`, `hardwareQueueName`, `overlapLevel`, `color` and
  `executionTimingMethod`; `processId` can be null (hardware ranges without an API queue); the
  response lists `sources` with their state and matching rows; new filters `queueName` and
  `hardwareQueueId`. CPU execution/stall times come from one `CpuExecutionRowId` lookup per event
  (`rowId`, with an `inconsistent` state when Execution + Stall differs from the duration) and fall
  back to the (EventId, begin, end) tuple match (`tupleMatch`) when the hidden column is absent.
- `pix_timing_overview` capability keys: `gpuEvents` is now `gpuMarkers` and `submissions` is now
  `gpuSubmissions`; new `cpuMarkers` and `gpuHardware`; recorded families carry `rows` and report
  `empty` when their table has no rows. Threads gain `startNs`, `endNs`, `pixEventCount`,
  `contextSwitchCount` and `markerCount`; queues gain `adapterName`, `beginNs`, `endNs`,
  `apiExecutionCount`, `commandListCount` and `maxWorkLevel`; a new `hardwareQueues` page lists the
  hardware queues with GPU work ranges. nextCalls lead to the `gpuSubmissions`, `gpuHardware` and
  `cpu` event domains and to `pix_timing_schema`.
- `pix_timing_overview` pages (processes, threads, queues, hardware queues) default to 5 rows
  instead of 25 to leave room for the new sections; page with `offset` and `limit`.
- Discovery no longer silently picks the newest of several eligible PIX Preview installs
  (build and runtime); choose one with `PIX_DIR` or opt into the newest explicitly.

- Every recorded-timing tool (`pix_timing_overview`, `pix_timing_events`, `pix_timing_submissions`,
  `pix_timing_thread_switches`, `pix_timing_counters_read`, `pix_timing_hotspots`,
  `pix_timing_calltree`, `pix_timing_sql`, `pix_timing_schema`) takes `rangeMode` and defaults to
  `full`: the window now runs from the first reliable timestamp (CaptureFacts 2) through the
  capture end (CaptureFacts 3) instead of stopping at the capture stop timestamp (CaptureFacts 24),
  which excluded most recorded data in captures that keep recording after stop. `rangeMode=reliable`
  restores the old end. Counts, totals and percentages over the default window change accordingly.
  The provenance range gains `rangeMode`, `captureEndNs`, a `note`, and `coverage` (in-window and
  total rows for context switches, CPU events, GPU submissions and GPU hardware ranges).
- Recorded-timing queries (`pix_timing_overview`, `pix_timing_events`, hotspots, calltree,
  counters, submissions, thread switches) run as managed jobs off the PIX worker, so a GPU
  replay no longer delays them. `pix_timing_save`, `pix_timing_resolve_symbols` and `pix_close`
  take the document's writer gate: running timing queries are interrupted and fail with
  `timing_query_invalidated` (retryable), a query that does not stop within 5 s makes save and
  symbol resolution fail with `timing_capture_busy`, and close proceeds with a warning.
- The server instructions shrank from about 5 KB to under 800 characters; the detail moved into
  tool descriptions, nextCalls and the playbook prompts. `pix_info` gains `toolsets` and
  `textContent`, and `pix_info.options` reports `PIXMCP_TOOLSETS` and `PIXMCP_TEXT_CONTENT`.
- `pix_gpu_shader_profile` returns a summary tier (R26): totals and per-shader sample breakdowns
  (stalled and issuing samples), stall totals, a heuristic keyword classification of the dominant
  stall, the `topN` hottest instructions with matched shaderRefs and ISA and static-profile
  nextCalls, and every instruction under `/detail` (previously `/shaders/N/instructions`). It gains
  `topN`, `shaderKey`, `shaderRef`, `stage`, `hash` and `includeProvenance`; identical calls join one
  job, and an unsupported driver yields a cached marker with the vendor, notes and static profiling
  nextCalls; a call PIX declines (0x8ABC0007 on this server's NVIDIA driver) yields a `declined`
  marker that is not cached on the handle.
- `pix_device_timing_capture_start` arguments are nullable so a preset can supply their defaults.
  Preview artifacts may be owned by any open handle (the device handle owns thumbnails of captures
  taken with `open=false`). An array argument with advertised choices now rejects unknown elements
  with `invalid_arguments` and a retry that keeps the recognised ones.
- pixtool process failures are `pixtool_unavailable`, `pixtool_start_failed`, `pixtool_timeout`
  (retryable) and `pixtool_failed` (naming the operation and pixtool's first error line) instead of
  the `preview_*` and `export_*` variants. `pix_gpu_preview` results add `images`,
  `selection.eventRef`, `selection.globalId`, `warning` and `replay.pixBuild`, and the
  `exactEventPreview` capability reports `unknown` until a capture's Global IDs are checked.
- Dump triage coverage for unreadable child events is keyed by the stable event path
  (`eventChildren:<queue>:<path>`) instead of native event ids, which repeat; invalid dump event
  references carry listing nextCalls.

### Removed

- Event kind `drawOrDispatch` (use `work`, which also includes `ExecuteIndirect`).
- Range parameters `firstEventIndex`/`lastEventIndex` (`pix_gpu_counters_read`),
  `firstEventGpuId`/`lastEventGpuId` (`pix_gpu_drpix_run`) and `firstEventRef`/`lastEventRef`
  (`pix_gpu_shader_profile`); use `scope` or `markerPathPrefix`.

## [1.0.0]

Typed navigation references (`eventRef`, `resourceRef`, `shaderRef`), `resultRef` snapshots with
`pix_result_read`, page envelopes, structured errors with `nextCalls`, 25-row default pages and
2-second preparation waits. The README section "Migrating from 0.2" has the migration table.
