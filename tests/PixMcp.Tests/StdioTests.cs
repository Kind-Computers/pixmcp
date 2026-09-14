using System.Diagnostics;
using System.Text.Json;
using PixMcp.Pix;
using Xunit;

namespace PixMcp.Tests;

[Collection(GpuReplayCollection.Name)]
public class StdioTests
{
    [SkippableFact]
    public async Task ResponseBudgetFailureRemainsAStructuredToolError()
    {
        Skip.If(PixDiscovery.InstallDir is null, "PIX Preview install required for server startup.");
        ProcessStartInfo start = ServerStart();
        start.Environment["PIXMCP_MAX_RESULT_BYTES"] = "80";
        await using var server = new StdioClient(start);
        await server.Send("initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "budget-test", version = "1" } });
        await server.Notify("notifications/initialized");
        JsonElement response = await server.Send("tools/call", new { name = "pix_info", arguments = new { } });
        Assert.False(response.TryGetProperty("error", out _));
        JsonElement result = response.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Equal("result_too_large", result.GetProperty("structuredContent").GetProperty("code").GetString());
        Assert.Equal(0, await server.Close());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrInvalidPixFailsBeforeWritingProtocolOutput(bool explicitOverride)
    {
        using DirectoryCleanup directory = new();
        var start = ServerStart();
        start.Environment.Remove("PIX_DIR");
        start.Environment["ProgramW6432"] = directory.Path;
        start.Environment["ProgramFiles"] = directory.Path;
        if (explicitOverride)
        {
            // A real fallback remains available: an explicit bad override must still fail.
            start.Environment["ProgramW6432"] = Environment.GetEnvironmentVariable("ProgramW6432") ?? @"C:\Program Files";
            start.Environment["PIX_DIR"] = directory.Path;
        }
        using var server = Process.Start(start)!;
        Task<string> stdout = server.StandardOutput.ReadToEndAsync();
        Task<string> stderr = server.StandardError.ReadToEndAsync();
        try
        {
            await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(1, server.ExitCode);
            Assert.Empty(await stdout);
            string error = await stderr;
            Assert.Contains("pixmcp:", error);
            Assert.Contains(explicitOverride ? "PIX_DIR" : "PIX Preview", error);
            Assert.Contains(directory.Path, error);
            Assert.DoesNotContain("ReflectionTypeLoadException", error);
            Assert.DoesNotContain("Unhandled exception", error);
        }
        finally
        {
            if (!server.HasExited)
            {
                server.Kill(entireProcessTree: true);
                await server.WaitForExitAsync();
            }
        }
    }

    [SkippableFact]
    public async Task MalformedResultBudgetFailsBeforeWritingProtocolOutput()
    {
        Skip.If(PixDiscovery.InstallDir is null, "PIX Preview install required for server startup.");
        ProcessStartInfo start = ServerStart();
        start.Environment["PIXMCP_RESULT_MEMORY_BYTES"] = "lots";
        using var server = Process.Start(start)!;
        Task<string> stdout = server.StandardOutput.ReadToEndAsync();
        Task<string> stderr = server.StandardError.ReadToEndAsync();
        try
        {
            await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(1, server.ExitCode);
            Assert.Empty(await stdout);
            string error = await stderr;
            Assert.Contains("pixmcp: PIXMCP_RESULT_MEMORY_BYTES", error);
            Assert.Contains("lots", error);
        }
        finally
        {
            if (!server.HasExited) { server.Kill(entireProcessTree: true); await server.WaitForExitAsync(); }
        }
    }

    [SkippableFact]
    public async Task ListsToolsAndResourcesHandlesErrorsAndShutsDownCleanly()
    {
        Skip.If(PixDiscovery.InstallDir is null, "The server only starts with a PIX Preview install; discovery found none.");
        await using var server = new StdioClient(ServerStart());
        JsonElement initialized = await server.Send("initialize", new
        {
            protocolVersion = "2025-06-18", capabilities = new { },
            clientInfo = new { name = "pixmcp-tests", version = "1" },
        });
        Assert.Equal("pixmcp", initialized.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.Equal(ServerHost.Instructions, initialized.GetProperty("result").GetProperty("instructions").GetString());
        await server.Notify("notifications/initialized");

        JsonElement listed = (await server.Send("tools/list")).GetProperty("result");
        JsonElement tools = listed.GetProperty("tools");
        string?[] names = tools.EnumerateArray().Select(tool => tool.GetProperty("name").GetString()).ToArray();
        Assert.Equal(3600000, listed.GetProperty("ttlMs").GetInt64());
        Assert.Equal("private", listed.GetProperty("cacheScope").GetString());
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), names);
        Assert.Equal(listed.GetRawText(), (await server.Send("tools/list")).GetProperty("result").GetRawText());
        Assert.Contains("pix_info", names);
        Assert.Contains("pix_gpu_open", names);
        Assert.Contains("pix_job_status", names);
        JsonElement resultReadInputs = tools.EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == "pix_result_read")
            .GetProperty("inputSchema").GetProperty("properties");
        Assert.Equal(new[] { "fields", "limit", "mode", "offset", "pointer", "resultRef", "where" }, resultReadInputs.EnumerateObject().Select(p => p.Name).OrderBy(name => name));
        Assert.Equal(new[] { "values", "outline" }, resultReadInputs.GetProperty("mode").GetProperty("enum").EnumerateArray().Select(v => v.GetString()));
        foreach (JsonElement tool in tools.EnumerateArray())
            Assert.Equal("object", tool.GetProperty("outputSchema").GetProperty("type").GetString());
        JsonElement inspectInputs = tools.EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == "pix_gpu_inspect_event")
            .GetProperty("inputSchema").GetProperty("properties");
        Assert.Equal(42, inspectInputs.GetProperty("eventRef").GetProperty("examples")[0].GetProperty("eventIndex").GetInt32());
        JsonElement timingInputs = tools.EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == "pix_gpu_timing_events")
            .GetProperty("inputSchema").GetProperty("properties");
        Assert.Equal(new[] { "objects", "table" }, timingInputs.GetProperty("format").GetProperty("enum").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal("Frame/Shadow", timingInputs.GetProperty("markerPathPrefix").GetProperty("examples")[0].GetString());
        Assert.Equal(4096, timingInputs.GetProperty("maxStringLength").GetProperty("maximum").GetDouble());
        JsonElement eventSchema = tools.EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == "pix_gpu_events")
            .GetProperty("outputSchema").GetProperty("anyOf")[0].GetProperty("properties");
        Assert.True(eventSchema.TryGetProperty("nextOffset", out _));
        Assert.True(eventSchema.GetProperty("items").GetProperty("items").GetProperty("properties").TryGetProperty("queueIndex", out _));

        JsonElement resourceList = (await server.Send("resources/list")).GetProperty("result");
        Assert.Equal("private", resourceList.GetProperty("cacheScope").GetString());
        JsonElement resources = resourceList.GetProperty("resources");
        Assert.Contains(resources.EnumerateArray(), resource => resource.GetProperty("uri").GetString() == "pix://handles");
        JsonElement templates = (await server.Send("resources/templates/list")).GetProperty("result").GetProperty("resourceTemplates");
        Assert.Contains(templates.EnumerateArray(), template => template.GetProperty("uriTemplate").GetString() == "pix://handles/{handle}");
        JsonElement handles = (await server.Send("resources/read", new { uri = "pix://handles" })).GetProperty("result").GetProperty("contents");
        Assert.Equal("{\"total\":0,\"offset\":0,\"count\":0,\"items\":[]}", Assert.Single(handles.EnumerateArray()).GetProperty("text").GetString());
        JsonElement missingHandle = (await server.Send("resources/read", new { uri = "pix://handles/nope" })).GetProperty("result").GetProperty("contents");
        using (var body = JsonDocument.Parse(Assert.Single(missingHandle.EnumerateArray()).GetProperty("text").GetString()!))
            Assert.Equal("unknown_handle", body.RootElement.GetProperty("code").GetString());
        JsonElement promptList = (await server.Send("prompts/list")).GetProperty("result");
        string?[] promptNames = promptList.GetProperty("prompts").EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToArray();
        Assert.Equal(3600000, promptList.GetProperty("ttlMs").GetInt64());
        Assert.Equal(Playbooks.All.Select(p => p.Name).Order(StringComparer.Ordinal), promptNames);
        JsonElement prompt = (await server.Send("prompts/get", new { name = "pix_frame_budget", arguments = new { handle = "gpu-9" } })).GetProperty("result");
        string promptText = prompt.GetProperty("messages")[0].GetProperty("content").GetProperty("text").GetString()!;
        Assert.Contains("\"handle\":\"gpu-9\"", promptText);
        Assert.Contains("pix_gpu_overview", promptText);

        foreach (JsonElement tool in tools.EnumerateArray())
        {
            string name = tool.GetProperty("name").GetString()!;
            JsonElement annotations = tool.GetProperty("annotations");
            Assert.False(string.IsNullOrWhiteSpace(annotations.GetProperty("title").GetString()), name + " has no title");
            Assert.Equal(name == "pix_device_connect", annotations.GetProperty("openWorldHint").GetBoolean());
            if (name is "pix_gpu_compare" or "pix_gpu_preview" or "pix_gpu_screenshot") Assert.False(annotations.GetProperty("readOnlyHint").GetBoolean(), name);
            foreach (JsonProperty property in tool.GetProperty("inputSchema").GetProperty("properties").EnumerateObject())
                Assert.True(property.Value.TryGetProperty("description", out _), $"{name}.{property.Name} has no description");
        }

        JsonElement info = await server.Send("tools/call", new { name = "pix_info", arguments = new { } });
        Assert.False(info.GetProperty("result").TryGetProperty("isError", out JsonElement isError) && isError.GetBoolean());
        using var infoText = JsonDocument.Parse(info.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.Equal(PixDiscovery.InstallDir, infoText.RootElement.GetProperty("pix").GetProperty("installDir").GetString());
        Assert.Equal("default", infoText.RootElement.GetProperty("options").GetProperty("inlineResultBytes").GetProperty("source").GetString());
        Assert.True(JsonElement.DeepEquals(infoText.RootElement, info.GetProperty("result").GetProperty("structuredContent")), info.GetRawText());
        OutputSchemaTests.AssertMatches(infoText.RootElement, tools.EnumerateArray().Single(t => t.GetProperty("name").GetString() == "pix_info").GetProperty("outputSchema"));
        // The shader profiling document makes native PIX code print to standard output; the protocol stream must stay valid JSON.
        JsonElement targets = (await server.Send("tools/call", new { name = "pix_shader_targets", arguments = new { vendor = "intel" } }, TimeSpan.FromSeconds(120))).GetProperty("result");
        Assert.False(targets.TryGetProperty("isError", out JsonElement targetsError) && targetsError.GetBoolean(), targets.GetRawText());

        JsonElement jobs = (await server.Send("tools/call", new { name = "pix_jobs", arguments = new { } })).GetProperty("result");
        Assert.Equal("{\"total\":0,\"offset\":0,\"count\":0,\"items\":[]}", jobs.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Empty(jobs.GetProperty("structuredContent").GetProperty("items").EnumerateArray());
        JsonElement closeAll = (await server.Send("tools/call", new { name = "pix_close_all", arguments = new { } })).GetProperty("result");
        Assert.Equal(0, closeAll.GetProperty("structuredContent").GetProperty("total").GetInt32());

        JsonElement unknown = await server.Send("tools/call", new { name = "pix_job_status", arguments = new { jobId = "missing-job" } });
        Assert.True(unknown.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Contains("Unknown job", unknown.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("unknown_job", unknown.GetProperty("result").GetProperty("structuredContent").GetProperty("code").GetString());
        Assert.Equal("pix_jobs", unknown.GetProperty("result").GetProperty("structuredContent").GetProperty("nextCalls")[0].GetProperty("tool").GetString());
        JsonElement invalidBounds = (await server.Send("tools/call", new { name = "pix_gpu_events", arguments = new { handle = "missing", limit = -1 } })).GetProperty("result");
        Assert.True(invalidBounds.GetProperty("isError").GetBoolean());
        Assert.Equal("invalid_arguments", invalidBounds.GetProperty("structuredContent").GetProperty("code").GetString());
        JsonElement invalid = await server.Send("tools/call", new { name = "pix_job_status", arguments = new { } });
        Assert.True(invalid.TryGetProperty("error", out _) ||
                    (invalid.GetProperty("result").TryGetProperty("isError", out isError) && isError.GetBoolean()));

        Assert.Equal(0, await server.Close());
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OlderPixThanTheBuildExitsWithCodeTwoUnlessStrictModeIsOff(bool strictOff)
    {
        Skip.If(PixDiscovery.InstallDir is null, "PIX Preview install required for server startup.");
        using DirectoryCleanup directory = new();
        // A stand-in install: the real managed assemblies (they load without native PIX) plus a version.xml older than the build.
        foreach (string dll in Directory.GetFiles(PixDiscovery.InstallDir!, "PixApiCsExt*.dll"))
            File.Copy(dll, Path.Combine(directory.Path, Path.GetFileName(dll)));
        File.WriteAllText(Path.Combine(directory.Path, "version.xml"),
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><PixVersion><Version>2606.17-preview</Version><Build>WinPIX_release_2606.17001</Build><Commit>abc</Commit></PixVersion>");
        ProcessStartInfo start = ServerStart();
        start.Environment["PIX_DIR"] = directory.Path;
        if (strictOff)
        {
            start.Environment["PIXMCP_PIX_STRICT"] = "0";
            await using var server = new StdioClient(start);
            await server.Send("initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "strict-test", version = "1" } });
            await server.Notify("notifications/initialized");
            JsonElement info = (await server.Send("tools/call", new { name = "pix_info", arguments = new { } })).GetProperty("result").GetProperty("structuredContent").GetProperty("pix");
            Assert.Equal("olderThanBuild", info.GetProperty("compatibility").GetProperty("state").GetString());
            Assert.Equal("off", info.GetProperty("compatibility").GetProperty("strictMode").GetString());
            Assert.False(info.GetProperty("compatibility").GetProperty("exit").GetBoolean());
            Assert.Equal("2606.17-preview", info.GetProperty("installVersion").GetProperty("xmlVersion").GetString());
            Assert.Equal("WinPIX_release_2606.17001", info.GetProperty("installVersion").GetProperty("build").GetString());
            Assert.Equal(directory.Path, info.GetProperty("installDir").GetString());
            Assert.Equal(0, await server.Close());
            return;
        }
        using var process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(2, process.ExitCode);
            Assert.Empty(await stdout);
            string error = await stderr;
            Assert.Contains("pixmcp:", error);
            Assert.Contains("2606.17-preview", error);
            Assert.Contains("older than the PIX build", error);
            Assert.Contains("PIXMCP_PIX_STRICT=0", error);
            Assert.DoesNotContain("Unhandled exception", error);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    [SkippableFact]
    public async Task ToolsetsHideToolsAndPromptsAndSummaryModeShortensText()
    {
        Skip.If(PixDiscovery.InstallDir is null, "The server only starts with a PIX Preview install; discovery found none.");
        ProcessStartInfo start = ServerStart();
        start.Environment["PIXMCP_TOOLSETS"] = "gpu";
        start.Environment["PIXMCP_TEXT_CONTENT"] = "summary";
        await using var server = new StdioClient(start);
        await server.Send("initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "pixmcp-tests", version = "1" } });
        await server.Notify("notifications/initialized");

        JsonElement tools = (await server.Send("tools/list")).GetProperty("result").GetProperty("tools");
        string?[] names = tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToArray();
        Assert.Contains("pix_gpu_overview", names);
        Assert.Contains("pix_info", names);
        Assert.DoesNotContain("pix_dump_open", names);
        Assert.DoesNotContain("pix_timing_sql", names);
        Assert.DoesNotContain("pix_gpu_sql", names);

        JsonElement disabled = (await server.Send("tools/call", new { name = "pix_dump_open", arguments = new { path = "missing.dxdmp_preview" } })).GetProperty("result");
        Assert.True(disabled.GetProperty("isError").GetBoolean());
        Assert.Equal("tool_disabled", disabled.GetProperty("structuredContent").GetProperty("code").GetString());

        JsonElement info = (await server.Send("tools/call", new { name = "pix_info", arguments = new { } })).GetProperty("result");
        string text = info.GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(text), 1, 512);
        Assert.Contains("Full result in structuredContent", text);
        JsonElement structured = info.GetProperty("structuredContent");
        Assert.Equal(new[] { "gpu", "session" }, structured.GetProperty("toolsets").GetProperty("enabled").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal("summary", structured.GetProperty("textContent").GetString());
        OutputSchemaTests.AssertMatches(structured, tools.EnumerateArray().Single(t => t.GetProperty("name").GetString() == "pix_info").GetProperty("outputSchema"));

        string?[] prompts = (await server.Send("prompts/list")).GetProperty("result").GetProperty("prompts").EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToArray();
        Assert.Contains("pix_frame_budget", prompts);
        Assert.DoesNotContain("pix_crash_triage", prompts);
        Assert.DoesNotContain("pix_cpu_vs_gpu", prompts);
        JsonElement hidden = await server.Send("prompts/get", new { name = "pix_crash_triage" });
        Assert.True(hidden.TryGetProperty("error", out _), hidden.GetRawText());
        Assert.Equal(0, await server.Close());
    }

    [SkippableFact]
    public async Task ForwardsReplayProgressToAClientThatSendsAProgressToken()
    {
        Skip.If(PixDiscovery.InstallDir is null, "The server only starts with a PIX Preview install; discovery found none.");
        string? capture = TestArtifacts.Capture;
        Skip.If(capture is null || !TestArtifacts.AnalysisEnabled, "Set PIX_TEST_CAPTURE and PIX_TEST_ANALYSIS=1 to replay a capture.");
        // PIX refuses (0x8ABC000B) a capture that another process already has open, and the in-process native tests may hold this one.
        using var directory = new DirectoryCleanup();
        string copy = Path.Combine(directory.Path, "progress.wpix");
        File.Copy(capture!, copy);
        await using var server = new StdioClient(ServerStart());
        await server.Send("initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "pixmcp-tests", version = "1" } });
        await server.Notify("notifications/initialized");
        JsonElement opened = (await server.Send("tools/call", new { name = "pix_gpu_open", arguments = new { path = copy } })).GetProperty("result");
        JsonElement openedContent = default, openedHandle = default;
        Assert.True(opened.TryGetProperty("structuredContent", out openedContent) && openedContent.TryGetProperty("handle", out openedHandle), opened.GetRawText());
        string handle = openedHandle.GetString()!;

        JsonElement prepared = (await server.Send("tools/call", new Dictionary<string, object?>
        {
            ["name"] = "pix_gpu_timing_prepare",
            ["arguments"] = new { handle, waitSeconds = 600 },
            ["_meta"] = new { progressToken = "timing-progress" },
        }, TimeSpan.FromMinutes(10))).GetProperty("result");
        Assert.False(prepared.TryGetProperty("isError", out JsonElement isError) && isError.GetBoolean(), prepared.GetRawText());
        Assert.True(prepared.GetProperty("structuredContent").GetProperty("status").GetString() == "succeeded", prepared.GetRawText());
        JsonElement[] progress = server.Notifications
            .Where(n => n.GetProperty("method").GetString() == "notifications/progress"
                && n.GetProperty("params").GetProperty("progressToken").GetString() == "timing-progress").ToArray();
        Assert.True(progress.Length >= 2, $"{progress.Length} progress notifications");
        double[] values = progress.Select(n => n.GetProperty("params").GetProperty("progress").GetDouble()).ToArray();
        Assert.Equal(values.Order(), values);
        Assert.Equal(100, values[^1]);
        Assert.Equal(0, await server.Close());
    }

    private static ProcessStartInfo ServerStart()
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "PixMcp.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        if (PixDiscovery.InstallDir is not null) start.Environment["PIX_DIR"] = PixDiscovery.InstallDir;
        return start;
    }

    private sealed class StdioClient : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _stderr;
        private int _id;
        private bool _closed;
        /// <summary>Server notifications read while waiting for responses.</summary>
        public List<JsonElement> Notifications { get; } = new();

        public StdioClient(ProcessStartInfo start)
        {
            _process = Process.Start(start)!;
            _stderr = _process.StandardError.ReadToEndAsync();
        }

        public async Task Notify(string method)
        {
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", method }));
            await _process.StandardInput.FlushAsync();
        }

        public async Task<JsonElement> Send(string method, object? parameters = null, TimeSpan? timeout = null)
        {
            int id = ++_id;
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
            await _process.StandardInput.FlushAsync();
            using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
            while (true)
            {
                string? line = await _process.StandardOutput.ReadLineAsync(deadline.Token);
                Assert.True(line is not null, "Server closed stdout: " + (_stderr.IsCompleted ? await _stderr : "stderr still open"));
                using JsonDocument document = JsonDocument.Parse(line!);
                JsonElement response = document.RootElement;
                Assert.Equal("2.0", response.GetProperty("jsonrpc").GetString());
                if (response.TryGetProperty("id", out JsonElement responseId) && responseId.GetInt32() == id)
                    return response.Clone();
                if (!response.TryGetProperty("id", out _) && response.TryGetProperty("method", out _)) Notifications.Add(response.Clone());
            }
        }

        public async Task<int> Close()
        {
            if (!_closed)
            {
                _closed = true;
                _process.StandardInput.Close();
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            return _process.ExitCode;
        }

        public async ValueTask DisposeAsync()
        {
            try { await Close(); }
            finally
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync();
                }
                _process.Dispose();
            }
        }
    }

    private sealed class DirectoryCleanup : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("pixmcp-stdio-");
        public string Path => _directory.FullName;
        public void Dispose() => _directory.Delete(recursive: true);
    }
}
