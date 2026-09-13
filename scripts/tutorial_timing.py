"""Bounded recorded-timing and existing DirectX-dump tutorial investigations.

The caller owns process launch and timing recording.  These functions use the
runner's context so every MCP request, response, and task outcome is retained.
"""

from collections import Counter
from decimal import Decimal, InvalidOperation
import json
import os
from pathlib import Path
import re
import uuid

from smoke import SmokeError


def _require(condition, message):
    if not condition:
        raise SmokeError(message)


def _ns(value):
    _require(isinstance(value, str), f"Nanoseconds must retain their decimal-string representation: {value!r}")
    try:
        number = Decimal(value)
    except InvalidOperation as error:
        raise SmokeError(f"Invalid decimal nanoseconds: {value!r}") from error
    _require(number.is_finite() and number == number.to_integral_value(), f"Invalid integral nanoseconds: {value!r}")
    return number


def validate_submissions(rows):
    """Check exact arithmetic without floating-point timestamp conversion."""
    usable = 0
    unavailable = Counter()
    for row in rows:
        state = row["gpuTiming"]["state"]
        if state == "available":
            submit, begin, end = (_ns(row[key]) for key in ("submitNs", "gpuBeginNs", "gpuEndNs"))
            _require(0 <= submit <= begin < end, "Available submission contains inconsistent GPU timestamps")
            _require(_ns(row["latencyNs"]) == begin - submit, "Submission latency lost exact nanosecond arithmetic")
            _require(_ns(row["gpuDurationNs"]) == end - begin, "Submission duration lost exact nanosecond arithmetic")
            usable += 1
        else:
            _require(row.get("latencyNs") is None and row.get("gpuDurationNs") is None,
                     "Unavailable GPU timing must not advertise derived latency or duration")
            unavailable[row["gpuTiming"].get("reason") or state] += 1
    return {"rowsChecked": len(rows), "usableGpuTimings": usable, "unavailableReasons": dict(unavailable)}


def _validate_events(pages):
    count = 0
    for response in pages:
        start, end = (_ns(response["provenance"][key]) for key in ("startNs", "endNs"))
        for row in response["events"]["items"]:
            begin, finish = (_ns(row[key]) for key in ("beginNs", "endNs"))
            _require(_ns(row["durationNs"]) == finish - begin, "Recorded event duration differs from its original interval")
            _require(_ns(row["overlapDurationNs"]) == max(Decimal(0), min(end, finish) - max(start, begin)),
                     "Recorded event overlap differs from its selected interval")
            count += 1
    return {"eventsChecked": count, "interpretation": "Nested event durations overlap; they are not summed into frame latency."}


def _pages(ctx, tool, collections, max_pages=2, **arguments):
    """Follow advertised continuation calls, retaining a deliberate page cap."""
    keys = (collections,) if isinstance(collections, str) else tuple(collections)
    pages, combined = [], {key: [] for key in keys}
    current = dict(arguments)
    next_call = None
    for _ in range(max_pages):
        response = ctx.query(tool, **current)
        _require(isinstance(response, dict), f"{tool} did not return an object")
        pages.append(response)
        for key in keys:
            page = response if key == "" else response[key]
            _require(isinstance(page.get("items"), list), f"{tool} omitted its {key or 'root'} collection")
            _require(page.get("offset", 0) == current.get("offset", 0), f"{tool} changed the page offset")
            combined[key].extend(page["items"])
        offsets = [(response if key == "" else response[key]).get("nextOffset") for key in keys]
        offsets = [offset for offset in offsets if offset is not None]
        if not offsets:
            next_call = None
            break
        next_offset = min(offsets)
        _require(next_offset > current.get("offset", 0), f"{tool} continuation did not advance")
        next_call = next((call for call in response.get("nextCalls", [])
                          if call.get("tool") == tool and call.get("arguments", {}).get("offset") == next_offset), None)
        # Legacy dump collection pages expose nextOffset without nextCalls.
        if next_call is None and tool.startswith("pix_dump_"):
            next_call = {"tool": tool, "arguments": {**current, "offset": next_offset}}
        _require(next_call is not None, f"{tool} page has no usable continuation call")
        current = dict(next_call["arguments"])
    result = {"pages": pages, "items": combined, "pagesRead": len(pages),
              "truncatedByInvestigationBudget": next_call is not None}
    if next_call:
        result["resumeCall"] = next_call
    return result


