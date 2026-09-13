"""Exercise the PIX tutorial against real UE workloads through MCP stdio.

Run from the repository root with the published server and a built CitySample.
Artifacts are never overwritten across sessions; --session resumes a named session.
GPU/CPU workloads run serially. Only processes launched by this runner are terminated.
"""
import argparse
import base64
import ctypes
import hashlib
import json
from pathlib import Path
import re
import shutil
import statistics
import sys
import time
import uuid

from benchmark import Investigator
from smoke import Client, SmokeError


ROOT = Path(__file__).resolve().parents[1]
UE = Path('W:/UE5/UnrealEngine')
ADAPTERS = {'intel': 'Intel(R) Arc(TM) B580', 'nvidia': 'NVIDIA GeForce RTX 4070 Ti'}


def slug(value):
    return re.sub(r'[^a-zA-Z0-9_.-]+', '-', str(value)).strip('-')[:140]


def alive(pid):
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.OpenProcess.restype = ctypes.c_void_p
    kernel.OpenProcess.argtypes = [ctypes.c_ulong, ctypes.c_int, ctypes.c_ulong]
    kernel.GetExitCodeProcess.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_ulong)]
    kernel.CloseHandle.argtypes = [ctypes.c_void_p]
    handle = kernel.OpenProcess(0x1000, False, pid)
    if not handle:
        return False
    try:
        code = ctypes.c_ulong()
        if not kernel.GetExitCodeProcess(handle, ctypes.byref(code)):
            raise ctypes.WinError(ctypes.get_last_error())
        return code.value == 259
    finally:
        kernel.CloseHandle(handle)


class RecordedClient(Client):
    def __init__(self, command, folder):
        self.folder = folder
        self.transcript = (folder / 'protocol.jsonl').open('a', encoding='utf-8')
        self.last_result_ref = None
        self.image_count = 0
        super().__init__(command, timeout=180, shutdown_timeout=15)

    def send(self, method, params=None, notify=False):
        self.transcript.write(json.dumps({'time': time.time(), 'request': {'method': method, 'params': params}}, ensure_ascii=False) + '\n')
        self.transcript.flush()
        result = super().send(method, params, notify)
        if result is not None:
            self.transcript.write(json.dumps({'time': time.time(), 'response': result}, ensure_ascii=False) + '\n')
            self.transcript.flush()
        if method == 'tools/call' and result:
            payload = result['result']
            texts = [b['text'] for b in payload.get('content', []) if b.get('type') == 'text']
            if 'structuredContent' in payload:
                if not texts or json.dumps(json.loads(texts[0]), sort_keys=True) != json.dumps(payload['structuredContent'], sort_keys=True):
                    raise SmokeError(f"{params['name']}: text and structuredContent differ")
            for block in payload.get('content', []):
                if block.get('type') == 'image':
                    data = base64.b64decode(block['data'], validate=True)
                    self.image_count += 1
                    (self.folder / f'image-{self.image_count:03d}.png').write_bytes(data)
            if texts:
                try:
                    value = json.loads(texts[0])
                    if isinstance(value, dict) and value.get('resultRef'):
                        self.last_result_ref = value['resultRef']
                except ValueError:
                    pass
        return result

    def close(self):
        try:
            return super().close()
        finally:
            (self.folder / 'server-stderr.log').write_text('\n'.join(self.stderr_lines), encoding='utf-8')
            self.transcript.close()


class TutorialInvestigator(Investigator):
    def wait(self, job):
        deadline = time.monotonic() + 1900
        while time.monotonic() < deadline:
            if job.get('status') in {'succeeded', 'failed', 'cancelled'}:
                if job['status'] != 'succeeded':
                    raise SmokeError(f'Job failed: {job}')
                return job
            print(time.strftime('%H:%M:%S'), 'Waiting:', job['jobId'], job.get('kind'), job.get('status'), flush=True)
            job = self.call('pix_job_wait', jobId=job['jobId'], timeoutSeconds=30)
        raise SmokeError(f"Job {job['jobId']} exceeded the investigation wait budget; native work may still be running")


