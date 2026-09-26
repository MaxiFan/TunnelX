namespace TunnelX.ProxyLifecycle;

/// <summary>
/// Publishes a local-proxy lifecycle line once per state change.
/// Repeated calls with the same endpoint and state are suppressed so a
/// reconnect is observable as disconnected, then connected.
/// </summary>
public sealed class ProxyLifecycleAnnouncer
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ProxyListenState> _last = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string> _publish;

    public ProxyLifecycleAnnouncer(Action<string> publish)
    {
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
    }

    /// <summary>
    /// Publishes <c>{host}:{port} is now connected|disconnected</c> when the
    /// state changes. Returns the line, or null when it was suppressed.
    /// </summary>
    public string? Announce(string? host, int port, ProxyListenState state)
    {
        if (!ProxyEndpointAnnouncement.TryFormat(host, port, state, out var endpoint, out var line))
            return null;

        lock (_gate)
        {
            if (_last.TryGetValue(endpoint, out var previous) && previous == state)
                return null;
            _last[endpoint] = state;
            _publish(line);
        }

        return line;
    }
}
