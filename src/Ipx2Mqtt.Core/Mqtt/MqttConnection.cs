using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;

namespace Ipx2Mqtt.Core.Mqtt;

/// <summary>
/// MQTTnet-based connection with automatic reconnection (capped exponential backoff),
/// Last Will on the availability topic and automatic re-subscription.
/// </summary>
public sealed class MqttConnection : IMqttPublisher, IAsyncDisposable
{
    private readonly MqttOptions _options;
    private readonly TopicBuilder _topics;
    private readonly ILogger<MqttConnection> _logger;
    private readonly MqttClientFactory _factory = new();
    private readonly IMqttClient _client;
    private readonly ConcurrentDictionary<string, byte> _subscriptions = new();

    public MqttConnection(IOptions<MqttOptions> options, TopicBuilder topics, ILogger<MqttConnection> logger)
    {
        _options = options.Value;
        _topics = topics;
        _logger = logger;
        _client = _factory.CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
    }

    public bool IsConnected => _client.IsConnected;

    public event Func<Task>? Connected;
    public event Func<MqttMessage, Task>? MessageReceived;

    /// <summary>Keeps the connection alive until cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var min = TimeSpan.FromSeconds(Math.Max(1, _options.ReconnectMinSeconds));
        var max = TimeSpan.FromSeconds(Math.Max(_options.ReconnectMinSeconds, _options.ReconnectMaxSeconds));
        var delay = min;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _client.ConnectAsync(BuildClientOptions(), ct).ConfigureAwait(false);
                _logger.LogInformation("Connected to MQTT broker {Host}:{Port}", _options.Host, _options.Port);
                delay = min;

                foreach (var topic in _subscriptions.Keys)
                {
                    await SubscribeCoreAsync(topic, ct).ConfigureAwait(false);
                }

                await RaiseConnectedAsync().ConfigureAwait(false);

                while (_client.IsConnected && !ct.IsCancellationRequested)
                {
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("MQTT connection failed or lost ({Reason}); retrying in {Delay}", ex.Message, delay);
                _logger.LogDebug(ex, "MQTT failure details");
            }

            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, max.Ticks));
        }

        if (_client.IsConnected)
        {
            try
            {
                await _client.DisconnectAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error while disconnecting from MQTT broker");
            }
        }
    }

    public async Task PublishAsync(string topic, string payload, bool retain = true, CancellationToken ct = default)
    {
        if (!_client.IsConnected)
        {
            _logger.LogDebug("MQTT not connected; dropping publish to {Topic}", topic);
            return;
        }

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag(retain)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        try
        {
            await _client.PublishAsync(message, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to publish to {Topic}", topic);
        }
    }

    public async Task SubscribeAsync(string topicFilter, CancellationToken ct = default)
    {
        if (!_subscriptions.TryAdd(topicFilter, 0))
        {
            return;
        }

        if (_client.IsConnected)
        {
            await SubscribeCoreAsync(topicFilter, ct).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task SubscribeCoreAsync(string topic, CancellationToken ct)
    {
        var options = _factory.CreateSubscribeOptionsBuilder()
            .WithTopicFilter(f => f.WithTopic(topic).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
            .Build();
        await _client.SubscribeAsync(options, ct).ConfigureAwait(false);
        _logger.LogDebug("Subscribed to {Topic}", topic);
    }

    private MqttClientOptions BuildClientOptions()
    {
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(_options.Host, _options.Port)
            .WithClientId(_options.ClientId)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(_options.KeepAliveSeconds))
            .WithCleanSession()
            .WithWillTopic(_topics.Availability)
            .WithWillPayload(AvailabilityManager.OfflinePayload)
            .WithWillRetain()
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);

        if (!string.IsNullOrEmpty(_options.Username))
        {
            builder.WithCredentials(_options.Username, _options.Password);
        }

        if (_options.UseTls)
        {
            builder.WithTlsOptions(o => o.UseTls());
        }

        return builder.Build();
    }

    private async Task RaiseConnectedAsync()
    {
        var handler = Connected;
        if (handler is null)
        {
            return;
        }

        foreach (var d in handler.GetInvocationList().Cast<Func<Task>>())
        {
            try
            {
                await d().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in MQTT connected handler");
            }
        }
    }

    private async Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        var handler = MessageReceived;
        if (handler is null)
        {
            return;
        }

        var message = new MqttMessage(
            e.ApplicationMessage.Topic,
            e.ApplicationMessage.ConvertPayloadToString() ?? string.Empty,
            e.ApplicationMessage.Retain);

        foreach (var d in handler.GetInvocationList().Cast<Func<MqttMessage, Task>>())
        {
            try
            {
                await d(message).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling MQTT message on {Topic}", message.Topic);
            }
        }
    }
}
