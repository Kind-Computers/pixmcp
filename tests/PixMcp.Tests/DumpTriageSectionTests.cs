using System.Text.Json;
using Microsoft.PIX;
using PixMcp.Pix;
using PixMcp.Tools;
using Xunit;

namespace PixMcp.Tests;

public sealed class DumpTriageSectionTests
{
    [Fact]
    public void JournalFailuresRankTheMostRecentWithinTheWindowAndCapAtTwenty()
    {
        List<JournalEntryData> window = Enumerable.Range(0, 30)
            .Select(i => new JournalEntryData(100 + i, i % 2 == 0 ? 0x887A0006u : 0x00000001u, 7, (uint)(1000 + i), i == 28 ? "Device removed" : null)).ToList();
        (DumpJournalSummaryDto summary, List<DumpTriageObservation> found) = TriageObservationBuilder.Journal(window, 130, "dump-1");
        Assert.Equal(new DumpJournalSummaryDto(130, 30, 15, 128), summary);
        Assert.Equal(15, found.Count);
        Assert.Equal("journal-128", found[0].Id);
        Assert.Contains("Device removed", found[0].Summary);
        Assert.Equal("pix_dump_journal", Assert.Single(found[0].NextCalls).Tool);

        List<JournalEntryData> many = Enumerable.Range(0, 50).Select(i => new JournalEntryData(i, 0x80004005u, 1, 1, null)).ToList();
        (_, List<DumpTriageObservation> capped) = TriageObservationBuilder.Journal(many, 50, "dump-1");
        Assert.Equal(20, capped.Count);
        Assert.Equal("journal-49", capped[0].Id);
        Assert.Equal(capped.Count, capped.Select(o => o.Id).Distinct().Count());
        Assert.All(capped, o => Assert.Equal(70, o.Priority));
        Assert.True(TriageObservationBuilder.IsJournalFailure(0x80004005));
        Assert.False(TriageObservationBuilder.IsJournalFailure(1));
        Assert.Equal(new DumpJournalSummaryDto(3, 0, 0, null), TriageObservationBuilder.Journal([], 3, "dump-1").Summary);
    }

    [Fact]
    public void HardwareStatusSeverityMapsToPrioritiesWithUniqueIds()
    {
        HardwareStatusData[] statuses =
        [
            new(1, "Engine reset", null, PIX_SEVERITY_LEVEL.PIX_SEVERITY_LEVEL_FATAL, 5),
            new(2, "Fence", "last completed", PIX_SEVERITY_LEVEL.PIX_SEVERITY_LEVEL_INFO, 9),
            new(3, "Watchdog", null, PIX_SEVERITY_LEVEL.PIX_SEVERITY_LEVEL_ERROR, 1),
            new(4, null, null, PIX_SEVERITY_LEVEL.PIX_SEVERITY_LEVEL_WARNING, null),
        ];
        List<DumpTriageObservation> found = TriageObservationBuilder.QueueStatus(1, "Direct queue", statuses, 2, 40, "dump-1");
        Assert.Equal(new[] { ("queue-1-status-0", 90), ("queue-1-status-2", 75), ("queue-1-status-3", 45) }, found.Select(o => (o.Id, o.Priority)));
        Assert.Contains("FATAL", found[0].Summary);
        Assert.Contains("'4'", found[2].Summary);
        Assert.Contains("pageFaultCount", JsonSerializer.Serialize(found[0].Evidence, Json.Options));
        Assert.Equal("FATAL", TriageObservationBuilder.MaxSeverity(statuses.Select(s => s.Severity)));
        Assert.Null(TriageObservationBuilder.MaxSeverity([]));
        Assert.Equal(0, TriageObservationBuilder.SeverityPriority(PIX_SEVERITY_LEVEL.PIX_SEVERITY_LEVEL_INFO));
    }

