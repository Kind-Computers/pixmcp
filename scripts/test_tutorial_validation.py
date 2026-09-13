"""Deterministic tutorial harness checks; no PIX, Unreal or GPU is launched."""
import base64
import io
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

import tutorial_validation as tutorial
from smoke import SmokeError


INTEL = "Intel(R) Arc(TM) B580 Graphics"
NVIDIA = "NVIDIA GeForce RTX 4070 Ti"


def adapter_log(chosen=1):
    return (
        f"LogD3D12RHI: Found D3D12 adapter 0: {NVIDIA} (VendorId: 10de, DeviceId: 2782)\n"
        f"LogD3D12RHI: Found D3D12 adapter 1: {INTEL} (VendorId: 8086, DeviceId: e20b)\n"
        f"LogD3D12RHI: Chosen D3D12 Adapter Id = {chosen}\n"
    )


def startup_settings_log(screen=100):
    values = {"r.Quinlight.Enabled": 0, "r.AntiAliasingMethod": 4, "r.ScreenPercentage": screen,
              "r.DynamicRes.OperationMode": 0, "r.VSync": 0, "t.MaxFPS": 0}
    return "".join(f'[2026.09.12-23.34.07:379][  0]{key} = "{value}"\n' for key, value in values.items())


def context():
    """Construct only the managed reporting state, with all external I/O mocked."""
    ctx = tutorial.Context.__new__(tutorial.Context)
    ctx.report = {"tasks": [], "limitations": [], "csvRuns": {}}
    ctx.args = SimpleNamespace(startup_seconds=120, adapter="both", runs=1)
    ctx.persist = Mock()
    ctx.log = Mock()
    ctx.save = Mock(return_value="evidence.json")
    ctx.agent = Mock(calls=[])
    ctx.client = Mock(last_result_ref="result-1")
    ctx.connections = set()
    ctx.recording = None
    return ctx


class AdapterEvidenceTests(unittest.TestCase):
    def test_chosen_adapter_index_resolves_to_enumerated_intel(self):
        evidence = tutorial.validate_adapter(adapter_log(), "intel")
        self.assertIn("B580", json.dumps(evidence))

    def test_chosen_adapter_index_resolves_to_enumerated_nvidia(self):
        evidence = tutorial.validate_adapter(adapter_log(0), "nvidia")
        self.assertIn("4070 Ti", json.dumps(evidence))

    def test_enumeration_without_selection_is_not_capture_adapter_evidence(self):
        log = adapter_log().split("LogD3D12RHI: Chosen", 1)[0]
        with self.assertRaises(SmokeError):
            tutorial.validate_adapter(log, "intel")

    def test_wrong_chosen_adapter_is_rejected_even_when_intel_is_enumerated(self):
        with self.assertRaises(SmokeError):
            tutorial.validate_adapter(adapter_log(0), "intel")

    def test_unknown_chosen_index_is_rejected(self):
        with self.assertRaises(SmokeError):
            tutorial.validate_adapter(adapter_log(4), "intel")

    def test_latest_adapter_selection_takes_precedence(self):
        log = adapter_log() + "LogD3D12RHI: Chosen D3D12 Adapter Id = 0\n"
        with self.assertRaises(SmokeError):
            tutorial.validate_adapter(log, "intel")

    def test_evidence_keeps_actual_choice_when_rhi_header_appears_later(self):
        log = adapter_log() + "LogRHI: RHI Adapter Info:\n"
        evidence = tutorial.validate_adapter(log, "intel")
        self.assertTrue(any("Chosen D3D12 Adapter Id = 1" in line for line in evidence))


class LaunchResolutionTests(unittest.TestCase):
    def test_all_workloads_force_requested_resolution_on_each_adapter(self):
        for adapter in ("intel", "nvidia"):
            for mode in ("csv", "gpu", "timing"):
                with self.subTest(adapter=adapter, mode=mode):
                    arguments = tutorial.launch_arguments(adapter, Path("mock-run"), mode)
                    self.assertIn("-ForceRes", arguments)
                    self.assertIn("-ResX=2560", arguments)
                    self.assertIn("-ResY=1440", arguments)


