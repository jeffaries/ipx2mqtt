using Ipx2Mqtt.Core.State;

namespace Ipx2Mqtt.Tests;

public class StateTests
{
    [Fact]
    public void StateCache_ReportsFirstValueAsChange()
    {
        var cache = new StateCache();

        Assert.True(cache.TryUpdate("t", "ON"));
    }

    [Fact]
    public void StateCache_IgnoresIdenticalValue()
    {
        var cache = new StateCache();
        cache.TryUpdate("t", "ON");

        Assert.False(cache.TryUpdate("t", "ON"));
    }

    [Fact]
    public void StateCache_ReportsChangedValue()
    {
        var cache = new StateCache();
        cache.TryUpdate("t", "ON");

        Assert.True(cache.TryUpdate("t", "OFF"));
        Assert.True(cache.TryUpdate("t", "ON"));
    }

    [Fact]
    public void StateCache_TracksTopicsIndependently()
    {
        var cache = new StateCache();
        cache.TryUpdate("a", "ON");

        Assert.True(cache.TryUpdate("b", "ON"));
        Assert.Equal(2, cache.Snapshot().Count);
    }

    [Fact]
    public async Task StatePublisher_PublishesOnlyChangesAsRetained()
    {
        var mqtt = new FakeMqttPublisher();
        var publisher = new StatePublisher(mqtt);

        Assert.True(await publisher.PublishIfChangedAsync("ipx800/relay/1/state", "ON"));
        Assert.False(await publisher.PublishIfChangedAsync("ipx800/relay/1/state", "ON"));
        Assert.True(await publisher.PublishIfChangedAsync("ipx800/relay/1/state", "OFF"));

        Assert.Equal(new[] { "ON", "OFF" }, mqtt.Published.Select(m => m.Payload));
        Assert.All(mqtt.Published, m => Assert.True(m.Retain));
    }

    [Fact]
    public async Task StatePublisher_RepublishAllSendsEveryKnownState()
    {
        var mqtt = new FakeMqttPublisher();
        var publisher = new StatePublisher(mqtt);
        await publisher.PublishIfChangedAsync("a", "1");
        await publisher.PublishIfChangedAsync("b", "2");
        mqtt.Published.Clear();

        await publisher.RepublishAllAsync();

        Assert.Equal(2, mqtt.Published.Count);
        Assert.Contains(mqtt.Published, m => m.Topic == "a" && m.Payload == "1");
        Assert.Contains(mqtt.Published, m => m.Topic == "b" && m.Payload == "2");
    }
}
