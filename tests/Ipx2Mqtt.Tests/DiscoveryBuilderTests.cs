using System.Text.Json.Nodes;
using Ipx2Mqtt.Core.Discovery;

namespace Ipx2Mqtt.Tests;

public class DiscoveryBuilderTests
{
    private static readonly DeviceInfo Device = new("ipx800", "IPX800 V5", "GCE Electronics", "IPX800 V5", "api 1.6");
    private static readonly OriginInfo Origin = new("ipx2mqtt", "1.0.0");

    private static List<ComponentDescriptor> Components() => new()
    {
        new ComponentDescriptor
        {
            Key = "relay_1",
            Platform = "switch",
            UniqueId = "ipx800_relay_1",
            Name = "Garage light",
            StateTopic = "ipx800/relay/1/state",
            CommandTopic = "ipx800/relay/1/set",
        },
        new ComponentDescriptor
        {
            Key = "analog_1",
            Platform = "sensor",
            UniqueId = "ipx800_analog_1",
            Name = "Cellar temperature",
            StateTopic = "ipx800/analog/1/state",
            UnitOfMeasurement = "°C",
            DeviceClass = "temperature",
        },
    };

    [Fact]
    public void BuildAll_GroupsComponentsOfAnAreaInTheirOwnDevice()
    {
        var components = Components();
        components[0].Area = "Séjour & Cuisine";

        var messages = new DiscoveryBuilder().BuildAll("ipx800", Device, Origin, "ipx800/status", components);

        Assert.Equal(2, messages.Count);
        var main = JsonNode.Parse(messages[0].Payload)!;
        Assert.Equal("homeassistant/device/ipx800/config", messages[0].Topic);
        Assert.Equal(new[] { "analog_1" }, main["cmps"]!.AsObject().Select(c => c.Key));

        var area = JsonNode.Parse(messages[1].Payload)!;
        Assert.Equal("homeassistant/device/ipx800_area_sejour_cuisine/config", messages[1].Topic);
        Assert.Equal("Séjour & Cuisine", (string?)area["dev"]!["sa"]);
        Assert.Equal("ipx800", (string?)area["dev"]!["via_device"]);
        Assert.Equal("ipx800_area_sejour_cuisine", (string?)area["dev"]!["ids"]);
        Assert.Equal("IPX800 V5 - Séjour & Cuisine", (string?)area["dev"]!["name"]);
        Assert.Equal(new[] { "relay_1" }, area["cmps"]!.AsObject().Select(c => c.Key));
        Assert.StartsWith(new DiscoveryBuilder().AreaTopicPrefix("ipx800"), messages[1].Topic);
    }

    [Fact]
    public void BuildAll_WithoutAreaIsTheSingleMainDevice()
    {
        var messages = new DiscoveryBuilder().BuildAll("ipx800", Device, Origin, "s", Components());

        Assert.Equal(new DiscoveryBuilder().Build("ipx800", Device, Origin, "s", Components()), messages.Single());
    }

    [Fact]
    public void BuildAll_ClearsTheMainDeviceWhenEveryComponentHasAnArea()
    {
        var components = Components();
        components.ForEach(c => c.Area = "Cave");

        var messages = new DiscoveryBuilder().BuildAll("ipx800", Device, Origin, "s", components);

        Assert.Equal(string.Empty, messages[0].Payload);
        Assert.Equal("homeassistant/device/ipx800/config", messages[0].Topic);
        Assert.Equal(2, JsonNode.Parse(messages[1].Payload)!["cmps"]!.AsObject().Count);
    }

    [Fact]
    public void Build_UsesDeviceDiscoveryTopic()
    {
        var message = new DiscoveryBuilder().Build("ipx800", Device, Origin, "ipx800/status", Components());

        Assert.Equal("homeassistant/device/ipx800/config", message.Topic);
    }

    [Fact]
    public void Build_UsesCustomPrefix()
    {
        var message = new DiscoveryBuilder("ha").Build("ipx800", Device, Origin, "ipx800/status", Components());

        Assert.Equal("ha/device/ipx800/config", message.Topic);
    }

