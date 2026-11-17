using System.Reflection;
using System.Threading.Channels;
using Ipx2Mqtt.Core.Discovery;
using Ipx2Mqtt.Core.Mqtt;
using Ipx2Mqtt.Core.State;
using Ipx2Mqtt.Ipx;
using Ipx2Mqtt.Ipx.Mapping;
using Microsoft.Extensions.Options;

namespace Ipx2Mqtt.Service;

/// <summary>
/// Glue between the IPX and MQTT. All events (IPX states, availability, MQTT connections, commands) are
/// queued and handled one at a time, so publications never race with each other.
/// </summary>
public sealed class BridgeWorker : BackgroundService
{
    private abstract record BridgeEvent;

    private sealed record StateEvent(IpxStateChange Change) : BridgeEvent;

    private sealed record AvailabilityEvent(bool Online) : BridgeEvent;

    private sealed record MqttConnectedEvent : BridgeEvent;

    private sealed record CommandEvent(string Topic, string Payload) : BridgeEvent;

    private readonly IIpxClient _ipx;
    private readonly IpxPushReceiver _push;
    private readonly IMqttPublisher _mqtt;
    private readonly AvailabilityManager _availability;
    private readonly DiscoveryBuilder _discoveryBuilder;
    private readonly DiscoveryPublisher _discovery;
    private readonly StatePublisher _states;
    private readonly EntityRegistry _registry;
    private readonly DeviceOptions _device;
    private readonly ILogger<BridgeWorker> _logger;
    private readonly MonitorStream? _monitor;
    private readonly Channel<BridgeEvent> _events = Channel.CreateUnbounded<BridgeEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly string _version =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.0.0";

    public BridgeWorker(
        IIpxClient ipx,
        IpxPushReceiver push,
        IMqttPublisher mqtt,
        AvailabilityManager availability,
        DiscoveryBuilder discoveryBuilder,
        DiscoveryPublisher discovery,
        StatePublisher states,
        EntityRegistry registry,
        IOptions<DeviceOptions> device,
        ILogger<BridgeWorker> logger,
        MonitorStream? monitor = null)
    {
        _ipx = ipx;
        _push = push;
        _mqtt = mqtt;
        _availability = availability;
        _discoveryBuilder = discoveryBuilder;
        _discovery = discovery;
        _states = states;
        _registry = registry;
        _device = device.Value;
        _logger = logger;
        _monitor = monitor;

        // Wired in the constructor (not in ExecuteAsync): ExecuteAsync may start after the other hosted services
        // have already produced events, and those must not be lost.
        _ipx.StateChanged += OnIpxState;
        _push.StateChanged += OnIpxState;
        _ipx.AvailabilityChanged += online => Post(new AvailabilityEvent(online));
        _mqtt.Connected += () =>
        {
            Post(new MqttConnectedEvent());
            return Task.CompletedTask;
        };
        _mqtt.MessageReceived += message =>
        {
            if (_registry.Commands.ContainsKey(message.Topic))
            {
                Post(new CommandEvent(message.Topic, message.Payload));
            }

            return Task.CompletedTask;
        };
        Post(new AvailabilityEvent(_ipx.IsAvailable));
    }

    private void Post(BridgeEvent e) => _events.Writer.TryWrite(e);

