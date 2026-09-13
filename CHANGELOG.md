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
- `pix_gpu_overview` per-queue `timing` totals, ranked `topPasses`/`topDraws` carrying `kind`,
  `semantics`, `eop` and `exec`, and targeted nextCalls (`pix_gpu_timing_tree` scoped to the top
  pass sorted by self time).
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
  `pix_gpu_info.vendor`, `pix_gpu_overview.vendor`, analysis status (`replayVendor`,
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

### Changed

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

### Removed

- Event kind `drawOrDispatch` (use `work`, which also includes `ExecuteIndirect`).
- Range parameters `firstEventIndex`/`lastEventIndex` (`pix_gpu_counters_read`),
  `firstEventGpuId`/`lastEventGpuId` (`pix_gpu_drpix_run`) and `firstEventRef`/`lastEventRef`
  (`pix_gpu_shader_profile`); use `scope` or `markerPathPrefix`.

## [1.0.0]

Typed navigation references (`eventRef`, `resourceRef`, `shaderRef`), `resultRef` snapshots with
`pix_result_read`, page envelopes, structured errors with `nextCalls`, 25-row default pages and
2-second preparation waits. The README section "Migrating from 0.2" has the migration table.
