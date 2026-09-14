"""Check that the documentation names exactly the registered MCP tools.

The README '## Tool catalog' section must list every tool once, and docs/tools.md (the generated reference, when present)
must have one '### pix_name' heading per tool. Exits 1 and names the drift otherwise.

    python scripts/check_docs.py
"""
import argparse
from collections import Counter
from pathlib import Path
import re
import sys

from tool_registry import ROOT, readme_catalog, registered_tools


def problems(root=ROOT):
    registered = set(registered_tools(root))
    found = []
    catalog = readme_catalog(root)
    if catalog is None:
        found.append("README.md has no '## Tool catalog' section")
    else:
        found += [f"README catalog lists {name} {count} times" for name, count in sorted(Counter(catalog).items()) if count > 1]
        found += [f"README catalog is missing {name}" for name in sorted(registered - set(catalog))]
        found += [f"README catalog names unregistered {name}" for name in sorted(set(catalog) - registered)]
    reference = Path(root) / "docs" / "tools.md"
    if reference.is_file():
        headings = re.findall(r"^### (pix_[a-z0-9_]+)\s*$", reference.read_text(encoding="utf-8-sig"), re.MULTILINE)
        found += [f"docs/tools.md has {name} {count} times" for name, count in sorted(Counter(headings).items()) if count > 1]
        found += [f"docs/tools.md is missing {name}" for name in sorted(registered - set(headings))]
        found += [f"docs/tools.md documents unregistered {name}" for name in sorted(set(headings) - registered)]
    return found


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--root", default=str(ROOT), help="Repository root.")
    args = parser.parse_args(argv)
    found = problems(args.root)
    for problem in found:
        print("check_docs:", problem)
    print(f"check_docs: {'drift' if found else 'ok'} ({len(registered_tools(args.root))} tools)")
    return 1 if found else 0


if __name__ == "__main__":
    sys.exit(main())
