using System.Text.RegularExpressions;
using PixMcp.Pix;
using Xunit;

namespace PixMcp.Tests;

/// <summary>Conventions the source must keep: stable error codes everywhere, every code documented.</summary>
public sealed class SourceConventionsTests
{
    private static string Root => TestArtifacts.Root ?? throw new InvalidOperationException("Repository root not found.");
    private static IEnumerable<string> ServerSources() => Directory.EnumerateFiles(Path.Combine(Root, "src", "PixMcp"), "*.cs", SearchOption.AllDirectories)
        .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));

    [Fact]
    public void NoBareMcpExceptionOutsidePixErrors()
    {
        var offenders = new List<string>();
        foreach (string path in ServerSources())
        {
            if (Path.GetFileName(path) == "PixErrors.cs") continue;
            string text = File.ReadAllText(path);
            foreach (Match match in Regex.Matches(text, @"new McpException\("))
                offenders.Add($"{Path.GetRelativePath(Root, path)}:{text[..match.Index].Count(c => c == '\n') + 1}");
        }
        Assert.Empty(offenders);
    }

    [Fact]
    public void NoLiteralErrorCodesOutsideTheRegistry()
    {
        var offenders = new List<string>();
        foreach (string path in ServerSources())
        {
            if (Path.GetFileName(path) == "PixErrors.cs") continue;
            string text = File.ReadAllText(path);
            foreach (Match match in Regex.Matches(text, @"new PixToolException\(""([a-z_]+)"""))
                offenders.Add($"{Path.GetRelativePath(Root, path)}: {match.Groups[1].Value}");
        }
        Assert.Empty(offenders);
    }

    [Fact]
    public void NoArgumentExceptionsInToolMethods()
    {
        var offenders = new List<string>();
        foreach (string path in Directory.EnumerateFiles(Path.Combine(Root, "src", "PixMcp", "Tools"), "*.cs"))
        {
            string text = File.ReadAllText(path);
            foreach (Match match in Regex.Matches(text, @"new Argument(OutOfRange)?Exception\("))
                offenders.Add($"{Path.GetRelativePath(Root, path)}:{text[..match.Index].Count(c => c == '\n') + 1}");
        }
        Assert.Empty(offenders);
    }

    [Fact]
    public void EveryErrorCodeIsDocumentedInTheReadme()
    {
        string readme = File.ReadAllText(Path.Combine(Root, "README.md"));
        Assert.True(PixErrors.Codes.All.Count >= 60);
        var missing = PixErrors.Codes.All.Where(code => !readme.Contains("`" + code + "`", StringComparison.Ordinal)).ToArray();
        Assert.Empty(missing);
        Assert.All(PixErrors.Codes.All, code => Assert.Matches("^[a-z][a-z0-9_]+$", code));
        Assert.Subset(PixErrors.Codes.All.ToHashSet(), PixErrors.Codes.Retryable.ToHashSet());
    }

    [Fact]
    public void ErrorDtosOnlyEverCarryRegisteredCodes()
    {
        Assert.Contains(PixErrors.Codes.InvalidArguments, PixErrors.Codes.All);
        Assert.Equal(PixErrors.Codes.InvalidArguments, PixErrors.ToDto(new ArgumentException("x")).Code);
        Assert.Equal(PixErrors.Codes.FileNotFound, PixErrors.ToDto(new FileNotFoundException("x")).Code);
        Assert.Equal(PixErrors.Codes.Timeout, PixErrors.ToDto(new TimeoutException()).Code);
        Assert.True(PixErrors.ToDto(new TimeoutException()).Retryable);
        Assert.Equal(PixErrors.Codes.PixError, PixErrors.ToDto(new InvalidOperationException("x")).Code);
    }

    [Fact]
    public void NotValidStateMapsToAnalysisRequiredOnlyForACaptureWithoutAnalysis()
    {
        var notValid = new System.Runtime.InteropServices.COMException("not valid", PixErrors.E_NOT_VALID_STATE);
        ErrorDto stopped = PixErrors.ToDto(notValid, "pix_gpu_pipeline_state", new PixErrors.AnalysisContext("gpu-1", false));
        Assert.Equal(PixErrors.Codes.AnalysisRequired, stopped.Code);
        Assert.Equal("pix_gpu_analysis_start", Assert.Single(stopped.NextCalls).Tool);
        Assert.Equal(PixErrors.Codes.InvalidState, PixErrors.ToDto(notValid, "pix_gpu_pipeline_state", new PixErrors.AnalysisContext("gpu-1", true)).Code);
        Assert.Equal(PixErrors.Codes.InvalidState, PixErrors.ToDto(notValid).Code);
        Assert.Equal(PixErrors.Hex(PixErrors.E_NOT_VALID_STATE), stopped.Hresult);
    }
}
