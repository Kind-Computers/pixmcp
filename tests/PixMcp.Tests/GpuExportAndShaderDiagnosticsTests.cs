using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PixMcp.Pix;
using PixMcp.Tools;
using Xunit;

namespace PixMcp.Tests;

public sealed class GpuExportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pixmcp-export-tests-" + Guid.NewGuid().ToString("N"));

    public GpuExportTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ArgumentsPreserveLiteralPathsAndPackageOptionsAreOptIn()
    {
        const string capture = "C:\\capture folder\\$(literal)`frame.wpix";
        const string output = "C:\\export folder\\project; $(literal)";
        ProcessStartInfo defaults = GpuExportTools.BuildStartInfo("pixtool.exe", capture, output, new(false, false, false));
        Assert.False(defaults.UseShellExecute);
        Assert.True(defaults.CreateNoWindow);
        Assert.Equal(ProcessWindowStyle.Hidden, defaults.WindowStyle);
        Assert.Equal(new[] { "--output=quiet", "--log=off", "open-capture", capture, "export-to-cpp", output }, defaults.ArgumentList);
        PixToolProcess.PrepareArguments(defaults);
        Assert.Empty(defaults.ArgumentList);
        Assert.Contains("\"" + capture + "\"", defaults.Arguments);
        Assert.Contains("\"" + output + "\"", defaults.Arguments);
        ProcessStartInfo optedIn = GpuExportTools.BuildStartInfo("pixtool.exe", capture, output, new(true, true, true));
        Assert.Contains("--use-winpixeventruntime", optedIn.ArgumentList);
        Assert.Contains("--use-agilitySdk", optedIn.ArgumentList);
        Assert.Contains("--use-replay-time-executeindirect-buffers", optedIn.ArgumentList);
        Assert.DoesNotContain("--force", optedIn.ArgumentList);
    }

    [Fact]
    public void OutputMustBeNewWithAnExistingParent()
    {
        string output = Path.Combine(_root, "project");
        Assert.Equal(output, GpuExportTools.ValidateOutputDirectory(output + Path.DirectorySeparatorChar));
        Assert.False(Directory.Exists(output));
        Assert.Equal("invalid_arguments", Assert.Throws<PixToolException>(() => GpuExportTools.ValidateOutputDirectory(Path.Combine(output, "nested"))).Detail.Code);
        Assert.Equal("invalid_arguments", Assert.Throws<PixToolException>(() => GpuExportTools.ValidateOutputDirectory(Path.GetPathRoot(_root)!)).Detail.Code);
        Assert.Throws<PixToolException>(() => GpuExportTools.ValidateOutputDirectory(" "));
        Assert.Throws<PixToolException>(() => GpuExportTools.ValidateOutputDirectory(output + "\n"));
        Directory.CreateDirectory(output);
        Assert.Equal("output_exists", Assert.Throws<PixToolException>(() => GpuExportTools.ValidateOutputDirectory(output)).Detail.Code);
        string file = Path.Combine(_root, "file");
        File.WriteAllText(file, "user data");
        Assert.Equal("output_exists", Assert.Throws<PixToolException>(() => GpuExportTools.ValidateOutputDirectory(file)).Detail.Code);
        Assert.Equal("user data", File.ReadAllText(file));
    }

    [Fact]
    public void ExportRechecksDestinationAndRequiresCMakeBeforeSuccess()
    {
        string output = Path.Combine(_root, "new-project");
        string cmake = GpuExportTools.ExportToDirectory(output, () =>
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "CMakeLists.txt"), "project(CapturedFrame)");
        }, () => { }, _ => { });
        Assert.Equal(Path.Combine(output, "CMakeLists.txt"), cmake);
        bool launched = false;
        Assert.Equal("output_exists", Assert.Throws<PixToolException>(() => GpuExportTools.ExportToDirectory(output,
            () => launched = true, () => { }, _ => { })).Detail.Code);
        Assert.False(launched);
        string missing = Path.Combine(_root, "missing");
        Assert.Equal("export_missing_output", Assert.Throws<PixToolException>(() => GpuExportTools.ExportToDirectory(missing,
            () => { }, () => { }, _ => { })).Detail.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedOrCancelledExportRetainsPartialOutputAndReportsItsPath(bool cancel)
    {
        string output = Path.Combine(_root, "partial");
        var messages = new List<string>();
        Exception expected = cancel ? new OperationCanceledException() : new PixToolException("export_failed", "Compiler backend failed");
        Exception? actual = Record.Exception(() => GpuExportTools.ExportToDirectory(output, () =>
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "partial.cpp"), "captured data");
            throw expected;
        }, () => { }, messages.Add));
        Assert.Same(expected, actual);
        Assert.Equal("captured data", File.ReadAllText(Path.Combine(output, "partial.cpp")));
        Assert.Contains(messages, message => message.Contains(output, StringComparison.Ordinal));
    }

    [Fact]
    public void MissingCMakeAlsoPreservesPartialOutput()
    {
        string output = Path.Combine(_root, "partial");
        var messages = new List<string>();
        Assert.Equal("export_missing_output", Assert.Throws<PixToolException>(() => GpuExportTools.ExportToDirectory(output,
            () => Directory.CreateDirectory(output), () => { }, messages.Add)).Detail.Code);
        Assert.True(Directory.Exists(output));
        Assert.Contains(messages, message => message.Contains(output, StringComparison.Ordinal));
    }

    [Fact]
    public void SharedProcessReportsExportSpecificErrors()
    {
        Assert.Equal("export_unavailable", Assert.Throws<PixToolException>(() => PixToolProcess.Executable("export", _root)).Detail.Code);
        var missing = PixToolProcess.StartInfo(Path.Combine(_root, "missing.exe"), []);
        Assert.Equal("export_start_failed", Assert.Throws<PixToolException>(() => PixToolProcess.Run(missing, TimeSpan.FromSeconds(20), default, _ => { }, "export")).Detail.Code);
        Assert.Equal("export_failed", Assert.Throws<PixToolException>(() => PixToolProcess.Run(Shell("exit 7"), TimeSpan.FromSeconds(20), default, _ => { }, "export")).Detail.Code);
        Assert.Equal("export_timeout", Assert.Throws<PixToolException>(() => PixToolProcess.Run(Shell("Start-Sleep -Seconds 30"), TimeSpan.FromMilliseconds(200), default, _ => { }, "export")).Detail.Code);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => PixToolProcess.Run(Shell("exit 0"), TimeSpan.FromSeconds(20), cancellation.Token, _ => { }, "export"));
    }

    [Fact]
    public void CaptureCopyIsIndependentAndNeverOverwrites()
    {
        string source = Path.Combine(_root, "source.wpix"), destination = Path.Combine(_root, "copy.wpix");
        File.WriteAllBytes(source, [1, 2, 3]);
        int cancellationChecks = 0;
        PixToolProcess.CopyCapture(source, destination, () => cancellationChecks++);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(destination));
        Assert.True(cancellationChecks > 0);
        Assert.Throws<IOException>(() => PixToolProcess.CopyCapture(source, destination, () => { }));
        File.WriteAllBytes(source, [4]);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(destination));
    }

    private static ProcessStartInfo Shell(string command)
        => PixToolProcess.StartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            ["-NoProfile", "-NonInteractive", "-Command", command]);

    public void Dispose()
    {
        // This directory is created by the fixture from a constant prefix and a fresh GUID, never from tool input.
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}

