using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Ipx2Mqtt.Ipx;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ipx2Mqtt.Tests;

/// <summary>
/// Minimal in-process imitation of the IPX800 V5 HTTP + WebSocket interface, as observed on a real IPX:
/// secure login (AES-ECB), /api/core/io|ana, PUT commands and the dashboard WebSocket.
/// </summary>
public sealed class MockIpxServer : IAsyncDisposable
{
    public const string SecureKey = "0123456789abcdef";
    public const string Token = "TOKEN123";

    private readonly WebApplication _app;
    private readonly List<WebSocket> _sockets = new();
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private int _restGets;
    private int _logins;
    private int _webSocketConnections;

    public ConcurrentDictionary<long, bool> Io { get; } = new(new Dictionary<long, bool>
    {
        [65536] = false, // relay cmd 1
        [65544] = true, // relay state 1
        [65552] = false, // digital input 1
    });

    public ConcurrentDictionary<long, double> Ana { get; } = new(new Dictionary<long, double> { [262153] = 9.19 });

    public ConcurrentQueue<(string Path, string Body)> Puts { get; } = new();

    public string Login { get; set; } = "api";

    public string Password { get; set; } = "pw";

    /// <summary>Makes every REST call answer 500.</summary>
    public volatile bool RestFailing;

    /// <summary>Refuses new WebSocket connections (503).</summary>
    public volatile bool WebSocketBlocked;

    /// <summary>Stops the periodic clock messages (simulates a dead connection).</summary>
    public volatile bool Silent;

    public int RestGets => Volatile.Read(ref _restGets);

    public int Logins => Volatile.Read(ref _logins);

    public int WebSocketConnections => Volatile.Read(ref _webSocketConnections);

    public int OpenSockets
    {
        get
        {
            lock (_gate)
            {
                return _sockets.Count(s => s.State == WebSocketState.Open);
            }
        }
    }

    public string Url { get; private set; } = string.Empty;

    private MockIpxServer(WebApplication app) => _app = app;

    public static async Task<MockIpxServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var server = new MockIpxServer(app);
        server.Map(app);
        await app.StartAsync();
        server.Url = app.Urls.First();
        _ = Task.Run(server.ClockLoopAsync);
        return server;
    }

    public async Task BroadcastAsync(string json)
    {
        WebSocket[] sockets;
        lock (_gate)
        {
            sockets = _sockets.Where(s => s.State == WebSocketState.Open).ToArray();
        }

        foreach (var socket in sockets)
        {
            try
            {
                await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch
            {
                // the socket may have been dropped meanwhile
            }
        }
    }

    /// <summary>Abruptly closes all WebSocket connections.</summary>
    public void DropConnections()
    {
        lock (_gate)
        {
            foreach (var socket in _sockets)
            {
                socket.Abort();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        DropConnections();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private async Task ClockLoopAsync()
    {
        var n = 0;
        while (!_stop.IsCancellationRequested)
        {
            if (!Silent)
            {
                await BroadcastAsync($"{{\"_id\":327687,\"value\":{++n}}}");
            }

            try
            {
                await Task.Delay(300, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private IResult Guard(HttpRequest request, Func<IResult> ok)
    {
        if (RestFailing)
        {
            return Results.StatusCode(500);
        }

        if (request.Query["AuthToken"] != Token)
        {
            return Results.Json(new Dictionary<string, object> { ["Status"] = "Invalid AuthToken" }, statusCode: 401);
        }

        return ok();
    }

    private void Map(WebApplication app)
    {
        app.UseWebSockets();

        app.MapPost("/api/auth/secure/{login}", () => RestFailing
            ? Results.StatusCode(500)
            : Results.Json(new Dictionary<string, object> { ["secureKey"] = SecureKey }));

        app.MapPost("/api/auth/login/{login}", async (HttpRequest request) =>
        {
            if (RestFailing)
            {
                return Results.StatusCode(500);
            }

            using var doc = await JsonDocument.ParseAsync(request.Body);
            var plain = IpxCrypto.DecryptAesEcb(doc.RootElement.GetProperty("secureContent").GetString()!, SecureKey);
            using var credentials = JsonDocument.Parse(plain);
            var ok = credentials.RootElement.GetProperty("login").GetString() == Login &&
                     credentials.RootElement.GetProperty("passwd").GetString() == Password;
            if (!ok)
            {
                return Results.Json(new Dictionary<string, object> { ["Status"] = "Error" }, statusCode: 401);
            }

            Interlocked.Increment(ref _logins);
            return Results.Json(new Dictionary<string, object> { ["Status"] = "Succes", ["AuthToken"] = Token });
        });

        app.MapGet("/api/core/io", (HttpRequest request) => Guard(request, () =>
        {
            Interlocked.Increment(ref _restGets);
            return Results.Json(Io.Select(kv => new Dictionary<string, object> { ["_id"] = kv.Key, ["name"] = $"io{kv.Key}", ["on"] = kv.Value }));
        }));

        app.MapGet("/api/core/ana", (HttpRequest request) => Guard(request, () =>
        {
            Interlocked.Increment(ref _restGets);
            return Results.Json(Ana.Select(kv => new Dictionary<string, object>
            {
                ["_id"] = kv.Key, ["name"] = $"ana{kv.Key}", ["unit"] = "RAW", ["nbdecimal"] = 2, ["value"] = kv.Value,
            }));
        }));

        app.MapGet("/api/system/ipx/info", (HttpRequest request) => Guard(request, () =>
            Results.Json(new Dictionary<string, object> { ["apiVersion"] = "1.6", ["objVersion"] = 1 })));

        app.MapPut("/api/core/io/{id:long}", async (long id, HttpRequest request) =>
        {
            var body = await new StreamReader(request.Body).ReadToEndAsync();
            return Guard(request, () =>
            {
                Puts.Enqueue((request.Path.Value!, body));
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("on", out var on))
                {
                    Io[id] = on.GetBoolean();
                }

                return Results.Json(new Dictionary<string, object> { ["_id"] = id, ["on"] = Io.GetValueOrDefault(id) });
            });
        });

        app.MapPut("/api/core/ana/{id:long}", async (long id, HttpRequest request) =>
        {
            var body = await new StreamReader(request.Body).ReadToEndAsync();
            return Guard(request, () =>
            {
                Puts.Enqueue((request.Path.Value!, body));
                return Results.Json(new Dictionary<string, object> { ["_id"] = id });
            });
        });

        app.Map("/websocket/dashboard/open", async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest || context.Request.Query["AuthToken"] != Token)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            if (WebSocketBlocked)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            Interlocked.Increment(ref _webSocketConnections);
            lock (_gate)
            {
                _sockets.Add(socket);
            }

            try
            {
                var buffer = new byte[1024];
                while (socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(buffer, _stop.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }
                }
            }
            catch
            {
                // dropped
            }
            finally
            {
                lock (_gate)
                {
                    _sockets.Remove(socket);
                }
            }
        });
    }
}
