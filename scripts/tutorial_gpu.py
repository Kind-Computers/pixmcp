"""Bounded GPU-capture investigations used by tutorial_validation.py.

All capture/replay operations go through the caller's MCP context. Workload launch
and final handle ownership belong to the caller; this module leaves captures open
and stops GPU analysis before returning.
"""
import base64
import hashlib
import json
from pathlib import Path
import re
import struct
import uuid
import zlib

from smoke import SmokeError


def _require(condition, message):
    if not condition:
        raise SmokeError(message)


def _items(value):
    if isinstance(value, list):
        return value
    return value.get("items", []) if isinstance(value, dict) else []


def _metadata(value):
    if isinstance(value, list):
        return next((item for item in value if isinstance(item, dict) and
                     item.get("type") != "image"), {})
    return value or {}


def _safe(value):
    return re.sub(r"[^A-Za-z0-9_.-]+", "-", str(value)).strip("-")


def _missing(reason, unsupported=False):
    return {"status": "unsupported" if unsupported else "untested", "reason": reason}


def _continuation(ctx, tool, arguments, page):
    """Exercise one exact continuation without draining an entire inventory."""
    if not isinstance(page, dict):
        return {"status": "untested", "reason": "The query did not return a page."}
    for call in page.get("nextCalls", []):
        if call.get("tool") == tool:
            return ctx.query(tool, **call["arguments"])
    next_offset = page.get("nextOffset")
    if next_offset is None:
        return {"complete": True, "reason": "The first page contains all matching rows."}
    _require(next_offset > arguments.get("offset", 0), "Pagination did not advance")
    return ctx.query(tool, **{**arguments, "offset": next_offset})


def _stop_all(ctx):
    inventory = ctx.query("pix_handles")
    stopped = []
    for item in _items(inventory):
        if item.get("kind") == "gpu" or str(item.get("handle", "")).startswith("gpu-"):
            stopped.append(ctx.query("pix_gpu_analysis_stop", handle=item["handle"]))
    return stopped


def _snapshot_job(ctx, name, tool, **arguments):
    job = ctx.call(tool, **arguments)
    job = ctx.agent.wait(job)
    reference = job["resultRef"]
    path = ctx.report_dir / f"{_safe(name)}-{uuid.uuid4().hex[:8]}.json"
    exported = ctx.query("pix_result_export", resultRef=reference, outPath=str(path))
    page = ctx.call("pix_result_read", resultRef=reference, limit=10)
    next_page = None
    if page.get("nextOffset") is not None:
        next_page = ctx.call("pix_result_read", resultRef=reference,
                             offset=page["nextOffset"], limit=10)
    result = {"job": job, "resultRef": reference, "export": exported,
              "page": page, "nextPage": next_page}
    value = page.get("value")
    if isinstance(value, dict) and value.get("unavailable") is True:
        result.update(unavailable=True, reason=value.get("reason", "Native feature is unavailable."))
    elif tool == "pix_gpu_shader_profile":
        shader_count = ctx.call("pix_result_read", resultRef=reference,
                                pointer="/shaderCount", limit=1)["value"]
        if shader_count:
            result["shaders"] = ctx.call("pix_result_read", resultRef=reference,
                                         pointer="/shaders", limit=1)
            args = {"resultRef": reference, "pointer": "/shaders/0/instructions", "limit": 5}
            instructions = ctx.call("pix_result_read", **args)
            result["instructions"] = instructions
            if instructions.get("nextOffset") is not None:
                result["nextInstructions"] = _continuation(ctx, "pix_result_read", args, instructions)
        else:
            result.update(status="untested", reason="Profiling completed without reporting any shaders.")
    if tool == "pix_gpu_drpix_run":
        succeeded = ctx.call("pix_result_read", resultRef=reference,
                             pointer="/results/0/succeeded", limit=1)
        if succeeded.get("value") is not True:
            status = ctx.call("pix_result_read", resultRef=reference,
                             pointer="/results/0/status", limit=100)
            result.update(status="failed", reason=f"Dr. PIX experiment failed: {status.get('value')}")
    return result


