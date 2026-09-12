"""Run with python -m unittest discover -s scripts -p test_smoke.py."""
import contextlib
import base64
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest
from unittest.mock import patch

import smoke


class SmokeTests(unittest.TestCase):
    def start(self, body, timeout=2):
        client = smoke.Client([sys.executable, "-u", "-c", body], timeout=timeout, shutdown_timeout=0.2)
        self.addCleanup(client.close)
        return client

    def responding(self, response):
        return self.start("import json, sys\nfor line in sys.stdin:\n"
                          " request = json.loads(line)\n"
                          " if 'id' not in request: continue\n"
                          f" response = json.loads({json.dumps(response)!r})\n"
                          " response.update(jsonrpc='2.0', id=request['id'])\n"
                          " print(json.dumps(response), flush=True)\n")

    @staticmethod
    def tool_result(value, **extra):
        return {"result": {"content": [{"type": "text", "text": json.dumps(value)}], **extra}}

    def test_rpc_error_is_a_failure(self):
        client = self.responding({"error": {"code": -32601, "message": "missing tool"}})
        with self.assertRaisesRegex(smoke.SmokeError, "RPC error.*missing tool"):
            client.call("missing")

    def test_tool_error_is_a_failure(self):
        client = self.responding(self.tool_result("bad handle", isError=True))
        with self.assertRaisesRegex(smoke.SmokeError, "tool error.*bad handle"):
            client.call("inspect")

    def test_image_content_is_decoded_and_reports_actual_byte_count(self):
        png = b"\x89PNG\r\n\x1a\n\xff"
        client = self.responding({"result": {"content": [{"type": "image", "mimeType": "image/png", "data": base64.b64encode(png).decode("ascii")}]}})
        self.assertEqual({"type": "image", "mimeType": "image/png", "bytes": len(png)}, client.call("preview"))

    def test_malformed_image_data_fails_smoke_checks(self):
        for invalid in ("\ufffdPNG", "%%%invalid%%%", None):
            with self.subTest(invalid=invalid):
                client = self.responding({"result": {"content": [{"type": "image", "mimeType": "image/png", "data": invalid}]}})
                with self.assertRaisesRegex(smoke.SmokeError, "image content.*base64"):
                    client.call("preview")
                client.close()

    def test_failed_and_cancelled_jobs_are_failures(self):
        for status in ("failed", "cancelled"):
            with self.subTest(status=status):
                client = self.responding(self.tool_result({"jobId": "job-3", "status": status, "error": "reason"}))
                with self.assertRaisesRegex(smoke.SmokeError, f"job job-3 {status}: reason"):
                    client.call("job")
                client.close()

    def test_queued_job_remains_usable_without_completion_assertion(self):
        value = {"jobId": "job-3", "status": "queued"}
        client = self.responding(self.tool_result(value))
        self.assertEqual(value, client.call("job"))

    def test_silent_server_times_out_and_is_reaped(self):
        client = self.start("import sys, time\nsys.stdin.readline()\ntime.sleep(30)", timeout=0.2)
        with self.assertRaisesRegex(smoke.SmokeError, "timed out"):
            client.send("initialize")
        client.close()
        self.assertIsNotNone(client.proc.poll())
        self.assertTrue(all(not thread.is_alive() for thread in client._threads))

    def test_notifications_do_not_extend_deadline(self):
        client = self.start("import json, sys, time\nsys.stdin.readline()\n"
                            "while True:\n"
                            " print(json.dumps({'jsonrpc':'2.0','method':'notifications/message'}), flush=True)\n"
                            " time.sleep(0.02)\n", timeout=0.3)
        started = time.monotonic()
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaisesRegex(smoke.SmokeError, "timed out"):
            client.send("initialize")
        self.assertLess(time.monotonic() - started, 2)

    def test_malformed_stdout_is_reported(self):
        client = self.start("import sys\nsys.stdin.readline()\nprint('not json', flush=True)")
        with self.assertRaisesRegex(smoke.SmokeError, "malformed JSON.*not json"):
            client.send("initialize")

    def test_non_protocol_json_is_reported(self):
        client = self.start("import sys\nsys.stdin.readline()\nprint('[]', flush=True)")
        with self.assertRaisesRegex(smoke.SmokeError, "invalid JSON-RPC"):
            client.send("initialize")

    def test_early_exit_preserves_stderr(self):
        client = self.start("import sys\nsys.stdin.readline()\nprint('install PIX first', file=sys.stderr)")
        with self.assertRaisesRegex(smoke.SmokeError, "server exited"):
            client.send("initialize")
        client.close()
        self.assertIn("install PIX first", client.stderr_lines)

    def test_stderr_history_is_bounded(self):
        client = self.start("import sys\nfor i in range(1000): print(i, file=sys.stderr)\n"
                            "sys.stderr.flush()\nsys.stdin.read()")
        client.close()
        self.assertEqual(200, len(client.stderr_lines))
        self.assertEqual("999", client.stderr_lines[-1])

    def test_resolves_nested_values_and_present_null(self):
        result = {"open": {"handle": "gpu-8"}, "last": {"items": [{"index": 9, "missing": None}]}}
        self.assertEqual({"handle": "gpu-8", "event": [9, None]}, smoke.resolve(
            {"handle": "$open.handle", "event": ["$last.items.0.index", "$last.items.0.missing"]}, result))

    def test_missing_references_fail(self):
        for reference in ("$unknown", "$last.absent", "$last.items.1", "$last.items.-1", "$last.items.nope"):
            with self.subTest(reference=reference), self.assertRaisesRegex(smoke.SmokeError, "Missing reference"):
                smoke.resolve(reference, {"last": {"items": [1]}})

    def test_environment_reference_requires_variable(self):
        with patch.dict(os.environ, {}, clear=True):
            with self.assertRaisesRegex(smoke.SmokeError, "Missing environment variable"):
                smoke.resolve("$env.PIX_TEST_CAPTURE", {})
        with patch.dict(os.environ, {"PIX_TEST_CAPTURE": "sample.wpix"}):
            self.assertEqual("sample.wpix", smoke.resolve("$env.PIX_TEST_CAPTURE", {}))

    def test_expectations_use_exact_nested_json_values(self):
        smoke.assert_result({"status": "succeeded", "result": {"items": [3]}}, {"status": "succeeded", "result.items.0": 3})
        for actual, expected in (("queued", "succeeded"), (True, 1), ({"a": True}, {"a": 1})):
            with self.subTest(actual=actual), self.assertRaisesRegex(smoke.SmokeError, "Assertion"):
                smoke.assert_result({"value": actual}, {"value": expected})

    def test_failed_step_stops_later_calls(self):
        client = self.responding(self.tool_result({"status": "queued"}))
        with self.assertRaisesRegex(smoke.SmokeError, r"Step 1 \(start\): Assertion"):
            smoke.run_steps(client, [["start", {}, {"status": "succeeded"}], ["later", {}]], 100)
        self.assertEqual(2, client.next_id)

    def test_invalid_scenario_is_rejected_before_startup(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "scenario.json"
            path.write_text('[ ["tool", {}, []] ]', encoding="utf-8")
            with patch.object(smoke, "Client") as start, contextlib.redirect_stderr(io.StringIO()):
                self.assertEqual(1, smoke.main(["unused.exe", "@" + str(path)]))
            start.assert_not_called()

    def test_cli_failure_returns_nonzero_and_cleans_up(self):
        for response in ({"error": {"code": -1, "message": "initialization failed"}},
                         {"result": {"serverInfo": {"name": "test"}}}):
            client = self.responding(response)
            errors = io.StringIO()
            with patch.object(smoke, "Client", return_value=client), contextlib.redirect_stderr(errors), contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(1, smoke.main(["unused.exe", "inspect"]))
            self.assertIsNotNone(client.proc.poll())
            self.assertIn("smoke:", errors.getvalue())

    def test_cli_success_returns_zero_and_cleans_up(self):
        client = self.responding({"result": {"serverInfo": {"name": "test"}}})
        with patch.object(smoke, "Client", return_value=client), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(0, smoke.main(["unused.exe"]))
        self.assertEqual(0, client.proc.poll())
        self.assertEqual(0, client.close())

    def test_cli_tool_failure_reports_step_and_returns_nonzero(self):
        client = self.start("import json, sys\nfor line in sys.stdin:\n"
                            " request = json.loads(line)\n"
                            " if 'id' not in request: continue\n"
                            " result = {'serverInfo': {'name': 'test'}} if request['method'] == 'initialize' else "
                            "{'isError': True, 'content': [{'type': 'text', 'text': 'invalid capture'}]}\n"
                            " print(json.dumps({'jsonrpc': '2.0', 'id': request['id'], 'result': result}), flush=True)\n")
        errors = io.StringIO()
        with patch.object(smoke, "Client", return_value=client), contextlib.redirect_stderr(errors), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(1, smoke.main(["unused.exe", "inspect"]))
        self.assertIn("Step 1 (inspect): inspect: tool error", errors.getvalue())
        self.assertIn("invalid capture", errors.getvalue())
        self.assertIsNotNone(client.proc.poll())

    def test_tools_listing_keeps_all_names_despite_output_limit(self):
        client = self.responding({"result": {"tools": [{"name": "first"}, {"name": "second"}]}})
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            smoke.run_steps(client, [["tools", {}]], 1)
        self.assertIn("2 tools:", output.getvalue())
        self.assertIn("first", output.getvalue())
        self.assertIn("second", output.getvalue())

    def test_process_exit_code_is_nonzero_on_failure(self):
        result = subprocess.run([sys.executable, str(Path(smoke.__file__)), "does-not-exist-pixmcp.exe"],
                                capture_output=True, text=True, timeout=10)
        self.assertEqual(1, result.returncode)
        self.assertIn("smoke:", result.stderr)

    def test_invalid_timeout_is_rejected(self):
        for timeout in (0, -1, float("nan"), float("inf")):
            with self.subTest(timeout=timeout), self.assertRaises(ValueError):
                smoke.Client(["does-not-exist.exe"], timeout=timeout)


if __name__ == "__main__":
    unittest.main()