public sealed class ShaderDiagnosticsTests
{
    private static readonly ShaderRef Reference = new(new("gpu-1", 0, 12), 1);

    [Fact]
    public void DefaultsProbeOnlyTheThreeKnownKindsAndSubsetsAreDeduplicated()
    {
        Assert.Equal(new[] { ShaderCodeKind.HLSL, ShaderCodeKind.IL, ShaderCodeKind.ISA }, ShaderDiagnosticsTools.NormalizeCodeTypes(null));
        Assert.Equal(new[] { ShaderCodeKind.ISA, ShaderCodeKind.HLSL }, ShaderDiagnosticsTools.NormalizeCodeTypes([ShaderCodeKind.ISA, ShaderCodeKind.ISA, ShaderCodeKind.HLSL]));
        Assert.Equal("invalid_arguments", Assert.Throws<PixToolException>(() => ShaderDiagnosticsTools.NormalizeCodeTypes([])).Detail.Code);
        Assert.Throws<PixToolException>(() => ShaderDiagnosticsTools.NormalizeCodeTypes([(ShaderCodeKind)99]));
    }

    [Fact]
    public void DiagnosticsDistinguishPresentEmptyAndFailedQueriesAndContinueAfterFailure()
    {
        var queried = new List<ShaderCodeKind>();
        int pdbQueries = 0;
        ShaderDiagnosticsDto result = ShaderDiagnosticsTools.Probe(Reference, "42", "PIXEL", ShaderDiagnosticsTools.NormalizeCodeTypes(null),
            () => { pdbQueries++; return [0xFE, 0xDC, 0x01]; }, kind =>
            {
                queried.Add(kind);
                return kind switch
                {
                    ShaderCodeKind.HLSL => 0,
                    ShaderCodeKind.IL => throw new COMException("No IL interface", unchecked((int)0x80004002)),
                    _ => 2,
                };
            });
        Assert.Equal(1, pdbQueries);
        Assert.Equal(new[] { ShaderCodeKind.HLSL, ShaderCodeKind.IL, ShaderCodeKind.ISA }, queried);
        Assert.Equal(Reference, result.ShaderRef);
        Assert.Equal("available", result.Pdb.State);
        Assert.Equal("fedc01", result.Pdb.Hash);
        Assert.Equal(new[] { "absent", "unavailable", "available" }, result.CodeAvailability.Select(code => code.State));
        Assert.Equal(0UL, result.CodeAvailability[0].NodeCount);
        Assert.Null(result.CodeAvailability[1].NodeCount);
        Assert.Equal("unsupported_feature", result.CodeAvailability[1].ErrorCode);
        Assert.Equal("0x80004002", result.CodeAvailability[1].Hresult);
        ToolCallDto call = Assert.Single(result.NextCalls);
        Assert.Equal("pix_gpu_shader_code", call.Tool);
        JsonElement arguments = JsonSerializer.Deserialize<JsonElement>(Json.Serialize(call.Arguments));
        Assert.Equal("ISA", arguments.GetProperty("codeType").GetString());
        Assert.Equal(-1, arguments.GetProperty("nodeIndex").GetInt32());
        Assert.Contains(result.Guidance, message => message.Contains("does not confirm", StringComparison.Ordinal));
        Assert.Contains(result.Guidance, message => message.Contains("does not identify a single cause", StringComparison.Ordinal));
    }

