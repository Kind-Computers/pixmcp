"""Dependency-free JSON-RPC stdio smoke client. Run from the repository root.

Usage: python scripts/smoke.py [--max=6000] [--timeout=660] <server exe> <tool> [json-args] ...
       python scripts/smoke.py <server exe> @scripts/scenarios/open-capture.json
 - 'tools' lists tools; 'sleep' with {"seconds": N} pauses.
 - '$tool.result.path', '$last.items.0.index', and '$env.NAME' resolve earlier results/environment.
 - Scenario steps are [tool, args] or [tool, args, {"result.path": expected_json_value}].
   Assertions use dotted paths into that step's result, for example {"status": "succeeded"}.
 - RPC, tool, failed/cancelled jobs, missing references, and assertion errors exit nonzero.
 - --validate-scenarios DIR checks every scenario (shape, registered tool names, step references) without a server.
 - --all DIR runs every scenario on a fresh server, except manual ones (provoke-hang); --skip-missing-env skips
   scenarios whose $env references are unset, naming the variables.
"""
import argparse
import base64
import binascii
from collections import deque
import json
import math
import os
import queue
import subprocess
import sys
import threading
import time


class SmokeError(RuntimeError):
    pass


class Client:
    def __init__(self, cmd, timeout=660, shutdown_timeout=5):
        if not math.isfinite(timeout) or timeout <= 0:
            raise ValueError("timeout must be a finite positive number")
        self.timeout = timeout
        self.shutdown_timeout = shutdown_timeout
        self.proc = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                     stderr=subprocess.PIPE, text=True, encoding="utf-8", bufsize=1)
        self.next_id = 1
        self.last_response_bytes = 0
        self.stderr_lines = deque(maxlen=200)
        self._stdout = queue.Queue()
        self._closed = False
        self._threads = [threading.Thread(target=self._pump_stdout, daemon=True),
                         threading.Thread(target=self._pump_stderr, daemon=True)]
        for thread in self._threads:
            thread.start()

    def _pump_stdout(self):
        try:
            for line in self.proc.stdout:
                self._stdout.put(line)
        except (OSError, UnicodeError, ValueError) as error:
            self._stdout.put(error)
        finally:
            self._stdout.put(None)

    def _pump_stderr(self):
        try:
            for line in self.proc.stderr:
                self.stderr_lines.append(line.rstrip())
        except (OSError, UnicodeError, ValueError) as error:
            self.stderr_lines.append(str(error))

    def send(self, method, params=None, notify=False):
        msg = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            msg["params"] = params
        if not notify:
            msg["id"] = self.next_id
            self.next_id += 1
        deadline = time.monotonic() + self.timeout
        try:
            self.proc.stdin.write(json.dumps(msg) + "\n")
            self.proc.stdin.flush()
        except (OSError, ValueError) as error:
            raise SmokeError(f"{method}: cannot write to server: {error}") from error
        if notify:
            return None
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise SmokeError(f"{method}: response timed out after {self.timeout:g}s")
            try:
                line = self._stdout.get(timeout=remaining)
            except queue.Empty as error:
                raise SmokeError(f"{method}: response timed out after {self.timeout:g}s") from error
            if line is None:
                raise SmokeError(f"{method}: server exited before responding")
            if isinstance(line, Exception):
                raise SmokeError(f"{method}: cannot read server output: {line}") from line
            try:
                data = json.loads(line)
            except json.JSONDecodeError as error:
                raise SmokeError(f"{method}: malformed JSON on stdout: {line[:300].rstrip()}") from error
            if not isinstance(data, dict) or data.get("jsonrpc") != "2.0":
                raise SmokeError(f"{method}: invalid JSON-RPC response: {data}")
            if data.get("id") == msg["id"]:
                if "error" in data:
                    raise SmokeError(f"{method}: RPC error: {json.dumps(data['error'])}")
                if "result" not in data:
                    raise SmokeError(f"{method}: response has neither result nor error")
                self.last_response_bytes = len(line.encode("utf-8"))
                return data
            print("<<", json.dumps(data)[:300], file=sys.stderr)

    def call(self, tool, args=None):
        result = self.send("tools/call", {"name": tool, "arguments": args or {}})["result"]
        if not isinstance(result, dict) or not isinstance(result.get("content"), list):
            raise SmokeError(f"{tool}: invalid tool result: {result}")
        out = []
        for content in result["content"]:
            if not isinstance(content, dict):
                raise SmokeError(f"{tool}: invalid content block: {content}")
            if content.get("type") == "text":
                if not isinstance(content.get("text"), str):
                    raise SmokeError(f"{tool}: invalid text content: {content}")
                try:
                    out.append(json.loads(content["text"]))
                except json.JSONDecodeError:
                    out.append(content["text"])
            elif content.get("type") == "image":
                data = content.get("data")
                if not isinstance(data, str):
                    raise SmokeError(f"{tool}: image content is missing base64 data")
                try:
                    decoded = base64.b64decode(data, validate=True)
                except (ValueError, binascii.Error) as error:
                    raise SmokeError(f"{tool}: image content is not valid base64") from error
                out.append({"type": "image", "mimeType": content.get("mimeType"), "bytes": len(decoded)})
            else:
                out.append({"type": content.get("type"), "mimeType": content.get("mimeType"),
                            "bytes": len(content.get("data", ""))})
        if result.get("isError"):
            raise SmokeError(f"{tool}: tool error: {json.dumps(out)}")
        value = out[0] if len(out) == 1 else out
        if isinstance(value, dict) and "jobId" in value and value.get("status") in ("failed", "cancelled"):
            raise SmokeError(f"{tool}: job {value['jobId']} {value['status']}: {value.get('error', '')}")
        return value

    def close(self):
        if self._closed:
            return self.proc.returncode
        self._closed = True
        try:
            self.proc.stdin.close()
        except (OSError, ValueError):
            pass
        try:
            self.proc.wait(timeout=self.shutdown_timeout)
        except subprocess.TimeoutExpired:
            self.proc.kill()
            self.proc.wait(timeout=self.shutdown_timeout)
        finally:
            for thread in self._threads:
                thread.join(timeout=1)
            self.proc.stdout.close()
            self.proc.stderr.close()
        return self.proc.returncode


