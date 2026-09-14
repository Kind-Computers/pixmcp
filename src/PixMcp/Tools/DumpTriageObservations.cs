using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.PIX;
using PixMcp.Pix;

namespace PixMcp.Tools;

/// <summary>PIX's own diagnosis of a dump; PIX derives the error bucket and summaries heuristically.</summary>
public sealed record DumpDiagnosisDto(string? ErrorCode, string? ErrorBucket, string? GpuStatus, string? BriefSummary, bool DetailedSummaryAvailable,
    string? DocumentationLink);

/// <summary>The D3D runtime journal as triage read it: all entries, the scanned window, failures inside it and the last failure's index.</summary>
public sealed record DumpJournalSummaryDto(int Entries, int Scanned, int Errors, int? LastErrorIndex);

internal readonly record struct JournalEntryData(int Index, uint Code, uint ThreadId, uint TickCount, string? Message);

internal readonly record struct HardwareStatusData(ulong Id, string? Name, string? Description, PIX_SEVERITY_LEVEL Severity, object? Value);

/// <summary>The pure observation rules of pix_dump_triage, apart from the native reads so they stay unit-testable.</summary>
internal static class TriageObservationBuilder
{
    public const int DefaultMaxEvents = 5000, MaxEvents = 50000, JournalWindow = 200, JournalObservationCap = 20;