def _expected_expired(ctx, handle, reference):
    try:
        response = ctx.query("pix_timing_submissions", handle=handle, submissionRef=reference)
    except SmokeError as error:
        # Jobs may wrap the structured tool error; match its code, not generic text.
        detail = str(error)
        if re.search(r'''["']code["']\s*:\s*["']result_expired["']''', detail):
            return {"status": "passed", "expectedError": "result_expired", "submissionRef": reference, "error": detail}
        raise
    raise SmokeError(f"Expired timing submission reference was accepted: {response}")


def _symbol_paths(ctx):
    args = getattr(ctx, "args", None)
    ue_root = Path(getattr(args, "ue_root", None) or r"W:\UE5\UnrealEngine")
    project = getattr(args, "project", None) or ue_root / "CitySample" / "CitySample.uproject"
    candidates = [ue_root / "Engine" / "Binaries" / "Win64"]
    if project:
        candidates.append(Path(project).parent / "Binaries" / "Win64")
    # Explicit context paths allow callers to use a staged or packaged workload.
    for value in getattr(ctx, "symbol_paths", []):
        candidates.append(Path(value))
    return list(dict.fromkeys(str(path.resolve()) for path in candidates if path.is_dir()))


def run_timing(ctx, capture_path, process_id):
    """Analyze a fresh recording and close only the handle opened here."""
    report = {"capture": str(capture_path), "processId": process_id, "limitations": [], "failedTasks": [],
              "measurementSource": "recorded timing capture; no GPU replay"}

    def stage(name, callback, optional=False):
        value = ctx.task("Timing: " + name, callback, optional=optional)
        if value is None:
            tasks = getattr(ctx, "report", {}).get("tasks", [])
            unsupported = optional and tasks and tasks[-1].get("status") == "unsupported"
            if unsupported:
                limitation(name, "Native capability is unsupported; see its retained MCP error and task report.")
            else:
                report["failedTasks"].append(name)
        return value

    def limitation(feature, reason, evidence=None):
        report["limitations"].append({"feature": feature, "reason": reason, **({"evidence": evidence} if evidence is not None else {})})
        if hasattr(ctx, "limitation"):
            ctx.limitation("Timing " + feature + ": " + reason, evidence)

    opened = stage("open fresh capture", lambda: ctx.query("pix_timing_open", path=str(capture_path)))
    if not opened:
        report["status"] = "failed"
        ctx.save("timing-investigation", report)
        return report
    handle = opened["handle"]
    selection = {"handle": handle, "processId": process_id}
    try:
        before = stage("overview before symbols", lambda: ctx.query("pix_timing_overview", **selection, limit=25))
        report["beforeSymbols"] = before
        old = stage("submission reference before symbols", lambda: ctx.query("pix_timing_submissions", **selection, limit=1), optional=True)
        old_rows = (old or {}).get("submissions", {}).get("items", [])
        paths = _symbol_paths(ctx)
        report["symbolSearchPaths"] = paths
        report["localPdbCounts"] = {path: sum(1 for _ in Path(path).glob("*.pdb")) for path in paths}
        if paths:
            resolved = stage("resolve local engine and game CPU symbols", lambda: ctx.query(
                "pix_timing_resolve_symbols", handle=handle, pdbSearchPath=";".join(paths),
                includeKernelSymbols=False, includeSourceData=True, useNtSymbolPath=False, waitSeconds=0), optional=True)
            report["symbolResolution"] = resolved
            if resolved and old_rows:
                stage("symbol resolution expires submission references", lambda: _expected_expired(ctx, handle, old_rows[0]["submissionRef"]))
        else:
            limitation("symbols", "No engine or game binary directories are present for local PDB resolution.")

        fresh = stage("submission reference before save", lambda: ctx.query("pix_timing_submissions", **selection, limit=1), optional=True)
        fresh_rows = (fresh or {}).get("submissions", {}).get("items", [])
        destination = Path(ctx.artifacts) / (Path(capture_path).stem + "-symbols-" + uuid.uuid4().hex + ".wpix")
        saved = stage("save resolved timing capture", lambda: ctx.query("pix_timing_save", handle=handle, asPath=str(destination)))
        report["savedCapture"] = saved
        if saved and fresh_rows:
            stage("save expires submission references", lambda: _expected_expired(ctx, handle, fresh_rows[0]["submissionRef"]))

        overview = stage("overview and process/thread/queue pages", lambda: _pages(
            ctx, "pix_timing_overview", ("processes", "threads", "queues"), max_pages=8, **selection, limit=100))
        threads = []
        if overview:
            report["overview"] = ctx.save("timing-overview", overview)
            first = overview["pages"][0]
            report["coverage"] = {key: first[key] for key in ("capabilities", "sampleCount", "stackCount", "symbolCount", "counterCount", "provenance")}
            for feature, capability in first["capabilities"].items():
                if capability["state"] != "available":
                    limitation(feature, capability.get("reason") or capability["state"], capability)
            if not overview["items"]["processes"]:
                limitation("process", "The launched process ID was not found in the recorded capture.")
            threads = overview["items"]["threads"]

        for domain in ("cpu", "gpu"):
            events = stage(domain + " marker events and continuation", lambda domain=domain: _pages(
                ctx, "pix_timing_events", "events", **selection, domain=domain, orderBy="duration", limit=25))
            if events:
                report[domain + "Events"] = ctx.save("timing-" + domain + "-events", events)
                stage(domain + " event interval arithmetic", lambda events=events: _validate_events(events["pages"]))
                if not events["items"]["events"]:
                    limitation(domain + "Events", "No recorded named marker executions match the launched process.")

        counters = stage("recorded counter catalog and continuation", lambda: _pages(
            ctx, "pix_timing_counters_list", "counters", max_pages=4, handle=handle, limit=25))
        if counters:
            report["counterCatalog"] = ctx.save("timing-counters", counters)
            rows = counters["items"]["counters"]
            if not rows:
                limitation("counters", "The timing capture contains no recorded counters.")
            ranked = sorted(rows, key=lambda row: (not any(word in (row.get("name") or "").lower()
                for word in ("cpu", "gpu", "memory", "utilization", "frequency")), row["counterId"]))
            report["counterReads"] = []
            for counter in ranked[:6]:
                samples = stage("read counter " + counter["counterId"], lambda counter=counter: _pages(
                    ctx, "pix_timing_counters_read", "samples", handle=handle, counterId=counter["counterId"], limit=25))
                if samples:
                    report["counterReads"].append(ctx.save("timing-counter-" + counter["counterId"], samples))
                    if not samples["items"]["samples"]:
                        limitation("counterSamples", "Selected counter has no samples in the reliable capture interval.", counter)

        hotspots = stage("CPU sampled hotspots and continuation", lambda: _pages(
            ctx, "pix_timing_hotspots", "hotspots", **selection, limit=25))
        if hotspots:
            report["hotspots"] = ctx.save("timing-hotspots", hotspots)
            profile = hotspots["pages"][0]
            coverage = profile["coverage"]
            report["sampleCoverage"] = coverage
            if coverage["totalSamples"] == 0:
                limitation("CPU profiling", "No statistical CPU samples match the launched process.", coverage)
            if coverage["samplesWithoutStacks"] or coverage["stackState"] != "available":
                limitation("sample stacks", "Some or all CPU samples have no usable recorded callstack.", coverage)
            if coverage["symbolState"] != "resolved":
                limitation("CPU symbols", "Some or all sampled addresses remain unresolved after local PDB resolution.", coverage)

            def coverage_check():
                _require(coverage["totalSamples"] == coverage["samplesWithStacks"] + coverage["samplesWithoutStacks"],
                         "CPU sample stack coverage does not partition all selected samples")
                for row in hotspots["items"]["hotspots"]:
                    _require(0 <= row["exclusiveSamples"] <= row["inclusiveSamples"] <= coverage["totalSamples"],
                             "CPU hotspot sample counts exceed their selection")
                return {"samplesChecked": coverage["totalSamples"], "metric": "statistical sample counts, not exact CPU duration"}
            stage("CPU sample coverage and count consistency", coverage_check)
            tree = stage("CPU caller tree", lambda: _pages(ctx, "pix_timing_calltree", "children",
                handle=handle, profileRef=profile["profileRef"], limit=25))
            if tree:
                report["calltree"] = ctx.save("timing-calltree", tree)
                branches = [row for row in tree["items"]["children"] if row["childCount"] > 0][:3]
                for branch in branches:
                    value = stage("follow caller tree " + branch["nodeId"], lambda branch=branch: _pages(
                        ctx, "pix_timing_calltree", "children", handle=handle, profileRef=profile["profileRef"],
                        parentNodeId=branch["nodeId"], limit=25))
                    if value:
                        ctx.save("timing-calltree-" + branch["nodeId"], value)

        submissions = stage("process queue submissions and continuation", lambda: _pages(
            ctx, "pix_timing_submissions", "submissions", max_pages=4, **selection, limit=25), optional=True)
        submission_rows = []
        if submissions:
            report["submissions"] = ctx.save("timing-submissions", submissions)
            submission_rows = submissions["items"]["submissions"]
            arithmetic = stage("exact submission latency and duration", lambda: validate_submissions(submission_rows))
            report["submissionArithmetic"] = arithmetic
            if not submission_rows:
                limitation("submissions", "No queue submissions match the launched process in the reliable interval.")
            elif arithmetic and arithmetic["unavailableReasons"]:
                limitation("GPU submission timing", "Some queue rows do not have usable recorded GPU timestamps.", arithmetic)
            if submission_rows:
                row = next((row for row in submission_rows if row["gpuTiming"]["state"] == "available"), submission_rows[0])

                def exact():
                    value = ctx.query("pix_timing_submissions", handle=handle, submissionRef=row["submissionRef"])
                    found = value["submissions"]["items"]
                    _require(len(found) == 1 and found[0] == row, "Exact submission lookup changed the recorded row")
                    return value
                stage("exact submission reference lookup", exact)
                for call in row.get("nextCalls", []):
                    if call["tool"] in ("pix_timing_events", "pix_timing_hotspots"):
                        value = stage("follow submitting-thread " + call["tool"],
                            lambda call=call: ctx.query(call["tool"], **call["arguments"]), optional=True)
                        if value:
                            ctx.save("timing-submission-" + call["tool"], value)

        thread_ids = list(dict.fromkeys(row["threadRowId"] for row in submission_rows
            if row.get("threadRowId") and row.get("threadCorrelation", {}).get("state") == "available"))
        thread_ids = list(dict.fromkeys(thread_ids + [row["threadRowId"] for row in threads]))[:3]
        report["switchCoverage"] = []
        for thread in thread_ids:
            switches = stage("scheduling transitions for thread " + thread, lambda thread=thread: _pages(
                ctx, "pix_timing_thread_switches", "switches", handle=handle, threadRowId=thread, limit=25), optional=True)
            if switches:
                ctx.save("timing-thread-" + thread + "-switches", switches)
                outgoing = [row for row in switches["items"]["switches"] if row["direction"] == "switchOut"]
                coverage = {"threadRowId": thread, "transitionsRead": len(switches["items"]["switches"]),
                    "switchOutRows": len(outgoing), "stackStates": dict(Counter(row["stack"]["state"] for row in outgoing)),
                    "symbolStates": dict(Counter(row["stack"]["symbolState"] for row in outgoing)),
                    "truncatedByInvestigationBudget": switches["truncatedByInvestigationBudget"]}
                report["switchCoverage"].append(coverage)
                if not outgoing or any(row["stack"]["state"] != "available" for row in outgoing):
                    limitation("switch-out stacks", "Inspected transitions do not all carry a stack recorded at the exact switch timestamp.", coverage)
                if any(row["stack"]["symbolState"] != "resolved" for row in outgoing):
                    limitation("switch-out symbols", "Some recorded switch-out addresses remain unresolved.", coverage)
        if not thread_ids:
            limitation("thread scheduling", "No attributable capture-local thread row is available to inspect.")
        report["interpretation"] = "Submission correlation and scheduling transitions do not establish waited-on objects or the cause of a GPU gap."
    except BaseException as error:
        report["failedTasks"].append("investigation processing")
        report["error"] = f"{type(error).__name__}: {error}"
        raise
    finally:
        stage("close timing capture", lambda: ctx.query("pix_close", handle=handle))
        report["status"] = "failed" if report["failedTasks"] else "passed"
        ctx.save("timing-investigation", report)
    return report


