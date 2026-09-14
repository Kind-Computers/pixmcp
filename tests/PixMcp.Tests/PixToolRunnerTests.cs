using System.Diagnostics;
using PixMcp.Pix;
using PixMcp.Pix.Handles;
using PixMcp.Tools;
using Xunit;

namespace PixMcp.Tests;

public sealed class PixToolRunnerTests
{
    private const char Q = '"';
    private static readonly string Bom = ((char)0xFEFF).ToString();

    [Fact]
    public void ChainedCommandsShareOneOpenCaptureAndQuoteOnlyValues()
    {
        const string capture = "C:/captures with spaces/frame.wpix";
        PixToolCommand[] commands =
        [
            PixToolCommand.SaveEventList("C:/out dir/events.csv"),
            PixToolCommand.SaveResource("C:/out dir/a.png", depth: false, rtvIndex: 2, globalId: 42),
            PixToolCommand.SaveResource("C:/out dir/b.png", depth: true, rtvIndex: 0, markerName: "Lighting pass"),
            PixToolCommand.RecaptureRegion("C:/out dir/sub.wpix", 6, 9),
        ];
        ProcessStartInfo start = PixToolRunner.StartInfo("pixtool.exe", capture, commands);
        Assert.False(start.UseShellExecute);
        Assert.Equal(new[]
        {
            "--output=quiet", "--log=off", "open-capture", capture,
            "save-event-list", "C:/out dir/events.csv",
            "save-resource", "C:/out dir/a.png", "--rtv=2", "--global-id=42",
            "save-resource", "C:/out dir/b.png", "--depth", "--marker=Lighting pass",
            "recapture-region", "C:/out dir/sub.wpix", "--start=6", "--end=9",
        }, start.ArgumentList);

        string line = PixToolProcess.FormatArguments(start.ArgumentList);
        Assert.Contains(" --global-id=42 ", line);
        Assert.Contains("--marker=" + Q + "Lighting pass" + Q, line);
        Assert.Contains(Q + "C:/out dir/sub.wpix" + Q + " --start=6 --end=9", line);
        Assert.Equal(new[] { "--global-id=7" }, PixToolCommand.SaveResource("x.png", false, 0, markerName: "ignored", globalId: 7).Arguments.Where(a => a.StartsWith("--g") || a.StartsWith("--m")));
    }

    [Fact]
    public void EventListParsingHandlesTheBomQuotesAndUnquotedCommasInNames()
    {
        string csv = Bom + string.Join((char)13 + "" + (char)10,
            "Queue ID, Parent, Name, Global ID",
            "0, -1, Signal, 1",
            "1, -1, Reset, ",
            Q + "2" + Q + ", -1, " + Q + "Draw " + Q + Q + "a, b" + Q + Q + Q + ", 4",
            "3, 2, Foo(1, 2), 5",
            "");
        IReadOnlyList<EventListRow> rows = GlobalIdProbe.Parse(csv)!;
        Assert.Equal(4, rows.Count);
        Assert.Equal(new EventListRow(0, "Signal", 1), rows[0]);
        Assert.Equal(new EventListRow(1, "Reset", null), rows[1]);
        Assert.Equal(new EventListRow(2, "Draw " + Q + "a, b" + Q, 4), rows[2]);
        Assert.Equal(new EventListRow(3, "Foo(1, 2)", 5), rows[3]);
        Assert.Null(GlobalIdProbe.Parse("Index, Name" + (char)10 + "0, Signal"));
        Assert.Null(GlobalIdProbe.Parse(""));
    }

