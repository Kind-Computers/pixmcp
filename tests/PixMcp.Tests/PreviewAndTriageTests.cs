using System.Diagnostics;
using PixMcp.Pix;
using PixMcp.Tools;
using Xunit;

namespace PixMcp.Tests;

public class PreviewAndTriageTests
{
    [Fact]
    public void PreviewArgumentsKeepPathsAndMarkerLiteralWithoutShellExpansion()
    {
        const string capture = "C:\\captures with spaces\\$(secret)`capture.wpix";
        const string marker = "Lighting; $(not-a-command)";
        ProcessStartInfo start = PreviewTools.BuildStartInfo("pixtool.exe", capture, "C:\\out folder\\preview.png", marker, PreviewTarget.RenderTarget, 3);
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.Equal(ProcessWindowStyle.Hidden, start.WindowStyle);
        Assert.Contains(capture, start.ArgumentList);
        Assert.Contains("--marker=" + marker, start.ArgumentList);
        Assert.Contains("--rtv=3", start.ArgumentList);
        Assert.Contains("--log=off", start.ArgumentList);
        Assert.DoesNotContain("--depth", start.ArgumentList);
        string arguments = PreviewTools.FormatPixToolArguments(start.ArgumentList);
        Assert.Contains("--marker=\"Lighting; $(not-a-command)\"", arguments);
        Assert.DoesNotContain("\"--marker=", arguments);
        Assert.Contains("\"" + capture + "\"", arguments);
    }

    [Fact]
    public void PreviewRejectsAmbiguousMarkerAcrossQueuesAndInvalidSelections()
    {
        PreviewTools.ValidateMarker(["Lighting", "Shadows"], "Lighting");
        Assert.Equal("ambiguous_marker", Assert.Throws<PixToolException>(() => PreviewTools.ValidateMarker(["Lighting", "Lighting"], "Lighting")).Detail.Code);
        Assert.Throws<PixToolException>(() => PreviewTools.ValidateMarker(["lighting"], "Lighting"));
        Assert.DoesNotContain(typeof(PreviewTools).GetMethod(nameof(PreviewTools.Preview))!.GetParameters(), p => p.Name == "eventRef");
        Assert.Throws<PixToolException>(() => PreviewTools.ValidateSelection(null, PreviewTarget.Depth, 1, 120));
        Assert.Throws<PixToolException>(() => PreviewTools.ValidateSelection(null, PreviewTarget.RenderTarget, 0, 0));
        Assert.Equal("unsupported_selection", Assert.Throws<PixToolException>(() => PreviewTools.ValidateSelection("quoted \"marker\"", PreviewTarget.RenderTarget, 0, 120)).Detail.Code);
    }

    [Fact]
    public void ArtifactCacheEvictsOldestAndInvalidatesClosedCaptures()
    {
        var artifacts = new PreviewArtifacts();
        string first = artifacts.Add("gpu-1", [1]);
        for (int i = 0; i < 50; i++) artifacts.Add("gpu-2", [(byte)i]);
        Assert.Throws<PixToolException>(() => artifacts.Get(first, _ => true));
        string latest = artifacts.Add("gpu-3", [42]);
        Assert.Equal(new byte[] { 42 }, artifacts.Get(latest, _ => true));
        artifacts.Forget("gpu-3");
        Assert.Throws<PixToolException>(() => artifacts.Get(latest, _ => true));
        string closed = artifacts.Add("gpu-4", [7]);
        Assert.Throws<PixToolException>(() => artifacts.Get(closed, id => id != "gpu-4"));
    }

