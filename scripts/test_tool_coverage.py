"""Tool registry, coverage gate and documentation check tests over a synthetic repository tree."""
import tempfile
import unittest
from pathlib import Path

import check_docs
import tool_coverage
from tool_registry import readme_catalog, registered_tools

TOOLS_CS = '''namespace PixMcp.Tools;

public static class FooTools
{
    [McpServerTool(Name = "pix_foo_open", Title = "Open", ReadOnly = true),
     Description("Opens.")]
    public static async Task<string> Open(PixSession session, string path, CancellationToken cancellationToken = default)
        => await Task.FromResult(path);

    [McpServerTool(Name = "pix_foo_list"), Description("Lists.")]
    public static Task<string> List(PixSession session) => Task.FromResult("");
}
'''
BAR_CS = '''public static partial class BarTools
{
    [McpServerTool(Name = "pix_bar_run")]
    public static Task<IReadOnlyList<string>> Run() => null!;

    [McpServerTool(Name = "pix_bar_unused")]
    public static string Unused() => "";
}
'''


class Repository:
    def __init__(self):
        self.directory = tempfile.TemporaryDirectory()
        self.root = Path(self.directory.name)
        self.write("src/PixMcp/Tools/FooTools.cs", TOOLS_CS)
        self.write("src/PixMcp/Tools/BarTools.cs", BAR_CS)
        self.write("tests/PixMcp.Tests/FooTests.cs", "var x = await FooTools.Open(session, path);\n")
        self.write("tests/PixMcp.Tests/StdioTests.cs", 'Call("pix_foo_list", new { });\n')
        self.write("scripts/scenarios/bar.json", '[["pix_bar_run", {}]]\n')
        self.write("README.md", "# x\n\n## Tool catalog\n\n| A | `pix_foo_open`, `pix_foo_list` |\n| B | `pix_bar_run`, `pix_bar_unused` |\n\n## Next\n")

    def write(self, relative, text):
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
        return path


class ToolCoverageTests(unittest.TestCase):
    def setUp(self):
        self.repo = Repository()
        self.addCleanup(self.repo.directory.cleanup)

    def test_registry_pairs_attributes_with_methods_and_classes(self):
        tools = registered_tools(self.repo.root)
        self.assertEqual(["pix_bar_run", "pix_bar_unused", "pix_foo_list", "pix_foo_open"], sorted(tools))
        self.assertEqual(("FooTools", "Open"), (tools["pix_foo_open"].class_name, tools["pix_foo_open"].method))
        self.assertEqual(("BarTools", "Run"), (tools["pix_bar_run"].class_name, tools["pix_bar_run"].method))
        self.assertEqual(["pix_foo_open", "pix_foo_list", "pix_bar_run", "pix_bar_unused"], readme_catalog(self.repo.root))

    def test_method_calls_stdio_names_and_scenarios_count_as_coverage(self):
        result = tool_coverage.evaluate(self.repo.root)
        self.assertEqual(["pix_bar_unused"], result["uncovered"])
        self.assertEqual(["tests/PixMcp.Tests/FooTests.cs"], result["references"]["pix_foo_open"])
        self.assertEqual(["tests/PixMcp.Tests/StdioTests.cs"], result["references"]["pix_foo_list"])
        self.assertEqual(["scripts/scenarios/bar.json"], result["references"]["pix_bar_run"])
        self.assertEqual(1, tool_coverage.main(["--root", str(self.repo.root), "--fail-on-uncovered"]))

    def test_allowlist_needs_reasons_and_fails_on_stale_entries(self):
        self.repo.write("scripts/tool-coverage-allowlist.txt", "# comment\npix_bar_unused  # needs a device\n")
        self.assertEqual(0, tool_coverage.main(["--root", str(self.repo.root), "--fail-on-uncovered"]))
        self.repo.write("scripts/tool-coverage-allowlist.txt", "pix_bar_unused  # needs a device\npix_foo_open  # covered now\npix_gone  # removed\n")
        result = tool_coverage.evaluate(self.repo.root)
        self.assertEqual(["pix_foo_open", "pix_gone"], result["staleAllowlist"])
        self.assertEqual(1, tool_coverage.main(["--root", str(self.repo.root), "--fail-on-uncovered"]))
        self.repo.write("scripts/tool-coverage-allowlist.txt", "pix_bar_unused\n")
        with self.assertRaisesRegex(ValueError, "reason"):
            tool_coverage.evaluate(self.repo.root)

    def test_docs_check_reports_catalog_and_reference_drift(self):
        self.assertEqual([], check_docs.problems(self.repo.root))
        self.repo.write("README.md", "## Tool catalog\n\n| A | `pix_foo_open`, `pix_foo_open`, `pix_old` |\n")
        found = check_docs.problems(self.repo.root)
        self.assertIn("README catalog lists pix_foo_open 2 times", found)
        self.assertIn("README catalog is missing pix_bar_run", found)
        self.assertIn("README catalog names unregistered pix_old", found)
        self.repo.write("README.md", "## Tool catalog\n\n`pix_foo_open` `pix_foo_list` `pix_bar_run` `pix_bar_unused`\n")
        self.repo.write("docs/tools.md", "# Tools\n\n### pix_foo_open\n\n### pix_foo_list\n\n### pix_bar_run\n")
        self.assertEqual(["docs/tools.md is missing pix_bar_unused"], check_docs.problems(self.repo.root))
        self.assertEqual(1, check_docs.main(["--root", str(self.repo.root)]))

    def test_real_repository_registry_matches_the_pinned_tool_list(self):
        pinned = Path(__file__).resolve().parent.parent / "tests" / "PixMcp.Tests" / "Fixtures" / "tool-names.txt"
        if not pinned.is_file():
            self.skipTest("pinned tool list not present")
        self.assertEqual([line for line in pinned.read_text(encoding="utf-8").splitlines() if line], sorted(registered_tools()))


if __name__ == "__main__":
    unittest.main()