    [Fact]
    public void AbsentOrFailingPdbDoesNotHideAvailableCode()
    {
        ShaderDiagnosticsDto absent = ShaderDiagnosticsTools.Probe(Reference, "42", "PIXEL", [ShaderCodeKind.HLSL], () => [], _ => 1);
        Assert.Equal("absent", absent.Pdb.State);
        Assert.Null(absent.Pdb.Hash);
        Assert.Equal("available", Assert.Single(absent.CodeAvailability).State);
        ShaderDiagnosticsDto failed = ShaderDiagnosticsTools.Probe(Reference, "42", "PIXEL", [ShaderCodeKind.IL],
            () => throw new InvalidOperationException("PDB unavailable"), kind => { Assert.Equal(ShaderCodeKind.IL, kind); return 1; });
        Assert.Equal("unavailable", failed.Pdb.State);
        Assert.Contains("PDB unavailable", failed.Pdb.Reason);
        Assert.Equal("available", Assert.Single(failed.CodeAvailability).State);
    }

    [Fact]
    public void CancellationIsNeverReportedAsMissingSymbols()
    {
        Assert.ThrowsAny<OperationCanceledException>(() => ShaderDiagnosticsTools.Probe(Reference, "42", "PIXEL", [ShaderCodeKind.HLSL],
            () => throw new OperationCanceledException(), _ => 1));
        Assert.Throws<COMException>(() => ShaderDiagnosticsTools.Probe(Reference, "42", "PIXEL", [ShaderCodeKind.HLSL],
            () => [], _ => throw new COMException("Cancelled", PixErrors.E_ABORT)));
    }

    [Fact]
    public async Task InvalidReferenceFailsBeforeAnyPreparationIsScheduled()
    {
        using var worker = new PixWorker();
        var session = new PixSession(worker, NullLogger<PixSession>.Instance);
        var jobs = new JobManager(worker, session);
        try
        {
            await Assert.ThrowsAsync<PixToolException>(() => ShaderDiagnosticsTools.Diagnostics(session, jobs, null!));
            await Assert.ThrowsAsync<PixToolException>(() => ShaderDiagnosticsTools.Diagnostics(session, jobs, Reference));
            Assert.Null(jobs.Running);
        }
        finally { await worker.Run(() => session.Dispose()); }
    }
}
