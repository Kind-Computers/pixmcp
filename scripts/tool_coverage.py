"""Report which registered MCP tools no test, scenario or script exercises.

A tool counts as covered when a C# test calls its method (`GpuCaptureTools.Open(`), StdioTests names it (protocol-level
calls), or a Python script or smoke scenario names it in a quoted string. Uncovered tools must be listed with a reason in
scripts/tool-coverage-allowlist.txt ("pix_name  # reason"); --fail-on-uncovered exits 1 on an unlisted uncovered tool and on
stale entries (a listed tool that is covered or no longer registered).

    python scripts/tool_coverage.py --fail-on-uncovered
"""
import argparse
import json
from pathlib import Path
import re
import sys

from tool_registry import ROOT, registered_tools

HARNESS_FILES = {"tool_registry.py", "tool_coverage.py", "test_tool_coverage.py", "check_docs.py", "test_check_docs.py"}


def references(root, tools):
    """Tool name -> sorted list of files that exercise it."""
    root = Path(root)
    found = {name: set() for name in tools}
    for path in sorted((root / "tests").rglob("*.cs")):
        if "bin" in path.parts or "obj" in path.parts:
            continue
        text = path.read_text(encoding="utf-8-sig")
        relative = path.relative_to(root).as_posix()
        for name, tool in tools.items():
            if re.search(rf"\b{re.escape(tool.class_name)}\.{re.escape(tool.method)}\s*\(", text):
                found[name].add(relative)
            elif path.name == "StdioTests.cs" and f'"{name}"' in text:
                found[name].add(relative)
    scripts = [path for path in (root / "scripts").rglob("*.py") if path.name not in HARNESS_FILES]
    scripts += list((root / "scripts" / "scenarios").glob("*.json"))
    for path in sorted(scripts):
        text = path.read_text(encoding="utf-8-sig")
        relative = path.relative_to(root).as_posix()
        for name in tools:
            if f'"{name}"' in text or f"'{name}'" in text:
                found[name].add(relative)
    return {name: sorted(files) for name, files in found.items()}


def read_allowlist(path):
    """Tool name -> reason; blank lines and full-line comments are ignored."""
    entries = {}
    if not Path(path).is_file():
        return entries
    for number, line in enumerate(Path(path).read_text(encoding="utf-8").splitlines(), 1):
        body = line.strip()
        if not body or body.startswith("#"):
            continue
        name, _, reason = body.partition("#")
        name, reason = name.strip(), reason.strip()
        if not reason:
            raise ValueError(f"{path}:{number}: {name} needs a '# reason'")
        entries[name] = reason
    return entries


def evaluate(root=ROOT, allowlist_path=None):
    tools = registered_tools(root)
    refs = references(root, tools)
    allowlist = read_allowlist(allowlist_path or Path(root) / "scripts" / "tool-coverage-allowlist.txt")
    uncovered = sorted(name for name, files in refs.items() if not files)
    return {
        "registered": len(tools),
        "covered": len(tools) - len(uncovered),
        "uncovered": uncovered,
        "unlisted": [name for name in uncovered if name not in allowlist],
        "staleAllowlist": sorted(name for name in allowlist if name not in tools or refs.get(name)),
        "allowlisted": {name: allowlist[name] for name in uncovered if name in allowlist},
        "references": refs,
    }


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--root", default=str(ROOT), help="Repository root.")
    parser.add_argument("--allowlist", help="Allowlist file (default scripts/tool-coverage-allowlist.txt).")
    parser.add_argument("--fail-on-uncovered", action="store_true", help="Exit 1 on unlisted uncovered tools or stale allowlist entries.")
    parser.add_argument("--json", action="store_true", help="Print the full result, including references per tool.")
    args = parser.parse_args(argv)
    result = evaluate(args.root, args.allowlist)
    if args.json:
        print(json.dumps(result, indent=1))
    else:
        print(f"tool_coverage: {result['covered']}/{result['registered']} tools exercised, {len(result['allowlisted'])} allowlisted")
        for name in result["unlisted"]:
            print(f"tool_coverage: uncovered and not allowlisted: {name}")
        for name in result["staleAllowlist"]:
            print(f"tool_coverage: stale allowlist entry (covered or unregistered): {name}")
    return 1 if args.fail_on_uncovered and (result["unlisted"] or result["staleAllowlist"]) else 0


if __name__ == "__main__":
    sys.exit(main())
