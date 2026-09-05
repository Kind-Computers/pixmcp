using System.ComponentModel;
using Microsoft.PIX;
using Microsoft.PIX.Extension;
using Microsoft.PIX.Extension.DeviceConnection;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using PixMcp.Pix.Handles;
using ConnDesc = Microsoft.PIX.Extension.DeviceConnection.PIX_CONNECTION_DESC;
using LaunchDesc = Microsoft.PIX.Extension.DeviceConnection.PIX_LAUNCH_PROCESS_DESC;

namespace PixMcp.Tools;

[McpServerToolType]
public static class DeviceTools
{
    [McpServerTool(Name = "pix_device_connect"), Description("Opens a connection to the local PIX device (this PC) for launching/attaching to D3D12 apps, taking GPU/timing captures and reading system monitor counters. Returns a device handle.")]
    public static Task<string> Connect(PixSession session)
        => Tools.Run(session, "pix_device_connect", () =>
        {
            ConnectionHandle? handle = null;
            var notifications = new DelegateConnectionNotifications();
            IPixConnectionDocument connection = session.Factory.OpenConnectionDocument<IPixConnectionDocument>(
                ConnDesc.CreateLocal(), notifications);
            handle = session.Register(new ConnectionHandle("local", connection, notifications));
            ConnectionHandle h = handle;
            notifications.OnAllTargetProcessesTerminated = () => h.Note("allTargetProcessesTerminated");
            notifications.OnDeviceCounterCollectionStarted = () => h.Note("counterCollectionStarted");
            notifications.OnDeviceCounterCollectionStopped = () => h.Note("counterCollectionStopped");
            notifications.OnDeviceCounterDescriptionsUpdated = () => h.Note("counterDescriptionsUpdated");
            notifications.OnNewGpuCaptureCompleted = (_, filename, _) => h.Note("gpuCaptureCompleted", new { filename });
            notifications.OnNewTimingCaptureError = (hr, message) => h.Note("timingCaptureError", new { hresult = PixErrors.Hex(hr), message });
            notifications.OnNotifyGpuCaptureTargetProcessesStatus = statuses => h.Note("targetProcessesStatus",
                statuses.Select(s => new { processId = s.ProcessId, name = Interop.W(s.ProcessName), unsupportedReason = s.UnsupportedReason }).ToArray());
            return DeviceInfo(h);
        });

    [McpServerTool(Name = "pix_device_info", ReadOnly = true), Description("Device connection details: system/GPU info strings, adapters and power states, launched processes and recent notifications.")]
    public static Task<string> Info(PixSession session, [Description("Device handle")] string handle)
        => Tools.Run(session, "pix_device_info", () => DeviceInfo(session.Get<ConnectionHandle>(handle)));

    private static object DeviceInfo(ConnectionHandle h)
    {
        object? metrics = null;
        try
        {
            IPixGetMetricsResults m = PixApiExtensionsDeviceConnection.GetMetrics<IPixGetMetricsResults>(h.Connection);
            var info = new Dictionary<string, string>();
            ulong n = m.GetNumInfoStrings();
            for (ulong i = 0; i < n; i++)
            {
                PIX_STRING_PAIR pair = PixApiExtensionsDeviceConnectionResults.GetInfoString(m, i);
                info[Interop.W(pair.Name)] = Interop.W(pair.Value);
            }
            var gpus = new List<Dictionary<string, string>>();
            ulong gpuCount = m.GetNumGpus();
            for (ulong g = 0; g < gpuCount; g++)
            {
                var gpu = new Dictionary<string, string>();
                ulong count = m.GetNumGpuInfoStrings(g);
                for (ulong i = 0; i < count; i++)
                {
                    PIX_STRING_PAIR pair = PixApiExtensionsDeviceConnectionResults.GetGpuInfoString(m, g, i);
                    gpu[Interop.W(pair.Name)] = Interop.W(pair.Value);
                }
                gpus.Add(gpu);
            }
            metrics = new { system = info, gpus };
        }
        catch (Exception ex) { metrics = PixErrors.Unavailable("metrics", ex); }

