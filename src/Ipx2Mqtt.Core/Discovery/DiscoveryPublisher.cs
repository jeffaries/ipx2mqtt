using Ipx2Mqtt.Core.Mqtt;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ipx2Mqtt.Core.Discovery;

/// <summary>
/// Publishes the retained discovery message, removes it on demand and republishes it
/// (then the current states) when Home Assistant announces it is online.
/// </summary>
public sealed class DiscoveryPublisher
{
    private readonly IMqttPublisher _mqtt;
    private readonly DiscoveryOptions _options;
    private readonly ILogger<DiscoveryPublisher> _logger;
    private CancellationToken _ct;

    public DiscoveryPublisher(IMqttPublisher mqtt, IOptions<DiscoveryOptions> options, ILogger<DiscoveryPublisher> logger)
    {
        _mqtt = mqtt;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Last message handed to <see cref="PublishAsync"/>.</summary>
    public DiscoveryMessage? Current { get; private set; }

    /// <summary>Called after the discovery has been republished because Home Assistant restarted.</summary>
    public Func<CancellationToken, Task>? HomeAssistantOnline { get; set; }

    /// <summary>Replaceable for tests.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    /// <summary>Subscribes to the Home Assistant birth topic.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        _ct = ct;
        _mqtt.MessageReceived += OnMessageAsync;
        await _mqtt.SubscribeAsync(_options.StatusTopic, ct).ConfigureAwait(false);
    }

    public Task PublishAsync(DiscoveryMessage message, CancellationToken ct = default)
    {
        Current = message;
        return _mqtt.PublishAsync(message.Topic, message.Payload, retain: true, ct);
    }

    public Task RepublishCurrentAsync(CancellationToken ct = default) =>
        Current is null ? Task.CompletedTask : _mqtt.PublishAsync(Current.Topic, Current.Payload, retain: true, ct);

    /// <summary>Removes the entities: empty retained payload on the config topic.</summary>
    public Task RemoveAsync(DiscoveryMessage removal, CancellationToken ct = default)
    {
        Current = null;
        return _mqtt.PublishAsync(removal.Topic, string.Empty, retain: true, ct);
    }

    private async Task OnMessageAsync(MqttMessage message)
    {
        if (message.Topic != _options.StatusTopic || message.Payload != _options.BirthPayload)
        {
            return;
        }

        var min = Math.Max(0, _options.RepublishDelayMinMs);
        var max = Math.Max(min, _options.RepublishDelayMaxMs);
        var wait = TimeSpan.FromMilliseconds(min == max ? min : Random.Shared.Next(min, max + 1));
        _logger.LogInformation("Home Assistant is online; republishing discovery in {Delay}", wait);

        await Delay(wait, _ct).ConfigureAwait(false);
        await RepublishCurrentAsync(_ct).ConfigureAwait(false);

        if (HomeAssistantOnline is { } callback)
        {
            await callback(_ct).ConfigureAwait(false);
        }
    }
}
