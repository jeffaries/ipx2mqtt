using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ipx2Mqtt.Core.Discovery;

/// <summary>
/// Builds Home Assistant MQTT "device" discovery messages:
/// <c>&lt;prefix&gt;/device/&lt;node_id&gt;/config</c> with <c>dev</c>, <c>o</c> and <c>cmps</c>.
/// </summary>
public sealed partial class DiscoveryBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public DiscoveryBuilder(string discoveryPrefix = "homeassistant")
    {
        Prefix = discoveryPrefix.Trim('/');
    }

    public string Prefix { get; }

    public string ConfigTopic(string nodeId)
    {
        ValidateId(nodeId, nameof(nodeId));
        return $"{Prefix}/device/{nodeId}/config";
    }

    public DiscoveryMessage Build(
        string nodeId,
        DeviceInfo device,
        OriginInfo origin,
        string availabilityTopic,
        IEnumerable<ComponentDescriptor> components)
    {
        var topic = ConfigTopic(nodeId);

        var dev = new JsonObject { ["ids"] = device.Id, ["name"] = device.Name };
        AddIfNotEmpty(dev, "mf", device.Manufacturer);
        AddIfNotEmpty(dev, "mdl", device.Model);
        AddIfNotEmpty(dev, "sw", device.SwVersion);

        var o = new JsonObject { ["name"] = origin.Name };
        AddIfNotEmpty(o, "sw", origin.SwVersion);
        AddIfNotEmpty(o, "url", origin.Url);

        var cmps = new JsonObject();
        foreach (var c in components)
        {
            if (cmps.ContainsKey(c.Key))
            {
                throw new ArgumentException($"Duplicate component key '{c.Key}'.", nameof(components));
            }

            cmps[c.Key] = BuildComponent(c);
        }

        if (cmps.Count == 0)
        {
            throw new ArgumentException("At least one component is required.", nameof(components));
        }

        var root = new JsonObject
        {
            ["dev"] = dev,
            ["o"] = o,
            // Availability is intentionally shared by all components (root level).
            ["avty_t"] = availabilityTopic,
            ["cmps"] = cmps,
        };

        return new DiscoveryMessage(topic, root.ToJsonString(JsonOptions));
    }

    /// <summary>Empty retained payload on the config topic removes the device and all its entities.</summary>
    public DiscoveryMessage BuildRemoval(string nodeId) => new(ConfigTopic(nodeId), string.Empty);

    private static JsonObject BuildComponent(ComponentDescriptor c)
    {
        var node = new JsonObject
        {
            ["p"] = c.Platform,
            ["unique_id"] = c.UniqueId,
            ["name"] = c.Name,
        };

        // Topics live on each component: root-level topics would apply to every component.
        AddIfNotEmpty(node, "state_topic", c.StateTopic);
        AddIfNotEmpty(node, "command_topic", c.CommandTopic);
        AddIfNotEmpty(node, "device_class", c.DeviceClass);
        AddIfNotEmpty(node, "unit_of_measurement", c.UnitOfMeasurement);
        AddIfNotEmpty(node, "state_class", c.StateClass);
        AddIfNotEmpty(node, "icon", c.Icon);

        foreach (var (key, value) in c.Extra)
        {
            if (value is not null)
            {
                node[key] = JsonSerializer.SerializeToNode(value);
            }
        }

        return node;
    }

    private static void AddIfNotEmpty(JsonObject node, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            node[key] = value;
        }
    }

    public static void ValidateId(string id, string paramName)
    {
        if (string.IsNullOrEmpty(id) || !IdRegex().IsMatch(id))
        {
            throw new ArgumentException(
                $"'{id}' is not a valid identifier: only letters, digits, underscore and hyphen are allowed.", paramName);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex IdRegex();
}
