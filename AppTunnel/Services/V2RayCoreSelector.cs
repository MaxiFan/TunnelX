namespace AppTunnel.Services;

/// <summary>
/// Decides which core a V2Ray share link or JSON document must run on.
/// Kept free of process and UI dependencies so latency planning can be tested on its own.
/// </summary>
internal static class V2RayCoreSelector
{
    public static bool RequiresXray(string config)
    {
        if (string.IsNullOrWhiteSpace(config)) return false;
        config = config.Trim();

        // VMess + WebSocket early-data (?ed2053 / ?ed=) matches v2rayNG/Xray, not sing-box reliably.
        // This app runs every vmess:// link on Xray.
        if (config.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
            return true;

        // XHTTP is an Xray transport in this app. It may arrive as:
        //   - vless://...?type=xhttp
        //   - Xray JSON: streamSettings.network=xhttp / xhttpSettings
        //   - sing-box-shaped JSON: transport.type=xhttp
        if (config.Contains("xhttp", StringComparison.OrdinalIgnoreCase) ||
            config.Contains("xhttpSettings", StringComparison.OrdinalIgnoreCase))
            return true;

        // vless over WebSocket + TLS (typical share links from v2rayNG).
        if (config.StartsWith("vless://", StringComparison.OrdinalIgnoreCase) &&
            config.Contains("type=ws", StringComparison.OrdinalIgnoreCase))
            return true;

        // Explicit Xray JSON should also stay on Xray even without xhttp.
        if (config.Contains("\"streamSettings\"", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>Share links that dial the server over WebSocket+TLS (bare TCP probe is misleading).</summary>
    public static bool IsWebSocketV2RayShareLink(string config)
    {
        if (string.IsNullOrWhiteSpace(config)) return false;
        config = config.Trim();

        if (config.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
            return true;

        return config.StartsWith("vless://", StringComparison.OrdinalIgnoreCase) &&
               config.Contains("type=ws", StringComparison.OrdinalIgnoreCase);
    }
}
