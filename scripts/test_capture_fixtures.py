"""Fixture orchestration tests; never launch an application or access a GPU."""
import hashlib
import json
import io
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock, patch

from capture_fixtures import fixture_source_hashes, generate, main, source_sha256
from smoke import SmokeError


class FakeInvestigator:
    def __init__(self, folder, fail_baseline=False):
        self.folder = Path(folder)
        self.calls = []
        self.fail_baseline = fail_baseline
        self.variant = None

    def query(self, tool, **arguments):
        self.calls.append({"tool": tool, "arguments": arguments})
        if tool == "pix_device_connect":
            return {"handle": "device-1"}
        if tool == "pix_device_launch":
            self.variant = "candidate" if "--variant candidate" in arguments["arguments"] else "baseline"
            return {"processId": 10, "capturable": True, "ready": False}
        if tool == "pix_device_take_gpu_capture":
            if self.fail_baseline and self.variant == "baseline":
                raise SmokeError("baseline failed")
            source = self.folder / f"source-{self.variant}.wpix"
            source.write_bytes(b"capture")
            return {"path": str(source)}
        if tool == "pix_device_timing_capture_start":
            Path(arguments["outputPath"]).write_bytes(b"timing")
        if tool == "pix_device_timing_capture_stop":
            return {"timingCapture": {"handle": "timing-1"}}
        return {}


class FixtureMainTests(unittest.TestCase):
    def invoke(self, *, cleanup_error=None, shutdown_error=None, server_exit=0, fixture_error=None, primary_error=None, adapter_name=None):
        client = Mock(timeout=60)
        client.call.side_effect = cleanup_error
        client.close.side_effect = shutdown_error
        client.close.return_value = server_exit
        fixture = {"name": "baseline", "status": "failed" if fixture_error else "passed"}
        if fixture_error:
            fixture["error"] = fixture_error
        generated = {"fixtures": [fixture.copy()], "errors": [fixture.copy()] if fixture_error else []}
        with tempfile.TemporaryDirectory() as folder:
            directory = Path(folder)
            app = directory / "app.exe"
            for path in (app, app.with_suffix(".pdb"), directory / "WinPixEventRuntime.dll"):
                path.write_bytes(b"mock dependency")
            stderr = io.StringIO()
            arguments = ["capture_fixtures.py", "server.exe", "--app", str(app), "--output-dir", str(directory)]
            if adapter_name is not None:
                arguments += ["--adapter-name", adapter_name]
            with patch("sys.argv", arguments), \
                    patch("capture_fixtures.Client", return_value=client), \
                    patch("capture_fixtures.generate", return_value=generated, side_effect=primary_error) as generated_call, \
                    patch("sys.stdout", new=io.StringIO()), patch("sys.stderr", new=stderr):
                status = main()
                self.assertEqual(adapter_name, generated_call.call_args.kwargs["adapter_name"])
            report = json.loads((directory / "fixture-report.json").read_text(encoding="utf-8"))
        client.call.assert_called_once_with("pix_close_all")
        client.close.assert_called_once_with()
        self.assertEqual([] if primary_error else [fixture], report["fixtures"])
        return status, report, stderr.getvalue()

    def test_clean_shutdown_preserves_success(self):
        status, report, stderr = self.invoke()
        self.assertEqual(0, status)
        self.assertEqual(0, report["serverExitCode"])
        self.assertEqual([], report["errors"])
        self.assertEqual("", stderr)

    def test_cli_passes_adapter_filter_to_generation(self):
        status, report, _ = self.invoke(adapter_name="Intel Arc B580")
        self.assertEqual(0, status)
        self.assertEqual([], report["errors"])

    def test_cli_rejects_empty_adapter_filter_before_starting_server(self):
        with patch("sys.argv", ["capture_fixtures.py", "server.exe", "--adapter-name", "  "]), \
                patch("capture_fixtures.Client") as client, patch("sys.stderr", new=io.StringIO()):
            with self.assertRaises(SystemExit) as error:
                main()
        self.assertEqual(2, error.exception.code)
        client.assert_not_called()

    def test_cleanup_errors_fail_and_still_close(self):
        for error in (SmokeError("tool cleanup failed"), OSError("cleanup pipe failed")):
            with self.subTest(error=type(error).__name__):
                status, report, stderr = self.invoke(cleanup_error=error)
                self.assertEqual(1, status)
                self.assertEqual([{"cleanupError": f"{type(error).__name__}: {error}"}], report["errors"])
                self.assertEqual(0, report["serverExitCode"])
                self.assertIn("Fixture cleanup failed", stderr)

    def test_nonzero_server_exit_fails(self):
        status, report, stderr = self.invoke(server_exit=9)
        self.assertEqual(1, status)
        self.assertEqual(9, report["serverExitCode"])
        self.assertEqual([{"shutdownError": "Server exited with code 9."}], report["errors"])
        self.assertIn("Fixture shutdown failed", stderr)

    def test_report_survives_close_exception(self):
        status, report, stderr = self.invoke(shutdown_error=OSError("close failed"))
        self.assertEqual(1, status)
        self.assertEqual([{"shutdownError": "OSError: close failed"}], report["errors"])
        self.assertNotIn("serverExitCode", report)
        self.assertIn("Fixture shutdown failed", stderr)

    def test_fixture_failure_is_preserved_alongside_cleanup_and_shutdown(self):
        status, report, _ = self.invoke(fixture_error="capture failed", cleanup_error=SmokeError("cleanup failed"),
                                       shutdown_error=OSError("close failed"))
        self.assertEqual(1, status)
        self.assertEqual([{"name": "baseline", "status": "failed", "error": "capture failed"},
                          {"cleanupError": "SmokeError: cleanup failed"},
                          {"shutdownError": "OSError: close failed"}], report["errors"])

    def test_primary_exception_is_preserved_alongside_cleanup_and_shutdown(self):
        status, report, stderr = self.invoke(primary_error=ValueError("primary failed"),
                                            cleanup_error=SmokeError("cleanup failed"), server_exit=9)
        self.assertEqual(1, status)
        self.assertEqual([{"error": "ValueError: primary failed"}, {"cleanupError": "SmokeError: cleanup failed"},
                          {"shutdownError": "Server exited with code 9."}], report["errors"])
        for detail in ("primary failed", "cleanup failed", "code 9"):
            self.assertIn(detail, stderr)

    def test_primary_exception_alone_fails(self):
        status, report, _ = self.invoke(primary_error=ValueError("primary failed"))
        self.assertEqual(1, status)
        self.assertEqual([{"error": "ValueError: primary failed"}], report["errors"])