class TaskReportingTests(unittest.TestCase):
    def test_repeated_task_names_preserve_each_original_evidence_file(self):
        with tempfile.TemporaryDirectory() as directory:
            first = tutorial.Context.__new__(tutorial.Context)
            first.report_dir = Path(directory)
            earlier = Path(first.save("Server preflight", {"pid": 1}))
            resumed = tutorial.Context.__new__(tutorial.Context)
            resumed.report_dir = Path(directory)
            later = Path(resumed.save("Server preflight", {"pid": 2}))
            self.assertNotEqual(earlier, later)
            self.assertEqual({"pid": 1}, json.loads(earlier.read_text(encoding="utf-8")))
            self.assertEqual({"pid": 2}, json.loads(later.read_text(encoding="utf-8")))

    def test_native_status_object_is_data_not_a_harness_status(self):
        ctx = context()
        value = {"status": {"started": True, "selectedAdapter": 123}}
        self.assertEqual(value, ctx.task("native status", lambda: value))
        self.assertEqual("passed", ctx.report["tasks"][0]["status"])

    def test_nested_failure_cannot_leave_parent_passed(self):
        ctx = context()

        def parent():
            ctx.task("child", lambda: (_ for _ in ()).throw(SmokeError("replay failed")))
            return {"handle": "gpu-1"}

        ctx.task("parent", parent)
        self.assertEqual(["failed", "failed"], [row["status"] for row in ctx.report["tasks"]])

    def test_earlier_independent_failure_does_not_contaminate_next_task(self):
        ctx = context()
        ctx.task("failed earlier", lambda: {"status": "failed", "reason": "native error"})
        ctx.task("independent success", lambda: {"count": 3})
        self.assertEqual(["failed", "passed"], [row["status"] for row in ctx.report["tasks"]])

    def test_native_unsupported_result_preserves_coverage(self):
        ctx = context()
        result = {"unavailable": True, "reason": "driver has no occupancy support"}
        self.assertEqual(result, ctx.task("occupancy", lambda: result, optional=True))
        self.assertEqual("unsupported", ctx.report["tasks"][0]["status"])
        self.assertEqual(result["reason"], ctx.report["tasks"][0]["reason"])

    def test_unexpected_optional_failure_remains_failed(self):
        ctx = context()
        ctx.task("optional replay", lambda: (_ for _ in ()).throw(SmokeError("device removed")), optional=True)
        self.assertEqual("failed", ctx.report["tasks"][0]["status"])


class RecordedProtocolTests(unittest.TestCase):
    def send(self, text, structured):
        client = tutorial.RecordedClient.__new__(tutorial.RecordedClient)
        client.transcript = io.StringIO()
        client.last_result_ref = None
        client.image_count = 0
        payload = {"result": {"content": [{"type": "text", "text": json.dumps(text)}],
                              "structuredContent": structured}}
        with patch.object(tutorial.Client, "send", return_value=payload):
            return client.send("tools/call", {"name": "pix_info", "arguments": {}})

    def test_identical_nested_content_is_accepted_independent_of_key_order(self):
        self.send({"a": [1, True], "b": None}, {"b": None, "a": [1, True]})

    def test_different_nested_content_is_rejected(self):
        with self.assertRaisesRegex(SmokeError, "structuredContent"):
            self.send({"items": [{"count": 3}]}, {"items": [{"count": 4}]})

    def test_json_boolean_and_number_are_not_interchangeable(self):
        with self.assertRaisesRegex(SmokeError, "structuredContent"):
            self.send({"items": [{"ready": True}]}, {"items": [{"ready": 1}]})

    def test_image_bytes_are_preserved_in_artifact_and_transcript(self):
        png = b"\x89PNG\r\n\x1a\nfixture"
        payload = {"result": {"content": [{"type": "image", "mimeType": "image/png",
                    "data": base64.b64encode(png).decode("ascii")}]}}
        with tempfile.TemporaryDirectory() as directory:
            client = tutorial.RecordedClient.__new__(tutorial.RecordedClient)
            client.transcript = io.StringIO()
            client.last_result_ref = None
            client.image_count = 0
            client.folder = Path(directory)
            with patch.object(tutorial.Client, "send", return_value=payload):
                client.send("tools/call", {"name": "pix_gpu_preview_image", "arguments": {}})
            images = list(client.folder.glob("*.png"))
            self.assertEqual(1, len(images))
            self.assertEqual(png, images[0].read_bytes())
            self.assertIn(base64.b64encode(png).decode("ascii"), client.transcript.getvalue())