    private static readonly Regex InterestingTable = new("fault|hang|timeout|error|status|reset|exception",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Journal codes are HRESULT-like: a set high bit marks a failure (non-failure codes are still counted as entries).</summary>
    public static bool IsJournalFailure(uint code) => (code & 0x80000000u) != 0;

    /// <summary>Summarises the last journal window and turns up to twenty of its most recent failures into observations (priority 70).</summary>
    public static (DumpJournalSummaryDto Summary, List<DumpTriageObservation> Observations) Journal(IReadOnlyList<JournalEntryData> window, int total, string handle)
    {
        JournalEntryData[] failures = window.Where(e => IsJournalFailure(e.Code)).ToArray();
        List<DumpTriageObservation> observations = failures.Reverse().Take(JournalObservationCap).Select(e => new DumpTriageObservation(
            $"journal-{e.Index}", 70, "runtimeError",
            $"D3D runtime journal error {Interop.Hex(e.Code)}{(e.Message is null ? "" : ": " + Truncate(e.Message, 160))}.",
            new { index = e.Index, code = Interop.Hex(e.Code), threadId = e.ThreadId, tickCount = e.TickCount, message = e.Message },
            [new ToolCallDto("pix_dump_journal", new { handle, offset = e.Index, limit = 1 }, CostHints.Query)])).ToList();
        return (new(total, window.Count, failures.Length, failures.Length == 0 ? null : failures[^1].Index), observations);
    }

    /// <summary>FATAL 90, ERROR 75, WARNING 45; INFO produces no observation.</summary>
    public static int SeverityPriority(PIX_SEVERITY_LEVEL severity) => severity switch
    {
        PIX_SEVERITY_LEVEL.PIX_SEVERITY_LEVEL_FATAL => 90,
        PIX_SEVERITY_LEVEL.PIX_SEVERITY_LEVEL_ERROR => 75,
        PIX_SEVERITY_LEVEL.PIX_SEVERITY_LEVEL_WARNING => 45,
        _ => 0,
    };

    public static string SeverityName(PIX_SEVERITY_LEVEL severity)
    {
        const string prefix = "PIX_SEVERITY_LEVEL_";
        string name = severity.ToString();
        return name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : name;
    }

    public static string? MaxSeverity(IEnumerable<PIX_SEVERITY_LEVEL> severities)
    {
        PIX_SEVERITY_LEVEL[] all = severities.ToArray();
        return all.Length == 0 ? null : SeverityName(all.Max());
    }

    /// <summary>One observation per hardware status entry of WARNING severity or above, with the queue's page-fault and root-event counts.</summary>
    public static List<DumpTriageObservation> QueueStatus(int queueIndex, string queueName, IReadOnlyList<HardwareStatusData> statuses,
        long? pageFaultCount, long? rootEventCount, string handle)
    {
        var observations = new List<DumpTriageObservation>();
        for (int i = 0; i < statuses.Count; i++)
        {
            HardwareStatusData status = statuses[i];
            int priority = SeverityPriority(status.Severity);
            if (priority == 0) continue;
            string severity = SeverityName(status.Severity);
            string label = status.Name ?? status.Id.ToString(CultureInfo.InvariantCulture);
            observations.Add(new($"queue-{queueIndex}-status-{i}", priority, "queueStatus", $"{queueName}: {severity} hardware status '{label}'.",
                new { queueIndex, queueName, statusIndex = i, id = status.Id, name = status.Name, description = status.Description, severity, value = status.Value, pageFaultCount, rootEventCount },
                [new ToolCallDto("pix_dump_queues", new { handle }, CostHints.Query), new ToolCallDto("pix_dump_events", new { handle, queueIndex, status = "IN_PROGRESS" }, CostHints.Query)]));
        }
        return observations;
    }

    public static bool IsInterestingGpuStateTable(string name, string? description)
        => InterestingTable.IsMatch(name) || description is not null && InterestingTable.IsMatch(description);

    /// <summary>A fault, hang, timeout, error, status, reset or exception table with rows (priority 60, counts only).</summary>
    public static DumpTriageObservation GpuStateTable(int tableIndex, string name, string? description, ulong rootRows, string handle)
        => new($"gpu-state-{tableIndex}", 60, "gpuState", $"GPU state table '{name}' has {rootRows} row(s).",
            new { tableIndex, name, description, rootRows }, [new ToolCallDto("pix_dump_gpu_state", new { handle, tableIndex }, CostHints.Query)]);

    /// <summary>PIX's diagnosis as an observation (priority 85), only when PIX reports an error code, a bucket or a summary.</summary>
    public static DumpTriageObservation? Diagnosis(DumpDiagnosisDto diagnosis, string handle)
    {
        bool errorCode = diagnosis.ErrorCode is { Length: > 0 } code && code != "0" && !code.EndsWith("NONE", StringComparison.OrdinalIgnoreCase);
        if (!errorCode && string.IsNullOrWhiteSpace(diagnosis.ErrorBucket) && string.IsNullOrWhiteSpace(diagnosis.BriefSummary)) return null;
        string headline = diagnosis.ErrorBucket ?? diagnosis.ErrorCode ?? "device error";
        string summary = diagnosis.BriefSummary is null ? "" : ": " + Truncate(diagnosis.BriefSummary, 200);
        return new("pix-diagnosis", 85, "pixDiagnosis", $"PIX diagnosis {headline}{summary} (PIX's heuristic summary).", diagnosis,
            [new ToolCallDto("pix_dump_info", new { handle }, CostHints.Query)]);
    }

    /// <summary>Event-status and hardware-status observations on a queue with page faults or in-progress events gain 10 points, capped at 99.</summary>
    public static DumpTriageObservation WithCorrelation(DumpTriageObservation observation, bool suspectQueue)
        => !suspectQueue ? observation : observation with
        {
            Priority = Math.Min(99, observation.Priority + 10),
            Summary = observation.Summary + " Correlated: this queue also has page faults or in-progress events.",
        };

    /// <summary>Coverage key for unreadable children: native event ids repeat, the collection path does not.</summary>
    public static string EventChildrenCoverageKey(int queueIndex, int[] path) => $"eventChildren:{queueIndex}:{string.Join('-', path)}";

    /// <summary>Where a truncated walk stopped: the queue's events from that root, and triage with a larger budget.</summary>
    public static ToolCallDto[] WalkContinuation(string handle, int queueIndex, int rootIndex, int maxEvents) =>
    [
        new("pix_dump_events", new { handle, queueIndex, offset = rootIndex, limit = 50, maxEvents = 5000 }, CostHints.Query),
        new("pix_dump_triage", new { handle, maxEvents = Math.Min(MaxEvents, Math.Max(maxEvents * 4, 1000)) }, CostHints.Query),
    ];

    public static DumpTriageCoverage D3DStateCoverage()
    {
        IReadOnlyList<string> notes = CompatibilityNotes.Texts("dumpD3DState", GpuVendor.Unknown, PixDiscovery.Version);
        return new("unsupported", Reason: notes.Count > 0 ? notes[0] : "IPixD3DState has no managed projection; D3D state tables are not readable through the API.");
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
