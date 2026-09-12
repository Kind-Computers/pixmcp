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
    public static Task<string> Connect(PixSession session, CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_device_connect", () =>
        {
            ConnectionHandle? handle = null;
            var notifications = new DelegateConnectionNotifications();
            IPixConnectionDocument connection = session.Factory.OpenConnectionDocument<IPixConnectionDocument>(
                ConnDesc.CreateLocal(), notifications);
            handle = session.Register(new ConnectionHandle("local", connection, notifications));
            ConnectionHandle h = handle;
            notifications.OnAllTargetProcessesTerminated = () => { h.Targets.TerminateAll(); h.Note("allTargetProcessesTerminated"); };
            notifications.OnDeviceCounterCollectionStarted = () => h.Note("counterCollectionStarted");
            notifications.OnDeviceCounterCollectionStopped = () => h.Note("counterCollectionStopped");
            notifications.OnDeviceCounterDescriptionsUpdated = () => h.Note("counterDescriptionsUpdated");
            notifications.OnNewGpuCaptureCompleted = (_, filename, _) => h.Note("gpuCaptureCompleted", new { filename });
            notifications.OnNewTimingCaptureError = (hr, message) => h.Note("timingCaptureError", new { hresult = PixErrors.Hex(hr), message });
            notifications.OnNotifyGpuCaptureTargetProcessesStatus = statuses =>
            {
                foreach (var status in statuses) h.Targets.Observe(status.ProcessId, status.UnsupportedReason);
                h.Note("targetProcessesStatus", statuses.Select(s => new { processId = s.ProcessId,
                    name = Interop.W(s.ProcessName), unsupportedReason = s.UnsupportedReason }).ToArray());
            };
            return DeviceInfo(h);
        }, cancellationToken);

    [McpServerTool(Name = "pix_device_info", ReadOnly = true), Description("Device connection details: system/GPU info strings, adapters and power states, launched processes and recent notifications.")]
    public static Task<string> Info(PixSession session, [Description("Device handle")] string handle, CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_device_info", () => DeviceInfo(session.Get<ConnectionHandle>(handle)), cancellationToken);

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

        return new { handle = h.Id, metrics, adapters, launchedProcessIds = h.ProcessIds, targets = h.Targets.Snapshot(), timingCaptureInProgress = h.TimingCaptureInProgress, recentEvents = h.RecentEvents() };
    }

    [McpServerTool(Name = "pix_device_processes", ReadOnly = true), Description("Pages the running processes PIX can see, with whether they use D3D12 and any reason they are unsupported for capture. Filter with nameContains (exe name) or d3d12Only, then pass processId to pix_device_attach.")]
    public static Task<string> Processes(
        PixSession session,
        [Description("Device handle")] string handle,
        [Description("Only processes whose exe name contains this text (case-insensitive).")] string? nameContains = null,
        [Description("Only processes PIX reports as using D3D12 (default false).")] bool d3d12Only = false,
        [Description("First process (default 0).")] int offset = 0,
        [Description("Maximum processes (default 25, max 1000).")] int limit = Paging.DefaultLimit,
        CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_device_processes", () =>
        {
            ConnectionHandle h = session.Get<ConnectionHandle>(handle);
            (int o, int l) = Paging.Normalize(offset, limit);
            IPixGetRunningProcessesResults results = PixApiExtensionsDeviceConnection.GetRunningProcesses<IPixGetRunningProcessesResults>(h.Connection);
            IEnumerable<IPixProcessInfo> processes = Enumerable.Range(0, (int)Math.Min(results.GetNumProcesses(), int.MaxValue))
                .Select(i => PixApiExtensionsDeviceConnection.GetProcessInfo<IPixProcessInfo>(results, (ulong)i))
                .Where(p => Tools.Contains(Interop.W(p.GetExeName()), nameContains))
                .Where(p => !d3d12Only || !Json.EnumName(p.GetUnsupportedReason()).Contains("NOT_USING_D3D12", StringComparison.Ordinal));
            return Paging.Collect(processes, o, l, p => new
            {
                processId = p.GetProcessId(),
                exeName = Interop.W(p.GetExeName()),
                friendlyName = Interop.WOrNull(p.GetFriendlyName()),
                commandLine = Interop.WOrNull(p.GetExeCmdLineArgs()),
                isPackagedApp = (bool)p.IsPackagedApp(),
                architecture = p.GetArchitecture(),
                unsupportedReason = p.GetUnsupportedReason(),
            });
        }, cancellationToken);

    [McpServerTool(Name = "pix_device_counters", ReadOnly = true), Description("Pages the system monitor counters (CPU, GPU, memory, ...) PIX can collect on this machine, with units and ranges; extra.groups lists the counter groups. Filter with nameContains or group. (These are live system counters; GPU hardware counters of a capture come from pix_gpu_counters_list.)")]
    public static Task<string> Counters(
        PixSession session,
        [Description("Device handle")] string handle,
        [Description("Only visible, non-internal counters (default true).")] bool visibleOnly = true,
        [Description("Only counters whose display name contains this text (case-insensitive).")] string? nameContains = null,
        [Description("Only counters in this group (name from extra.groups, case-insensitive).")] string? group = null,
        [Description("First counter (default 0).")] int offset = 0,
        [Description("Maximum counters (default 25, max 1000).")] int limit = Paging.DefaultLimit,
        CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_device_counters", () =>
        {
            ConnectionHandle h = session.Get<ConnectionHandle>(handle);
            (int o, int l) = Paging.Normalize(offset, limit);
            IPixGetCounterDescriptionsResults results = PixApiExtensionsDeviceConnection.GetCounterDescriptions<IPixGetCounterDescriptionsResults>(h.Connection);
            var groups = new Dictionary<uint, string>();
            ulong groupCount = results.GetNumCounterGroups();
            for (ulong i = 0; i < groupCount; i++)
            {
                IPixSystemMonitorCounterGroup g = PixApiExtensionsDeviceConnectionResults.GetCounterGroupDescription<IPixSystemMonitorCounterGroup>(results, i);
                groups[g.GetCounterGroupId()] = Interop.W(g.GetName());
            }
            string GroupName(IPixSystemMonitorCounter c) => groups.TryGetValue(c.GetCounterGroupId(), out string? gn) ? gn : c.GetCounterGroupId().ToString();
            IEnumerable<IPixSystemMonitorCounter> counters = Enumerable.Range(0, (int)Math.Min(results.GetNumCounters(), int.MaxValue))
                .Select(i => PixApiExtensionsDeviceConnectionResults.GetCounterDescription<IPixSystemMonitorCounter>(results, (ulong)i))
                .Where(c => !visibleOnly || ((bool)c.GetIsVisible() && !(bool)c.GetIsInternal()))
                .Where(c => Tools.Contains(Interop.W(c.GetDisplayName()), nameContains))
                .Where(c => string.IsNullOrEmpty(group) || GroupName(c).Equals(group, StringComparison.OrdinalIgnoreCase));
            return Paging.Collect(counters, o, l, c => new
            {
                id = c.GetCounterId(),
                name = Interop.W(c.GetDisplayName()),
                internalName = Interop.WOrNull(c.GetInternalName()),
                group = GroupName(c),
                units = Interop.WOrNull(c.GetUnits()),
                description = Interop.WOrNull(c.GetDescription()),
                min = c.GetDefinedMin(),
                max = c.GetDefinedMax(),
                isDefault = (bool)c.GetIsDefault(),
                processType = c.GetProcessType(),
            }, new { groups = groups.Select(kv => new { id = kv.Key, name = kv.Value }).ToArray() });
        }, cancellationToken);

    [McpServerTool(Name = "pix_device_launch"), Description("Launches a Win32 executable (exePath) or an installed packaged app (packageFullName + applicationId from pix_device_packaged_apps) under PIX, by default hooked for GPU capture. Returns the process id. Give the app a few seconds to create its D3D12 device before taking a capture. D3D settings from pix_device_d3d_settings_set apply to the launched process.")]
    public static Task<string> Launch(
        PixSession session,
        [Description("Device handle")] string handle,
        [Description("Path to the .exe to launch (omit when launching a packaged app).")] string? exePath = null,
        [Description("Command line arguments (Win32 only).")] string? arguments = null,
        [Description("Working directory (default: the exe's directory; Win32 only).")] string? workingDirectory = null,
        [Description("Launch under GPU capture (default true).")] bool underGpuCapture = true,
        [Description("Additional launch flags, e.g. GPU_CAPTURE_DISABLE_HUD, GPU_CAPTURE_ENABLE_DRED_LOGGING, SUSPENDED, TERMINATE_RUNNING_PACKAGE.")] string[]? flags = null,
        [Description("Package full name of a packaged (UWP/MSIX) app to launch instead of an exe.")] string? packageFullName = null,
        [Description("Application id within the package (default: the first one PIX reports for the package).")] string? applicationId = null,
        CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_device_launch", () =>
        {
            ConnectionHandle h = session.Get<ConnectionHandle>(handle);
            bool packaged = !string.IsNullOrWhiteSpace(packageFullName);
            if (!packaged && string.IsNullOrWhiteSpace(exePath))
            {
                throw new McpException("Specify exePath (Win32 executable) or packageFullName (packaged app).");
            }
            var desc = new LaunchDesc
            {
                launchMode = packaged ? PIX_APPLICATION_LAUNCH_MODE.PIX_APPLICATION_LAUNCH_MODE_PACKAGED_APP : PIX_APPLICATION_LAUNCH_MODE.PIX_APPLICATION_LAUNCH_MODE_WIN32_EXECUTABLE,
                launchFlags = underGpuCapture ? PIX_APPLICATION_LAUNCH_FLAGS.PIX_APPLICATION_LAUNCH_FLAG_UNDER_GPU_CAPTURE : 0,
            };
            if (flags is not null)
            {
                foreach (string f in flags)
                {
                    desc.launchFlags |= Tools.ParseEnum<PIX_APPLICATION_LAUNCH_FLAGS>(f);
                }
            }
            string target;
            if (packaged)
            {
                desc.launchInfo.packagedApp.packageFullName = packageFullName!;
                desc.launchInfo.packagedApp.applicationId = string.IsNullOrWhiteSpace(applicationId) ? FirstApplicationId(h, packageFullName!) : applicationId;
                target = packageFullName! + "!" + desc.launchInfo.packagedApp.applicationId;
            }
            else
            {
                string exe = Tools.RequireFile(exePath!, "Executable");
                desc.launchInfo.win32.exePath = exe;
                desc.launchInfo.win32.commandLineArgs = arguments ?? string.Empty;
                desc.launchInfo.win32.initialWorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? (Path.GetDirectoryName(exe) ?? string.Empty) : Path.GetFullPath(workingDirectory);
                target = exe;
            }

            long beforeLaunch = h.Targets.Revision;
            IPixLaunchProcessResults results = PixApiExtensionsDeviceConnection.LaunchProcess<IPixLaunchProcessResults>(h.Connection, ref desc);
            uint pid = results.GetProcessId();
            PIX_PROCESS_UNSUPPORTED_REASON reason = results.GetUnsupportedReason();
            bool capturable = IsCapturable(reason) && pid != 0;
            if (capturable)
            {
                h.AddProcess(pid);
                h.Targets.Track(pid, reason, beforeLaunch);
            }
            h.Note("launched", new { pid, target, unsupportedReason = reason });
            return new
            {
                processId = pid,
                processName = Interop.WOrNull(results.GetProcessName()),
                unsupportedReason = reason,
                capturable,
                ready = capturable && h.Targets.Get(pid).Snapshot().Ready,
                note = reason == PIX_PROCESS_UNSUPPORTED_REASON.PIX_PROCESS_UNSUPPORTED_REASON_NOT_USING_D3D12
                    ? "NOT_USING_D3D12 is normal right after launch; the app has not created its D3D12 device yet."
                    : capturable ? null : $"PIX cannot capture this process ({Json.EnumName(reason)}); it was not recorded as a capture target.",
            };
        }, cancellationToken);

    private static string FirstApplicationId(ConnectionHandle h, string packageFullName)
    {
        IPixGetInstalledPackagedAppsResults apps = PixApiExtensionsDeviceConnection.GetInstalledPackagedApps<IPixGetInstalledPackagedAppsResults>(h.Connection);
        ulong count = apps.GetNumInstalledPackagedApps();
        for (ulong i = 0; i < count; i++)
        {
            IPixPackagedAppInfo app = PixApiExtensionsDeviceConnectionResults.GetInstalledPackagedAppInfo<IPixPackagedAppInfo>(apps, i);
            if (Interop.W(app.GetPackageFullName()).Equals(packageFullName, StringComparison.OrdinalIgnoreCase))
            {
                return Interop.W(app.GetApplicationId());
            }
        }
        throw new McpException($"No installed packaged app has package full name '{packageFullName}'. List them with pix_device_packaged_apps.");
    }

    [McpServerTool(Name = "pix_device_packaged_apps", ReadOnly = true), Description("Pages the installed packaged (UWP/MSIX) apps PIX can launch: package full name, application id, friendly name, architecture and any reason PIX cannot capture them. Pass packageFullName (and applicationId) to pix_device_launch.")]
    public static Task<string> PackagedApps(
        PixSession session,
        [Description("Device handle")] string handle,
        [Description("Only apps whose friendly name or package name contains this text (case-insensitive).")] string? nameContains = null,
        [Description("First app (default 0).")] int offset = 0,
        [Description("Maximum apps (default 25, max 1000).")] int limit = Paging.DefaultLimit,
        CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_device_packaged_apps", () =>
        {
            ConnectionHandle h = session.Get<ConnectionHandle>(handle);
            (int o, int l) = Paging.Normalize(offset, limit);
            IPixGetInstalledPackagedAppsResults apps = PixApiExtensionsDeviceConnection.GetInstalledPackagedApps<IPixGetInstalledPackagedAppsResults>(h.Connection);
            IEnumerable<IPixPackagedAppInfo> matching = Enumerable.Range(0, (int)Math.Min(apps.GetNumInstalledPackagedApps(), int.MaxValue))
                .Select(i => PixApiExtensionsDeviceConnectionResults.GetInstalledPackagedAppInfo<IPixPackagedAppInfo>(apps, (ulong)i))
                .Where(a => Tools.Contains(Interop.W(a.GetFriendlyName()), nameContains) || Tools.Contains(Interop.W(a.GetPackageFullName()), nameContains));
            return Paging.Collect(matching, o, l, a => new
            {
                packageFullName = Interop.W(a.GetPackageFullName()),
                applicationId = Interop.W(a.GetApplicationId()),
                friendlyName = Interop.WOrNull(a.GetFriendlyName()),
                architecture = a.GetArchitecture(),
                unsupportedReason = a.GetUnsupportedReason(),
            });
        }, cancellationToken);

    [McpServerTool(Name = "pix_device_attach"), Description("Attaches PIX to a running process (by pid, see pix_device_processes) for GPU capture.")]
    public static Task<string> Attach(
        PixSession session,
        [Description("Device handle")] string handle,
        [Description("Process id")] uint processId,
        [Description("Attach for GPU capture (default true).")] bool forGpuCapture = true,
        CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_device_attach", () =>
        {
            ConnectionHandle h = session.Get<ConnectionHandle>(handle);
            var desc = new PIX_ATTACH_TO_PROCESS_DESC { ProcessId = processId, ForGpuCapture = forGpuCapture };
            long beforeAttach = h.Targets.Revision;
            IPixAttachToProcessResults results = PixApiExtensionsDeviceConnection.AttachToProcess<IPixAttachToProcessResults>(h.Connection, ref desc);
            PIX_PROCESS_UNSUPPORTED_REASON reason = results.GetUnsupportedReason();
            bool capturable = IsCapturable(reason);
            if (capturable)
            {
                h.AddProcess(processId);
                h.Targets.Track(processId, reason, beforeAttach);
            }
            h.Note("attached", new { processId, unsupportedReason = reason });
            return new
            {
                processId,
                processName = Interop.WOrNull(results.GetProcessName()),
                unsupportedReason = reason,
                capturable,
                ready = capturable && h.Targets.Get(processId).Snapshot().Ready,
                note = capturable ? null : $"PIX cannot capture this process ({Json.EnumName(reason)}); it was not recorded as a capture target.",
            };
        }, cancellationToken);

    /// <summary>NOT_USING_D3D12 is transient (the device is created later); the other reasons are permanent.</summary>
    internal static bool IsCapturable(PIX_PROCESS_UNSUPPORTED_REASON reason)
        => reason is PIX_PROCESS_UNSUPPORTED_REASON.PIX_PROCESS_UNSUPPORTED_REASON_NONE or PIX_PROCESS_UNSUPPORTED_REASON.PIX_PROCESS_UNSUPPORTED_REASON_NOT_USING_D3D12;

    [McpServerTool(Name = "pix_device_take_gpu_capture"), Description("Takes a GPU capture of a process launched/attached through this connection and (by default) opens it as a GPU capture handle. Blocks until the app presents the captured frame(s); returns a job.")]
    public static async Task<string> TakeGpuCapture(
        PixSession session,
        JobManager jobs,
        [Description("Device handle")] string handle,
        [Description("Process id (from pix_device_launch / pix_device_attach).")] uint processId,
        [Description("Optional additional warmup after readiness, 0 through 60 seconds (default 0).")] double delaySeconds = 0,
        [Description("Number of frames to capture (default 1).")] uint frameCount = 1,
        [Description("Open the resulting .wpix as a GPU capture handle (default true).")] bool open = true,
        [Description(Tools.WaitSecondsDescription)] double waitSeconds = 0,
        [Description("Maximum seconds to await a capturable D3D12 device, 0 through 300 (default 30). Zero checks once.")] double readinessTimeoutSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!double.IsFinite(delaySeconds) || delaySeconds is < 0 or > 60
                || !double.IsFinite(readinessTimeoutSeconds) || readinessTimeoutSeconds is < 0 or > 300
                || !double.IsFinite(waitSeconds) || waitSeconds is < 0 or > 3600 || frameCount == 0)
                throw new PixToolException("invalid_arguments", "frameCount must be positive; delaySeconds must be 0–60, readinessTimeoutSeconds 0–300, and waitSeconds 0–3600, all finite.");
            CaptureTarget target = session.Get<ConnectionHandle>(handle).Targets.Get(processId);
            object retry = StructuredToolResults.CurrentArguments() ?? new { handle, processId, delaySeconds, frameCount, open, waitSeconds, readinessTimeoutSeconds };
            Job job = jobs.StartAfter("gpu-capture", $"Take GPU capture of pid {processId}", async j =>
            {
                try
                {
                    j.AddMessage("Waiting for a capturable D3D12 device...");
                    await target.WaitReadyAsync(TimeSpan.FromSeconds(readinessTimeoutSeconds), j.Cancellation.Token).ConfigureAwait(false);
                    if (delaySeconds > 0) j.AddMessage($"Warming up for {delaySeconds:0.#}s...");
                    await target.WarmupAsync(TimeSpan.FromSeconds(delaySeconds), j.Cancellation.Token).ConfigureAwait(false);
                }
                catch (PixToolException ex) when (ex.Detail.Code == "capture_target_not_ready")
                {
                    throw new PixToolException(ex.Detail with { NextCalls = [new("pix_device_info", new { handle }), new("pix_device_take_gpu_capture", retry)] });
                }
            }, j =>
            {
                ConnectionHandle h = session.Get<ConnectionHandle>(handle);
                h.Targets.Validate(target);
                IPixGpuCaptureResult result = CaptureWithOptions(
                    processId,
                    frameCount,
                    options => PixApiExtensionsDeviceConnection.SetGpuCaptureOptions(h.Connection, options),
                    () =>
                    {
                        j.AddMessage("Capturing...");
                        return PixApiExtensionsDeviceConnection.TakeGpuCaptureResult(h.Connection, processId)
                            ?? throw new McpException("PIX returned no GPU capture result.");
                    }, j.Cancellation.Token);
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
            }, handle);
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
        Func<T> capture,
        CancellationToken cancellationToken = default)
    {
        if (frameCount == 0)
        {
            throw new McpException("frameCount must be at least 1.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        // PIX retains these settings for subsequent captures, including single-frame requests.
        applyOptions(new PIX_GPU_CAPTURE_OPTIONS
        {
            Delimiter = PIX_GPU_CAPTURE_DELIMITER.PIX_GPU_CAPTURE_DELIMITER_PRESENT,
            FrameCount = frameCount,
            TargetProcessId = processId,
        });
        cancellationToken.ThrowIfCancellationRequested();
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
        [Description("Automatic capture duration in seconds (0 = until stopped).")] uint durationSeconds = 0,
        CancellationToken cancellationToken = default)
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
        }, cancellationToken);

    [McpServerTool(Name = "pix_device_timing_capture_stop"), Description("Stops the in-progress timing capture and optionally opens the resulting file as a timing capture handle. PIX finalises the file asynchronously, so this runs as a job (waited for inline by default).")]
    public static Task<string> TimingCaptureStop(
        PixSession session,
        JobManager jobs,
        [Description("Device handle")] string handle,
        [Description("Open the capture as a timing handle after stopping (default true).")] bool open = true,
        [Description("Seconds to wait inline for the job (default 30). Completed status includes a resultRef; 0 returns immediately.")] double waitSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        string? path = null;
        object? timing = null;
        return Tools.RunJob(jobs, "pix_device_timing_capture_stop", () => jobs.StartAfter("timing-capture-stop", $"Stop timing capture on {handle}", async j =>
        {
            path = await session.Run(() =>
            {
                ConnectionHandle h = session.Get<ConnectionHandle>(handle);
                string pending = h.TimingCaptureInProgress ?? throw new PixToolException("capture_not_running", "No timing capture is tracked on this connection.");
                h.Connection.StopTimingCapture();
                h.TimingCaptureInProgress = null;
                h.Note("timingCaptureStopped", new { path = pending });
                return pending;
            }, j.Cancellation.Token, j.Id + ": stop timing capture").ConfigureAwait(false);
            j.AddMessage("Waiting for PIX to finalise " + path);
            try
            {
                timing = await WaitForReadableCaptureAsync(() => session.Run<object?>(() =>
                {
                    session.Get<ConnectionHandle>(handle);
                    if (!File.Exists(path)) throw new IOException("The capture file has not been created yet.");
                    IPixTimingCaptureDocument document = session.Factory.OpenTimingCaptureDocument<IPixTimingCaptureDocument>(path);
                    if (open) return session.Register(new TimingCaptureHandle(path, document)).Summary();
                    document.Close();
                    return null;
                }, j.Cancellation.Token, j.Id + ": finalize timing capture"), TimeSpan.FromSeconds(30), j.Cancellation.Token).ConfigureAwait(false);
            }
            catch (PixToolException ex) when (ex.Detail.Code == "capture_finalization_timeout")
            {
                throw new PixToolException(ex.Detail with { NextCalls = [new("pix_timing_open", new { path })] });
            }
        }, _ => new { stopped = true, path, timingCapture = timing }, handle), waitSeconds, cancellationToken);
    }

    /// <summary>Finalization is proven by a successful document open; delays never occupy the native worker.</summary>
    internal static async Task<T> WaitForReadableCaptureAsync<T>(Func<Task<T>> open, TimeSpan timeout, CancellationToken cancellationToken)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return await open().ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or System.Runtime.InteropServices.ExternalException)
            {
                TimeSpan remaining = timeout - System.Diagnostics.Stopwatch.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero) throw new PixToolException("capture_finalization_timeout",
                    "PIX did not finish a readable timing capture before the deadline: " + PixErrors.Describe(ex), true);
                await Task.Delay(remaining < TimeSpan.FromMilliseconds(250) ? remaining : TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    [McpServerTool(Name = "pix_device_detach", Destructive = true), Description("Detaches PIX from all target processes of this connection (optionally terminating them). Closing the device handle also detaches without terminating.")]
    public static Task<string> Detach(
        PixSession session,
        [Description("Device handle")] string handle,
        [Description("Terminate the target processes (default false).")] bool terminate = false,
        CancellationToken cancellationToken = default)
        => Tools.Run(session, "pix_device_detach", () =>
        {
            ConnectionHandle h = session.Get<ConnectionHandle>(handle);
            h.Connection.DetachFromAllProcesses(terminate);
            h.Note("detached", new { terminate });
            h.ClearProcesses();
            return new { detached = true, terminated = terminate };
        }, cancellationToken);
}
