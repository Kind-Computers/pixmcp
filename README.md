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
2. Call `pix_gpu_overview` for capture facts and frames, queue kinds and replay totals, the top
   passes and work events, and an EOP histogram.
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

### Prompts, the investigation ladder, and progress

The server lists ten playbook prompts: `pix_frame_budget`, `pix_regression`,
`pix_dispatch_slow`, `pix_cpu_vs_gpu`, `pix_crash_triage`, `pix_async_overlap`,
`pix_bandwidth_hogs`, `pix_fill_vs_vertex`, `pix_sql_investigation` and `pix_gpu_bottleneck`.
Each renders numbered steps with exact JSON calls, the ladder level of every call, what the
numbers do not mean, and example questions. String arguments such as `handle`, or an `eventRef`
passed as JSON text, are substituted into the calls. A prompt whose tools are disabled by
`PIXMCP_TOOLSETS` is not listed. [docs/investigation-ladder.md](docs/investigation-ladder.md)
groups the tools into Level 0 verdicts, Level 1 composites, Level 2 scoped enumerators and
Level 3 raw SQL and result reads.

A client that sends a progress token (`_meta.progressToken`) with a tool call receives
`notifications/progress` while that call waits on a job. That covers `pix_job_wait`,
job-starting tools called with `waitSeconds`, and query tools waiting for a replay preparation.
Each report carries the job's progress on a 0..100 scale and its latest status message, at most
four times a second, with a final report when the wait ends. Polling `pix_job_status` keeps
working for clients without a token.

The instructions sent with `initialize` stay under 800 characters. Tool descriptions,
`nextCalls` and the prompts carry the detail.

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
to array values. A nonempty `where` scans every element (O(n); prefer the outline plus
pointers for huge arrays), and `total` becomes the count of evaluated matches. Without
predicates (including `where: []`), offsets and `total` follow the original array;
oversized rows appear as deferred pointers to their original contents. Projected items
fill the page under the inline budget, and `projection` reports rows scanned, matched
and too large to evaluate.
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
| `PIXMCP_GPUSQL_MAX_BYTES` | 1073741824 (1 GiB) | Byte cap of each GPU capture's private SQL store (at least 1048576) |
| `PIXMCP_TOOLSETS` | all | Toolsets to advertise, separated by commas: `gpu`, `timing`, `dump`, `device`, `csv`, `gpusql`, `drpix`, `shader` (`session` is always on). Disabled tools leave `tools/list`, fail with `tool_disabled` and hide the prompts that need them |
| `PIXMCP_TEXT_CONTENT` | full | `summary` replaces the text block of each successful result with a summary of at most 512 bytes; `structuredContent` and errors stay complete |
| `PIXMCP_TIMING_EXPERIMENT_PARTS` | none | Timing capture option parts appended to every timing capture, separated by commas: `VIDEO`, `INCLUDE_CAPTURE_ETL`, `CIRCULAR`, `PAGEFAULT`, `CAPTURE_SYSMON_COUNTERS`, `CLRDATA`, `FORCE_COM_PATH`, `GPU_ONLY_EVENTS`, `MINIMAL_INSTRUMENTATION`. For experiments such as `scripts/gpu_frame_experiment.py` |

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
timing, accessed resources, the shared `pix_gpu_inspect_event` preparation) before starting their
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
| `analysis_incompatible` |  | PIX declined to replay the capture on the chosen adapter (0x8ABC006B, e.g. a capture taken on another GPU vendor); nextCalls retry with `IGNORE_INCOMPATIBILITIES`. |
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
| `global_id_mismatch` |  | pixtool Global IDs differ from this capture's GPU ids, so exact-event previews and subcaptures are refused; nextCalls fall back to the nearest marker. |
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
| `pixtool_failed` |  | pixtool exited with an error; the message names the operation and pixtool's first error line. |
| `pixtool_start_failed` |  | pixtool could not be started. |
| `pixtool_timeout` | yes | pixtool exceeded its timeout and was terminated (retryable). |
| `pixtool_unavailable` |  | The PIX install has no pixtool.exe. |
| `preparation_failed` |  | The preparation job (analysis, timing, counters, resources) failed; the message carries its error. |
| `preparation_unavailable` | yes | The preparation finished but its data vanished (analysis stopped, handle changed); retry the call (retryable). |
| `preview_invalid_output` |  | pixtool produced something that is not a PNG. |
| `preview_missing_output` |  | pixtool completed without producing a PNG. |
| `preview_too_large` |  | The pixtool PNG exceeds the artifact limit. |
| `result_capacity_exceeded` | yes | Result retention is full (retryable); the message reports usage and the budget variables, nextCalls repeat the originating call and pix_info. |
| `result_expired` |  | The result snapshot was closed, evicted or never existed; nextCalls repeat the originating call. |
| `result_too_large` |  | The selected value exceeds the response budget; read bounded windows (outline first). |
| `server_shutting_down` |  | The server is stopping; queued calls are not started. |
| `sql_execution_error` |  | SQLite failed while running a prepared statement (for example malformed JSON passed to json_each); the message carries SQLite's text. |
| `sql_forbidden` |  | The statement touches something read-only SQL may not (writes, schema changes, PRAGMA, ATTACH, transactions, file or extension functions); the message names the denied action. |
| `sql_interrupted` | yes | SQLite interrupted the statement without a timeout or invalidation; repeat it. |
| `sql_invalid_parameter` |  | A parameter value is not a string, number, boolean or null (pass arrays as JSON text and read them with json_each), uses a positional ? placeholder, or shadows a server-bound name. |
| `sql_missing_parameter` |  | The statement declares a parameter that params does not supply; nextCalls carry a params skeleton. |
| `sql_multiple_statements` |  | Only one statement per call; the text after the first statement is not a comment. |
| `sql_not_read_only` |  | SQLite reports the statement would write (for example VACUUM). |
| `sql_syntax_error` |  | SQLite could not prepare the statement (syntax, unknown table, column or function); the message carries SQLite's text and nextCalls the schema. |
| `sql_timeout` | yes | The statement exceeded timeoutSeconds; narrow the window, add indexed predicates or LIMIT, or raise the budget. |
| `sql_tables_not_populated` |  | The GPU SQL statement reads a family pix_gpu_sql_populate has not materialised; nextCalls carry the populate call (or pass autoPopulate=true). |
| `sql_capacity_exceeded` |  | Populating a family would grow the GPU SQL store past PIXMCP_GPUSQL_MAX_BYTES; the family keeps its previous rows. |
| `sql_store_closed` |  | The GPU capture handle and its SQL store closed; reopen the capture and populate again. |
| `sql_store_busy` | yes | A populate transaction holds the GPU SQL store; retry. |
| `subcapture_invalid_output` |  | PIX does not read the file pixtool wrote as a current-format capture. |
| `subcapture_missing_output` |  | pixtool completed without writing the subcapture. |
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
  timestamps and API text are dropped). `pix_gpu_inspect_event` accepts `brief` and
  `maxStringLength` only; `pix_gpu_overview` also accepts `format = table`, which turns its
  `topPasses` and `topDraws` into positional tables.
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
the call; the top level of such a partial answer is never `pending`. If an overview joins
an existing preparation before the event caches required by its scope or frame selection
are ready, the whole response is `pending` with the same job and an exact retry call.

