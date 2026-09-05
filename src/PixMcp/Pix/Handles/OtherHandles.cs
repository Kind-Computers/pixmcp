using Microsoft.PIX;
using Microsoft.PIX.Extension.DeviceConnection;
using Microsoft.PIX.Internal;

namespace PixMcp.Pix.Handles;

public sealed class TimingCaptureHandle : PixHandle
{
    public TimingCaptureHandle(string path, IPixTimingCaptureDocument document) : base(path)
    {
        Document = document;
        CapturePath = Interop.W(document.GetCapturePath());
        PixStoragePath = Interop.W(document.GetPixStoragePath());
    }

    public override string Kind => "timing";
    public IPixTimingCaptureDocument Document { get; private set; }
    public string CapturePath { get; }
    public string PixStoragePath { get; }
    public bool SymbolsResolved { get; set; }

    public override object Summary() => new
    {
        handle = Id,
        kind = Kind,
        path = Path,
        openedAt = OpenedAt,
        capturePath = CapturePath,
        pixStoragePath = PixStoragePath,
        symbolsResolved = SymbolsResolved,
    };

    public override void Close(List<string> warnings)
    {
        try { Document.Close(); }
        catch (Exception ex) { warnings.Add("Close: " + PixErrors.Describe(ex)); }
        Document = null!;
    }
}

public sealed class DumpHandle : PixHandle
{
    public DumpHandle(string path, IPixPostmortemDocument document) : base(path)
    {
        Document = document;
    }

    public override string Kind => "dump";
    public IPixPostmortemDocument Document { get; private set; }
    public List<IPixPostmortemQueueInfo>? Queues { get; set; }
    public object? Metadata { get; set; }

    public override object Summary() => new
    {
        handle = Id,
        kind = Kind,
        path = Path,
        openedAt = OpenedAt,
        metadata = Metadata,
    };

    public override void Close(List<string> warnings)
    {
        Queues = null;
        Document = null!;
    }
}

public sealed class ConnectionHandle : PixHandle
{
    public ConnectionHandle(string path, IPixConnectionDocument connection, DelegateConnectionNotifications notifications) : base(path)
    {
        Connection = connection;
        Notifications = notifications;
    }

    public override string Kind => "device";
    public IPixConnectionDocument Connection { get; private set; }
    public DelegateConnectionNotifications Notifications { get; }
    public List<object> Events { get; } = new();
    public List<uint> LaunchedProcessIds { get; } = new();
    public string? TimingCaptureInProgress { get; set; }

    public void Note(string kind, object? detail = null)
    {
        lock (Events)
        {
            Events.Add(new { time = DateTimeOffset.UtcNow, kind, detail });
            if (Events.Count > 200)
            {
                Events.RemoveAt(0);
            }
        }
    }

    public object[] RecentEvents()
    {
        lock (Events)
        {
            return Events.TakeLast(30).ToArray();
        }
    }

    public override object Summary() => new
    {
        handle = Id,
        kind = Kind,
        path = Path,
        openedAt = OpenedAt,
        launchedProcessIds = LaunchedProcessIds,
        timingCaptureInProgress = TimingCaptureInProgress,
        recentEvents = RecentEvents(),
    };

    public override void Close(List<string> warnings)
    {
        if (TimingCaptureInProgress is not null)
        {
            try { Connection.StopTimingCapture(); }
            catch (Exception ex) { warnings.Add("StopTimingCapture: " + PixErrors.Describe(ex)); }
        }
        try { Connection.DetachFromAllProcesses(false); }
        catch (Exception ex) { warnings.Add("DetachFromAllProcesses: " + PixErrors.Describe(ex)); }
        try { Connection.Disconnect(); }
        catch (Exception ex) { warnings.Add("Disconnect: " + PixErrors.Describe(ex)); }
        Connection = null!;
    }
}
