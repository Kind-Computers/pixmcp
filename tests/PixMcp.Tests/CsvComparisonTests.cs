using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using PixMcp.Pix;
using PixMcp.Pix.Handles;
using PixMcp.Tools;
using Xunit;

namespace PixMcp.Tests;

public sealed class CsvComparisonTests
{
    private const string HelperJson = """
        {"kind":"pixdiff","schemaVersion":1,"stat":"median","prefix":"GPU/","units":"ms","deltaConvention":"oursMinusTheirs",
         "ours":{"path":"candidate.csv","frames":2},"theirs":{"path":"baseline.csv","frames":2},
         "total":{"name":"GPU/Total","oursMs":10,"theirsMs":0,"deltaMs":10,"deltaPercent":null,"oursPresent":true,"theirsPresent":true,"oursSamples":2,"theirsSamples":2},
         "rows":[{"name":"GPU/BasePass","oursMs":3,"theirsMs":2,"deltaMs":1,"deltaPercent":50,"oursPresent":true,"theirsPresent":true,"oursSamples":2,"theirsSamples":2}],
         "missing":[{"name":"GPU/Empty","oursMs":null,"theirsMs":null,"deltaMs":null,"deltaPercent":null,"oursPresent":true,"theirsPresent":false,"oursSamples":0,"theirsSamples":0}]}
        """;

    private static CsvComparisonDto Parse(string json = HelperJson)
        => CsvComparison.Parse(json, "baseline.csv", "candidate.csv", "GPU/", "median");