class Context:
    def __init__(self, args):
        self.args = args
        self.artifacts = Path(args.artifact_root).resolve() / args.session
        self.report_dir = ROOT / 'tests' / 'artifacts' / args.session
        self.artifacts.mkdir(parents=True, exist_ok=True)
        self.report_dir.mkdir(parents=True, exist_ok=True)
        self.report_path = self.report_dir / 'report.json'
        self.report = json.loads(self.report_path.read_text(encoding='utf-8')) if self.report_path.exists() else {
            'session': args.session, 'artifacts': str(self.artifacts), 'tasks': [], 'limitations': [],
            'csvRuns': {}, 'gpuCaptures': {}, 'timingCaptures': [],
            'scope': 'MCP tutorial validation; GPUView and generated-C++ build/run excluded',
            'measurementNote': 'CSV recordings and GPU replay have separate provenance. Warmup is excluded. Native unsupported features are reported explicitly.'}
        profile = 'tsr-serial-translation' if getattr(args, 'serial_translation', False) else 'standard-tsr'
        if self.report.get('profile', 'standard-tsr' if self.report_path.exists() else profile) != profile:
            raise SmokeError('Use a separate session for the serial-translation diagnostic profile; baseline measurements must not be mixed.')
        self.report['profile'] = profile
        rpc_folder = self.report_dir / f'rpc-{time.strftime("%H%M%S")}-{args.stage}'
        rpc_folder.mkdir()
        self.client = RecordedClient([str(Path(args.server).resolve())], rpc_folder)
        self.agent = TutorialInvestigator(self.client)
        self.connections = set()
        self.recording = None
        try:
            self.client.send('initialize', {'protocolVersion': '2025-06-18', 'capabilities': {}, 'clientInfo': {'name': 'pixmcp-tutorial', 'version': '1'}})
            self.client.send('notifications/initialized', notify=True)
            self.report['toolSchemas'] = self.save('tool-schemas', self.client.send('tools/list')['result'])
            self.report['serverSha256'] = hashlib.sha256(Path(args.server).read_bytes()).hexdigest()
            self.task('Server preflight', lambda: self.query('pix_info', probe=True))
        except BaseException:
            self.client.close()
            raise

    @property
    def last_job_result_ref(self):
        return self.client.last_result_ref

    def log(self, message):
        print(time.strftime('%H:%M:%S'), message, flush=True)

    def save(self, name, value):
        path = self.report_dir / f'{slug(name)}-{uuid.uuid4().hex}.json'
        with path.open('x', encoding='utf-8') as output:
            json.dump(value, output, indent=2, ensure_ascii=False)
        return str(path)

    def persist(self):
        self.report['currentCalls'] = self.agent.calls
        self.report_path.write_text(json.dumps(self.report, indent=2, ensure_ascii=False), encoding='utf-8')
        lines = ['# PIX tutorial validation', '', f"Session: `{self.args.session}`", '', '| Check | Status | Evidence / reason |', '|---|---|---|']
        for item in self.report['tasks']:
            evidence = item.get('error') or item.get('reason') or item.get('evidence', '')
            lines.append(f"| {item['name']} | {item['status']} | {str(evidence).replace('|', '/').replace(chr(10), ' ')} |")
        lines += ['', '## Limitations', ''] + ['- ' + str(x) for x in self.report['limitations']]
        (self.report_dir / 'report.md').write_text('\n'.join(lines) + '\n', encoding='utf-8')

    def limitation(self, message, evidence=None):
        self.report['limitations'].append({'message': message, 'evidence': evidence} if evidence else message)
        self.persist()

    def call(self, tool, **arguments):
        self.log(tool)
        try:
            return self.agent.call(tool, **arguments)
        finally:
            self.persist()

    def query(self, tool, **arguments):
        self.log(tool)
        try:
            return self.agent.query(tool, **arguments)
        finally:
            self.persist()

    def task(self, name, work, optional=False):
        entry = {'name': name, 'status': 'running', 'started': time.time()}
        self.report['tasks'].append(entry)
        child_start = len(self.report['tasks'])
        self.persist()
        self.log(name)
        try:
            value = work()
            entry['status'] = 'passed'
            if isinstance(value, dict):
                if isinstance(value.get('status'), str) and value['status'] in {'untested', 'unsupported', 'failed'}:
                    entry['status'] = value['status']
                elif value.get('unavailable') is True:
                    entry['status'] = 'unsupported'
                if value.get('reason'):
                    entry['reason'] = value['reason']
            entry['evidence'] = self.save(name, value)
            failures = [t['name'] for t in self.report['tasks'][child_start:] if t['status'] == 'failed' and not t.get('resolvedBy')]
            if failures:
                entry['status'] = 'failed'
                entry['reason'] = 'Failed child checks: ' + ', '.join(failures)
            return value
        except Exception as error:
            entry['status'] = 'unsupported' if optional and ('unsupported_feature' in str(error) or 'timing_schema_unsupported' in str(error) or 'not supported' in str(error).lower()) else 'failed'
            entry['error'] = f'{type(error).__name__}: {error}'
            self.log(entry['error'])
            return None
        finally:
            entry['seconds'] = time.time() - entry['started']
            self.persist()

    def connect(self):
        handle = self.query('pix_device_connect')['handle']
        self.connections.add(handle)
        self.save('device-' + handle, self.query('pix_device_info', handle=handle))
        return handle

    def detach(self, handle):
        # Each connection belongs to one run and only tracks its launched process.
        self.query('pix_device_detach', handle=handle, terminate=True)
        self.query('pix_close', handle=handle)
        self.connections.discard(handle)

    def close(self):
        if self.recording:
            self.task('Cleanup active timing recording', lambda: self.query('pix_device_timing_capture_stop', handle=self.recording, open=False, waitSeconds=0))
        for handle in list(self.connections):
            self.task('Cleanup ' + handle, lambda h=handle: self.detach(h))
        self.task('Close all handles', lambda: self.query('pix_close_all'))
        code = self.client.close()
        self.report['serverExitCode'] = code
        self.report['tasks'].append({'name': 'Server shutdown', 'status': 'passed' if code == 0 else 'failed', 'exitCode': code})
        self.persist()


