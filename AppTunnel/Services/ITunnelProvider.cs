using AppTunnel.Models;

namespace AppTunnel.Services;

/// <summary>
/// Abstraction over a tunnel transport (L2TP/IPsec, V2Ray, …).
/// VpnService acts as a dispatcher that selects the correct provider at connect time.
/// </summary>
public interface ITunnelProvider
{
    /// <summary>Establish the tunnel. Returns true on success.</summary>
    Task<bool> ConnectAsync(ServerConfig config, CancellationToken ct);

    /// <summary>Tear down the tunnel gracefully.</summary>
    Task DisconnectAsync();

    /// <summary>Live status of the connection (state, IPs, timing, …).</summary>
    ConnectionStatus Status { get; }

    /// <summary>
    /// Returns true when the underlying network interface is still operational.
    /// Used by the VPN health monitor.
    /// </summary>
    bool IsInterfaceUp();
}
