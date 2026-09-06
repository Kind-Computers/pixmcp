# pixmcp

An [MCP](https://modelcontextprotocol.io/) server that exposes Microsoft's **PIX on Windows** API
so an AI agent (Claude Code, Claude Desktop, or any MCP client) can open GPU captures, timing
captures and DirectX dump files and ask questions about them: which draws are slowest, what
pipeline state and resources a draw uses, what a shader's source looks like, what Dr. PIX thinks,
or why a GPU hang happened.

It wraps the same API the [microsoft/pix-samples](https://github.com/microsoft/pix-samples)
repository demonstrates (`PixApiCsExt.experimental.dll`), and it needs no database: the capture
file plus PIX's own engine is the queryable store. The server just keeps open documents and the
(expensive) GPU analysis session alive in memory behind handles.

## Prerequisites

- Windows 11 x64 with a D3D12-capable GPU.
- A **PIX Preview build newer than 2606.15** from https://devblogs.microsoft.com/pix/download/
  (retail PIX builds do not ship the API; 2606.18-preview is the build the server is verified
  against). The server looks in `%ProgramFiles%\Microsoft PIX Preview\<version>` and picks the
  newest; set `PIX_DIR` to override. An invalid explicit override is an error; the server does not
  silently select another installation.
- **Windows Developer Mode** enabled (required by PIX for GPU analysis / replay).
- .NET 10 SDK (to build) and runtime (to run).

## Build

```
dotnet build pixmcp.sln -c Release
dotnet test pixmcp.sln -c Release
```

The output is `src\PixMcp\bin\x64\Release\net10.0-windows10.0.26100.0\PixMcp.exe`. The PIX managed
assembly is loaded in place from the PIX install (it is never copied next to the exe), so the
build also fails with a clear message when no compatible PIX Preview is installed.
At runtime, missing or invalid PIX installations produce an actionable stderr message and
exit code 1 before the MCP transport starts. `global.json` pins the .NET 10 SDK band.

`scripts\publish.cmd` publishes a single framework-dependent `dist\PixMcp.exe` (about 30 MB; the
.NET 10 runtime must be installed, PIX is still loaded from its install directory) that can be
copied anywhere and registered with any MCP client.

## Use with Claude Code

The repo ships a `.mcp.json` that points at the built exe, so after building you can just run
`claude` in this directory and approve the `pix` server. To add it to another project:

```
claude mcp add pix -- C:\path\to\pixmcp\src\PixMcp\bin\x64\Release\net10.0-windows10.0.26100.0\PixMcp.exe
```

For other MCP clients, run the exe as a stdio server (no arguments). Logs go to stderr;
`Logging__LogLevel__Default=Debug` in the environment also shows PIX engine informational messages.

### Claude Desktop

Add to `%APPDATA%\Claude\claude_desktop_config.json` (use `dist\PixMcp.exe` from
`scripts\publish.cmd` or the built exe):

```json
{
  "mcpServers": {
    "pix": {
      "command": "C:\\path\\to\\pixmcp\\dist\\PixMcp.exe",
      "env": { "PIX_DIR": "C:\\Program Files\\Microsoft PIX Preview\\2606.18-preview" }
    }
  }
}
```

The `env` block is only needed when PIX Preview is not installed under `%ProgramFiles%`.

### VS Code

`.vscode/mcp.json` in a workspace (or `code --add-mcp` with the same object):

```json
{
  "servers": {
    "pix": { "type": "stdio", "command": "C:\\path\\to\\pixmcp\\dist\\PixMcp.exe" }
  }
}
```

### Typical conversation flow

1. `pix_info` – confirms which PIX install loaded and that Developer Mode is on.
2. `pix_gpu_open` – returns a handle (`gpu-1`), file info, application and queue list.
3. `pix_gpu_events` – paged, filterable event list (`kind: "draw"`, `nameContains`, `parentIndex`, ...).
4. `pix_gpu_timing_tree` – GPU time rolled up the marker hierarchy ("which pass is slowest"), then
   `pix_gpu_timing_events` for the slowest individual draws (both start analysis automatically).
5. `pix_gpu_pipeline_state`, `pix_gpu_shader_code`, `pix_gpu_event_resources` – inspect one draw
   (`pix_gpu_event` with `gpuId` maps timing rows or Dr. PIX ranges back to an event).
6. `pix_gpu_drpix_run` – run Dr. PIX experiments over the frame.
7. `pix_close` when done.

Long operations (analysis start, timing and counter collection, Dr. PIX, symbol resolution, taking
and stopping captures, capture upgrades) return a `jobId`; poll `pix_job_status`, block with
`pix_job_wait`, or pass `waitSeconds` to the starting tool. `pix_job_cancel` requests cancellation;
`cancellationRequested: true` does not mean the operation was interrupted. Work that completes
before cancellation takes effect remains `succeeded` with its result. Cancelling during the capture
initialization delay prevents capture from starting. The most recent 50 finished jobs are kept.

Query tools that need GPU analysis (`pix_gpu_pipeline_state`, `pix_gpu_shader_code`,
`pix_gpu_event_resources`, `pix_gpu_timing_events`, `pix_gpu_counters_*`, `pix_gpu_occupancy`,
`pix_gpu_hf_counters`, `pix_gpu_drpix_experiments`) never block the PIX thread on a replay. If
analysis (or the timing/counter data they need) is not ready they start it as a job, wait up to
`waitSeconds` (default 60), and either answer or return
`{ "pending": true, "jobId": "job-3", "retry": "pix_gpu_pipeline_state" }`: wait for the job with
`pix_job_wait` and repeat the call. Concurrent callers share one job, and an explicit
`pix_gpu_analysis_start` / `pix_gpu_timing_collect` / `pix_gpu_counters_start` job is joined the
same way.

All PIX calls run on one thread, so while a job replays the capture every other PIX tool call waits
behind it; `pix_info`, `pix_handles` and the job tools answer regardless, and `pix_info.worker`
shows the running job and queue depth. Requests a client abandons (timeout, cancellation) are
dropped before they reach the PIX thread.

`pix_gpu_analysis_start` always returns a job, including when compatible analysis is already
running (`result.alreadyStarted: true`). To change an active analysis's adapter, power state, or
flags, call `pix_gpu_analysis_stop` first.

## Tools

| Area | Tools |
|------|-------|
| Session | `pix_info`, `pix_handles`, `pix_close`, `pix_close_all`, `pix_jobs`, `pix_job_status`, `pix_job_wait`, `pix_job_cancel`, `pix_log` |
| GPU capture | `pix_gpu_open`, `pix_gpu_info`, `pix_gpu_queues`, `pix_gpu_events`, `pix_gpu_event`, `pix_gpu_api_objects`, `pix_gpu_screenshot` |
| Analysis (replay) | `pix_gpu_analysis_start`, `pix_gpu_analysis_status`, `pix_gpu_analysis_adapters`, `pix_gpu_analysis_stop` |
| Timing and counters | `pix_gpu_timing_collect`, `pix_gpu_timing_events`, `pix_gpu_timing_tree`, `pix_gpu_counters_list`, `pix_gpu_counters_start`, `pix_gpu_counters_collect`, `pix_gpu_occupancy`, `pix_gpu_hf_counters` |
| Pipeline and shaders | `pix_gpu_pipeline_state`, `pix_gpu_shader_code` |
| Resources | `pix_gpu_resources`, `pix_gpu_resource`, `pix_gpu_event_resources` |
| Dr. PIX | `pix_gpu_drpix_experiments`, `pix_gpu_drpix_run` |
| Timing captures | `pix_timing_open`, `pix_timing_resolve_symbols`, `pix_timing_save` |
| Device (live) | `pix_device_connect`, `pix_device_info`, `pix_device_processes`, `pix_device_counters`, `pix_device_launch`, `pix_device_attach`, `pix_device_take_gpu_capture`, `pix_device_timing_capture_start`, `pix_device_timing_capture_stop`, `pix_device_detach` |
| Capture files | `pix_capture_format`, `pix_capture_upgrade` |
| DirectX dump files | `pix_dump_open`, `pix_dump_info`, `pix_dump_queues`, `pix_dump_events`, `pix_dump_page_faults`, `pix_dump_breadcrumbs`, `pix_dump_resources`, `pix_dump_gpu_state`, `pix_dump_blobs`, `pix_dump_journal`, `pix_dump_shader_waves` |

Paged enumeration tools take `offset`/`limit` (default 100, max 1000) and return `total`,
`count`, `items`, `nextOffset` and an optional `extra` object (for example the counter groups of
`pix_gpu_counters_list` or the DRED data of `pix_dump_page_faults`). When PIX has no data for a
paged query the page is empty and `extra.unavailable` says why. Enum values are returned as
trimmed names (`GRAPHICS`, `R8G8B8A8_UNORM`). Optional fields and hardware features (occupancy,
high-frequency counters, per-view bindings, correlated shaders) return
`{ "unavailable": true, "feature": ..., "reason": ... }` instead of failing or silently
disappearing. Every cap (`maxChars`, `maxRows`, `maxEvents`, children/parents/lanes) is stated in
the parameter description and reported with a `...Truncated` flag when it cuts data.

A tool result larger than 2 MB of JSON is refused with a message naming the paging parameter to
reduce (`PIXMCP_MAX_RESULT_BYTES` changes the limit); `pix_job_status` is exempt so a large job
result stays reachable.

MCP resources `pix://handles`, `pix://handles/{handle}`, `pix://jobs` and `pix://jobs/{jobId}`
mirror the handle and job tables for clients that prefer resources over tool calls.

For large counter queries, call `pix_gpu_counters_start(handle, counterIds, waitSeconds=0)`
and wait for its job before paging `pix_gpu_counters_collect`. The existing collect tool still
collects synchronously if needed. Decoded rows are cached per counter set and queue until
analysis stops; reading another page does not reread every native counter value.

`pix_dump_breadcrumbs` defaults to an operation window around the completed-operation boundary.
Use `nodeIndex` to select one command list and `offset` to navigate its operation history;
`offset: 0` retrieves the original prefix view. Operation indices remain absolute.
`pix_gpu_resource` and `pix_gpu_event_resources` accept `viewIndex`, `bindingOffset`, and
`bindingLimit` (default 32) to retrieve bindings beyond the initial page. Each view reports
its binding total and continuation offset. A view index is relative to the resource's views
or the event's views, respectively.

`pix_gpu_timing_tree` rolls the measured end-of-pipe time of every draw/dispatch up its parent
markers: each node reports `selfEopNs`, `inclusiveEopNs`, `percentOfQueue`, `timedDescendants`
and `childCount`, children come most expensive first, and `depth` (max 4) or a child's index as
`parentIndex` drills down. `pix_gpu_timing_events` remains the flat, sortable per-event view.

Successful JSON tool results include `structuredContent` and advertised output schemas while
retaining their existing JSON text. Object results have the same fields in both representations;
legacy top-level arrays use `{ "items": [...] }` in structured content. Screenshot image blocks
are preserved. Page, job, and event schemas describe stable fields; experimental PIX details
remain extensible.

## Troubleshooting

| Symptom | Cause and fix |
|---------|---------------|
| The server exits with code 1 and `pixmcp: No PIX Preview install found` | Install a PIX Preview build newer than 2606.15, or set `PIX_DIR` to its versioned folder (`C:\Program Files\Microsoft PIX Preview\2606.18-preview`). Retail PIX has no API. |
| `PIX_DIR is set to ... but it does not contain PixApiCsExt.experimental.dll` | `PIX_DIR` must point at the versioned folder, not at `Microsoft PIX Preview`. An explicit bad override is never silently replaced. |
| A tool fails with `0x8ABC0000` or `0x8ABC0001` | Windows Developer Mode is off. The error text contains the Settings location and a `reg add` command. |
| A query tool returns `{ "pending": true, "jobId": ... }` | GPU analysis (or timing/counter collection) is still running as that job. `pix_job_wait` on it, then repeat the call unchanged. |
| Calls seem to hang or time out | `pix_info` (always answers) shows `worker.runningJob` and `worker.queuedCalls`. Wait for or `pix_job_cancel` the running job; abandoned requests are dropped before they run. |
| `E_NOT_VALID_STATE (0x8007139F)` from pipeline/shader/resource tools | The event is not a draw or dispatch. Find one with `pix_gpu_events(kind: "drawOrDispatch")`. |
| `unavailable: true` inside a result | The hardware or PIX build does not provide that feature (occupancy and high-frequency counters on many GPUs); `reason` carries PIX's message. The rest of the result is valid. |
| `the result is N characters, above the ... limit` | Page or filter (`offset`/`limit`, `nameContains`, `max*`), or raise `PIXMCP_MAX_RESULT_BYTES`. |
| Reopening a capture fails or analysis will not start | Close the previous handle for the same file first (`pix_close`); one analysis session per capture. |
| A documented tool is missing from the client's tool list | The client runs a stale build. Rebuild; note that `PixMcp.exe` cannot be overwritten while a client has it running. |
| `pix_gpu_screenshot` reports an unsupported format | Only 8-bit RGBA/BGRA and R10G10B10A2 swapchains are encoded; HDR formats are not yet supported. |
| Timing capture tools show no CPU samples or events | The PIX API only opens, resolves symbols for and saves timing captures; it has no reader for their contents. Open the saved `.wpix` in PIX for the timeline. |

## Architecture

```
src/PixMcp/
  Program.cs            PIX discovery check, then ServerHost (must not touch Microsoft.PIX types)
  ServerHost.cs         MCP host: stdio transport, tool/resource discovery, structured results filter
  Pix/PixDiscovery.cs   Finds the PIX Preview install and wires assembly + native DLL resolution
  Pix/PixWorker.cs      The single PIX thread; every native call is queued here
  Pix/PixSession.cs     PIX factory plus the handle table (gpu-1, timing-1, dump-1, device-1)
  Pix/Jobs.cs           Background jobs: progress, cancellation tokens, retention
  Pix/Handles/          Per-document state: GpuCaptureHandle caches events, analysis, timing, counters
  Pix/TimingTree.cs     GPU time rolled up the marker hierarchy
  Pix/StructuredToolResults.cs  structuredContent and outputSchema per tool
  Tools/Tools.cs        Run (worker + error mapping), RunJob, RunWhenReady (auto-job + pending), Try, paging
  Tools/*Tools.cs       One static class per area; each [McpServerTool] is a thin wrapper over PIX calls
tests/PixMcp.Tests/     xUnit: pure unit tests, worker/job lifecycle tests, stdio protocol test, opt-in integration tests
tests/D3D12TestApp/     Tiny D3D12 app used as a capture target by the smoke scenarios
scripts/smoke.py        Dependency-free stdio MCP client; scripts/scenarios/*.json are scripted tool sequences
```

## Design notes

- **One PIX thread.** Every PIX call runs on a single dedicated worker thread (`PixWorker`);
  the API is nano-COM without apartment marshalling and the analysis session is not
  thread-safe. Tool calls are therefore serialized, and a long job (Dr. PIX run) blocks other
  PIX calls until it finishes, exactly as the PIX UI would. Tools never start a replay inside a
  request: `Tools.RunWhenReady` turns a missing prerequisite into a job and a `pending` answer.
- **Handles.** `PixSession` owns the factory and a handle table. A GPU capture handle connects to
  the local GPU and starts analysis on first need; `pix_close` stops analysis and disconnects
  first, which is required for the next open of the same capture to work.
- **Errors.** COM failures are reported with their HRESULT; the Developer Mode HRESULTs
  (`0x8ABC0000`/`0x8ABC0001`) include the fix.
- **Experimental surface.** `pix_dump_*` and shader source retrieval use the experimental PIX API
  (`Microsoft.PIX.Internal`), which may change between Preview builds.
- **Timing captures are write-only through the API.** `IPixTimingCaptureDocument` exposes only
  open, symbol resolution and save; there is no reader for CPU samples, threads or PIX events, so
  `pix_timing_*` prepares files for the PIX UI rather than querying them.

## Testing

- `dotnet test` runs unit tests. Set `PIX_TEST_CAPTURE=<path to .wpix>` to also run the
  integration tests (and `PIX_TEST_ANALYSIS=1` to include a GPU replay). Building anything needs a
  PIX Preview install, so `.github/workflows/ci.yml` runs only the Python harness tests on hosted
  runners and the full build/test/smoke job on a self-hosted runner labelled `pix`, on demand.
- `tests\D3D12TestApp\build.cmd` builds a tiny D3D12 triangle app (needs Visual Studio 2022 C++
  tools) that is a convenient capture target.
- `scripts\smoke.py` is a dependency-free stdio MCP client; `scripts\scenarios\*.json` are
  scripted tool sequences, e.g.

  ```
  python scripts\smoke.py src\PixMcp\bin\x64\Release\net10.0-windows10.0.26100.0\PixMcp.exe @scripts\scenarios\capture-and-inspect.json
  ```

  The `capture-and-inspect` scenario launches the test app under PIX, takes a capture, opens it,
  replays it for timing, and inspects the first draw. `take-capture` only produces a capture file
  (its path is in the `pix_device_take_gpu_capture` result); `open-capture` and `analysis-pending`
  take that path in `PIX_TEST_CAPTURE` and exercise, respectively, the basic inspection flow and the
  `pending`/`pix_job_wait`/retry flow of the analysis-dependent tools.
  Requests time out after 660 seconds by default; use `--timeout <seconds>` to change this.
  Protocol errors, tool errors, failed/cancelled jobs, unresolved result references, and failed
  assertions produce a nonzero exit code. A scenario step may include expected result fields:
  `["pix_job_wait", {"jobId": "$last.jobId"}, {"status": "succeeded"}]`.

## Status

Verified on PIX 2606.18-preview with an NVIDIA RTX 4070 Ti: capture taking, event/resource
queries, screenshot export, analysis, per-event timing, hardware counters, pipeline state and
root signature decoding, HLSL source retrieval, bound resources/views, Dr. PIX experiments,
system-wide timing captures, and the device tools. Occupancy and high-frequency counters report
`unavailable` on this hardware. The DirectX dump (`pix_dump_*`) tools compile against the
experimental API and follow the official DXDumpFileParser sample but have not been run against a
real `.dxdmp_preview` file yet (one is only produced by a GPU hang/TDR).

## License

Licensed under the [MIT License](LICENSE).

PIX installation discovery includes adaptations from Microsoft's PIX samples;
see [third-party notices](THIRD_PARTY_NOTICES.md).