    [Fact]
    public void HelperDiscoveryHasExplicitAdjacentThenPathPrecedence()
    {
        string folder = Path.Combine(Path.GetTempPath(), "pixdiff-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string adjacent = Path.Combine(folder, "server"), pathFolder = Path.Combine(folder, "path");
        Directory.CreateDirectory(adjacent); Directory.CreateDirectory(pathFolder);
        string explicitPath = Path.Combine(folder, "configured.exe");
        File.WriteAllText(explicitPath, "test");
        File.WriteAllText(Path.Combine(pathFolder, "pixdiff.exe"), "test");
        try
        {
            Assert.Equal("PATH", PixDiffDiscovery.Find(null, adjacent, pathFolder).DiscoveredVia);
            File.WriteAllText(Path.Combine(adjacent, "pixdiff.exe"), "test");
            Assert.Equal("server directory", PixDiffDiscovery.Find(null, adjacent, pathFolder).DiscoveredVia);
            Assert.Equal(explicitPath, PixDiffDiscovery.Find(explicitPath, adjacent, pathFolder).Executable);
            foreach (string invalid in new[] { "", "pixdiff.exe", Path.Combine(folder, "missing.exe") })
            {
                var result = PixDiffDiscovery.Find(invalid, adjacent, pathFolder);
                Assert.False(result.Available);
                Assert.Equal("PIXMCP_PIXDIFF_PATH", result.DiscoveredVia);
            }
            Assert.False(PixDiffDiscovery.Find(null, Path.Combine(folder, "absent"), "").Available);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void ProcessArgumentsPreservePathsAndMapCandidateToOurs()
    {
        const string candidate = "C:\\capture folder\\$(literal)`ours.csv", baseline = "C:\\base folder\\theirs.csv";
        var start = CsvComparison.BuildStartInfo("pixdiff.exe", baseline, candidate, "GPU/Lighting;\"test\"", "median");
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.Equal(ProcessWindowStyle.Hidden, start.WindowStyle);
        Assert.Equal(new[] { candidate, baseline, "--format=json", "--prefix", "GPU/Lighting;\"test\"", "--stat", "median" }, start.ArgumentList);
    }

    [Fact]
    public void ReportNormalizesSignUnitsAndCoverageWithoutConflatingMissingWithZero()
    {
        var report = Parse();
        Assert.Equal("csvComparison", report.Kind);
        Assert.Equal("ueCsvProfiler", report.Source);
        Assert.Equal("candidateMinusBaseline", report.DeltaConvention);
        Assert.Equal("baseline.csv", report.Baseline.Path);
        Assert.Equal(2, report.Items[0].BaselineMs);
        Assert.Equal(3, report.Items[0].CandidateMs);
        Assert.Equal(1, report.Items[0].DeltaMs);
        Assert.Equal(50, report.Items[0].DeltaPercent);
        Assert.Null(report.Total!.DeltaPercent);
        Assert.True(report.Missing[0].CandidatePresent);
        Assert.False(report.Missing[0].BaselinePresent);
        Assert.Equal(0, report.Missing[0].CandidateSamples);
        Assert.Null(report.Missing[0].CandidateMs);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("options")]
    [InlineData("samples")]
    [InlineData("sign")]
    [InlineData("nonfinite")]
    [InlineData("missing")]
    public void RejectsMalformedOrIncompatibleHelperReports(string mutation)
    {
        JsonNode json = JsonNode.Parse(HelperJson)!;
        switch (mutation)
        {
            case "version": json["schemaVersion"] = 2; break;
            case "options": json["stat"] = "mean"; break;
            case "samples": json["rows"]![0]!["oursSamples"] = 3; break;
            case "sign": json["rows"]![0]!["deltaMs"] = -1; break;
            case "nonfinite": json["rows"]![0]!["oursMs"] = "NaN"; break;
            case "missing": json.AsObject().Remove("rows"); break;
        }
        Assert.Equal("pixdiff_invalid_output", Assert.Throws<PixToolException>(() => Parse(json.ToJsonString())).Detail.Code);
        Assert.Equal("pixdiff_invalid_output", Assert.Throws<PixToolException>(() => Parse("not json")).Detail.Code);
    }

    [Fact]
    public void SavedPassReadsAndPagingSurviveOriginalFilesAndOtherCaptureClose()
    {
        using var worker = new PixWorker();
        using var session = new PixSession(worker, NullLogger<PixSession>.Instance);
        string reference = session.Results.Store(Parse());
        var handle = session.Register(new FakeHandle());
        session.Close(handle.Id);
        var saved = CsvComparison.ReadPass(session.Results, reference, "GPU/BasePass", default);
        Assert.Equal("GPU/", saved.Prefix);
        Assert.Equal(1, saved.Pass.DeltaMs);
        Assert.Equal("GPU/Total", CsvComparison.ReadPass(session.Results, reference, "GPU/Total", default).Pass.Name);
        Assert.Equal("GPU/Empty", CsvComparison.ReadPass(session.Results, reference, "GPU/Empty", default).Pass.Name);
        Assert.Equal("csv_pass_not_found", Assert.Throws<PixToolException>(() => CsvComparison.ReadPass(session.Results, reference, "GPU/Absent", default)).Detail.Code);
        string wrong = session.Results.Store(new { kind = "other" });
        Assert.Equal("invalid_arguments", Assert.Throws<PixToolException>(() => CsvComparison.ReadPass(session.Results, wrong, "GPU/BasePass", default)).Detail.Code);
        Assert.Equal("result_expired", Assert.Throws<PixToolException>(() => CsvComparison.ReadPass(session.Results, "expired", "GPU/BasePass", default)).Detail.Code);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => CsvComparison.ReadPass(session.Results, reference, "GPU/BasePass", cancellation.Token));
    }

    [Fact]
    public void CandidatesRetainDuplicatesWithExactNamesFirstAndStableContinuation()
    {
        var pass = Parse().Items[0];
        EventDto E(int queue, uint index, string name) => new(queue, index, null, null, name, null, 0, null)
        { EventRef = new("gpu-1", queue, index), MarkerPath = ["Frame"] };
        EventDto[] markers = [E(0, 0, "BasePass (opaque)"), E(2, 3, "BasePass"), E(0, 9, "basepass"), E(0, 2, "Unrelated")];
        var first = CsvTools.FindCandidates("result-1", "GPU/", pass, "gpu-1", markers, 0, 2);
        Assert.False(first.IdentityEstablished);
        Assert.Equal(3, first.Total);
        Assert.Equal(2, first.NextOffset);
        Assert.All(first.Items, item => Assert.Equal("exactName", item.MatchKind));
        Assert.Equal(new EventRef("gpu-1", 0, 9), first.Items[0].Event.EventRef);
        Assert.Equal(new EventRef("gpu-1", 2, 3), first.Items[1].Event.EventRef);
        Assert.Equal("pix_csv_pass_candidates", Assert.Single(first.NextCalls).Tool);
        var last = CsvTools.FindCandidates("result-1", "GPU/", pass, "gpu-1", markers, first.NextOffset!.Value, 2);
        Assert.Equal("containsName", Assert.Single(last.Items).MatchKind);
        Assert.Null(last.NextOffset);
        Assert.Equal("pix_gpu_inspect_event", last.Items[0].NextCalls[0].Tool);
        Assert.Empty(CsvTools.FindCandidates("result-1", "GPU/", pass, "gpu-1", [], 0, 25).Items);
        var total = CsvTools.FindCandidates("result-1", "GPU/", Parse().Total!, "gpu-1", markers, 0, 25);
        Assert.Empty(total.Items);
        Assert.NotNull(total.UnavailableReason);
        Assert.Equal("pix_gpu_overview", Assert.Single(total.NextCalls).Tool);
    }

    private static ProcessStartInfo Shell(string command)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-Command", command }) start.ArgumentList.Add(arg);
        return start;
    }

    [Fact]
    public async Task HelperDrainsPipesBoundsOutputAndReportsExitFailure()
    {
        var messages = new List<string>();
        string output = await CsvComparison.RunProcess(Shell("[Console]::Out.Write('{}'); [Console]::Error.Write(('x' * 100000))"),
            TimeSpan.FromSeconds(20), default, messages.Add);
        Assert.Equal("{}", output);
        Assert.Equal(4096, Assert.Single(messages).Length);
        Assert.Equal("pixdiff_failed", (await Assert.ThrowsAsync<PixToolException>(() => CsvComparison.RunProcess(Shell("exit 3"),
            TimeSpan.FromSeconds(20), default, _ => { }))).Detail.Code);
        Assert.Equal("pixdiff_output_too_large", (await Assert.ThrowsAsync<PixToolException>(() => CsvComparison.RunProcess(Shell("[Console]::Out.Write(('x' * 100000))"),
            TimeSpan.FromSeconds(20), default, _ => { }, maxOutputBytes: 100))).Detail.Code);
    }

    [Fact]
    public async Task HelperTimeoutAndCancellationTerminateTheProcess()
    {
        var elapsed = Stopwatch.StartNew();
        Assert.Equal("pixdiff_timeout", (await Assert.ThrowsAsync<PixToolException>(() => CsvComparison.RunProcess(Shell("Start-Sleep -Seconds 30"),
            TimeSpan.FromMilliseconds(200), default, _ => { }))).Detail.Code);
        using var cancellation = new CancellationTokenSource(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CsvComparison.RunProcess(Shell("Start-Sleep -Seconds 30"),
            TimeSpan.FromSeconds(20), cancellation.Token, _ => { }));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10));
    }

    private sealed class FakeHandle() : PixHandle("fake")
    {
        public override string Kind => "fake";
        public override void Close(List<string> warnings) { }
    }
}
