"""Protocol/navigation checks for the investigation benchmark; no PIX or GPU required."""
import json
import io
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock, patch

from benchmark import Investigator, TaskRecorder, exit_status, known_root_constant_limitation, main, new_report
from smoke import SmokeError


class ScriptedClient:
    def __init__(self, steps):
        self.steps = list(steps)
        self.calls = []

    def call(self, tool, args):
        self.calls.append((tool, args))
        expected_tool, expected_args, response = self.steps.pop(0)
        if (tool, args) != (expected_tool, expected_args):
            raise AssertionError(f"Expected {(expected_tool, expected_args)}, got {(tool, args)}")
        if isinstance(response, Exception):
            raise response
        return response


def read_args(pointer="", offset=0):
    return {"resultRef": "r", "pointer": pointer, "offset": offset, "limit": 1000}


class BenchmarkMainTests(unittest.TestCase):
    def invoke(self, *, cleanup_error=None, shutdown_error=None, server_exit=0,
               task_status="passed", primary_error=None, strict=False):
        client = Mock(timeout=660)
        client.call.side_effect = cleanup_error
        client.close.side_effect = shutdown_error
        client.close.return_value = server_exit
        task = {"name": "original task", "status": task_status, "passed": task_status == "passed"}

        def run(client, capture, candidate, report):
            report["tasks"].append(task.copy())
            if primary_error:
                raise primary_error

        with tempfile.TemporaryDirectory() as folder:
            destination = Path(folder) / "report.json"
            arguments = ["benchmark.py", "server.exe", "--capture", "capture.wpix", "--output", str(destination)]
            if strict:
                arguments.append("--strict")
            stderr = io.StringIO()
            with patch("sys.argv", arguments), patch("benchmark.Client", return_value=client), \
                    patch("benchmark.run", side_effect=run), patch("sys.stdout", new=io.StringIO()), patch("sys.stderr", new=stderr):
                status = main()
            report = json.loads(destination.read_text(encoding="utf-8"))
        client.call.assert_called_once_with("pix_close_all")
        client.close.assert_called_once_with()
        self.assertEqual([task], report["tasks"])
        return status, report, stderr.getvalue()

    def test_clean_shutdown_preserves_success(self):
        status, report, stderr = self.invoke()
        self.assertEqual(0, status)
        self.assertEqual(0, report["serverExitCode"])
        self.assertNotIn("cleanupError", report)
        self.assertNotIn("shutdownError", report)
        self.assertEqual("", stderr)

    def test_cleanup_errors_fail_in_both_modes_and_still_close(self):
        for strict in (False, True):
            for error in (SmokeError("tool cleanup failed"), OSError("cleanup pipe failed")):
                with self.subTest(strict=strict, error=type(error).__name__):
                    status, report, stderr = self.invoke(cleanup_error=error, strict=strict)
                    self.assertEqual(1, status)
                    self.assertIn(str(error), report["cleanupError"])
                    self.assertEqual(0, report["serverExitCode"])
                    self.assertNotIn("fatalError", report)
                    self.assertIn("Benchmark cleanup failed", stderr)

    def test_nonzero_server_exit_fails_in_both_modes(self):
        for strict in (False, True):
            with self.subTest(strict=strict):
                status, report, stderr = self.invoke(server_exit=9, strict=strict)
                self.assertEqual(1, status)
                self.assertEqual(9, report["serverExitCode"])
                self.assertIn("code 9", report["shutdownError"])
                self.assertNotIn("cleanupError", report)
                self.assertIn("Benchmark shutdown failed", stderr)

    def test_report_survives_close_exception_in_both_modes(self):
        for strict in (False, True):
            with self.subTest(strict=strict):
                status, report, stderr = self.invoke(shutdown_error=OSError("close failed"), strict=strict)
                self.assertEqual(1, status)
                self.assertEqual("OSError: close failed", report["shutdownError"])
                self.assertNotIn("serverExitCode", report)
                self.assertNotIn("fatalError", report)
                self.assertIn("close failed", stderr)

    def test_primary_error_is_preserved_alongside_cleanup_and_shutdown(self):
        status, report, stderr = self.invoke(task_status="failed", primary_error=ValueError("primary failed"),
                                            cleanup_error=SmokeError("cleanup failed"), shutdown_error=OSError("close failed"))
        self.assertEqual(1, status)
        self.assertEqual("ValueError: primary failed", report["fatalError"])
        self.assertEqual("SmokeError: cleanup failed", report["cleanupError"])
        self.assertEqual("OSError: close failed", report["shutdownError"])
        for detail in ("primary failed", "cleanup failed", "close failed"):
            self.assertIn(detail, stderr)

    def test_primary_error_alone_fails_without_teardown_errors(self):
        status, report, _ = self.invoke(primary_error=ValueError("primary failed"))
        self.assertEqual(1, status)
        self.assertEqual("ValueError: primary failed", report["fatalError"])
        self.assertNotIn("cleanupError", report)
        self.assertNotIn("shutdownError", report)

    def test_strict_mode_only_changes_limited_and_skipped_task_outcomes(self):
        for task_status in ("passed", "limited", "skipped", "failed"):
            for strict in (False, True):
                with self.subTest(task_status=task_status, strict=strict):
                    status, _, _ = self.invoke(task_status=task_status, strict=strict)
                    self.assertEqual(int(task_status == "failed" or strict and task_status != "passed"), status)


