using System.ComponentModel;
using ModelContextProtocol.Server;
using PixMcp.Pix;
using PixMcp.Pix.Handles;

namespace PixMcp.Tools;

[McpServerToolType]
public static class CsvTools
{
    [McpServerTool(Name = "pix_csv_compare", Title = "Compare Unreal CSVs", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Compares recorded UE CsvProfiler GPU pass timings using optional pixdiff. Returns a job and a complete saved comparison: positive candidate-minus-baseline milliseconds mean the candidate is slower. Recorded CSV aggregates remain separate from PIX replay timings. Read /items or /missing with pix_result_read; follow a pass using pix_csv_pass_candidates.")]
    public static Task<string> Compare(JobManager jobs, [Description("Path of the baseline Unreal CSV file.")] string baselinePath, [Description("Path of the candidate Unreal CSV file.")] string candidatePath,
        [Description("GPU timing column prefix, default GPU/. Selected columns must contain milliseconds.")] string prefix = "GPU/",
        [Description("mean, median, or p95. Median is the tutorial's two-GPU default.")] string stat = "median",
        [Description("Process timeout in seconds, 1 through 3600. Default: 120.")] int timeoutSeconds = 120,
        [Description(Tools.WaitSecondsDescription)] double waitSeconds = 0, CancellationToken cancellationToken = default)
    {
        if (prefix is null || stat is not ("mean" or "median" or "p95") || timeoutSeconds is < 1 or > 3600)
            throw new PixToolException(PixErrors.Codes.InvalidArguments, "Use stat mean/median/p95, a non-null prefix, and timeoutSeconds 1 through 3600.");
        string baseline = CsvComparison.ValidatePath(baselinePath), candidate = CsvComparison.ValidatePath(candidatePath);
        PixDiffInfoDto helper = PixDiffDiscovery.Info();
        if (!helper.Available) throw new PixToolException(PixErrors.Codes.PixdiffUnavailable, helper.Error ?? "pixdiff is unavailable.");
        return Tools.RunJob(jobs, "pix_csv_compare", () => jobs.StartManaged("csvComparison", "Compare recorded CSV GPU passes", async job =>
        {
            string json = await CsvComparison.RunProcess(CsvComparison.BuildStartInfo(helper.Executable!, baseline, candidate, prefix, stat),
                TimeSpan.FromSeconds(timeoutSeconds), job.Cancellation.Token, job.AddMessage).ConfigureAwait(false);
            job.ThrowIfCancellationRequested();
            return CsvComparison.Parse(json, baseline, candidate, prefix, stat);
        }), waitSeconds, cancellationToken);
    }

    [McpServerTool(Name = "pix_csv_pass_candidates", Title = "CSV pass marker candidates", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false), Description("Finds candidate PIX markers for an exact passName in a saved CSV comparison. Searches the caller-selected GPU capture by name after removing the CSV prefix; exact names precede substring matches. All results are name-based candidates, including duplicates, and do not prove that the capture corresponds to either CSV. Pages reuse saved comparison data without reading the CSV files or replaying.")]
    public static async Task<string> PassCandidates(PixSession session, [Description("The resultRef of a finished pix_csv_compare job.")] string resultRef, [Description("CSV pass (stat) name whose PIX marker candidates are wanted.")] string passName, [Description("GPU capture handle whose markers are searched.")] string handle,
        [Description("First item to return (default 0).")] int offset = 0, [Description("Maximum items to return (default 25, max 1000).")] int limit = 25, CancellationToken cancellationToken = default)
    {
        if (offset < 0 || limit is < 1 or > 1000)
            throw new PixToolException(PixErrors.Codes.InvalidArguments, "offset must be nonnegative and limit must be 1 through 1000.");
        (string prefix, CsvPassDto pass) = await Task.Run(() => CsvComparison.ReadPass(session.Results, resultRef, passName, cancellationToken), cancellationToken).ConfigureAwait(false);
        return await Tools.Run(session, "pix_csv_pass_candidates", () =>
        {
            GpuCaptureHandle capture = session.Get<GpuCaptureHandle>(handle);
            IEnumerable<EventDto> Markers()
            {
                foreach (var queue in capture.Queues)
                foreach (var evt in capture.AllEvents(queue.Index))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Tools.MatchesKind(evt, "marker")) yield return capture.DescribeEvent(queue.Index, evt);
                }
            }
            return FindCandidates(resultRef, prefix, pass, handle, Markers(), offset, limit, cancellationToken);
        }, cancellationToken).ConfigureAwait(false);
    }

    internal static CsvPassCandidatesDto FindCandidates(string resultRef, string prefix, CsvPassDto pass, string handle,
        IEnumerable<EventDto> markers, int offset, int limit, CancellationToken cancellationToken = default)
    {
        const string evidence = "Case-insensitive marker-name search; capture association and pass identity are unverified. CSV timings are recorded aggregates, not PIX replay measurements.";
        string search = pass.Name.StartsWith(prefix, StringComparison.Ordinal) ? pass.Name[prefix.Length..] : pass.Name;
        if (pass.Name == prefix + "Total" || string.IsNullOrWhiteSpace(search))
            return new(resultRef, pass.Name, handle, search, evidence, false, pass, 0, offset, 0, null, [],
                "This CSV aggregate has no specific marker to match.", [new("pix_gpu_overview", new { handle })]);
        var matches = new List<(EventDto Event, bool Exact)>();
        foreach (EventDto evt in markers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (evt.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                matches.Add((evt, evt.Name.Equals(search, StringComparison.OrdinalIgnoreCase)));
        }
        var items = matches.OrderByDescending(m => m.Exact).ThenBy(m => m.Event.QueueIndex).ThenBy(m => m.Event.Index)
            .Skip(offset).Take(limit).Select(m => new CsvPassCandidateDto(m.Event, m.Exact ? "exactName" : "containsName",
                [new("pix_gpu_inspect_event", new { eventRef = m.Event.EventRef }),
                 new("pix_gpu_timing_events", new { handle, scope = m.Event.EventRef })])).ToArray();
        int? next = (long)offset + items.Length < matches.Count ? offset + items.Length : null;
        return new(resultRef, pass.Name, handle, search, evidence, false, pass, matches.Count, offset, items.Length, next, items, null,
            next.HasValue ? [new("pix_csv_pass_candidates", new { resultRef, passName = pass.Name, handle, offset = next.Value, limit })] : []);
    }
}