        object? adapters = null;
        try
        {
            IPixAdapters a = PixApiExtensionsDeviceConnectionResults.GetAdapters<IPixAdapters>(h.Connection);
            var list = new List<object>();
            ulong count = a.GetCount();
            for (ulong i = 0; i < count; i++)
            {
                PIX_ADAPTER adapter = PixApiExtensionsDeviceConnectionResults.GetAdapter(a, i);
                object? powerStates = null;
                try
                {
                    IPixPowerStates ps = PixApiExtensionsDeviceConnectionResults.GetPowerStates<IPixPowerStates>(h.Connection, ref adapter);
                    var states = new List<object>();
                    ulong psCount = ps.GetCount();
                    for (ulong s = 0; s < psCount; s++)
                    {
                        PIX_POWER_STATE state = PixApiExtensionsDeviceConnectionResults.GetPowerState(ps, s);
                        states.Add(new { id = state.Id, name = Interop.W(state.Name), description = Interop.WOrNull(state.Description) });
                    }
                    powerStates = states;
                }
                catch (Exception ex) { powerStates = PixErrors.Unavailable("powerStates", ex); }
                list.Add(new { id = adapter.Id, name = Interop.W(adapter.Name), powerStates });
            }
            adapters = list;
        }
        catch (Exception ex) { adapters = PixErrors.Unavailable("adapters", ex); }

        return new { handle = h.Id, metrics, adapters, launchedProcessIds = h.LaunchedProcessIds, timingCaptureInProgress = h.TimingCaptureInProgress, recentEvents = h.RecentEvents() };
    }

    [McpServerTool(Name = "pix_device_processes"), Description("Lists running processes PIX can see, with whether they use D3D12 and any reason they are unsupported for capture.")]
    public static Task<string> Processes(
        PixSession session,
        [Description("Device handle")] string handle,
        [Description("Only processes whose exe name contains this text.")] string? nameContains = null)
        => Tools.Run(session, "pix_device_processes", () =>
        {
            ConnectionHandle h = session.Get<ConnectionHandle>(handle);
            IPixGetRunningProcessesResults results = PixApiExtensionsDeviceConnection.GetRunningProcesses<IPixGetRunningProcessesResults>(h.Connection);
            var list = new List<object>();
            ulong count = results.GetNumProcesses();
            for (ulong i = 0; i < count; i++)
            {
                IPixProcessInfo p = PixApiExtensionsDeviceConnection.GetProcessInfo<IPixProcessInfo>(results, i);
                string exe = Interop.W(p.GetExeName());
                if (!Tools.Contains(exe, nameContains))
                {
                    continue;
                }
                list.Add(new
                {
                    processId = p.GetProcessId(),
                    exeName = exe,
                    friendlyName = Interop.WOrNull(p.GetFriendlyName()),
                    commandLine = Interop.WOrNull(p.GetExeCmdLineArgs()),
                    isPackagedApp = (bool)p.IsPackagedApp(),
                    architecture = p.GetArchitecture(),
                    unsupportedReason = p.GetUnsupportedReason(),
                });
            }
            return list;
        });

    [McpServerTool(Name = "pix_device_counters"), Description("Lists the system monitor counters (CPU, GPU, memory, ...) PIX can collect on this machine, with units and ranges.")]
    public static Task<string> Counters(
        PixSession session,
        [Description("Device handle")] string handle,
        [Description("Only visible, non-internal counters (default true).")] bool visibleOnly = true)
        => Tools.Run(session, "pix_device_counters", () =>
        {
            ConnectionHandle h = session.Get<ConnectionHandle>(handle);
            IPixGetCounterDescriptionsResults results = PixApiExtensionsDeviceConnection.GetCounterDescriptions<IPixGetCounterDescriptionsResults>(h.Connection);
            var groups = new Dictionary<uint, string>();
            ulong groupCount = results.GetNumCounterGroups();
            for (ulong i = 0; i < groupCount; i++)
            {
                IPixSystemMonitorCounterGroup g = PixApiExtensionsDeviceConnectionResults.GetCounterGroupDescription<IPixSystemMonitorCounterGroup>(results, i);
                groups[g.GetCounterGroupId()] = Interop.W(g.GetName());
            }
            var counters = new List<object>();
            ulong count = results.GetNumCounters();
            for (ulong i = 0; i < count; i++)
            {
                IPixSystemMonitorCounter c = PixApiExtensionsDeviceConnectionResults.GetCounterDescription<IPixSystemMonitorCounter>(results, i);
                if (visibleOnly && (!(bool)c.GetIsVisible() || (bool)c.GetIsInternal()))
                {
                    continue;
                }
                counters.Add(new
                {
                    id = c.GetCounterId(),
                    name = Interop.W(c.GetDisplayName()),
                    internalName = Interop.WOrNull(c.GetInternalName()),
                    group = groups.TryGetValue(c.GetCounterGroupId(), out string? gn) ? gn : c.GetCounterGroupId().ToString(),
                    units = Interop.WOrNull(c.GetUnits()),
                    description = Interop.WOrNull(c.GetDescription()),
                    min = c.GetDefinedMin(),
                    max = c.GetDefinedMax(),
                    isDefault = (bool)c.GetIsDefault(),
                    processType = c.GetProcessType(),
                });
            }
            return new { groups = groups.Select(kv => new { id = kv.Key, name = kv.Value }).ToArray(), counters };
        });

