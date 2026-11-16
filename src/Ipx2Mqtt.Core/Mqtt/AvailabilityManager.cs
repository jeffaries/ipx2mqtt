namespace Ipx2Mqtt.Core.Mqtt;

/// <summary>Publishes the retained online/offline availability of the adapter.</summary>
public sealed class AvailabilityManager
{
    public const string OnlinePayload = "online";
    public const string OfflinePayload = "offline";

    private readonly IMqttPublisher _mqtt;

    public AvailabilityManager(IMqttPublisher mqtt, TopicBuilder topics)
    {
        _mqtt = mqtt;
        Topic = topics.Availability;
    }

    public string Topic { get; }

    /// <summary>Last requested availability; false until the source reports otherwise.</summary>
    public bool Online { get; private set; }

    public Task SetAsync(bool online, CancellationToken ct = default)
    {
        Online = online;
        return PublishCurrentAsync(ct);
    }

    /// <summary>Re-sends the current availability (used after an MQTT reconnection).</summary>
    public Task PublishCurrentAsync(CancellationToken ct = default) =>
        _mqtt.PublishAsync(Topic, Online ? OnlinePayload : OfflinePayload, retain: true, ct);
}
