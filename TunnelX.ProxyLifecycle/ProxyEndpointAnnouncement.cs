namespace TunnelX.ProxyLifecycle;

/// <summary>
/// Stable text announced when a local proxy endpoint drops or comes back.
/// The endpoint id is <c>host:port</c> (the same <c>127.0.0.1:port</c> form
/// TunnelX already shows for its listening proxy).
/// </summary>
public static class ProxyEndpointAnnouncement
{
    public static bool TryFormat(string? host, int port, ProxyListenState state, out string endpoint, out string line)
    {
        endpoint = "";
        line = "";
        if (port is <= 0 or > 65535)
            return false;
        if (string.IsNullOrWhiteSpace(host))
            return false;

        var verb = state == ProxyListenState.Connected ? "connected" : "disconnected";
        endpoint = $"{host.Trim()}:{port}";
        line = $"{endpoint} is now {verb}";
        return true;
    }
}
