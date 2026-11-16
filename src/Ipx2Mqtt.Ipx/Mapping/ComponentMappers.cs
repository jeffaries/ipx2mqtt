using System.Globalization;
using Ipx2Mqtt.Core.Discovery;
using Ipx2Mqtt.Core.Mqtt;

namespace Ipx2Mqtt.Ipx.Mapping;

/// <summary>Naming context handed to the mappers.</summary>
public sealed record EntityContext(TopicBuilder Topics, string NodeId)
{
    public string Key(EntityDefinition d) => $"{d.Type}_{ResolveIndex(d)}";

    public string UniqueId(EntityDefinition d) => $"{NodeId}_{Key(d)}";

    public string Topic(EntityDefinition d, params string[] suffix) =>
        Topics.Join(new object[] { d.Type, ResolveIndex(d) }.Concat(suffix).ToArray());

    private static long ResolveIndex(EntityDefinition d) =>
        d.Index ?? d.IdForIndex ?? throw new EntityConfigurationException($"Entity '{d.Name}': set Index, StateId or CommandId.");
}

/// <summary>
/// Turns an <see cref="EntityDefinition"/> into a discovery component plus its topic bindings.
/// Implement this interface and register it to support a new Home Assistant platform
/// (for example a cover for the X-4VR extension).
/// </summary>
public interface IComponentMapper
{
    string Platform { get; }

    MappedEntity Map(EntityDefinition definition, EntityContext context);
}

internal static class MapperHelpers
{
    public static long Require(long? id, EntityDefinition d, string field) =>
        id ?? throw new EntityConfigurationException($"Entity '{d.Name}' ({d.Component}) requires {field}.");

    public static CommandBinding OnOffCommand(string topic, long ipxId) =>
        new(topic, payload => IpxPayloads.TryParseOnOff(payload, out var on) ? new IpxCommand(ipxId, on) : null);
}

/// <summary>Relay-like outputs (IPX relays, X-8R...): state id + command id.</summary>
public sealed class SwitchMapper : IComponentMapper
{
    public string Platform => "switch";

    public MappedEntity Map(EntityDefinition d, EntityContext ctx)
    {
        var commandId = MapperHelpers.Require(d.CommandId, d, nameof(d.CommandId));
        var stateId = d.StateId ?? commandId;
        var stateTopic = ctx.Topic(d, "state");
        var commandTopic = ctx.Topic(d, "set");

        return new MappedEntity(
            d,
            new ComponentDescriptor
            {
                Key = ctx.Key(d),
                Platform = Platform,
                UniqueId = ctx.UniqueId(d),
                Name = d.Name,
                StateTopic = stateTopic,
                CommandTopic = commandTopic,
                DeviceClass = d.DeviceClass,
                Icon = d.Icon,
            },
            new[] { new StateBinding(stateId, stateTopic, v => IpxPayloads.OnOff(v)) },
            new[] { MapperHelpers.OnOffCommand(commandTopic, commandId) });
    }
}

/// <summary>Digital inputs.</summary>
public sealed class BinarySensorMapper : IComponentMapper
{
    public string Platform => "binary_sensor";

    public MappedEntity Map(EntityDefinition d, EntityContext ctx)
    {
        var stateId = MapperHelpers.Require(d.StateId, d, nameof(d.StateId));
        var stateTopic = ctx.Topic(d, "state");
        var invert = d.Invert;

        return new MappedEntity(
            d,
            new ComponentDescriptor
            {
                Key = ctx.Key(d),
                Platform = Platform,
                UniqueId = ctx.UniqueId(d),
                Name = d.Name,
                StateTopic = stateTopic,
                DeviceClass = d.DeviceClass,
                Icon = d.Icon,
            },
            new[] { new StateBinding(stateId, stateTopic, v => IpxPayloads.OnOff(v, invert)) },
            Array.Empty<CommandBinding>());
    }
}

/// <summary>Analog values.</summary>
public sealed class SensorMapper : IComponentMapper
{
    public string Platform => "sensor";

