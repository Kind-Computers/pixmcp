# pixmcp

An [MCP](https://modelcontextprotocol.io/) server for Microsoft's **PIX on Windows**.
Open GPU captures and DirectX dumps, locate expensive work, inspect pipeline state and
resources, search shaders, compare captures, and view replay output. It provides typed
navigation references, compact summaries with explicit denominators, table-shaped pages,
vendor-aware counters, and retrievable result snapshots for LLMs. Version 2.0 breaks the
1.x wire contract; `CHANGELOG.md` lists every change.

The server wraps the experimental PIX API. Capture files remain the source of truth;
open documents and replay sessions are process-local. Result snapshots use bounded
memory and temporary-disk storage, and can be exported as JSON.

## Requirements and build

- Windows 11 x64 and a D3D12-capable GPU.
- PIX Preview newer than 2606.15, verified on **2606.18-preview** (both strings live in
  `Directory.Build.props`); retail PIX does not ship this API. Install from
  [Microsoft's PIX download page](https://devblogs.microsoft.com/pix/download/).
- Windows Developer Mode for GPU replay.
- .NET 10 SDK to build and runtime to run.

The single eligible installation under `%ProgramFiles%\Microsoft PIX Preview` is selected.
Set `PIX_DIR` to a versioned installation directory to override it. An invalid explicit
override is an error, and so are several eligible installations unless `PIX_DIR` chooses
one or `PIXMCP_PIX_PICK_NEWEST=1` (build: `/p:PixMcpPickNewestPix=true`) accepts the newest.
The managed PIX DLL loads in place from that installation.

### PIX version support

The build records the install's `version.xml` and `PixApiCsExt.experimental.dll` file version
as assembly metadata. At startup the server compares the PIX it loads against that record;
`pix_info.pix` reports `installVersion`, `builtAgainst`, `verifiedRange`, `compatibility`
(`state`, `message`, `strictMode`, `exit`), the `apiSurface` type probes (with `drift` when the
loaded assembly disagrees with this build), `loggerAttached` and `loggerError`.

| Situation | Build (`dotnet build`) | Runtime (`PixMcp.exe`) |
|---|---|---|
| One eligible install (newer than 2606.15) | Selected | Selected |
| Several eligible installs | Error unless `/p:PixMcpPickNewestPix=true`, `PIX_DIR` or `/p:PixInstallDir` | Exit 1 unless `PIXMCP_PIX_PICK_NEWEST=1` or `PIX_DIR` |
| Install newer than the verified 2606.18-preview | Error unless `/p:PixMcpAllowUnverifiedPix=true` | Starts with a warning (`compatibility.state = newerUnverified`); exit 2 when `PIXMCP_PIX_STRICT=1` |
| Install older than the build the server was compiled against | (the build is the reference) | Exit 2 (`olderThanBuild`) unless `PIXMCP_PIX_STRICT=0` |
| Same version, different assembly file version | (rebuild) | Exit 2 (`mismatch`) unless `PIXMCP_PIX_STRICT=0` |

Exit codes: 1 for discovery and option problems, 2 for a PIX compatibility refusal. A type or
member the server binds against that the loaded assembly lacks surfaces as the
`pix_api_mismatch` error with a `pix_info` recovery call. `PIXMCP_PIX_STRICT` and
`PIXMCP_PIX_PICK_NEWEST` are read during discovery, before the other `PIXMCP_*` options.
`python scripts/check_versions.py` (run in CI) keeps README, CLAUDE.md and the sources on the
verified version.

```powershell
dotnet build pixmcp.sln -c Release
dotnet test pixmcp.sln -c Release
```

The executable is
`src\PixMcp\bin\x64\Release\net10.0-windows10.0.26100.0\PixMcp.exe`.
`scripts\publish.cmd` creates a framework-dependent `dist\PixMcp.exe`.
The .NET runtime and PIX installation are still required. The single-file publish
includes the SQLite native library and extracts it on first use.

## Connect a client

Run the executable as a stdio MCP server, with no arguments. Logs go to stderr.
Set `Logging__LogLevel__Default=Debug` to include PIX informational messages.

The repository's `.mcp.json` points Claude Code at the built executable. Other projects
can register it with:

```
claude mcp add pix -- C:\path\to\pixmcp\dist\PixMcp.exe
```

Claude Desktop configuration:

```json
{
  "mcpServers": {
    "pix": {
      "command": "C:\\path\\to\\pixmcp\\dist\\PixMcp.exe",
      "env": {
        "PIX_DIR": "C:\\Program Files\\Microsoft PIX Preview\\2606.18-preview"
      }
    }
  }
}
```

Omit `env` when automatic discovery is sufficient. VS Code's `.vscode/mcp.json` uses:

```json
{
  "servers": {
    "pix": {
      "type": "stdio",
      "command": "C:\\path\\to\\pixmcp\\dist\\PixMcp.exe"
    }
  }
}
```

## Investigation workflow

1. Call `pix_info`, then `pix_gpu_open`.
2. Call `pix_gpu_overview` for queues, capabilities, and the top measured passes/draws.
   Use `includeTiming: false` for metadata without replay.
3. Pass a returned `eventRef` to `pix_gpu_inspect_event` to get event context, timing,
   pipeline state, root constants, bindings, and suggested follow-up calls.
4. Follow `resourceRef` with `pix_gpu_resource_uses`; follow `shaderRef` with
   `pix_gpu_shader_code` or `pix_gpu_shader_search`.
5. Use `pix_gpu_compare` for a baseline/candidate investigation or `pix_gpu_preview`
   to inspect a replayed render target.
6. Close handles with `pix_close` or `pix_close_all`.

References contain capture identity and are safe to pass between tools:

```json
{
  "eventRef": {"handle": "gpu-1", "queueIndex": 0, "eventIndex": 42},
  "resourceRef": {"handle": "gpu-1", "apiObjectId": "0x1234"},
  "shaderRef": {
    "eventRef": {"handle": "gpu-1", "queueIndex": 0, "eventIndex": 42},
    "shaderIndex": 0
  }
}
```

Copy references from results. Event indices are queue-local; API object IDs are strings.
A shader index refers to the shader list for that event. Names and marker paths provide
context, while references provide identity within the open capture.

### Results, paging, and jobs

JSON tools publish output schemas and return matching text and `structuredContent`.
Top-level collections use an `items` object. Most pages default to **25** rows, with a
maximum of 1,000; summaries default to ten entries. Follow `nextOffset` and executable
`nextCalls` instead of guessing arguments.

Normal JSON responses target **32 KiB** (`PIXMCP_INLINE_RESULT_BYTES`). Larger managed
results become immutable snapshots with an opaque `resultRef` (`r-` plus ten random
characters). A deferred response offers two calls: the outline of the result, then its
first page of values. Read them through `pix_result_read`:

```json
{"resultRef": "r-3f9a1c2e7k", "pointer": "", "mode": "outline"}
{"resultRef": "r-3f9a1c2e7k", "pointer": "/items", "offset": 0, "limit": 25}
{"resultRef": "r-3f9a1c2e7k", "pointer": "/items", "fields": ["name", "eventRef/eventIndex"],
 "where": [{"field": "name", "op": "startsWith", "value": "Draw"}]}
```

`mode=outline` returns the shape of a value without its contents: one entry per child
with `kind`, `total`, `bytes`, whether a values read would defer it, and a sample of its
keys (for arrays, the keys of the first element), so a 2 MiB result is understood in one
call. `fields` (names or relative pointers, max 32) and `where` (max 8 clauses, ANDed;
ops `eq`, `ne`, `gt`, `ge`, `lt`, `le`, `in`, `contains`, `startsWith`, `exists`) apply
to array values: every element is evaluated (O(n); prefer the outline plus pointers for
huge arrays), `total` becomes the matched count, projected items fill the page under the
inline budget, and `projection` reports rows scanned, matched and too large to evaluate.
The values reader returns `kind`, `value`, window counts, and continuation calls. JSON
Pointer selects a nested field; escape `~` as `~0` and `/` as `~1`. Arrays, objects, and
strings can all be paged. Oversized children are represented by deferred pointers with
exact reader calls, so nested details remain reachable. A pointer that stops resolving
reports the nearest container, its keys and an outline call.

The final JSON response guard measures UTF-8 bytes and defaults to **2 MiB**
(`PIXMCP_MAX_RESULT_BYTES`). Snapshots use up to **256 MiB RAM** and **2 GiB temporary
disk** (`PIXMCP_RESULT_MEMORY_BYTES`, `PIXMCP_RESULT_DISK_BYTES`) under the system temp
directory or `PIXMCP_RESULT_DIR`. Every variable is validated once at startup: a malformed
value prints `pixmcp: <variable> ...` and exits 1 before any protocol output, and
`pix_info.options` reports each value with its source (`default` or `env`).

| Variable | Default | Meaning |
|---|---|---|
| `PIXMCP_INLINE_RESULT_BYTES` | 32768 (at most the maximum) | Responses above this become snapshots (1024 .. max) |
| `PIXMCP_MAX_RESULT_BYTES` | 2097152 | Hard cap on one response; larger reads fail with `result_too_large` |
| `PIXMCP_RESULT_MEMORY_BYTES` | 268435456 | Snapshot bytes kept in memory before spilling to disk |
| `PIXMCP_RESULT_DISK_BYTES` | 2147483648 | Snapshot bytes kept on disk (memory and disk cannot both be 0) |
| `PIXMCP_RESULT_DIR` | system temp | Absolute directory for spilled snapshots (created if missing) |

The store retains up to 50 transient snapshots and the latest 50 finished jobs.
Storage pressure evicts transient results first, then finished jobs (after a 30 second
grace) with their results. `pix_info.results` reports storage usage, leases and
evictions. Closing an owning capture invalidates its snapshots. Reads and exports
already in progress can finish; subsequent access returns `result_expired` carrying the
originating tool call in `nextCalls` (kept for the last 256 expired results), and
`result_capacity_exceeded` names the budgets, the usage and the originating call.
Cancelling a snapshot read or export releases its lease without cancelling shared
query jobs or invalidating the retained snapshot.

`pix_result_export(resultRef, outPath, pointer="", overwrite=false)` atomically writes
the complete selected JSON value to a file in an existing directory. Exports remain
usable after the server exits. Temporary snapshots are cleaned up on disposal; a
later server also reclaims abandoned sessions after verifying their ownership lock.

Replay and other expensive operations run as jobs. Query preparation waits up to
`waitSeconds` (**2 seconds** by default), then returns `pending`, `jobId`, and exact
wait/retry calls. This budget includes waiting to enter the PIX worker queue. If the
worker cannot admit the request in time, `worker_busy` includes the active operation
and exact recovery calls. A zero budget accepts an idle worker immediately. Once a
native query starts, it runs to its actual outcome. Concurrent requests share the
same preparation. Follow returned calls, then repeat the original query.

Explicit job tools return compact status. A successful job with retained output has
`resultRef` and `resultState = available`; read it with `pix_result_read`. Status never
embeds a large result. A result the store could not retain leaves the job `succeeded` with
`resultState = retentionFailed` and `resultError`; a result removed by storage pressure is
reported as `resultState = evicted`. In both cases `nextCalls` repeat `origin`, the tool call
that started the job. Finished results are protected from pressure eviction for 30 seconds so
`pix_job_wait` can always hand them over. `pix_job_cancel` removes queued work immediately.
Cancellation of running native work is best effort; an operation that finishes before
cancellation takes effect remains succeeded.

Every handle has one preparation gate: parallel callers of the same replay share one job, and
query tools join any running job that makes progress toward what they need (analysis start,
timing, accessed resources, another `pix_gpu_inspect_event` variant) before starting their
own. Once the preparation has finished the query is admitted with at least one extra second,
so a ready prerequisite is not lost to a busy queue. Starting analysis with different
adapter, power-state or flag settings while a start is queued or running fails immediately
with `analysis_settings_conflict`.

Every native PIX call runs on one worker thread. The worker owns CLI preview jobs too,
for their entire lifetime. Session metadata and job tools remain responsive during
native replay. `pix_info.worker` reports ordinary operations as well as jobs, including
their start time, elapsed time, and queue depth.

Errors use `code`, `message`, optional `hresult`, `retryable`, and `nextCalls`.
Optional data includes an explicit unavailable reason. An unexpected replay failure is
reported as a failure, rather than being remembered as unsupported hardware. Every code
the server emits is listed here (`PixErrors.Codes`); a retryable code describes a
transient condition to retry after following `nextCalls`.

| Code | Retryable | Meaning and recovery |
|---|---|---|
| `ambiguous_marker` |  | markerName must identify exactly one PIX marker across all queues. |
| `analysis_active` | yes | The call cannot proceed while analysis runs on this handle (retryable); nextCalls stop it. |
| `analysis_required` |  | The call needs GPU analysis that is not started for this handle; nextCalls start it. |
| `analysis_settings_conflict` |  | Analysis is queued, running or started with different adapter, power state or flags; nextCalls stop it. |
| `artifact_expired` |  | The preview artifact is unknown, evicted or its capture closed; run the preview again. |
| `blob_window_too_large` |  | A dump blob window exceeds the page limit. |
| `cancelled` |  | The call was cancelled by the client. |
| `capture_finalization_timeout` |  | The capture file was not finalized within the wait. |
| `capture_not_running` |  | No timing capture is in progress on this connection. |
| `capture_target_changed` |  | The capture target process changed before the capture started. |
| `capture_target_not_ready` |  | The target did not become capturable within the readiness wait. |
| `capture_target_terminated` |  | The target process exited before the capture. |
| `capture_target_unsupported` |  | The target process cannot be captured (not D3D12, not attached). |
| `counter_read_failed` |  | PIX could not read counter values for a queue; nextCalls stop analysis and list counters. |
| `csv_file_not_found` |  | A CSV input file does not exist. |
| `csv_pass_not_found` |  | The CSV pass name is not in the comparison result. |
| `developer_mode_required` |  | PIX needs Windows Developer Mode for this operation; the message tells how to enable it. |
| `directory_not_found` |  | The output's parent directory does not exist. |
| `export_missing_output` |  | pixtool completed without producing the export. |
| `file_exists` |  | The output file exists; pass overwrite=true or another path. |
| `file_not_found` |  | An input file does not exist. |
| `image_too_large` |  | The image exceeds the decoding or artifact limit; retrieve the original bytes instead. |
| `invalid_arguments` |  | An argument is missing, malformed or out of range; the message names it and nextCalls show a corrected call when one exists. |
| `invalid_image` |  | The PNG header is malformed. |
| `invalid_pointer` |  | A pix_result_read JSON pointer stops resolving; the message names the nearest container and its keys, nextCalls outline it. |
| `invalid_reference` |  | A queue, event, shader, resource, heap, view, node, wave, table or blob index does not exist; nextCalls list the valid ones. |
| `invalid_state` |  | The object is not in a state that supports the call (for example a timing capture already in progress). |
| `job_already_finished` |  | The job cannot be cancelled because it already finished; nextCalls read its status and result. |
| `output_exists` |  | The output directory or file exists; pass overwrite or another path. |
| `pix_api_mismatch` |  | The loaded PIX assembly lacks a type or member this build binds against (MissingMethodException, TypeLoadException, ...); pix_info.pix.compatibility names the version drift. |
| `pix_error` |  | PIX declined or failed the operation; the message carries the HRESULT and PIX's text. |
| `pix_unavailable` |  | The PIX API could not be loaded; see pix_info. |
| `pixdiff_failed` |  | pixdiff exited with an error; the message carries its output. |
| `pixdiff_output_too_large` |  | pixdiff produced more output than the server retains. |
| `pixdiff_start_failed` |  | pixdiff could not be started. |
| `pixdiff_timeout` |  | pixdiff did not finish within its timeout. |
| `pixdiff_unavailable` |  | pixdiff.exe was not found (PIXMCP_PIXDIFF_PATH, beside the server, or PATH). |
| `preparation_failed` |  | The preparation job (analysis, timing, counters, resources) failed; the message carries its error. |
| `preparation_unavailable` | yes | The preparation finished but its data vanished (analysis stopped, handle changed); retry the call (retryable). |
| `preview_invalid_output` |  | pixtool produced something that is not a PNG. |
| `preview_missing_output` |  | pixtool completed without producing a PNG. |
| `preview_too_large` |  | The pixtool PNG exceeds the artifact limit. |
| `result_capacity_exceeded` | yes | Result retention is full (retryable); the message reports usage and the budget variables, nextCalls repeat the originating call and pix_info. |
| `result_expired` |  | The result snapshot was closed, evicted or never existed; nextCalls repeat the originating call. |
| `result_too_large` |  | The selected value exceeds the response budget; read bounded windows (outline first). |
| `server_shutting_down` |  | The server is stopping; queued calls are not started. |
| `timeout` | yes | A bounded wait elapsed (retryable). |
| `timing_capture_busy` | yes | The timing capture is being saved or resolved; retry (retryable). |
| `timing_capture_invalid` |  | The file is not a recorded timing capture. |
| `timing_counter_not_found` |  | The recorded counter id does not exist in the timing capture. |
| `timing_query_interrupted` | yes | A recorded timing query was interrupted (retryable). |
| `timing_query_invalidated` | yes | The timing document changed (save, symbol resolution) while the query ran (retryable). |
| `timing_query_timeout` |  | A recorded timing query exceeded its time budget. |
| `timing_range_unavailable` |  | The timing capture has no usable capture range facts. |
| `timing_schema_unsupported` |  | The timing capture lacks the table or column the query needs. |
| `timing_sql_unavailable` |  | The recorded timing capture could not be opened as SQLite (pixstorage missing or the file is not a timing capture). |
| `timing_thread_lifetime_unavailable` |  | The timing capture has no thread lifetime data. |
| `tool_disabled` |  | Reserved: the tool belongs to a toolset excluded by PIXMCP_TOOLSETS. |
| `tool_error` |  | A tool failed without a structured code (SDK text error); the message is the raw text. |
| `unavailable_shader_data` |  | The shader has no code of the requested type or the dump carries no shader debugging data. |
| `unknown_counter` |  | A counter id is not in the capture's counter list; nextCalls list them. |
| `unknown_handle` |  | No open handle has this id; nextCalls list the open handles. |
| `unknown_job` |  | No job has this id; nextCalls list the jobs. |
| `unsupported_feature` |  | PIX or the current hardware does not support the operation; nothing to retry. |
| `unsupported_selection` |  | The pixtool parser cannot represent the requested marker name. |
| `worker_busy` | yes | The single PIX worker could not admit the call within waitSeconds (retryable); nextCalls wait for the running job, then repeat. |
| `wrong_handle_kind` |  | The handle exists but is a different kind (gpu, timing, dump, device) than the tool needs. |

### Shaping responses

Paged GPU and timing tools (`pix_gpu_events`, `pix_gpu_timing_events`, `pix_gpu_timing_tree`,
`pix_gpu_counters_read`, `pix_gpu_shaders`, `pix_gpu_resources`, `pix_timing_events`,
`pix_timing_hotspots`) take the same four shaping parameters:

- `format`: `objects` (default, typed rows) or `table` (positional rows). A table carries
  `columns` (name, type, unit, description), `rows` (one array per row, null cells kept in
  place), and a `legend` whose `refs` say how to rebuild a reference from columns, for example
  `eventRef = { handle: $handle, queueIndex: $col:queueIndex, eventIndex: $col:eventIndex }`.
  Duration objects flatten to `eop.ns`, `eop.ms`, `eop.percentOfQueueSpan`; marker paths join
  with `/`; integers above 2^53 are decimal strings. Tables are several times smaller than
  object rows, so a 1,000-row page usually stays inline.
- `brief`: only identifying and ranking fields (marker paths, execution durations, raw
  timestamps and API text are dropped). `pix_gpu_overview` and `pix_gpu_inspect_event` accept
  `brief` and `maxStringLength` only.
- `topN`: the first N rows of the sorted set with no continuation (`offset` must be 0).
- `maxStringLength` (default 200, 16..4096): longer strings are cut with `…`; the response
  counts them in `truncatedStrings` and offers a continuation with `maxStringLength = 4096`.

Every `nextCalls` entry carries a `cost` hint: `cached` (answered from memory), `query`
(capture metadata or SQLite), `replay` (starts a GPU replay or collection), `pixtool`
(spawns pixtool) or `job` (waits on a job). Hints are advisory and read off the worker.

Provenance blocks (replay adapter, flags, timing range) are returned in full the first time a
handle emits them, with a `fingerprint`. Later responses replace an unchanged block with
`{ provenanceRef: "<handle>#<fingerprint>", unchanged: true }`; the block comes back in full
when it changes (`changed: true`) or when the call passes `includeProvenance = true`.

On a cold capture `pix_gpu_overview` and `pix_gpu_inspect_event` answer immediately with the
metadata that needs no replay (queues, kinds, capabilities; the event record and marker path)
and put `{ pending: true, jobId, retry }` in the sections that wait for the preparation job
(`timing`; `timing`, `pipeline`, `bindings` plus `preparation`). Wait for the job, then repeat
the call; the top level of such a partial answer is never `pending`.

Input schemas carry `examples` for reference-shaped parameters (`handle`, `eventRef`, `scope`,
`shaderRef`, `resourceRef`, `markerPathPrefix`, `format`, `kind`), so the wire shape of an
`EventRef` is visible in `tools/list` rather than learned from errors.

### Timing and counters

Timing rows and counter values are read with PIX's event-indexed bulk API and fall back to the
per-event API when the entry count disagrees with the event count or the bulk call is
unavailable. Every queue reports how it was read (`readback`: `bulk` or `perEvent`, the reason,
interop call counts, failed reads) in the timing-prepare summary, in
`pix_gpu_timing_events.extra.readback` and per counter in `pix_gpu_counters_read.extra.coverage`.
`PIXMCP_VERIFY_BULK_READBACK=1` re-reads 50 sampled events per queue through the per-event API
after a bulk pass and reports mismatches without failing the job.

Every counter carries an inferred `unit` (`percent`, `count`, `bytes`, `bytesPerSecond`, `cycles`,
`ns`, `ratio`, `boolean`, `bitmask` or `unknown`) with `unitSource` (format, name or description),
`unitConfidence` and `aggregationHint` (`sum` for totals, `avg` for rates and shares); PIX exposes
no unit field, so this is vocabulary-based and stated as such. `pix_gpu_counters_prepare` and
`pix_gpu_counters_read` take either `counterIds` or a `preset` (`utilization`, `aluUtilization`,
`perStageAlu`, `occupancy`, `stalls`, `cache`, `memoryBandwidth`, `fixedFunction`,
`pipelineStatistics`, `depthOcclusion`) resolved for the capture's vendor; `pix_gpu_counters_list`
reports every preset's matches in `extra.presets` with a confidence (`verified` on this
server's hardware, `transcribed` from vendor plugin strings, `unverified`). On the NVIDIA RTX
4070 Ti used to verify this release PIX exposes only the 22 D3D counters, so only
`pipelineStatistics` and `depthOcclusion` resolve there; the Intel vocabulary comes from the
2601.15 plugin strings and is unverified on hardware (contributor checklist: run
`pix_gpu_counters_list` on an Arc/Xe2 machine and replace `tests/PixMcp.Tests/Fixtures/counter-catalogs/intel-xe2.json`).

Vendor identity is reported everywhere replayed numbers appear: queues carry `vendor`, `pix_gpu_info`
and `pix_gpu_overview` carry the capture's `vendor` (from the capture file's vendor id or device
name), analysis status carries `replayVendor` and `captureVendor`, and replay provenance carries
`adapterName`, `vendor`, `captureVendor`, `vendorMismatch` and `pixBuild`. Known limitations live
in a machine-readable registry (`src/PixMcp/Resources/compatibility-notes.json`): capabilities
take their state from it when nothing probed them, every capability lists its `notes`, and the
counter, occupancy and high-frequency tools attach the notes for the capture's vendor
(`extra.notes`). `pix_info.pix.notes` lists the whole registry.

Counter sets are cached per analysis session: a set that is a subset of an already collected
set is projected from it without a replay (`extra.collection.source` is
`projectedFromSuperset`), a superset replays. Counter rows carry `rowKind`: a `marker` row is
PIX's own measurement over the marker's span, collected in a separate playback round, and is
never the sum of the `event` rows below it; `descendantDataEvents` counts those rows and the
response warns when a page mixes both kinds.

Every replay duration is a `DurationDto`: `ns`, `ms`, `percentOfQueueSpan` (share of the
queue wall span from the first EOP start to the last EOP end), `percentOfQueueSum` (share of
the sum of top-level inclusive values, which overstates when roots overlap),
`percentOfParent`, and a 1-based `rank` where a list was ranked. A `denominators` object
spells out each denominator, and per-queue `QueueTotals` report `busyNs` (union of the
TOP..EOP windows of timed leaf events), `spanNs`, `idleNs`, `sumOfRootsNs`, `rootsOverlap`
and timed/untimed event counts.

Timing-tree nodes carry `semantics`: `measured` (PIX timed the event itself; markers are
measured as the span of their contents), `derivedSum` (the serialized sum of the children),
`mixed` (a derived sum that adds measured spans to derived sums, so it can over- or
understate) or `untimed`. `childSumEopNs`, `childrenExceedMeasured` and `childOverflowNs`
expose pipelined children whose sum exceeds the measured span; `untimedChildren` and
`repaired` expose timing gaps and corrupt parent links instead of hiding them. Self time is
clamped at 0. These are PIX replay measurements. Summed end-of-pipe intervals are **not
frame latency** and do not establish overlap between asynchronous queues.

`pix_gpu_timing_tree` takes `scope` (an `EventRef` whose children form the first level),
`sortBy` (`inclusive`, `self`, `childCount`, `index`, `topStart`), `minInclusiveNs`,
`minSelfNs` and a global `maxNodes` budget. Omitted siblings and descendants include
continuation calls. `pix_gpu_timing_events` provides the flat sortable view; its rows carry
`kind`, `eop` and `exec` (TOP-to-EOP) durations.

Event kinds are `work` (draw, dispatch and executeIndirect together), `draw`, `dispatch`,
`executeIndirect`, `copy`, `clear`, `resolve`, `barrier`, `present`, `marker` (PIX events and
native labels that have children or no GPU id) and `label` (timed leaf labels such as a
SetMarker). Unknown kinds are rejected with `invalid_arguments`.

Counter queries can sort by counter ID, apply numeric thresholds, and restrict work to
a marker scope or event range. Rows key values by counter ID; metadata carries names
and units separately, avoiding collisions between identically named counters.
Integers outside JavaScript's exact range are returned as decimal strings. Timing,
counter sets, occupancy, and high-frequency collections have reusable preparation jobs;
subsequent pages reuse collected data. Sampled high-frequency summaries also expose
offsets for retrieving the original point sequence.

### Pipeline, resources, and shaders

`pix_gpu_inspect_event` combines common queries in one preparation and response.
Root-constant bindings retrieve DWORD values where the native API supplies them;
missing values have explicit coverage. Resource uses include event navigation and
describe its evidence. A cached traversal of event-scoped views can recover navigation
when the native binding API does not return a usable event identity.
This reverse-use index is shared by all resource queries. Every GPU analysis tool that
selects events (`pix_gpu_events`, `pix_gpu_timing_events`, `pix_gpu_timing_tree`,
`pix_gpu_counters_read`, `pix_gpu_occupancy`, `pix_gpu_hf_counters`, `pix_gpu_drpix_run`,
`pix_gpu_shader_profile`, `pix_gpu_resource_uses`, `pix_gpu_shaders`, `pix_gpu_shader_uses`,
`pix_gpu_overview`) takes the same two selectors: `scope` (an `EventRef`; the event and its
descendants) and `markerPathPrefix` (every marker subtree whose `/`-joined path of ancestor
names plus own name starts with the prefix, case-insensitive, e.g. `Frame/Shadow`). Given
together they intersect. Responses echo what the selection resolved to (`scope` or
`extra.scope`: root, prefix, `matchedRoots`, `matchedRootCount`), so a prefix that matches
repeated marker names is visible rather than silently ambiguous. Range consumers (Dr. PIX,
shader profiling) need a single queue: a prefix that matches markers on several queues fails
with `invalid_arguments` and one `nextCalls` entry per queue. The range PIX receives is the
lowest and highest event with a GPU id inside the selection, ordered by PIX's moment handle
and reported back as `range` with event references. `pix_gpu_drpix_run` no longer runs over
the whole capture by default; pass `wholeCapture=true` explicitly. Event inspection preserves
timing and pipeline sections when PIX explicitly reports binding preparation as unsupported.

Standalone pipeline tools offer compact defaults and optional full sections. Resource
views and bindings have separate paging. Shader source is line-addressable, with
literal text search, context lines, and exact continuations. Source-node selection,
source kind (HLSL/IL/ISA), and shader identity are explicit.
`pix_gpu_shaders` provides a capture-wide inventory, and `pix_gpu_shader_uses` returns
events using a shader. Returned shader references can be passed directly to code/search.

When shader source is missing, call `pix_gpu_shader_diagnostics(shaderRef)`. It checks
HLSL, IL and ISA node availability for that shader and reports its PDB hash when present.
`codeTypes` can select a subset. Each probe distinguishes absent data from a native
failure and includes source-retrieval calls when available. A PDB hash identifies the
expected symbols; it does not establish that PIX found a matching PDB. Diagnostics do
not load source text, search local PDB directories, or scan other shaders.

### Capture comparison

`pix_gpu_compare` takes `baselineHandle`, `candidateHandle`, and selected sections:
`timings`, `shaders`, `pipeline`, and `resources`. It returns a job whose result
summarizes changes and provides a `fullResultRef` for complete differences, unmatched
events, ambiguity, and coverage.

Queue names/types, marker paths, work kind, and available shader hashes provide
conservative matching. Numeric IDs never establish identity across captures.
Ambiguous matches are reported; `queuePairs` and `eventPairs` let the caller supply
known correspondences. Timing differences are signed, and a zero baseline has no
percentage change. Resource and pipeline comparisons exclude capture-local identities.

Comparison snapshots the baseline into managed data before replaying the candidate.
It may stop baseline analysis to make replay available for the candidate; coverage
reports this transition. Missing sections and replay configuration remain visible.

`pix_gpu_compare_changes` filters and pages the saved `fullResultRef` without replay.
Choose `direction` (`all`, `regressions`, or `improvements`), inclusive absolute
`minDeltaNs` and `minDeltaPercent` thresholds, and `sortBy` (`absoluteDeltaNs`, `deltaNs`,
`deltaPercent`, or `event`). Both thresholds must match; zero means no threshold.
Unmeasured structural changes remain visible with defaults, and missing sort values
remain last. Use `pix_result_export` to save complete differences.

### Replay previews

`pix_gpu_screenshot` reads the image embedded in the capture and shares the preview
artifact retrieval tools. It writes nothing unless `outPath` is set (`overwrite` guards an
existing file); the PNG is retained as an artifact and, with `inline: true`, returned as
image content shrunk to the response budget (`budgetLimited`). Every file-writing tool
refuses the server's private result storage, needs an existing parent directory and reports
`file_exists` unless `overwrite: true`. The hard response budget counts text and base64
image bytes as well as structured JSON. `pix_gpu_preview_image` accepts a `crop` in original pixel
coordinates and `maxDimension` for proportional resizing without upscaling.
Set `ignoreAlpha: true` to view stored RGB as opaque before cropping or resizing.
Render targets can contain useful scene colors with zero alpha, which otherwise
appear transparent or black. This option defaults to false and reports the opaque
transformation in image metadata; byte paging still returns the original PNG.
`pix_gpu_preview` uses the installed `pixtool.exe` to replay and export an RTV slot or
depth target. It returns an artifact reference, dimensions, and selection metadata;
`pix_gpu_preview_image` retrieves inline MCP image content and
`pix_gpu_preview_bytes` pages PNG bytes.

**Stop analysis on every open GPU capture first.** A conflicting request returns
`analysis_active` with the required stop calls. Preview jobs execute exclusively on
the PIX worker, with a hidden process, deadline, cancellation, and temporary-file cleanup.
Each replay uses a temporary capture copy because the native document keeps the original
open. CLI replay uses its own defaults, independently of native analysis adapter settings.

Supported selectors follow pixtool's semantics: no marker selects the last instance
with the requested resource bound; a unique exact marker name selects its last child
with that resource bound. Use the exact name returned by event enumeration.
This is not a promise of an exact arbitrary-event or presentation-time image.
The input schema exposes only selectors supported by the CLI.
Marker names containing literal double quotes or control characters are unsupported by
the CLI argument parser; capture-end selection remains available.

Artifacts are memory-resident, with a 50-item/64 MiB cache and a 32 MiB per-image limit.
Inline images have a 4 MiB limit; oversized originals get a thumbnail automatically,
while byte paging retains the original PNG. Closing the capture
expires its artifacts.

### Export a capture to C++

`pix_gpu_export_cpp(handle, outputDirectory)` runs the installed `pixtool` as a job and
returns the generated project's directory and `CMakeLists.txt` path through the job's
`resultRef`. The destination must not exist and its parent must already exist. Export
never overwrites existing output. The default timeout is 1,800 seconds (maximum 3,600).

As with previews, stop all connected GPU analyses before export. CLI jobs use an owned
capture copy, run exclusively on the PIX worker, and support cancellation and timeouts.
Failed or cancelled exports retain partial output with its location in job diagnostics.
Generated files survive capture close and server shutdown. Building or running the
project is a separate action.

`useWinPixEventRuntime`, `useAgilitySdk`, and `useReplayTimeExecuteIndirectBuffers` are
false by default. The first two opt into the CLI's package/license options. Export uses
the CLI's runtime and defaults independently of native analysis adapter/power settings.

### Unreal CSV comparisons

Install the companion `pixdiff` from the `pix_tutorial` project with JSON output support.
The server discovers `PIXMCP_PIXDIFF_PATH`, then `pixdiff.exe` beside the server, then PATH.
An invalid explicit override is an error. `pix_info.pixdiff` reports discovery without
launching the helper; all other PIX tools work when the helper is absent.

Call `pix_csv_compare(baselinePath, candidatePath)` to compare recorded per-pass GPU
milliseconds. Its default statistic is `median`, with `mean` and `p95` also supported.
Positive deltas mean the candidate is slower. Rows sort by absolute delta, so improvements
may appear before regressions. Results retain complete measured and missing or unmeasured
pass lists, sample counts, frame counts, and the `GPU/Total` aggregate when present. Zero
baselines have no percentage delta. The helper's text output keeps its existing mean/top-30 defaults;
`pixdiff candidate.csv baseline.csv --format=json --stat median` emits complete JSON.

Comparison runs as a managed job without occupying the PIX worker. Read the saved
`resultRef` with `pix_result_read` or export it with `pix_result_export`; paging never
reopens the source CSVs. `pix_csv_pass_candidates(resultRef, passName, handle)` searches
an explicitly selected GPU capture for possible marker matches. Exact names rank first;
substring matches and duplicate names remain visible with their event references and
follow-up calls. `GPU/Total` links to the capture overview instead of a marker.
These are name-based candidates. CSV recordings and GPU replay measurements retain
separate provenance and do not establish capture identity or directly comparable timing.

### Live and recorded timing captures

GPU capture waits for launch/attach readiness callbacks for up to
`readinessTimeoutSeconds` (default 30, range 0–300). `pix_device_info.targets` reports
readiness and unsupported reasons. Optional `delaySeconds` is a warmup after readiness
(default 0, range 0–60). Neither wait occupies the PIX worker. Cancellation, detachment,
termination, and connection close interrupt these waits. Timing capture stop waits for
the saved capture to become readable before reporting success.
Windows may request UAC approval when PIX starts its timing recorder. Complete that
desktop prompt before recording; unattended runners need the PIX service/elevation
configured in advance.

`pix_device_timing_capture_start` accepts `contextSwitchStacks` and `captureSysmonCounters`
(both default false). Enable both for the tutorial's CPU/GPU investigation. Switch stacks
require `contextSwitches=true`; the start response includes effective capture settings.
Launch Unreal with `-PIX -statnamedevents` for timing capture and keep GPU-capture
injection (`-attachPIX`, or `underGpuCapture=true`) for separate GPU-capture runs.

Start recorded analysis with `pix_timing_overview`, then use `pix_timing_events`,
`pix_timing_counters_list`, and `pix_timing_counters_read`. These query the timing
document's PixStorage database read-only; they do not replay the GPU. Times use decimal
nanoseconds and half-open `[startNs,endNs)` intervals, defaulting to the reliable capture
range. Rows preserve original event duration and selected-range overlap; execution and
stall information are present when recorded.

`pix_timing_hotspots` ranks sampled CPU functions and addresses;
`pix_timing_calltree` pages caller-to-callee paths using a reusable `profileRef`.
Inclusive and exclusive sample counts are statistical observations, not exact CPU time.
Coverage includes samples without stacks and unresolved symbols. Symbol resolution is
explicit through `pix_timing_resolve_symbols`; save and symbol resolution invalidate
cached queries and profiles. Supply matching PDBs for application function names.

Use `pix_timing_submissions` to follow a recorded CPU queue submission to its GPU
execution. Its time filter selects submission timestamps in `[startNs,endNs)`, retaining
GPU start/end times outside that selection. Valid intervals include submission-to-GPU
latency and GPU duration; missing, zero-duration, or inconsistent execution timestamps
have explicit coverage instead. Pass the returned `submissionRef` back to the tool for
an exact lookup without lane/time filters. References expire on save, symbol resolution,
or close. This is queue-submission correlation; individual draws and named markers are
not inferred from names or nearby timestamps.

Follow a returned `threadRowId` with `pix_timing_thread_switches` to inspect recorded
switch-in/out transitions within that thread's lifetime. Switch-out rows include raw
wait-reason codes and exact-timestamp stacks when recorded. Missing stacks and unresolved
symbols remain distinct. A transition does not identify a waited-on object or establish
blocked duration or the cause of a GPU gap. These queries do not calculate GPUView's
hardware-queue busy percentage.

### Dump triage

`pix_dump_triage` summarizes deterministic evidence: nested incomplete events,
page faults, resource lifetime information, breadcrumbs, and shader waves. It includes
coverage and follow-up calls rather than claiming a definitive cause.

Dump event references contain `handle`, `queueIndex`, and an `eventPath` of child indices.
`pix_dump_event` follows those paths and pages direct children. Wave data, shader
variables, blobs, and other large details have retrieval windows. Incomplete descendants
are examined even when their parent reports completed.
`pix_dump_shader_eval` accepts `laneMask` as a decimal or hexadecimal string, preserving
all 64 bits across clients. Oversized GPU state tables retain every nested row in a result
snapshot. Blob byte windows include exact continuation calls; later windows require
reading the preceding bytes because the native blob API only exposes prefix reads.

## Tool catalog

| Area | Tools |
|---|---|
| Session/results | `pix_info`, `pix_handles`, `pix_close`, `pix_close_all`, `pix_jobs`, `pix_job_status`, `pix_job_wait`, `pix_job_cancel`, `pix_log`, `pix_result_read`, `pix_result_export` |
| Investigation | `pix_gpu_overview`, `pix_gpu_inspect_event`, `pix_gpu_compare`, `pix_gpu_compare_changes` |
| GPU capture | `pix_gpu_open`, `pix_gpu_info`, `pix_gpu_queues`, `pix_gpu_events`, `pix_gpu_event`, `pix_gpu_api_objects`, `pix_gpu_screenshot` |
| Analysis | `pix_gpu_analysis_start`, `pix_gpu_analysis_status`, `pix_gpu_analysis_adapters`, `pix_gpu_analysis_stop` |
| Timing/counters | `pix_gpu_timing_prepare`, `pix_gpu_timing_events`, `pix_gpu_timing_tree`, `pix_gpu_counters_list`, `pix_gpu_counters_prepare`, `pix_gpu_counters_read`, `pix_gpu_occupancy`, `pix_gpu_hf_counters` |
| Pipeline/shaders | `pix_gpu_pipeline_state`, `pix_gpu_shaders`, `pix_gpu_shader_uses`, `pix_gpu_shader_code`, `pix_gpu_shader_search`, `pix_gpu_shader_diagnostics`, `pix_gpu_shader_profile` |
| Resources | `pix_gpu_resources`, `pix_gpu_resource`, `pix_gpu_event_resources`, `pix_gpu_resource_uses`, `pix_gpu_heap` |
| Preview | `pix_gpu_preview`, `pix_gpu_preview_image`, `pix_gpu_preview_bytes` |
| C++ export | `pix_gpu_export_cpp` |
| Unreal CSV | `pix_csv_compare`, `pix_csv_pass_candidates` |
| Dr. PIX | `pix_gpu_drpix_experiments`, `pix_gpu_drpix_run` |
| Timing captures | `pix_timing_open`, `pix_timing_overview`, `pix_timing_events`, `pix_timing_submissions`, `pix_timing_thread_switches`, `pix_timing_counters_list`, `pix_timing_counters_read`, `pix_timing_hotspots`, `pix_timing_calltree`, `pix_timing_resolve_symbols`, `pix_timing_save` |
| Live device | `pix_device_connect`, `pix_device_info`, `pix_device_processes`, `pix_device_packaged_apps`, `pix_device_counters`, `pix_device_d3d_settings`, `pix_device_d3d_settings_set`, `pix_device_launch`, `pix_device_attach`, `pix_device_take_gpu_capture`, `pix_device_timing_capture_start`, `pix_device_timing_capture_stop`, `pix_device_detach` |
| Capture format | `pix_capture_format`, `pix_capture_upgrade` |
| Dump investigation | `pix_dump_open`, `pix_dump_info`, `pix_dump_triage`, `pix_dump_queues`, `pix_dump_events`, `pix_dump_event`, `pix_dump_page_faults`, `pix_dump_breadcrumbs`, `pix_dump_resources`, `pix_dump_gpu_state`, `pix_dump_blobs`, `pix_dump_journal` |
| Dump shaders | `pix_dump_shader_waves`, `pix_dump_shader_wave`, `pix_dump_shader_wave_data`, `pix_dump_shader_variable`, `pix_dump_shader_eval` |

MCP resources `pix://handles`, `pix://handles/{handle}`, `pix://jobs`, and
`pix://jobs/{jobId}` mirror session tables. Native experimental details remain
extensible within typed outer schemas.

## Migrating from 1.x

Version 2.0 renames tools, removes the old range vocabularies and reshapes durations. The
`Changed` and `Removed` sections of `CHANGELOG.md` list every break; the most common ones:

| 1.x pattern | Version 2.0 |
|---|---|
| `pix_gpu_timing_collect`, `pix_gpu_counters_start`, `pix_gpu_counters_collect` | `pix_gpu_timing_prepare`, `pix_gpu_counters_prepare`, `pix_gpu_counters_read` |
| `parentIndex`, `firstEventIndex`/`lastEventIndex`, gpuId or eventRef ranges | `scope` (EventRef subtree) or `markerPathPrefix` |
| Bare `eopDurationNs`, `percentOfQueue`, `totalEopNs` | `DurationDto` (`ns`, `ms`, percents with `denominators`) and `sumOfRootsNs` |
| Kind `drawOrDispatch` | Kind `work` (includes `ExecuteIndirect`) |
| Bare arrays from list tools | `{ total, offset, count, items }` envelopes |
| `result-N` ids | Opaque `r-` ids; `pix_result_read` also offers `mode=outline`, `fields` and `where` |
| `pix_error` for argument and state problems | Registered codes (README error table) with `nextCalls` |

## Migrating from 0.2

Version 1.0 intentionally changes the wire contract:

| Previous pattern | Version 1.0 |
|---|---|
| Separate handle/queue/event selectors for event inspection | Pass the returned `eventRef` |
| Separate handle/object ID for resource inspection | Pass `resourceRef`; IDs stay strings |
| Event selectors plus shader index for code | Pass `shaderRef` |
| Shader `maxChars` prefix | Use `startLine`/`lineCount`, node paging, or search |
| Embedded `job.result` | Read `job.resultRef` through `pix_result_read`, then use `value` |
| Large inline JSON or an oversized-result failure | Follow `resultRef` and nested deferred pointers |
| Top-level JSON arrays | Read the `items` wrapper |
| Default 100-row pages / 60-second preparation waits | Default 25 rows / 2 seconds |
| Guessed retries from text errors | Structured error codes and exact `nextCalls` |

Existing smoke scenarios have been migrated to the new contract.

## Testing

`dotnet test` runs unit, worker/job lifecycle, schema, and stdio protocol tests.
Set `PIX_TEST_CAPTURE` to a capture path to enable capture integration tests;
also set `PIX_TEST_ANALYSIS=1` for replay tests. Hosted CI runs the dependency-free
Python harness tests. The manually dispatched self-hosted `pix` job builds the fixture,
generates captures, and then runs native tests serially. Set `PIX_TEST_TIMING_CAPTURE`
to the generated timing capture for named marker/counter/callstack validation.

```powershell
python -m unittest discover -s scripts -p "test_*.py"
tests\D3D12TestApp\build.cmd
```

The fixture needs Visual Studio C++ tools. It renders graphics and compute work with
fixed markers and known root constants. `--variant baseline` is the default;
`--variant candidate` changes a shader, root constants, resource size, and adds a pass.
Use `--hidden` for unattended capture and `--duplicate-markers` to test ambiguity.
`--startup-delay-ms` delays D3D12 creation to exercise readiness callbacks.
`--timing-workload` records nested CPU markers and a custom frame counter around
named, non-inlined functions. The build restores a pinned WinPixEventRuntime and
keeps matching PDBs beside the executable.

Generate baseline, candidate, and symbol-resolved timing captures together:

```powershell
python scripts\capture_fixtures.py src\PixMcp\bin\x64\Release\net10.0-windows10.0.26100.0\PixMcp.exe
```

`scripts\smoke.py` runs scripted MCP scenarios:

```powershell
python scripts\smoke.py src\PixMcp\bin\x64\Release\net10.0-windows10.0.26100.0\PixMcp.exe @scripts\scenarios\capture-and-inspect.json
```

`take-capture` produces a capture. `open-capture`, `analysis-pending`, and
`inspect-extras` accept `PIX_TEST_CAPTURE`. Tool/protocol errors, failed jobs,
unresolved references, and failed assertions fail the scenario.

`tutorial-gpu-extras` also accepts `PIX_TEST_CAPTURE` and a new destination directory
through `PIX_TEST_CPP_OUTPUT`. It verifies C++ export, shader diagnostics, and preview
rendering in sequence. The destination's parent must already exist.

Once baseline and candidate captures exist, run the investigation benchmark:

```powershell
python scripts\benchmark.py src\PixMcp\bin\x64\Release\net10.0-windows10.0.26100.0\PixMcp.exe --capture tests\artifacts\baseline.wpix --candidate tests\artifacts\candidate.wpix
```

It checks hotspot discovery, actual root constants, shader inventory and reverse
uses, shader navigation/search, scoped events, timings and resource uses,
filtered comparisons, complete JSON exports, and cropped preview thumbnails. Its JSON report
records correctness, tool calls, decoded JSON bytes, received JSON-RPC wire bytes,
elapsed time, and preparation jobs per task. Missing native root values with explicit
coverage are reported as a limited task only for explicitly recognized native
limitations. PIX does not expose its internal replay iteration count; job counts
are not a substitute for that measurement. Generated captures/reports under
`tests/artifacts/` are ignored by Git.
Reports survive individual failures so independent tasks can still run. Actual failures
return a failing exit status; `--strict` also fails on limited or skipped tasks.
Both the benchmark and fixture generator fail on cleanup errors or an unsuccessful
server shutdown, recording those diagnostics separately from the original results.
The self-hosted workflow exposes this as `strict_benchmark`. Call counts and elapsed
time remain advisory measurements.

GPU hang generation is never part of the default tests or benchmark.
The optional `provoke-hang` scenario deliberately resets the GPU and exists only
for manual dump testing.

### UE tutorial walkthrough

Run the tutorial against the published MCP server from the repository root:

```powershell
python scripts/tutorial_validation.py --session tutorial-YYYYMMDD-HHMMSS
```

The runner defaults to `dist/PixMcp.exe`, a built Unreal Editor and CitySample under
`W:\UE5\UnrealEngine`, Intel Arc B580 and NVIDIA RTX 4070 Ti adapters, and the
`APT_DetFlyby1` automated performance sequence. It also needs the PIX Preview/runtime
requirements above, the companion `pixdiff`, and enough free space for CitySample
captures and C++ exports. These workload paths and adapter defaults are defined in
the runner.

The baseline uses DX12 at 2560×1440, TSR at 100% screen percentage, dynamic resolution
disabled, VSync off, and Quinlight disabled. CSV collection discards one warmup and
records three measured runs per adapter, followed by an Intel 50% screen-percentage
cross-check. Startup allows 900 seconds; measured runs with recorded compilation,
incorrect CSV resolution/VSync metadata, or missing/mismatched startup console
settings are rejected and retried once. Console echoes verify settings before the
flyby starts; they do not establish those settings on every recorded frame.
GPU investigations explicitly verify the
replay adapter and TSR markers; hardware-dependent capabilities retain their reported
unsupported outcomes.

For a separate investigation of UE background command-list translation crashes,
`--serial-translation` disables `r.RHICmd.ParallelTranslate.Enable` through a
temporary startup override. Use a new session: the runner refuses to mix this
diagnostic profile with standard baseline measurements.

Select `--stage csv`, `--stage gpu`, `--stage timing`, or `--stage dumps` to run one
stage in the same session; the default is `all`. `--adapter intel` or `--adapter nvidia`
restricts CSV/GPU workloads, while timing recording targets Intel. Captures and images
go to `E:\PixCaptures\<session>` by default (`--artifact-root` overrides this).
JSON/Markdown reports, tool schemas, raw MCP transcripts, and server diagnostics go
to ignored `tests/artifacts/<session>`. Task evidence files have unique names so
resumed investigations retain earlier evidence. Independent stages continue after failures.
GPUView, building/running generated C++, and deliberate GPU-hang generation are
excluded; the dump stage inspects an existing suitable dump when available.

## Limitations and troubleshooting

- Enable Windows Developer Mode when PIX reports `0x8ABC0000` or `0x8ABC0001`.
- If a query is pending, wait for its job and follow the supplied retry call.
  `pix_info.worker` helps distinguish queued work from an idle server.
- Stop analysis before changing adapter, power-state, or replay flags.
- Optional occupancy, high-frequency counters, and shader profiling depend on
  the GPU, driver, and PIX build. Explicit unsupported results are cached until analysis resets.
- Heap inspection needs a capture containing placed-resource heaps.
- On the tested 2606.18-preview build, root-constant value access returns
  `E_INVALIDARG` for the fixture despite its declared four-DWORD root parameter.
  `rootConstantCoverage` reports this limitation. The same build returns empty
  binding-event identities; resource-use navigation falls back to event-scoped
  views and exact captured API object arguments, with explicit evidence and coverage.
- Dump tools follow the experimental API and have deterministic managed tests,
  but still need validation against a real `.dxdmp_preview` file.
- Timing queries depend on the experimental PixStorage schema. Missing tables or
  unavailable recordings return explicit coverage. CPU sampling requires stacks to
  be recorded for calltree attribution and matching symbols for function names.
- The embedded screenshot encoder supports common 8/10-bit and HDR swapchains;
  supported HDR formats are tone-mapped to sRGB and reported as `toneMapped`.
- A running MCP client locks the server executable. Close it before rebuilding.
- All handles, jobs, result references, and preview artifacts are process-local.

## Architecture

`PixWorker` owns all native calls. `PixSession` owns document handles and detached
results; GPU handles cache events and preparations. `Jobs` tracks cancellation,
progress, and retention. `StructuredToolResults` provides output schemas, errors,
and response budgets; `ResultStore` implements leased, bounded retrieval and exports.
Readiness waits run outside the worker. Timing SQL runs in cancellable preparation
jobs with private, read-only SQLite connections closed before native document changes.
Tools compose these primitives into small, navigable investigations.

`Program.cs` must not directly reference Microsoft.PIX types before discovery
loads the installed assembly. Expensive native preparation belongs in a job,
using `Tools.RunWhenReady` for queries that depend on it.

## License

[MIT](LICENSE). PIX discovery includes adaptations from Microsoft's samples;
see [third-party notices](THIRD_PARTY_NOTICES.md).
