namespace Ipx2Mqtt.Ipx;

/// <summary>Everything the bridge needs from the IPX, whatever the transport.</summary>
public interface IIpxClient
{
    /// <summary>True when at least one source (WebSocket or REST polling) is working.</summary>
    bool IsAvailable { get; }

    /// <summary>Raised for each state change; handlers must not block.</summary>
    event Action<IpxStateChange>? StateChanged;

    /// <summary>Raised when <see cref="IsAvailable"/> changes.</summary>
    event Action<bool>? AvailabilityChanged;

    /// <summary>Maintains the connection(s) until cancelled.</summary>
    Task RunAsync(CancellationToken ct);

    /// <summary>Reads the complete current state.</summary>
    Task<IReadOnlyList<IpxObject>> GetFullStateAsync(CancellationToken ct);

    /// <summary>Sends a command. The resulting state must be confirmed through <see cref="StateChanged"/>.</summary>
    Task SetAsync(long id, object value, CancellationToken ct);

    /// <summary>Version information, when the IPX exposes it (null otherwise).</summary>
    Task<string?> GetSystemInfoAsync(CancellationToken ct);
}