`pix_gpu_overview` reports:

- `capture`: path, event total, queue count, the capture's `adapter` vendor, and `frames` (frames
  delimited by Present calls, the presenting queue, and how other queues are assigned: `present`
  by replay-clock windows between Present completions, `indexOnly` without timing, `none` without
  a Present).
- `queues`: `kinds` (every kind, with `work` = draw + dispatch + executeIndirect) and replay
  `totals` (busy, span, idle).
- `topPasses`: timed markers with children, with `inclusive` and `self` durations, semantics,
  child overflow, `childCount` and `workCount`.
- `topDraws`: timed work events with `eop`, `exec` and the captured call text in `parameters.raw`.
- `histogram`: work-event EOP durations in the non-empty 1-2-5 buckets (edges 10 us to 10 ms,
  plus overflow), against the busy time of the queues involved.
- `frames` (multi-frame captures only): busy time per frame as the union of TOP..EOP windows,
  up to 50 rows, with nearest-rank percentiles and a nextCall to the slowest frame.
- `insights` (up to eight, warnings first): `vendor_mismatch`, `children_exceed_measured`,
  `untimed_events`, `frame_variance_high`, `queue_idle_high`, `single_pass_dominates`,
  `pass_self_time_high`, `long_tail_draws`, `no_markers`, `executeindirect_heavy` and
  `many_barriers`, each with evidence, implication and follow-up calls that name only registered
  tools and parameters (`includeInsights = false` skips them).

`frameIndex` restricts passes, work events and the histogram to one frame. Capabilities carry
states and reasons; their compatibility-registry notes are in `pix_gpu_info`. `brief = true` keeps
every ranked row but drops execution durations, call text, capability reasons, denominators and
zero kind counts.

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
server's hardware, `transcribed` from vendor plugin strings, `unverified`). Vendor blocks never
match the D3D runtime counters; only `pipelineStatistics` and `depthOcclusion` do.

The presets were checked on one machine with three GPUs (PIX 2606.18-preview), and the real
catalogs live in `tests/PixMcp.Tests/Fixtures/counter-catalogs/`:

| Adapter | Counters | Presets that resolve |
|---|---|---|
| NVIDIA GeForce RTX 4070 Ti | 22 D3D | `pipelineStatistics`, `depthOcclusion` |
| Intel Arc B580 (32.0.101.8991) | 22 D3D + 259 `INTEL:` in 16 groups | every preset (`verified`) |
| AMD Radeon iGPU (32.0.21018.14) | 22 D3D | `pipelineStatistics`, `depthOcclusion` |

Intel counter names carry no unit; the descriptions do (`INTEL: Percentage of time ...`,
`INTEL: Number of ...`), which is where `unit` comes from. `GPU Busy` and `Command Parser Render
Engine Busy` read about 100 on every replayed event; per-stage `XVE Inst Executed ALU0 * Utilization`,
`XVE Threads Occupancy All` and the `XVE Stall *` counters carry the signal. On a new PIX build
or vendor, `PIXMCP_VENDOR_PROBE_OUT=<dir>` with `PIX_TEST_CAPTURE` and `PIX_TEST_ANALYSIS=1` makes
`VendorProbeTests` replay the fixtures on every adapter and write each tool answer to that directory.

Vendor identity is reported everywhere replayed numbers appear: queues carry `vendor`, `pix_gpu_info`
carries the capture's `vendor` and `pix_gpu_overview` its `capture.adapter` (from the capture file's vendor id or device
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

