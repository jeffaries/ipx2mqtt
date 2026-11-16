using Ipx2Mqtt.Core.Mqtt;

namespace Ipx2Mqtt.Core.State;

/// <summary>Publishes retained state topics, skipping unchanged values.</summary>
public sealed class StatePublisher
{
    private readonly IMqttPublisher _mqtt;
    private readonly StateCache _cache = new();

    public StatePublisher(IMqttPublisher mqtt) => _mqtt = mqtt;

    /// <returns>True when the value changed and was published.</returns>
    public async Task<bool> PublishIfChangedAsync(string topic, string payload, CancellationToken ct = default)
    {
        if (!_cache.TryUpdate(topic, payload))
        {
            return false;
        }

        await _mqtt.PublishAsync(topic, payload, retain: true, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Re-sends every known state (after an MQTT reconnection or a Home Assistant restart).</summary>
    public async Task RepublishAllAsync(CancellationToken ct = default)
    {
        foreach (var (topic, payload) in _cache.Snapshot())
        {
            await _mqtt.PublishAsync(topic, payload, retain: true, ct).ConfigureAwait(false);
        }
    }
}
