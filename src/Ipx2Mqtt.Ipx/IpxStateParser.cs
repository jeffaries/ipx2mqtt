using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Ipx2Mqtt.Ipx;

/// <summary>Tolerant parsers for IPX JSON: anything unexpected is counted and ignored, never thrown.</summary>
public static class IpxStateParser
{
    /// <summary>
    /// Parses a WebSocket text frame: <c>{"_id":65638,"on":false}</c> or <c>{"_id":196618,"value":33}</c>
    /// (an array of such objects is accepted too).
    /// </summary>
    public static IReadOnlyList<IpxStateChange> ParseMessage(string text, IpxDiagnostics diagnostics, ILogger? logger = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var changes = new List<IpxStateChange>();

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in root.EnumerateArray())
                {
                    AddOrReject(element, changes, diagnostics, logger);
                }
            }
            else
            {
                AddOrReject(root, changes, diagnostics, logger);
            }

            return changes;
        }
        catch (JsonException)
        {
            diagnostics.RecordRejected("malformed JSON", text, logger);
            return Array.Empty<IpxStateChange>();
        }
    }

    /// <summary>Parses the array returned by GET /api/core/io.</summary>
    public static IReadOnlyList<IpxObject> ParseIoObjects(JsonElement array, IpxDiagnostics diagnostics, ILogger? logger = null) =>
        ParseObjects(array, IpxObjectKind.Io, diagnostics, logger);

    /// <summary>Parses the array returned by GET /api/core/ana.</summary>
    public static IReadOnlyList<IpxObject> ParseAnaObjects(JsonElement array, IpxDiagnostics diagnostics, ILogger? logger = null) =>
        ParseObjects(array, IpxObjectKind.Ana, diagnostics, logger);

    private static IReadOnlyList<IpxObject> ParseObjects(JsonElement array, IpxObjectKind kind, IpxDiagnostics diagnostics, ILogger? logger)
    {
        var result = new List<IpxObject>();
        if (array.ValueKind != JsonValueKind.Array)
        {
            diagnostics.RecordRejected($"{kind} snapshot is not an array", array.GetRawText(), logger);
            return result;
        }

        foreach (var element in array.EnumerateArray())
        {
            if (!TryParseChange(element, out var change))
            {
                diagnostics.RecordRejected($"invalid {kind} object", element.GetRawText(), logger);
                continue;
            }

            string? name = element.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            string? unit = element.TryGetProperty("unit", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
            var decimals = element.TryGetProperty("nbdecimal", out var d) && d.TryGetInt32(out var di) ? di : 0;
            result.Add(new IpxObject(change.Id, name, kind, change.Value, decimals, unit));
        }

        return result;
    }

    private static void AddOrReject(JsonElement element, List<IpxStateChange> changes, IpxDiagnostics diagnostics, ILogger? logger)
    {
        if (TryParseChange(element, out var change))
        {
            changes.Add(change);
        }
        else
        {
            diagnostics.RecordRejected("unknown message shape", element.GetRawText(), logger);
        }
    }

    internal static bool TryParseChange(JsonElement element, out IpxStateChange change)
    {
        change = default!;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("_id", out var idElement) ||
            idElement.ValueKind != JsonValueKind.Number ||
            !idElement.TryGetInt64(out var id))
        {
            return false;
        }

        if (element.TryGetProperty("on", out var on) && on.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            change = new IpxStateChange(id, on.GetBoolean());
            return true;
        }

        if (element.TryGetProperty("value", out var value))
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Number:
                    change = new IpxStateChange(id, value.GetDouble());
                    return true;
                case JsonValueKind.String:
                    change = new IpxStateChange(id, value.GetString() ?? string.Empty);
                    return true;
                case JsonValueKind.True or JsonValueKind.False:
                    change = new IpxStateChange(id, value.GetBoolean());
                    return true;
            }
        }

        return false;
    }
}