def launch_arguments(adapter, folder, mode, screen=100):
    settings = {'r.Quinlight.Enabled': 0, 'r.AntiAliasingMethod': 4, 'r.ScreenPercentage': screen, 'r.DynamicRes.OperationMode': 0, 'r.VSync': 0}
    parts = [f'"{UE / "CitySample/CitySample.uproject"}"', '-game', '-d3d12', '-windowed', '-ForceRes', '-ResX=2560', '-ResY=1440', '-NoVSync',
             '-graphicsadapter=-1', '-preferIntel' if adapter == 'intel' else '-preferNvidia', '-fixedseed', '-DisableSandboxIntro', '-statnamedevents',
             '-unattended', '-nosplash', f'-abslog="{folder / "Unreal.log"}"']
    parts += [f'-ini:Engine:[ConsoleVariables]:{key}={value}' for key, value in settings.items()]
    commands = [f'{key} {value}' for key, value in settings.items()] + ['t.MaxFPS 0']
    if mode == 'csv':
        parts += ['-csvGpuStats', '-gauntlet=AutomatedSequencePerfTest', '-AutomatedPerfTest.SequencePerfTest.MapSequenceName=APT_DetFlyby1',
                  '-AutomatedPerfTest.DoCSVProfiler', f'-AutomatedPerfTest.TestID={folder.name}', f'-AutomatedPerfTest.ArtifactOutputPath="{folder}"',
                  '-logcmds="LogAutomatedPerfTest Verbose"',
                  '-ini:Engine:[/Script/AutomatedPerfTesting.AutomatedSequencePerfTestProjectSettings]:SequenceStartDelay=60']
    elif mode == 'timing':
        parts += ['-PIX']
    elif mode == 'gpu':
        commands += ['r.RHISetGPUCaptureOptions 1']
    parts.append('-ExecCmds="' + ','.join(commands) + '"')
    return ' '.join(parts)


def read_log(path):
    return path.read_text(encoding='utf-8', errors='replace') if path.exists() else ''


def pending_compilation(log):
    counts = {}
    for line in log.splitlines():
        if not re.search(r'Log(ShaderCompilers|AssetCompilingManager|StaticMesh):', line, re.I):
            continue
        if re.search(r'LogShaderCompilers:\s*(?:Display:\s*)?(?:All shaders (?:compiled|finished)|Shader compilation (?:complete|completed|finished))\b', line, re.I):
            counts = {kind: count for kind, count in counts.items() if not kind.startswith(('shaders', 'jobs'))}
        worker = re.search(r'Worker\s*\((\d+/\d+)\)', line, re.I)
        suffix = f' (worker {worker[1]})' if worker else ''
        for match in re.finditer(r'\b([\d,]+)\s+(shaders|assets|jobs|meshes)\s+(?:remaining|left)', line, re.I):
            counts[match[2].lower() + suffix] = int(match[1].replace(',', ''))
        for match in re.finditer(r'(shaders|assets|jobs|meshes)\s+(?:remaining|left)(?:\s+to\s+compile)?\s*(?:[:=]\s*)?([\d,]+)', line, re.I):
            counts[match[1].lower() + suffix] = int(match[2].replace(',', ''))
    return {kind: count for kind, count in counts.items() if count > 0}


