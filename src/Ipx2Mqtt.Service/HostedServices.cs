using Ipx2Mqtt.Core.Mqtt;
using Ipx2Mqtt.Ipx;

namespace Ipx2Mqtt.Service;

/// <summary>Keeps the MQTT connection alive.</summary>
public sealed class MqttWorker(MqttConnection mqtt) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => mqtt.RunAsync(stoppingToken);
}

/// <summary>Keeps the IPX connection(s) alive (WebSocket + REST fallback).</summary>
public sealed class IpxWorker(IIpxClient ipx) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => ipx.RunAsync(stoppingToken);
}
