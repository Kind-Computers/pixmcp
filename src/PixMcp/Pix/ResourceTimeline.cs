using System.ComponentModel;
using PixMcp.Pix.Handles;

namespace PixMcp.Pix;

/// <summary>One observed use of a resource, flattened from the resource-use index.</summary>
internal sealed record TimelineUse(int QueueIndex, uint EventIndex, uint? GpuId, string Name, string MarkerPath, string Access, string ViewType, string Evidence,
    string? Stage, string? StateBefore, string? StateAfter);

/// <param name="OrderKey">Cross-queue order of an event: its GPU id, or the next GPU id on its queue for events without one.</param>
/// <param name="PresentAfter">True when the queue presents after the given event index.</param>
/// <param name="Eop">Measured EOP duration of an event, or null when timing is not collected or the event is untimed.</param>
internal sealed record TimelineInputs(string Handle, string ResourceName, ulong? EstimatedBytes, IReadOnlyList<TimelineUse> Uses, Func<int, uint, ulong> OrderKey,
    Func<int, uint, bool> PresentAfter, Func<int, uint, ulong?> Eop);

public sealed record ResourceTimelineRowDto(
    [property: Description("1-based position in capture order (GPU id, then queue and event index).")] int Ordinal,
    int QueueIndex, uint EventIndex, uint? GpuId, string Name, string MarkerPath,
    [property: Description("read, write, readWrite, copySrc, copyDst, barrier or unknown.")] string Access,
    string ViewType,
    [property: Description("eventScopedView (views bound at the event), capturedApiArgument, barrierArgument or nativeBinding.")] string Evidence,
    string? Stage, string? StateBefore, string? StateAfter,
    [property: Description("The event's measured EOP duration when timing is collected.")] ulong? EopNs);

public sealed record ResourcePhaseDto(int QueueIndex,
    [property: Description("The access class shared by consecutive uses on this queue.")] string Access,
    uint FirstEventIndex, uint LastEventIndex, int Events, string MarkerPath,
    [property: Description("Summed EOP of the phase's distinct events when timing is collected.")] ulong? EopNs, double? EopMs);

public sealed record ResourceTimelineSummaryDto(
    [property: Description("Distinct events per access class.")] int Reads, int Writes, int ReadWrites, int CopySources, int CopyDestinations, int Barriers, int Unknown,
    EventRef? FirstWrite, EventRef? LastRead,
    [property: Description("Estimated bytes times the distinct events that read or write the resource.")] ulong? EstimatedTrafficBytes,
    [property: Description("upperBound:fullResourceTouchPerUse: every use is assumed to touch the whole resource.")] string TrafficMethod);

public sealed record ResourceTimelineDto(ResourceSummaryDto Resource, string Semantics, ResourceTimelineSummaryDto Summary, IReadOnlyList<ResourcePhaseDto> Phases,
    IReadOnlyList<InsightDto> Insights, long Total, int Offset, int Count, int? NextOffset, IReadOnlyList<ResourceTimelineRowDto> Rows, IReadOnlyList<object> Coverage,
    IReadOnlyList<ToolCallDto> NextCalls)
{
    public ScopeDescriptionDto? Scope { get; init; }
    public ReplayProvenance? Provenance { get; init; }
}

internal sealed record ResourceTimelineResult(IReadOnlyList<ResourceTimelineRowDto> Rows, IReadOnlyList<ResourcePhaseDto> Phases, ResourceTimelineSummaryDto Summary,
    IReadOnlyList<InsightDto> Insights);

/// <summary>Orders a resource's uses across queues, groups them into access phases, and flags ordering and barrier findings.</summary>
internal static class ResourceTimeline
{
    public const string TrafficMethod = "upperBound:fullResourceTouchPerUse";
    public const string Semantics = "Captured uses of one resource in capture order: views bound at draw and dispatch events, captured API object arguments " +
        "(copies, barriers, indirect buffers). This is binding evidence, not proof that every texel was read or written.";