def validate_adapter(log, adapter):
    # Enumerated adapters also appear in logs; only the chosen RHI device establishes identity.
    lines = [line for line in log.splitlines() if re.search(r'(Chosen D3D12 Adapter|RHI Adapter Info|Adapter Name:|RHI Name:)', line, re.I)]
    chosen = re.findall(r'Chosen D3D12 Adapter Id\s*=\s*(\d+)', log, re.I)
    if chosen:
        enumerated = [line for line in log.splitlines() if re.search(r'Found D3D12 adapter\s+' + re.escape(chosen[-1]) + r':', line, re.I)]
        selection = [line for line in log.splitlines() if re.search(r'Chosen D3D12 Adapter Id\s*=', line, re.I)]
        lines = enumerated[-1:] + selection[-1:]
    expected = 'B580' if adapter == 'intel' else '4070 Ti'
    if not any(expected.lower() in line.lower() for line in lines):
        raise SmokeError(f'Fresh chosen-RHI adapter evidence does not establish {expected}: {lines}')
    return lines


def validate_csv_log(log, adapter):
    if re.search(r'(Fatal error:|=== Critical error: ===|Assertion failed:)', log, re.I):
        raise SmokeError('UE reported a fatal error; this CSV cannot establish a successful run')
    evidence = validate_adapter(log, adapter)
    start = log.find('RunTest::Valid Sequence Player')
    end = log.find('AutomatedSequencePerfTest::TeardownTest', start)
    if start < 0 or end < 0:
        raise SmokeError('Flyby start/end evidence missing')
    pending_at_start = pending_compilation(log[:start])
    compilation = ['Recorded compilation still pending at flyby start: ' + json.dumps(pending_at_start)] if pending_at_start else []
    for line in log[start:end].splitlines():
        if re.search(r'Log(Material|ShaderCompilers|AssetCompilingManager|StaticMesh):', line, re.I):
            active = re.search(r'(compiling|Built static mesh|NaniteBuild)', line, re.I)
            pending = re.search(r'\b([1-9]\d*)\s+(?:shaders|assets|jobs)\s+(?:left|remaining)', line, re.I)
            if active or pending:
                compilation.append(line)
    return {'adapterEvidence': evidence, 'compilationOverlap': compilation,
            'pendingCompilationAtStart': pending_at_start, 'validMeasurement': not compilation}


def csv_settings_evidence(log, screen):
    """Verify direct startup echoes separately from the recorded CSV VSync metadata."""
    start = log.find('RunTest::Valid Sequence Player')
    end = log.find('AutomatedSequencePerfTest::TeardownTest', start)
    expected = {'r.Quinlight.Enabled': 0, 'r.AntiAliasingMethod': 4, 'r.ScreenPercentage': screen,
                'r.DynamicRes.OperationMode': 0, 'r.VSync': 0, 't.MaxFPS': 0}
    observed, echoes, mismatches = {}, {}, []
    startup = log[:start] if start >= 0 else ''
    for key, wanted in expected.items():
        pattern = r'^\s*(?:\[[^\]\r\n]*\]\s*)*' + re.escape(key) + r'\s*=\s*"([^"\r\n]*)"[^\S\r\n]*$'
        matches = list(re.finditer(pattern, startup, re.M))
        raw = matches[-1][1] if matches else None
        if matches:
            echoes[key] = matches[-1][0].strip()
        try:
            observed[key] = float(raw) if raw is not None else None
        except ValueError:
            observed[key] = raw
        if observed[key] != wanted:
            mismatches.append(f'{key}: expected {wanted}, startup echo {raw!r}')
    phase = log[start:end] if start >= 0 and end >= start else ''
    metadata = re.findall(r'LogCsvProfiler:[^\r\n]*Metadata set\s*:\s*vsyncenabled="(\d+)"', phase)
    vsync = int(metadata[-1]) if metadata else None
    if vsync != 0:
        mismatches.append(f'CSV vsyncenabled: expected 0, recorded {vsync!r}')
    return {'verified': not mismatches, 'expectedValues': expected, 'startupValues': observed,
            'startupEchoes': echoes, 'csvVsyncEnabled': vsync, 'mismatches': mismatches,
            'scope': 'Latest direct console echoes before flyby start, plus CSV VSync metadata. Startup echoes do not verify settings on every recorded frame.'}


