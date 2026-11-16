using System.Collections;
using Ipx2Mqtt.Core.Mqtt;

namespace Ipx2Mqtt.Tests;

/// <summary>Thread-safe list: the code under test publishes from background threads.</summary>
public sealed class SafeList<T> : IEnumerable<T>
{
    private readonly List<T> _items = new();
    private readonly object _gate = new();

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    public void Add(T item)
    {
        lock (_gate)
        {
            _items.Add(item);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
        }
    }

    public T[] ToArray()
    {
        lock (_gate)
        {
            return _items.ToArray();
        }
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)ToArray()).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Records publications and lets tests inject incoming messages.</summary>
public sealed class FakeMqttPublisher : IMqttPublisher
{
    public bool IsConnected { get; set; } = true;

    public SafeList<MqttMessage> Published { get; } = new();

    public SafeList<string> Subscriptions { get; } = new();

    public event Func<Task>? Connected;

    public event Func<MqttMessage, Task>? MessageReceived;

    public Task PublishAsync(string topic, string payload, bool retain = true, CancellationToken ct = default)
    {
        Published.Add(new MqttMessage(topic, payload, retain));
        return Task.CompletedTask;
    }

    public Task SubscribeAsync(string topicFilter, CancellationToken ct = default)
    {
        Subscriptions.Add(topicFilter);
        return Task.CompletedTask;
    }

    public Task ReceiveAsync(string topic, string payload) =>
        MessageReceived?.Invoke(new MqttMessage(topic, payload, false)) ?? Task.CompletedTask;

    public Task RaiseConnectedAsync() => Connected?.Invoke() ?? Task.CompletedTask;
}
