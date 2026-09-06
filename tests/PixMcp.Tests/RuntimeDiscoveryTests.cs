using PixMcp.Pix;
using Xunit;

namespace PixMcp.Tests;

public class RuntimeDiscoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectsNewestUsableVersionWithDeterministicTies(bool reverseOrder)
    {
        using var fixture = new InstallFixture();
        string[] versions = ["2607.9-preview", "2607.18-preview", "2607.18.2-main", "2607.18.10-main", "2607.18.10-preview"];
        foreach (string version in reverseOrder ? versions.Reverse() : versions) fixture.Add(version);
        fixture.Add("9999.99-preview", marker: false);
        fixture.Add("not-a-version");
        fixture.Add("99999999999999999.1-preview");

        string? result = PixDiscovery.Find(null, fixture.ProgramFiles, out string? source, out string? error);

        Assert.Equal(Path.Combine(fixture.PreviewRoot, "2607.18.10-preview"), result);
        Assert.Equal("Program Files scan", source);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("incompatible")]
    public void ExplainsMissingCompatibleInstallation(string state)
    {
        using var fixture = new InstallFixture();
        if (state != "missing") Directory.CreateDirectory(fixture.PreviewRoot);
        if (state == "incompatible")
        {
            fixture.Add("2606.14-preview");
            fixture.Add("2606.15.999-preview");
            fixture.Add("2607.18-preview", marker: false);
        }

        Assert.Null(PixDiscovery.Find(null, fixture.ProgramFiles, out string? source, out string? error));
        Assert.Null(source);
        Assert.Contains("PIX Preview", error);
        Assert.Contains(fixture.PreviewRoot, error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitOverrideNeverFallsBack(bool valid)
    {
        using var fixture = new InstallFixture();
        fixture.Add("9999.99-preview");
        string configured = fixture.Add("2606.18-preview", marker: valid);

        string? result = PixDiscovery.Find(configured, fixture.ProgramFiles, out string? source, out string? error);

        if (valid)
        {
            Assert.Equal(configured, result);
            Assert.Equal("PIX_DIR", source);
            Assert.Null(error);
        }
        else
        {
            Assert.Null(result);
            Assert.Null(source);
            Assert.Contains(configured, error);
            Assert.Contains("Correct PIX_DIR or unset it", error);
        }
    }

    [Theory]
    [InlineData("99999999999999999.1-preview")]
    [InlineData("2607.2147483648")]
    [InlineData("2607.+18")]
    [InlineData("2607. 18")]
    [InlineData("2607.18.")]
    public void MalformedVersionComponentsAreSkipped(string version)
        => Assert.Null(PixDiscovery.ParseVersion(version));

    private sealed class InstallFixture : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("pixmcp-runtime-discovery-");
        public string ProgramFiles => Path.Combine(_root.FullName, "Program Files");
        public string PreviewRoot => Path.Combine(ProgramFiles, "Microsoft PIX Preview");

        public string Add(string version, bool marker = true)
        {
            string directory = Path.Combine(PreviewRoot, version);
            Directory.CreateDirectory(directory);
            if (marker) File.WriteAllBytes(Path.Combine(directory, PixDiscovery.MarkerDll), []);
            return directory;
        }

        public void Dispose() => _root.Delete(recursive: true);
    }
}
