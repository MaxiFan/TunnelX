namespace AppTunnel.Models;

/// <summary>How an OpenVPN profile reaches its server before the tunnel is up.</summary>
public enum OpenVpnUpstreamProxyKind
{
    None = 0,
    Http = 1,
    Socks = 2
}