class CsvMeasurementTests(unittest.TestCase):
    def validate_log(self, phase="", ending="", before=""):
        log = (adapter_log() + before + "LogWorld: Bringing World /Big_City up for play\n"
               "LogAutomatedPerfTest: RunTest::Valid Sequence Player, proceeding\n"
               "LogCsvProfiler: CSV capture started\n" + phase
               + "LogAutomatedPerfTest: AutomatedSequencePerfTest::TeardownTest\n"
               "LogCsvProfiler: CSV capture finished\n" + ending)
        return tutorial.validate_csv_log(log, "intel")

    def invoke(self, phase="", ending="", before="", resolution=(2560, 1440), warmup=False):
        ctx = context()
        ctx.detach = Mock()
        ctx.query = Mock(return_value={
            "candidate": {"frames": 120},
            "items": [{"name": "GPU/BasePass", "baselineMs": 1.0, "candidateMs": 1.0,
                       "deltaMs": 0, "baselineSamples": 120, "candidateSamples": 120}],
        })
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory) / ("intel-warmup" if warmup else "intel-run1")
            folder.mkdir()
            (folder / "CSV").mkdir()
            (folder / "CSV" / "flyby.csv").write_text("GPU/BasePass\n1\n", encoding="utf-8")
            resolution_log = "" if resolution is None else (
                f'LogCsvProfiler: Metadata set : resx="{resolution[0]}"\n'
                f'LogCsvProfiler: Metadata set : resy="{resolution[1]}"\n')
            log = (adapter_log() + before + resolution_log + startup_settings_log() + "LogWorld: Bringing World /Big_City up for play\n"
                   "LogAutomatedPerfTest: RunTest::Valid Sequence Player, proceeding\n"
                   'LogCsvProfiler: Metadata set : vsyncenabled="0"\n'
                   "LogCsvProfiler: CSV capture started\n" + phase
                   + "LogAutomatedPerfTest: AutomatedSequencePerfTest::TeardownTest\n"
                   "LogCsvProfiler: CSV capture finished\n" + ending)
            (folder / "Unreal.log").write_text(log, encoding="utf-8")
            with patch.object(tutorial, "launch", return_value=("device-1", 123, folder)), \
                    patch.object(tutorial, "alive", return_value=False):
                value = tutorial.csv_run(ctx, "intel", "intel-run1")
        ctx.detach.assert_called_once_with("device-1")
        return value

    def test_finalized_clean_flyby_is_eligible_for_comparison(self):
        value = self.invoke()
        self.assertTrue(value["validMeasurement"])
        self.assertEqual(120, value["frames"])
        self.assertEqual({"resx": 2560, "resy": 1440}, value["recordedResolution"])

    def test_measured_csv_with_wrong_runtime_resolution_is_rejected(self):
        with self.assertRaisesRegex(SmokeError, "resolution"):
            self.invoke(resolution=(1280, 720))

    def test_measured_csv_without_runtime_resolution_evidence_is_rejected(self):
        with self.assertRaisesRegex(SmokeError, "resolution"):
            self.invoke(resolution=None)

    def test_latest_runtime_resolution_overrides_earlier_correct_metadata(self):
        earlier = 'LogCsvProfiler: Metadata set : resx="2560"\nLogCsvProfiler: Metadata set : resy="1440"\n'
        with self.assertRaisesRegex(SmokeError, "resolution"):
            self.invoke(before=earlier, resolution=(1280, 720))

    def test_final_correct_resolution_overrides_startup_window_size(self):
        earlier = 'LogCsvProfiler: Metadata set : resx="1280"\nLogCsvProfiler: Metadata set : resy="720"\n'
        self.assertTrue(self.invoke(before=earlier)["validMeasurement"])

    def test_warmup_with_wrong_resolution_is_retained_but_excluded(self):
        value = self.invoke(resolution=(1280, 720), warmup=True)
        self.assertFalse(value["validMeasurement"])
        self.assertEqual({"resx": 1280, "resy": 720}, value["recordedResolution"])
        self.assertIn("warmup", value["reason"].lower())

    def test_shader_compilation_during_capture_excludes_measurement(self):
        value = self.validate_log("LogShaderCompilers: Display: Compiling 32 shaders\n")
        self.assertFalse(value["validMeasurement"])
        self.assertTrue(value["compilationOverlap"])

    def test_material_shadermap_compilation_during_capture_excludes_measurement(self):
        value = self.validate_log("LogMaterial: Display: Missing cached shadermap for /Game/Test, compiling.\n")
        self.assertFalse(value["validMeasurement"])

    def test_zero_remaining_shaders_is_not_compile_overlap(self):
        value = self.validate_log("LogShaderCompilers: Display: 0 shaders remaining\n")
        self.assertTrue(value["validMeasurement"])

    def test_pending_shader_work_before_capture_is_still_overlap(self):
        value = self.validate_log(before="LogShaderCompilers: Display: 1,024 shaders remaining\n")
        self.assertFalse(value["validMeasurement"])
        self.assertEqual({"shaders": 1024}, value["pendingCompilationAtStart"])

    def test_completed_work_before_capture_does_not_invalidate_measurement(self):
        for completion in ("0 shaders remaining", "Shader compilation complete"):
            with self.subTest(completion=completion):
                value = self.validate_log(before="LogShaderCompilers: Display: 42 shaders remaining\n"
                                         + "LogShaderCompilers: Display: " + completion + "\n")
                self.assertTrue(value["validMeasurement"])

    def test_one_idle_worker_does_not_hide_pending_work_on_another(self):
        pending = tutorial.pending_compilation("LogShaderCompilers: Verbose: Worker (1/16): shaders left to compile 42\n"
                                               "LogShaderCompilers: Verbose: Worker (2/16): shaders left to compile 0\n")
        self.assertEqual({"shaders (worker 1/16)": 42}, pending)

    def test_fatal_exit_is_rejected_even_if_csv_was_already_written(self):
        with self.assertRaisesRegex(SmokeError, "fatal|Fatal"):
            self.validate_log(ending="LogWindows: Error: Fatal error: device removed\n")


