using AppTunnel.Models;

namespace AppTunnel.Services;

/// <summary>
/// Profiles that can run a pre-connect real-delay probe (a request through the config outbound).
/// A TCP/TLS ping of the server address is not a connection ping.
/// </summary>
public static class ConnectionPingSupport
{
    public static bool SupportsProfile(ConnectionProfile? profile)
    {
        if (profile == null || !profile.IsReady || profile.TunnelType != TunnelType.V2Ray)
            return false;

        return PreConnectLatencyPlan.UsesRealDelay(profile.V2RayConfig);
    }

    public static bool SupportsSingBoxShareLink(string? config)
        => PreConnectLatencyPlan.ForV2RayConfig(config) == PreConnectLatencyMode.RealDelaySingBox;
}
