using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace Ipx2Mqtt.Service;

/// <summary>Converts between YAML text and a <see cref="JsonNode"/> tree (what the web interface exchanges).</summary>
public static partial class YamlJson
{
    public static JsonNode? Parse(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return stream.Documents.Count == 0 ? null : ToJson(stream.Documents[0].RootNode);
    }

    private static JsonNode? ToJson(YamlNode node) => node switch
    {
        YamlMappingNode map => map.Children.Aggregate(new JsonObject(), (obj, pair) =>
        {
            obj[((YamlScalarNode)pair.Key).Value ?? string.Empty] = ToJson(pair.Value);
            return obj;
        }),
        YamlSequenceNode seq => new JsonArray(seq.Children.Select(ToJson).ToArray()),
        YamlScalarNode scalar => ScalarToJson(scalar),
        _ => null,
    };

    private static JsonNode? ScalarToJson(YamlScalarNode scalar)
    {
        var value = scalar.Value;
        if (scalar.Style != ScalarStyle.Plain)
        {
            return JsonValue.Create(value);
        }

        if (string.IsNullOrEmpty(value) || value is "~" or "null" or "Null" or "NULL")
        {
            return null;
        }

        if (value.Equals("true", StringComparison.OrdinalIgnoreCase)) return JsonValue.Create(true);
        if (value.Equals("false", StringComparison.OrdinalIgnoreCase)) return JsonValue.Create(false);
        if (IntegerPattern().IsMatch(value) && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
        {
            return JsonValue.Create(l);
        }

        if (DecimalPattern().IsMatch(value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            return JsonValue.Create(d);
        }

        return JsonValue.Create(value);
    }

    public static string ToYaml(JsonNode? node)
    {
        var serializer = new SerializerBuilder()
            .WithIndentedSequences()
            .WithQuotingNecessaryStrings()
            .Build();
        return serializer.Serialize(ToPlain(node) ?? new Dictionary<string, object?>());
    }

    private static object? ToPlain(JsonNode? node) => node switch
    {
        null => null,
        JsonObject obj => obj.Where(p => p.Value is not null)
            .ToDictionary(p => p.Key, p => ToPlain(p.Value), StringComparer.Ordinal),
        JsonArray arr => arr.Select(ToPlain).ToList(),
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<long>(out var l) => l,
        JsonValue v when v.TryGetValue<double>(out var d) => d,
        JsonValue v => v.ToString(),
        _ => null,
    };

    [GeneratedRegex(@"^[-+]?\d+$")]
    private static partial Regex IntegerPattern();

    [GeneratedRegex(@"^[-+]?(\d+\.\d*|\.\d+|\d+)([eE][-+]?\d+)?$")]
    private static partial Regex DecimalPattern();
}
