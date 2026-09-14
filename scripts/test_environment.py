"""Environment recorder tests with a fake repository, PIX install and command runner."""
import hashlib
import json
import tempfile
import unittest
from pathlib import Path

import environment


def fake_runner(outputs):
    def runner(command, cwd=None, timeout=30):
        key = command[0] if command[0] != "git" else " ".join(command[:2])
        value = outputs.get(key)
        if isinstance(value, Exception):
            raise value
        return value
    return runner


class EnvironmentTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name) / "repo"
        (self.root / "tests" / "artifacts").mkdir(parents=True)
        (self.root / "Directory.Build.props").write_text(
            "<Project><PropertyGroup><Version>2.0.0</Version><PixPreviewMinDate>2606.15</PixPreviewMinDate>"
            "<PixVerifiedVersion>2606.18-preview</PixVerifiedVersion></PropertyGroup></Project>", encoding="utf-8")
        (self.root / "tests" / "artifacts" / "baseline.wpix").write_bytes(b"capture")
        self.pix_root = Path(self.directory.name) / "PIX"
        install = self.pix_root / "2606.18-preview"
        install.mkdir(parents=True)
        (install / "version.xml").write_text("<VersionInfo><Version>2606.18-preview</Version><Build>WinPIX_release_2606.18001</Build>"
                                             "<Commit>abc123</Commit></VersionInfo>", encoding="utf-8")

    def collect(self, outputs, environ=None):
        return environment.collect(self.root, environ or {}, fake_runner(outputs), self.pix_root)

    def test_collects_versions_adapters_captures_and_options(self):
        adapters = json.dumps([{"Name": "NVIDIA GeForce RTX 4090", "DriverVersion": "32.0.15.7688", "AdapterCompatibility": "NVIDIA"},
                               {"Name": "Microsoft Basic Display Adapter", "DriverVersion": "10.0", "AdapterCompatibility": "Microsoft"}])
        result = self.collect({"git rev-parse": "0123abcd\n", "git status": " M README.md\n", "powershell": adapters},
                              {"PIXMCP_TEXT_CONTENT": "summary", "PIX_TEST_CAPTURE": "tests/artifacts/baseline.wpix"})
        self.assertEqual({"commit": "0123abcd", "dirty": True, "error": None}, result["git"])
        self.assertEqual(("2.0.0", "2606.18-preview"), (result["server"]["Version"], result["server"]["PixVerifiedVersion"]))
        self.assertEqual(("2606.18-preview", "WinPIX_release_2606.18001", "abc123"),
                         (result["pix"]["version"], result["pix"]["build"], result["pix"]["commit"]))
        self.assertEqual(["NVIDIA GeForce RTX 4090", "Microsoft Basic Display Adapter"], [a["name"] for a in result["adapters"]["items"]])
        self.assertEqual(hashlib.sha256(b"capture").hexdigest(), result["captures"]["baseline"]["sha256"])
        self.assertIsNone(result["captures"]["candidate"])
        self.assertEqual(result["captures"]["baseline"]["sha256"], result["captures"]["PIX_TEST_CAPTURE"]["sha256"])
        self.assertEqual("summary", result["options"]["PIXMCP_TEXT_CONTENT"])

    def test_failed_probes_degrade_to_errors(self):
        result = self.collect({"git rev-parse": RuntimeError("not a git repository"), "git status": RuntimeError("no"),
                               "powershell": OSError("no powershell")}, {"PIX_DIR": str(Path(self.directory.name) / "missing")})
        self.assertIsNone(result["git"]["commit"])
        self.assertIn("not a git repository", result["git"]["error"])
        self.assertIsNone(result["adapters"]["items"])
        self.assertIn("version.xml not found", result["pix"]["error"])

    def test_single_adapter_json_object_is_a_list_of_one(self):
        result = self.collect({"git rev-parse": "x", "git status": "", "powershell": json.dumps({"Name": "Intel Arc B580"})})
        self.assertEqual(["Intel Arc B580"], [a["name"] for a in result["adapters"]["items"]])
        self.assertFalse(result["git"]["dirty"])


if __name__ == "__main__":
    unittest.main()