def lookup(value, path, reference):
    for part in path.split("."):
        if isinstance(value, dict) and part in value:
            value = value[part]
        elif isinstance(value, list) and part.isascii() and part.isdigit() and int(part) < len(value):
            value = value[int(part)]
        else:
            raise SmokeError(f"Missing reference {reference!r} at {part!r}")
    return value


def resolve(value, results):
    if isinstance(value, str) and value.startswith("$env."):
        name = value[5:]
        if name not in os.environ:
            raise SmokeError(f"Missing environment variable {name!r}")
        return os.environ[name]
    if isinstance(value, str) and value.startswith("$"):
        return lookup(results, value[1:], value)
    if isinstance(value, dict):
        return {key: resolve(item, results) for key, item in value.items()}
    if isinstance(value, list):
        return [resolve(item, results) for item in value]
    return value


def assert_result(result, expected):
    for path, wanted in expected.items():
        actual = lookup(result, path, path)
        # Canonical JSON distinguishes booleans from numbers, including nested values.
        if json.dumps(actual, sort_keys=True) != json.dumps(wanted, sort_keys=True):
            raise SmokeError(f"Assertion {path!r}: expected {json.dumps(wanted)}, got {json.dumps(actual)}")


def parse_steps(arguments):
    if arguments and arguments[0].startswith("@"):
        if len(arguments) != 1:
            raise SmokeError("A scenario filename must be the only argument after the server")
        with open(arguments[0][1:], encoding="utf-8") as scenario:
            steps = json.load(scenario)
    else:
        steps = []
        index = 0
        while index < len(arguments):
            tool = arguments[index]
            index += 1
            args = {}
            if index < len(arguments) and arguments[index].startswith("{"):
                args = json.loads(arguments[index])
                index += 1
            steps.append([tool, args])
    if not isinstance(steps, list):
        raise SmokeError("A scenario must be a list of steps")
    for index, step in enumerate(steps, 1):
        if (not isinstance(step, list) or len(step) not in (2, 3)
                or not isinstance(step[0], str) or not isinstance(step[1], dict)
                or (len(step) == 3 and not isinstance(step[2], dict))):
            raise SmokeError(f"Invalid scenario step {index}: expected [tool, args, optional assertions]")
    return steps


