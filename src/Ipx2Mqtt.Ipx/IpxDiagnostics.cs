using Microsoft.Extensions.Logging;

namespace Ipx2Mqtt.Ipx;

/// <summary>
/// Counts messages that could not be parsed. The IPX WebSocket is not an official API and may change with a
/// firmware update, so failures are logged at Debug level individually and summarised as a Warning.
/// </summary>
public sealed class IpxDiagnostics
{
    private readonly object _gate = new();
    private long _total;
    private long _sinceReport;
    private DateTime _lastReport = DateTime.UtcNow;
    private string? _lastSample;

    /// <summary>Total number of ignored messages since startup.</summary>
    public long TotalRejected
    {
        get
        {
            lock (_gate)
            {
                return _total;
            }
        }
    }

    public void RecordRejected(string reason, string? sample, ILogger? logger = null)
    {
        var truncated = sample is { Length: > 200 } ? sample[..200] + "..." : sample;
        lock (_gate)
        {
            _total++;
            _sinceReport++;
            _lastSample = truncated;
        }

        logger?.LogDebug("Ignored IPX message ({Reason}): {Sample}", reason, truncated);
    }

    /// <summary>Emits one aggregated Warning if failures happened and <paramref name="interval"/> elapsed.</summary>
    public void ReportIfDue(ILogger logger, TimeSpan interval)
    {
        long count;
        long total;
        string? sample;
        lock (_gate)
        {
            if (_sinceReport == 0 || DateTime.UtcNow - _lastReport < interval)
            {
                return;
            }

            count = _sinceReport;
            total = _total;
            sample = _lastSample;
            _sinceReport = 0;
            _lastReport = DateTime.UtcNow;
        }

        logger.LogWarning(
            "{Count} IPX message(s) could not be parsed recently (total {Total}). Last sample: {Sample}. " +
            "The IPX firmware may have changed its WebSocket protocol.",
            count, total, sample);
    }
}
