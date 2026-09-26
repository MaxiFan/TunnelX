namespace AppTunnel.Services;

internal enum PreConnectLatencyMode
{
    /// <summary>Temporary sing-box mixed inbound, then a real request through the outbound.</summary>
    RealDelaySingBox,

    /// <summary>Temporary Xray SOCKS inbound, then a real request through the outbound.</summary>
    RealDelayXray,

    /// <summary>
    /// No real-delay probe is available. Callers must not substitute a TCP/TLS/ICMP ping of the
    /// server address for V2Ray configs — that reports success for dead proxies.
    /// </summary>
    Unsupported
}

/// <summary>
/// Chooses the pre-connect latency strategy. V2Ray/Xray configs use a real-delay probe through
/// the outbound (v2rayN "test real delay"). A reachable server port is not treated as success.
/// </summary>
internal static class PreConnectLatencyPlan
{
    public static PreConnectLatencyMode ForV2RayConfig(string? config)
    {
        if (string.IsNullOrWhiteSpace(config))
            return PreConnectLatencyMode.Unsupported;

        config = config.Trim();
        if (config.StartsWith('{'))
        {
            // Xray JSON can be started as a SOCKS probe. Other full documents (sing-box JSON)
            // cannot, and must not fall back to pinging the server IP.
            return V2RayCoreSelector.RequiresXray(config)
                ? PreConnectLatencyMode.RealDelayXray
                : PreConnectLatencyMode.Unsupported;
        }

        if (V2RayCoreSelector.RequiresXray(config))
            return PreConnectLatencyMode.RealDelayXray;

        if (IsSingBoxShareLink(config))
            return PreConnectLatencyMode.RealDelaySingBox;

        return PreConnectLatencyMode.Unsupported;
    }

    public static bool UsesRealDelay(string? config)
    {
        var mode = ForV2RayConfig(config);
        return mode is PreConnectLatencyMode.RealDelaySingBox or PreConnectLatencyMode.RealDelayXray;
    }

    private static bool IsSingBoxShareLink(string config)
    {
        return config.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)
               || config.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase)
               || config.StartsWith("ss://", StringComparison.OrdinalIgnoreCase)
               || config.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase)
               || config.StartsWith("socks://", StringComparison.OrdinalIgnoreCase)
               || config.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
    }
}