def run_steps(client, steps, maxchars):
    results = {}
    for index, step in enumerate(steps, 1):
        tool, args = step[:2]
        expected = step[2] if len(step) == 3 else {}
        try:
            args = resolve(args, results)
            started = time.monotonic()
            if tool == "tools":
                result = client.send("tools/list")["result"]
            elif tool == "sleep":
                seconds = float(args.get("seconds", 1))
                if not math.isfinite(seconds) or seconds < 0:
                    raise SmokeError("sleep seconds must be finite and nonnegative")
                time.sleep(seconds)
                result = {"seconds": seconds}
            else:
                result = client.call(tool, args)
            assert_result(result, resolve(expected, results))
            results[tool] = result
            results["last"] = result
            print(f"== {tool} {json.dumps(args)} ({time.monotonic() - started:.1f}s)")
            if tool == "tools":
                tools = result.get("tools") if isinstance(result, dict) else None
                if not isinstance(tools, list):
                    raise SmokeError("tools/list: missing tools array")
                print(f"{len(tools)} tools:")
                for item in tools:
                    print(" -", item["name"], "|", item.get("description", "")[:90])
                continue
            text = json.dumps(result, indent=1)
            print(text if len(text) <= maxchars else text[:maxchars] + f"\n... [{len(text)} chars]")
        except (SmokeError, ValueError, TypeError) as error:
            raise SmokeError(f"Step {index} ({tool}): {error}") from error


MANUAL_SCENARIOS = {"provoke-hang": "deliberately hangs and resets the GPU"}
PSEUDO_TOOLS = {"tools", "sleep"}


def environment_references(value):
    """Names of the $env.NAME references anywhere in a step."""
    if isinstance(value, str):
        return {value[5:]} if value.startswith("$env.") else set()
    items = value.values() if isinstance(value, dict) else value if isinstance(value, list) else ()
    found = set()
    for item in items:
        found |= environment_references(item)
    return found


def step_references(value):
    """Result names referenced as $name.path ($env references excluded)."""
    if isinstance(value, str):
        return {value[1:].split(".", 1)[0]} if value.startswith("$") and not value.startswith("$env.") else set()
    items = value.values() if isinstance(value, dict) else value if isinstance(value, list) else ()
    found = set()
    for item in items:
        found |= step_references(item)
    return found


def scenario_files(folder):
    return sorted(os.path.join(folder, name) for name in os.listdir(folder) if name.endswith(".json"))


def validate_scenario(path, registered):
    """Problems in one scenario: its shape, unregistered tools, and references to results no earlier step produced."""
    name = os.path.basename(path)
    try:
        steps = parse_steps(["@" + path])
    except (SmokeError, OSError, ValueError) as error:
        return [f"{name}: {error}"]
    problems = []
    earlier = {"last"}
    for index, step in enumerate(steps, 1):
        if step[0] not in PSEUDO_TOOLS and step[0] not in registered:
            problems.append(f"{name}: step {index} calls unregistered tool {step[0]!r}")
        for missing in sorted(step_references(step[1:]) - earlier):
            problems.append(f"{name}: step {index} references ${missing} before any step produced it")
        earlier.add(step[0])
    return problems


def validate_scenarios(folder, registered=None):
    if registered is None:
        from tool_registry import registered_tools
        registered = set(registered_tools())
    problems = []
    for path in scenario_files(folder):
        problems += validate_scenario(path, registered)
    return problems