    public static ResourceTimelineResult Build(TimelineInputs input)
    {
        ResourceTimelineRowDto[] rows = input.Uses
            .OrderBy(u => input.OrderKey(u.QueueIndex, u.EventIndex)).ThenBy(u => u.QueueIndex).ThenBy(u => u.EventIndex)
            .ThenBy(u => Array.IndexOf(ResourceAccess.Classes, u.Access)).ThenBy(u => u.ViewType, StringComparer.Ordinal)
            .Select((u, i) => new ResourceTimelineRowDto(i + 1, u.QueueIndex, u.EventIndex, u.GpuId, u.Name, u.MarkerPath, u.Access, u.ViewType, u.Evidence, u.Stage,
                u.StateBefore, u.StateAfter, input.Eop(u.QueueIndex, u.EventIndex)))
            .ToArray();

        var phases = new List<ResourcePhaseDto>();
        foreach (IGrouping<int, ResourceTimelineRowDto> queue in rows.GroupBy(r => r.QueueIndex).OrderBy(g => g.Key))
        {
            ResourceTimelineRowDto? first = null;
            var events = new HashSet<uint>();
            uint last = 0;
            ulong eop = 0;
            bool timed = false;
            void Flush()
            {
                if (first is not null) phases.Add(new(queue.Key, first.Access, first.EventIndex, last, events.Count, first.MarkerPath, timed ? eop : null, timed ? Metrics.Ms(eop) : null));
            }
            foreach (ResourceTimelineRowDto row in queue)
            {
                if (first is null || row.Access != first.Access)
                {
                    Flush();
                    first = row;
                    events = [];
                    eop = 0;
                    timed = false;
                }
                if (events.Add(row.EventIndex) && row.EopNs is ulong duration)
                {
                    eop += duration;
                    timed = true;
                }
                last = row.EventIndex;
            }
            Flush();
        }

        int Distinct(Func<ResourceTimelineRowDto, bool> predicate) => rows.Where(predicate).Select(r => (r.QueueIndex, r.EventIndex)).Distinct().Count();
        EventRef Ref(ResourceTimelineRowDto r) => new(input.Handle, r.QueueIndex, r.EventIndex);
        ResourceTimelineRowDto? firstWrite = rows.FirstOrDefault(r => ResourceAccess.Writes(r.Access));
        ResourceTimelineRowDto? lastRead = rows.LastOrDefault(r => ResourceAccess.Reads(r.Access));
        int touches = Distinct(r => ResourceAccess.Reads(r.Access) || ResourceAccess.Writes(r.Access));
        var summary = new ResourceTimelineSummaryDto(Distinct(r => r.Access == "read"), Distinct(r => r.Access == "write"), Distinct(r => r.Access == "readWrite"),
            Distinct(r => r.Access == "copySrc"), Distinct(r => r.Access == "copyDst"), Distinct(r => r.Access == "barrier"), Distinct(r => r.Access == "unknown"),
            firstWrite is null ? null : Ref(firstWrite), lastRead is null ? null : Ref(lastRead), input.EstimatedBytes is ulong bytes ? bytes * (ulong)touches : null, TrafficMethod);

        var insights = new List<InsightDto>();
        void Add(string id, string severity, string text, Dictionary<string, object?> evidence, string implication, params ToolCallDto[] calls)
            => insights.Add(new(id, severity, text, evidence, implication, calls.Where(ToolRegistry.Accepts).ToArray()));
        string name = input.ResourceName;

        ResourceTimelineRowDto? firstPureRead = rows.FirstOrDefault(r => r.Access is "read" or "copySrc");
        ResourceTimelineRowDto? firstPureWrite = rows.FirstOrDefault(r => r.Access is "write" or "copyDst");
        if (firstPureRead is not null && firstPureWrite is not null && firstPureRead.Ordinal < firstPureWrite.Ordinal)
            Add("read_before_write", "info", $"{name} is read at queue {firstPureRead.QueueIndex} event {firstPureRead.EventIndex} before its first captured write.",
                new() { ["firstRead"] = Ref(firstPureRead), ["firstWrite"] = Ref(firstPureWrite) },
                "The first read sees contents from before this capture's writes (an upload, an earlier frame or uninitialised memory); if the pass expects this frame's data, the order is wrong.",
                new ToolCallDto("pix_gpu_inspect_event", new { eventRef = Ref(firstPureRead) }));

        if (rows.Any(r => r.Access == "barrier" && r.StateAfter is not null))
            foreach (ResourceTimelineRowDto write in rows.Where(r => r.Access is "write" or "copyDst"))
            {
                ResourceTimelineRowDto? read = rows.FirstOrDefault(r => r.Ordinal > write.Ordinal && r.QueueIndex == write.QueueIndex && r.Access is "read" or "copySrc"
                    && r.EventIndex != write.EventIndex);
                if (read is null || rows.Any(r => r.Access == "barrier" && r.QueueIndex == write.QueueIndex && r.Ordinal > write.Ordinal && r.Ordinal < read.Ordinal)) continue;
                Add("missing_barrier_between_write_and_read", "info",
                    $"{name} is written at event {write.EventIndex} and read at event {read.EventIndex} on queue {write.QueueIndex} with no captured barrier between them.",
                    new() { ["write"] = Ref(write), ["read"] = Ref(read), ["writeView"] = write.ViewType, ["readView"] = read.ViewType },
                    "Other uses of this resource are transitioned explicitly, so this pair may rely on implicit state promotion or miss a transition; the D3D12 debug layer confirms which.",
                    new ToolCallDto("pix_gpu_inspect_event", new { eventRef = Ref(read) }));
                break;
            }

        ResourceTimelineRowDto? lastWrite = rows.LastOrDefault(r => ResourceAccess.Writes(r.Access));
        if (lastWrite is not null && !rows.Any(r => ResourceAccess.Reads(r.Access)) && !input.PresentAfter(lastWrite.QueueIndex, lastWrite.EventIndex))
            Add("written_never_read", "info", $"{name} is written {summary.Writes + summary.ReadWrites + summary.CopyDestinations} time(s) but never read in this capture.",
                new() { ["lastWrite"] = Ref(lastWrite), ["writeEvents"] = summary.Writes + summary.ReadWrites + summary.CopyDestinations },
                "Unless a later frame or the CPU consumes it, the work that fills this resource may be wasted.",
                new ToolCallDto("pix_gpu_inspect_event", new { eventRef = Ref(lastWrite) }));

        if (rows.Where(r => r.MarkerPath.Length > 0).GroupBy(r => (r.QueueIndex, r.MarkerPath))
            .FirstOrDefault(g => g.Any(r => r.ViewType == "RENDER_TARGET_VIEW") && g.Any(r => r.ViewType == "SHADER_RESOURCE_VIEW")) is { } pass)
        {
            ResourceTimelineRowDto target = pass.First(r => r.ViewType == "RENDER_TARGET_VIEW"), source = pass.First(r => r.ViewType == "SHADER_RESOURCE_VIEW");
            Add("bound_as_rtv_and_srv_same_pass", "warning", $"{name} is bound as a render target and as a shader resource inside {pass.Key.MarkerPath}.",
                new() { ["renderTargetEvent"] = Ref(target), ["shaderResourceEvent"] = Ref(source), ["markerPath"] = pass.Key.MarkerPath },
                "Reading and writing the same texture in one pass risks a feedback loop or forces the runtime to unbind one view; split the pass or use a copy.",
                new ToolCallDto("pix_gpu_inspect_event", new { eventRef = Ref(target) }));
        }
        return new(rows, phases, summary, insights.OrderBy(i => i.Severity == "warning" ? 0 : 1).ToArray());
    }
}