class FixtureTests(unittest.TestCase):
    def test_adapter_filter_is_quoted_for_both_gpu_and_timing_launches(self):
        # Spaces, embedded quotes and a trailing slash must remain one Win32 argument.
        adapter_name = 'Intel Arc "B580"\\'
        with tempfile.TemporaryDirectory() as folder:
            agent = FakeInvestigator(folder)
            with patch("capture_fixtures.Investigator", return_value=agent):
                report = generate(None, Path(folder) / "app.exe", Path(folder) / "out",
                                  sleep=lambda _: None, adapter_name=adapter_name)
            self.assertEqual([], report["errors"])
            launches = [row["arguments"] for row in agent.calls if row["tool"] == "pix_device_launch"]
            self.assertEqual([True, True, False], [row["underGpuCapture"] for row in launches])
            expected_suffix = r'--adapter-name "Intel Arc \"B580\"\\"'
            for launch in launches:
                self.assertTrue(launch["arguments"].endswith(expected_suffix), launch["arguments"])
                self.assertEqual(1, launch["arguments"].count("--adapter-name"))
            self.assertEqual(adapter_name, report["requestedAdapterName"])
            self.assertEqual([row["arguments"] for row in launches],
                             [row["arguments"] for row in report["launches"]])

    def test_empty_adapter_filter_is_rejected_before_any_mcp_work(self):
        with patch("capture_fixtures.Investigator") as agent:
            for adapter_name in ("", " \t", "B580\0"):
                with self.subTest(adapter_name=adapter_name), self.assertRaises(ValueError):
                    generate(None, "unused.exe", "unused-output", adapter_name=adapter_name)
        agent.assert_not_called()

    def test_cleanup_failure_keeps_the_original_capture_error(self):
        with tempfile.TemporaryDirectory() as folder:
            agent = FakeInvestigator(folder, fail_baseline=True)
            original = agent.query
            def query(tool, **arguments):
                if tool == "pix_device_detach":
                    raise SmokeError("cleanup failed")
                return original(tool, **arguments)
            agent.query = query
            with patch("capture_fixtures.Investigator", return_value=agent):
                report = generate(None, Path(folder) / "app.exe", Path(folder) / "out", sleep=lambda _: None, only="gpu")
            self.assertIn("baseline failed", report["fixtures"][0]["error"])
            self.assertIn("pix_device_take_gpu_capture", report["fixtures"][0]["error"])
            self.assertTrue(any("cleanupErrors" in error for error in report["errors"]))

    def test_targets_launch_before_recording_and_cleanup_is_serial(self):
        with tempfile.TemporaryDirectory() as folder:
            agent = FakeInvestigator(folder)
            with patch("capture_fixtures.Investigator", return_value=agent):
                report = generate(None, Path(folder) / "app.exe", Path(folder) / "out", sleep=lambda _: None)
            self.assertFalse(report["errors"])
            self.assertEqual(["baseline", "candidate", "timing"], [row["name"] for row in report["fixtures"]])
            calls = agent.calls
            timing_start = next(i for i, row in enumerate(calls) if row["tool"] == "pix_device_timing_capture_start")
            self.assertEqual("pix_device_launch", calls[timing_start - 1]["tool"])
            self.assertIn("--timing-workload", calls[timing_start - 1]["arguments"]["arguments"])
            self.assertFalse(calls[timing_start - 1]["arguments"]["underGpuCapture"])
            self.assertTrue(calls[timing_start]["arguments"]["cpuSampleStacks"])
            self.assertTrue(calls[timing_start]["arguments"]["contextSwitchStacks"])
            self.assertTrue(calls[timing_start]["arguments"]["captureSysmonCounters"])
            self.assertEqual(3, sum(row["tool"] == "pix_device_detach" for row in calls))
            self.assertTrue(all("--hang" not in row["arguments"].get("arguments", "") for row in calls))
            self.assertTrue(all("--adapter-name" not in row["arguments"].get("arguments", "") for row in calls))
            self.assertIsNone(report["requestedAdapterName"])
            self.assertEqual("pix_device_detach", calls[-1]["tool"])
            persisted = json.loads((Path(folder) / "out" / "fixture-report.json").read_text(encoding="utf-8"))
            self.assertEqual(3, len(persisted["fixtures"]))

    def test_capture_failure_is_reported_but_other_fixtures_still_run(self):
        with tempfile.TemporaryDirectory() as folder:
            agent = FakeInvestigator(folder, fail_baseline=True)
            with patch("capture_fixtures.Investigator", return_value=agent):
                report = generate(None, Path(folder) / "app.exe", Path(folder) / "out", sleep=lambda _: None)
            self.assertEqual(["failed", "passed", "passed"], [row["status"] for row in report["fixtures"]])
            self.assertEqual(1, len(report["errors"]))
            self.assertEqual(3, sum(row["tool"] == "pix_device_detach" for row in agent.calls))


