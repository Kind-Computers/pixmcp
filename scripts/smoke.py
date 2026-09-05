"""Minimal raw JSON-RPC stdio client for smoke-testing the pixmcp server (no mcp package needed).

Run from the repository root so relative paths in scenarios resolve.

Usage: python mcpclient.py <server exe> <tool> [json-args] ...
 - 'tools' lists tools.
 - String argument values starting with '$' are resolved from earlier results:
   "$pix_device_launch.processId" or "$last.result.path" (dot path into the JSON result);
   "$env.NAME" reads an environment variable.
 - 'sleep' with {"seconds": N} pauses.
"""
import json
import os
import subprocess
import sys
import threading
import time


class Client:
    def __init__(self, cmd):
        self.proc = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8", bufsize=1)
        self.next_id = 1
        self.stderr_lines = []
        threading.Thread(target=self._pump_stderr, daemon=True).start()

    def _pump_stderr(self):
        for line in self.proc.stderr:
            self.stderr_lines.append(line.rstrip())

    def send(self, method, params=None, notify=False):
        msg = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            msg["params"] = params
        if not notify:
            msg["id"] = self.next_id
            self.next_id += 1
        self.proc.stdin.write(json.dumps(msg) + "\n")
        self.proc.stdin.flush()
        if notify:
            return None
        while True:
            line = self.proc.stdout.readline()
            if not line:
                raise RuntimeError("server exited: " + "\n".join(self.stderr_lines[-30:]))
            data = json.loads(line)
            if data.get("id") == msg["id"]:
                return data
            print("<<", json.dumps(data)[:300], file=sys.stderr)

    def call(self, tool, args=None):
        r = self.send("tools/call", {"name": tool, "arguments": args or {}})
        if "error" in r:
            return {"rpcError": r["error"]}
        result = r["result"]
        out = []
        for c in result.get("content", []):
            if c.get("type") == "text":
                try:
                    out.append(json.loads(c["text"]))
                except Exception:
                    out.append(c["text"])
            else:
                out.append({"type": c.get("type"), "mimeType": c.get("mimeType"), "bytes": len(c.get("data", ""))})
        if result.get("isError"):
            return {"toolError": out}
        return out[0] if len(out) == 1 else out

    def close(self):
        try:
            self.proc.stdin.close()
            self.proc.wait(timeout=60)
        except Exception:
            self.proc.kill()


def resolve(value, results):
    if isinstance(value, str) and value.startswith("$env."):
        return os.environ.get(value[5:])
    if isinstance(value, str) and value.startswith("$"):
        parts = value[1:].split(".")
        cur = results.get(parts[0])
        for p in parts[1:]:
            if isinstance(cur, list):
                cur = cur[int(p)]
            elif isinstance(cur, dict):
                cur = cur.get(p)
            else:
                return None
        return cur
    if isinstance(value, dict):
        return {k: resolve(v, results) for k, v in value.items()}
    if isinstance(value, list):
        return [resolve(v, results) for v in value]
    return value


def main():
    argv = sys.argv[1:]
    maxchars = 6000
    if argv and argv[0].startswith("--max="):
        maxchars = int(argv[0][6:])
        argv = argv[1:]
    cmd = [os.path.abspath(argv[0])]
    c = Client(cmd)
    init = c.send("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "smoke", "version": "0"}})
    print("initialize:", json.dumps(init.get("result", init).get("serverInfo")))
    c.send("notifications/initialized", notify=True)
    results = {}
    steps = []
    if len(argv) > 1 and argv[1].startswith("@"):
        steps = json.load(open(argv[1][1:], encoding="utf-8"))
    else:
        i = 1
        while i < len(argv):
            tool = argv[i]
            args = {}
            if i + 1 < len(argv) and argv[i + 1].startswith("{"):
                args = json.loads(argv[i + 1])
                i += 1
            i += 1
            steps.append([tool, args])
    for tool, args in steps:
        if tool == "tools":
            r = c.send("tools/list")
            tools = r["result"]["tools"]
            print(f"{len(tools)} tools:")
            for t in tools:
                print(" -", t["name"], "|", t.get("description", "")[:90])
            continue
        if tool == "sleep":
            time.sleep(float(args.get("seconds", 1)))
            continue
        args = resolve(args, results)
        t0 = time.time()
        res = c.call(tool, args)
        results[tool] = res
        results["last"] = res
        print(f"== {tool} {json.dumps(args)} ({time.time() - t0:.1f}s)")
        text = json.dumps(res, indent=1, default=str)
        print(text if len(text) < maxchars else text[:maxchars] + f"\n... [{len(text)} chars]")
    c.close()
    errs = [l for l in c.stderr_lines if "warn" in l.lower() or "error" in l.lower() or "fail" in l.lower()]
    if errs:
        print("--- stderr warnings/errors ---")
        print("\n".join(errs[-30:]))


if __name__ == "__main__":
    main()
