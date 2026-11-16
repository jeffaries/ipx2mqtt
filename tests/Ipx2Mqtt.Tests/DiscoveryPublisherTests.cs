using Ipx2Mqtt.Core.Discovery;
using Ipx2Mqtt.Core.Mqtt;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ipx2Mqtt.Tests;

public class DiscoveryPublisherTests
{
    private static (DiscoveryPublisher Publisher, FakeMqttPublisher Mqtt, List<TimeSpan> Delays) Create()
    {
        var mqtt = new FakeMqttPublisher();
        var delays = new List<TimeSpan>();
        var publisher = new DiscoveryPublisher(mqtt, Options.Create(new DiscoveryOptions()), NullLogger<DiscoveryPublisher>.Instance)
        {
            Delay = (d, _) =>
            {
                delays.Add(d);
                return Task.CompletedTask;
            },
        };
        return (publisher, mqtt, delays);
    }

    [Fact]
    public async Task Publish_SendsRetainedMessage()
    {
        var (publisher, mqtt, _) = Create();

        await publisher.PublishAsync(new DiscoveryMessage("homeassistant/device/x/config", "{}"));

        var sent = Assert.Single(mqtt.Published);
        Assert.Equal("homeassistant/device/x/config", sent.Topic);
        Assert.True(sent.Retain);
    }

    [Fact]
    public async Task Remove_SendsEmptyRetainedPayload()
    {
        var (publisher, mqtt, _) = Create();

        await publisher.RemoveAsync(new DiscoveryMessage("homeassistant/device/x/config", string.Empty));

        var sent = Assert.Single(mqtt.Published);
        Assert.Equal(string.Empty, sent.Payload);
        Assert.True(sent.Retain);
        Assert.Null(publisher.Current);
    }

    [Fact]
    public async Task Publish_ClearsRetainedAreaDevicesThatAreNoLongerPublished()
    {
        var (publisher, mqtt, _) = Create();
        publisher.StaleTopicPrefix = "homeassistant/device/ipx800_area_";
        await publisher.StartAsync(CancellationToken.None);
        Assert.Contains("homeassistant/device/+/config", mqtt.Subscriptions);

        // retained devices of a previous run, delivered by the broker before and after we publish
        await mqtt.ReceiveAsync("homeassistant/device/ipx800_area_cuisine/config", "{}");
        await mqtt.ReceiveAsync("homeassistant/device/ipx800_area_salon/config", "{}");
        await mqtt.ReceiveAsync("homeassistant/device/other_node/config", "{}");
        await publisher.PublishAsync(new[]
        {
            new DiscoveryMessage("homeassistant/device/ipx800/config", "{}"),
            new DiscoveryMessage("homeassistant/device/ipx800_area_salon/config", "{}"),
        });
        await mqtt.ReceiveAsync("homeassistant/device/ipx800_area_cave/config", "{}");

        var cleared = mqtt.Published.Where(m => m.Payload.Length == 0).Select(m => m.Topic).Order().ToArray();
        Assert.Equal(
            new[] { "homeassistant/device/ipx800_area_cave/config", "homeassistant/device/ipx800_area_cuisine/config" },
            cleared);
    }

    [Fact]
    public async Task Start_SubscribesToHomeAssistantStatus()
    {
        var (publisher, mqtt, _) = Create();

        await publisher.StartAsync(CancellationToken.None);

        Assert.Contains("homeassistant/status", mqtt.Subscriptions);
    }

    [Fact]
    public async Task HomeAssistantOnline_RepublishesDiscoveryThenStatesAfterDelay()
    {
        var (publisher, mqtt, delays) = Create();
        var order = new List<string>();
        publisher.HomeAssistantOnline = _ =>
        {
            order.Add("states");
            return Task.CompletedTask;
        };
        await publisher.StartAsync(CancellationToken.None);
        await publisher.PublishAsync(new DiscoveryMessage("homeassistant/device/x/config", "{\"a\":1}"));
        mqtt.Published.Clear();

        await mqtt.ReceiveAsync("homeassistant/status", "online");

        var republished = Assert.Single(mqtt.Published);
        Assert.Equal("{\"a\":1}", republished.Payload);
        Assert.Equal(new[] { "states" }, order);
        var delay = Assert.Single(delays);
        Assert.InRange(delay.TotalMilliseconds, 1000, 5000);
    }

    [Theory]
    [InlineData("homeassistant/status", "offline")]
    [InlineData("other/topic", "online")]
    public async Task OtherMessages_AreIgnored(string topic, string payload)
    {
        var (publisher, mqtt, delays) = Create();
        await publisher.StartAsync(CancellationToken.None);
        await publisher.PublishAsync(new DiscoveryMessage("homeassistant/device/x/config", "{}"));
        mqtt.Published.Clear();

        await mqtt.ReceiveAsync(topic, payload);

        Assert.Empty(mqtt.Published);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task AvailabilityManager_PublishesRetainedOnlineOffline()
    {
        var mqtt = new FakeMqttPublisher();
        var availability = new AvailabilityManager(mqtt, new TopicBuilder("ipx800"));

        await availability.SetAsync(true);
        await availability.SetAsync(false);

        Assert.Equal("ipx800/status", availability.Topic);
        Assert.Equal(new[] { "online", "offline" }, mqtt.Published.Select(m => m.Payload));
        Assert.All(mqtt.Published, m => Assert.True(m.Retain));
    }
}
