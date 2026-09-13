using System.Collections.Concurrent;

namespace PixMcp.Pix.Handles;

public abstract class PixHandle
{
    protected PixHandle(string path)
    {
        Path = path;
    }

    public string Id { get; internal set; } = string.Empty;
    public abstract string Kind { get; }
    public string Path { get; }
    public DateTimeOffset OpenedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Background jobs that prepare expensive per-handle state (analysis start, timing collection,
    /// a counter set), keyed by what they prepare. Query tools reuse a running job instead of
    /// blocking the PIX thread or queuing a second replay (see Tools.RunWhenReady).
    /// </summary>
    internal ConcurrentDictionary<string, Job> PreparationJobs { get; } = new();

    /// <summary>Serializes "find a running preparation or start one" so two callers never start the same replay twice.</summary>
    internal object PreparationGate { get; } = new();

    /// <summary>Fingerprint of the last provenance block returned for this handle, per property name (provenance dedup).</summary>
    internal ConcurrentDictionary<string, string> ProvenanceFingerprints { get; } = new(StringComparer.Ordinal);

    /// <summary>Releases PIX objects. Called on the worker thread. Append non-fatal problems to <paramref name="warnings"/>.</summary>
    public abstract void Close(List<string> warnings);

    public virtual object Summary() => new
    {
        handle = Id,
        kind = Kind,
        path = Path,
        openedAt = OpenedAt,
    };

    public static string KindOf<T>() where T : PixHandle => typeof(T).Name switch
    {
        nameof(GpuCaptureHandle) => "gpu",
        nameof(TimingCaptureHandle) => "timing",
        nameof(DumpHandle) => "dump",
        nameof(ConnectionHandle) => "device",
        _ => typeof(T).Name,
    };
}