    private void OnIpxState(IpxStateChange change)
    {
        // The IPX publishes many objects we do not expose (clock, diagnostics): drop them early.
        if (_registry.StatesById.TryGetValue(change.Id, out var bindings))
        {
            _monitor?.Publish(MonitorKinds.IpxState, topic: bindings[0].Topic, ipxId: change.Id, payload: change.Value);
            Post(new StateEvent(change));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _discovery.HomeAssistantOnline = token => _states.RepublishAllAsync(token);
        _discovery.StaleTopicPrefix = _discoveryBuilder.AreaTopicPrefix(_device.NodeId);
        await _discovery.StartAsync(ct).ConfigureAwait(false);
        foreach (var topic in _registry.Commands.Keys)
        {
            await _mqtt.SubscribeAsync(topic, ct).ConfigureAwait(false);
        }

        await _discovery.PublishAsync(BuildDiscovery(swVersion: null), ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Bridge started: {Count} entities exposed on device '{Node}'", _registry.Entities.Count, _device.NodeId);

        _ = ReportSystemInfoAsync(ct);

        try
        {
            await foreach (var e in _events.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await HandleAsync(e, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Error while handling {Event}", e.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        finally
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _availability.SetAsync(false, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not publish offline on shutdown");
            }
        }
    }

    private async Task HandleAsync(BridgeEvent e, CancellationToken ct)
    {
        switch (e)
        {
            case StateEvent s when _registry.StatesById.TryGetValue(s.Change.Id, out var bindings):
                foreach (var binding in bindings)
                {
                    var payload = binding.ToPayload(s.Change.Value);
                    if (await _states.PublishIfChangedAsync(binding.Topic, payload, ct).ConfigureAwait(false))
                    {
                        _logger.LogDebug("{Topic} = {Payload}", binding.Topic, payload);
                    }
                }

                break;

            case AvailabilityEvent a:
                _monitor?.Publish(MonitorKinds.System, detail: a.Online ? "IPX online" : "IPX offline");
                await _availability.SetAsync(a.Online, ct).ConfigureAwait(false);
                break;

            case MqttConnectedEvent:
                _monitor?.Publish(MonitorKinds.System, detail: "MQTT connected");
                // The broker may have lost its retained messages: send everything again.
                await _discovery.RepublishCurrentAsync(ct).ConfigureAwait(false);
                await _availability.PublishCurrentAsync(ct).ConfigureAwait(false);
                await _states.RepublishAllAsync(ct).ConfigureAwait(false);
                break;

            case CommandEvent c when _registry.Commands.TryGetValue(c.Topic, out var binding):
                var command = binding.Parse(c.Payload);
                if (command is null)
                {
                    _logger.LogWarning("Ignoring unsupported payload '{Payload}' on {Topic}", c.Payload, c.Topic);
                    break;
                }

                // The state is NOT published here: it will be published when the IPX confirms it.
                _ = Task.Run(() => SendCommandAsync(command, c.Topic), ct);
                break;
        }
    }

    private async Task SendCommandAsync(IpxCommand command, string topic)
    {
        try
        {
            await _ipx.SetAsync(command.Id, command.Value, CancellationToken.None).ConfigureAwait(false);
            _monitor?.Publish(MonitorKinds.IpxCommand, topic: topic, ipxId: command.Id, payload: command.Value, ok: true);
            _logger.LogInformation("Command sent to IPX object {Id}: {Value} (from {Topic})", command.Id, command.Value, topic);
        }
        catch (Exception ex)
        {
            _monitor?.Publish(MonitorKinds.IpxCommand, topic: topic, ipxId: command.Id, payload: command.Value, ok: false, detail: ex.Message);
            _logger.LogError(ex, "Command to IPX object {Id} failed (from {Topic})", command.Id, topic);
        }
    }

    private IReadOnlyList<DiscoveryMessage> BuildDiscovery(string? swVersion) =>
        _discoveryBuilder.BuildAll(
            _device.NodeId,
            new DeviceInfo(_device.NodeId, _device.Name, _device.Manufacturer, _device.Model, swVersion),
            new OriginInfo("ipx2mqtt", _version),
            _availability.Topic,
            _registry.Components);

    /// <summary>The IPX WebSocket is not official: log version information as soon as it is reachable.</summary>
    private async Task ReportSystemInfoAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var info = await _ipx.GetSystemInfoAsync(ct).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(info))
                    {
                        _logger.LogInformation("IPX800 V5 version information: {Info}", info);
                        await _discovery.PublishAsync(BuildDiscovery(info), ct).ConfigureAwait(false);
                    }

                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "IPX version information not available yet");
                    await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}
