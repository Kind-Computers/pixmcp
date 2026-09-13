"""Generate baseline/candidate GPU captures and a symbol-resolved timing capture serially.

Build tests/D3D12TestApp/build.cmd first. It restores pinned WinPixEventRuntime and places
the runtime DLL and PDB beside the fixture executable. No hang workloads are launched.
Use --adapter-name B580 to require that hardware in GPU and timing launches. The
fixture logs its selected adapter name, vendor/device IDs and LUID to stdout; the
report records the requested filter and launch command lines, not a confirmed GPU.
"""
import argparse
import json
from pathlib import Path
import shutil
import subprocess
import sys
import time

from benchmark import Investigator
from smoke import Client, SmokeError


def generate(client, app, output_dir, timing_seconds=3, startup_delay_ms=1500, sleep=time.sleep, only="all", adapter_name=None):
    if adapter_name is not None and (not isinstance(adapter_name, str) or not adapter_name.strip() or "\0" in adapter_name):
        raise ValueError("adapter-name must be a nonempty adapter-name substring without NUL characters")
    app = Path(app).resolve()
    output_dir = Path(output_dir).resolve()
    output_dir.mkdir(parents=True, exist_ok=True)
    report = {"fixtures": [], "errors": [], "app": str(app), "outputDirectory": str(output_dir),
              "requestedAdapterName": adapter_name, "launches": []}
    agent = Investigator(client)
    def query(tool, **arguments):
        report["currentOperation"] = tool
        report["calls"] = agent.calls
        (output_dir / "fixture-report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
        print(f"Fixture: {tool}", file=sys.stderr, flush=True)
        try:
            return agent.query(tool, **arguments)
        except Exception as error:
            raise SmokeError(f"{tool}: {error}") from error

    connection = query("pix_device_connect")["handle"]

    def launch(arguments, under_gpu_capture=True):
        tokens = [*arguments, *(["--adapter-name", adapter_name] if adapter_name is not None else [])]
        command_line = subprocess.list2cmdline(tokens)
        report["launches"].append({"arguments": command_line, "underGpuCapture": under_gpu_capture})
        result = query("pix_device_launch", handle=connection, exePath=str(app), arguments=command_line,
                       underGpuCapture=under_gpu_capture)
        if not result.get("capturable") or not result.get("processId"):
            raise SmokeError(f"Fixture process cannot be captured: {result}")
        report["activeProcessId"] = result["processId"]
        return result["processId"]

    def detach():
        query("pix_device_detach", handle=connection, terminate=True)
        report.pop("activeProcessId", None)

    def cleanup(actions, primary_error):
        errors = []
        for action in actions:
            try:
                action()
            except Exception as error:
                errors.append(str(error))
        if errors:
            report["errors"].append({"cleanupErrors": errors, "processId": report.get("activeProcessId")})
            if primary_error is None:
                raise SmokeError("Fixture cleanup failed: " + "; ".join(errors))

    def capture_gpu(variant):
        primary_error = None
        try:
            pid = launch(["--hidden", "--frames", "1800", "--variant", variant,
                          "--startup-delay-ms", str(startup_delay_ms)])
            result = query("pix_device_take_gpu_capture", handle=connection, processId=pid,
                                 delaySeconds=0.25, readinessTimeoutSeconds=30, open=False, waitSeconds=0)
            source = Path(result["path"])
            if not source.is_file() or source.stat().st_size == 0:
                raise SmokeError(f"PIX did not produce a nonempty {variant} GPU capture")
            destination = output_dir / f"{variant}.wpix"
            if source.resolve() != destination:
                temporary = destination.with_suffix(".wpix.tmp")
                try:
                    shutil.copyfile(source, temporary)
                    temporary.replace(destination)
                finally:
                    temporary.unlink(missing_ok=True)
            return {"path": str(destination), "bytes": destination.stat().st_size, "processId": pid}
        except Exception as error:
            primary_error = error
            raise
        finally:
            cleanup([detach], primary_error)

    def capture_timing():
        active = False
        timing_handle = None
        primary_error = None
        try:
            # Launch/attach before capture start so the selected process has CPU sample stacks.
            pid = launch(["--hidden", "--frames", "100000", "--timing-workload"], under_gpu_capture=False)
            path = output_dir / "timing.wpix"
            query("pix_device_timing_capture_start", handle=connection, outputPath=str(path),
                        cpuSamples=True, cpuSamplesPerSecond=1000, cpuSampleStacks=True,
                        contextSwitches=True, contextSwitchStacks=True, captureSysmonCounters=True,
                        pixEvents=True, gpuTiming=True,
                        maxFileSizeMb=256, durationSeconds=0)
            active = True
            sleep(timing_seconds)
            stopped = query("pix_device_timing_capture_stop", handle=connection, open=True, waitSeconds=0)
            active = False
            timing_handle = (stopped.get("timingCapture") or {}).get("handle")
            if not timing_handle:
                raise SmokeError(f"Timing capture could not be opened: {stopped}")
            query("pix_timing_resolve_symbols", handle=timing_handle, pdbSearchPath=str(app.parent),
                        includeKernelSymbols=False, useNtSymbolPath=False, waitSeconds=0)
            query("pix_timing_save", handle=timing_handle)
            if not path.is_file() or path.stat().st_size == 0:
                raise SmokeError("PIX did not produce a nonempty timing capture")
            return {"path": str(path), "bytes": path.stat().st_size, "processId": pid,
                    "symbols": str(app.with_suffix(".pdb")), "durationSeconds": timing_seconds}
        except Exception as error:
            primary_error = error
            raise
        finally:
            actions = []
            if active:
                actions.append(lambda: query("pix_device_timing_capture_stop", handle=connection, open=False, waitSeconds=0))
            if timing_handle:
                actions.append(lambda: query("pix_close", handle=timing_handle))
            cleanup([*actions, detach], primary_error)

    for name, work in (("baseline", lambda: capture_gpu("baseline")),
                       ("candidate", lambda: capture_gpu("candidate")), ("timing", capture_timing)):
        if only == "gpu" and name == "timing" or only == "timing" and name != "timing":
            continue
        started = time.monotonic()
        try:
            report["fixtures"].append({"name": name, "status": "passed", **work(), "seconds": time.monotonic() - started})
        except Exception as error:
            detail = {"name": name, "status": "failed", "error": f"{type(error).__name__}: {error}",
                      "seconds": time.monotonic() - started}
            report["fixtures"].append(detail)
            report["errors"].append(detail)
        # Preserve every completed fixture's status if a later native operation fails.
        report["calls"] = agent.calls
        (output_dir / "fixture-report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("server")
    parser.add_argument("--app", default="tests/D3D12TestApp/bin/D3D12TestApp.exe")
    parser.add_argument("--output-dir", default="tests/artifacts")
    parser.add_argument("--timing-seconds", type=float, default=3)
    parser.add_argument("--startup-delay-ms", type=int, default=1500)
    parser.add_argument("--only", choices=("all", "gpu", "timing"), default="all")
    parser.add_argument("--adapter-name", help="Require a case-insensitive hardware-adapter name substring (for example B580); no fallback.")
    parser.add_argument("--timeout", type=float, default=60, help="Maximum seconds per MCP response.")
    args = parser.parse_args()
    if not 0.1 <= args.timing_seconds <= 60 or not 0 <= args.startup_delay_ms <= 25000 or not 1 <= args.timeout <= 3600:
        parser.error("timing-seconds must be 0.1..60, startup-delay-ms 0..25000, and timeout 1..3600")
    if args.adapter_name is not None and (not args.adapter_name.strip() or "\0" in args.adapter_name):
        parser.error("adapter-name must be a nonempty adapter-name substring without NUL characters")
    client = None
    report = {"fixtures": [], "errors": []}
    output_dir = Path(args.output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)
    try:
        app = Path(args.app)
        for path in (app, app.with_suffix(".pdb"), app.parent / "WinPixEventRuntime.dll"):
            if not path.is_file():
                raise SmokeError(f"Missing fixture dependency {path}; run tests/D3D12TestApp/build.cmd")
        client = Client([args.server], timeout=args.timeout)
        client.send("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                   "clientInfo": {"name": "pixmcp-fixtures", "version": "1.0"}})
        client.send("notifications/initialized", notify=True)
        report = generate(client, args.app, output_dir, args.timing_seconds, args.startup_delay_ms,
                          only=args.only, adapter_name=args.adapter_name)
    except Exception as error:
        report["errors"].append({"error": f"{type(error).__name__}: {error}"})
        print(f"Fixture generation failed: {error}", file=sys.stderr)
    finally:
        try:
            if client is not None:
                try:
                    client.timeout = min(client.timeout, 10)
                    client.call("pix_close_all")
                except Exception as error:
                    detail = f"{type(error).__name__}: {error}"
                    report["errors"].append({"cleanupError": detail})
                    print(f"Fixture cleanup failed: {detail}", file=sys.stderr)
                finally:
                    try:
                        report["serverExitCode"] = client.close()
                        if report["serverExitCode"] != 0:
                            detail = f"Server exited with code {report['serverExitCode']}."
                            report["errors"].append({"shutdownError": detail})
                            print(f"Fixture shutdown failed: {detail}", file=sys.stderr)
                    except Exception as error:
                        detail = f"{type(error).__name__}: {error}"
                        report["errors"].append({"shutdownError": detail})
                        print(f"Fixture shutdown failed: {detail}", file=sys.stderr)
        finally:
            destination = output_dir / "fixture-report.json"
            destination.write_text(json.dumps(report, indent=2), encoding="utf-8")
            print(json.dumps({"fixtures": report["fixtures"], "errors": report["errors"], "report": str(destination)}, indent=2))
    return int(bool(report["errors"]))


if __name__ == "__main__":
    sys.exit(main())
