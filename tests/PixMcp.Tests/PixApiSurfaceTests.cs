using System.Text.RegularExpressions;
using PixMcp.Pix;
using Xunit;

namespace PixMcp.Tests;

public class PixApiSurfaceTests
{
    [Fact]
    public void ProbesTypesAndMembersByName()
    {
        (string Feature, string Type, string? Member, bool Expected)[] definitions =
        [
            ("stringType", "System.String", null, true),
            ("stringSubstring", "System.String", "Substring", true),
            ("missingMember", "System.String", "Frobnicate", true),
            ("missingType", "System.Nope", null, false),
            ("unexpectedlyPresent", "System.Int32", null, false),
        ];

        IReadOnlyList<ApiProbe> probes = PixApiSurface.ProbeAssembly(typeof(string).Assembly, definitions);

        Assert.Equal(definitions.Select(d => d.Feature), probes.Select(p => p.Feature));
        Assert.True(probes[0].Present);
        Assert.True(probes[1].Present);
        Assert.False(probes[2].Present);
        Assert.False(probes[3].Present);
        Assert.True(probes[4].Present);
        Assert.Equal(new[] { "missingMember", "unexpectedlyPresent" }, probes.Where(p => p.Drift).Select(p => p.Feature));
    }

    [Fact]
    public void DefinitionsAreUniqueAndFullyQualified()
    {
        Assert.Equal(PixApiSurface.Definitions.Count, PixApiSurface.Definitions.Select(d => d.Feature).Distinct().Count());
        Assert.All(PixApiSurface.Definitions, d => Assert.StartsWith("Microsoft.PIX.", d.Type));
    }

    [SkippableFact]
    public void LoadedPixAssemblyMatchesTheInstallAndExposesWhatTheServerBindsAgainst()
    {
        TestArtifacts.SkipUnlessPix();
        Assert.Null(PixApiSurface.LoadError);
        Assert.Equal(PixDiscovery.AssemblyFileVersion, PixApiSurface.LoadedFileVersion);
        IReadOnlyList<ApiProbe> probes = PixApiSurface.Probes;
        Assert.True(probes.Single(p => p.Feature == "bulkTimingReadback").Present);
        Assert.True(probes.Single(p => p.Feature == "timingPassOccupancy").Present);
        Assert.True(probes.Single(p => p.Feature == "staticShaderProfiling").Present);
        Assert.False(probes.Single(p => p.Feature == "d3dState").Present);
        Assert.Empty(probes.Where(p => p.Drift).Select(p => p.Feature + " present=" + p.Present));
        Assert.Equal(true, PixApiSurface.Has("shaderProfilingVendorName"));
        Assert.Equal(false, PixApiSurface.Has("pixelHistory"));
        Assert.Null(PixApiSurface.Has("notAFeature"));
    }

    [SkippableFact]
    public void DevBoxInstallMatchesTheBuild()
    {
        TestArtifacts.SkipUnlessPix();
        PixInstallVersion install = Assert.IsType<PixInstallVersion>(PixDiscovery.InstallVersion);
        Assert.Equal("version.xml", install.Source);
        Assert.Matches(new Regex(@"^WinPIX_release_\d{4}\.\d{5}$"), install.Build ?? "");
        Assert.Equal(PixDiscovery.BuiltAgainst.Build, install.Build);
        Assert.Equal(PixDiscovery.BuiltAgainst.FileVersion, install.FileVersion);
        PixCompatibility compatibility = PixDiscovery.Compatibility;
        Assert.Equal("match", compatibility.State);
        Assert.False(compatibility.Exit);
    }
}
