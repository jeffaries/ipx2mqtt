using Ipx2Mqtt.Core.Discovery;

namespace Ipx2Mqtt.Ipx.Mapping;

/// <summary>IPX object id to MQTT state topic, with the conversion of the IPX value to a payload.</summary>
public sealed record StateBinding(long IpxId, string Topic, Func<object, string> ToPayload);

/// <summary>MQTT command topic to IPX command (null when the payload is not understood).</summary>
public sealed record CommandBinding(string Topic, Func<string, IpxCommand?> Parse);

public sealed record MappedEntity(
    EntityDefinition Definition,
    ComponentDescriptor Component,
    IReadOnlyList<StateBinding> States,
    IReadOnlyList<CommandBinding> Commands);

public sealed class EntityConfigurationException : Exception
{
    public EntityConfigurationException(string message)
        : base(message)
    {
    }
}
