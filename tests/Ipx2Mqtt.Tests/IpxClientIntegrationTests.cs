using Ipx2Mqtt.Ipx;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ipx2Mqtt.Tests;

/// <summary>Exercises the real IPX clients (login, WebSocket, polling, fallback) against <see cref="MockIpxServer"/>.</summary>
public class IpxClientIntegrationTests : IAsyncLifetime
{
    private readonly CancellationTokenSource _cts = new();
    private readonly SafeList<IpxStateChange> _changes = new();
    private readonly SafeList<bool> _availability = new();
    private MockIpxServer _server = null!;
    private IpxHttpApi _api = null!;
    private IpxClientStrategy _strategy = null!;
    private IpxDiagnostics _diagnostics = null!;
    private Task _run = Task.CompletedTask;

    public async Task InitializeAsync()
    {
        _server = await MockIpxServer.StartAsync();
        var options = Options.Create(new IpxOptions
        {
            BaseUrl = _server.Url,
            Login = "api",
            Password = "pw",
            RequestTimeoutSeconds = 2,
            OfflineGraceSeconds = 1,
            WebSocket = new WebSocketSettings
            {
                ReceiveTimeoutSeconds = 2,
                KeepAliveSeconds = 1,
                ReconnectMinSeconds = 1,
                ReconnectMaxSeconds = 1,
                StableAfterSeconds = 1,
            },
            Polling = new PollingSettings { IntervalSeconds = 1 },
        });
        _diagnostics = new IpxDiagnostics();
        _api = new IpxHttpApi(new HttpClient(), options, _diagnostics, NullLogger<IpxHttpApi>.Instance);
        var webSocket = new IpxWebSocketClient(_api, options, _diagnostics, NullLogger<IpxWebSocketClient>.Instance);
        var polling = new IpxRestPollingClient(_api, options, NullLogger<IpxRestPollingClient>.Instance);
        _strategy = new IpxClientStrategy(webSocket, polling, options, NullLogger<IpxClientStrategy>.Instance);
        _strategy.StateChanged += _changes.Add;
        _strategy.AvailabilityChanged += _availability.Add;
    }

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            await _run;
        }
        catch (OperationCanceledException)
        {
            // expected
        }

        await _server.DisposeAsync();
    }

    private void Start() => _run = Task.Run(() => _strategy.RunAsync(_cts.Token));

    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        for (var waited = 0; waited < timeoutMs && !condition(); waited += 25)
        {
            await Task.Delay(25);
        }

        Assert.True(condition(), $"Timed out waiting for: {what}");
    }

    [Fact]
    public async Task SecureLogin_ObtainsATokenFromTheEncryptedCredentials()
    {
        var token = await _api.GetAuthTokenAsync(CancellationToken.None);

        Assert.Equal(MockIpxServer.Token, token);
        Assert.Equal(1, _server.Logins);
    }

    [Fact]
    public async Task SecureLogin_FailsWithAWrongPassword()
    {
        _server.Password = "other";

        await Assert.ThrowsAsync<HttpRequestException>(() => _api.GetAuthTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FullState_ReadsIoAndAnaObjects()
    {
        var state = await _api.GetAllAsync(CancellationToken.None);

        Assert.Contains(state, o => o.Id == 65544 && o.Kind == IpxObjectKind.Io && Equals(o.Value, true));
        Assert.Contains(state, o => o.Id == 262153 && o.Kind == IpxObjectKind.Ana && Equals(o.Value, 9.19) && o.Unit == "RAW");
    }

    [Fact]
    public async Task SystemInfo_ReportsApiVersion()
    {
        Assert.Equal("apiVersion=1.6, objVersion=1", await _api.GetSystemInfoAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Commands_UseTheDocumentedBodies()
    {
        await _api.SetAsync(65536, true, CancellationToken.None);
        await _api.SetAsync(262153, 40.0, CancellationToken.None);

        var puts = _server.Puts.ToArray();
        Assert.Contains(puts, p => p.Path == "/api/core/io/65536" && p.Body.Replace(" ", "") == "{\"on\":true}");
        Assert.Contains(puts, p => p.Path == "/api/core/ana/262153" && p.Body.Replace(" ", "") == "{\"value\":40}");
    }

    [Fact]
    public async Task Commands_RejectValuesOfTheWrongKind()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _api.SetAsync(65536, 12.0, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => _api.SetAsync(262153, true, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => _api.SetAsync(12, true, CancellationToken.None));
    }

    [Fact]
    public async Task Connect_ResynchronisesFullStateThenStreamsWebSocketChanges()
    {
        Start();

        await WaitUntil(() => _strategy.IsAvailable, "IPX available");
        Assert.Contains(_changes, c => c.Id == 65544 && Equals(c.Value, true)); // from the snapshot
        Assert.Contains(_changes, c => c.Id == 262153 && Equals(c.Value, 9.19));

        await _server.BroadcastAsync("{\"_id\":65544,\"on\":false}");
        await WaitUntil(() => _changes.Any(c => c.Id == 65544 && Equals(c.Value, false)), "WebSocket change");
    }

    [Fact]
    public async Task UnparseableWebSocketMessages_AreCountedAndNeverBreakTheStream()
    {
        Start();
        await WaitUntil(() => _strategy.IsAvailable, "IPX available");

        await _server.BroadcastAsync("this is not json");
        await _server.BroadcastAsync("{\"surprise\":true}");
        await _server.BroadcastAsync("{\"_id\":65552,\"on\":true}");

        await WaitUntil(() => _changes.Any(c => c.Id == 65552 && Equals(c.Value, true)), "valid message after the bad ones");
        Assert.Equal(2, _diagnostics.TotalRejected);
        Assert.True(_strategy.IsAvailable);
    }

    [Fact]
    public async Task WebSocketDown_FallsBackToPollingWithoutGoingOffline_ThenStopsPollingWhenItIsBack()
    {
        Start();
        await WaitUntil(() => _strategy.IsAvailable && _server.OpenSockets == 1, "WebSocket connected");
        var idle = _server.RestGets;
        await Task.Delay(2500);
        Assert.Equal(idle, _server.RestGets); // no polling while the WebSocket works

        _server.WebSocketBlocked = true;
        _server.DropConnections();
        _server.Io[65544] = false; // changes while only polling can see them

        await WaitUntil(() => _changes.Any(c => c.Id == 65544 && Equals(c.Value, false)), "state seen through polling");
        await Task.Delay(1500);
        Assert.True(_strategy.IsAvailable);
        Assert.DoesNotContain(false, _availability); // never reported offline during the hand-over

        _server.WebSocketBlocked = false;
        await WaitUntil(() => _server.OpenSockets == 1, "WebSocket reconnected");
        await Task.Delay(1000); // let the resynchronisation finish
        var afterReconnect = _server.RestGets;
        await Task.Delay(2500);
        Assert.Equal(afterReconnect, _server.RestGets); // polling disabled again
    }

    [Fact]
    public async Task BothSourcesDown_ReportsUnavailable_AndRecoversAutomatically()
    {
        Start();
        await WaitUntil(() => _strategy.IsAvailable, "IPX available");

        _server.WebSocketBlocked = true;
        _server.RestFailing = true;
        _server.DropConnections();
        await WaitUntil(() => !_strategy.IsAvailable, "IPX unavailable");
        Assert.Contains(false, _availability);

        _server.WebSocketBlocked = false;
        _server.RestFailing = false;
        await WaitUntil(() => _strategy.IsAvailable, "IPX available again");
    }

    [Fact]
    public async Task SilentConnection_IsDetectedAsDeadAndReconnected()
    {
        Start();
        await WaitUntil(() => _strategy.IsAvailable && _server.WebSocketConnections == 1, "first connection");

        _server.Silent = true; // the server keeps the socket open but stops talking

        await WaitUntil(() => _server.WebSocketConnections >= 2, "reconnection after the receive timeout", 15_000);
    }

    [Fact]
    public async Task Reconnection_ResynchronisesStatesChangedWhileDisconnected()
    {
        Start();
        await WaitUntil(() => _strategy.IsAvailable && _server.OpenSockets == 1, "connected");

        _server.WebSocketBlocked = true;
        _server.DropConnections();
        _server.Io[65552] = true;
        _server.WebSocketBlocked = false;

        await WaitUntil(() => _changes.Any(c => c.Id == 65552 && Equals(c.Value, true)), "state changed while disconnected");
    }
}