`pix_gpu_inspect_event` combines common queries in one preparation and response. Its sections
are `timing`, `pipeline`, `shaders`, `bindings`, `targets`, `counters`, `occupancy`, `hf` and
`hints`; the default is `timing`, `pipeline`, `shaders`, `bindings`, `targets` and `hints`.
Every answer carries the event `kind` and `parameters`: the captured call parsed into named
arguments (XML element or positional form) with `workItems` (vertices or indices times
instances, thread groups, or ExecuteIndirect's maximum command count). `timing` ranks the event
among the queue's timed work events (`rankInQueue`) and its timed siblings (`rankInParent`, and
`siblings` with median, max and this event's share), reports the EOP duration, the TOP-start to
EOP-end `exec` window and `perWorkItem` cost, and says whether the GPU started the event before
the previous work event finished (`pipelinedWithPrevious`, `previousGapNs`; ancestors never
count as the previous event). `targets` lists bound render targets and depth-stencil views with
format, size, samples, pixel count at the view's mip slice and EOP nanoseconds per megapixel.
`counters`, `occupancy` and `hf` read data already collected on the handle and never collect:
`occupancy` gives PIX's own points for the event (`GetEventPoints`) next to each series held over
the event's window, and `hf` gives window statistics and a heuristic utilization ranking per
collected high-frequency counter set. `hints` flags `zero_work`,
`tiny_dispatch`, `pipeline_latency`, `pipelined`, `dominant_in_parent` and `large_rt_fill` with
evidence, implication and follow-up calls. Timing, bindings and hints share one `inspection`
preparation (analysis, timing and accessed resources); pipeline, shaders and targets need only
analysis, and counters, occupancy and hf need no replay.

`pix_gpu_rollup` answers "where does the time go" in one call. `groupBy` is `marker` (nearest
enclosing marker), `markerPath`, `markerDepth` (PIX's own marker spans at a depth, plus a
`(no marker)` remainder so the rows add up to the queue's sum of roots), `shader` (a work event
counts once per bound shader), `psoKey` (the bound shader set), `kind`, `queue`, `commandList` or
`api`. Each group reports its count, measured and untimed rows, summed EOP as a duration with
queue percentages, avg/min/max, nearest-rank p50/p95, `semantics` and a representative event with
a follow-up call. `metric` is `eop`, `self`, `exec` or `counter` (per-group counter aggregates from
`counterIds` or a `preset`, summed or averaged by unit, optionally `normalize = perMs`). Groupings
other than `markerDepth` add measured event rows (`derivedSum`), never PIX's marker rounds.
`pix_gpu_pipelines` ranks pipelines by summed replay time or use count with their shaders.
`pix_gpu_shaders` takes `sortBy` (`gpuTime` and `useCount` rank largest first) and reports each
shader's `shaderKey` (`hash:STAGE:HASH`) and `gpuTimeMs`; `pix_gpu_shader_uses` accepts
`shaderKey` with `handle`. `pix_gpu_events` with `mode = count` returns only the number of
matching events, and `mode = histogram` counts them per `bucketBy` value.

`pix_gpu_queue_overlap` compares queues on the replay clock: per queue the busy time (the union of
TOP-to-EOP windows of timed leaf events; markers with children are left out because their spans
bridge the gaps being measured), idle and solo-busy time, every queue pair's overlap with a verdict
(`overlapping` at 50 % of the smaller busy time or more, `mostlySerialized` at 10 %, else
`serialized`), the critical-path queue and the longest idle gaps. `pix_gpu_bubbles` lists those
gaps with the leaf events around them, a primary cause taken from the events between the pair
and the event after the gap (`present`, `queueWait`, `queueSignal`, `barrier`,
`commandListBoundary` or `unknown`, in that precedence; a Present, Wait or Signal right before the
gap also counts), how busy the other queues were meanwhile, and totals per cause. `minGapNs` (default
50 us) sets the threshold, and `scope` or `markerPathPrefix` clips every queue to the selection's
window. Replay may serialise queues and adds its own synchronisation, so overlap is an upper bound
and gaps a lower bound. `pix_gpu_overview` embeds the pairs and per-queue gap totals in `overlap`.

`pix_gpu_resources` adds an estimated size (`estimatedBytes` with `estimateMethod`: buffer width,
texels times bits over mips, array slices and samples, or 4x4 blocks for block-compressed formats;
alignment and tiling are ignored) and a `heapKind`, filters by exact `name`, `pixelFormat`, `flags`
and `minBytes`, sorts by `index`, `estimatedBytes`, `name` or `traffic`, and reports
`extra.totals` by dimension, format and heap kind. `scope`, `markerPathPrefix`, `usedAs` and
`sortBy = traffic` read the capture-wide resource-use index (one replay-backed pass over every
event's views, captured API object arguments and barrier arguments) and add `trafficBytes`
(estimated bytes times the events that read or write the resource, an upper bound) and
`extra.scopeSummary` with the selection's render and depth targets. Every resource use carries
`access` (`read`, `write`, `readWrite`, `copySrc`, `copyDst`, `barrier`, `unknown`) and
`evidence`; `pix_gpu_resource_uses` filters by `access` and sorts by event, access or view type.
`pix_gpu_resource_timeline` orders one resource's uses across queues, reports barrier states,
phases of consecutive same-access uses, summary counts and insights (`read_before_write`,
`missing_barrier_between_write_and_read`, `written_never_read`, `bound_as_rtv_and_srv_same_pass`),
and joins replay timing only when it is already collected.

`pix_gpu_drpix_experiments` adds each experiment's `family` (`basic`, `depthStencil`,
`rasterization`, `executeIndirect`, `shaderCorrectness`, `debugBreak`, `memory`, `vendor`,
`other`), `whatItProves` and `supportsRanges`, with `category`, `family` and `source` filters.
`pix_gpu_drpix_run` runs every selected experiment (`experiments`, `categories`, `families`) over
every range: `scope`, one range per top-most marker that `markerPathPrefix` matches, or
`wholeCapture`; `perEvent = true` splits each range into one run per draw, dispatch or
ExecuteIndirect, and `maxRuns` (default 50) bounds the replays. The result lists `runs[]` with
structured `records` (values keyed by label, percent strings as numbers, PIX navigation links as
`pixnavlink:` URIs), a `timing` pair (baseline and experiment milliseconds, saving and
`semantics` `detected` or `inferredByOrder`) and `rangeIgnored` for experiments that always run
over the whole capture, plus `ranges[]`, a one-row-per-run `table`, `savings` with implications, a
`summary` and `notes`. While the job runs, its status offers `partialResultRef` with the runs
finished so far; a cancelled or failed job keeps them as a `partial` result.

`pix_gpu_compare` compares two captures, or two scopes or frames of one capture
(`baselineScope`, `candidateScope`, `baselineFrame`, `candidateFrame`, `markerPathPrefix`; marker
paths are taken relative to a scope). The summary adds `totals` (busy time per side and queue,
the delta, the share the largest changes explain, and time only one side has), `byMarkerPath`
(deltas rolled up to `rollupDepth` levels, listed without ancestor and descendant double counting;
the full list is at `/byMarkerPath`), `noise` (with `repeats` of 2 to 5 each side's timing is
collected again, by recollection or an analysis restart, and events report median EOP and spread;
changes inside the spread are `belowNoiseFloor`), provenance mismatch `warnings`, and `codeDiffs`
(HLSL line diffs for matched events whose shader hash changed). Events that share a marker path
pair by order when both sides have the same count (`ordinalMatching`, confidence `low`); bindings
compare as sets and ignore descriptor-heap indices unless `includeDescriptorHeapIndices` is true.
`pix_gpu_compare_changes` also filters by `markerPathPrefix`, `kind`, `queueIndex`, `section`,
`direction = structural`, `excludeBelowNoise` and `minConfidence`.

`pix_gpu_bottleneck` classifies what limits one scope (`scope` or `markerPathPrefix`, required)
as a job. Evidence comes in stages: `timing` (always; idle time in the scope window, work pending
before execution, small dispatches), `counters` (the `preset`, default `utilization`, plus D3D
pipeline statistics and depth occlusion over the scope's work events, with derived ratios and
per-cache hit rates; for a vendor with a `counters` list in `bottleneck-rules.json`, currently
Intel, also the XVE utilization, occupancy, stall, memory, depth-latency and cache counters its rules
read), `occupancy` and `hf` over the scope's replay window, `drpix` (up to `maxDrPixRuns`
experiments, each a replay) and `shaderProfile` (a pointer, not scored). Rules in the embedded
`bottleneck-rules.json` score the limiters `pixelShading`, `vertexOrGeometry`, `rasterOrDepth`,
`memoryBandwidth`, `cacheMiss`, `occupancyLatency`, `launchOverhead` and `syncIdle`. The result
carries a `verdict` with `confidence`. High confidence needs two evidence sources, a clear margin,
every requested stage the PIX build and GPU support, and a vendor block validated on hardware.
Only `intel` is validated so far: on an Arc B580 the perf fixture's full-screen Lighting pass reads
XVE ALU0 PS utilization 58 % next to a 97 % 1x1-viewport saving. Occupancy and HF counters return
E_NOTIMPL on every GPU with 2606.18, and those `unsupported` stages do not lower confidence.
The result also carries `alternatives`, the
`evidence` table, `ruleResults`, `recommendations` with calls, `coverage` per stage and a
`detailRef` with every row. Results are cached per scope and evidence until analysis stops.

`pix_gpu_counters_read` reads every queue unless `queueIndex` is given, and each row carries the
event's replay `eop` and `exec` durations (`includeTiming`, default true, collects timing in the
same job). `normalize` divides values by the event's EOP milliseconds (`perMs`), a dispatch's
thread groups (`perThreadGroup`) or the pixels of a draw's largest bound render target
(`perPixel`); percent, ratio, rate, boolean and bitmask counters are never normalized, and rows
without a divisor say why in `normalizeReason`. `derived` adds ratio columns
(`{ name, numeratorId, denominatorId }`). `sortBy` is `index`, `name`, `eop` or `counter` (with
`sortCounterId`), and `filterCounterId` with `minValue`/`maxValue` filters rows. A `groupBy` other
than `none` returns `pix_gpu_rollup` counter aggregates over the same collected set, so no second
counters job starts.

