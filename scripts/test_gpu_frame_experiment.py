"""GpuFrame experiment orchestration tests; never launch an application or access a GPU."""
from pathlib import Path
import unittest

from gpu_frame_experiment import matrix, run_cell, summarize


class GpuFrameExperimentTests(unittest.TestCase):
    def test_matrix_covers_no_part_and_each_part_for_both_windows_and_gpu_timing(self):
        cells = matrix(["GPU_ONLY_EVENTS", "CIRCULAR"])
        self.assertEqual(12, len(cells))
        self.assertEqual(len(cells), len({cell["id"] for cell in cells}))
        self.assertEqual({"id": "none-hidden-gpu", "parts": "", "window": "hidden", "gpuTiming": True}, cells[0])
        self.assertEqual({"", "GPU_ONLY_EVENTS", "CIRCULAR"}, {cell["parts"] for cell in cells})

    def test_cell_records_counts_option_parts_and_detaches(self):
        calls = []
        responses = {
            "pix_device_connect": {"handle": "device-1"},
            "pix_device_launch": {"processId": 7},
            "pix_device_timing_capture_start": {"optionParts": [{"type": "gpuOnlyEvents", "value": "true"}]},
            "pix_device_timing_capture_stop": {"timingCapture": {"handle": "timing-1"}},
            "pix_timing_sql": {"rows": [[3, 589]]},
        }
        def query(tool, **arguments):
            calls.append((tool, arguments))
            return responses.get(tool, {})
        cell = matrix(["GPU_ONLY_EVENTS"])[5]
        result = run_cell(query, Path("app.exe"), Path("out"), cell, 1, sleep=lambda _: None)
        self.assertEqual((3, 589), (result["gpuFrames"], result["customMarkers"]))
        self.assertEqual([{"type": "gpuOnlyEvents", "value": "true"}], result["optionParts"])
        launch = next(arguments for tool, arguments in calls if tool == "pix_device_launch")
        self.assertEqual("visible" == cell["window"], "--hidden" not in launch["arguments"])
        start = next(arguments for tool, arguments in calls if tool == "pix_device_timing_capture_start")
        self.assertEqual(cell["gpuTiming"], start["gpuTiming"])
        self.assertEqual("pix_device_detach", calls[-1][0])
        self.assertNotIn("error", result)

    def test_failures_are_recorded_and_cleanup_still_runs(self):
        calls = []
        def query(tool, **arguments):
            calls.append(tool)
            if tool == "pix_device_timing_capture_start":
                raise RuntimeError("UAC declined")
            return {"handle": "device-1", "processId": 1}
        result = run_cell(query, Path("app.exe"), Path("out"), matrix([])[0], 1, sleep=lambda _: None)
        self.assertIn("UAC declined", result["error"])
        self.assertEqual("pix_device_detach", calls[-1])
        self.assertEqual({"cells": 2, "failed": 1, "gpuFramePopulatedBy": ["b"]},
                         summarize([result, {"id": "b", "gpuFrames": 2}]))


if __name__ == "__main__":
    unittest.main()