def _validate_png(png):
    _require(png.startswith(b"\x89PNG\r\n\x1a\n"), "Image does not have a PNG signature")
    _require(len(png) >= 33 and png[12:16] == b"IHDR", "PNG IHDR is missing")
    width, height = struct.unpack(">II", png[16:24])
    _require(width > 0 and height > 0, "PNG dimensions must be positive")
    position, compressed, ended = 8, bytearray(), False
    while position + 12 <= len(png):
        size = struct.unpack(">I", png[position:position + 4])[0]
        kind = png[position + 4:position + 8]
        end = position + 8 + size
        _require(end + 4 <= len(png), "PNG chunk extends past the image")
        crc = struct.unpack(">I", png[end:end + 4])[0]
        _require(zlib.crc32(png[position + 4:end]) & 0xffffffff == crc,
                 "PNG chunk checksum failed")
        if kind == b"IDAT":
            compressed.extend(png[position + 8:end])
        if kind == b"IEND":
            ended = True
            break
        position = end + 4
    _require(ended and bool(compressed), "PNG lacks image data or IEND")
    _require(bool(zlib.decompress(compressed)), "PNG image stream is empty")
    return {"width": width, "height": height, "bytes": len(png),
            "sha256": hashlib.sha256(png).hexdigest()}


def _retrieve_image(ctx, artifact_ref, name, original_path=None, *, ignore_alpha=False):
    # Raw transport preserves inline image bytes; smoke.Client.call summarizes them.
    arguments = {"artifactRef": artifact_ref, "maxDimension": 1280}
    if ignore_alpha:
        arguments["ignoreAlpha"] = True
    wire = ctx.client.send("tools/call", {"name": "pix_gpu_preview_image", "arguments": arguments})
    response = wire["result"]
    _require(not response.get("isError"), f"Image retrieval failed: {response}")
    metadata = next((json.loads(item["text"]) for item in response.get("content", [])
                     if item.get("type") == "text"), {})
    if "structuredContent" in response:
        _require(metadata == response["structuredContent"], "Image structured/text results disagree")
    if ignore_alpha:
        _require(metadata.get("alphaIgnored") is True, "Opaque RGB display was not applied")
    images = [item for item in response.get("content", []) if item.get("type") == "image"]
    _require(len(images) == 1, "Image retrieval did not return exactly one image")
    png = base64.b64decode(images[0]["data"], validate=True)
    decoded = _validate_png(png)
    _require((decoded["width"], decoded["height"]) == (metadata["width"], metadata["height"]),
             "PNG dimensions disagree with metadata")
    # Never replace the original screenshot with its resized inline representation.
    # Unique paths also preserve evidence when a named investigation is resumed.
    path = ctx.artifacts / f"{_safe(name)}-{uuid.uuid4().hex[:8]}.png"
    path.write_bytes(png)
    # Keep the original-byte paging check bounded even for a multi-megabyte PNG.
    first = ctx.query("pix_gpu_preview_bytes", artifactRef=artifact_ref, limit=16384)
    chunks = [base64.b64decode(first["base64"], validate=True)]
    _require(len(chunks[0]) == first["returnedBytes"], "Original byte count disagrees")
    last = first
    if first.get("nextOffset") is not None:
        last = ctx.query("pix_gpu_preview_bytes", artifactRef=artifact_ref,
                         offset=first["nextOffset"], limit=16384)
        second = base64.b64decode(last["base64"], validate=True)
        _require(last["offset"] == len(chunks[0]) and len(second) == last["returnedBytes"],
                 "Original-byte continuation is not contiguous")
        chunks.append(second)
    prefix = b"".join(chunks)
    _require(prefix.startswith(b"\x89PNG\r\n\x1a\n"), "Original artifact is not PNG")
    if original_path:
        with Path(original_path).open("rb") as source:
            _require(source.read(len(prefix)) == prefix, "Artifact bytes differ from saved screenshot")
    return {"path": str(path), "decoded": decoded, "metadata": metadata,
            "originalBytes": first["totalBytes"], "originalPrefixVerifiedBytes": len(prefix),
            "originalFullyRetrieved": last.get("nextOffset") is None,
            "artifactRef": artifact_ref}


