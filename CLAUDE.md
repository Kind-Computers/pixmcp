# pixmcp

C# / .NET 10 MCP server over the PIX on Windows API (PIX Preview newer than 2606.15, verified on
2606.18-preview; both strings live only in `Directory.Build.props`, `scripts/check_versions.py`
enforces it). See README.md.

- Build: `dotnet build pixmcp.sln -c Release`; tests: `dotnet test pixmcp.sln -c Release`.
- The PIX managed assembly `PixApiCsExt.experimental.dll` is referenced with `Private=false` and
  loaded in place from the PIX install by `src/PixMcp/Pix/PixDiscovery.cs` (module initializer).
  `Program.cs` must never reference `Microsoft.PIX` types directly.
- All PIX calls go through `PixWorker` (single thread). Tools are static methods in
  `src/PixMcp/Tools/*.cs`, return JSON strings via `Tools.Run(...)`, and use `PixToolException`
  for stable error codes and executable recovery calls (`PixErrors`). The shared result filter
  emits structured errors, matching text/structuredContent, and snapshots oversized JSON.
  Every tool takes a trailing
  `CancellationToken cancellationToken = default` and passes it to `Tools.Run`.
- A tool must never start GPU analysis or collect timing/counters inline. Use
  `Tools.RunWhenReady(session, jobs, tool, handle, preparation, query, waitSeconds, ct)` with a
  `Preparation<T>` (`GpuCaptureHandle.AnalysisPreparation`, `CountersTools.TimingPreparation`,
  `CountersTools.CounterSetPreparation`); it runs the preparation as a job and returns a
  `PendingDto` when the wait elapses. Job-starting tools register their job with
  `Tools.StartPreparation` (find-or-start under the handle's preparation gate) so queries join it. Add new tool names to
  `StructuredToolResults.SchemaFor`.
- Navigation uses `EventRef`, `ResourceRef`, and `ShaderRef`; reuse returned references in
  follow-up calls. Keep IDs that can exceed JavaScript precision as strings. Pages default to
  25 rows. Preserve full managed data before response caps: `ResultStore` makes nested
  objects, arrays, and strings retrievable through `pix_result_read`. Job status carries
  `resultRef`, never an embedded result. Every truncation needs an exact continuation.
- Resolve client-supplied paths with `ServerPaths.Full`, never `Path.GetFullPath(path)`: static shader profiling points the
  process working directory at a private folder (`ServerPaths.CompilerWorkingDirectory`) while PIX loads and runs vendor
  compiler plugins, which write report files there.
- Environment variables are read only through `ServerOptions` (`src/PixMcp/Pix/ServerOptions.cs`),
  parsed once before protocol output; consumers read `ServerOptions.Current` (tests scope
  `ServerOptions.Override`). `PIX_DIR` and the PIX discovery variables stay in `PixDiscovery`.
- Result retention uses bounded serialized memory and temporary disk. Preserve owner
  identity, acquire leases for streaming reads/exports, and never retain a second raw
  job result. `pix_result_export` writes JSON atomically to an existing directory.
- Use `PixWorker.RunWithAdmission` through `Tools.RunWhenReady` for bounded queue waits;
  do not time out native work after execution starts. Managed capture readiness/warmup
  belongs in `JobManager.StartAfter`, outside the worker.
- Timing analysis reads the document's PixStorage path using private, read-only SQLite
  connections in cancellable managed jobs off the PIX worker (`TimingCaptureHandle.QueryJob`
  behind `DocumentGate`); save, symbol resolution and close go through `WithDocumentWriter`,
  which interrupts and drains readers. All caller-supplied SQL runs through `SqlQuery` and
  `SqlStatementGuard` (`Pix/Sql`), never through `SqliteCommand`. Close queries before save/symbol resolution; invalidate
  cached profiles after document changes. Recorded sample counts are not exact CPU time.
- Exact PIX API signatures: reflect over the DLL (see the `reflect` scratch project pattern in git
  history/README) or read the official samples at https://github.com/microsoft/pix-samples.
- Prompts are playbooks in `src/PixMcp/Pix/Playbooks.cs` (served by `Tools/PixPrompts.cs`); a step
  may only name registered tools with parameters they accept (PromptTests). Every new tool needs a
  toolset in `Pix/Toolsets.cs` (ToolsetTests). Keep `ServerHost.Instructions` at 800 characters or
  fewer; detail belongs in descriptions, nextCalls and playbooks.
- Tools wait on jobs through `Job.WaitAsync`, which forwards progress to clients that sent a
  progress token; never await `Job.Completion` directly in a tool.
- Smoke test end to end: `python scripts/smoke.py <PixMcp.exe> @scripts/scenarios/capture-and-inspect.json`
  (requires `tests/D3D12TestApp/build.cmd` to have been run).
