using Ipx2Mqtt.Core.Discovery;
using Ipx2Mqtt.Core.Mqtt;
using Microsoft.Extensions.Options;

namespace Ipx2Mqtt.Ipx.Mapping;

/// <summary>Validates the configured entities and maps them with the registered platform mappers.</summary>
public sealed class EntityMapper
{
    private readonly Dictionary<string, IComponentMapper> _mappers;
    private readonly EntityContext _context;

    public EntityMapper(IEnumerable<IComponentMapper> mappers, TopicBuilder topics, IOptions<DeviceOptions> device)
    {
        _mappers = mappers.ToDictionary(m => m.Platform, StringComparer.OrdinalIgnoreCase);
        _context = new EntityContext(topics, device.Value.NodeId);
    }

    public MappedEntity Map(EntityDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            throw new EntityConfigurationException("Every entity needs a Name.");
        }

        try
        {
            DiscoveryBuilder.ValidateId(definition.Type, nameof(definition.Type));
        }
        catch (ArgumentException)
        {
            throw new EntityConfigurationException(
                $"Entity '{definition.Name}': Type '{definition.Type}' must contain only letters, digits, '_' or '-'.");
        }

        if (!_mappers.TryGetValue(definition.Component ?? string.Empty, out var mapper))
        {
            throw new EntityConfigurationException(
                $"Entity '{definition.Name}': unsupported Component '{definition.Component}'. " +
                $"Supported: {string.Join(", ", _mappers.Keys.Order())}.");
        }

        return mapper.Map(definition, _context);
    }

    /// <summary>Maps all entities and rejects duplicated keys, unique ids or topics.</summary>
    public IReadOnlyList<MappedEntity> MapAll(IEnumerable<EntityDefinition> definitions)
    {
        var result = new List<MappedEntity>();
        var keys = new HashSet<string>();
        var topics = new HashSet<string>();

        foreach (var definition in definitions)
        {
            var mapped = Map(definition);
            if (!keys.Add(mapped.Component.Key))
            {
                throw new EntityConfigurationException(
                    $"Entity '{definition.Name}' duplicates '{mapped.Component.Key}' (same Type and Index).");
            }

            foreach (var topic in mapped.States.Select(s => s.Topic).Concat(mapped.Commands.Select(c => c.Topic)))
            {
                if (!topics.Add(topic))
                {
                    throw new EntityConfigurationException($"Entity '{definition.Name}': topic '{topic}' is already used.");
                }
            }

            result.Add(mapped);
        }

        return result;
    }
}
