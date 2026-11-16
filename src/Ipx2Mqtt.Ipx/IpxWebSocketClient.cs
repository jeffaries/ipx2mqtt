using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ipx2Mqtt.Ipx;

/// <summary>
/// Client of the (undocumented) WebSocket used by the IPX web UI: login token, automatic reconnection with
/// capped exponential backoff, dead connection detection and full resynchronisation on every connection.
/// Commands go through REST because no WebSocket command format is known.
/// </summary>
public sealed class IpxWebSocketClient : IIpxClient
{
    private static readonly TimeSpan WarningInterval = TimeSpan.FromMinutes(1);

    private readonly IpxHttpApi _api;
    private readonly IpxOptions _options;
    private readonly IpxDiagnostics _diagnostics;
    private readonly ILogger<IpxWebSocketClient> _logger;
    private volatile bool _available;

    public IpxWebSocketClient(IpxHttpApi api, IOptions<IpxOptions> options, IpxDiagnostics diagnostics, ILogger<IpxWebSocketClient> logger)
    {
        _api = api;
        _options = options.Value;
        _diagnostics = diagnostics;
        _logger = logger;
    }

    public bool IsAvailable => _available;

    public event Action<IpxStateChange>? StateChanged;
    public event Action<bool>? AvailabilityChanged;

    public Task<IReadOnlyList<IpxObject>> GetFullStateAsync(CancellationToken ct) => _api.GetAllAsync(ct);

    public Task SetAsync(long id, object value, CancellationToken ct) => _api.SetAsync(id, value, ct);

    public Task<string?> GetSystemInfoAsync(CancellationToken ct) => _api.GetSystemInfoAsync(ct);

    public async Task RunAsync(CancellationToken ct)
    {
        var settings = _options.WebSocket;
        var min = TimeSpan.FromSeconds(Math.Max(1, settings.ReconnectMinSeconds));
        var max = TimeSpan.FromSeconds(Math.Max(settings.ReconnectMinSeconds, settings.ReconnectMaxSeconds));
        var delay = min;

        while (!ct.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            var connected = false;
            try
            {
                await ConnectAndReceiveAsync(ct, () => connected = true).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("IPX WebSocket connection failed or lost: {Reason}", ex.Message);
                _logger.LogDebug(ex, "IPX WebSocket failure details");
            }
            finally
            {
                SetAvailable(false);
            }

            if (connected && DateTime.UtcNow - started >= TimeSpan.FromSeconds(settings.StableAfterSeconds))
            {
                delay = min;
            }

            var wait = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * (0.8 + Random.Shared.NextDouble() * 0.4));
            _logger.LogInformation("Reconnecting to the IPX WebSocket in {Delay:0.#}s", wait.TotalSeconds);
            try
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, max.Ticks));
        }
    }

    private async Task ConnectAndReceiveAsync(CancellationToken ct, Action onConnected)
    {
        var settings = _options.WebSocket;
        var token = await _api.GetAuthTokenAsync(ct, forceRefresh: true).ConfigureAwait(false);

        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(Math.Max(1, settings.KeepAliveSeconds));
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(Math.Max(1, settings.KeepAliveSeconds) * 2);
        if (_options.AllowInvalidCertificate)
        {
            socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.RequestTimeoutSeconds)));
            await socket.ConnectAsync(_api.BuildWebSocketUri(token), connectCts.Token).ConfigureAwait(false);
        }

        _logger.LogInformation("IPX WebSocket connected");
        onConnected();

        // Full resynchronisation on every (re)connection. Messages that arrive while the snapshot is read are
        // buffered by the socket and applied afterwards; any later change simply overwrites the snapshot value.
        var snapshot = await _api.GetAllAsync(ct).ConfigureAwait(false);
        foreach (var obj in snapshot)
        {
            StateChanged?.Invoke(new IpxStateChange(obj.Id, obj.Value));
        }

        _logger.LogInformation("IPX full state resynchronised ({Count} objects)", snapshot.Count);
        SetAvailable(true);

        await ReceiveLoopAsync(socket, ct).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(1, _options.WebSocket.ReceiveTimeoutSeconds));
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        while (socket.State == WebSocketState.Open)
        {
            message.SetLength(0);
            ValueWebSocketReceiveResult result;
            do
            {
                receiveCts.CancelAfter(timeout);
                try
                {
                    result = await socket.ReceiveAsync(buffer.AsMemory(), receiveCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"No data received from the IPX for {timeout.TotalSeconds:0}s; connection considered dead.");
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation("IPX closed the WebSocket");
                    return;
                }

                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Text)
            {
                HandleText(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
            }
            else
            {
                _diagnostics.RecordRejected("binary frame", null, _logger);
            }

            _diagnostics.ReportIfDue(_logger, WarningInterval);
        }
    }

    private void HandleText(string text)
    {
        foreach (var change in IpxStateParser.ParseMessage(text, _diagnostics, _logger))
        {
            StateChanged?.Invoke(change);
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
