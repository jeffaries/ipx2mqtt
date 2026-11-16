using System.Globalization;

namespace Ipx2Mqtt.Core.Mqtt;

/// <summary>Builds topics below a configurable base prefix.</summary>
public sealed class TopicBuilder
{
    public TopicBuilder(string baseTopic)
    {
        Base = (baseTopic ?? throw new ArgumentNullException(nameof(baseTopic))).Trim('/');
        if (Base.Length == 0)
        {
            throw new ArgumentException("Base topic must not be empty.", nameof(baseTopic));
        }
    }

    public string Base { get; }

    /// <summary>Availability topic shared by the whole adapter: "&lt;base&gt;/status".</summary>
    public string Availability => Join("status");

    public string Join(params object[] parts) =>
        Base + "/" + string.Join('/', parts.Select(p => Convert.ToString(p, CultureInfo.InvariantCulture)!.Trim('/')));
}
