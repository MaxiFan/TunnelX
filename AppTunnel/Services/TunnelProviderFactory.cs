namespace AppTunnel.Services;

public static class TunnelProviderFactory
{
    public static ITunnelProvider Create(string config)
    {
        config = config.Trim();

        if (RequiresXray(config))
        {
            Logger.Info("[CORE] Using Xray-core");
            return new XrayTunnelProvider();
        }

        Logger.Info("[CORE] Using sing-box");
        return new V2RayTunnelProvider();
    }

    public static bool RequiresXray(string config)
    {
        if (string.IsNullOrWhiteSpace(config)) return false;

        // XHTTP is an Xray transport in this app. It may arrive as:
        //   - vless://...?type=xhttp
        //   - Xray JSON: streamSettings.network=xhttp / xhttpSettings
        //   - sing-box-shaped JSON: transport.type=xhttp
        if (config.Contains("xhttp", StringComparison.OrdinalIgnoreCase) ||
            config.Contains("xhttpSettings", StringComparison.OrdinalIgnoreCase))
            return true;

        // Explicit Xray JSON should also stay on Xray even without xhttp.
        if (config.Contains("\"streamSettings\"", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}
