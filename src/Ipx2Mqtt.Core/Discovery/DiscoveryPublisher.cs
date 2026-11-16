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

    private IReadOnlyList<DiscoveryMessage> _messages = Array.Empty<DiscoveryMessage>();
    private readonly HashSet<string> _retainedTopics = new();
    private readonly object _gate = new();

    /// <summary>First (main) message handed to <see cref="PublishAsync(DiscoveryMessage, CancellationToken)"/>.</summary>
    public DiscoveryMessage? Current => _messages.Count > 0 ? _messages[0] : null;

    /// <summary>
    /// Topic prefix of the area devices this node publishes. Retained discovery messages under it that are no longer
    /// published (an area was renamed or removed) are cleared, otherwise their entities would come back.
    /// </summary>
    public string? StaleTopicPrefix { get; set; }

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
        if (StaleTopicPrefix is not null)
        {
            await _mqtt.SubscribeAsync($"{_options.Prefix.Trim('/')}/device/+/config", ct).ConfigureAwait(false);
        }
    }

    public Task PublishAsync(DiscoveryMessage message, CancellationToken ct = default) =>
        PublishAsync(new[] { message }, ct);

    /// <summary>Publishes the main device message and the area device messages, then clears the stale area devices.</summary>
    public async Task PublishAsync(IReadOnlyList<DiscoveryMessage> messages, CancellationToken ct = default)
    {
        _messages = messages;
        foreach (var message in messages)
        {
            await _mqtt.PublishAsync(message.Topic, message.Payload, retain: true, ct).ConfigureAwait(false);
        }

        await ClearStaleAsync(ct).ConfigureAwait(false);
    }

    public async Task RepublishCurrentAsync(CancellationToken ct = default)
    {
        foreach (var message in _messages)
        {
            await _mqtt.PublishAsync(message.Topic, message.Payload, retain: true, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Removes the entities: empty retained payload on the config topic.</summary>
    public Task RemoveAsync(DiscoveryMessage removal, CancellationToken ct = default)
    {
        _messages = Array.Empty<DiscoveryMessage>();
        return _mqtt.PublishAsync(removal.Topic, string.Empty, retain: true, ct);
    }

    private async Task ClearStaleAsync(CancellationToken ct)
    {
        string[] stale;
        lock (_gate)
        {
            var current = _messages.Where(m => m.Payload.Length > 0).Select(m => m.Topic).ToHashSet();
            stale = _retainedTopics.Where(t => !current.Contains(t)).ToArray();
            foreach (var topic in stale)
            {
                _retainedTopics.Remove(topic);
            }
        }

        foreach (var topic in stale)
        {
            _logger.LogInformation("Clearing stale discovery device {Topic}", topic);
            await _mqtt.PublishAsync(topic, string.Empty, retain: true, ct).ConfigureAwait(false);
        }
    }

    private async Task OnMessageAsync(MqttMessage message)
    {
        // A retained area device of this node, seen on the broker (possibly before our own messages are published).
        if (StaleTopicPrefix is { } prefix && message.Payload.Length > 0
            && message.Topic.StartsWith(prefix, StringComparison.Ordinal) && message.Topic.EndsWith("/config", StringComparison.Ordinal))
        {
            lock (_gate)
            {
                _retainedTopics.Add(message.Topic);
            }

            if (_messages.Count > 0)
            {
                await ClearStaleAsync(_ct).ConfigureAwait(false);
            }

            return;
        }

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
