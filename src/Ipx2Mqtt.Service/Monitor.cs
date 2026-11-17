using System.Globalization;
using Ipx2Mqtt.Core.Mqtt;
using Microsoft.AspNetCore.SignalR;

namespace Ipx2Mqtt.Service;

/// <summary>Kinds of events shown in the web interface "monitor" tab.</summary>
public static class MonitorKinds
{
    /// <summary>The IPX reported a new value for an exposed object (IPX → bridge).</summary>
    public const string IpxState = "ipx.state";

    /// <summary>The bridge sent a command to the IPX (bridge → IPX).</summary>
    public const string IpxCommand = "ipx.command";

    /// <summary>The bridge published an MQTT message (bridge → broker).</summary>
    public const string MqttPublish = "mqtt.publish";

    /// <summary>The bridge received an MQTT message (broker → bridge).</summary>
    public const string MqttReceive = "mqtt.receive";

    /// <summary>Availability, connections...</summary>
    public const string System = "system";
}

public sealed record MonitorEvent(
    long Seq,
    DateTimeOffset Time,
    string Kind,
    string? Topic,
    long? IpxId,
    string? Payload,
    bool? Retain,
    bool? Ok,
    string? Detail);

/// <summary>Keeps the latest events and pushes every new one to the connected web interfaces (SignalR).</summary>
public sealed class MonitorStream(IHubContext<MonitorHub> hub)
{
    public const int Capacity = 500;
    private const int MaxPayloadLength = 500;

    private readonly Queue<MonitorEvent> _recent = new();
    private readonly object _lock = new();
    private long _seq;

    public void Publish(
        string kind, string? topic = null, long? ipxId = null, object? payload = null,
        bool? retain = null, bool? ok = null, string? detail = null)
    {
        var text = payload switch
        {
            null => null,
            string s => s,
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => payload.ToString(),
        };
        if (text is { Length: > MaxPayloadLength })
        {
            text = text[..MaxPayloadLength] + "…";
        }

        MonitorEvent e;
        lock (_lock)
        {
            e = new MonitorEvent(++_seq, DateTimeOffset.UtcNow, kind, topic, ipxId, text, retain, ok, detail);
            _recent.Enqueue(e);
            while (_recent.Count > Capacity)
            {
                _recent.Dequeue();
            }
        }

        // Fire and forget: the monitor must never slow down or break the bridge.
        _ = hub.Clients.All.SendAsync("event", e).ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    public IReadOnlyList<MonitorEvent> Snapshot()
    {
        lock (_lock)
        {
            return _recent.ToArray();
        }
    }
}

public sealed class MonitorHub(MonitorStream stream) : Hub
{
    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("backlog", stream.Snapshot());
        await base.OnConnectedAsync();
    }
}

/// <summary>Records every MQTT message sent or received, then delegates to the real connection.</summary>
public sealed class MonitoredMqttPublisher : IMqttPublisher
{
    private readonly IMqttPublisher _inner;
    private readonly MonitorStream _monitor;

    public MonitoredMqttPublisher(IMqttPublisher inner, MonitorStream monitor)
    {
        _inner = inner;
        _monitor = monitor;
        _inner.MessageReceived += message =>
        {
            _monitor.Publish(MonitorKinds.MqttReceive, topic: message.Topic, payload: message.Payload, retain: message.Retain);
            return Task.CompletedTask;
        };
    }

    public bool IsConnected => _inner.IsConnected;

    public event Func<Task>? Connected
    {
        add => _inner.Connected += value;
        remove => _inner.Connected -= value;
    }

    public event Func<MqttMessage, Task>? MessageReceived
    {
        add => _inner.MessageReceived += value;
        remove => _inner.MessageReceived -= value;
    }

    public Task PublishAsync(string topic, string payload, bool retain = true, CancellationToken ct = default)
    {
        var connected = _inner.IsConnected;
        _monitor.Publish(MonitorKinds.MqttPublish, topic: topic, payload: payload, retain: retain,
            ok: connected ? null : false, detail: connected ? null : "not connected");
        return _inner.PublishAsync(topic, payload, retain, ct);
    }

    public Task SubscribeAsync(string topicFilter, CancellationToken ct = default) => _inner.SubscribeAsync(topicFilter, ct);
}