`pix_gpu_occupancy` reads the occupancy collected with the timing pass when PIX provides it
(`source = timingPass`, collecting timing first) and replays only as a fallback or with
`forceStandalone = true` (`source = standaloneReplay`); `timingPassProbe` records what PIX
reported. Each series adds `peakPercent`, a time-weighted `timeWeightedAveragePercent` and
`activeDurationNs`, and each point its `percent` of the type's maximum slots; points hold until
the next one. `scope` or `markerPathPrefix` add `window` statistics over the selection's
replay-clock window after a `clockCheck` of the series range (window numbers are withheld on
`mismatch`), `eventRef` adds PIX's own points for that event next to the series held over its
window, and `groupBy` (`event`, `marker`) lists per-series averages and peaks for the longest
work events or the scope's child markers. `pix_gpu_hf_counters` works the same way for a counter
set (`setIndex` or `setName`): samples from the timing pass when present, per-counter `window`
statistics (count, min, max, average, time-weighted average, coverage, held value, nearest
sample), `groupBy = event`, and a heuristic `utilizationRanking` when three or more percent-unit
counters exist. The ranking never states a verdict.
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

### Static shader profiling

`pix_shader_targets` lists the AMD and Intel GPU targets that PIX can compile for with the offline
compilers shipped in its install, whatever GPU the machine has. On 2606.18 these are Intel Xe2
(Battlemage, Lunar Lake) and Xe3 (Panther Lake), and AMD RDNA3, RDNA3.5 and RDNA4 families. Target
ids can change between PIX releases, so pass an architecture (`Xe2-HPG`), family (`gfx1201`) or
adapter name; `extra.families` gives one stable spelling per family.

`pix_gpu_shader_static_profile` compiles a pipeline for one target as a job. Pass `sources` to
profile HLSL without a capture, or `shaderRef` / `shaderKey` to profile a captured shader whose
HLSL the capture holds (analysis starts if needed, and the capture's pipeline state, root
signature and application description are used). Each shader summary reports the instruction
mix with fixed cycle estimates, register pressure, loops from the control flow graph, hot spots
weighted by loop depth, HLSL source lines where the vendor maps them, compiler warnings and, for
AMD, the compiler's resource usage. Every block and instruction is under `/detail`. A preprocess
or compile error is data: `succeeded: false` with `phase`, `compilerOutput` and `hints`.

Behaviour observed on PIX 2606.18:

- Defines travel to the compiler as `-DNAME=VALUE` arguments; `coverage.definesFormat.verified`
  reports whether the probe confirmed it.
- Inline pipelines start from PIX's defaults, whose root signature is empty. A shader that binds
  resources declares its root signature with `[RootSignature("...")]`, and the server then drops
  the default. A graphics pipeline needs its vertex shader and uses a triangle topology with one
  `R8G8B8A8_UNORM` render target unless `pipeline` says otherwise.
- AMD reports no source mapping. Intel reports none for pixel shaders, returns a placeholder shader
  hash and fails mesh pipelines. Weights and cycles are static estimates, not measured GPU time,
  and the server does not estimate theoretical occupancy.

`pix_gpu_shader_profile` profiles the same shaders live on the local GPU when its driver supports
it. The result totals samples and stall samples per shader and across shaders, names each shader's
dominant stall with a keyword heuristic (memory latency, dependencies, instruction issues, control
flow), lists the `topN` hottest instructions by ISA byte offset with matched `shaderRef`s, and keeps
every instruction under `/detail`. Samples are counts, not time. A driver without live profiling
returns a cached `unavailable` marker that points at static profiling.

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
`pix_gpu_preview` uses the installed `pixtool.exe` to replay and export RTV slots or the
depth target. It returns artifact references, dimensions, and selection metadata;
`pix_gpu_preview_image` retrieves inline MCP image content and
`pix_gpu_preview_bytes` pages PNG bytes.

**Stop analysis on every open GPU capture first.** A conflicting request returns
`analysis_active` with the required stop calls. Preview jobs execute exclusively on
the PIX worker, with a hidden process, deadline, cancellation, and temporary-file cleanup.
Each replay uses a temporary capture copy because the native document keeps the original
open. CLI replay uses its own defaults, independently of native analysis adapter settings.

