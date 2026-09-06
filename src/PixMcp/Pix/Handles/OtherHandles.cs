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
    // Mutated on the PIX thread and by PIX notification callbacks; read by pix_handles/pix_info
    // off the worker, so every access goes through the lock.
    private readonly List<object> _events = new();
    private readonly List<uint> _processIds = new();
    public string? TimingCaptureInProgress { get; set; }

    public void Note(string kind, object? detail = null)
    {
        lock (_events)
        {
            _events.Add(new { time = DateTimeOffset.UtcNow, kind, detail });
            if (_events.Count > 200)
            {
                _events.RemoveAt(0);
            }
        }
    }

    public object[] RecentEvents()
    {
        lock (_events)
        {
            return _events.TakeLast(30).ToArray();
        }
    }

    public void AddProcess(uint processId) { lock (_events) _processIds.Add(processId); }
    public void ClearProcesses() { lock (_events) _processIds.Clear(); }
    /// <summary>Snapshot of the process ids launched or attached through this connection.</summary>
    public uint[] ProcessIds { get { lock (_events) return _processIds.ToArray(); } }

    public override object Summary() => new
    {
        handle = Id,
        kind = Kind,
        path = Path,
        openedAt = OpenedAt,
        launchedProcessIds = ProcessIds,
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
