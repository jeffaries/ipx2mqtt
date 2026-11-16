namespace Ipx2Mqtt.Core.State;

/// <summary>Remembers the last published payload per topic so that only changes are published.</summary>
public sealed class StateCache
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>Stores <paramref name="payload"/>; returns true when it differs from the previous one.</summary>
    public bool TryUpdate(string topic, string payload)
    {
        lock (_gate)
        {
            if (_values.TryGetValue(topic, out var previous) && previous == payload)
            {
                return false;
            }

            _values[topic] = payload;
            return true;
        }
    }

    public IReadOnlyDictionary<string, string> Snapshot()
    {
        lock (_gate)
        {
            return new Dictionary<string, string>(_values, StringComparer.Ordinal);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _values.Clear();
        }
    }
}