    [Fact]
    public void GlobalIdVerdictsNeedEightMatchingRowsOnTheListedQueue()
    {
        (string Name, uint GpuId)[] listed = Enumerable.Range(0, 12).Select(i => ($"Draw{i}", (uint)(i + 1))).ToArray();
        EventRecord[] graphics = Queue(listed);
        EventRecord[] compute = Queue(("Dispatch", 50), ("Signal", 51));
        EventListRow[] rows = listed.Select((e, i) => new EventListRow((uint)i, e.Name, e.GpuId)).ToArray();

        GlobalIdMappingDto verified = GlobalIdProbe.Verify(rows, [compute, graphics]);
        Assert.Equal((GlobalIdProbe.Verified, 12, (int?)1), (verified.State, verified.Compared, verified.QueueIndex));

        GlobalIdMappingDto mismatch = GlobalIdProbe.Verify(rows.Select(r => r with { GlobalId = r.GlobalId + 1u }).ToArray(), [compute, graphics]);
        Assert.Equal((GlobalIdProbe.Mismatch, 1), (mismatch.State, mismatch.Compared));
        Assert.Contains("Global ID 2", mismatch.FirstMismatch);

        Assert.Equal(GlobalIdProbe.Inconclusive, GlobalIdProbe.Verify(rows.Take(5).ToArray(), [graphics]).State);
        Assert.Equal(GlobalIdProbe.Inconclusive, GlobalIdProbe.Verify(rows.Select(r => r with { Name = "Other" }).ToArray(), [graphics]).State);
        Assert.Equal(GlobalIdProbe.Inconclusive, GlobalIdProbe.Verify(null, [graphics]).State);
        Assert.Equal("global_id_mismatch", GlobalIdProbe.MismatchError(mismatch).Detail.Code);
    }

    [Fact]
    public void SubcapturePathsDefaultBesideTheSourceAndRefuseUnsafeTargets()
    {
        string folder = Directory.CreateTempSubdirectory("pixmcp-runner-").FullName;
        try
        {
            string source = Path.Combine(folder, "frame.wpix");
            File.WriteAllText(source, "x");
            Assert.Equal(Path.Combine(folder, "frame.sub-6-9.wpix"), SubcaptureTools.DefaultPath(source, 6, 9));
            Assert.Equal(Path.Combine(folder, "cut.wpix"), SubcaptureTools.ValidateOutputPath(Path.Combine(folder, "cut.wpix"), source, false, null));
            Assert.Equal("invalid_arguments", Code(() => SubcaptureTools.ValidateOutputPath(Path.Combine(folder, "cut.png"), source, false, null)));
            Assert.Equal("invalid_arguments", Code(() => SubcaptureTools.ValidateOutputPath(source, source, true, null)));
            Assert.Equal("file_exists", Code(() => SubcaptureTools.ValidateOutputPath(source, Path.Combine(folder, "other.wpix"), false, null)));
            Assert.Equal("directory_not_found", Code(() => SubcaptureTools.ValidateOutputPath(Path.Combine(folder, "missing", "cut.wpix"), source, false, null)));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void PreviewSelectionRejectsConflictingSelectorsAndBadTargetLists()
    {
        var eventRef = new EventRef("gpu-1", 0, 19);
        PreviewTargetSpec[] one = [new(PreviewTarget.RenderTarget, 0)];
        PreviewTools.ValidateSelection(null, eventRef, one, 120);
        Assert.Equal("invalid_arguments", Code(() => PreviewTools.ValidateSelection("Frame", eventRef, one, 120)));
        Assert.Equal("invalid_arguments", Code(() => PreviewTools.ValidateSelection(null, eventRef, [], 120)));
        Assert.Equal("invalid_arguments", Code(() => PreviewTools.ValidateSelection(null, eventRef, Enumerable.Repeat(one[0], 9).ToArray(), 120)));
        Assert.Equal("invalid_arguments", Code(() => PreviewTools.ValidateSelection(null, null, [new(PreviewTarget.Depth, 1)], 120)));
    }

    [Fact]
    public void ProcessErrorsNameTheOperationAndPixtoolsFirstErrorLine()
    {
        Assert.Equal(": 0.7: pixtool error: PIXTOOL9 - Render Target missing", PixToolProcess.FirstError("banner" + (char)10 + "0.7: pixtool error: PIXTOOL9 - Render Target missing", ""));
        Assert.Equal(": first", PixToolProcess.FirstError("", (char)10 + "  first  " + (char)10 + "second"));
        Assert.Equal("", PixToolProcess.FirstError("", " "));
        Assert.Equal(302, PixToolProcess.FirstError(new string('x', 1000)).Length);
    }

    private static EventRecord[] Queue(params (string Name, uint GpuId)[] events)
        => events.Select((e, i) => new EventRecord((uint)i, e.GpuId, uint.MaxValue, e.Name, "", 0, 0)).ToArray();

    private static string Code(Action action) => Assert.Throws<PixToolException>(action).Detail.Code;
}
