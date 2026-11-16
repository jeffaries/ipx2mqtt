using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ipx2Mqtt.Ipx;

/// <summary>
/// Plan B: polls the REST API. It only polls while <see cref="Active"/> (activated by the strategy when the
/// WebSocket is unavailable).
/// </summary>
public sealed class IpxRestPollingClient : IIpxClient
{
    private readonly IpxHttpApi _api;
    private readonly IpxOptions _options;
    private readonly ILogger<IpxRestPollingClient> _logger;
    private volatile bool _active;
    private volatile bool _available;

    public IpxRestPollingClient(IpxHttpApi api, IOptions<IpxOptions> options, ILogger<IpxRestPollingClient> logger)
    {
        _api = api;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsAvailable => _available;

    /// <summary>Whether polling is currently wanted.</summary>
    public bool Active
    {
        get => _active;
        set => _active = value;
    }

    public event Action<IpxStateChange>? StateChanged;
    public event Action<bool>? AvailabilityChanged;

    public Task<IReadOnlyList<IpxObject>> GetFullStateAsync(CancellationToken ct) => _api.GetAllAsync(ct);

    public Task SetAsync(long id, object value, CancellationToken ct) => _api.SetAsync(id, value, ct);

    public Task<string?> GetSystemInfoAsync(CancellationToken ct) => _api.GetSystemInfoAsync(ct);

    public async Task RunAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.Polling.IntervalSeconds));
        var due = DateTime.MinValue;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!_active)
                {
                    due = DateTime.MinValue;
                    SetAvailable(false);
                }
                else if (DateTime.UtcNow >= due)
                {
                    due = DateTime.UtcNow + interval;
                    await PollOnceAsync(ct).ConfigureAwait(false);
                }

                await Task.Delay(250, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutting down
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        try
        {
            var snapshot = await _api.GetAllAsync(ct).ConfigureAwait(false);
            if (!_active)
            {
                return; // the WebSocket came back while the request was running
            }

            foreach (var obj in snapshot)
            {
                StateChanged?.Invoke(new IpxStateChange(obj.Id, obj.Value));
            }

            SetAvailable(true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("IPX REST polling failed: {Reason}", ex.Message);
            _logger.LogDebug(ex, "IPX REST polling failure details");
            SetAvailable(false);
        }
    }

    private void SetAvailable(bool available)
    {
        if (_available == available)
        {
            return;
        }

        _available = available;
        AvailabilityChanged?.Invoke(available);
    }
}