def _existing_dumps():
    roots = [Path(r"E:\PixCaptures"), Path(os.environ.get("LOCALAPPDATA", "")) / "CrashDumps"]
    candidates, errors, ordinary = [], [], 0
    for root in roots:
        if not root.is_dir():
            continue
        for directory, _, names in os.walk(root, onerror=lambda error: errors.append(str(error))):
            for name in names:
                path = Path(directory) / name
                if path.suffix.lower() in (".dxdmp_preview", ".dxdmp"):
                    candidates.append(path)
                elif path.suffix.lower() == ".dmp":
                    ordinary += 1
    return sorted(candidates, key=lambda path: path.stat().st_mtime, reverse=True), {
        "searchedRoots": [str(root) for root in roots], "searchErrors": errors,
        "ordinaryMinidumpsExcluded": ordinary,
        "supportedExtensions": [".dxdmp_preview", ".dxdmp"]}


def run_dumps(ctx):
    """Inspect an existing suitable dump only; never induce a hang or TDR."""
    candidates, search = _existing_dumps()
    report = {**search, "candidates": [str(path) for path in candidates], "limitations": []}
    if not candidates:
        report.update(status="untested", reason="No existing DirectX dump was found in the targeted locations. Ordinary Windows .dmp files cannot exercise PIX DirectX-dump triage.")
        ctx.save("dump-investigation", report)
        return report
    handle = None
    for path in candidates[:3]:
        opened = ctx.task("Dump: open " + path.name, lambda path=path: ctx.query("pix_dump_open", path=str(path)), optional=True)
        if opened:
            handle = opened["handle"]
            report["selectedDump"] = str(path)
            report["metadata"] = opened
            break
    if not handle:
        report.update(status="failed", reason="None of the three newest candidate DirectX dumps could be opened; see retained native errors.")
        ctx.save("dump-investigation", report)
        return report
    try:
        triage = ctx.task("Dump: ranked triage and continuation", lambda: _pages(
            ctx, "pix_dump_triage", "observations", handle=handle, limit=10))
        if triage:
            report["triage"] = ctx.save("dump-triage", triage)
            report["coverage"] = triage["pages"][0]["coverage"]
            for feature, coverage in report["coverage"].items():
                if coverage.get("state") not in ("available", "empty"):
                    report["limitations"].append({"feature": feature, "coverage": coverage})
            calls = [call for row in triage["items"]["observations"] for call in row.get("nextCalls", [])]
            seen = set()
            for call in calls[:8]:
                key = json.dumps(call, sort_keys=True)
                if key in seen:
                    continue
                seen.add(key)
                value = ctx.task("Dump: follow " + call["tool"], lambda call=call: ctx.query(call["tool"], **call["arguments"]), optional=True)
                if value:
                    ctx.save("dump-evidence-" + str(len(seen)), value)
        queues = ctx.task("Dump: queue state", lambda: ctx.query("pix_dump_queues", handle=handle), optional=True)
        if queues:
            ctx.save("dump-queues", queues)
            rows = queues if isinstance(queues, list) else queues.get("items", [])
            for queue in rows[:3]:
                events = ctx.task("Dump: queue " + str(queue["queueIndex"]) + " events", lambda queue=queue: _pages(
                    ctx, "pix_dump_events", "", handle=handle, queueIndex=queue["queueIndex"],
                    limit=10, maxDepth=1, maxEvents=40), optional=True)
                if events:
                    ctx.save("dump-queue-" + str(queue["queueIndex"]), events)
                    first = next((row for row in events["items"][""] if row.get("eventRef")), None)
                    if first:
                        ctx.task("Dump: navigate exact event", lambda first=first: ctx.query("pix_dump_event", eventRef=first["eventRef"], limit=10), optional=True)
        for tool, arguments in (("pix_dump_page_faults", {"limit": 10, "maxResourceEvents": 10}),
                                ("pix_dump_resources", {"limit": 10}),
                                ("pix_dump_journal", {"limit": 10}),
                                ("pix_dump_shader_waves", {"limit": 5})):
            value = ctx.task("Dump: " + tool, lambda tool=tool, arguments=arguments: ctx.query(tool, handle=handle, **arguments), optional=True)
            if value:
                ctx.save("dump-" + tool.removeprefix("pix_dump_"), value)
        report["status"] = "passed" if triage else "failed"
        report["interpretation"] = "Ranked dump observations retain evidence and coverage; they do not establish a causal diagnosis."
    finally:
        ctx.task("Dump: close capture", lambda: ctx.query("pix_close", handle=handle))
        ctx.save("dump-investigation", report)
    return report