Selection follows pixtool: `eventRef` picks that exact event through `--global-id` (the image is
the resource's contents after the event), a unique exact `markerName` picks its last child with the
resource bound, and neither picks the last event with it bound. The event needs a GPU id (draws,
dispatches, clears and `SetMarker` labels have one; `BeginEvent` markers do not). The first
`eventRef` preview or subcapture of a capture chains `save-event-list` and compares up to 64 of
pixtool's Global IDs with the native GPU ids (pixtool lists only the first queue): `verified` needs
at least eight matching rows and turns the `exactEventPreview` capability `supported`; a mismatch
fails with `global_id_mismatch` (nextCalls fall back to the nearest marker) and disables exact
selection for that capture; `inconclusive` proceeds with a `warning` and checks again next time.
`targets` saves up to eight RTV or depth images from one replay. pixtool stops at the first command
that fails, so one missing render target fails the whole job. Marker names containing literal
double quotes or control characters are unsupported by the CLI argument parser.

Artifacts are memory-resident, with a 50-item/64 MiB cache and a 32 MiB per-image limit.
Inline images have a 4 MiB limit; oversized originals get a thumbnail automatically,
while byte paging retains the original PNG. Closing the capture
expires its artifacts.

### Subcaptures

`pix_gpu_subcapture(handle, scope | markerPathPrefix)` runs pixtool `recapture-region` over the
first and last GPU ids of the selection (a prefix must match exactly one marker subtree) and writes
a smaller `.wpix`, by default `<capture>.sub-<first>-<last>.wpix` beside the source (`outPath` and
`overwrite` follow the usual file rules). The job checks the Global ID mapping as previews do,
confirms that PIX reads the output as a current-format capture, and with `open: true` (the default)
opens it as a new handle whose `pix_gpu_info.derivedFrom` names the source handle, scope and GPU
ids. A subcapture holds the region's GPU work plus the state setup pixtool adds, and its Global IDs
restart at 1, so its totals, rollups and frame reports describe the region, not the original frame.

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

### Replay flags

`pix_gpu_analysis_start` accepts the eight `PIX_ANALYSIS_FLAGS` and `NONE` by short name
(`IGNORE_INCOMPATIBILITIES`, `USE_REPLAY_ARGUMENT_BUFFERS`, `USE_SINGLE_COMMAND_QUEUE`,
`ENABLE_DEBUG_LAYER`, `ENABLE_RECREATE_AT_GPUVA`, `ENABLE_APPLICATION_SPECIFIC_DRIVER_STATE`,
`DISABLE_GPU_PLUGINS`, `FORCE_SET_APPLICATION_SPECIFIC_DRIVER_STATE`); the `PIX_ANALYSIS_FLAG_` and
`PIX_ANALYSIS_` prefixes are optional and the input schema lists the names under `items.enum`. An
unknown name fails with `invalid_arguments` and a retry that keeps the recognised flags.
`pix_gpu_analysis_status` and replay provenance report `flagsDecoded` (names and meanings) and
`flagsSource`: `explicit` when flags were passed, `pixDefault` when PIX chose, in which case the
effective flags are not observable.

`pix_gpu_analysis_start` also takes `adapterName`: an exact adapter name, a unique part of one
(`"Arc B580"`) or a vendor (`intel`, `amd`, `nvidia`, `warp`); unknown or ambiguous names fail with
`invalid_arguments` listing the adapters. Adapter ids derive from the adapter LUID and change
between boots. PIX declines to replay a capture taken on another vendor's GPU unless
`IGNORE_INCOMPATIBILITIES` is set (observed: an NVIDIA capture on an Intel Arc B580 fails with
0x8ABC006B). The server reports that as `analysis_incompatible` with a retry that adds the flag;
replay provenance then shows `vendorMismatch`.

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

`pix_device_timing_capture_start` takes a `preset` (`default`, `memory`, `fileIo`, `gpuOnly`,
`minimal`) and explicit arguments that override it: CPU samples and stacks, context switches and
`contextSwitchStacks`, PIX events, GPU timing and memory usage, file I/O and `fileIoStacks`, the
event levels `virtualAllocEvents`, `heapAllocEvents`, `pixMemEvents` and `pageFaults` (`none`,
`enabled`, `withStacks`), tracked functions, kernel image merging, stacks for all processes, and the
option parts `captureSysmonCounters`, `video` (with `videoSourceType` and `videoSourceId`), `clrData`,
`includeCaptureEtl`, `circular`, `forceComPath`, `gpuOnlyEvents` and `minimalInstrumentation`.
Enable `contextSwitchStacks` and `captureSysmonCounters` for the tutorial's CPU/GPU investigation.
The response echoes the effective `settings`, the `optionParts` sent to PIX (the sysmon part is
always sent, the others only when enabled) and capture notes; a failed start names the parts and
suggests the default preset. Memory events, page faults and stacks can produce multi-GB captures.

`pix_device_take_gpu_capture` takes `delimiter` (`present`, or `capturableRegion` for apps that call
`ID3D12SharingContract::BeginCapturableWork`/`EndCapturableWork`) and `captureKey` (`none` or
`F1`–`F12`, always written so an earlier hotkey is cleared); `captureOptions` in the result echoes
both. With `thumbnail=true` (the default) the result carries `thumbnail { available, artifactRef,
width, height }`: PIX's screenshot kept as a preview artifact owned by the opened capture, or by the
device handle when `open=false`, readable with `pix_gpu_preview_image` until that handle closes.
Launch Unreal with `-PIX -statnamedevents` for timing capture and keep GPU-capture
injection (`-attachPIX`, or `underGpuCapture=true`) for separate GPU-capture runs.

Start recorded analysis with `pix_timing_overview`, then use `pix_timing_events`,
`pix_timing_counters_list`, and `pix_timing_counters_read`. These query the timing
document's PixStorage database read-only, off the PIX worker; they do not replay the GPU.
Times use decimal nanoseconds and half-open `[startNs,endNs)` intervals. The default
`rangeMode=full` runs from the first reliable timestamp through the capture end;
`rangeMode=reliable` stops at the capture stop timestamp. Every response's provenance
reports both ends and `coverage` (in-window versus total context switches, CPU events,
GPU submissions and GPU hardware ranges), so a truncating window is visible.

`pix_timing_events` pages one or all recorded families (`domain`):

- `cpu`: PIX CPU events with nesting level and thread. `executionNs` and `stallNs` come
  from one `CpuExecutionRowId` lookup per event (`executionTimingMethod: rowId`), or from
  the (event, begin, end) tuple when that column is absent (`tupleMatch`).
- `cpuMarkers`: PIX CPU point markers (zero-length rows).
- `gpuMarkers`: GPU-side PIX events, empty unless the application emits them.
- `gpuSubmissions`: one row per ExecuteCommandLists with `submitNs`, `submitLatencyNs`,
  `commandListCount` and a `submissionRef` for `pix_timing_submissions`. This is the real
  GPU timeline of a timing capture.
- `gpuHardware`: hardware queue work ranges (`hardwareQueueName`, `overlapLevel`); rows
  are containers of many packets, not individual work items.

Rows preserve original event duration and selected-range overlap; `sources` lists each
family's state and matching rows.

`pix_timing_overview` reports every recorded family (`cpuEvents`, `cpuMarkers`, `gpuMarkers`,
`gpuSubmissions`, `gpuHardware`, `threadSwitches` and more) with its state and row count, thread
lifetimes with per-thread event, marker and context-switch counts, API queues with their adapter,
lifetime and command-list counts, and a `hardwareQueues` page of the kernel queues that carried
GPU work. Pages default to 5 rows.

Its `sections` summarise the capture with the same named queries `pix_timing_sql` runs, so the two
never disagree: `capture` (target process, PIX version, OS, CPU, memory, GPU and capture options as
recorded), `dataQuality` (dropped, truncated and lost ETW
events, effective samples per second), `gpu` (per-queue submissions, busy share of the window and
submit latency), `frames` (VSync pacing), `cores`, `modules` (symbol state) and `vram` (usage
against budget). `insights` lists up to eight findings, warnings first, each with its evidence,
what it implies, and the exact follow-up calls, for example `window_truncates_data`,
`dropped_data`, `vram_over_budget`, `gpu_idle_high`, `submit_latency_high` and `no_gpu_markers`.
A section whose tables the capture lacks is null and named in `unavailable`.

`pix_timing_hotspots` ranks sampled CPU functions and addresses;
`pix_timing_calltree` pages caller-to-callee paths using a reusable `profileRef`.
On CPUs with more than one core efficiency class, both report `samplesByEfficiencyClass` in
coverage and `inclusiveByEfficiencyClass` per hotspot, and `efficiencyClass` restricts the profile
to samples taken on cores of that class (the `core_efficiency` named query lists the classes).
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

`pix_timing_gpu_summary` rolls recorded GPU work up per API command queue of the target process
(or `processId`, `queueId`). Submissions are selected by CPU submit timestamp, like
`pix_timing_submissions`, and each queue reports:

- `submissions`, `validSubmissions` and `invalidReasons` (`missingTimestamps`, `zeroDuration`,
  `inconsistentTimestamps`).
- `totals`: busy (the union of valid execution intervals clipped to the window, so overlapping
  submissions count once), idle, span and summed execution, each with its share of the span and
  the sum, plus `busyPercentOfWindow`.
- `submitLatency` and `execution` statistics (count, average, nearest-rank p50/p95, maximum).
- `topSubmittingThreads` with per-thread latency and `longestExecutions` with their `submissionRef`.

The response also carries busy time per hardware queue (system-wide), VSync pacing per monitor, and
`cpuGpuCausality`, which is `empty` unless the application emitted GPU-side PIX markers. Recorded
execution spans whole ExecuteCommandLists calls, not individual draws.

`pix_timing_tree` aggregates one lane's recorded PIX events by marker path: a thread's CPU events
(`threadRowId`) or an API queue's GPU-side events (`queueId`, empty unless the application emits GPU
markers). Events nest by recorded level and interval. Each path reports `occurrences`, `inclusive`
and `self` time clipped to the window (with shares of the lane span, the top-level sum and the
parent path), complete-occurrence duration statistics, `executionNs` and `stallNs` sums on thread
lanes (up to 2000 events per call), and `childrenExceedMeasured` with `childOverflowNs` when a child
runs past its parent. Walk it with `parentPath`, `depth`, `sortBy` (`inclusive`, `self`,
`occurrences`, `name`, `firstStart`) and `minSelfNs`, and sum siblings, never ancestors. The
slowest occurrence of the first path links to `pix_timing_hotspots` and `pix_timing_thread_switches`.