class CleanupTests(unittest.TestCase):
    def test_detach_failure_does_not_prevent_other_owned_cleanup_or_shutdown(self):
        ctx = context()
        ctx.connections = {"device-1", "device-2"}
        ctx.client.close.return_value = 0
        ctx.query = Mock(return_value={"closed": True})

        def detach(handle):
            if handle == "device-1":
                raise SmokeError("detach failed")

        ctx.detach = Mock(side_effect=detach)
        ctx.close()
        self.assertEqual({"device-1", "device-2"}, {call.args[0] for call in ctx.detach.call_args_list})
        ctx.query.assert_any_call("pix_close_all")
        ctx.client.close.assert_called_once()
        failures = [row for row in ctx.report["tasks"] if row["status"] == "failed"]
        self.assertEqual(["Cleanup device-1"], [row["name"] for row in failures])

    def timing_capture(self, start_error=None, in_progress=None, stop_error=None, info_error=None, detach_error=None):
        ctx = context()
        ctx.detach = Mock(side_effect=detach_error)

        def query(tool, **arguments):
            if tool == "pix_device_timing_capture_start":
                if start_error:
                    raise start_error
                return {"started": True}
            if tool == "pix_device_info":
                if info_error:
                    raise info_error
                return {"timingCaptureInProgress": in_progress}
            if tool == "pix_device_timing_capture_stop":
                if stop_error:
                    raise stop_error
                return {"stopped": True}
            raise AssertionError(tool)

        ctx.query = Mock(side_effect=query)
        with patch.object(tutorial, "launch", return_value=("device-1", 123, Path("mock-timing"))), \
                patch.object(tutorial, "wait_scene", return_value={"ready": True}), \
                patch.object(tutorial.time, "sleep"):
            try:
                value = tutorial.capture_timing(ctx)
                return ctx, value, None
            except BaseException as error:
                return ctx, None, error

    def test_uncertain_start_stops_only_a_confirmed_active_recorder(self):
        primary = SmokeError("start transport timeout")
        for recording in (None, "capture.wpix"):
            with self.subTest(recording=recording):
                ctx, _, error = self.timing_capture(start_error=primary, in_progress=recording)
                self.assertIs(primary, error)
                tools = [call.args[0] for call in ctx.query.call_args_list]
                self.assertEqual(bool(recording), "pix_device_timing_capture_stop" in tools)
                ctx.detach.assert_called_once_with("device-1")

    def test_timing_stop_failure_does_not_mask_start_error_or_prevent_detach(self):
        primary = SmokeError("start transport timeout")
        ctx, _, error = self.timing_capture(start_error=primary, in_progress="capture.wpix", stop_error=SmokeError("stop failed"))
        self.assertIs(primary, error)
        ctx.detach.assert_called_once_with("device-1")
        self.assertEqual("stop timing recorder", ctx.report["cleanupErrors"][0]["operation"])

    def test_uncertain_state_query_failure_still_detaches_and_preserves_primary_error(self):
        primary = SmokeError("start transport timeout")
        ctx, _, error = self.timing_capture(start_error=primary, info_error=SmokeError("server busy"))
        self.assertIs(primary, error)
        ctx.detach.assert_called_once_with("device-1")
        self.assertTrue(ctx.report["cleanupErrors"])

    def test_detach_failure_after_successful_recording_fails_the_task(self):
        ctx, _, error = self.timing_capture(detach_error=SmokeError("detach failed"))
        self.assertIsInstance(error, SmokeError)
        self.assertIn("Timing cleanup failed", str(error))
        self.assertEqual("detach timing workload", ctx.report["cleanupErrors"][0]["operation"])


