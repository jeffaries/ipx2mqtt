namespace Ipx2Mqtt.Core.Mqtt;

/// <summary>Connection and topic settings for the MQTT broker.</summary>
public sealed class MqttOptions
{
    public const string SectionName = "Mqtt";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool UseTls { get; set; }
    public string ClientId { get; set; } = "ipx2mqtt";

    /// <summary>Prefix of every topic published by this adapter (e.g. "ipx800").</summary>
    public string BaseTopic { get; set; } = "ipx800";

    public int KeepAliveSeconds { get; set; } = 30;
    public int ReconnectMinSeconds { get; set; } = 1;
    public int ReconnectMaxSeconds { get; set; } = 60;
}