def csv_result(ctx, adapter, folder, screen=100):
    log = read_log(folder / 'Unreal.log')
    evidence = validate_csv_log(log, adapter)
    resolutions = {key: re.findall(r'Metadata set\s*:\s*' + key + r'="(\d+)"', log) for key in ('resx', 'resy')}
    resolution = {key: int(values[-1]) if values else None for key, values in resolutions.items()}
    evidence['recordedResolution'] = resolution
    is_warmup = folder.name.endswith('-warmup')
    if not is_warmup and resolution != {'resx': 2560, 'resy': 1440}:
        raise SmokeError(f'CSV render resolution differs from required 2560x1440: {resolution}')
    evidence['settingsEvidence'] = csv_settings_evidence(log, screen)
    if not is_warmup and not evidence['settingsEvidence']['verified']:
        raise SmokeError('CSV settings verification failed: ' + '; '.join(evidence['settingsEvidence']['mismatches']))
    files = list((folder / 'CSV').glob('*.csv'))
    if len(files) != 1 or files[0].stat().st_size == 0:
        raise SmokeError(f'Expected one finalized flyby CSV, found {files}; see {folder / "Unreal.log"}')
    comparison = ctx.query('pix_csv_compare', baselinePath=str(files[0]), candidatePath=str(files[0]), stat='median')
    if comparison['candidate']['frames'] <= 0 or not comparison['items']:
        raise SmokeError('CSV contains no measured GPU passes')
    value = {'path': str(files[0]), 'log': str(folder / 'Unreal.log'), 'screenPercentage': screen,
             'frames': comparison['candidate']['frames'], **evidence,
             'gpu': comparison, 'gpuResultRef': ctx.last_job_result_ref}
    if is_warmup:
        value.update(validMeasurement=False, reason='Discarded warmup; only functional capture coverage is evaluated.')
    elif not evidence['validMeasurement']:
        value['status'] = 'failed'
        value['reason'] = 'Compilation overlaps measured flyby; excluded from performance comparison.'
    return value


def launch(ctx, adapter, label, mode, screen=100):
    folder = ctx.artifacts / label
    folder.mkdir(exist_ok=False)
    handle = ctx.connect()
    arguments = launch_arguments(adapter, folder, mode, screen)
    if getattr(ctx.args, 'serial_translation', False):
        arguments += ' -ini:Engine:[ConsoleVariables]:r.RHICmd.ParallelTranslate.Enable=0'
    ctx.save(label + '-launch', {'exe': str(UE / 'Engine/Binaries/Win64/UnrealEditor.exe'), 'arguments': arguments, 'underGpuCapture': mode == 'gpu'})
    result = ctx.query('pix_device_launch', handle=handle, exePath=str(UE / 'Engine/Binaries/Win64/UnrealEditor.exe'), arguments=arguments, underGpuCapture=mode == 'gpu')
    if not result.get('processId') or not result.get('capturable'):
        raise SmokeError(f'UE launch unsuccessful: {result}')
    ctx.report['activeRun'] = {'label': label, 'pid': result['processId'], 'connection': handle, 'log': str(folder / 'Unreal.log')}
    ctx.persist()
    return handle, result['processId'], folder


def wait_scene(ctx, pid, folder, adapter):
    deadline = time.monotonic() + ctx.args.startup_seconds
    stable_since = None
    previous_compile = ''
    progress = 0
    path = folder / 'Unreal.log'
    while time.monotonic() < deadline:
        log = read_log(path)
        if not alive(pid) or 'Fatal error:' in log:
            raise SmokeError(f'UE exited or failed during startup; see {path}')
        world_ready = 'Bringing World' in log and 'up for play' in log
        compile_lines = [line for line in log.splitlines() if re.search(r'(Compil(e|ing|ed)|ShaderCompileWorker|Remaining.*shader)', line, re.I)]
        current_compile = compile_lines[-1] if compile_lines else ''
        pending = pending_compilation(log)
        if not world_ready or pending or current_compile != previous_compile:
            stable_since = None
        elif stable_since is None:
            stable_since = time.monotonic()
        previous_compile = current_compile
        if stable_since and time.monotonic() - stable_since >= 60:
            return {'adapterEvidence': validate_adapter(log, adapter), 'quietSeconds': 60, 'lastCompileLog': current_compile}
        if time.monotonic() - progress >= 30:
            ctx.log(f'Waiting for {folder.name}: worldReady={world_ready}, pending={pending}, lastCompile={current_compile[-180:]}')
            progress = time.monotonic()
        time.sleep(2)
    raise SmokeError(f'No warmed playable scene within {ctx.args.startup_seconds}s; see {path}')


