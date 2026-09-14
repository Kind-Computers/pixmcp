using PixMcp.Pix;
using Xunit;

namespace PixMcp.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessWorkingDirectoryCollection
{
    public const string Name = "process working directory";
}

/// <summary>These tests move the process working directory, so they run outside the parallel collections.</summary>
[Collection(ProcessWorkingDirectoryCollection.Name)]
public sealed class ServerPathsTests
{
    [Fact]
    public void ClientPathsResolveAgainstTheStartDirectoryWhileCompilersRunInTheirOwnFolder()
    {
        string start = ServerPaths.StartDirectory;
        string expected = Path.GetFullPath(Path.Combine("captures", "frame.wpix"), start);
        Assert.Equal(expected, ServerPaths.Full(Path.Combine("captures", "frame.wpix")));
        string absolute = Path.Combine(Path.GetTempPath(), "absolute.wpix");
        Assert.Equal(Path.GetFullPath(absolute), ServerPaths.Full(absolute));

        string before = Environment.CurrentDirectory;
        string probe = "pixmcp-working-directory-probe-" + Guid.NewGuid().ToString("N") + ".txt";
        using (ServerPaths.CompilerWorkingDirectory())
        {
            Assert.Equal(expected, ServerPaths.Full(Path.Combine("captures", "frame.wpix")));
            using (ServerPaths.CompilerWorkingDirectory()) { }
            File.WriteAllText(probe, "written relative to the compiler folder");
        }
        string written = Path.Combine(ServerPaths.CompilerReportDirectory, probe);
        try
        {
            Assert.True(File.Exists(written));
            Assert.Equal(before, Environment.CurrentDirectory);
        }
        finally
        {
            File.Delete(written);
        }
    }

    [Fact]
    public void ToolsResolveClientPathsThroughServerPaths()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "pixmcp.sln"))) root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("pixmcp.sln not found above the test output.");
        string[] offenders = Directory.EnumerateFiles(Path.Combine(root, "src", "PixMcp", "Tools"), "*.cs")
            .Where(file => File.ReadAllText(file).Contains("Path.GetFullPath(", StringComparison.Ordinal))
            .Select(Path.GetFileName).ToArray()!;
        Assert.Empty(offenders);
    }
}
