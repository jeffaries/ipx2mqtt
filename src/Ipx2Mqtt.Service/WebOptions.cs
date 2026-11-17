namespace Ipx2Mqtt.Service;

/// <summary>Built-in web interface used to edit config.yml and entities.yml.</summary>
public sealed class WebOptions
{
    public const string SectionName = "Web";

    public bool Enabled { get; set; } = true;

    public int Port { get; set; } = 8080;

    /// <summary>When Password is set, the interface asks for HTTP Basic credentials (Username defaults to "admin").</summary>
    public string? Username { get; set; }

    public string? Password { get; set; }
}
