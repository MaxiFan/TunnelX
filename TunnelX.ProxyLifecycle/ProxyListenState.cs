namespace TunnelX.ProxyLifecycle;

/// <summary>
/// Whether a local SOCKS/HTTP listener is accepting clients.
/// </summary>
public enum ProxyListenState
{
    Disconnected,
    Connected
}
