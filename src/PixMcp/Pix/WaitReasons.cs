namespace PixMcp.Pix;

/// <summary>
/// Probable names of context-switch wait reasons: the Windows KWAIT_REASON enumeration as the WDK documents it. PixStorage
/// records the raw FromThreadWaitReason code only, so every name is reported as probable.
/// </summary>
internal static class WaitReasons
{
    private static readonly string[] Names =
    [
        "Executive", "FreePage", "PageIn", "PoolAllocation", "DelayExecution", "Suspended", "UserRequest", "WrExecutive", "WrFreePage", "WrPageIn",
        "WrPoolAllocation", "WrDelayExecution", "WrSuspended", "WrUserRequest", "WrSpare0", "WrQueue", "WrLpcReceive", "WrLpcReply", "WrVirtualMemory", "WrPageOut",
        "WrRendezvous", "WrKeyedEvent", "WrTerminated", "WrProcessInSwap", "WrCpuRateControl", "WrCalloutStack", "WrKernel", "WrResource", "WrPushLock", "WrMutex",
        "WrQuantumEnd", "WrDispatchInt", "WrPreempted", "WrYieldExecution", "WrFastMutex", "WrGuardedMutex", "WrRundown", "WrAlertByThreadId", "WrDeferredPreempt", "WrPhysicalFault",
        "WrIoRing", "WrMdlCache", "WrRcu",
    ];

    /// <summary>Switch-outs that leave the thread runnable: quantum end, dispatch interrupt, preemption, yield and deferred preemption.</summary>
    public static readonly IReadOnlySet<int> Runnable = new HashSet<int> { 30, 31, 32, 33, 38 };

    public static string? ProbableName(int code) => code >= 0 && code < Names.Length ? Names[code] : null;

    public static bool IsRunnable(int? code) => code is int value && Runnable.Contains(value);
}
