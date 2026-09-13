"""Keeps the PIX version strings in sync.

Directory.Build.props is the single source of PixPreviewMinDate and PixVerifiedVersion. This check fails when
README.md or CLAUDE.md do not repeat both strings verbatim, or when any tracked source or documentation file
outside CHANGELOG.md, the tests and the scripts' own unit tests names a different ``YYMM.DD-preview`` build (a stale example, a copied
path, a forgotten note). Run with ``python scripts/check_versions.py [repo-root]``; exit code 1 on problems.
"""
import re
import sys
from pathlib import Path

PROPS = "Directory.Build.props"
DOCS_THAT_MUST_NAME_BOTH = ("README.md", "CLAUDE.md")
SCANNED_GLOBS = (
    "README.md", "CLAUDE.md", "docs/**/*.md", "src/**/*.cs", "src/**/*.json", "src/**/*.csproj",
    "scripts/*.py", "scripts/*.cmd", "*.props", ".github/**/*.yml", ".github/**/*.md", ".mcp.json",
)
EXCLUDED_PARTS = {"bin", "obj", "artifacts", "node_modules", ".git"}
PREVIEW_TOKEN = re.compile(r"\b(\d{4}\.\d{1,2}(?:\.\d+)?-preview)\b")


def read_props(root):
    """Returns (min_date, verified_version) from Directory.Build.props or raises ValueError."""
    text = (Path(root) / PROPS).read_text(encoding="utf-8")
    values = {}
    for name in ("PixPreviewMinDate", "PixVerifiedVersion"):
        match = re.search(rf"<{name}>\s*([^<\s]+)\s*</{name}>", text)
        if not match:
            raise ValueError(f"{PROPS} does not define <{name}>")
        values[name] = match.group(1)
    return values["PixPreviewMinDate"], values["PixVerifiedVersion"]


def scanned_files(root):
    root = Path(root)
    seen = set()
    for pattern in SCANNED_GLOBS:
        for path in sorted(root.glob(pattern)):
            if not path.is_file() or path in seen or path.name.startswith("test_"):
                continue  # unit tests carry deliberately foreign version strings
            if EXCLUDED_PARTS.intersection(path.relative_to(root).parts):
                continue
            seen.add(path)
            yield path


def check(root):
    """Returns a list of problem strings (empty when everything is consistent)."""
    root = Path(root)
    problems = []
    try:
        min_date, verified = read_props(root)
    except (OSError, ValueError) as error:
        return [str(error)]
    for name in DOCS_THAT_MUST_NAME_BOTH:
        path = root / name
        try:
            text = path.read_text(encoding="utf-8")
        except OSError:
            problems.append(f"{name}: missing")
            continue
        for expected in (min_date, verified):
            if expected not in text:
                problems.append(f"{name}: does not mention {expected} (from {PROPS})")
    for path in scanned_files(root):
        try:
            text = path.read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue
        for line_number, line in enumerate(text.splitlines(), start=1):
            for token in PREVIEW_TOKEN.findall(line):
                if token != verified:
                    relative = path.relative_to(root).as_posix()
                    problems.append(f"{relative}:{line_number}: names PIX {token}; the verified build is {verified} ({PROPS})")
    return problems


def main(argv=None):
    argv = sys.argv[1:] if argv is None else list(argv)
    root = Path(argv[0]) if argv else Path(__file__).resolve().parent.parent
    problems = check(root)
    for problem in problems:
        print(problem)
    if problems:
        print(f"check_versions: {len(problems)} problem(s)")
        return 1
    min_date, verified = read_props(root)
    print(f"check_versions: ok (PIX Preview newer than {min_date}, verified on {verified})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
