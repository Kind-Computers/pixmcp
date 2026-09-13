using PixMcp.Pix;
using Xunit;

namespace PixMcp.Tests;

public sealed class ToolsetTests
{
    [Fact]
    public void EveryRegisteredToolBelongsToAToolset()
    {
        Assert.True(ToolRegistry.AllNames.Count > 100);
        foreach (string tool in ToolRegistry.AllNames)
            Assert.True(Toolsets.For(tool) is string set && Toolsets.Names.Contains(set), tool);
        Assert.Equal(Toolsets.Names.Order(StringComparer.Ordinal), Toolsets.Names);
        Assert.Equal(Toolsets.Names, ToolRegistry.AllNames.Select(t => Toolsets.For(t)!).Distinct().Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("pix_info", "session")]
    [InlineData("pix_job_wait", "session")]
    [InlineData("pix_result_read", "session")]
    [InlineData("pix_gpu_overview", "gpu")]
    [InlineData("pix_capture_upgrade", "gpu")]
    [InlineData("pix_gpu_shader_code", "gpu")]
    [InlineData("pix_gpu_shader_profile", "shader")]
    [InlineData("pix_gpu_sql", "gpusql")]
    [InlineData("pix_gpu_sql_populate", "gpusql")]
    [InlineData("pix_gpu_drpix_run", "drpix")]
    [InlineData("pix_timing_sql", "timing")]
    [InlineData("pix_correlate", "timing")]
    [InlineData("pix_dump_triage", "dump")]
    [InlineData("pix_device_launch", "device")]
    [InlineData("pix_csv_compare", "csv")]
    public void ToolsMapToTheirToolset(string tool, string toolset) => Assert.Equal(toolset, Toolsets.For(tool));

    [Fact]
    public void ParsingAcceptsNamesAndAllAndReportsUnknownToolsets()
    {
        ServerOptions two = ServerOptions.Parse(name => name == ServerOptions.ToolsetsVariable ? " GPU, timing " : null);
        Assert.Empty(two.Problems);
        Assert.Equal(new[] { "gpu", "session", "timing" }, two.Toolsets!.Order(StringComparer.Ordinal));
        Assert.Equal(ServerOptions.FromEnvironment, two.ToolsetsSource);
        Assert.Equal("gpu,session,timing", two.Describe().Toolsets!.Value);

        Assert.Null(ServerOptions.Parse(name => name == ServerOptions.ToolsetsVariable ? "all" : null).Toolsets);
        Assert.Null(ServerOptions.Parse(name => name == ServerOptions.ToolsetsVariable ? "" : null).Toolsets);
        ServerOptions defaults = ServerOptions.Parse(_ => null);
        Assert.Null(defaults.Toolsets);
        Assert.Equal(ServerOptions.FromDefault, defaults.ToolsetsSource);
        Assert.Equal("all", defaults.Describe().Toolsets!.Value);

        ServerOptions bad = ServerOptions.Parse(name => name == ServerOptions.ToolsetsVariable ? "gpu bogus" : null);
        Assert.Null(bad.Toolsets);
        string problem = Assert.Single(bad.Problems);
        Assert.Contains(ServerOptions.ToolsetsVariable, problem);
        Assert.Contains("'bogus'", problem);
        Assert.Contains("gpusql", problem);
    }

    [Fact]
    public void DisabledToolsetsHideToolsFromTheRegistryAndExplainTheError()
    {
        using IDisposable scope = ServerOptions.Override(ServerOptions.Current with { Toolsets = new HashSet<string> { "gpu", "session" } });
        Assert.True(ToolRegistry.Has("pix_gpu_overview", "handle"));
        Assert.True(ToolRegistry.Has("pix_job_wait"));
        Assert.False(ToolRegistry.Has("pix_dump_open"));
        Assert.False(ToolRegistry.Has("pix_timing_sql"));
        Assert.False(ToolRegistry.Accepts(new ToolCallDto("pix_gpu_sql", new { handle = "gpu-1" })));
        Assert.Contains("pix_dump_open", ToolRegistry.AllNames);
        Assert.False(Toolsets.Enabled("pix_correlate"));

        ToolsetsInfoDto info = Toolsets.Describe();
        Assert.Equal(new[] { "gpu", "session" }, info.Enabled);
        Assert.Contains("dump", info.Disabled);
        Assert.Equal(ToolRegistry.AllNames.Count(t => Toolsets.For(t) is not ("gpu" or "session")), info.HiddenTools);

        PixToolException error = PixErrors.ToolDisabled("pix_dump_open");
        Assert.Equal(PixErrors.Codes.ToolDisabled, error.Detail.Code);
        Assert.Contains("'dump'", error.Detail.Message);
        Assert.Contains(ServerOptions.ToolsetsVariable, error.Detail.Message);
        Assert.Equal("pix_info", Assert.Single(error.Detail.NextCalls).Tool);
    }

    [Fact]
    public void CorrelationNeedsBothTheTimingAndGpuToolsets()
    {
        Assert.False(Toolsets.Enabled("pix_correlate", ServerOptions.Current with { Toolsets = new HashSet<string> { "timing", "session" } }));
        Assert.True(Toolsets.Enabled("pix_correlate", ServerOptions.Current with { Toolsets = new HashSet<string> { "timing", "gpu", "session" } }));
        Assert.True(Toolsets.Enabled("pix_info", ServerOptions.Current with { Toolsets = new HashSet<string> { "session" } }));
        Assert.True(Toolsets.Enabled("not_a_tool", ServerOptions.Current with { Toolsets = new HashSet<string> { "session" } }));
    }
}