    [McpServerTool(Name = "pix_device_launch"), Description("Launches a Win32 executable under PIX, by default hooked for GPU capture. Returns the process id. Give the app a few seconds to create its D3D12 device before taking a capture.")]
    public static Task<string> Launch(
        PixSession session,
        [Description("Device handle")] string handle,
        [Description("Path to the .exe to launch.")] string exePath,
        [Description("Command line arguments.")] string? arguments = null,
        [Description("Working directory (default: the exe's directory).")] string? workingDirectory = null,
        [Description("Launch under GPU capture (default true).")] bool underGpuCapture = true,
        [Description("Additional launch flags, e.g. GPU_CAPTURE_DISABLE_HUD, GPU_CAPTURE_ENABLE_DRED_LOGGING, SUSPENDED.")] string[]? flags = null)
        => Tools.Run(session, "pix_device_launch", () =>
        {
            ConnectionHandle h = session.Get<ConnectionHandle>(handle);
            string exe = Tools.RequireFile(exePath, "Executable");
            var desc = new LaunchDesc
            {
                launchMode = PIX_APPLICATION_LAUNCH_MODE.PIX_APPLICATION_LAUNCH_MODE_WIN32_EXECUTABLE,
                launchFlags = underGpuCapture ? PIX_APPLICATION_LAUNCH_FLAGS.PIX_APPLICATION_LAUNCH_FLAG_UNDER_GPU_CAPTURE : 0,
            };
            if (flags is not null)
            {
                foreach (string f in flags)
                {
                    desc.launchFlags |= Tools.ParseEnum<PIX_APPLICATION_LAUNCH_FLAGS>(f);
                }
            }
            desc.launchInfo.win32.exePath = exe;
            desc.launchInfo.win32.commandLineArgs = arguments ?? string.Empty;
            desc.launchInfo.win32.initialWorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? (Path.GetDirectoryName(exe) ?? string.Empty) : Path.GetFullPath(workingDirectory);

            IPixLaunchProcessResults results = PixApiExtensionsDeviceConnection.LaunchProcess<IPixLaunchProcessResults>(h.Connection, ref desc);
            uint pid = results.GetProcessId();
            PIX_PROCESS_UNSUPPORTED_REASON reason = results.GetUnsupportedReason();
            h.LaunchedProcessIds.Add(pid);
            h.Note("launched", new { pid, exe });
            return new
            {
                processId = pid,
                processName = Interop.WOrNull(results.GetProcessName()),
                unsupportedReason = reason,
                note = reason == PIX_PROCESS_UNSUPPORTED_REASON.PIX_PROCESS_UNSUPPORTED_REASON_NOT_USING_D3D12
                    ? "NOT_USING_D3D12 is normal right after launch; the app has not created its D3D12 device yet."
                    : null,
            };
        });

    [McpServerTool(Name = "pix_device_attach"), Description("Attaches PIX to a running process (by pid) for GPU capture.")]
    public static Task<string> Attach(
        PixSession session,
        [Description("Device handle")] string handle,
        [Description("Process id")] uint processId,
        [Description("Attach for GPU capture (default true).")] bool forGpuCapture = true)
        => Tools.Run(session, "pix_device_attach", () =>
        {
            ConnectionHandle h = session.Get<ConnectionHandle>(handle);
            var desc = new PIX_ATTACH_TO_PROCESS_DESC { ProcessId = processId, ForGpuCapture = forGpuCapture };
            IPixAttachToProcessResults results = PixApiExtensionsDeviceConnection.AttachToProcess<IPixAttachToProcessResults>(h.Connection, ref desc);
            h.LaunchedProcessIds.Add(processId);
            h.Note("attached", new { processId });
            return new { processId, processName = Interop.WOrNull(results.GetProcessName()), unsupportedReason = results.GetUnsupportedReason() };
        });

