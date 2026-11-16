using System.Globalization;

namespace Ipx2Mqtt.Ipx;

/// <summary>
/// Optional extra source: states pushed by the IPX "Push" object over HTTP (see README).
/// The exact request format sent by the IPX depends on the Push object configuration.
/// </summary>
public sealed class IpxPushReceiver
{
    public event Action<IpxStateChange>? StateChanged;

    /// <summary>Forwards an already parsed change.</summary>
    public void Submit(IpxStateChange change) => StateChanged?.Invoke(change);

    /// <summary>Accepts "on"/"off"/"true"/"false" as booleans, other numbers as doubles, else text.</summary>
    public bool TrySubmit(long id, string? rawValue, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            error = "Missing value.";
            return false;
        }

        object value;
        if (IpxPayloads.TryParseOnOff(rawValue, out var on) && !double.TryParse(rawValue, out _))
        {
            value = on;
        }
        else if (double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            value = number;
        }
        else
        {
            value = rawValue;
        }

        StateChanged?.Invoke(new IpxStateChange(id, value));
        return true;
    }
}
