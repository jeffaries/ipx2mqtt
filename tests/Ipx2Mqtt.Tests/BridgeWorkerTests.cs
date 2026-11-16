using Ipx2Mqtt.Core.Discovery;
using Ipx2Mqtt.Core.Mqtt;
using Ipx2Mqtt.Core.State;
using Ipx2Mqtt.Ipx;
using Ipx2Mqtt.Ipx.Mapping;
using Ipx2Mqtt.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ipx2Mqtt.Tests;

public class BridgeWorkerTests : IAsyncLifetime
{
    private readonly FakeMqttPublisher _mqtt = new();
    private readonly FakeIpxClient _ipx = new();
    private readonly BridgeWorker _worker;

    public BridgeWorkerTests()
    {
        var topics = new TopicBuilder("ipx800");
        var mapper = new EntityMapper(
            new IComponentMapper[] { new SwitchMapper(), new BinarySensorMapper(), new SensorMapper(), new LightMapper(), new NumberMapper() },
            topics,
            Options.Create(new DeviceOptions()));
        var registry = new EntityRegistry(mapper.MapAll(new[]
        {
            new EntityDefinition { Type = "relay", Index = 1, Name = "Relay 1", Component = "switch", StateId = 65544, CommandId = 65536 },
            new EntityDefinition { Type = "input", Index = 1, Name = "Input 1", Component = "binary_sensor", StateId = 65552 },
            new EntityDefinition { Type = "analog", Index = 1, Name = "Temp", Component = "sensor", StateId = 262153, Decimals = 1 },
        }));

        _ipx.StateIdForCommand = id => id == 65536 ? 65544 : null;

        var discovery = new DiscoveryPublisher(_mqtt, Options.Create(new DiscoveryOptions()), NullLogger<DiscoveryPublisher>.Instance);
        _worker = new BridgeWorker(
            _ipx,
            new IpxPushReceiver(),
            _mqtt,
            new AvailabilityManager(_mqtt, topics),
            new DiscoveryBuilder(),
            discovery,
            new StatePublisher(_mqtt),
            registry,
            Options.Create(new DeviceOptions()),
            NullLogger<BridgeWorker>.Instance);
    }

    public async Task InitializeAsync() => await _worker.StartAsync(CancellationToken.None);

    public async Task DisposeAsync() => await _worker.StopAsync(CancellationToken.None);

    private async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        var published = string.Join(" | ", _mqtt.Published.ToArray().Select(m => $"{m.Topic}={(m.Payload.Length > 20 ? m.Payload[..20] : m.Payload)}"));
        Assert.True(condition(), "Condition not reached in time. Published: " + published);
    }

    private bool Has(string topic, string payload) => _mqtt.Published.ToArray().Any(m => m.Topic == topic && m.Payload == payload);

    [Fact]
    public async Task Start_PublishesDiscoveryAndSubscribesToCommands()
    {
        await WaitUntil(() => _mqtt.Published.Any(m => m.Topic == "homeassistant/device/ipx800/config"));

        Assert.Contains("ipx800/relay/1/set", _mqtt.Subscriptions);
        Assert.Contains("homeassistant/status", _mqtt.Subscriptions);
    }

    [Fact]
    public async Task IpxStateChanges_ArePublishedOnlyWhenTheyChange()
    {
        _ipx.Emit(65544, true);
        _ipx.Emit(65544, true);
        _ipx.Emit(65552, false);
        _ipx.Emit(262153, 9.19);
        _ipx.Emit(65544, false);

        await WaitUntil(() => Has("ipx800/relay/1/state", "OFF"));

        var relay = _mqtt.Published.ToArray().Where(m => m.Topic == "ipx800/relay/1/state").Select(m => m.Payload);
        Assert.Equal(new[] { "ON", "OFF" }, relay);
        Assert.True(Has("ipx800/input/1/state", "OFF"));
        Assert.True(Has("ipx800/analog/1/state", "9.2"));
        Assert.All(_mqtt.Published.ToArray().Where(m => m.Topic.EndsWith("/state")), m => Assert.True(m.Retain));
    }

    [Fact]
    public async Task UnmappedIpxObjects_AreIgnored()
    {
        _ipx.Emit(327687, 1794777918.0); // IPX clock
        _ipx.Emit(65552, true);

        await WaitUntil(() => Has("ipx800/input/1/state", "ON"));

        Assert.DoesNotContain(_mqtt.Published.ToArray(), m => m.Topic.Contains("327687"));
    }

    [Fact]
    public async Task Command_IsSentToTheIpxAndStateIsPublishedOnlyAfterConfirmation()
    {
        _ipx.StateIdForCommand = null; // the IPX does not confirm yet

        await _mqtt.ReceiveAsync("ipx800/relay/1/set", "ON");
        await WaitUntil(() => _ipx.Commands.Count == 1);

        Assert.Equal(new IpxCommand(65536, true), _ipx.Commands[0]);
        await Task.Delay(100);
        Assert.DoesNotContain(_mqtt.Published.ToArray(), m => m.Topic == "ipx800/relay/1/state");

        _ipx.Emit(65544, true); // confirmation coming from the IPX
        await WaitUntil(() => Has("ipx800/relay/1/state", "ON"));
    }

    [Fact]
    public async Task UnsupportedCommandPayload_IsNotSent()
    {
        await _mqtt.ReceiveAsync("ipx800/relay/1/set", "BANANA");
        await Task.Delay(100);

        Assert.Empty(_ipx.Commands);
    }

    [Fact]
    public async Task Availability_FollowsTheIpx()
    {
        _ipx.SetAvailable(true);
        await WaitUntil(() => Has("ipx800/status", "online"));

        _ipx.SetAvailable(false);
        await WaitUntil(() => _mqtt.Published.ToArray().Last(m => m.Topic == "ipx800/status").Payload == "offline");
    }

    [Fact]
    public async Task MqttReconnection_RepublishesDiscoveryAvailabilityAndStates()
    {
        _ipx.SetAvailable(true);
        _ipx.Emit(65544, true);
        await WaitUntil(() => Has("ipx800/relay/1/state", "ON") && Has("ipx800/status", "online"));
        _mqtt.Published.Clear();

        await _mqtt.RaiseConnectedAsync();

        await WaitUntil(() =>
            _mqtt.Published.ToArray().Any(m => m.Topic == "homeassistant/device/ipx800/config") &&
            Has("ipx800/status", "online") &&
            Has("ipx800/relay/1/state", "ON"));
    }

    [Fact]
    public async Task HomeAssistantRestart_RepublishesStatesAfterDiscovery()
    {
        _ipx.Emit(65544, true);
        await WaitUntil(() => Has("ipx800/relay/1/state", "ON"));
        _mqtt.Published.Clear();

        await _mqtt.ReceiveAsync("homeassistant/status", "online");

        await WaitUntil(() => Has("ipx800/relay/1/state", "ON") &&
                              _mqtt.Published.ToArray().Any(m => m.Topic == "homeassistant/device/ipx800/config"));
    }

    [Fact]
    public async Task Stop_PublishesOffline()
    {
        _ipx.SetAvailable(true);
        await WaitUntil(() => Has("ipx800/status", "online"));

        await _worker.StopAsync(CancellationToken.None);

        Assert.Equal("offline", _mqtt.Published.ToArray().Last(m => m.Topic == "ipx800/status").Payload);
    }
}