def csv_run(ctx, adapter, label, screen=100):
    handle = None
    try:
        handle, pid, folder = launch(ctx, adapter, label, 'csv', screen)
        return wait_csv(ctx, pid, folder, adapter, screen)
    finally:
        if handle:
            ctx.detach(handle)
        ctx.report.pop('activeRun', None)
        ctx.persist()


def wait_csv(ctx, pid, folder, adapter, screen=100):
    deadline = time.monotonic() + ctx.args.startup_seconds
    capture_started = False
    last_progress = 0
    while alive(pid):
        log = read_log(folder / 'Unreal.log')
        if 'Fatal error:' in log:
            raise SmokeError(f'UE fatal error; see {folder / "Unreal.log"}')
        if not capture_started and ('RunTest::Valid Sequence Player' in log or 'CSV capture started' in log):
            capture_started = True
            deadline = time.monotonic() + 900
        if time.monotonic() >= deadline:
            raise SmokeError(f'CSV {"completion" if capture_started else "startup"} deadline exceeded; see {folder / "Unreal.log"}')
        if time.monotonic() - last_progress > 30:
            ctx.log(f'{folder.name}: {"recording flyby" if capture_started else "loading/warming"}; log bytes={len(log)}')
            last_progress = time.monotonic()
        time.sleep(2)
    return csv_result(ctx, adapter, folder, screen)


def run_csv(ctx):
    adapters = [ctx.args.adapter] if ctx.args.adapter != 'both' else ['intel', 'nvidia']
    for adapter in adapters:
        runs = ctx.report['csvRuns'].setdefault(adapter, [])
        if not runs:
            warmup = ctx.artifacts / (adapter + '-warmup')
            if warmup.exists():
                ctx.task(f'{adapter} recovered CSV warmup', lambda a=adapter, f=warmup: csv_result(ctx, a, f))
            else:
                ctx.task(f'{adapter} CSV warmup', lambda a=adapter: csv_run(ctx, a, a + '-warmup'))
        for index in range(1, ctx.args.runs + 1):
            if any(run.get('run') == index and run.get('validMeasurement') for run in runs):
                continue
            for attempt in range(1, 3):
                label = f'{adapter}-run{index}-attempt{attempt}'
                if (ctx.artifacts / label).exists():
                    value = ctx.task(label + ' recovered', lambda a=adapter, l=label: csv_result(ctx, a, ctx.artifacts / l))
                else:
                    value = ctx.task(label, lambda a=adapter, l=label: csv_run(ctx, a, l))
                if value:
                    value['run'] = index
                    runs.append(value)
                if value and value.get('validMeasurement'):
                    for task in ctx.report['tasks']:
                        if task['status'] == 'failed' and task['name'].startswith(f'{adapter}-run{index}-attempt'):
                            task['resolvedBy'] = label
                    break
            ctx.persist()
    if ctx.args.adapter in {'both', 'intel'} and not (ctx.artifacts / 'intel-screen50').exists():
        ctx.report['screen50'] = ctx.task('Intel 50 percent cross-check', lambda: csv_run(ctx, 'intel', 'intel-screen50', 50))
    elif ctx.args.adapter in {'both', 'intel'} and not ctx.report.get('screen50'):
        ctx.report['screen50'] = ctx.task('Recovered Intel 50 percent cross-check', lambda: csv_result(ctx, 'intel', ctx.artifacts / 'intel-screen50', 50))
    compare_csv(ctx)


