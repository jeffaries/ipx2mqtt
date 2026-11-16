namespace Ipx2Mqtt.Core.Discovery;

/// <summary>Device block (<c>dev</c>) shared by all components of a discovery message.</summary>
public sealed record DeviceInfo(string Id, string Name, string? Manufacturer = null, string? Model = null, string? SwVersion = null);

/// <summary>Origin block (<c>o</c>): the software that publishes the discovery message.</summary>
public sealed record OriginInfo(string Name, string? SwVersion = null, string? Url = null);

/// <summary>One entry of <c>cmps</c>. State and command topics are kept per component.</summary>
public sealed class ComponentDescriptor
{
    /// <summary>Key of the component inside <c>cmps</c>.</summary>
    public required string Key { get; init; }

    /// <summary>Platform (<c>p</c>): switch, binary_sensor, sensor, light...</summary>
    public required string Platform { get; init; }

    public required string UniqueId { get; init; }
    public required string Name { get; init; }
    public string? StateTopic { get; init; }
    public string? CommandTopic { get; init; }
    public string? DeviceClass { get; init; }
    public string? UnitOfMeasurement { get; init; }
    public string? StateClass { get; init; }
    public string? Icon { get; init; }

    /// <summary>Additional platform-specific keys written as-is (e.g. brightness_command_topic).</summary>
    public IReadOnlyDictionary<string, object?> Extra { get; init; } = new Dictionary<string, object?>();
}

public sealed record DiscoveryMessage(string Topic, string Payload);
