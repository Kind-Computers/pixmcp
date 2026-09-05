using System.Diagnostics;
using System.Security;
using System.Text.Json;
using Xunit;

namespace PixMcp.Tests;

public class BuildDiscoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectsNewestUsableVersionRegardlessOfDirectoryOrder(bool reverseOrder)
    {
        using var fixture = new DiscoveryFixture();
        string[] versions = ["2607.9-preview", "2607.18-preview", "2607.18.2-main", "2607.18.10-preview"];
        foreach (string version in reverseOrder ? versions.Reverse() : versions)
        {
            fixture.AddPreview(version);
        }
        fixture.AddPreview("9999.99-preview", hasMarker: false);
        fixture.AddPreview("not-a-version");
        fixture.AddPreview("99999999999999999.1-preview");

        BuildResult result = await fixture.Run();

        AssertSelection(result, Path.Combine(fixture.PreviewRoot, "2607.18.10-preview"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("incompatible")]
    public async Task ReportsWhenNoCompatibleInstallationExists(string rootState)
    {
        using var fixture = new DiscoveryFixture();
        if (rootState != "missing") Directory.CreateDirectory(fixture.PreviewRoot);
        if (rootState == "incompatible")
        {
            fixture.AddPreview("2606.14-preview");
            fixture.AddPreview("2606.15.999-main");
            fixture.AddPreview("2607.18-preview", hasMarker: false);
            fixture.AddPreview("not-a-version");
        }

        BuildResult result = await fixture.Run();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("No usable PIX installation found", result.Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitOverridesTakePrecedenceOverDiscovery(bool usePropertyOverride)
    {
        using var fixture = new DiscoveryFixture();
        fixture.AddPreview("9999.99-preview");
        string environmentInstall = fixture.AddOverride("environment-install");
        string? propertyInstall = usePropertyOverride ? fixture.AddOverride("property-install") : null;

        BuildResult result = await fixture.Run(environmentInstall, propertyInstall);

        AssertSelection(result, propertyInstall ?? environmentInstall);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidExplicitOverridesDoNotFallBackToAnotherInstallation(bool usePropertyOverride)
    {
        using var fixture = new DiscoveryFixture();
        fixture.AddPreview("2607.18-preview");
        string invalidInstall = fixture.AddOverride("missing-marker", hasMarker: false);
        string environmentInstall = usePropertyOverride ? fixture.AddOverride("environment-install") : invalidInstall;

        BuildResult result = await fixture.Run(environmentInstall, usePropertyOverride ? invalidInstall : null);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("does not contain PixApiCsExt.experimental.dll", result.Output);
        Assert.Contains(invalidInstall, result.Output);
    }

    private static void AssertSelection(BuildResult result, string expectedDirectory)
    {
        Assert.True(result.ExitCode == 0, result.Output);
        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement properties = document.RootElement.GetProperty("Properties");
        Assert.Equal(expectedDirectory, properties.GetProperty("PixInstallDir").GetString());
        Assert.Equal(expectedDirectory, properties.GetProperty("PixApiBinDir").GetString());
        string expectedDll = Path.Combine(expectedDirectory, "PixApiCsExt.experimental.dll");
        Assert.Equal(expectedDll, properties.GetProperty("PreparedHintPath").GetString());
        JsonElement reference = Assert.Single(document.RootElement.GetProperty("Items").GetProperty("Reference").EnumerateArray());
        Assert.Equal(expectedDll, reference.GetProperty("HintPath").GetString());
        Assert.Equal("false", reference.GetProperty("Private").GetString());
    }

    private sealed record BuildResult(int ExitCode, string Stdout, string Stderr)
    {
        public string Output => Stdout + Stderr;
    }

    private sealed class DiscoveryFixture : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("pixmcp-build-discovery-");
        private readonly string _projectPath;
        private readonly string _programFiles;

        public DiscoveryFixture()
        {
            _programFiles = Path.Combine(_directory.FullName, "Program Files");
            PreviewRoot = Path.Combine(_programFiles, "Microsoft PIX Preview");
            _projectPath = Path.Combine(_directory.FullName, "discovery.proj");
            string propsPath = SecurityElement.Escape(FindBuildProps())!;
            // A plain MSBuild project avoids restores/builds and records the hint path as seen
            // by PrepareForBuild, after the imported discovery target must have run.
            File.WriteAllText(_projectPath, $$"""
                <Project>
                  <Import Project="{{propsPath}}" />
                  <PropertyGroup>
                    <PixMcpRequiresPix>true</PixMcpRequiresPix>
                  </PropertyGroup>
                  <ItemGroup>
                    <Reference Include="PixApiCsExt.experimental">
                      <HintPath>$(PixApiBinDir)\PixApiCsExt.experimental.dll</HintPath>
                      <Private>false</Private>
                    </Reference>
                  </ItemGroup>
                  <Target Name="PrepareForBuild">
                    <PropertyGroup>
                      <PreparedHintPath>@(Reference->'%(HintPath)')</PreparedHintPath>
                    </PropertyGroup>
                  </Target>
                </Project>
                """);
        }

        public string PreviewRoot { get; }

        public string AddPreview(string version, bool hasMarker = true)
            => AddInstallation(Path.Combine(PreviewRoot, version), hasMarker);

        public string AddOverride(string name, bool hasMarker = true)
            => AddInstallation(Path.Combine(_directory.FullName, name), hasMarker);

        private static string AddInstallation(string directory, bool hasMarker)
        {
            Directory.CreateDirectory(directory);
            if (hasMarker) File.WriteAllBytes(Path.Combine(directory, "PixApiCsExt.experimental.dll"), []);
            return directory;
        }

        public async Task<BuildResult> Run(string? environmentInstall = null, string? propertyInstall = null)
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                WorkingDirectory = _directory.FullName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string name in new[] { "PIX_DIR", "PixInstallDir", "PixApiBinDir", "_PixDiscoveredInstallDir" })
            {
                start.Environment.Remove(name);
            }
            if (environmentInstall is not null) start.Environment["PIX_DIR"] = environmentInstall;
            foreach (string argument in new[]
            {
                "msbuild", _projectPath, "-nologo", "-t:PrepareForBuild", "-nodeReuse:false",
                "-p:_PixProgramFiles=" + _programFiles,
                "-getProperty:PixInstallDir,PixApiBinDir,PreparedHintPath", "-getItem:Reference",
            })
            {
                start.ArgumentList.Add(argument);
            }
            if (propertyInstall is not null) start.ArgumentList.Add("-p:PixInstallDir=" + propertyInstall);

            using Process process = Process.Start(start)!;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw new TimeoutException("The isolated PIX build discovery check did not finish within 60 seconds.");
            }
            return new BuildResult(process.ExitCode, await stdout, await stderr);
        }

        private static string FindBuildProps()
        {
            for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                string props = Path.Combine(directory.FullName, "Directory.Build.props");
                if (File.Exists(props) && File.Exists(Path.Combine(directory.FullName, "pixmcp.sln"))) return props;
            }
            throw new InvalidOperationException("Cannot locate the repository's Directory.Build.props.");
        }

        public void Dispose() => _directory.Delete(recursive: true);
    }
}