def compare_csv(ctx):
    good = {a: [r for r in ctx.report['csvRuns'].get(a, []) if r.get('validMeasurement')] for a in ADAPTERS}
    comparisons = []
    for intel in good['intel']:
        nvidia = next((r for r in good['nvidia'] if r['run'] == intel['run']), None)
        if not nvidia:
            continue
        value = ctx.task(f'CSV comparison run {intel["run"]}', lambda: ctx.query('pix_csv_compare', baselinePath=nvidia['path'], candidatePath=intel['path'], stat='median'))
        if value:
            value['resultRef'] = ctx.last_job_result_ref
            comparisons.append(value)
    grouped = {}
    for comparison in comparisons:
        for row in comparison['items']:
            if row.get('deltaMs') is not None:
                grouped.setdefault(row['name'], []).append(row['deltaMs'])
    ranked = sorted(({'name': name, 'medianDeltaMs': statistics.median(values), 'minDeltaMs': min(values), 'maxDeltaMs': max(values), 'runs': len(values)}
                     for name, values in grouped.items()), key=lambda row: row['medianDeltaMs'], reverse=True)
    ctx.report['csvComparisons'] = comparisons
    ctx.report['rankedCsvPasses'] = ranked
    frame_metrics = {}
    for adapter, runs in good.items():
        if runs:
            metrics = frame_metrics[adapter] = {}
            for prefix in ['FrameTime', 'GPUTime', 'GameThreadTime', 'RenderThreadTime', 'RHIThreadTime']:
                value = ctx.task(f'{adapter} {prefix}', lambda p=prefix, r=runs[0]: ctx.query('pix_csv_compare', baselinePath=r['path'], candidatePath=r['path'], prefix=p, stat='median'), optional=True)
                if value:
                    row = next((row for row in value['items'] if row['name'] == prefix), None)
                    if row:
                        metrics[prefix] = row['candidateMs']
                    else:
                        ctx.limitation(f'{adapter}: exact {prefix} CSV statistic is unavailable.')
            metrics['sourcePath'] = runs[0]['path']
            metrics['note'] = 'Recorded per-frame median milliseconds. GPUTime is UE RHI GPU frame time; no summed pass durations or GPUView utilization are substituted.'
    ctx.report['frameMetrics'] = frame_metrics
    ctx.persist()


def capture_gpu(ctx, adapter):
    label = adapter + '-gpu-' + time.strftime('%H%M%S')
    handle = None
    try:
        handle, pid, folder = launch(ctx, adapter, label, 'gpu')
        ready = wait_scene(ctx, pid, folder, adapter)
        captured = ctx.query('pix_device_take_gpu_capture', handle=handle, processId=pid, open=False, readinessTimeoutSeconds=300, delaySeconds=2, waitSeconds=0)
        destination = folder / 'frame.wpix'
        shutil.copyfile(captured['path'], destination)
        return {'path': str(destination), 'source': captured['path'], 'bytes': destination.stat().st_size, 'ready': ready, 'adapter': adapter}
    finally:
        if handle:
            ctx.detach(handle)
        ctx.report.pop('activeRun', None)


def run_gpu(ctx):
    from tutorial_gpu import run_gpu as investigate, compare_gpu
    adapters = [ctx.args.adapter] if ctx.args.adapter != 'both' else ['intel', 'nvidia']
    handles = {}
    for adapter in adapters:
        capture = ctx.report['gpuCaptures'].get(adapter)
        if not capture:
            capture = ctx.task(adapter + ' CitySample GPU capture', lambda a=adapter: capture_gpu(ctx, a))
            if capture:
                ctx.report['gpuCaptures'][adapter] = capture
                ctx.persist()
        if capture:
            value = ctx.task(adapter + ' GPU investigation', lambda c=capture, a=adapter: investigate(ctx, c['path'], 'B580' if a == 'intel' else '4070 Ti', a))
            if value and value.get('replay') and value.get('baselineVerified'):
                handles[adapter] = value
    if 'intel' in handles and 'nvidia' in handles:
        compare_gpu(ctx, handles['nvidia']['handle'], handles['intel']['handle'])
    intel_handle = (handles.get('intel') or {}).get('handle')
    if intel_handle and ctx.report.get('csvComparisons'):
        # References are process-local: regenerate the comparison in this server session.
        previous = ctx.report['csvComparisons'][0]
        ctx.query('pix_csv_compare', baselinePath=previous['baseline']['path'], candidatePath=previous['candidate']['path'], stat='median')
        reference = ctx.last_job_result_ref
        for row in [r for r in ctx.report['rankedCsvPasses'] if r['medianDeltaMs'] > 0][:5]:
            ctx.task('CSV marker candidates ' + row['name'], lambda r=row: ctx.query('pix_csv_pass_candidates', resultRef=reference, passName=r['name'], handle=intel_handle))


