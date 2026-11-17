using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ipx2Mqtt.Core.Discovery;
using Ipx2Mqtt.Core.Mqtt;
using Ipx2Mqtt.Ipx;
using Microsoft.Extensions.Configuration;
using NetEscapades.Configuration.Yaml;

namespace Ipx2Mqtt.Service;

/// <summary>Reads, validates and writes config.yml and entities.yml for the web interface.</summary>
public sealed partial class ConfigEditor(string? directory)
{
    public const string Mask = "********";

    /// <summary>Settings never sent to the browser in clear text (the mask means "keep the stored value").</summary>
    private static readonly string[][] SecretPaths =
    {
        new[] { "Mqtt", "Password" },
        new[] { "Ipx", "Password" },
        new[] { "Ipx", "ApiKey" },
        new[] { "Push", "Token" },
        new[] { "Web", "Password" },
    };

    private static readonly string[] Components = { "switch", "binary_sensor", "sensor", "light", "number" };

    public string? Directory { get; } = directory;

    private string ConfigPath => Path.Combine(Directory!, ExternalConfig.FileNames[0]);
    private string EntitiesPath => Path.Combine(Directory!, ExternalConfig.FileNames[1]);

    public JsonObject Read()
    {
        var config = Directory is null ? null : ReadFile(ConfigPath) as JsonObject;
        config ??= new JsonObject();
        var entities = Directory is null ? null : ReadFile(EntitiesPath) as JsonObject;
        var list = entities?["Entities"] as JsonArray ?? new JsonArray();

        foreach (var path in SecretPaths)
        {
            if (Navigate(config, path) is { } parent && !string.IsNullOrEmpty(parent[path[^1]]?.ToString()))
            {
                parent[path[^1]] = Mask;
            }
        }

        return new JsonObject
        {
            ["directory"] = Directory,
            ["config"] = config,
            ["entities"] = JsonNode.Parse(list.ToJsonString()),
        };
    }

    /// <summary>Validates the new content and writes both files (the previous ones are kept as *.bak). Returns the errors, empty when saved.</summary>
    public IReadOnlyList<string> Save(JsonObject config, JsonArray entities)
    {
        if (Directory is null)
        {
            return new[] { "No configuration folder found: mount a folder on /config or set IPX2MQTT_CONFIG_DIR." };
        }

        // Secrets sent back as the mask keep their stored value.
        var current = ReadFile(ConfigPath) as JsonObject;
        foreach (var path in SecretPaths)
        {
            if (Navigate(config, path) is { } parent && parent[path[^1]]?.ToString() == Mask)
            {
                var stored = current is null ? null : Navigate(current, path)?[path[^1]];
                parent[path[^1]] = stored is null ? null : JsonNode.Parse(stored.ToJsonString());
            }
        }

        var entitiesDoc = new JsonObject { ["Entities"] = entities.DeepClone() };
        var configYaml = "# Written by the ipx2mqtt web interface.\n" + YamlJson.ToYaml(config);
        var entitiesYaml = "# Written by the ipx2mqtt web interface.\n" + YamlJson.ToYaml(entitiesDoc);

        var errors = Validate(configYaml, entitiesYaml);
        if (errors.Count > 0)
        {
            return errors;
        }

        Write(ConfigPath, configYaml);
        Write(EntitiesPath, entitiesYaml);
        return Array.Empty<string>();
    }

    private static List<string> Validate(string configYaml, string entitiesYaml)
    {
        var errors = new List<string>();
        IConfigurationRoot root;
        MqttOptions mqtt;
        DeviceOptions device;
        IpxOptions ipx;
        PushOptions push;
        WebOptions web;
        List<Ipx.Mapping.EntityDefinition> entities;
        try
        {
            root = new ConfigurationBuilder()
                .AddYamlStream(new MemoryStream(Encoding.UTF8.GetBytes(configYaml)))
                .AddYamlStream(new MemoryStream(Encoding.UTF8.GetBytes(entitiesYaml)))
                .Build();
            mqtt = root.GetSection(MqttOptions.SectionName).Get<MqttOptions>() ?? new();
            root.GetSection(DiscoveryOptions.SectionName).Get<DiscoveryOptions>();
            ipx = root.GetSection(IpxOptions.SectionName).Get<IpxOptions>() ?? new();
            device = root.GetSection(DeviceOptions.SectionName).Get<DeviceOptions>() ?? new();
            push = root.GetSection(PushOptions.SectionName).Get<PushOptions>() ?? new();
            web = root.GetSection(WebOptions.SectionName).Get<WebOptions>() ?? new();
            entities = root.Get<EntitiesOptions>()?.Entities ?? new();
        }
        catch (Exception ex)
        {
            return new List<string> { $"Invalid value: {ex.Message}" };
        }

        if (string.IsNullOrWhiteSpace(mqtt.Host)) errors.Add("Mqtt.Host is required.");
        if (mqtt.Port is < 1 or > 65535) errors.Add("Mqtt.Port must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(mqtt.BaseTopic)) errors.Add("Mqtt.BaseTopic is required.");
        if (!Identifier().IsMatch(device.NodeId)) errors.Add("Device.NodeId: letters, digits, '_' and '-' only.");
        if (!Uri.TryCreate(ipx.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            errors.Add("Ipx.BaseUrl must be an http:// or https:// URL.");
        }

        if (push.Port is < 1 or > 65535) errors.Add("Push.Port must be between 1 and 65535.");
        if (web.Port is < 1 or > 65535) errors.Add("Web.Port must be between 1 and 65535.");

        for (var i = 0; i < entities.Count; i++)
        {
            var e = entities[i];
            var label = $"Entities[{i + 1}] ({(string.IsNullOrWhiteSpace(e.Key) ? e.Name : e.Key)})";
            if (!string.IsNullOrWhiteSpace(e.Key))
            {
                if (!string.IsNullOrEmpty(e.Component) && e.Component is not ("switch" or "light"))
                {
                    errors.Add($"{label}: Component must be switch or light for a customised channel.");
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(e.Name)) errors.Add($"{label}: Name is required.");
            if (!Identifier().IsMatch(e.Type)) errors.Add($"{label}: Type is required (letters, digits, '_' and '-').");
            if (!Components.Contains(e.Component)) errors.Add($"{label}: Component must be one of {string.Join(", ", Components)}.");
            if (e.StateId is null && e.CommandId is null) errors.Add($"{label}: StateId or CommandId is required.");
        }

        return errors;
    }

    private static JsonNode? ReadFile(string path) =>
        File.Exists(path) ? YamlJson.Parse(File.ReadAllText(path)) : null;

    private static void Write(string path, string content)
    {
        if (File.Exists(path))
        {
            File.Copy(path, path + ".bak", overwrite: true);
        }

        var temp = path + ".tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Returns the object holding the last segment of <paramref name="path"/>, or null when a parent is missing.</summary>
    private static JsonObject? Navigate(JsonObject root, string[] path)
    {
        var node = root;
        foreach (var segment in path[..^1])
        {
            if (node[segment] is not JsonObject child)
            {
                return null;
            }

            node = child;
        }

        return node;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]+$")]
    private static partial Regex Identifier();
}