def run_gpu(ctx, capture_path, adapter_name, label, *, require_tsr=True):
    """Inspect a frame on a matching adapter; non-UE fixtures may disable TSR checks."""
    prefix = f"GPU {label}"
    task = lambda name, work, optional=False: ctx.task(f"{prefix}: {name}", work, optional=optional)
    result = {"path": str(capture_path), "label": label, "requestedAdapter": adapter_name,
              "requireTsr": require_tsr, "baselineVerified": None}
    opened = task("open capture", lambda: ctx.query("pix_gpu_open", path=str(Path(capture_path).resolve())))
    if not opened:
        return {**result, "status": "failed", "reason": "Capture could not be opened."}
    handle = result["handle"] = opened["handle"]
    result["metadata"] = opened
    large_capture = opened.get("totalEvents", 0) > 2000
    selected = None
    try:
        def select_adapter():
            adapters = ctx.query("pix_gpu_analysis_adapters", handle=handle)
            matches = [item for item in adapters["adapters"]
                       if adapter_name.casefold() in item["name"].casefold()]
            _require(len(matches) == 1,
                     f"Expected exactly one replay adapter matching {adapter_name!r}: {adapters}")
            chosen = matches[0]
            analysis = ctx.query("pix_gpu_analysis_start", handle=handle, adapterId=chosen["id"])
            status = ctx.query("pix_gpu_analysis_status", handle=handle)
            _require(status.get("started") and str(status.get("selectedAdapter")) == str(chosen["id"]),
                     f"Replay did not start on the requested adapter: {status}")
            return {"adapter": chosen, "analysis": analysis, "nativeStatus": status}

        selected = task("select and verify replay adapter", select_adapter)
        result["replay"] = selected
        if selected:
            overview = task("overview and replay hotspots", lambda: ctx.query("pix_gpu_overview", handle=handle, limit=10))
            result["overview"] = overview
            large_capture = large_capture or sum(q.get("eventCount", 0) for q in (overview or {}).get("queues", [])) > 2000

            def events():
                args = {"handle": handle, "kind": "drawOrDispatch", "limit": 10}
                page = ctx.query("pix_gpu_events", **args)
                _require(bool(_items(page)), "No draw or dispatch events were found")
                return {"page": page, "nextPage": _continuation(ctx, "pix_gpu_events", args, page)}

            event_pages = task("event paging", events)
            if require_tsr:
                markers = task("TSR markers", lambda: ctx.query("pix_gpu_events", handle=handle,
                               kind="marker", nameContains="TemporalSuperResolution", limit=10))
                if markers and not _items(markers):
                    markers = task("TSR abbreviated markers", lambda: ctx.query("pix_gpu_events", handle=handle,
                                   kind="marker", nameContains="TSR", limit=10))
                result["tsrMarkers"] = markers
                def confirm_tsr():
                    _require(bool(_items(markers)), "TSR baseline verification failed: neither TemporalSuperResolution nor TSR marker was recorded.")
                    return {"verified": True, "matchingMarkers": len(_items(markers))}
                result["baselineVerified"] = bool(task("confirm TSR baseline in capture", confirm_tsr))

            passes = (overview or {}).get("topPasses", [])
            draws = (overview or {}).get("topDraws", [])
            if not draws and event_pages:
                draws = _items(event_pages["page"])
            result["selectedEvents"] = draws[:3]
            if passes:
                scope = passes[0]["eventRef"]
                result["selectedScope"] = scope
                tree_args = {"handle": handle, "queueIndex": scope["queueIndex"],
                             "parentIndex": scope["eventIndex"], "depth": 2, "limit": 5, "maxNodes": 20}
                tree = task("scoped timing tree", lambda: ctx.query("pix_gpu_timing_tree", **tree_args))
                if tree:
                    task("timing-tree continuation", lambda: _continuation(ctx, "pix_gpu_timing_tree", tree_args, tree))
                timing_args = {"handle": handle, "scope": scope, "kind": "drawOrDispatch", "limit": 5}
                page = task("scoped timed events", lambda: ctx.query("pix_gpu_timing_events", **timing_args))
                if page:
                    task("timing-event continuation", lambda: _continuation(ctx, "pix_gpu_timing_events", timing_args, page))

            shaders, resources, resource_scopes = [], [], {}
            for index, draw in enumerate(draws[:3]):
                event_ref = draw["eventRef"]
                inspected = task(f"inspect expensive event {index + 1}", lambda ref=event_ref:
                                 ctx.query("pix_gpu_inspect_event", eventRef=ref, includeDetails=True,
                                           viewLimit=3, bindingLimit=5))
                pipeline = task(f"pipeline state {index + 1}", lambda ref=event_ref:
                                ctx.query("pix_gpu_pipeline_state", eventRef=ref,
                                          includeSubobjects=True, includeRootSignature=True))
                for shader in _items((pipeline or {}).get("shaders")):
                    if shader.get("shaderRef"):
                        shaders.append(shader)
                if inspected:
                    for binding in (inspected.get("bindings") or {}).get("resources", []):
                        resource_ref = (binding.get("resource") or {}).get("resourceRef")
                        if resource_ref and resource_ref not in resources:
                            resources.append(resource_ref)
                            resource_scopes[json.dumps(resource_ref, sort_keys=True)] = event_ref
            result["resourceRefs"] = resources[:2]
            for index, reference in enumerate(resources[:2]):
                task(f"resource description {index + 1}", lambda ref=reference:
                     ctx.query("pix_gpu_resource", resourceRef=ref, viewLimit=3, bindingLimit=3))
                task(f"resource uses {index + 1}", lambda ref=reference:
                     ctx.query("pix_gpu_resource_uses", resourceRef=ref, limit=5,
                               **({"scope": resource_scopes[json.dumps(ref, sort_keys=True)]} if large_capture else {})))
            if large_capture:
                result["resourceNavigationScope"] = "Originating inspected event; whole-capture reverse indexing is exercised on the controlled fixture."
            if not resources:
                task("resource navigation availability", lambda: _missing("Inspected events did not return usable resource references."))

            inventory = task("shader inventory", lambda: _missing("Whole-capture shader indexing is exercised on the fixture; this large capture uses shaders returned by inspected pipelines.")
                             if large_capture else ctx.query("pix_gpu_shaders", handle=handle, limit=5), optional=True)
            for row in _items(inventory):
                if (row.get("shader") or {}).get("shaderRef"):
                    shaders.append(row["shader"])
            if shaders:
                # Prefer source when advertised, while always collecting missing-symbol evidence.
                shader = next((row for row in shaders if "HLSL" in row.get("availableCode", [])), shaders[0])
                ref = result["shaderRef"] = shader["shaderRef"]
                diagnostics = task("shader symbols and code diagnostics", lambda:
                                   ctx.query("pix_gpu_shader_diagnostics", shaderRef=ref), optional=True)
                result["shaderDiagnostics"] = diagnostics
                task("shader reverse navigation", lambda: _missing("Whole-capture reverse shader indexing is exercised on the fixture; event shader references remain available here.")
                     if large_capture else ctx.query("pix_gpu_shader_uses", shaderRef=ref, limit=5), optional=True)
                codes = [code for code in ("HLSL", "IL", "ISA") if code in shader.get("availableCode", [])]
                if codes:
                    code_type = codes[0]
                    source = task("shader source window", lambda: ctx.query("pix_gpu_shader_code", shaderRef=ref,
                                  codeType=code_type, lineCount=20, nodeLimit=5), optional=True)
                    if source and source.get("nextStartLine"):
                        task("shader source continuation", lambda: ctx.query("pix_gpu_shader_code", shaderRef=ref,
                             codeType=code_type, startLine=source["nextStartLine"], lineCount=10, nodeLimit=5), optional=True)
                    text = (source or {}).get("code") or ""
                    token = next(iter(re.findall(r"[A-Za-z_][A-Za-z_0-9]{2,}", text)), "float")
                    task("shader source search", lambda: ctx.query("pix_gpu_shader_search", shaderRef=ref,
                         codeType=code_type, nodeIndex=0, query=token, contextLines=1, limit=5), optional=True)
                else:
                    task("shader source availability", lambda: _missing("Selected shader advertises no HLSL, IL or ISA code nodes.", True), optional=True)
            else:
                task("shader navigation availability", lambda: _missing("Neither event inspection nor shader inventory returned usable shader references."))

            counters = task("advertised hardware counters", lambda: ctx.query("pix_gpu_counters_list", handle=handle, limit=100), optional=True)
            counter_rows = _items(counters)
            if counter_rows:
                keywords = ("busy", "stall", "bandwidth", "throughput", "occupancy", "duration")
                counter_rows.sort(key=lambda row: (not any(word in row.get("name", "").lower() for word in keywords), row["id"]))
                counter_ids = [row["id"] for row in counter_rows[:3]]
                result["selectedCounters"] = counter_rows[:3]
                collected = task("collect small hardware counter set", lambda: _snapshot_job(ctx,
                                 f"{label}-counters", "pix_gpu_counters_start", handle=handle, counterIds=counter_ids), optional=True)
                if collected:
                    queue_index = draws[0]["eventRef"]["queueIndex"] if draws else opened["queues"][0]["queueIndex"]
                    task("counter values and numeric ordering", lambda: ctx.query("pix_gpu_counters_collect", handle=handle,
                         counterIds=counter_ids, queueIndex=queue_index, kind="drawOrDispatch", limit=10,
                         orderByCounterId=counter_ids[0]), optional=True)
            elif counters is not None:
                task("counter collection availability", lambda: _missing("No hardware counters were advertised for this capture and adapter.", True), optional=True)
            else:
                task("counter collection availability", lambda: _missing("Counter catalog query failed; hardware support was not established."), optional=True)
            task("occupancy samples", lambda: ctx.query("pix_gpu_occupancy", handle=handle, maxPoints=25), optional=True)
            hf = task("high-frequency counter catalog", lambda: ctx.query("pix_gpu_hf_counters", handle=handle, maxSamples=25), optional=True)
            if hf and hf.get("sets"):
                task("high-frequency counter samples", lambda: ctx.query("pix_gpu_hf_counters", handle=handle,
                     setIndex=hf["sets"][0]["index"], maxSamples=25), optional=True)
            if draws:
                ref = draws[0]["eventRef"]
                task("targeted shader profiling", lambda: _snapshot_job(ctx, f"{label}-shader-profile",
                     "pix_gpu_shader_profile", firstEventRef=ref), optional=True)
                experiments = task("Dr. PIX experiment catalog", lambda: ctx.query("pix_gpu_drpix_experiments", handle=handle), optional=True)
                if _items(experiments):
                    event = task("Dr. PIX target event", lambda: ctx.query("pix_gpu_event", eventRef=ref, maxChildren=1))
                    gpu_ids = _find_gpu_ids(event)
                    if gpu_ids:
                        experiment = _items(experiments)[0]
                        task("targeted Dr. PIX experiment", lambda: _snapshot_job(ctx, f"{label}-drpix",
                             "pix_gpu_drpix_run", handle=handle, experiments=[experiment["guid"]],
                             firstEventGpuId=gpu_ids[0], lastEventGpuId=gpu_ids[0]), optional=True)
                    else:
                        task("Dr. PIX range availability", lambda: _missing("Selected draw has no usable GPU event ID."), optional=True)
            result["finalAnalysis"] = task("final native capabilities", lambda: ctx.query("pix_gpu_info", handle=handle))
        else:
            task("native analysis coverage", lambda: _missing("Requested replay adapter could not be started; native analyses require it."))

        screenshot = task("embedded screenshot", lambda: ctx.query("pix_gpu_screenshot", handle=handle,
                          outPath=str(ctx.artifacts / f"{_safe(label)}-screenshot.png")), optional=True)
        screenshot = _metadata(screenshot)
        result["screenshot"] = screenshot
        if screenshot.get("artifactRef"):
            result["screenshotImage"] = task("embedded image retrieval and PNG verification", lambda:
                _retrieve_image(ctx, screenshot["artifactRef"], f"{label}-screenshot-inline", screenshot.get("path")), optional=True)
        stopped = task("disconnect all analyses before CLI", lambda: _stop_all(ctx))
        result["cliProvenance"] = "pixtool defaults; native replay adapter and power settings do not apply."
        if stopped is not None:
            preview = task("CLI replay preview", lambda: ctx.query("pix_gpu_preview", handle=handle, timeoutSeconds=600), optional=True)
            result["preview"] = preview
            if preview and preview.get("artifactRef"):
                result["previewImage"] = task("preview image retrieval and PNG verification", lambda:
                    _retrieve_image(ctx, preview["artifactRef"], f"{label}-preview", ignore_alpha=True), optional=True)
            output = ctx.artifacts / f"{_safe(label)}-cpp-{uuid.uuid4().hex[:8]}"
            result["cppExport"] = task("CLI C++ project export", lambda: ctx.query("pix_gpu_export_cpp",
                                       handle=handle, outputDirectory=str(output), timeoutSeconds=1800), optional=True)
    finally:
        task("leave capture open with replay stopped", lambda: ctx.query("pix_gpu_analysis_stop", handle=handle))
        ctx.save(f"gpu-{_safe(label)}", result)
    return result


