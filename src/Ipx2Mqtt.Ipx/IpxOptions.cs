using Ipx2Mqtt.Ipx.Mapping;

namespace Ipx2Mqtt.Ipx;

/// <summary>Connection settings for the IPX800 V5.</summary>
public sealed class IpxOptions
{
    public const string SectionName = "Ipx";

    /// <summary>Base URL of the IPX (http://host or https://host); ws/wss is derived from it.</summary>
    public string BaseUrl { get; set; } = "http://192.168.1.54";

    /// <summary>User used for the secure login (needed by the WebSocket).</summary>
    public string Login { get; set; } = "api";

    public string Password { get; set; } = string.Empty;

    /// <summary>Optional official API key. When set, REST calls use it instead of a login token.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Expose every relay, dimmer and analog output found on the IPX, in addition to the configured Entities (which win).</summary>
    public bool AutoDiscover { get; set; } = true;

    /// <summary>
    /// Display names for auto-discovered entities, by module code: "&lt;module&gt;.&lt;channel&gt;" such as "04-8R.05",
    /// "06-DM.03" or "01-IPX.01". Channels without a label get a generic name.
    /// </summary>
    public Dictionary<string, string> Labels { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int RequestTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// The IPX is reported unavailable only after both sources have been down for this long, so that the
    /// WebSocket-to-polling hand-over does not make the availability flap.
    /// </summary>
    public int OfflineGraceSeconds { get; set; } = 5;

    /// <summary>Accept self-signed certificates (https/wss).</summary>
    public bool AllowInvalidCertificate { get; set; }

    public WebSocketSettings WebSocket { get; set; } = new();
    public PollingSettings Polling { get; set; } = new();
}

public sealed class WebSocketSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>The IPX publishes its clock every second: silence beyond this delay means a dead connection.</summary>
    public int ReceiveTimeoutSeconds { get; set; } = 30;

    public int KeepAliveSeconds { get; set; } = 15;
    public int ReconnectMinSeconds { get; set; } = 1;
    public int ReconnectMaxSeconds { get; set; } = 60;

    /// <summary>A connection lasting at least this long resets the reconnection backoff.</summary>
    public int StableAfterSeconds { get; set; } = 30;
}

public sealed class PollingSettings
{
    /// <summary>Polling period of the REST fallback (only used while the WebSocket is down).</summary>
    public int IntervalSeconds { get; set; } = 10;
}

public sealed class DeviceOptions
{
    public const string SectionName = "Device";

    /// <summary>Node id used in the discovery topic and as unique id prefix.</summary>
    public string NodeId { get; set; } = "ipx800";

    public string Name { get; set; } = "IPX800 V5";
    public string Manufacturer { get; set; } = "GCE Electronics";
    public string Model { get; set; } = "IPX800 V5";
}

public sealed class PushOptions
{
    public const string SectionName = "Push";

    /// <summary>Exposes an HTTP endpoint the IPX "Push" object can call (optional, off by default).</summary>
    public bool Enabled { get; set; }

    public int Port { get; set; } = 8080;

    /// <summary>When set, requests must carry it in the "X-Push-Token" header or "token" query parameter.</summary>
    public string? Token { get; set; }
}

/// <summary>Root-bound list of exposed entities.</summary>
public sealed class EntitiesOptions
{
    public List<EntityDefinition> Entities { get; set; } = new();
}
