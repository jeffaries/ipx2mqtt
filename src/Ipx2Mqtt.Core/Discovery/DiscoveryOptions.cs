namespace Ipx2Mqtt.Core.Discovery;

public sealed class DiscoveryOptions
{
    public const string SectionName = "Discovery";

    public string Prefix { get; set; } = "homeassistant";

    /// <summary>Home Assistant birth topic; republish discovery when it receives <see cref="BirthPayload"/>.</summary>
    public string StatusTopic { get; set; } = "homeassistant/status";

    public string BirthPayload { get; set; } = "online";

    /// <summary>Random delay before republishing after the birth message.</summary>
    public int RepublishDelayMinMs { get; set; } = 1000;

    public int RepublishDelayMaxMs { get; set; } = 5000;
}
