using System.Runtime.CompilerServices;
using Ipx2Mqtt.Core.Discovery;
using Ipx2Mqtt.Core.Mqtt;
using Ipx2Mqtt.Ipx;
using Ipx2Mqtt.Ipx.Mapping;
using Ipx2Mqtt.Service;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using NetEscapades.Configuration.Yaml;

namespace Ipx2Mqtt.Tests;

public class ExternalConfigTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ipxcfg_" + Guid.NewGuid().ToString("N"));

    public ExternalConfigTests() => Directory.CreateDirectory(Path.Combine(_root, "config", "sub"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void LocateDirectory_FindsConfigFolderInParents()
    {
        var config = Path.Combine(_root, "config");
        File.WriteAllText(Path.Combine(config, "entities.yml"), "Entities: []");

        Assert.Equal(config, ExternalConfig.LocateDirectory(null, Path.Combine(config, "sub")));
    }

    [Fact]
    public void LocateDirectory_IgnoresFolderWithoutConfigFiles() =>
        Assert.Null(ExternalConfig.LocateDirectory(null, Path.Combine(_root, "config", "sub")));

    [Fact]
    public void LocateDirectory_ExplicitMissingFolderFails() =>
        Assert.Throws<DirectoryNotFoundException>(() => ExternalConfig.LocateDirectory(Path.Combine(_root, "nope")));

    [Fact]
    public void YamlFiles_BindSettingsLabelsAndEntities()
    {
        var config = Path.Combine(_root, "config");
        File.WriteAllText(Path.Combine(config, "config.yml"), "Ipx:\n  BaseUrl: http://10.0.0.5\n  AutoDiscover: false\n");
        File.WriteAllText(Path.Combine(config, "entities.yml"), """
            Entities:
              - { Key: "04-8R.05", Name: Z3 1er Etage, Component: light }
              - { Type: relay, Index: 1, Name: Relais 1, Component: switch, StateId: 65544, CommandId: 65536 }
              - { Type: analog, Index: 1, Name: Temp, Component: sensor, StateId: 262153, Unit: "°C", Decimals: 1 }
            """);

        var configuration = new ConfigurationBuilder()
            .AddYamlFile(Path.Combine(config, "config.yml"))
            .AddYamlFile(Path.Combine(config, "entities.yml"))
            .Build();

        var ipx = configuration.GetSection("Ipx").Get<IpxOptions>()!;
        var entities = configuration.Get<EntitiesOptions>()!;

        Assert.Equal("http://10.0.0.5", ipx.BaseUrl);
        Assert.False(ipx.AutoDiscover);
        Assert.Equal("Z3 1er Etage", entities.Overrides["04-8R.05"].Name);
        Assert.Equal("light", entities.Overrides["04-8r.05"].Component);
        Assert.Equal(2, entities.Declared.Count);
        Assert.Equal(65536, entities.Declared[0].CommandId);
        Assert.Equal("°C", entities.Declared[1].Unit);
    }

    private static string ConfigDirectory([CallerFilePath] string testFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "config"));

    [Fact]
    public void ExampleFiles_LoadAndMap()
    {
        var configuration = new ConfigurationBuilder()
            .AddYamlFile(Path.Combine(ConfigDirectory(), "config.example.yml"))
            .AddYamlFile(Path.Combine(ConfigDirectory(), "entities.example.yml"))
            .Build();

        Assert.Equal("mosquitto", configuration["Mqtt:Host"]);
        Assert.True(configuration.GetSection("Ipx").Get<IpxOptions>()!.AutoDiscover);

        var entities = configuration.Get<EntitiesOptions>()!;
        var mapper = new EntityMapper(
            new IComponentMapper[] { new SwitchMapper(), new BinarySensorMapper(), new SensorMapper(), new LightMapper(), new NumberMapper() },
            new TopicBuilder("ipx800"),
            Options.Create(new DeviceOptions()));

        Assert.Equal(5, mapper.MapAll(entities.Declared).Count);
        Assert.Equal("Z3 1er Etage", entities.Overrides["04-8R.05"].Name);
    }
}
