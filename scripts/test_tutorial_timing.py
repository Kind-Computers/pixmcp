"""Timing harness regressions without starting PIX or a graphics workload."""

from copy import deepcopy
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import patch

from smoke import SmokeError
import tutorial_timing as timing


class FakeContext:
    def __init__(self):
        self.args = SimpleNamespace()
        self.artifacts = Path("unused-artifacts")
        self.report = {"tasks": []}
        self.calls = []
        self.saved = []

    def query(self, tool, **arguments):
        self.calls.append((tool, arguments))
        if tool == "pix_timing_open":
            return {"handle": "timing-1"}
        if tool == "pix_timing_save":
            return {"saved": arguments["asPath"]}
        return {}

    def task(self, name, callback, optional=False):
        entry = {"name": name, "status": "running"}
        self.report["tasks"].append(entry)
        try:
            value = callback()
            entry["status"] = "passed"
            return value
        except Exception as error:
            entry.update(status="failed", error=str(error))
            return None

    def save(self, name, value):
        self.saved.append((name, deepcopy(value)))
        return name + ".json"


class TimingLifecycleTests(unittest.TestCase):
    def test_repeated_capture_stems_keep_distinct_resolved_artifacts(self):
        ctx = FakeContext()
        with patch.object(timing, "_symbol_paths", return_value=[]), patch.object(timing, "_pages", return_value=None):
            timing.run_timing(ctx, "first/timing.wpix", 1)
            timing.run_timing(ctx, "second/timing.wpix", 2)
        destinations = [arguments["asPath"] for tool, arguments in ctx.calls if tool == "pix_timing_save"]
        self.assertEqual(len(destinations), 2)
        self.assertNotEqual(destinations[0], destinations[1])
        self.assertTrue(all(Path(path).parent == ctx.artifacts for path in destinations))
        self.assertEqual(sum(tool == "pix_close" for tool, _ in ctx.calls), 2)

    def test_processing_exception_closes_handle_and_preserves_failed_report(self):
        ctx = FakeContext()
        with patch.object(timing, "_symbol_paths", side_effect=OSError("symbol inventory unavailable")):
            with self.assertRaisesRegex(OSError, "symbol inventory unavailable"):
                timing.run_timing(ctx, "timing.wpix", 1)
        self.assertIn(("pix_close", {"handle": "timing-1"}), ctx.calls)
        report = next(value for name, value in reversed(ctx.saved) if name == "timing-investigation")
        self.assertEqual(report["status"], "failed")
        self.assertEqual(report["error"], "OSError: symbol inventory unavailable")


class SubmissionPrecisionTests(unittest.TestCase):
    def test_timestamp_arithmetic_preserves_values_beyond_float_precision(self):
        row = {"submitNs": "9007199254740993", "gpuBeginNs": "9007199254741000",
               "gpuEndNs": "9007199254741101", "latencyNs": "7", "gpuDurationNs": "101",
               "gpuTiming": {"state": "available"}}
        self.assertEqual(timing.validate_submissions([row])["usableGpuTimings"], 1)
        with self.assertRaisesRegex(SmokeError, "latency"):
            timing.validate_submissions([{**row, "latencyNs": "8"}])

    def test_unavailable_gpu_timing_cannot_supply_derived_duration(self):
        row = {"latencyNs": None, "gpuDurationNs": "0", "gpuTiming": {"state": "unavailable"}}
        with self.assertRaisesRegex(SmokeError, "Unavailable"):
            timing.validate_submissions([row])


if __name__ == "__main__":
    unittest.main()