`pix_timing_verdict` (experimental) classifies recorded frames without replay:

- Frames come from `frameSource`: `present` (GpuFrame), `cpuMarker` (the render thread's most
  repeated top-level PIX event, or `frameMarkerName`), `vsync` (the busiest VSync lane) or
  `submission` (render-thread submit cadence); `auto` takes the first available in that order.
- The render thread is the thread with the most submissions unless `renderThreadRowId` is given.
- Per frame it measures GPU busy time (the union of recorded queue execution) and the render
  thread's on-CPU, blocked and ready-not-running time from its context switches. ContextSwitch does
  not record thread states, so wait reasons 30-33 and 38 count as ready and other waits are blocked
  until the ready event named by the next switch-in.
- Ordered rules, returned with their thresholds, assign `gpuBound`, `unknown`, `presentBound`,
  `cpuBound`, `syncBound` (blocked while the GPU is busy), `waitBound` (blocked while the GPU is
  idle), `contended` or `balanced`.

The response carries the dominant verdict with a confidence, duration-weighted shares, frame and
ready-latency statistics, the five longest frames, time per wait reason with probable KWAIT_REASON
names, per-queue busy shares, a video-memory budget check, a paged per-frame table (`maxFrames`,
`offset`, `limit`) and follow-up calls to hotspots, thread switches and the marker tree of the
relevant frames.

`pix_correlate` joins a GPU capture's timed marker passes (`gpuHandle`, optionally `queueIndex`,
`scope`, `markerPathPrefix`) to a timing capture's recorded PIX markers (`timingHandle`,
`processId`, window). It matches the full marker path first and then a leaf name unique on both
sides, after normalizing case, whitespace, the legacy `<deprecated - use pix3.h instead>` prefix
and trailing numbers (each match lists the normalizations it needed). Each match reports the
replayed inclusive EOP time, the recorded occurrences and duration statistics, submissions made
inside those occurrences, the emitting threads' blocked and ready time and
`ratioRecordedToReplay`; unmatched paths on both sides and a queue map by type and name follow.
Names are not identity: `identity` says so, recorded occurrences are averaged and the clocks
differ. The GPU side replays the capture when its timing is not prepared.

### SQL over timing captures

`pix_timing_schema` describes the capture's PixStorage SQLite database: base tables, the PixStorage
virtual tables (`ContextSwitch`, `PixCpuExecution`, `PixCpuExecutionTimes`, `PixCounters`,
`PixCpuMarker`, `PixGpuExecution`, `FileEvents`, `CpuMemoryEvent`) with hidden constraint columns,
documented units, joins and caveats, `findstackid`, capture facts, capability probes, the
pre-bound parameters and the named query library. Pass `table` for DDL, indexes and sample rows.

`pix_timing_sql` runs one read-only statement, or `query` naming a library entry, off the PIX worker:

```json
{ "handle": "timing-1", "sql": "SELECT Core, COUNT(*) AS switches FROM ContextSwitch WHERE Timestamp >= $start AND Timestamp < $end GROUP BY Core ORDER BY switches DESC", "maxRows": 10 }
```

- `$start` and `$end` (the window per `rangeMode` = `full` or `reliable`, or `startNs`/`endNs`),
  `$reliableStart`, `$reliableEnd`, `$captureEnd` and `$targetPid` are bound when the statement
  declares them. `params` binds your own `$name` values (scalars; arrays as JSON text read through
  `json_each`).
- Rows are positional, with `columns` (declared type, affinity, documented unit),
  `referencedTables`, `boundParameters`, truncation state (`hasMore`, `truncationReason`,
  `nextOffset` and an exact continuation call), `countTotal` and `explain` (EXPLAIN QUERY PLAN).
- A SQLite authorizer allows only SELECT, reads, recursive CTEs and non-file functions. Writes,
  PRAGMA, ATTACH, transactions and `load_extension` fail with `sql_forbidden`; a second statement
  fails with `sql_multiple_statements`; `timeoutSeconds` (default 30, max 120) bounds execution
  (`sql_timeout`). Save, symbol resolution and close interrupt running statements
  (`timing_query_invalidated`).
- Named queries (`pix_timing_schema` lists their parameters, requirements and caveats):
  - Capture and data quality: `capture_facts`, `dropped_data`, `module_symbols`, `core_efficiency`.
  - GPU: `gpu_busy_per_queue` (busy time as the union of execution intervals),
    `submit_latency_per_thread`, `gpu_hardware_queues`.
  - Frames: `frames_vsync` (intervals and p50/p95 per monitor), `frames_present`.
  - Scheduling: `thread_summary`, `context_switches_per_thread`, `context_switch_waits` (time
    switched out per raw wait reason), `ready_thread_latency`.
  - CPU, counters and memory: `cpu_execution_rollup`, `cpu_markers`, `counters_bucketed`
    (requires `counterId`), `vram_budget`, `file_io_summary`, `memory_summary`.
- Row ids are not OS ids: submissions, markers and executions reference `Threads.Id`, and queues
  and counters reference `Processes.Id`; `pix_timing_schema` documents each join.

### SQL over GPU captures

