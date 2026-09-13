using PixMcp.Pix.Sql;

namespace PixMcp.Pix.Handles;

public sealed partial class GpuCaptureHandle
{
    private readonly object _sqlGate = new();
    private GpuSqlStore? _sqlStore;

    /// <summary>Increments whenever analysis stops; GPU SQL rows of replay families from an older generation are stale.</summary>
    internal int AnalysisGeneration { get; private set; }

    internal GpuSqlStore? SqlStore
    {
        get { lock (_sqlGate) return _sqlStore; }
    }

    /// <summary>The handle's GPU SQL store, created with the full schema on first use in the session's private result directory.</summary>
    internal GpuSqlStore EnsureSqlStore(ResultStore results)
    {
        lock (_sqlGate)
            return _sqlStore ??= new GpuSqlStore(Id, results.ScratchPath($"gpusql-{Id}.sqlite"), ServerOptions.Current.GpuSqlMaxBytes);
    }

    private void CloseSqlStore(List<string> warnings)
    {
        GpuSqlStore? store;
        lock (_sqlGate)
        {
            store = _sqlStore;
            _sqlStore = null;
        }
        try { store?.Dispose(); }
        catch (Exception ex) { warnings.Add("GPU SQL store: " + PixErrors.Describe(ex)); }
    }
}
