namespace Ipx2Mqtt.Core.Mqtt;

public sealed record MqttMessage(string Topic, string Payload, bool Retain);

/// <summary>Minimal MQTT surface needed by the adapters (easy to fake in tests).</summary>
public interface IMqttPublisher
{
    bool IsConnected { get; }

    /// <summary>Raised after every (re)connection, once subscriptions are restored.</summary>
    event Func<Task>? Connected;

    /// <summary>Raised for every message received on a subscribed topic.</summary>
    event Func<MqttMessage, Task>? MessageReceived;

    /// <summary>Publishes a message. Does nothing (and logs) when disconnected.</summary>
    Task PublishAsync(string topic, string payload, bool retain = true, CancellationToken ct = default);

    /// <summary>Registers a subscription; it is restored automatically after reconnections.</summary>
    Task SubscribeAsync(string topicFilter, CancellationToken ct = default);
}
