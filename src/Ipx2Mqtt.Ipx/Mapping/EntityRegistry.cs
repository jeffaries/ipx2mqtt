using Ipx2Mqtt.Core.Discovery;

namespace Ipx2Mqtt.Ipx.Mapping;

/// <summary>Lookup tables over the mapped entities (state by IPX id, command by MQTT topic).</summary>
public sealed class EntityRegistry
{
    public EntityRegistry(IReadOnlyList<MappedEntity> entities)
    {
        Entities = entities;
        StatesById = entities
            .SelectMany(e => e.States)
            .GroupBy(s => s.IpxId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<StateBinding>)g.ToList());
        Commands = entities
            .SelectMany(e => e.Commands)
            .ToDictionary(c => c.Topic, StringComparer.Ordinal);
    }

    public IReadOnlyList<MappedEntity> Entities { get; }

    public IReadOnlyDictionary<long, IReadOnlyList<StateBinding>> StatesById { get; }

    public IReadOnlyDictionary<string, CommandBinding> Commands { get; }

    public IEnumerable<ComponentDescriptor> Components => Entities.Select(e => e.Component);
}