class BenchmarkTests(unittest.TestCase):
    def test_worker_busy_recovery_follows_exact_wait_and_original_retry(self):
        args = {"handle": "gpu-1", "scope": {"handle": "gpu-1", "queueIndex": 0, "eventIndex": 9}}
        wait = {"jobId": "j", "timeoutSeconds": 2}
        detail = {"code": "worker_busy", "retryable": True, "nextCalls": [
            {"tool": "pix_job_wait", "arguments": wait}, {"tool": "scoped", "arguments": args}]}
        client = ScriptedClient([("scoped", args, SmokeError("scoped: tool error: " + json.dumps([detail]))),
                                 ("pix_job_wait", wait, {"jobId": "j", "status": "succeeded"}),
                                 ("scoped", args, {"answer": 42})])
        self.assertEqual({"answer": 42}, Investigator(client).query("scoped", **args))

    def test_worker_busy_does_not_guess_changed_arguments_or_retry_unrelated_errors(self):
        agent = Investigator(None)
        for code, retry_arguments in (("pix_error", {}), ("worker_busy", {"handle": "different"})):
            detail = {"code": code, "retryable": True, "nextCalls": [{"tool": "query", "arguments": retry_arguments}]}
            self.assertFalse(agent.recover_worker_busy(SmokeError("query: tool error: " + json.dumps([detail])), "query", {}))

    def test_native_root_constant_allowlist_does_not_hide_unexpected_failures(self):
        known = {"feature": "rootConstants", "error": {"code": "invalid_arguments",
                 "message": "ArgumentException: Value does not fall within the expected range."}}
        self.assertTrue(known_root_constant_limitation([known]))
        self.assertFalse(known_root_constant_limitation([]))
        self.assertFalse(known_root_constant_limitation([{"feature": "rootConstants", "error": {"code": "pix_error", "message": "device removed"}}]))
        self.assertFalse(known_root_constant_limitation([known, {"feature": "pipeline", "state": "unsupported"}]))

    def test_failed_request_remains_in_call_metrics(self):
        agent = Investigator(ScriptedClient([("broken", {}, SmokeError("failure"))]))
        with self.assertRaises(SmokeError):
            agent.call("broken")
        self.assertEqual(1, len(agent.calls))
        self.assertIn("failure", agent.calls[0]["error"])

    def test_partial_failures_do_not_prevent_independent_tasks(self):
        client = ScriptedClient([("pix_jobs", {}, {"items": []}), ("pix_jobs", {}, {"items": []})])
        report = new_report()
        recorder = TaskRecorder(Investigator(client), report)
        def fail():
            raise SmokeError("native failure")
        self.assertIsNone(recorder.task("failed", fail))
        self.assertIsNone(recorder.task("dependent", lambda: self.fail("must skip"), available=False))
        self.assertEqual(42, recorder.task("independent", lambda: 42))
        self.assertEqual(["failed", "skipped", "passed"], [row["status"] for row in report["tasks"]])
        self.assertEqual(1, exit_status(report))

    def test_strict_mode_rejects_limitations_without_reclassifying_them(self):
        report = new_report()
        recorder = TaskRecorder(Investigator(ScriptedClient([("pix_jobs", {}, {"items": []})])), report)
        recorder.task("known limitation", lambda: report["limitations"].append({"feature": "rootConstantValues"}))
        self.assertEqual("limited", report["tasks"][0]["status"])
        self.assertEqual(0, exit_status(report))
        self.assertEqual(1, exit_status(report, strict=True))

    def test_startup_failure_still_writes_report(self):
        with tempfile.TemporaryDirectory() as folder:
            output = Path(folder) / "report.json"
            with patch("sys.argv", ["benchmark.py", "missing.exe", "--capture", "a.wpix", "--output", str(output)]), \
                    patch("benchmark.Client", side_effect=SmokeError("startup failed")), patch("sys.stdout", new=io.StringIO()), patch("sys.stderr", new=io.StringIO()):
                self.assertEqual(1, main())
            self.assertIn("startup failed", json.loads(output.read_text(encoding="utf-8"))["fatalError"])

    def test_nested_deferred_fields_and_all_page_tails_are_retrieved(self):
        client = ScriptedClient([
            ("pix_result_read", read_args(), {"kind": "object", "value": {"items": {"deferred": True, "pointer": "/items"}}, "nextOffset": 1}),
            ("pix_result_read", read_args("/items"), {"kind": "array", "value": [{"deferred": True, "pointer": "/items/0"}], "nextOffset": 1}),
            ("pix_result_read", read_args("/items/0"), {"kind": "string", "value": "GPU ", "nextOffset": 4}),
            ("pix_result_read", read_args("/items/0", 4), {"kind": "string", "value": "\U0001f680"}),
            ("pix_result_read", read_args("/items", 1), {"kind": "array", "value": [99]}),
            ("pix_result_read", read_args(offset=1), {"kind": "object", "value": {"complete": True}}),
        ])
        self.assertEqual({"items": ["GPU \U0001f680", 99], "complete": True}, Investigator(client).read("r"))
        self.assertFalse(client.steps)

    def test_pending_query_waits_and_repeats_original_reference(self):
        args = {"eventRef": {"handle": "gpu-1", "queueIndex": 2, "eventIndex": 999}, "waitSeconds": 0}
        client = ScriptedClient([
            ("inspect", args, {"pending": True, "jobId": "j"}),
            ("pix_job_wait", {"jobId": "j", "timeoutSeconds": 30}, {"jobId": "j", "kind": "event-inspection", "status": "succeeded", "resultRef": "preparation"}),
            ("inspect", args, {"eventRef": args["eventRef"]}),
        ])
        agent = Investigator(client)
        self.assertEqual({"eventRef": args["eventRef"]}, agent.query("inspect", **args))
        self.assertEqual({"j"}, agent.replay_jobs)
        self.assertFalse(client.steps)

    def test_completed_job_reads_result_reference(self):
        client = ScriptedClient([
            ("compare", {}, {"jobId": "j", "kind": "capture-compare", "status": "succeeded", "resultRef": "r"}),
            ("pix_result_read", read_args(), {"kind": "object", "value": {"matchedCount": 7}}),
        ])
        self.assertEqual({"matchedCount": 7}, Investigator(client).query("compare"))

    def test_nonprogressing_continuation_fails(self):
        client = ScriptedClient([
            ("pix_result_read", read_args(), {"kind": "array", "value": [], "nextOffset": 0}),
        ])
        with self.assertRaisesRegex(SmokeError, "no progress"):
            Investigator(client).read("r")

    def test_failed_job_cannot_be_counted_as_success(self):
        with self.assertRaisesRegex(SmokeError, "Job failed"):
            Investigator(None).wait({"jobId": "j", "status": "failed", "error": {"code": "replay_failed"}})

    def test_inventory_counts_inline_preparations_without_counting_them_twice(self):
        agent = Investigator(None)
        for kind in ("timing", "event-inspection", "resource-uses", "capture-compare", "preview"):
            agent.observe_job({"kind": kind, "jobId": kind, "status": "succeeded"})
            agent.observe_job({"kind": kind, "jobId": kind, "status": "succeeded"})
        self.assertEqual(5, len(agent.replay_jobs))

    def test_bytes_measure_utf8_and_preparation_jobs_are_unique(self):
        response = {"jobId": "j", "kind": "timing", "status": "running", "message": "\U0001f680"}
        client = ScriptedClient([("status", {}, response), ("status", {}, response)])
        agent = Investigator(client)
        agent.call("status")
        agent.call("status")
        expected = len(json.dumps(response, ensure_ascii=False).encode("utf-8"))
        self.assertEqual([expected, expected], [call["returnedJsonBytes"] for call in agent.calls])
        self.assertEqual({"j"}, agent.replay_jobs)


if __name__ == "__main__":
    unittest.main()
