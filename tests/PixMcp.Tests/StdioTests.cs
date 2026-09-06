using System.Diagnostics;
using System.Text.Json;
using PixMcp.Pix;
using Xunit;

namespace PixMcp.Tests;

public class StdioTests
{
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

    [Fact]
    public async Task ListsToolsAndResourcesHandlesErrorsAndShutsDownCleanly()
    {
        await using var server = new StdioClient(ServerStart());
        JsonElement initialized = await server.Send("initialize", new
        {
            protocolVersion = "2025-06-18", capabilities = new { },
            clientInfo = new { name = "pixmcp-tests", version = "1" },
        });
        Assert.Equal("pixmcp", initialized.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
        await server.Notify("notifications/initialized");

        JsonElement tools = (await server.Send("tools/list")).GetProperty("result").GetProperty("tools");
        string?[] names = tools.EnumerateArray().Select(tool => tool.GetProperty("name").GetString()).ToArray();
        Assert.Contains("pix_info", names);
        Assert.Contains("pix_gpu_open", names);
        Assert.Contains("pix_job_status", names);
        foreach (JsonElement tool in tools.EnumerateArray())
            Assert.Equal("object", tool.GetProperty("outputSchema").GetProperty("type").GetString());
        JsonElement eventSchema = tools.EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == "pix_gpu_events")
            .GetProperty("outputSchema").GetProperty("properties");
        Assert.True(eventSchema.TryGetProperty("nextOffset", out _));
        Assert.True(eventSchema.GetProperty("items").GetProperty("items").GetProperty("properties").TryGetProperty("queueIndex", out _));

        JsonElement resources = (await server.Send("resources/list")).GetProperty("result").GetProperty("resources");
        Assert.Contains(resources.EnumerateArray(), resource => resource.GetProperty("uri").GetString() == "pix://handles");
        JsonElement templates = (await server.Send("resources/templates/list")).GetProperty("result").GetProperty("resourceTemplates");
        Assert.Contains(templates.EnumerateArray(), template => template.GetProperty("uriTemplate").GetString() == "pix://handles/{handle}");
        JsonElement handles = (await server.Send("resources/read", new { uri = "pix://handles" })).GetProperty("result").GetProperty("contents");
        Assert.Equal("[]", Assert.Single(handles.EnumerateArray()).GetProperty("text").GetString());

        JsonElement info = await server.Send("tools/call", new { name = "pix_info", arguments = new { } });
        Assert.False(info.GetProperty("result").TryGetProperty("isError", out JsonElement isError) && isError.GetBoolean());
        using var infoText = JsonDocument.Parse(info.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.Equal(PixDiscovery.InstallDir, infoText.RootElement.GetProperty("pix").GetProperty("installDir").GetString());
        Assert.True(JsonElement.DeepEquals(infoText.RootElement, info.GetProperty("result").GetProperty("structuredContent")), info.GetRawText());

        JsonElement jobs = (await server.Send("tools/call", new { name = "pix_jobs", arguments = new { } })).GetProperty("result");
        Assert.Equal("[]", jobs.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Empty(jobs.GetProperty("structuredContent").GetProperty("items").EnumerateArray());

        JsonElement unknown = await server.Send("tools/call", new { name = "pix_job_status", arguments = new { jobId = "missing-job" } });
        Assert.True(unknown.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Contains("Unknown job", unknown.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
        Assert.False(unknown.GetProperty("result").TryGetProperty("structuredContent", out _));
        JsonElement invalid = await server.Send("tools/call", new { name = "pix_job_status", arguments = new { } });
        Assert.True(invalid.TryGetProperty("error", out _) ||
                    (invalid.GetProperty("result").TryGetProperty("isError", out isError) && isError.GetBoolean()));

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

        public async Task<JsonElement> Send(string method, object? parameters = null)
        {
            int id = ++_id;
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
            await _process.StandardInput.FlushAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (true)
            {
                string? line = await _process.StandardOutput.ReadLineAsync(timeout.Token);
                Assert.True(line is not null, "Server closed stdout: " + (_stderr.IsCompleted ? await _stderr : "stderr still open"));
                using JsonDocument document = JsonDocument.Parse(line!);
                JsonElement response = document.RootElement;
                Assert.Equal("2.0", response.GetProperty("jsonrpc").GetString());
                if (response.TryGetProperty("id", out JsonElement responseId) && responseId.GetInt32() == id)
                    return response.Clone();
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
