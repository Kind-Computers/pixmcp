using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.PIX.Extension;
using Microsoft.PIX.Internal;
using ModelContextProtocol;
using PixMcp.Pix.Handles;

namespace PixMcp.Pix;

/// <summary>
/// Owns the PIX factory and the registry of open documents (GPU captures, timing captures,
/// dump files, device connections). PIX itself is the queryable store; this is just the
/// in-memory handle table so expensive state (an analysis session) survives across tool calls.
/// </summary>
public sealed class PixSession : IDisposable
{
    private readonly PixWorker _worker;
    private readonly ILogger<PixSession> _logger;
    private readonly ConcurrentDictionary<string, PixHandle> _handles = new();
    private readonly Dictionary<string, int> _counters = new();
    private IPixFactoryExperimental? _factory;

    public PixSession(PixWorker worker, ILogger<PixSession> logger)
    {
        _worker = worker;
        _logger = logger;
        Log = new PixLog(logger);
    }

    public PixWorker Worker => _worker;
    public PixLog Log { get; }
    public bool FactoryCreated => _factory is not null;

    /// <summary>The PIX factory; created lazily on the worker thread on first use.</summary>
    public IPixFactoryExperimental Factory
    {
        get
        {
            if (_factory is null)
            {
                EnsurePixAvailable();
                _factory = PixApiExtensions.PixCreateFactory<IPixFactoryExperimental>();
                try { _factory.SetLogger(Log); }
                catch (Exception ex) { _logger.LogWarning(ex, "SetLogger failed; PIX engine log messages will not be captured."); }
                _logger.LogInformation("PIX factory created from {Dir}", PixDiscovery.InstallDir);
            }
            return _factory;
        }
    }

    public static void EnsurePixAvailable()
    {
        if (PixDiscovery.InstallDir is null)
        {
            throw new McpException("The PIX API is not available: " + (PixDiscovery.Error ?? "no PIX Preview install found") +
                                   " Set the PIX_DIR environment variable to a PIX Preview install (2606.18-preview or newer).");
        }
    }

    public Task<T> Run<T>(Func<T> work) => _worker.Run(work);

    public T Register<T>(T handle) where T : PixHandle
    {
        lock (_counters)
        {
            _counters.TryGetValue(handle.Kind, out int n);
            n++;
            _counters[handle.Kind] = n;
            handle.Id = $"{handle.Kind}-{n}";
        }
        _handles[handle.Id] = handle;
        _logger.LogInformation("Opened {Kind} handle {Id}: {Path}", handle.Kind, handle.Id, handle.Path);
        return handle;
    }

    public IReadOnlyList<PixHandle> Handles => _handles.Values.OrderBy(h => h.OpenedAt).ToArray();

    public PixHandle Get(string handleId)
    {
        if (string.IsNullOrWhiteSpace(handleId))
        {
            throw new McpException("A handle id is required (e.g. the value returned by pix_gpu_open).");
        }
        if (_handles.TryGetValue(handleId.Trim(), out PixHandle? handle))
        {
            return handle;
        }
        string known = _handles.Count == 0 ? "none open" : string.Join(", ", _handles.Keys);
        throw new McpException($"Unknown handle '{handleId}'. Open handles: {known}.");
    }

    public T Get<T>(string handleId) where T : PixHandle
    {
        PixHandle handle = Get(handleId);
        return handle as T
            ?? throw new McpException($"Handle '{handleId}' is a {handle.Kind} handle, but this tool needs a {PixHandle.KindOf<T>()} handle.");
    }

    /// <summary>Closes and forgets a handle. Must run on the worker thread.</summary>
    public object Close(string handleId)
    {
        PixHandle handle = Get(handleId);
        _handles.TryRemove(handle.Id, out _);
        var warnings = new List<string>();
        try { handle.Close(warnings); }
        catch (Exception ex) { warnings.Add(PixErrors.Describe(ex)); }
        Collect();
        _logger.LogInformation("Closed handle {Id}", handle.Id);
        return new { closed = handle.Id, kind = handle.Kind, warnings = warnings.Count == 0 ? null : warnings };
    }

    public object CloseAll()
    {
        var results = new List<object>();
        foreach (string id in _handles.Keys.ToArray())
        {
            try { results.Add(Close(id)); }
            catch (Exception ex) { results.Add(new { closed = id, error = PixErrors.Describe(ex) }); }
        }
        return results;
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    public void Dispose()
    {
        try
        {
            if (_handles.IsEmpty)
            {
                return;
            }
            _worker.Run(() => CloseAll()).Wait(TimeSpan.FromSeconds(30));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while closing PIX handles at shutdown.");
        }
    }
}
