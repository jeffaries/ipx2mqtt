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
        AddIfNotEmpty(dev, "sa", device.SuggestedArea);
        AddIfNotEmpty(dev, "via_device", device.ViaDevice);

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

    /// <summary>Node id of the device grouping the entities of an area.</summary>
    public static string AreaNodeId(string nodeId, string area) => $"{nodeId}_area_{Slug(area)}";

    /// <summary>Topic prefix of the area devices of a node (all their config topics start with it).</summary>
    public string AreaTopicPrefix(string nodeId) => $"{Prefix}/device/{nodeId}_area_";

    /// <summary>
    /// Home Assistant has no area on an entity, only on a device. Components without area form the main device (as
    /// <see cref="Build"/>); the components of each area form their own device, named after the area, with
    /// <c>sa</c> set and linked to the main one (<c>via_device</c>). The main message is an empty removal when every
    /// component has an area, so that a previously published main device does not keep the entities.
    /// </summary>
    public IReadOnlyList<DiscoveryMessage> BuildAll(
        string nodeId,
        DeviceInfo device,
        OriginInfo origin,
        string availabilityTopic,
        IEnumerable<ComponentDescriptor> components)
    {
        var all = components.ToList();
        var main = all.Where(c => string.IsNullOrWhiteSpace(c.Area)).ToList();
        var areas = all.Where(c => !string.IsNullOrWhiteSpace(c.Area)).GroupBy(c => Slug(c.Area!)).ToList();

        var messages = new List<DiscoveryMessage>
        {
            main.Count > 0 || areas.Count == 0
                ? Build(nodeId, device, origin, availabilityTopic, main.Count > 0 ? main : all)
                : BuildRemoval(nodeId),
        };

        foreach (var group in areas)
        {
            var area = group.First().Area!.Trim();
            var areaId = AreaNodeId(nodeId, area);
            var areaDevice = device with
            {
                Id = areaId,
                Name = $"{device.Name} - {area}",
                SuggestedArea = area,
                ViaDevice = device.Id,
            };
            messages.Add(Build(areaId, areaDevice, origin, availabilityTopic, group));
        }

        return messages;
    }

    private static string Slug(string text)
    {
        var chars = text.Normalize(System.Text.NormalizationForm.FormD)
            .Where(ch => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .Select(ch => char.IsAsciiLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_');
        var slug = Regex.Replace(new string(chars.ToArray()), "_+", "_").Trim('_');
        return slug.Length == 0 ? "area" : slug;
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
