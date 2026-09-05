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
