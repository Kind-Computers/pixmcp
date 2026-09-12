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
    public ResultStore Results { get; } = new();
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
                                   $" Set the PIX_DIR environment variable to {PixDiscovery.Requirement}.");
        }
    }

    public Task<T> Run<T>(Func<T> work, CancellationToken cancellationToken = default, string? operation = null)
        => _worker.Run(work, cancellationToken, operation);

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

    /// <summary>Lookup that never throws; safe to call from any thread (the handle table is concurrent).</summary>
    public T? TryGet<T>(string? handleId) where T : PixHandle
        => !string.IsNullOrWhiteSpace(handleId) && _handles.TryGetValue(handleId.Trim(), out PixHandle? handle) ? handle as T : null;

    /// <summary>Closes and forgets a handle. Must run on the worker thread.</summary>
    public object Close(string handleId) => Close(handleId, collect: true);

    public object CloseAll()
    {
        var results = new List<object>();
        foreach (string id in _handles.Keys.ToArray())
        {
            try { results.Add(Close(id, collect: false)); }
            catch (Exception ex) { results.Add(new { closed = id, error = PixErrors.Describe(ex) }); }
        }
        Collect();
        return results;
    }

    private object Close(string handleId, bool collect)
    {
        PixHandle handle = Get(handleId);
        _handles.TryRemove(handle.Id, out _);
        Results.InvalidateOwner(handle.Id);
        PixMcp.Tools.PreviewTools.ForgetCapture(this, handle.Id);
        var warnings = new List<string>();
        try { handle.Close(warnings); }
        catch (Exception ex) { warnings.Add(PixErrors.Describe(ex)); }
        handle.PreparationJobs.Clear();
        if (collect) Collect();
        _logger.LogInformation("Closed handle {Id}", handle.Id);
        return new { closed = handle.Id, kind = handle.Kind, warnings = warnings.Count == 0 ? null : warnings };
    }

    /// <summary>
    /// The PIX objects are COM RCWs whose native side (analysis session, document) is only released
    /// when the wrapper is finalized; reopening the same capture needs that to have happened, so a
    /// close forces one collection with finalizers.
    /// </summary>
    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
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
        finally { Results.Dispose(); }
    }
}
