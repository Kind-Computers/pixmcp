using Microsoft.Extensions.Logging;
using Microsoft.PIX;

namespace PixMcp.Pix;

/// <summary>Receives PIX engine log messages (IPixFactory.SetLogger) and keeps the last few hundred.</summary>
public sealed class PixLog : IPixLogging
{
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly List<object> _entries = new();

    public PixLog(ILogger logger) => _logger = logger;

    public void ReportLog(PIX_LOGGING_MESSAGE log)
    {
        string message = Interop.W(log.Message);
        string source = Interop.W(log.SourceName);
        var entry = new
        {
            time = DateTimeOffset.UtcNow,
            severity = Json.EnumName(log.Severity),
            source = Json.EnumName(log.SourceType),
            sourceName = source,
            message,
        };
        lock (_lock)
        {
            _entries.Add(entry);
            if (_entries.Count > 500)
            {
                _entries.RemoveAt(0);
            }
        }

        LogLevel level = log.Severity switch
        {
            PIX_SEVERITY_LEVEL.PIX_SEVERITY_LEVEL_WARNING => LogLevel.Warning,
            PIX_SEVERITY_LEVEL.PIX_SEVERITY_LEVEL_ERROR => LogLevel.Error,
            PIX_SEVERITY_LEVEL.PIX_SEVERITY_LEVEL_FATAL => LogLevel.Critical,
            _ => LogLevel.Debug,
        };
        _logger.Log(level, "PIX [{Source}] {Message}", source, message);
    }

    public IReadOnlyList<object> Recent(int count)
    {
        lock (_lock)
        {
            return _entries.TakeLast(count).ToArray();
        }
    }
}
