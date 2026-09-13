namespace PixMcp.Pix;

/// <summary>
/// Heuristic per-frame verdicts from the share of frame time the GPU executed recorded work and the render thread was on
/// CPU, blocked or ready. Rules are evaluated in order and the first match wins; every threshold is echoed in the response.
/// </summary>
internal static class VerdictRules
{
    public const double GpuBoundBusy = 90, UnknownAbove = 50, PresentBoundShare = 50, CpuBoundOnCpu = 80, GpuIdleBelow = 60, BlockedAtLeast = 40, ContendedReady = 25;

    public static readonly TimingVerdictRuleDto[] Rules =
    [
        new("gpuBound", "gpuBusy >= 90 %"),
        new("unknown", "render-thread state unknown for > 50 % of the frame"),
        new("presentBound", "presentToVsync >= 50 % (present frames only)"),
        new("cpuBound", "onCpu >= 80 % and gpuBusy < 60 %"),
        new("syncBound", "blocked >= 40 % and gpuBusy >= 60 %"),
        new("waitBound", "blocked >= 40 % and gpuBusy < 60 %"),
        new("contended", "readyNotRunning >= 25 %"),
        new("balanced", "no rule above matched"),
    ];

    public static readonly TimingVerdictRulesDto RulesDto = new(true, Rules, WaitReasons.Runnable.Order().ToArray(),
        "Rules are evaluated in order for each frame and the first match wins; percentages are shares of the frame duration.");

    public static readonly string[] Semantics =
    [
        "Per-frame percentages are shares of that frame's duration; summary shares weight frames by duration.",
        "gpuBusy is the union of recorded ExecuteCommandLists execution on every queue of the process, not utilization inside the GPU.",
        "Render-thread states come from its context switches. ContextSwitch does not record the old thread state: wait reasons 30-33 and 38 count as ready, other waits are blocked until the ready event named by the next switch-in.",
        "Wait reason names are probable KWAIT_REASON names. Verdicts are experimental heuristics; check the rules and per-frame shares before acting.",
    ];

    public static string Classify(double gpuBusy, double onCpu, double blocked, double ready, double unknown, double? presentToVsync = null)
    {
        if (gpuBusy >= GpuBoundBusy) return "gpuBound";
        if (unknown > UnknownAbove) return "unknown";
        if (presentToVsync >= PresentBoundShare) return "presentBound";
        if (onCpu >= CpuBoundOnCpu && gpuBusy < GpuIdleBelow) return "cpuBound";
        if (blocked >= BlockedAtLeast) return gpuBusy >= GpuIdleBelow ? "syncBound" : "waitBound";
        if (ready >= ContendedReady) return "contended";
        return "balanced";
    }

    /// <summary>The most frequent verdict (ties in rule order), its share of frames and a confidence from dominance, frame count and scheduling coverage.</summary>
    public static (string Verdict, double Percent, string Confidence) Summarize(IReadOnlyDictionary<string, long> frames, int total, double unknownPercent)
    {
        if (total == 0 || frames.Count == 0) return ("unknown", 0, "low");
        KeyValuePair<string, long> top = frames.OrderByDescending(f => f.Value).ThenBy(f => Array.FindIndex(Rules, r => r.Verdict == f.Key)).First();
        double percent = Math.Round(100.0 * top.Value / total, 2);
        string confidence = top.Key == "unknown" ? "low"
            : percent >= 75 && total >= 30 && unknownPercent < 10 ? "high"
            : percent >= 50 && total >= 10 && unknownPercent < 50 ? "medium"
            : "low";
        return (top.Key, percent, confidence);
    }

    public static string Implication(string verdict) => verdict switch
    {
        "gpuBound" => "The GPU executed submitted work for most of each frame: GPU cost limits the frame rate. Capture a GPU frame and rank pass costs.",
        "cpuBound" => "The render thread ran for most of each frame while the GPU was mostly idle: CPU work on that thread limits the frame rate. Inspect hotspots and the marker tree of the slowest frames.",
        "syncBound" => "The render thread was blocked for much of each frame while the GPU was busy: it waits on GPU completion (fences, Present or readback). Inspect its thread switches and wait reasons.",
        "waitBound" => "The render thread was blocked for much of each frame while the GPU was mostly idle: it waits on something else (VSync or a frame limiter, locks, I/O). Inspect its thread switches and wait reasons.",
        "contended" => "The render thread was ready but not running for a large share of each frame: other threads took its cores. Inspect ready latency and core usage.",
        "presentBound" => "Most of each frame passed between the present call and VSync: presentation pacing limits the frame rate.",
        "balanced" => "No single limiter dominates the frames; compare the longest frames.",
        _ => "Scheduling data is missing for most frame time, so the verdict cannot be computed.",
    };
}