def capture_timing(ctx):
    handle = None
    start_attempted = False
    start_confirmed = False
    primary_error = None
    label = 'intel-timing-' + time.strftime('%H%M%S')
    try:
        handle, pid, folder = launch(ctx, 'intel', label, 'timing')
        ready = wait_scene(ctx, pid, folder, 'intel')
        path = folder / 'timing.wpix'
        # The synchronous native recorder startup previously needed 85 seconds.
        start_attempted = True
        settings = ctx.query('pix_device_timing_capture_start', handle=handle, outputPath=str(path), cpuSamples=True, cpuSampleStacks=True,
                             contextSwitches=True, contextSwitchStacks=True, captureSysmonCounters=True, pixEvents=True, gpuTiming=True, maxFileSizeMb=2048)
        start_confirmed = True
        ctx.recording = handle
        time.sleep(10)
        result = ctx.query('pix_device_timing_capture_stop', handle=handle, open=False, waitSeconds=0)
        ctx.recording = None
        return {'path': str(path), 'processId': pid, 'settings': settings, 'stop': result, 'ready': ready}
    except BaseException as error:
        primary_error = error
        raise
    finally:
        cleanup_errors = []
        if handle and start_attempted and not start_confirmed:
            try:
                info = ctx.query('pix_device_info', handle=handle)
                if info.get('timingCaptureInProgress'):
                    ctx.recording = handle
            except Exception as error:
                cleanup_errors.append({'operation': 'check timing recorder after uncertain start', 'error': str(error)})
        if handle and ctx.recording == handle:
            try:
                ctx.query('pix_device_timing_capture_stop', handle=handle, open=False, waitSeconds=0)
                ctx.recording = None
            except Exception as error:
                cleanup_errors.append({'operation': 'stop timing recorder', 'error': str(error)})
        if handle:
            try:
                ctx.detach(handle)
                if ctx.recording == handle:
                    ctx.recording = None  # Closing this connection also asks PIX to stop its recorder.
            except Exception as error:
                cleanup_errors.append({'operation': 'detach timing workload', 'error': str(error)})
        ctx.report.pop('activeRun', None)
        if cleanup_errors:
            ctx.report.setdefault('cleanupErrors', []).extend(cleanup_errors)
        ctx.persist()
        if cleanup_errors and primary_error is None:
            raise SmokeError('Timing cleanup failed: ' + json.dumps(cleanup_errors))


def run_timing(ctx):
    from tutorial_timing import run_timing as investigate
    capture = ctx.task('Intel CitySample timing recording', lambda: capture_timing(ctx))
    if capture:
        ctx.report['timingCaptures'].append(capture)
        ctx.task('Recorded timing investigation', lambda: investigate(ctx, capture['path'], capture['processId']))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--server', default=str(ROOT / 'dist/PixMcp.exe'))
    parser.add_argument('--session', default='tutorial-' + time.strftime('%Y%m%d-%H%M%S'))
    parser.add_argument('--artifact-root', default='E:/PixCaptures')
    parser.add_argument('--stage', choices=['all', 'csv', 'gpu', 'timing', 'dumps'], default='all')
    parser.add_argument('--adapter', choices=['both', 'intel', 'nvidia'], default='both')
    parser.add_argument('--runs', type=int, default=3)
    parser.add_argument('--startup-seconds', type=int, default=900)
    parser.add_argument('--serial-translation', action='store_true', help='Separate diagnostic profile for UE background-translation crashes; use a new session.')
    args = parser.parse_args()
    if slug(args.session) != args.session or not 1 <= args.runs <= 10 or not 60 <= args.startup_seconds <= 3600:
        parser.error('Use a simple session name, 1..10 runs and 60..3600 startup seconds.')
    previous_report = ROOT / 'tests' / 'artifacts' / args.session / 'report.json'
    if previous_report.exists():
        previous = json.loads(previous_report.read_text(encoding='utf-8'))
        active = previous.get('activeRun')
        if active and alive(active['pid']):
            parser.error(f"Previous workload PID {active['pid']} is still running; do not overlap session controllers.")
    ctx = Context(args)
    try:
        for stage, work in [('csv', run_csv), ('gpu', run_gpu), ('timing', run_timing)]:
            if args.stage in {'all', stage}:
                ctx.task(stage + ' stage', lambda w=work: w(ctx))
        if args.stage in {'all', 'dumps'}:
            from tutorial_timing import run_dumps
            ctx.task('DirectX dump stage', lambda: run_dumps(ctx))
        ctx.report['externalExercises'] = {'GPUView': 'untested: outside selected MCP scope', 'C++ build/run': 'untested: export is exercised through MCP'}
    finally:
        ctx.close()
    ctx.log('Report: ' + str(ctx.report_dir / 'report.md'))
    return 1 if any(t['status'] == 'failed' and not t.get('resolvedBy') for t in ctx.report['tasks']) else 0


if __name__ == '__main__':
    sys.exit(main())