GPU captures are not SQLite, so `pix_gpu_sql_populate` materialises them into a private SQLite
store per handle (in the session's private result directory, deleted when the handle closes) as
one job with one transaction per family. `core` (capture, queues, events with kinds, marker
paths, subtree bounds and frames, parsed call arguments, work items) and `resources` need no
replay. `timing` (timing rows, the timing tree, queue totals), `shaders` (shader identities and
per-event pipeline keys), `counters` (one counter set from `counterIds` or a `preset`),
`resourceUses` (views bound per work event with a coarse access class) and `psos` (pipeline
state JSON per work event) replay the capture if analysis is not started. `tables = all`
populates core, timing, shaders and resources; counters, resourceUses and psos must be named.
Ready families are skipped unless `force = true`, and rows of replay families populated under an
earlier analysis are reported `stale` but still answer. `PIXMCP_GPUSQL_MAX_BYTES` caps the store
(default 1 GiB); a family that would exceed it rolls back with `sql_capacity_exceeded`.

`pix_gpu_sql` runs one read-only statement or a named library query (`top_passes`,
`cost_by_marker`, `cost_by_kind_per_queue`, `cost_by_shader`, `cost_by_pso`,
`dispatch_cost_per_workitem`, `resource_flow`, `barriers_per_pass`, `counters_for_events`,
`counter_ratio`, `frames_summary`, `kinds_by_queue`, `untimed_events`, `unbalanced_markers`) off
the PIX worker, under the same guard as `pix_timing_sql`. SQLite's authorizer first reports the
tables the statement reads: a family that is not populated fails with `sql_tables_not_populated`
and the populate call, or starts that job with `autoPopulate = true` (never for counters).
`$handle`, `$scopeQueue`, `$scopeFirst` and `$scopeLast` (the `scope` event's subtree) and
`$markerPathPrefix` are pre-bound, and the library queries honour them. Results have the timing
SQL shape with `source = gpusql` and `provenance.tableStates`. `pix_gpu_sql_tables` describes
families, documented tables, the views (`v_work`, `v_events_timed`, `v_passes`, `v_shader_cost`,
`v_counters_joined`) and the named queries without touching the worker, and
`pix_gpu_sql_export` streams every row to CSV or JSON as a job. Occupancy, high-frequency
counters and Dr. PIX runs are not materialised yet.

### Dump triage

`pix_dump_triage` ranks deterministic evidence: page faults with resource lifetime information,
PIX's own diagnosis (error code, bucket, GPU status and brief summary, returned as `diagnosis` and
labelled heuristic), D3D runtime journal failures (the last 200 entries, up to 20 recent errors),
queue hardware status entries of WARNING severity or above, shader exceptions, in-progress and
possibly-completed events, GPU state tables whose name or description mentions a fault, hang,
timeout, error, status, reset or exception, and DRED breadcrumb boundaries. Event and hardware
status observations on a queue that also has page faults or in-progress events gain 10 priority
points (capped at 99, below page faults). The event walk visits at most `maxEvents` events
(default 5,000, maximum 50,000); a truncated walk reports `truncated` coverage and continuation
calls. `eventStatusCountsByQueue` splits the status counts per queue, and each section's coverage is
`available`, `absent`, `unavailable`, `truncated` or `unsupported`. `IPixD3DState` has no managed
projection in PIX 2606.18, so `d3dState` is always `unsupported`. Triage includes coverage and
follow-up calls rather than claiming a definitive cause. `pix_dump_info` returns the same
`diagnosis`, and `pix_dump_queues` rows add `maxHardwareSeverity`.

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
| Investigation | `pix_gpu_overview`, `pix_gpu_inspect_event`, `pix_gpu_bottleneck`, `pix_gpu_rollup`, `pix_gpu_pipelines`, `pix_gpu_queue_overlap`, `pix_gpu_bubbles`, `pix_gpu_compare`, `pix_gpu_compare_changes` |
| GPU SQL | `pix_gpu_sql_populate`, `pix_gpu_sql`, `pix_gpu_sql_tables`, `pix_gpu_sql_export` |
| GPU capture | `pix_gpu_open`, `pix_gpu_info`, `pix_gpu_queues`, `pix_gpu_events`, `pix_gpu_event`, `pix_gpu_api_objects`, `pix_gpu_screenshot` |
| Analysis | `pix_gpu_analysis_start`, `pix_gpu_analysis_status`, `pix_gpu_analysis_adapters`, `pix_gpu_analysis_stop` |
| Timing/counters | `pix_gpu_timing_prepare`, `pix_gpu_timing_events`, `pix_gpu_timing_tree`, `pix_gpu_counters_list`, `pix_gpu_counters_prepare`, `pix_gpu_counters_read`, `pix_gpu_occupancy`, `pix_gpu_hf_counters` |
| Pipeline/shaders | `pix_gpu_pipeline_state`, `pix_gpu_shaders`, `pix_gpu_shader_uses`, `pix_gpu_shader_code`, `pix_gpu_shader_search`, `pix_gpu_shader_diagnostics`, `pix_gpu_shader_profile`, `pix_shader_targets`, `pix_gpu_shader_static_profile` |
| Resources | `pix_gpu_resources`, `pix_gpu_resource`, `pix_gpu_event_resources`, `pix_gpu_resource_uses`, `pix_gpu_resource_timeline`, `pix_gpu_heap` |
| Preview | `pix_gpu_preview`, `pix_gpu_preview_image`, `pix_gpu_preview_bytes` |
| C++ export | `pix_gpu_export_cpp`, `pix_gpu_subcapture` |
| Unreal CSV | `pix_csv_compare`, `pix_csv_pass_candidates` |
| Dr. PIX | `pix_gpu_drpix_experiments`, `pix_gpu_drpix_run` |
| Timing captures | `pix_timing_open`, `pix_timing_overview`, `pix_timing_gpu_summary`, `pix_timing_tree`, `pix_timing_verdict`, `pix_correlate`, `pix_timing_schema`, `pix_timing_sql`, `pix_timing_events`, `pix_timing_submissions`, `pix_timing_thread_switches`, `pix_timing_counters_list`, `pix_timing_counters_read`, `pix_timing_hotspots`, `pix_timing_calltree`, `pix_timing_resolve_symbols`, `pix_timing_save` |
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
Python harness tests and gates. The self-hosted `pix` job runs on demand and on every push to main. It
builds the fixture, generates captures, and then runs the native tests serially. Set `PIX_TEST_TIMING_CAPTURE`
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

Rich fixture flags compose; an unsupported feature prints `fixture: skipped <flag>: <reason>` and the
app keeps rendering. `--depth` adds D32 depth and a depth-rejected triangle, `--placed-heap` a heap with
placed resources, `--reserved` a 4096x4096 reserved texture with four mapped tiles, `--indirect`
ExecuteIndirect draws, `--async-overlap` heavy compute with a graphics-queue wait on the compute fence,
`--msaa N` a multisampled target with a resolve pass, `--mrt 2` a second float render target,
`--bandwidth` a 2048x2048 float texture sampled eight times per pixel, `--hdr` a float swap chain,
`--gpu-markers` WinPixEventRuntime command-list markers (which timing captures record), `--dxc` runtime
DXC at shader model 6 with embedded debug information, `--mesh` a mesh-shader quad,
`--programmatic-capture PATH` a `PIXGpuCaptureNextFrames` call at `--capture-at N` for
`--capture-frames N`, and `--workload perf` a 1080p offscreen Shadow, GBuffer, Lighting and Post frame
with a copy-queue upload whose candidate doubles the Lighting cost. `--report PATH` writes the effective
flags, skips, adapter and DXC version as JSON.

Generate baseline, candidate, and symbol-resolved timing captures together, or another fixture profile:

```powershell
python scripts\capture_fixtures.py src\PixMcp\bin\x64\Release\net10.0-windows10.0.26100.0\PixMcp.exe
python scripts\capture_fixtures.py src\PixMcp\bin\x64\Release\net10.0-windows10.0.26100.0\PixMcp.exe --profile rich
```

`--profile rich` writes `rich.wpix` (three frames) and `rich-timing.wpix`, `perf` writes
`perf-baseline.wpix` and `perf-candidate.wpix`, `sm6` writes `sm6.wpix` (`--dxc --mesh`),
`programmatic` writes `programmatic.wpix`, and `all` writes every set. The report embeds each launch's
`--report` sidecar and the repository commit. Timing fixtures (`timing`, `rich-timing`) and the GpuFrame
experiment start the PIX timing recorder, which can raise a UAC prompt; approve it at the desktop, or the
start fails with 0x800704C7 once the prompt times out. Native tests read the captures through
`PIX_TEST_RICH_CAPTURE`, `PIX_TEST_RICH_TIMING_CAPTURE`, `PIX_TEST_PERF_BASELINE`,
`PIX_TEST_PERF_CANDIDATE`, `PIX_TEST_SM6_CAPTURE` and `PIX_TEST_PROGRAMMATIC_CAPTURE`.

`--adapter-name` makes the fixture app render on one GPU. For example, `--adapter-name B580
--output-dir tests\artifacts\intel` or `--adapter-name Radeon --output-dir tests\artifacts\amd`
captures on the Intel Arc or the AMD iGPU; GPU capture profiles need no UAC prompt. Set
`PIX_TEST_VENDOR_VALIDATION=1` to run `VendorValidationTests` on every GPU present, with the NVIDIA
captures above and, when set, `PIX_TEST_INTEL_CAPTURE`, `PIX_TEST_INTEL_PERF_BASELINE`,
`PIX_TEST_AMD_CAPTURE` and `PIX_TEST_AMD_PERF_BASELINE`. They check:
- captures replay on their own vendor without flags, and cross-vendor starts need `IGNORE_INCOMPATIBILITIES`;
- occupancy and HF counters are unsupported;
- the live-profiling and Bandwidth outcomes per vendor;
- the Intel counter catalog and the Lighting calibration pass.
`scripts\gpu_frame_experiment.py` records which timing capture option parts, applied through
`PIXMCP_TIMING_EXPERIMENT_PARTS`, populate `GpuFrame`.

PIX-free timing tests run on `tests\PixMcp.Tests\Fixtures\timing-synthetic.sqlite`, a committed
PixStorage-shaped database: every table and index of the recorded 2606.18 schema with its real DDL, the eight
pixstorage.dll virtual tables as plain stand-ins without hidden columns, and the rows of `timing-fixture.json`.
`scripts\pixstorage_schema.py <timing.wpix>` records that schema (105 tables, 41 indexes, 8 virtual tables with
their columns, `findstackid/2`) in `pixstorage-schema-2606.18.json`, and `--check` reports drift on a new PIX
build. `scripts\make_timing_fixture.py` rebuilds the database; it refuses to change the logical hash (SHA-256
over the sorted dump, stored in `timing-synthetic.sqlite.sha256`) unless `--update-hash` is passed, and
`--check` verifies the committed file. With `PIX_TEST_TIMING_CAPTURE` set, `TimingNativeSchemaTests` compares
the real capture with the recorded schema column by column. It also checks that writes are refused without
touching the file, and that row caps and timeouts return exact continuations.
With `PIX_TEST_CAPTURE` set, `GoldenCaptureTests` compares every event of `tests\artifacts\baseline.wpix` and
`candidate.wpix` with `tests\PixMcp.Tests\Fixtures\golden\*.events.json`. A header records hashes of the
capture, `main.cpp` and `build.cmd`, plus the PIX build, so a regenerated fixture fails with
`golden_header_mismatch`. Set `PIXMCP_UPDATE_GOLDEN=1` to rewrite the goldens, and review the diff.
`capture_fixtures.py` writes the same hashes into its report. `Fixtures\tool-names.txt` pins the registered
tool set, and the README tool catalog must name every tool once. The `gpu-overview-golden` scenario asserts
the baseline capture's golden queue and kind counts through the stdio transport.

`scripts\smoke.py` runs scripted MCP scenarios:

```powershell
python scripts\smoke.py src\PixMcp\bin\x64\Release\net10.0-windows10.0.26100.0\PixMcp.exe @scripts\scenarios\capture-and-inspect.json
```

`take-capture` produces a capture. `open-capture`, `analysis-pending`, `inspect-extras`,
`capture-format` and `gpu-overview-golden` accept `PIX_TEST_CAPTURE`. `device-inventory` lists local
processes, packaged apps and counters, and `jobs-and-log` reads the session tables; neither needs a capture. Tool/protocol errors, failed jobs,
unresolved references, and failed assertions fail the scenario.

`timing-sql` accepts `PIX_TEST_TIMING_CAPTURE`. It checks the PixStorage schema census, one table detail, a
named query and a paged `ContextSwitch` query through the stdio transport.

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
The self-hosted workflow exposes this as `strict_benchmark`. The report embeds the
environment (`scripts\environment.py`: commit, server and PIX versions, GPU adapters, capture hashes and
options). The `pix_jobs` inventory taken after each task counts as that task's `overhead`, not its calls.
`scripts\benchmark-budgets.json` caps each task's calls and returned JSON bytes and the wire-to-JSON ratio
per text mode, and `--enforce-budgets` fails a run more than 10 % over a budget. `--baseline previous.json`
marks tasks whose calls or bytes grew more than 25 %. Elapsed time stays advisory.

The harness gates run without PIX:

```powershell
python scripts\smoke.py --validate-scenarios scripts\scenarios
python scripts\tool_coverage.py --fail-on-uncovered
python scripts\check_docs.py
python scripts\make_timing_fixture.py --check
```

`--validate-scenarios` checks each scenario's shape, tool names and step references. `smoke.py --all
scripts\scenarios --skip-missing-env <PixMcp.exe>` runs every scenario on its own server, skipping the
manual `provoke-hang` and naming any scenario whose `$env` variables are unset. `tool_coverage.py` requires
every registered tool to be exercised by a C# test call, StdioTests, a script or a scenario, or listed with a
reason in `scripts\tool-coverage-allowlist.txt`; stale entries fail. `check_docs.py` compares the tool catalog
below and `docs\tools.md` with the registered tools. `PixMcp.exe --tool-reference docs\tools.md` regenerates
that reference from the tool attributes; `--check` exits 2 when it is stale. The self-hosted CI job runs
on demand and on every push to main. It runs all of these plus `dotnet test`, the smoke suite and the
budgeted benchmark, and uploads only JSON, TRX and PNG artifacts.

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
Readiness waits run outside the worker. Recorded-timing SQL runs in cancellable managed
jobs off the PIX worker, each with a private, read-only SQLite connection; a document gate
interrupts and drains those readers before save, symbol resolution or close. Caller-supplied
SQL (`Pix/Sql`) is limited to one read-only statement under a SQLite authorizer and limits.
Tools compose these primitives into small, navigable investigations.

`Program.cs` must not directly reference Microsoft.PIX types before discovery
loads the installed assembly. Expensive native preparation belongs in a job,
using `Tools.RunWhenReady` for queries that depend on it.

## License

[MIT](LICENSE). PIX discovery includes adaptations from Microsoft's samples;
see [third-party notices](THIRD_PARTY_NOTICES.md).
