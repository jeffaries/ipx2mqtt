using Ipx2Mqtt.Core.Mqtt;
using Ipx2Mqtt.Ipx;
using Ipx2Mqtt.Ipx.Mapping;
using Microsoft.Extensions.Options;

namespace Ipx2Mqtt.Tests;

public class EntityMapperTests
{
    private static EntityMapper CreateMapper(string baseTopic = "ipx800") => new(
        new IComponentMapper[] { new SwitchMapper(), new BinarySensorMapper(), new SensorMapper(), new LightMapper(), new NumberMapper() },
        new TopicBuilder(baseTopic),
        Options.Create(new DeviceOptions { NodeId = "ipx800" }));

    [Fact]
    public void Switch_MapsStateAndCommandTopics()
    {
        var mapped = CreateMapper().Map(new EntityDefinition
        {
            Type = "relay", Index = 1, Name = "Garage light", Component = "switch", StateId = 65544, CommandId = 65536,
        });

        Assert.Equal("switch", mapped.Component.Platform);
        Assert.Equal("ipx800_relay_1", mapped.Component.UniqueId);
        Assert.Equal("ipx800/relay/1/state", mapped.Component.StateTopic);
        Assert.Equal("ipx800/relay/1/set", mapped.Component.CommandTopic);

        var state = Assert.Single(mapped.States);
        Assert.Equal(65544, state.IpxId);
        Assert.Equal("ON", state.ToPayload(true));
        Assert.Equal("OFF", state.ToPayload(false));
    }

    [Fact]
    public void Switch_ParsesCommandsToTheCommandObject()
    {
        var mapped = CreateMapper().Map(new EntityDefinition
        {
            Type = "relay", Index = 1, Name = "Garage light", Component = "switch", StateId = 65544, CommandId = 65536,
        });
        var command = Assert.Single(mapped.Commands);

        Assert.Equal(new IpxCommand(65536, true), command.Parse("ON"));
        Assert.Equal(new IpxCommand(65536, false), command.Parse("off"));
        Assert.Null(command.Parse("TOGGLE"));
    }

    [Fact]
    public void Switch_UsesCommandIdAsStateWhenNoStateId()
    {
        var mapped = CreateMapper().Map(new EntityDefinition
        {
            Type = "relay", Index = 2, Name = "R2", Component = "switch", CommandId = 65537,
        });

        Assert.Equal(65537, Assert.Single(mapped.States).IpxId);
    }

    [Fact]
    public void Switch_WithoutCommandIdIsRejected()
    {
        Assert.Throws<EntityConfigurationException>(() => CreateMapper().Map(new EntityDefinition
        {
            Type = "relay", Index = 1, Name = "R", Component = "switch", StateId = 1,
        }));
    }

    [Fact]
    public void BinarySensor_SupportsInversionAndHasNoCommand()
    {
        var mapped = CreateMapper().Map(new EntityDefinition
        {
            Type = "input", Index = 1, Name = "Garage door", Component = "binary_sensor", StateId = 65552, DeviceClass = "door", Invert = true,
        });

        Assert.Empty(mapped.Commands);
        Assert.Null(mapped.Component.CommandTopic);
        Assert.Equal("door", mapped.Component.DeviceClass);
        var state = Assert.Single(mapped.States);
        Assert.Equal("OFF", state.ToPayload(true));
        Assert.Equal("ON", state.ToPayload(false));
    }

    [Fact]
    public void Sensor_FormatsNumbersWithInvariantCultureAndRounding()
    {
        var mapped = CreateMapper().Map(new EntityDefinition
        {
            Type = "analog", Index = 1, Name = "Cellar temperature", Component = "sensor", StateId = 262153, Unit = "°C", Decimals = 1,
        });

        Assert.Equal("°C", mapped.Component.UnitOfMeasurement);
        var state = Assert.Single(mapped.States);
        Assert.Equal("9.2", state.ToPayload(9.19));
        Assert.Equal("100", state.ToPayload(100.0));
        Assert.Equal("-3.5", state.ToPayload(-3.54));
    }

    [Fact]
    public void Light_MapsBrightnessTopicsAndCommands()
    {
        var mapped = CreateMapper().Map(new EntityDefinition
        {
            Type = "dimmer", Index = 1, Name = "Living room", Component = "light",
            StateId = 65587, CommandId = 65587, BrightnessStateId = 196617, BrightnessCommandId = 262194,
        });

        Assert.Equal("ipx800/dimmer/1/brightness/set", mapped.Component.Extra["brightness_command_topic"]);
        Assert.Equal("ipx800/dimmer/1/brightness/state", mapped.Component.Extra["brightness_state_topic"]);
        Assert.Equal(100, mapped.Component.Extra["brightness_scale"]);
        Assert.Equal(2, mapped.States.Count);
        Assert.Equal(2, mapped.Commands.Count);

        var brightness = mapped.Commands.Single(c => c.Topic.EndsWith("brightness/set"));
        Assert.Equal(new IpxCommand(262194, 40.0), brightness.Parse("40"));
        Assert.Equal(new IpxCommand(262194, 100.0), brightness.Parse("250"));
        Assert.Null(brightness.Parse("abc"));
    }

    [Fact]
    public void BaseTopicIsConfigurable()
    {
        var mapped = CreateMapper("home/ipx").Map(new EntityDefinition
        {
            Type = "relay", Index = 1, Name = "R", Component = "switch", CommandId = 1,
        });

        Assert.Equal("home/ipx/relay/1/state", mapped.Component.StateTopic);
    }

    [Fact]
    public void IndexDefaultsToIpxId()
    {
        var mapped = CreateMapper().Map(new EntityDefinition
        {
            Type = "relay", Name = "R", Component = "switch", StateId = 65544, CommandId = 65536,
        });

        Assert.Equal("ipx800/relay/65544/state", mapped.Component.StateTopic);
    }

    [Theory]
    [InlineData("", "R", "switch")]
    [InlineData("rel ay", "R", "switch")]
    [InlineData("relay", "", "switch")]
    [InlineData("relay", "R", "fan")]
    public void InvalidDefinitionsAreRejected(string type, string name, string component)
    {
        Assert.Throws<EntityConfigurationException>(() => CreateMapper().Map(new EntityDefinition
        {
            Type = type, Index = 1, Name = name, Component = component, StateId = 1, CommandId = 1,
        }));
    }

    [Fact]
    public void MapAll_RejectsDuplicates()
    {
        var a = new EntityDefinition { Type = "relay", Index = 1, Name = "A", Component = "switch", CommandId = 1 };
        var b = new EntityDefinition { Type = "relay", Index = 1, Name = "B", Component = "switch", CommandId = 2 };

        Assert.Throws<EntityConfigurationException>(() => CreateMapper().MapAll(new[] { a, b }));
    }

    [Fact]
    public void Registry_IndexesStatesByIpxIdAndCommandsByTopic()
    {
        var mapper = CreateMapper();
        var registry = new EntityRegistry(mapper.MapAll(new[]
        {
            new EntityDefinition { Type = "relay", Index = 1, Name = "A", Component = "switch", StateId = 65544, CommandId = 65536 },
            new EntityDefinition { Type = "input", Index = 1, Name = "B", Component = "binary_sensor", StateId = 65552 },
        }));

        Assert.True(registry.StatesById.ContainsKey(65544));
        Assert.True(registry.StatesById.ContainsKey(65552));
        Assert.False(registry.StatesById.ContainsKey(327687)); // the IPX clock is not exposed
        Assert.Contains("ipx800/relay/1/set", registry.Commands.Keys);
        Assert.Equal(2, registry.Components.Count());
    }
}
