"""Find what makes PIX populate GpuFrame in API-taken timing captures.

Runs a matrix over the timing capture option parts (PIXMCP_TIMING_EXPERIMENT_PARTS, one part per cell,
plus none), the fixture window (hidden or visible) and GPU timing (on or off). Each cell starts its own
server so the option part applies, records a short timing capture of the GPU-marker fixture and counts
GpuFrame and CustomMarker rows through pix_timing_sql. The report (default
tests/artifacts/gpu-frame-experiment.json) records every cell either way; this is a scheduled experiment,
not a test. Windows may show a UAC prompt when the timing recorder starts.
"""
import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import subprocess
import sys
import time

from benchmark import Investigator
from smoke import Client

PARTS_VARIABLE = "PIXMCP_TIMING_EXPERIMENT_PARTS"
DEFAULT_PARTS = ("GPU_ONLY_EVENTS", "MINIMAL_INSTRUMENTATION", "CIRCULAR", "FORCE_COM_PATH", "INCLUDE_CAPTURE_ETL")
COUNT_SQL = "SELECT (SELECT COUNT(*) FROM GpuFrame) AS gpuFrames, (SELECT COUNT(*) FROM CustomMarker) AS customMarkers"


def matrix(parts, windows=("hidden", "visible"), gpu_timing=(True, False)):
    cells = []
    for part in ("", *parts):
        for window in windows:
            for timing in gpu_timing:
                cells.append({"id": f"{part.lower() or 'none'}-{window}-{'gpu' if timing else 'nogpu'}",
                              "parts": part, "window": window, "gpuTiming": timing})
    return cells


def run_cell(query, app, output_dir, cell, seconds, sleep=time.sleep):
    result = dict(cell)
    connection = None
    try:
        connection = query("pix_device_connect")["handle"]
        arguments = ["--frames", "100000", "--gpu-markers", *(["--hidden"] if cell["window"] == "hidden" else [])]
        query("pix_device_launch", handle=connection, exePath=str(app), arguments=subprocess.list2cmdline(arguments),
              underGpuCapture=False)
        path = Path(output_dir) / f"gpu-frame-{cell['id']}.wpix"
        started = query("pix_device_timing_capture_start", handle=connection, outputPath=str(path), gpuTiming=cell["gpuTiming"],
                        durationSeconds=0, maxFileSizeMb=256)
        result["optionParts"] = started.get("optionParts")
        sleep(seconds)
        stopped = query("pix_device_timing_capture_stop", handle=connection, open=True, waitSeconds=0)
        handle = (stopped.get("timingCapture") or {}).get("handle")
        if not handle:
            raise RuntimeError(f"the timing capture could not be opened: {stopped}")
        counts = query("pix_timing_sql", handle=handle, sql=COUNT_SQL)
        result["gpuFrames"], result["customMarkers"] = counts["rows"][0][0], counts["rows"][0][1]
        result["capture"] = str(path)
        query("pix_close", handle=handle)
    except Exception as error:
        result["error"] = f"{type(error).__name__}: {error}"
    finally:
        if connection is not None:
            try:
                query("pix_device_detach", handle=connection, terminate=True)
            except Exception as error:
                result["cleanupError"] = f"{type(error).__name__}: {error}"
    return result


def summarize(results):
    populated = [row["id"] for row in results if (row.get("gpuFrames") or 0) > 0]
    return {"cells": len(results), "failed": sum("error" in row for row in results), "gpuFramePopulatedBy": populated}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("server")
    parser.add_argument("--app", default="tests/D3D12TestApp/bin/D3D12TestApp.exe")
    parser.add_argument("--output", default="tests/artifacts/gpu-frame-experiment.json")
    parser.add_argument("--parts", default=",".join(DEFAULT_PARTS), help="Comma-separated option parts to try one at a time.")
    parser.add_argument("--seconds", type=float, default=3)
    parser.add_argument("--timeout", type=float, default=120)
    parser.add_argument("--keep-captures", action="store_true")
    args = parser.parse_args()
    app = Path(args.app).resolve()
    output = Path(args.output).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    cells = matrix([part.strip().upper() for part in args.parts.split(",") if part.strip()])
    results = []
    for cell in cells:
        print(f"GpuFrame experiment: {cell['id']}", file=sys.stderr, flush=True)
        os.environ[PARTS_VARIABLE] = cell["parts"]
        client = Client([args.server], timeout=args.timeout)
        try:
            client.send("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                       "clientInfo": {"name": "pixmcp-gpu-frame-experiment", "version": "1.0"}})
            client.send("notifications/initialized", notify=True)
            result = run_cell(Investigator(client).query, app, output.parent, cell, args.seconds)
        finally:
            try:
                client.call("pix_close_all")
            finally:
                client.close()
        if not args.keep_captures and result.get("capture"):
            Path(result["capture"]).unlink(missing_ok=True)
        results.append(result)
        report = {"generatedAt": datetime.now(timezone.utc).isoformat(), "app": str(app), "seconds": args.seconds,
                  "summary": summarize(results), "results": results}
        output.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(summarize(results), indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