def _find_gpu_ids(value):
    if isinstance(value, dict):
        if value.get("gpuId") is not None and int(value["gpuId"]) != 0xffffffff:
            return [int(value["gpuId"])]
        # Event details also include parents/children; choose only the selected event.
        if isinstance(value.get("event"), dict):
            return _find_gpu_ids(value["event"])
    return []


def compare_gpu(ctx, baseline_handle, candidate_handle):
    """Compare native replay results; retain and page the immutable full snapshot."""
    task = lambda name, work: ctx.task(f"GPU comparison: {name}", work)
    result = {"baselineHandle": baseline_handle, "candidateHandle": candidate_handle}
    try:
        baseline = task("baseline metadata", lambda: ctx.query("pix_gpu_info", handle=baseline_handle))
        candidate = task("candidate metadata", lambda: ctx.query("pix_gpu_info", handle=candidate_handle))
        if not baseline or not candidate:
            return {**result, "status": "untested", "reason": "Both open capture metadata records are required."}
        sections = ["timings", "shaders", "pipeline", "resources"]
        if baseline.get("totalEvents", 0) + candidate.get("totalEvents", 0) > 2000:
            sections = ["timings"]
            ctx.limitation("Large-capture comparison uses timings only; pipeline, shader and resource inspection is sampled separately.",
                           {"baselineEvents": baseline.get("totalEvents"), "candidateEvents": candidate.get("totalEvents")})
        result["sections"] = sections
        comparison = task("capture differences", lambda: ctx.query("pix_gpu_compare", baselineHandle=baseline_handle,
                          candidateHandle=candidate_handle, sections=sections, limit=10))
        result["comparison"] = comparison
        if comparison and comparison.get("fullResultRef"):
            reference = comparison["fullResultRef"]
            result["fullResultRef"] = reference
            result["export"] = task("export complete saved comparison", lambda: ctx.query("pix_result_export",
                                    resultRef=reference, outPath=str(ctx.report_dir / f"gpu-comparison-{uuid.uuid4().hex[:8]}.json")))
            args = {"fullResultRef": reference, "direction": "regressions", "sortBy": "deltaNs", "limit": 5}
            page = task("largest positive replay deltas", lambda: ctx.query("pix_gpu_compare_changes", **args))
            if page:
                _require(all(float(row["deltaNs"]) > 0 for row in _items(page)), "Regression filtering returned a non-positive delta")
                result["regressions"] = page
                task("saved comparison continuation", lambda: _continuation(ctx, "pix_gpu_compare_changes", args, page))
            task("saved ambiguity evidence", lambda: ctx.query("pix_result_read", resultRef=reference, pointer="/ambiguous", limit=5))
    finally:
        for handle in (baseline_handle, candidate_handle):
            task(f"stop replay {handle}", lambda h=handle: ctx.query("pix_gpu_analysis_stop", handle=h))
        ctx.save("gpu-comparison", result)
    return result