class ProfileInvestigator(FakeInvestigator):
    """Also plays the fixture app: writes the --report sidecar and any --programmatic-capture file at launch."""

    def query(self, tool, **arguments):
        if tool == "pix_device_launch":
            parts = arguments["arguments"].split()
            if "--report" in parts:
                Path(parts[parts.index("--report") + 1]).write_text(json.dumps(
                    {"flags": [p for p in parts if p in ("--dxc", "--mesh", "--depth")], "skips": ["--reserved: tiled resources are not supported"],
                     "dxcVersion": "1.8"}), encoding="utf-8")
            if "--programmatic-capture" in parts:
                Path(parts[parts.index("--programmatic-capture") + 1]).write_bytes(b"programmatic capture")
        return super().query(tool, **arguments)


class FixtureProfileTests(unittest.TestCase):
    EXPECTED = {"default": ["baseline", "candidate", "timing"], "rich": ["rich", "rich-timing"],
                "perf": ["perf-baseline", "perf-candidate"], "sm6": ["sm6"], "programmatic": ["programmatic"]}

    def run_profile(self, profile, agent_type=ProfileInvestigator, **options):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        agent = agent_type(directory.name)
        with patch("capture_fixtures.Investigator", return_value=agent):
            report = generate(None, Path(directory.name) / "app.exe", Path(directory.name) / "out", sleep=lambda _: None,
                              profile=profile, **options)
        return agent, report, Path(directory.name) / "out"

    def test_profiles_select_their_fixture_sets(self):
        for profile, names in self.EXPECTED.items():
            with self.subTest(profile=profile):
                _, report, out = self.run_profile(profile)
                self.assertEqual([], report["errors"])
                self.assertEqual(names, [row["name"] for row in report["fixtures"]])
                self.assertEqual(profile, report["profile"])
                for name in names:
                    self.assertTrue((out / f"{name}.wpix").is_file(), name)
        _, report, _ = self.run_profile("all")
        self.assertEqual(sum(self.EXPECTED.values(), []), [row["name"] for row in report["fixtures"]])

    def test_launch_flags_frame_counts_and_sidecar_reports(self):
        agent, report, _ = self.run_profile("all")
        launches = {row["fixture"]: row["arguments"] for row in report["launches"]}
        self.assertIn("--msaa 4 --mrt 2", launches["rich"])
        self.assertIn("--gpu-markers", launches["rich-timing"])
        self.assertIn("--workload perf --variant candidate", launches["perf-candidate"])
        self.assertIn("--dxc --mesh", launches["sm6"])
        self.assertIn("--programmatic-capture", launches["programmatic"])
        self.assertTrue(all("--report" in arguments for arguments in launches.values()))
        captures = [row["arguments"] for row in agent.calls if row["tool"] == "pix_device_take_gpu_capture"]
        self.assertEqual([1, 1, 3, 1, 1, 1], [capture["frameCount"] for capture in captures])
        fixtures = {row["name"]: row for row in report["fixtures"]}
        self.assertEqual(["--reserved: tiled resources are not supported"], fixtures["rich"]["fixture"]["skips"])
        self.assertEqual(["--dxc", "--mesh"], fixtures["sm6"]["fixture"]["flags"])
        self.assertEqual(3, fixtures["programmatic"]["frameCount"])
        self.assertIn("commit", report)
        for row in report["fixtures"]:
            self.assertEqual(hashlib.sha256(Path(row["path"]).read_bytes()).hexdigest(), row["sha256"], row["name"])

    def test_missing_programmatic_capture_fails_after_its_wait_and_detaches(self):
        ticks = iter(range(0, 10000, 50))
        agent, report, _ = self.run_profile("programmatic", agent_type=FakeInvestigator, clock=lambda: next(ticks),
                                            programmatic_wait_seconds=100)
        self.assertEqual("failed", report["fixtures"][0]["status"])
        self.assertIn("did not appear", report["fixtures"][0]["error"])
        self.assertEqual(1, sum(row["tool"] == "pix_device_detach" for row in agent.calls))

    def test_unknown_profile_is_rejected_before_any_mcp_work(self):
        with patch("capture_fixtures.Investigator") as agent, self.assertRaises(ValueError):
            generate(None, "unused.exe", "unused-output", profile="turbo")
        agent.assert_not_called()


class SourceHashTests(unittest.TestCase):
    def test_source_hash_ignores_line_endings_and_bom(self):
        with tempfile.TemporaryDirectory() as directory:
            lf, crlf = Path(directory) / "lf.cpp", Path(directory) / "crlf.cpp"
            lf.write_bytes(b"int main()\n{\n}\n")
            crlf.write_bytes(b"\xef\xbb\xbfint main()\r\n{\r\n}\r\n")
            self.assertEqual(hashlib.sha256(b"int main()\n{\n}\n").hexdigest(), source_sha256(lf))
            self.assertEqual(source_sha256(lf), source_sha256(crlf))
            self.assertIsNone(source_sha256(Path(directory) / "missing.cpp"))

    def test_fixture_sources_are_found_beside_or_above_the_app(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "bin").mkdir()
            (root / "main.cpp").write_text("x\n", encoding="utf-8")
            hashes = fixture_source_hashes(root / "bin" / "app.exe")
            self.assertEqual(source_sha256(root / "main.cpp"), hashes["main.cpp"])
            self.assertIsNone(hashes["build.cmd"])
            self.assertIsNone(fixture_source_hashes(root / "elsewhere" / "deeper" / "app.exe"))


if __name__ == "__main__":
    unittest.main()
