"""Run with python -m unittest discover -s scripts -p test_check_versions.py."""
import io
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import check_versions


PROPS = """<Project>
  <PropertyGroup>
    <PixPreviewMinDate>2606.15</PixPreviewMinDate>
    <PixVerifiedVersion>2606.18-preview</PixVerifiedVersion>
  </PropertyGroup>
</Project>
"""


class CheckVersionsTests(unittest.TestCase):
    def setUp(self):
        self.root = Path(tempfile.mkdtemp(prefix="pixmcp-versions-"))
        self.write("Directory.Build.props", PROPS)
        self.write("README.md", "PIX Preview newer than 2606.15, verified on 2606.18-preview.\n")
        self.write("CLAUDE.md", "PIX Preview newer than 2606.15, verified on 2606.18-preview.\n")
        self.write("CHANGELOG.md", "## [1.0.0]\n- Verified on 2606.11-preview back then.\n")

    def write(self, relative, text):
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")

    def test_consistent_tree_passes(self):
        self.write("src/PixMcp/Pix/PixDiscovery.cs", 'VerifiedVersion = "2606.18-preview";\n')
        self.assertEqual([], check_versions.check(self.root))
        self.assertEqual(0, check_versions.main([str(self.root)]))

    def test_docs_must_repeat_both_strings(self):
        self.write("README.md", "Verified on 2606.18-preview only.\n")
        self.write("CLAUDE.md", "Needs 2606.15 or newer.\n")
        problems = check_versions.check(self.root)
        self.assertIn("README.md: does not mention 2606.15 (from Directory.Build.props)", problems)
        self.assertIn("CLAUDE.md: does not mention 2606.18-preview (from Directory.Build.props)", problems)

    def test_other_preview_tokens_outside_changelog_and_tests_fail(self):
        self.write("src/PixMcp/Tools/SessionTools.cs", '// tested on 2607.01-preview\n')
        self.write("tests/PixMcp.Tests/RuntimeDiscoveryTests.cs", '"2607.02-preview"\n')
        self.write("tests/artifacts/notes.md", "2607.03-preview\n")
        problems = check_versions.check(self.root)
        self.assertEqual(["src/PixMcp/Tools/SessionTools.cs:1: names PIX 2607.01-preview; the verified build is 2606.18-preview (Directory.Build.props)"], problems)

    def test_missing_props_is_one_problem(self):
        (self.root / "Directory.Build.props").unlink()
        problems = check_versions.check(self.root)
        self.assertEqual(1, len(problems))
        with patch("sys.stdout", new=io.StringIO()) as out:
            self.assertEqual(1, check_versions.main([str(self.root)]))
        self.assertIn("1 problem(s)", out.getvalue())


if __name__ == "__main__":
    unittest.main()