def run_scenario(server, steps, maxchars, timeout):
    """Starts the server, runs the steps, shuts the server down and returns the exit code."""
    client = None
    exit_code = 0
    try:
        client = Client([os.path.abspath(server)], timeout=timeout)
        init = client.send("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                          "clientInfo": {"name": "smoke", "version": "1"}})
        if not isinstance(init["result"], dict) or "serverInfo" not in init["result"]:
            raise SmokeError("initialize: missing serverInfo")
        print("initialize:", json.dumps(init["result"]["serverInfo"]))
        client.send("notifications/initialized", notify=True)
        run_steps(client, steps, maxchars)
    except (SmokeError, OSError, ValueError, TypeError) as error:
        print(f"smoke: {error}", file=sys.stderr)
        exit_code = 1
    except KeyboardInterrupt:
        print("smoke: interrupted", file=sys.stderr)
        exit_code = 130
    finally:
        if client is not None:
            try:
                server_exit = client.close()
                if exit_code == 0 and server_exit != 0:
                    print(f"smoke: server exited with code {server_exit}", file=sys.stderr)
                    exit_code = 1
            except (OSError, subprocess.TimeoutExpired) as error:
                print(f"smoke: server cleanup failed: {error}", file=sys.stderr)
                exit_code = 1
            lines = list(client.stderr_lines)
            if exit_code:
                lines = lines[-30:]
            else:
                lines = [line for line in lines if any(word in line.lower() for word in ("warn", "error", "fail"))][-30:]
            if lines:
                print("--- server stderr ---\n" + "\n".join(lines), file=sys.stderr)
    return exit_code


def run_all(server, folder, maxchars, timeout, skip_missing_env, environ=None):
    """Runs every scenario in folder; the last stdout line is 'smoke: {passed, failed, skipped}'."""
    environ = os.environ if environ is None else environ
    summary = {"passed": [], "failed": [], "skipped": []}
    for path in scenario_files(folder):
        name = os.path.splitext(os.path.basename(path))[0]
        if name in MANUAL_SCENARIOS:
            summary["skipped"].append(f"{name} (manual: {MANUAL_SCENARIOS[name]})")
            continue
        steps = parse_steps(["@" + path])
        missing = sorted(environment_references(steps) - set(environ))
        if missing and skip_missing_env:
            summary["skipped"].append(f"{name} (unset: {', '.join(missing)})")
            continue
        print(f"=== scenario {name}", flush=True)
        code = run_scenario(server, steps, maxchars, timeout)
        summary["passed" if code == 0 else "failed"].append(name)
        if code == 130:
            break
    print("smoke: " + json.dumps(summary), flush=True)
    return 1 if summary["failed"] else 0


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--max", type=int, default=6000, dest="maxchars")
    parser.add_argument("--timeout", type=float, default=660, help="Deadline in seconds for each RPC response")
    parser.add_argument("--validate-scenarios", metavar="DIR", help="Check every scenario in DIR without starting a server.")
    parser.add_argument("--all", metavar="DIR", dest="all_dir", help="Run every scenario in DIR, each on a fresh server.")
    parser.add_argument("--skip-missing-env", action="store_true", help="With --all, skip scenarios whose $env references are unset.")
    parser.add_argument("server", nargs="?")
    parser.add_argument("steps", nargs=argparse.REMAINDER)
    options = parser.parse_args(argv)
    if options.validate_scenarios:
        try:
            problems = validate_scenarios(options.validate_scenarios)
        except (OSError, ValueError) as error:
            problems = [str(error)]
        for problem in problems:
            print(f"smoke: {problem}", file=sys.stderr)
        print(f"smoke: {len(scenario_files(options.validate_scenarios)) if not problems else 'invalid'} scenarios"
              f" {'valid' if not problems else ''} in {options.validate_scenarios}".replace("  ", " "))
        return 1 if problems else 0
    if not options.server:
        print("smoke: a server executable is required", file=sys.stderr)
        return 1
    if options.maxchars <= 0:
        print("smoke: --max must be positive", file=sys.stderr)
        return 1
    try:
        if options.all_dir:
            if options.steps:
                raise SmokeError("--all runs the scenarios in DIR; do not pass steps")
            return run_all(options.server, options.all_dir, options.maxchars, options.timeout, options.skip_missing_env)
        steps = parse_steps(options.steps)
    except (SmokeError, OSError, ValueError, TypeError) as error:
        print(f"smoke: {error}", file=sys.stderr)
        return 1
    return run_scenario(options.server, steps, options.maxchars, options.timeout)


if __name__ == "__main__":
    sys.exit(main())