    [Fact]
    public void Build_WritesDeviceAndOrigin()
    {
        var json = JsonNode.Parse(new DiscoveryBuilder().Build("ipx800", Device, Origin, "ipx800/status", Components()).Payload)!;

        Assert.Equal("ipx800", (string?)json["dev"]!["ids"]);
        Assert.Equal("IPX800 V5", (string?)json["dev"]!["name"]);
        Assert.Equal("GCE Electronics", (string?)json["dev"]!["mf"]);
        Assert.Equal("IPX800 V5", (string?)json["dev"]!["mdl"]);
        Assert.Equal("api 1.6", (string?)json["dev"]!["sw"]);
        Assert.Equal("ipx2mqtt", (string?)json["o"]!["name"]);
        Assert.Equal("1.0.0", (string?)json["o"]!["sw"]);
    }

    [Fact]
    public void Build_KeepsTopicsOnEachComponentNotOnRoot()
    {
        var json = JsonNode.Parse(new DiscoveryBuilder().Build("ipx800", Device, Origin, "ipx800/status", Components()).Payload)!;

        Assert.Null(json["state_topic"]);
        Assert.Null(json["command_topic"]);
        Assert.Equal("ipx800/relay/1/state", (string?)json["cmps"]!["relay_1"]!["state_topic"]);
        Assert.Equal("ipx800/relay/1/set", (string?)json["cmps"]!["relay_1"]!["command_topic"]);
        Assert.Equal("ipx800/analog/1/state", (string?)json["cmps"]!["analog_1"]!["state_topic"]);
        Assert.Null(json["cmps"]!["analog_1"]!["command_topic"]);
    }

    [Fact]
    public void Build_WritesPlatformUniqueIdNameAndSensorAttributes()
    {
        var json = JsonNode.Parse(new DiscoveryBuilder().Build("ipx800", Device, Origin, "ipx800/status", Components()).Payload)!;

        var relay = json["cmps"]!["relay_1"]!;
        Assert.Equal("switch", (string?)relay["p"]);
        Assert.Equal("ipx800_relay_1", (string?)relay["unique_id"]);
        Assert.Equal("Garage light", (string?)relay["name"]);

        var sensor = json["cmps"]!["analog_1"]!;
        Assert.Equal("sensor", (string?)sensor["p"]);
        Assert.Equal("°C", (string?)sensor["unit_of_measurement"]);
        Assert.Equal("temperature", (string?)sensor["device_class"]);
    }

    [Fact]
    public void Build_SharesAvailabilityAtRootLevel()
    {
        var json = JsonNode.Parse(new DiscoveryBuilder().Build("ipx800", Device, Origin, "ipx800/status", Components()).Payload)!;

        Assert.Equal("ipx800/status", (string?)json["avty_t"]);
    }

    [Fact]
    public void Build_WritesExtraKeys()
    {
        var light = new ComponentDescriptor
        {
            Key = "dimmer_1",
            Platform = "light",
            UniqueId = "ipx800_dimmer_1",
            Name = "Dimmer",
            Extra = new Dictionary<string, object?> { ["brightness_scale"] = 100, ["brightness_command_topic"] = "t/set", ["ignored"] = null },
        };

        var json = JsonNode.Parse(new DiscoveryBuilder().Build("ipx800", Device, Origin, "s", new[] { light }).Payload)!;

        Assert.Equal(100, (int?)json["cmps"]!["dimmer_1"]!["brightness_scale"]);
        Assert.Equal("t/set", (string?)json["cmps"]!["dimmer_1"]!["brightness_command_topic"]);
        Assert.Null(json["cmps"]!["dimmer_1"]!["ignored"]);
    }

    [Fact]
    public void BuildRemoval_IsEmptyPayloadOnConfigTopic()
    {
        var removal = new DiscoveryBuilder().BuildRemoval("ipx800");

        Assert.Equal("homeassistant/device/ipx800/config", removal.Topic);
        Assert.Equal(string.Empty, removal.Payload);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad node")]
    [InlineData("a/b")]
    [InlineData("é")]
    public void Build_RejectsInvalidNodeId(string nodeId)
    {
        Assert.Throws<ArgumentException>(() => new DiscoveryBuilder().Build(nodeId, Device, Origin, "s", Components()));
    }

    [Fact]
    public void Build_RejectsDuplicateKeysAndEmptyList()
    {
        var duplicate = Components();
        duplicate.Add(duplicate[0]);

        Assert.Throws<ArgumentException>(() => new DiscoveryBuilder().Build("ipx800", Device, Origin, "s", duplicate));
        Assert.Throws<ArgumentException>(() => new DiscoveryBuilder().Build("ipx800", Device, Origin, "s", Array.Empty<ComponentDescriptor>()));
    }
}
