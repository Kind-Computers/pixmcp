using System.Text.RegularExpressions;
using Xunit;

namespace PixMcp.Tests;

public sealed class ToolReferenceTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("pixmcp-tool-reference-");

    public void Dispose() => _directory.Delete(recursive: true);

    [Fact]
    public void ReferenceIsDeterministicAndDocumentsEveryPinnedToolOnce()
    {
        string reference = ToolReference.Generate();
        Assert.Equal(reference, ToolReference.Generate());
        string[] headings = Regex.Matches(reference, "^### (pix_[a-z0-9_]+)$", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToArray();
        string[] pinned = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tool-names.txt")).Where(l => l.Length > 0).ToArray();
        Assert.Equal(pinned, headings.Order(StringComparer.Ordinal));
        Assert.DoesNotContain("| `session` |", reference[reference.IndexOf("\n## ", StringComparison.Ordinal)..]);
        Assert.DoesNotContain("| `cancellationToken` |", reference);
        Assert.Contains("| `handle` | string | required |", reference);
        Assert.Contains("| `worker_busy` | yes |", reference);
        Assert.Contains("| `invalid_arguments` | no |", reference);
        Assert.DoesNotContain("\r", reference);
    }

    [Fact]
    public void CheckReportsMissingStaleAndCurrentReferencesAndUsageErrors()
    {
        string path = Path.Combine(_directory.FullName, "docs", "tools.md");
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(2, ToolReference.Run([path, "--check"], output, error));
        Assert.Equal(0, ToolReference.Run([path], output, error));
        Assert.Equal(0, ToolReference.Run([path, "--check"], output, error));
        File.WriteAllText(path, File.ReadAllText(path).Replace("\n", "\r\n", StringComparison.Ordinal));
        Assert.Equal(0, ToolReference.Run(["--check", path], output, error));
        File.AppendAllText(path, "edited\n");
        Assert.Equal(2, ToolReference.Run([path, "--check"], output, error));
        Assert.Contains("is stale", error.ToString());
        Assert.Equal(1, ToolReference.Run([], output, error));
        Assert.Equal(1, ToolReference.Run([path, "--bogus"], output, error));
    }

    [Fact]
    public void CommittedReferenceIsCurrent()
    {
        string root = TestArtifacts.Root ?? throw new Xunit.Sdk.XunitException("Repository root not found.");
        string path = Path.Combine(root, "docs", "tools.md");
        Assert.True(File.Exists(path) && File.ReadAllText(path).ReplaceLineEndings("\n") == ToolReference.Generate(),
            "docs/tools.md is stale; regenerate with PixMcp.exe --tool-reference docs/tools.md");
    }
}
