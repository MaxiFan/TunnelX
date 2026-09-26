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

    public static bool RequiresXray(string config) => V2RayCoreSelector.RequiresXray(config);

    /// <summary>Share links that dial the server over WebSocket+TLS (bare TCP probe is misleading).</summary>
    public static bool IsWebSocketV2RayShareLink(string config) => V2RayCoreSelector.IsWebSocketV2RayShareLink(config);
}
