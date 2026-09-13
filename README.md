# pixmcp

An [MCP](https://modelcontextprotocol.io/) server for Microsoft's **PIX on Windows**.
Open GPU captures and DirectX dumps, locate expensive work, inspect pipeline state and
resources, search shaders, compare captures, and view replay output. Version 1.0 provides
typed navigation references, compact summaries, and retrievable result snapshots for LLMs.

The server wraps the experimental PIX API. Capture files remain the source of truth;
open documents and replay sessions are process-local. Result snapshots use bounded
memory and temporary-disk storage, and can be exported as JSON.

## Requirements and build

- Windows 11 x64 and a D3D12-capable GPU.
- PIX Preview newer than 2606.15. Development uses **2606.18-preview**; retail PIX does not
  ship this API. Install from [Microsoft's PIX download page](https://devblogs.microsoft.com/pix/download/).
- Windows Developer Mode for GPU replay.
- .NET 10 SDK to build and runtime to run.

The newest installation under `%ProgramFiles%\Microsoft PIX Preview` is selected.
Set `PIX_DIR` to a versioned installation directory to override it. An invalid explicit
override is an error. The managed PIX DLL loads in place from that installation.

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

Normal JSON responses target **32 KiB**. Larger managed results become immutable
snapshots with a `resultRef`. Read them through `pix_result_read`:

```json
{"resultRef": "result-1", "pointer": "/items", "offset": 0, "limit": 25}
```

The reader returns `kind`, `value`, window counts, and continuation calls. JSON Pointer
selects a nested field; escape `~` as `~0` and `/` as `~1`. Arrays, objects, and strings
can all be paged. Oversized children are represented by deferred pointers with exact
reader calls, so nested details remain reachable.

The final JSON response guard measures UTF-8 bytes and defaults to **2 MiB**. Configure
it with `PIXMCP_MAX_RESULT_BYTES`. Snapshots use up to **256 MiB RAM** and **2 GiB temporary
disk**, configurable with `PIXMCP_RESULT_MEMORY_BYTES` and `PIXMCP_RESULT_DISK_BYTES`.
The store retains up to 50 transient snapshots and the latest 50 finished jobs.
Storage pressure evicts transient results first, then eligible finished jobs with
their results. `pix_info.results` reports storage usage, leases and evictions.
Closing an owning capture invalidates its snapshots. Reads and exports already in
progress can finish; subsequent access returns `result_expired`.
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
`resultRef`; read it with `pix_result_read`. Status never embeds a large result.
`pix_job_cancel` removes queued work immediately. Cancellation of running native work
is best effort; an operation that finishes before cancellation takes effect remains succeeded.

Every native PIX call runs on one worker thread. The worker owns CLI preview jobs too,
for their entire lifetime. Session metadata and job tools remain responsive during
native replay. `pix_info.worker` reports ordinary operations as well as jobs, including
their start time, elapsed time, and queue depth.

Errors use `code`, `message`, optional `hresult`, `retryable`, and `nextCalls`.
Optional data includes an explicit unavailable reason. An unexpected replay failure is
reported as a failure, rather than being remembered as unsupported hardware.

### Timing and counters

Timing results include replay provenance, adapter/configuration, nanosecond units, and
whether a marker value was derived from descendants. These are PIX replay measurements.
Summed end-of-pipe intervals are **not frame latency** and do not establish overlap
between asynchronous queues.

`pix_gpu_timing_tree` has a global `maxNodes` budget. Omitted siblings and descendants
include continuation calls. `pix_gpu_timing_events` provides the flat sortable view.

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
This reverse-use index is shared by all resource queries. Event enumeration, GPU timing
events, and resource uses accept an `EventRef` scope; scope includes the selected event
and its descendants. Event inspection preserves timing and pipeline sections when PIX
explicitly reports binding preparation as unsupported.

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
artifact retrieval tools. `pix_gpu_preview_image` accepts a `crop` in original pixel
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
| Timing/counters | `pix_gpu_timing_collect`, `pix_gpu_timing_events`, `pix_gpu_timing_tree`, `pix_gpu_counters_list`, `pix_gpu_counters_start`, `pix_gpu_counters_collect`, `pix_gpu_occupancy`, `pix_gpu_hf_counters` |
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
