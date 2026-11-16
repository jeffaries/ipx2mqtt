using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ipx2Mqtt.Ipx;

/// <summary>
/// Chooses the source: the WebSocket when it works, REST polling otherwise (enabled as soon as the WebSocket
/// is down, disabled as soon as it is back). The IPX is reported unavailable only when both sources are down.
/// </summary>
public sealed class IpxClientStrategy : IIpxClient
{
    private readonly IpxWebSocketClient _webSocket;
    private readonly IpxRestPollingClient _polling;
    private readonly bool _webSocketEnabled;
    private readonly TimeSpan _offlineGrace;
    private bool _offlinePending;
    private readonly ILogger<IpxClientStrategy> _logger;
    private readonly object _gate = new();
    private bool _available;

    public IpxClientStrategy(
        IpxWebSocketClient webSocket,
        IpxRestPollingClient polling,
        IOptions<IpxOptions> options,
        ILogger<IpxClientStrategy> logger)
    {
        _webSocket = webSocket;
        _polling = polling;
        _logger = logger;
        _webSocketEnabled = options.Value.WebSocket.Enabled;
        _offlineGrace = TimeSpan.FromSeconds(Math.Max(0, options.Value.OfflineGraceSeconds));

        _polling.Active = true; // until the WebSocket is up
        _webSocket.StateChanged += change => StateChanged?.Invoke(change);
        _polling.StateChanged += change => StateChanged?.Invoke(change);
        _webSocket.AvailabilityChanged += OnWebSocketAvailabilityChanged;
        _polling.AvailabilityChanged += _ => Recompute();
    }

    public bool IsAvailable
    {
        get
        {
            lock (_gate)
            {
                return _available;
            }
        }
    }

    public event Action<IpxStateChange>? StateChanged;
    public event Action<bool>? AvailabilityChanged;

    public Task RunAsync(CancellationToken ct) =>
        Task.WhenAll(_webSocketEnabled ? _webSocket.RunAsync(ct) : Task.CompletedTask, _polling.RunAsync(ct));

    public Task<IReadOnlyList<IpxObject>> GetFullStateAsync(CancellationToken ct) => _webSocket.GetFullStateAsync(ct);

    public Task SetAsync(long id, object value, CancellationToken ct) => _webSocket.SetAsync(id, value, ct);

    public Task<string?> GetSystemInfoAsync(CancellationToken ct) => _webSocket.GetSystemInfoAsync(ct);

    private void OnWebSocketAvailabilityChanged(bool up)
    {
        if (up)
        {
            _logger.LogInformation("WebSocket is up: REST polling disabled");
        }
        else if (_webSocketEnabled)
        {
            _logger.LogWarning("WebSocket is down: REST polling enabled");
        }

        _polling.Active = !up;
        Recompute();
    }

    private void Recompute()
    {
        bool changed;
        bool value;
        lock (_gate)
        {
            value = (_webSocketEnabled && _webSocket.IsAvailable) || _polling.IsAvailable;

            if (!value && _available && _offlineGrace > TimeSpan.Zero)
            {
                // Both sources look down: wait a little before declaring the IPX unavailable.
                if (!_offlinePending)
                {
                    _offlinePending = true;
                    _ = ConfirmOfflineAsync();
                }

                return;
            }

            changed = value != _available;
            _available = value;
        }

        if (changed)
        {
            _logger.LogInformation("IPX is now {State}", value ? "available" : "unavailable");
            AvailabilityChanged?.Invoke(value);
        }
    }

    private async Task ConfirmOfflineAsync()
    {
        await Task.Delay(_offlineGrace).ConfigureAwait(false);
        lock (_gate)
        {
            _offlinePending = false;
        }

        // Re-evaluate: either a source came back (stay available) or both are still down (go offline).
        var stillDown = false;
        lock (_gate)
        {
            stillDown = !((_webSocketEnabled && _webSocket.IsAvailable) || _polling.IsAvailable);
            if (stillDown && _available)
            {
                _available = false;
            }
            else
            {
                stillDown = false;
            }
        }

        if (stillDown)
        {
            _logger.LogWarning("IPX is now unavailable: WebSocket and REST polling are both down");
            AvailabilityChanged?.Invoke(false);
        }
    }
}