class CsvSettingsEvidenceTests(unittest.TestCase):
    def validate(self, startup=None, phase='', requested=100, metadata='0', after=''):
        startup = startup_settings_log(requested) if startup is None else startup
        metadata_line = '' if metadata is None else f'LogCsvProfiler: Display: Metadata set : vsyncenabled="{metadata}"\n'
        log = (startup + 'LogAutomatedPerfTest: RunTest::Valid Sequence Player\n'
               + metadata_line + phase + 'LogAutomatedPerfTest: AutomatedSequencePerfTest::TeardownTest\n' + after)
        return tutorial.csv_settings_evidence(log, requested)

    def test_requested_screen_percentage_is_verified_from_startup_echo(self):
        for screen in (100, 50):
            with self.subTest(screen=screen):
                value = self.validate(requested=screen)
                self.assertTrue(value['verified'])
                self.assertEqual(screen, value['startupValues']['r.ScreenPercentage'])
                self.assertEqual(0, value['csvVsyncEnabled'])
                self.assertIn('do not verify', value['scope'])

    def test_command_line_and_config_requests_cannot_replace_direct_echoes(self):
        startup = 'LogInit: Command Line: -ExecCmds="r.ScreenPercentage 100"\nLogConfig: Set CVar [[r.ScreenPercentage:100]]\n'
        value = self.validate(startup=startup)
        self.assertFalse(value['verified'])
        self.assertIsNone(value['startupValues']['r.ScreenPercentage'])

    def test_latest_pre_capture_override_is_rejected(self):
        value = self.validate(startup=startup_settings_log() + 'r.ScreenPercentage = "50"\n')
        self.assertFalse(value['verified'])
        self.assertEqual(50, value['startupValues']['r.ScreenPercentage'])

    def test_post_capture_echo_cannot_repair_wrong_startup_setting(self):
        value = self.validate(startup=startup_settings_log(50), after='r.ScreenPercentage = "100"\n')
        self.assertFalse(value['verified'])

    def test_csv_vsync_metadata_must_confirm_disabled(self):
        for metadata in ('1', None):
            with self.subTest(metadata=metadata):
                self.assertFalse(self.validate(metadata=metadata)['verified'])


class StartupReadinessTests(unittest.TestCase):
    def wait(self, tail):
        ctx = context()
        ctx.args.startup_seconds = 70
        now = [100.0]

        def sleep(seconds):
            now[0] += seconds

        log = adapter_log() + "LogWorld: Bringing World /Big_City up for play\n" + tail
        with patch.object(tutorial, "alive", return_value=True), \
                patch.object(tutorial, "read_log", return_value=log), \
                patch.object(tutorial.time, "monotonic", side_effect=lambda: now[0]), \
                patch.object(tutorial.time, "sleep", side_effect=sleep):
            return tutorial.wait_scene(ctx, 123, Path("mock-run"), "intel")

    def test_quiet_playable_scene_becomes_ready_after_warmup(self):
        result = self.wait("LogShaderCompilers: Display: 0 shaders remaining\n")
        self.assertIn("B580", json.dumps(result["adapterEvidence"]))
        self.assertGreaterEqual(result["quietSeconds"], 60)

    def test_silent_but_pending_shader_work_is_not_ready(self):
        with self.assertRaises(SmokeError):
            self.wait("LogShaderCompilers: Display: 42 shaders remaining\n")


class ResumeTests(unittest.TestCase):
    def test_finalized_warmup_and_run_are_recovered_without_launch(self):
        ctx = context()
        ctx.args.adapter = "nvidia"
        with tempfile.TemporaryDirectory() as directory:
            ctx.artifacts = Path(directory)
            for name in ("nvidia-warmup", "nvidia-run1-attempt1"):
                (ctx.artifacts / name).mkdir()
            with patch.object(tutorial, "csv_result", side_effect=lambda context, adapter, folder: {
                    "path": str(folder / "CSV" / "flyby.csv"), "validMeasurement": True}), \
                    patch.object(tutorial, "csv_run") as launch, \
                    patch.object(tutorial, "compare_csv"):
                tutorial.run_csv(ctx)
            launch.assert_not_called()
            self.assertEqual([1], [run["run"] for run in ctx.report["csvRuns"]["nvidia"]])
            self.assertTrue(all(row["status"] == "passed" for row in ctx.report["tasks"]))


if __name__ == "__main__":
    unittest.main()
