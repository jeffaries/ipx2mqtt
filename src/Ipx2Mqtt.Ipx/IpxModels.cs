using System.Globalization;

namespace Ipx2Mqtt.Ipx;

public enum IpxObjectKind
{
    Io,
    Ana,
    Str,
}

/// <summary>An IPX object as returned by the REST API (value is bool, double or string).</summary>
public sealed record IpxObject(long Id, string? Name, IpxObjectKind Kind, object Value, int Decimals = 0, string? Unit = null);

/// <summary>A state change (WebSocket message, REST snapshot entry or push call).</summary>
public sealed record IpxStateChange(long Id, object Value);

/// <summary>A command to send to an IPX object.</summary>
public sealed record IpxCommand(long Id, object Value);

/// <summary>IPX object ids are allocated in fixed ranges by object type.</summary>
public static class IpxIdRanges
{
    public static IpxObjectKind? KindOf(long id) => id switch
    {
        >= 0x10000 and <= 0x1FFFF => IpxObjectKind.Io,
        >= 0x30000 and <= 0x5FFFF => IpxObjectKind.Ana,
        >= 0x80000 and <= 0xDFFFF => IpxObjectKind.Str,
        _ => null,
    };
}

/// <summary>Conversions between IPX values and MQTT payloads.</summary>
public static class IpxPayloads
{
    public const string On = "ON";
    public const string Off = "OFF";

    public static string OnOff(object value, bool invert = false)
    {
        var on = value switch
        {
            bool b => b,
            double d => d != 0,
            long l => l != 0,
            int i => i != 0,
            string s => TryParseOnOff(s, out var parsed) && parsed,
            _ => false,
        };
        return on ^ invert ? On : Off;
    }

    public static string Number(object value, int? decimals = null) => value switch
    {
        double d => FormatDouble(d, decimals),
        long l => l.ToString(CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "1" : "0",
        string s => s,
        _ => string.Empty,
    };

    public static bool TryParseOnOff(string text, out bool on)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "on" or "true" or "1":
                on = true;
                return true;
            case "off" or "false" or "0":
                on = false;
                return true;
            default:
                on = false;
                return false;
        }
    }

    private static string FormatDouble(double value, int? decimals)
    {
        if (decimals is { } d)
        {
            value = Math.Round(value, Math.Clamp(d, 0, 15), MidpointRounding.AwayFromZero);
        }

        return value.ToString("0.###############", CultureInfo.InvariantCulture);
    }
}
