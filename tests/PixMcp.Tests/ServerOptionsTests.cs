using PixMcp.Pix;
using Xunit;

namespace PixMcp.Tests;

public sealed class ServerOptionsTests
{
    private static ServerOptions Parse(params (string Name, string Value)[] variables)
    {
        var environment = variables.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
        return ServerOptions.Parse(name => environment.GetValueOrDefault(name));
    }

    [Fact]
    public void DefaultsAreReportedWithTheirSource()
    {
        ServerOptions options = Parse();
        Assert.Empty(options.Problems);
        Assert.Equal(ServerOptions.DefaultInlineResultBytes, options.InlineResultBytes);
        Assert.Equal(ServerOptions.DefaultMaxResultBytes, options.MaxResultBytes);
        Assert.Equal(ServerOptions.DefaultResultMemoryBytes, options.ResultMemoryBytes);
        Assert.Equal(ServerOptions.DefaultResultDiskBytes, options.ResultDiskBytes);
        Assert.Null(options.ResultDirectory);
        ServerOptionsSummary summary = options.Describe();
        Assert.Equal("default", summary.InlineResultBytes.Source);
        Assert.Equal(ServerOptions.MaxVariable, summary.MaxResultBytes.Variable);
    }

    [Fact]
    public void ExplicitValuesComeFromTheEnvironment()
    {
        ServerOptions options = Parse((ServerOptions.MaxVariable, "100000"), (ServerOptions.InlineVariable, "2048"),
            (ServerOptions.MemoryVariable, "0"), (ServerOptions.DiskVariable, "5000"));
        Assert.Empty(options.Problems);
        Assert.Equal(2048, options.InlineResultBytes);
        Assert.Equal("env", options.InlineSource);
        Assert.Equal(100000, options.MaxResultBytes);
        Assert.Equal(0, options.ResultMemoryBytes);
        Assert.Equal(5000, options.ResultDiskBytes);
    }

    [Fact]
    public void InlineDefaultFollowsASmallerMaximum()
    {
        ServerOptions options = Parse((ServerOptions.MaxVariable, "80"));
        Assert.Empty(options.Problems);
        Assert.Equal(80, options.InlineResultBytes);
        Assert.Equal(80, options.MaxResultBytes);
    }

    [Theory]
    [InlineData(ServerOptions.MaxVariable, "0")]
    [InlineData(ServerOptions.MaxVariable, "lots")]
    [InlineData(ServerOptions.InlineVariable, "512")]
    [InlineData(ServerOptions.InlineVariable, "9999999")]
    [InlineData(ServerOptions.MemoryVariable, "-1")]
    [InlineData(ServerOptions.DiskVariable, "2GB")]
    [InlineData(ServerOptions.DirectoryVariable, "relative/dir")]
    public void EachMalformedVariableYieldsOneMessageNamingIt(string variable, string value)
    {
        ServerOptions options = Parse((variable, value));
        string problem = Assert.Single(options.Problems);
        Assert.Contains(variable, problem);
        Assert.Contains(value, problem);
        // The default is kept so the process can still report the problem.
        Assert.Equal(ServerOptions.DefaultMaxResultBytes, options.MaxResultBytes);
        Assert.Equal(ServerOptions.DefaultInlineResultBytes, options.InlineResultBytes);
    }

    [Fact]
    public void BothBudgetsAtZeroIsOneMessageNamingBoth()
    {
        ServerOptions options = Parse((ServerOptions.MemoryVariable, "0"), (ServerOptions.DiskVariable, "0"));
        string problem = Assert.Single(options.Problems);
        Assert.Contains(ServerOptions.MemoryVariable, problem);
        Assert.Contains(ServerOptions.DiskVariable, problem);
        Assert.Equal(ServerOptions.DefaultResultMemoryBytes, options.ResultMemoryBytes);
    }

    [Fact]
    public void AbsoluteResultDirectoryIsCreated()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pixmcp-options-" + Guid.NewGuid().ToString("N"));
        try
        {
            ServerOptions options = Parse((ServerOptions.DirectoryVariable, directory));
            Assert.Empty(options.Problems);
            Assert.True(Directory.Exists(directory));
            Assert.Equal(Path.GetFullPath(directory), options.ResultDirectory);
            Assert.Equal("env", options.DirectorySource);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task OverrideIsScopedToTheAsyncFlow()
    {
        ServerOptions before = ServerOptions.Current;
        using (ServerOptions.Override(ServerOptions.With(inlineResultBytes: 4096)))
        {
            Assert.Equal(4096, ServerOptions.Current.InlineResultBytes);
            Assert.Equal(4096, ResultStore.TargetBytes);
            await Task.Yield();
            Assert.Equal(4096, ServerOptions.Current.InlineResultBytes);
            Assert.Equal(before.InlineResultBytes, await Task.Run(() => { using (ServerOptions.Override(before)) return ServerOptions.Current.InlineResultBytes; }));
        }
        Assert.Same(before, ServerOptions.Current);
    }
}
