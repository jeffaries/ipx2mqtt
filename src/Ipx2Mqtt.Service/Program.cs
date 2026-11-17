using System.Text.Json;
using Ipx2Mqtt.Core;
using Ipx2Mqtt.Ipx;
using Ipx2Mqtt.Service;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

var configDirectory = ExternalConfig.AddTo(builder.Configuration, args);

// The HTTP server serves the web interface (Web.Port) and the optional IPX "Push" endpoint (Push.Port).
// When both are disabled it listens on a random loopback port.
var pushOptions = builder.Configuration.GetSection(PushOptions.SectionName).Get<PushOptions>() ?? new PushOptions();
var webOptions = builder.Configuration.GetSection(WebOptions.SectionName).Get<WebOptions>() ?? new WebOptions();
var ports = new List<int>();
if (webOptions.Enabled) ports.Add(webOptions.Port);
if (pushOptions.Enabled && !ports.Contains(pushOptions.Port)) ports.Add(pushOptions.Port);
builder.WebHost.UseUrls(ports.Count > 0 ? ports.Select(p => $"http://0.0.0.0:{p}").ToArray() : new[] { "http://127.0.0.1:0" });

builder.Services.AddSingleton(new ConfigEditor(configDirectory ?? ExternalConfig.DefaultWritableDirectory()));

builder.Services.AddIpx2MqttCore(builder.Configuration);
builder.Services.AddIpx(builder.Configuration);

// Start order: MQTT, bridge, IPX. Stop order is the reverse, so "offline" is published before MQTT disconnects.
builder.Services.AddHostedService<MqttWorker>();
builder.Services.AddHostedService<BridgeWorker>();
builder.Services.AddHostedService<IpxWorker>();

var app = builder.Build();

if (webOptions.Enabled)
{
    app.UseWebInterface(webOptions, pushOptions);
}

if (pushOptions.Enabled)
{
    app.MapMethods("/push/{id:long}/{value}", new[] { "GET", "POST", "PUT" },
        (long id, string value, HttpRequest request, IpxPushReceiver receiver, IOptions<PushOptions> options) =>
            !Authorized(request, options.Value)
                ? Results.Unauthorized()
                : receiver.TrySubmit(id, value, out var error) ? Results.Ok() : Results.BadRequest(error));

    app.MapPost("/push", async (HttpRequest request, IpxPushReceiver receiver, IpxDiagnostics diagnostics, IOptions<PushOptions> options) =>
    {
        if (!Authorized(request, options.Value))
        {
            return Results.Unauthorized();
        }

        using var reader = new StreamReader(request.Body);
        var changes = IpxStateParser.ParseMessage(await reader.ReadToEndAsync(), diagnostics);
        if (changes.Count == 0)
        {
            return Results.BadRequest("Expected {\"_id\":<id>,\"on\":<bool>} or {\"_id\":<id>,\"value\":<number|text>}.");
        }

        foreach (var change in changes)
        {
            receiver.Submit(change);
        }

        return Results.Ok();
    });
}

app.Run();

static bool Authorized(HttpRequest request, PushOptions options) =>
    string.IsNullOrEmpty(options.Token) ||
    request.Headers["X-Push-Token"] == options.Token ||
    request.Query["token"] == options.Token;