    [Fact]
    public void PngDimensionsRequirePngAndReadBigEndianHeader()
    {
        byte[] header = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 7, 128, 0, 0, 4, 56];
        Assert.Equal((1920u, 1080u), PreviewTools.PngDimensions(header));
        header[0] = 0;
        Assert.Throws<PixToolException>(() => PreviewTools.PngDimensions(header));
        Assert.Throws<PixToolException>(() => PreviewTools.PngDimensions([]));
    }

    [Fact]
    public void PreviewImageContentEncodesPngAsBase64OnWire()
    {
        byte[] png = [137, 80, 78, 71, 13, 10, 26, 10, 255];
        using var document = System.Text.Json.JsonDocument.Parse(Json.Serialize(PreviewTools.ImageResult("preview-test", new RenderedImage(png, 1, 1, 1, 1, null, false))));
        string wireData = document.RootElement.GetProperty("content")[1].GetProperty("data").GetString()!;
        Assert.Equal(png, Convert.FromBase64String(wireData));
        Assert.Equal("image/png", document.RootElement.GetProperty("content")[1].GetProperty("mimeType").GetString());
    }

    private static ProcessStartInfo Shell(string command)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardError = true, RedirectStandardOutput = true,
        };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-Command", command }) start.ArgumentList.Add(arg);
        return start;
    }

    [Fact]
    public void ProcessDrainsLargeOutputButBoundsDiagnosticsAndReportsFailure()
    {
        var messages = new List<string>();
        PreviewTools.RunProcess(Shell("[Console]::Out.Write(('x' * 100000)); [Console]::Error.Write(('y' * 100000))"), TimeSpan.FromSeconds(20), default, messages.Add);
        Assert.Equal(2, messages.Count);
        Assert.All(messages, message => Assert.Equal(4096, message.Length));
        Assert.Equal("preview_failed", Assert.Throws<PixToolException>(() => PreviewTools.RunProcess(Shell("exit 3"), TimeSpan.FromSeconds(20), default, _ => { })).Detail.Code);
    }

    [Fact]
    public void ProcessTimeoutAndCancellationTerminateTheHelper()
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        Assert.Equal("preview_timeout", Assert.Throws<PixToolException>(() => PreviewTools.RunProcess(Shell("Start-Sleep -Seconds 30"), TimeSpan.FromMilliseconds(200), default, _ => { })).Detail.Code);
        using var cancellation = new CancellationTokenSource(200);
        Assert.ThrowsAny<OperationCanceledException>(() => PreviewTools.RunProcess(Shell("Start-Sleep -Seconds 30"), TimeSpan.FromSeconds(20), cancellation.Token, _ => { }));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10));
    }

    private sealed record Event(string Status, Event[] Children);

    [Fact]
    public void TriageTraversesIncompleteDescendantsBeneathCompletedParentsWithStablePaths()
    {
        Event[] roots = [new("COMPLETED", [new("IN_PROGRESS", [new("POSSIBLY_COMPLETED", [])]), new("NOT_STARTED", [])]), new("UNKNOWN", [])];
        var walked = DumpTools.Walk(roots, node => node.Children).ToArray();
        Assert.Equal(5, walked.Length);
        Assert.Equal(new[] { 0, 0 }, walked[1].Path);
        Assert.Equal(new[] { 0, 0, 0 }, walked[2].Path);
        Assert.Equal(new[] { 0, 1 }, walked[3].Path);
        Assert.Equal(new[] { 1 }, walked[4].Path);
        Assert.Equal(60, DumpTools.EventPriority(walked[1].Node.Status));
        Assert.Equal(40, DumpTools.EventPriority(walked[2].Node.Status));
        Assert.Equal(0, DumpTools.EventPriority(walked[0].Node.Status));
        Assert.Equal(0, DumpTools.EventPriority(walked[3].Node.Status));
    }

    [Fact]
    public void TriageMissingSectionsHaveCoverageAndDoNotConflateAbsenceWithFailure()
    {
        Assert.Equal(new DumpTriageCoverage("absent"), DumpTools.ReadCoverage(() => null));
        Assert.Equal(new DumpTriageCoverage("available", 0), DumpTools.ReadCoverage(() => 0));
        DumpTriageCoverage failed = DumpTools.ReadCoverage(() => throw new InvalidOperationException("DRED unavailable"));
        Assert.Equal("unavailable", failed.State);
        Assert.Contains("DRED unavailable", failed.Reason);
        Assert.Throws<OperationCanceledException>(() => DumpTools.ReadCoverage(() => throw new OperationCanceledException()));
    }

    [Fact]
    public void TriageRangeMatchingAvoidsOverflowAndRanksDeterministically()
    {
        Assert.True(DumpTools.AddressInResource(ulong.MaxValue, ulong.MaxValue - 2, 4));
        Assert.False(DumpTools.AddressInResource(1, ulong.MaxValue - 2, 4));
        Assert.False(DumpTools.AddressInResource(10, 10, 0));
        Assert.False(DumpTools.AddressInResource(20, 10, 10));
        DumpTriageObservation Row(string id, int priority) => new(id, priority, "test", "evidence", new { }, []);
        Assert.Equal(new[] { "fault", "a", "b" }, DumpTools.RankObservations([Row("b", 40), Row("a", 40), Row("fault", 100)]).Select(o => o.Id));
    }

    [Fact]
    public void GpuStateRowBudgetPreservesEveryNestedRowInSnapshot()
    {
        var results = new ResultStore();
        var table = new { rows = new[] { new { name = "parent", children = new[] { new { name = "late child", value = 42 } } } } };
        var response = Assert.IsType<DeferredResultDto>(DumpTools.GpuStateResult(results, "dump-1", table, 2, 1));
        using var page = System.Text.Json.JsonDocument.Parse(Json.Serialize(results.Read(response.ResultRef, "/rows/0/children/0/value", 0, 25)));
        Assert.Equal(42, page.RootElement.GetProperty("value").GetInt32());
        results.InvalidateOwner("dump-1");
        Assert.Throws<PixToolException>(() => results.Read(response.ResultRef, "", 0, 25));
    }

    [Fact]
    public void BlobByteWindowsReconstructWithoutDuplicateBytesOrUnreachableTail()
    {
        byte[] source = Enumerable.Range(0, 53).Select(i => (byte)i).ToArray();
        var restored = new List<byte>();
        var requestedPrefixes = new List<ulong>();
        int? offset = 0;
        while (offset.HasValue)
        {
            var page = DumpTools.ReadBlobWindow((ulong)source.Length, offset.Value, 7, count =>
            {
                requestedPrefixes.Add(count);
                return source.Take((int)count).ToArray();
            });
            restored.AddRange(page.Bytes);
            offset = page.NextOffset;
        }
        Assert.Equal(source, restored);
        Assert.Equal(53UL, requestedPrefixes[^1]);
        var pastEnd = DumpTools.ReadBlobWindow(53, 100, 7, _ => throw new InvalidOperationException("Must not read beyond blob"));
        Assert.Empty(pastEnd.Bytes);
        Assert.Null(pastEnd.NextOffset);
        Assert.Throws<PixToolException>(() => DumpTools.ReadBlobWindow(ulong.MaxValue, int.MaxValue, 1, _ => []));
    }
}
