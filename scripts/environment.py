"""Record the environment a validation run used: commit, server and PIX versions, GPU adapters, capture hashes, options.

The self-hosted CI job writes this next to the test results and benchmark report so numbers can be compared across
machines and PIX builds. Every probe degrades to null with an error string instead of failing.

    python scripts/environment.py --out tests/artifacts/environment.json
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import xml.etree.ElementTree as ElementTree

ROOT = Path(__file__).resolve().parent.parent
CAPTURES = {"baseline": "tests/artifacts/baseline.wpix", "candidate": "tests/artifacts/candidate.wpix",
            "timing": "tests/artifacts/timing.wpix", "timingValidation": "tests/artifacts/timing-validation/timing.wpix"}
CAPTURE_VARIABLES = ("PIX_TEST_CAPTURE", "PIX_TEST_TIMING_CAPTURE", "PIX_TEST_RICH_CAPTURE", "PIX_TEST_RICH_TIMING_CAPTURE",
                     "PIX_TEST_PERF_BASELINE", "PIX_TEST_PERF_CANDIDATE", "PIX_TEST_SM6_CAPTURE", "PIX_TEST_PROGRAMMATIC_CAPTURE",
                     "PIX_TEST_INTEL_CAPTURE", "PIX_TEST_INTEL_PERF_BASELINE", "PIX_TEST_AMD_CAPTURE", "PIX_TEST_AMD_PERF_BASELINE")
SERVER_VARIABLES = ("PIXMCP_TEXT_CONTENT", "PIXMCP_INLINE_RESULT_BYTES", "PIXMCP_MAX_RESULT_BYTES", "PIXMCP_TOOLSETS", "PIX_TEST_ANALYSIS", "PIX_TEST_VENDOR_VALIDATION")
PIX_ROOT = Path(r"C:\Program Files\Microsoft PIX Preview")


def run(command, cwd=None, timeout=30):
    result = subprocess.run(command, cwd=cwd, capture_output=True, text=True, timeout=timeout)
    if result.returncode != 0:
        raise RuntimeError((result.stderr or result.stdout).strip() or f"exit code {result.returncode}")
    return result.stdout


def probe(work):
    try:
        return work(), None
    except Exception as error:  # every probe is best effort
        return None, f"{type(error).__name__}: {error}"


def git(root, runner):
    commit, error = probe(lambda: runner(["git", "rev-parse", "HEAD"], cwd=root).strip())
    status, _ = probe(lambda: runner(["git", "status", "--porcelain", "--untracked-files=no"], cwd=root))
    return {"commit": commit, "dirty": bool(status.strip()) if status is not None else None, "error": error}


def properties(root):
    text = (Path(root) / "Directory.Build.props").read_text(encoding="utf-8-sig")
    return {name: (match.group(1).strip() if (match := re.search(rf"<{name}>([^<]*)</{name}>", text)) else None)
            for name in ("Version", "PixPreviewMinDate", "PixVerifiedVersion")}


def pix_install(environ, pix_root=PIX_ROOT):
    directory = environ.get("PIX_DIR")
    if not directory:
        candidates = sorted(path for path in Path(pix_root).glob("*") if (path / "version.xml").is_file()) if Path(pix_root).is_dir() else []
        directory = str(candidates[-1]) if candidates else None
    if directory is None:
        return {"directory": None, "error": "No PIX Preview install found (set PIX_DIR)."}
    install = {"directory": directory}
    version_file = Path(directory) / "version.xml"
    if version_file.is_file():
        document = ElementTree.parse(version_file).getroot()
        for element in ("Version", "Build", "Commit"):
            found = document if document.tag == element else document.find(f".//{element}")
            install[element.lower()] = found.text.strip() if found is not None and found.text else None
    else:
        install["error"] = "version.xml not found"
    return install


def adapters(runner):
    command = ["powershell", "-NoProfile", "-Command",
               "Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion, AdapterCompatibility | ConvertTo-Json -Compress"]
    output, error = probe(lambda: runner(command))
    if error:
        return {"items": None, "error": error}
    value = json.loads(output) if output.strip() else []
    items = value if isinstance(value, list) else [value]
    return {"items": [{"name": item.get("Name"), "driverVersion": item.get("DriverVersion"), "vendor": item.get("AdapterCompatibility")} for item in items]}


def file_record(path):
    path = Path(path)
    if not path.is_file():
        return None
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for block in iter(lambda: stream.read(1 << 20), b""):
            digest.update(block)
    return {"path": str(path), "bytes": path.stat().st_size, "sha256": digest.hexdigest()}


def collect(root=ROOT, environ=None, runner=run, pix_root=PIX_ROOT):
    environ = os.environ if environ is None else environ
    root = Path(root)
    props, props_error = probe(lambda: properties(root))
    pix, pix_error = probe(lambda: pix_install(environ, pix_root))
    captures = {name: file_record(root / relative) for name, relative in CAPTURES.items()}
    for variable in CAPTURE_VARIABLES:
        if environ.get(variable):
            value = Path(environ[variable])
            captures[variable] = file_record(value if value.is_absolute() else root / value)
    return {
        "git": git(root, runner),
        "server": {**(props or {}), "error": props_error},
        "pix": pix if pix is not None else {"error": pix_error},
        "adapters": adapters(runner),
        "python": sys.version.split()[0],
        "captures": captures,
        "options": {variable: environ.get(variable) for variable in SERVER_VARIABLES},
    }


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--out", help="Write the JSON here instead of printing it.")
    args = parser.parse_args(argv)
    text = json.dumps(collect(), indent=2)
    if args.out:
        Path(args.out).parent.mkdir(parents=True, exist_ok=True)
        Path(args.out).write_text(text + "\n", encoding="utf-8")
        print(f"environment: wrote {args.out}")
    else:
        print(text)
    return 0


if __name__ == "__main__":
    sys.exit(main())
