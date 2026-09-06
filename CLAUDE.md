# pixmcp

C# / .NET 10 MCP server over the PIX on Windows API (PIX Preview >= 2606.18). See README.md.

- Build: `dotnet build pixmcp.sln -c Release`; tests: `dotnet test pixmcp.sln -c Release`.
- The PIX managed assembly `PixApiCsExt.experimental.dll` is referenced with `Private=false` and
  loaded in place from the PIX install by `src/PixMcp/Pix/PixDiscovery.cs` (module initializer).
  `Program.cs` must never reference `Microsoft.PIX` types directly.
- All PIX calls go through `PixWorker` (single thread). Tools are static methods in
  `src/PixMcp/Tools/*.cs`, return JSON strings via `Tools.Run(...)`, and throw `McpException`
  with an HRESULT-bearing message on failure (`PixErrors`). Every tool takes a trailing
  `CancellationToken cancellationToken = default` and passes it to `Tools.Run`.
- A tool must never start GPU analysis or collect timing/counters inline. Use
  `Tools.RunWhenReady(session, jobs, tool, handle, preparation, query, waitSeconds, ct)` with a
  `Preparation<T>` (`GpuCaptureHandle.AnalysisPreparation`, `CountersTools.TimingPreparation`,
  `CountersTools.CounterSetPreparation`); it runs the preparation as a job and returns a
  `PendingDto` when the wait elapses. Job-starting tools register their job with
  `Tools.RegisterPreparation` so queries join it. Add new tool names to
  `StructuredToolResults.SchemaFor`.
- Exact PIX API signatures: reflect over the DLL (see the `reflect` scratch project pattern in git
  history/README) or read the official samples at https://github.com/microsoft/pix-samples.
- Smoke test end to end: `python scripts/smoke.py <PixMcp.exe> @scripts/scenarios/capture-and-inspect.json`
  (requires `tests/D3D12TestApp/build.cmd` to have been run).
