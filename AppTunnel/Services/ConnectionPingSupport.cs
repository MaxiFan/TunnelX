using AppTunnel.Models;

namespace AppTunnel.Services;

/// <summary>
/// Profiles that can run pre-connect connection ping (SOCKS probe through the config path).
/// Extend here when Xray/JSON and other tunnel types gain probe support.
/// </summary>
public static class ConnectionPingSupport
{
    public static bool SupportsProfile(ConnectionProfile? profile)
    {
        if (profile == null || !profile.IsReady)
            return false;

        return profile.TunnelType switch
        {
            TunnelType.V2Ray => SupportsSingBoxShareLink(profile.V2RayConfig),
            _ => false
        };
    }

    public static bool SupportsSingBoxShareLink(string? config)
    {
        if (string.IsNullOrWhiteSpace(config))
            return false;

        config = config.Trim();
        if (config.StartsWith('{'))
            return false;

        if (TunnelProviderFactory.RequiresXray(config))
            return false;

        return config.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)
               || config.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase)
               || config.StartsWith("ss://", StringComparison.OrdinalIgnoreCase)
               || config.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase)
               || config.StartsWith("socks://", StringComparison.OrdinalIgnoreCase)
               || config.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
    }
}