    [Fact]
    public void GpuStateTablesCorrelationCoverageKeysAndContinuations()
    {
        Assert.True(TriageObservationBuilder.IsInterestingGpuStateTable("Engine Hang Info", null));
        Assert.True(TriageObservationBuilder.IsInterestingGpuStateTable("Registers", "TDR reset reasons"));
        Assert.False(TriageObservationBuilder.IsInterestingGpuStateTable("Adapter", "memory segments"));
        DumpTriageObservation table = TriageObservationBuilder.GpuStateTable(3, "Fault Info", null, 4, "dump-1");
        Assert.Equal(("gpu-state-3", 60, "pix_dump_gpu_state"), (table.Id, table.Priority, table.NextCalls[0].Tool));

        var inProgress = new DumpTriageObservation("event-0-1", 60, "eventStatus", "Draw has status IN_PROGRESS.", new { }, []);
        Assert.Equal(70, TriageObservationBuilder.WithCorrelation(inProgress, true).Priority);
        Assert.Contains("Correlated", TriageObservationBuilder.WithCorrelation(inProgress, true).Summary);
        Assert.Same(inProgress, TriageObservationBuilder.WithCorrelation(inProgress, false));
        Assert.Equal(99, TriageObservationBuilder.WithCorrelation(inProgress with { Priority = 95 }, true).Priority);

        Assert.NotEqual(TriageObservationBuilder.EventChildrenCoverageKey(0, [0, 1]), TriageObservationBuilder.EventChildrenCoverageKey(0, [1, 0]));
        Assert.Equal("eventChildren:2:0-3-1", TriageObservationBuilder.EventChildrenCoverageKey(2, [0, 3, 1]));

        ToolCallDto[] next = TriageObservationBuilder.WalkContinuation("dump-1", 1, 7, 1);
        Assert.Equal(new[] { "pix_dump_events", "pix_dump_triage" }, next.Select(c => c.Tool));
        Assert.All(next, call => Assert.True(ToolRegistry.Accepts(call), call.Tool));
        StructuredToolResults.ValidateArguments("pix_dump_events", Arguments(next[0]));
        StructuredToolResults.ValidateArguments("pix_dump_triage", Arguments(next[1]));

        DumpTriageCoverage d3d = TriageObservationBuilder.D3DStateCoverage();
        Assert.Equal("unsupported", d3d.State);
        Assert.Contains("IPixD3DState", d3d.Reason);
    }

    [Fact]
    public void PixDiagnosisIsAHeuristicObservationOnlyWhenPixReportsAnError()
    {
        Assert.Null(TriageObservationBuilder.Diagnosis(new DumpDiagnosisDto("D3D12_DEVICE_ERROR_CODE_NONE", null, null, null, false, null), "dump-1"));
        Assert.Null(TriageObservationBuilder.Diagnosis(new DumpDiagnosisDto(null, " ", null, null, false, null), "dump-1"));
        DumpTriageObservation found = TriageObservationBuilder.Diagnosis(
            new DumpDiagnosisDto("D3D12_DEVICE_ERROR_CODE_HANG", "HANG_ENGINE_0", "Hung", "The GPU stopped responding.", true, null), "dump-1")!;
        Assert.Equal(("pix-diagnosis", 85, "pixDiagnosis"), (found.Id, found.Priority, found.Kind));
        Assert.Contains("HANG_ENGINE_0", found.Summary);
        Assert.Contains("heuristic", found.Summary);
    }

    private sealed record Node(string Name, Node[] Children);

    [Fact]
    public void WalkHandsEachChildrenCallbackTheNodePath()
    {
        Node[] roots = [new("a", [new("a0", []), new("a1", [new("a10", [])])]), new("b", [])];
        var requested = new List<string>();
        string[] visited = DumpTools.Walk(roots, (node, path) =>
        {
            requested.Add(node.Name + "@" + string.Join('-', path));
            return node.Children;
        }).Select(v => v.Node.Name).ToArray();
        Assert.Equal(new[] { "a", "a0", "a1", "a10", "b" }, visited);
        Assert.Equal(new[] { "a@0", "a0@0-0", "a1@0-1", "a10@0-1-0", "b@1" }, requested);
    }

    private static Dictionary<string, JsonElement> Arguments(ToolCallDto call)
        => JsonSerializer.SerializeToElement(call.Arguments, Json.Options).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
}
