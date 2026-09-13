using System.Text.RegularExpressions;
using PixMcp.Pix;
using Xunit;

namespace PixMcp.Tests;

public class RuntimeDiscoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectsTheOnlyEligibleInstallRegardlessOfDirectoryOrder(bool reverseOrder)
    {
        using var fixture = new InstallFixture();
        string[] versions = ["2606.14-preview", "2606.15.999-preview", "2606.17-preview"];
        foreach (string version in reverseOrder ? versions.Reverse() : versions) fixture.Add(version);
        fixture.Add("9999.99-preview", marker: false);
        fixture.Add("not-a-version");
        fixture.Add("99999999999999999.1-preview");

        string? result = PixDiscovery.Find(null, fixture.ProgramFiles, out string? source, out string? error);

        Assert.Equal(Path.Combine(fixture.PreviewRoot, "2606.17-preview"), result);
        Assert.Equal("Program Files scan", source);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SeveralEligibleInstallsAreAnErrorUnlessTheNewestIsRequested(bool reverseOrder)
    {
        using var fixture = new InstallFixture();
        string[] versions = ["2607.9-preview", "2607.18-preview", "2607.18.2-main", "2607.18.10-main", "2607.18.10-preview"];
        foreach (string version in reverseOrder ? versions.Reverse() : versions) fixture.Add(version);

        Assert.Null(PixDiscovery.Find(null, fixture.ProgramFiles, out string? source, out string? error));
        Assert.Null(source);
        Assert.Contains("Multiple PIX Preview installs are eligible", error);
        Assert.All(versions, version => Assert.Contains(version, error));
        Assert.Contains(PixDiscovery.PickNewestVariable, error);
        Assert.Contains("(2607.18.10-preview)", error);

        string? newest = PixDiscovery.Find(null, fixture.ProgramFiles, out source, out error, pickNewest: true);
        Assert.Equal(Path.Combine(fixture.PreviewRoot, "2607.18.10-preview"), newest);
        Assert.Equal("Program Files scan (newest of 5)", source);
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

    [Fact]
    public void ReadsInstallVersionFromVersionXmlAndFallsBackToTheDirectoryName()
    {
        using var fixture = new InstallFixture();
        string withXml = fixture.Add("2606.18-preview");
        File.WriteAllText(Path.Combine(withXml, "version.xml"), """
            <?xml version="1.0" encoding="utf-8"?>
            <PixVersion>
              <Version>2606.18-preview</Version>
              <FriendlyVersion>2606.18-preview</FriendlyVersion>
              <Build>WinPIX_release_2606.18001</Build>
              <Commit>5a7c9127d7f5b5668bbc38858cb9058cd2c8c75e</Commit>
            </PixVersion>
            """);
        PixInstallVersion parsed = PixDiscovery.ReadInstallVersion(withXml);
        Assert.Equal("2606.18-preview", parsed.Effective);
        Assert.Equal("2606.18-preview", parsed.XmlVersion);
        Assert.Equal("WinPIX_release_2606.18001", parsed.Build);
        Assert.Equal("5a7c9127d7f5b5668bbc38858cb9058cd2c8c75e", parsed.Commit);
        Assert.Equal("version.xml", parsed.Source);
        Assert.Null(parsed.FileVersion); // the fixture's marker DLL is an empty file
        Assert.Null(parsed.Error);

        string bare = fixture.Add("2606.17-preview");
        PixInstallVersion fallback = PixDiscovery.ReadInstallVersion(bare);
        Assert.Equal("2606.17-preview", fallback.Effective);
        Assert.Null(fallback.XmlVersion);
        Assert.Equal("directoryName", fallback.Source);

        string broken = fixture.Add("2606.16-preview");
        File.WriteAllText(Path.Combine(broken, "version.xml"), "<PixVersion><Version>");
        PixInstallVersion damaged = PixDiscovery.ReadInstallVersion(broken);
        Assert.Equal("2606.16-preview", damaged.Effective);
        Assert.Contains("version.xml could not be read", damaged.Error);
    }

    [Theory]
    [InlineData("2606.18-preview", "1.0.2606.18001", null, "match", false)]
    [InlineData("2606.18-preview", "1.0.2606.18001", "1", "match", false)]
    [InlineData("2606.17-preview", "1.0.2606.17001", null, "olderThanBuild", true)]
    [InlineData("2606.17-preview", "1.0.2606.17001", "0", "olderThanBuild", false)]
    [InlineData("2607.02-preview", "1.0.2607.2001", null, "newerUnverified", false)]
    [InlineData("2607.02-preview", "1.0.2607.2001", "1", "newerUnverified", true)]
    [InlineData("2607.02-preview", "1.0.2607.2001", "off", "newerUnverified", false)]
    [InlineData("2606.18-preview", "1.0.2606.18002", null, "mismatch", true)]
    [InlineData("2606.18-preview", "1.0.2606.18002", "false", "mismatch", false)]
    [InlineData("2606.18-preview", null, null, "match", false)]
    [InlineData("custom-build", "1.0.2606.18001", null, "unknown", false)]
    [InlineData("custom-build", "1.0.2606.18001", "1", "unknown", false)]
    public void ClassifiesTheInstallAgainstTheBuildReference(string installVersion, string? fileVersion, string? strict, string state, bool exit)
    {
        var built = new PixBuildReference("2606.18-preview", "WinPIX_release_2606.18001", "5a7c9127", "1.0.2606.18001", "2606.15", "2606.18-preview");
        var install = new PixInstallVersion(installVersion, installVersion, null, null, fileVersion, "version.xml", null);

        PixCompatibility compatibility = PixDiscovery.Classify(install, built, strict);

        Assert.Equal(state, compatibility.State);
        Assert.Equal(exit, compatibility.Exit);
        Assert.Equal(strict is null ? "default" : strict is "1" ? "on" : "off", compatibility.StrictMode);
        Assert.Contains(installVersion, compatibility.Message);
        if (state == "olderThanBuild") Assert.Contains("older than the PIX build", compatibility.Message);
        if (state == "mismatch") Assert.Contains("1.0.2606.18002", compatibility.Message);
    }

    [Fact]
    public void NoInstallClassifiesAsUnknownWithoutExiting()
    {
        PixCompatibility compatibility = PixDiscovery.Classify(null, PixDiscovery.BuiltAgainst, "1");
        Assert.Equal("unknown", compatibility.State);
        Assert.False(compatibility.Exit);
        Assert.Contains("no install discovered", compatibility.Message);
    }

    [Fact]
    public void BuildReferenceComesFromAssemblyMetadata()
    {
        // Directory.Build.props records the PIX the server was compiled against; test builds always have a real PIX.
        PixBuildReference built = PixDiscovery.BuiltAgainst;
        Assert.Equal("2606.15", built.MinPreviewDate);
        Assert.Matches(new Regex(@"^\d{4}\.\d{1,2}(\.\d+)?-preview$"), built.VerifiedVersion);
        Assert.Matches(new Regex(@"^1\.0\.\d{4}\.\d{5}$"), built.FileVersion ?? "");
        Assert.Matches(new Regex(@"^WinPIX_release_\d{4}\.\d{5}$"), built.Build ?? "");
        Assert.Equal(PixDiscovery.VerifiedVersion, built.VerifiedVersion);
    }

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