    [McpServerTool(Name = "pix_device_take_gpu_capture"), Description("Takes a GPU capture of a process launched/attached through this connection and (by default) opens it as a GPU capture handle. Blocks until the app presents the captured frame(s); returns a job.")]
    public static async Task<string> TakeGpuCapture(
        PixSession session,
        JobManager jobs,
        [Description("Device handle")] string handle,
        [Description("Process id (from pix_device_launch / pix_device_attach).")] uint processId,
        [Description("Seconds to wait before triggering the capture, to let the app initialise (default 3).")] double delaySeconds = 3,
        [Description("Number of frames to capture (default 1).")] uint frameCount = 1,
        [Description("Open the resulting .wpix as a GPU capture handle (default true).")] bool open = true,
        [Description("Seconds to wait inline for completion (default 0 = return job immediately).")] double waitSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ConnectionHandle h = session.Get<ConnectionHandle>(handle);
            Job job = jobs.Start("gpu-capture", $"Take GPU capture of pid {processId}", j =>
            {
                if (delaySeconds > 0)
                {
                    j.AddMessage($"Waiting {delaySeconds:0.#}s for the target to initialise...");
                    Thread.Sleep(TimeSpan.FromSeconds(Math.Min(delaySeconds, 60)));
                }
                IPixGpuCaptureResult result = CaptureWithOptions(
                    processId,
                    frameCount,
                    options => PixApiExtensionsDeviceConnection.SetGpuCaptureOptions(h.Connection, options),
                    () =>
                    {
                        j.AddMessage("Capturing...");
                        return PixApiExtensionsDeviceConnection.TakeGpuCaptureResult(h.Connection, processId)
                            ?? throw new McpException("PIX returned no GPU capture result.");
                    });
                string path = Interop.W(result.GetFilename());
                if (string.IsNullOrEmpty(path))
                {
                    throw new McpException("PIX returned an empty capture filename.");
                }
                h.Note("gpuCaptureTaken", new { processId, path });
                j.AddMessage("Capture saved to " + path);

                object? gpu = null;
                if (open && File.Exists(path))
                {
                    IPixGpuCaptureDocument document = session.Factory.OpenGpuCaptureDocument<IPixGpuCaptureDocument>(path);
                    gpu = session.Register(new GpuCaptureHandle(path, document)).Summary();
                }
                return new { path, gpuCapture = gpu };
            });
            return Json.Serialize(await jobs.WaitOrStatus(job, waitSeconds, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            throw PixErrors.ToMcp(ex, "pix_device_take_gpu_capture");
        }
    }

    internal static T CaptureWithOptions<T>(
        uint processId,
        uint frameCount,
        Action<PIX_GPU_CAPTURE_OPTIONS> applyOptions,
        Func<T> capture)
    {
        if (frameCount == 0)
        {
            throw new McpException("frameCount must be at least 1.");
        }
        // PIX retains these settings for subsequent captures, including single-frame requests.
        applyOptions(new PIX_GPU_CAPTURE_OPTIONS
        {
            Delimiter = PIX_GPU_CAPTURE_DELIMITER.PIX_GPU_CAPTURE_DELIMITER_PRESENT,
            FrameCount = frameCount,
            TargetProcessId = processId,
        });
        return capture();
    }

    [McpServerTool(Name = "pix_device_timing_capture_start"), Description("Starts a system-wide PIX timing capture (CPU samples, context switches, PIX events, GPU timing) to the given .wpix path. Stop it with pix_device_timing_capture_stop.")]
    public static Task<string> TimingCaptureStart(
        PixSession session,
        [Description("Device handle")] string handle,
        [Description("Output .wpix path.")] string outputPath,
        [Description("Capture CPU samples (default true).")] bool cpuSamples = true,
        [Description("CPU samples per second (default 1000).")] uint cpuSamplesPerSecond = 1000,
        [Description("Capture CPU sample callstacks (default true).")] bool cpuSampleStacks = true,
        [Description("Capture context switches and ready-thread events (default true).")] bool contextSwitches = true,
        [Description("Capture PIX events/markers (default true).")] bool pixEvents = true,
        [Description("Capture GPU timing (default true).")] bool gpuTiming = true,
        [Description("Capture GPU memory usage (default false).")] bool gpuMemoryUsage = false,
        [Description("Capture file I/O events (default false).")] bool fileIo = false,
        [Description("Maximum capture file size in MB (default 1024).")] uint maxFileSizeMb = 1024,
        [Description("Automatic capture duration in seconds (0 = until stopped).")] uint durationSeconds = 0)
        => Tools.Run(session, "pix_device_timing_capture_start", () =>
        {
            ConnectionHandle h = session.Get<ConnectionHandle>(handle);
            if (h.TimingCaptureInProgress is not null)
            {
                throw new McpException($"A timing capture is already in progress: {h.TimingCaptureInProgress}");
            }
            string full = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full) ?? ".");
            PIX_EVENT_COLLECTION_LEVEL Level(bool on, bool stacks) => !on ? PIX_EVENT_COLLECTION_LEVEL.PIX_EVENT_COLLECTION_LEVEL_NONE
                : stacks ? PIX_EVENT_COLLECTION_LEVEL.PIX_EVENT_COLLECTION_LEVEL_ENABLED_WITH_STACKS : PIX_EVENT_COLLECTION_LEVEL.PIX_EVENT_COLLECTION_LEVEL_ENABLED;
            var options = new PIX_TIMING_CAPTURE_OPTIONS
            {
                CapturePixEvents = pixEvents,
                CaptureContextSwitchAndReadyThread = Level(contextSwitches, false),
                CaptureCpuSamples = Level(cpuSamples, cpuSampleStacks),
                CaptureTrackedFunctions = false,
                CaptureGpuTiming = gpuTiming,
                CaptureGpuMemoryUsage = gpuMemoryUsage,
                MaximumCaptureFileSizeMb = maxFileSizeMb,
                CpuSamplesPerSecond = cpuSamplesPerSecond,
                CaptureFileIO = Level(fileIo, false),
                MergeKernelImages = false,
                CaptureStacksForAllProcesses = false,
                CaptureVirtualAllocEvents = PIX_EVENT_COLLECTION_LEVEL.PIX_EVENT_COLLECTION_LEVEL_NONE,
                CaptureHeapAllocEvents = PIX_EVENT_COLLECTION_LEVEL.PIX_EVENT_COLLECTION_LEVEL_NONE,
                CapturePixMemEvents = PIX_EVENT_COLLECTION_LEVEL.PIX_EVENT_COLLECTION_LEVEL_NONE,
                CaptureDuration = durationSeconds,
            };
            PixApiExtensionsDeviceConnection.StartTimingCapture(h.Connection, full, options, Array.Empty<PIX_TIMING_CAPTURE_OPTION_PART>());
            h.TimingCaptureInProgress = full;
            h.Note("timingCaptureStarted", new { path = full });
            return new { started = true, path = full };
        });

    [McpServerTool(Name = "pix_device_timing_capture_stop"), Description("Stops the in-progress timing capture and optionally opens the resulting file as a timing capture handle.")]
    public static Task<string> TimingCaptureStop(
        PixSession session,
        [Description("Device handle")] string handle,
        [Description("Open the capture as a timing handle after stopping (default true).")] bool open = true)
        => Tools.Run(session, "pix_device_timing_capture_stop", () =>
        {
            ConnectionHandle h = session.Get<ConnectionHandle>(handle);
            string? path = h.TimingCaptureInProgress;
            h.Connection.StopTimingCapture();
            h.TimingCaptureInProgress = null;
            h.Note("timingCaptureStopped", new { path });
            object? timing = null;
            string? openError = null;
            if (open && path is not null)
            {
                try
                {
                    // The file is finalised asynchronously; give it a moment.
                    for (int i = 0; i < 20 && !File.Exists(path); i++) Thread.Sleep(500);
                    IPixTimingCaptureDocument document = session.Factory.OpenTimingCaptureDocument<IPixTimingCaptureDocument>(path);
                    timing = session.Register(new TimingCaptureHandle(path, document)).Summary();
                }
                catch (Exception ex) { openError = PixErrors.Describe(ex); }
            }
            return new { stopped = true, path, timingCapture = timing, openError };
        });

    [McpServerTool(Name = "pix_device_detach"), Description("Detaches PIX from all target processes of this connection (optionally terminating them).")]
    public static Task<string> Detach(
        PixSession session,
        [Description("Device handle")] string handle,
        [Description("Terminate the target processes (default false).")] bool terminate = false)
        => Tools.Run(session, "pix_device_detach", () =>
        {
            ConnectionHandle h = session.Get<ConnectionHandle>(handle);
            h.Connection.DetachFromAllProcesses(terminate);
            h.Note("detached", new { terminate });
            h.LaunchedProcessIds.Clear();
            return new { detached = true, terminated = terminate };
        });
}
