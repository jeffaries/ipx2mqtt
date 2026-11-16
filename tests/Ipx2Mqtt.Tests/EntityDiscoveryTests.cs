using Ipx2Mqtt.Core.Discovery;
using Ipx2Mqtt.Core.Mqtt;
using Ipx2Mqtt.Ipx;
using Ipx2Mqtt.Ipx.Mapping;
using Microsoft.Extensions.Options;

namespace Ipx2Mqtt.Tests;

public class EntityDiscoveryTests
{
    private static IpxInventory Inventory()
    {
        var o = new List<IpxListedObject>();
        for (var i = 1; i <= 8; i++)
        {
            o.Add(new(65535 + i, $"[01-IPX]Relay cmd {i}", 12648447 + i, false));
            o.Add(new(65543 + i, $"[01-IPX]Relay state {i}", 0, false));
            o.Add(new(65613 + i, $"[03-8R]Relay cmd {i}", 12845056, false));
            o.Add(new(65630 + i, $"[03-8R]Relay state {i}", 0, false));
        }

        for (var i = 1; i <= 4; i++)
        {
            o.Add(new(65560 + i, $"[IPX]Open coll.  {i}", 0, false));
            o.Add(new(65570 + i, $"[IPX]Open coll. state {i}", 0, false));
            o.Add(new(65585 + 2 * i, $"[06-DM]On/Off {i}", 4849664, false));
            o.Add(new(65586 + 2 * i, $"[06-DM]Input {i}", 4849664, false));
            o.Add(new(196616 + i, $"[06-DM]Position {i}", 0, true));
            o.Add(new(262193 + i, $"[06-DM]Consign {i}", 4849664, true));
        }

        o.Add(new(65595, "[06-DM]On/Off All", 4849664, false));
        o.Add(new(65552, "[01-IPX]Digital input 1", 0, false));
        o.Add(new(262153, "[IPX]Analog input 1", 0, true));
        o.Add(new(262600, "Chan 1", 777, true));
        o.Add(new(262601, "Chan 2", 777, true));

        return new IpxInventory(o, new[] { new IpxExtension("xpwm", 777, "PWM") });
    }

    private static IEnumerable<EntityDefinition> Of(IReadOnlyList<EntityDefinition> all, string type) => all.Where(e => e.Type == type);

    private static EntityMapper Mapper() => new(
        new IComponentMapper[] { new SwitchMapper(), new BinarySensorMapper(), new SensorMapper(), new LightMapper(), new NumberMapper() },
        new TopicBuilder("ipx800"),
        Options.Create(new DeviceOptions()));

    [Fact]
    public void Discover_ExposesEveryRelayDimmerAndAnalogOutput()
    {
        var e = IpxEntityDiscovery.Discover(Inventory());

        Assert.Equal(8, Of(e, "relay").Count());
        Assert.Equal(8, Of(e, "03-8r").Count());
        Assert.Equal(4, Of(e, "collector").Count());
        Assert.Equal(4, Of(e, "06-dm").Count(x => x.Component == "light"));
        Assert.Single(Of(e, "06-dm-all"));
        Assert.Equal(2, Of(e, "xpwm").Count(x => x.Component == "number"));
        Assert.DoesNotContain(e, x => x.Name.Contains("Digital input") || x.Name.Contains("Input"));

        var relay1 = Of(e, "relay").Single(x => x.Index == 1);
        Assert.Equal(65536, relay1.CommandId);
        Assert.Equal(65544, relay1.StateId);

        var dimmer1 = Of(e, "06-dm").Single(x => x.Index == 1);
        Assert.Equal(65587, dimmer1.CommandId);
        Assert.Equal(262194, dimmer1.BrightnessCommandId);
        Assert.Equal(196617, dimmer1.BrightnessStateId);

        var coll1 = Of(e, "collector").Single(x => x.Index == 1);
        Assert.Equal(65561, coll1.CommandId);
        Assert.Equal(65571, coll1.StateId);
    }

    [Fact]
    public void Discover_AppliesLabelsByModuleCode()
    {
        var labels = new Dictionary<string, string>
        {
            ["03-8R.02"] = "Lumières Bureau Z1", ["06-DM.01"] = "Salle à manger Z1", ["01-IPX.03"] = "Cuisine Z1",
        };

        var e = IpxEntityDiscovery.Discover(Inventory(), labels);

        Assert.Equal("Lumières Bureau Z1", Of(e, "03-8r").Single(x => x.Index == 2).Name);
        Assert.Equal("Salle à manger Z1", Of(e, "06-dm").Single(x => x.Index == 1).Name);
        Assert.Equal("Cuisine Z1", Of(e, "relay").Single(x => x.Index == 3).Name);
        Assert.Equal("03-8R relais 3", Of(e, "03-8r").Single(x => x.Index == 3).Name);
    }

    [Fact]
    public void Discover_ResultMapsWithoutConflicts()
    {
        var registry = new EntityRegistry(Mapper().MapAll(IpxEntityDiscovery.Discover(Inventory())));

        Assert.Equal(8 + 8 + 4 + 4 + 1 + 2, registry.Entities.Count);
        Assert.Contains("ipx800/relay/1/set", registry.Commands.Keys);
        Assert.Contains("ipx800/06-dm/1/brightness/set", registry.Commands.Keys);
    }

    [Fact]
    public void Discover_FindsNothingWithoutObjects()
    {
        Assert.Empty(IpxEntityDiscovery.Discover(new IpxInventory(Array.Empty<IpxListedObject>(), Array.Empty<IpxExtension>())));
    }

    [Fact]
    public void Merge_DeclaredEntitiesWin()
    {
        var declared = new[]
        {
            new EntityDefinition { Type = "relay", Index = 1, Name = "Mine", Component = "switch", StateId = 65544, CommandId = 65536 },
        };

        var merged = IpxEntityDiscovery.Merge(declared, IpxEntityDiscovery.Discover(Inventory()));

        Assert.Equal(1, merged.Count(e => e.CommandId == 65536));
        Assert.Equal("Mine", merged.Single(e => e.CommandId == 65536).Name);
        Assert.Equal(8 + 8 + 4 + 4 + 1 + 2, merged.Count);
        Mapper().MapAll(merged); // no duplicate key or topic
    }

    [Fact]
    public void Number_ClampsAndRejectsBadPayloads()
    {
        var mapped = Mapper().Map(new EntityDefinition
        {
            Type = "xpwm", Name = "PWM", Component = "number", CommandId = 262400, Min = 0, Max = 100,
        });

        Assert.Equal(100.0, mapped.Component.Extra["max"]);
        var command = mapped.Commands.Single();
        Assert.Equal(new IpxCommand(262400, 42.0), command.Parse("42"));
        Assert.Equal(new IpxCommand(262400, 100.0), command.Parse("250"));
        Assert.Null(command.Parse("abc"));
        Assert.Null(command.Parse("NaN"));
    }
}