    public MappedEntity Map(EntityDefinition d, EntityContext ctx)
    {
        var stateId = MapperHelpers.Require(d.StateId, d, nameof(d.StateId));
        var stateTopic = ctx.Topic(d, "state");
        var decimals = d.Decimals;

        return new MappedEntity(
            d,
            new ComponentDescriptor
            {
                Key = ctx.Key(d),
                Platform = Platform,
                UniqueId = ctx.UniqueId(d),
                Name = d.Name,
                StateTopic = stateTopic,
                DeviceClass = d.DeviceClass,
                UnitOfMeasurement = d.Unit,
                StateClass = d.StateClass,
                Icon = d.Icon,
            },
            new[] { new StateBinding(stateId, stateTopic, v => IpxPayloads.Number(v, decimals)) },
            Array.Empty<CommandBinding>());
    }
}

/// <summary>Dimmers (X-DIMMER, X-DM channels): on/off io plus optional 0-100 level ana objects.</summary>
public sealed class LightMapper : IComponentMapper
{
    public string Platform => "light";

    public MappedEntity Map(EntityDefinition d, EntityContext ctx)
    {
        var commandId = MapperHelpers.Require(d.CommandId, d, nameof(d.CommandId));
        var stateId = d.StateId ?? commandId;
        var stateTopic = ctx.Topic(d, "state");
        var commandTopic = ctx.Topic(d, "set");

        var states = new List<StateBinding> { new(stateId, stateTopic, v => IpxPayloads.OnOff(v)) };
        var commands = new List<CommandBinding> { MapperHelpers.OnOffCommand(commandTopic, commandId) };
        var extra = new Dictionary<string, object?>();

        if (d.BrightnessCommandId is { } brightnessCommandId)
        {
            var topic = ctx.Topic(d, "brightness", "set");
            extra["brightness_command_topic"] = topic;
            extra["brightness_scale"] = 100;
            commands.Add(new CommandBinding(topic, payload =>
                double.TryParse(payload, NumberStyles.Float, CultureInfo.InvariantCulture, out var level)
                    ? new IpxCommand(brightnessCommandId, Math.Clamp(Math.Round(level), 0, 100))
                    : null));
        }

        if (d.BrightnessStateId is { } brightnessStateId)
        {
            var topic = ctx.Topic(d, "brightness", "state");
            extra["brightness_state_topic"] = topic;
            extra["brightness_scale"] = 100;
            states.Add(new StateBinding(brightnessStateId, topic, v => IpxPayloads.Number(v, 0)));
        }

        return new MappedEntity(
            d,
            new ComponentDescriptor
            {
                Key = ctx.Key(d),
                Platform = Platform,
                UniqueId = ctx.UniqueId(d),
                Name = d.Name,
                StateTopic = stateTopic,
                CommandTopic = commandTopic,
                Icon = d.Icon,
                Extra = extra,
            },
            states,
            commands);
    }
}

/// <summary>Writable analog outputs (X-PWM, X-400...): a Home Assistant number backed by one ana object.</summary>
public sealed class NumberMapper : IComponentMapper
{
    public string Platform => "number";

    public MappedEntity Map(EntityDefinition d, EntityContext ctx)
    {
        var commandId = MapperHelpers.Require(d.CommandId, d, nameof(d.CommandId));
        var stateId = d.StateId ?? commandId;
        var stateTopic = ctx.Topic(d, "state");
        var commandTopic = ctx.Topic(d, "set");
        var min = d.Min ?? 0;
        var max = d.Max ?? 100;
        if (max <= min)
        {
            throw new EntityConfigurationException($"Entity '{d.Name}': Max must be greater than Min.");
        }

        var decimals = d.Decimals;

        return new MappedEntity(
            d,
            new ComponentDescriptor
            {
                Key = ctx.Key(d),
                Platform = Platform,
                UniqueId = ctx.UniqueId(d),
                Name = d.Name,
                StateTopic = stateTopic,
                CommandTopic = commandTopic,
                DeviceClass = d.DeviceClass,
                UnitOfMeasurement = d.Unit,
                Icon = d.Icon,
                Extra = new Dictionary<string, object?> { ["min"] = min, ["max"] = max, ["step"] = 1 },
            },
            new[] { new StateBinding(stateId, stateTopic, v => IpxPayloads.Number(v, decimals)) },
            new[]
            {
                new CommandBinding(commandTopic, payload =>
                    double.TryParse(payload, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
                        ? new IpxCommand(commandId, Math.Clamp(value, min, max))
                        : null),
            });
    }
}
