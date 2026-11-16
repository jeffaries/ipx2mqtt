namespace Ipx2Mqtt.Ipx;

/// <summary>In-memory <see cref="IIpxClient"/> for tests.</summary>
public sealed class FakeIpxClient : IIpxClient
{
    private readonly Dictionary<long, IpxObject> _objects = new();
    private readonly object _gate = new();
    private bool _available;

    public bool IsAvailable => _available;

    private readonly List<IpxCommand> _commands = new();

    /// <summary>Snapshot of the commands received through <see cref="SetAsync"/>.</summary>
    public IReadOnlyList<IpxCommand> Commands
    {
        get
        {
            lock (_gate)
            {
                return _commands.ToArray();
            }
        }
    }

    /// <summary>Optional: state id that confirms a command id (the fake then emits the new state).</summary>
    public Func<long, long?>? StateIdForCommand { get; set; }

    public string? SystemInfo { get; set; } = "fake";

    public event Action<IpxStateChange>? StateChanged;
    public event Action<bool>? AvailabilityChanged;

    public async Task RunAsync(CancellationToken ct)
    {
        SetAvailable(true);
        try
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // stopped
        }
    }

    public Task<IReadOnlyList<IpxObject>> GetFullStateAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<IpxObject>>(_objects.Values.ToList());
        }
    }

    public Task SetAsync(long id, object value, CancellationToken ct)
    {
        lock (_gate)
        {
            _commands.Add(new IpxCommand(id, value));
        }

        if (StateIdForCommand?.Invoke(id) is { } stateId)
        {
            Emit(stateId, value);
        }

        return Task.CompletedTask;
    }

    public Task<string?> GetSystemInfoAsync(CancellationToken ct) => Task.FromResult(SystemInfo);

    public void SetAvailable(bool available)
    {
        if (_available == available)
        {
            return;
        }

        _available = available;
        AvailabilityChanged?.Invoke(available);
    }

    /// <summary>Records the value and raises <see cref="StateChanged"/> (like a WebSocket message).</summary>
    public void Emit(long id, object value)
    {
        lock (_gate)
        {
            var kind = IpxIdRanges.KindOf(id) ?? IpxObjectKind.Io;
            _objects[id] = new IpxObject(id, null, kind, value);
        }

        StateChanged?.Invoke(new IpxStateChange(id, value));
    }
}
