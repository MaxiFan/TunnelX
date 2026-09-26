using System.Text.Json;

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
            // Bare hysteria outbounds can be wrapped for a sing-box real-delay probe.
            if (IsBareHysteriaJson(config))
                return PreConnectLatencyMode.RealDelaySingBox;

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
        return config.StartsWith("hysteria2://", StringComparison.OrdinalIgnoreCase)
               || config.StartsWith("hy2://", StringComparison.OrdinalIgnoreCase)
               || config.StartsWith("hysteria://", StringComparison.OrdinalIgnoreCase)
               || config.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)
               || config.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase)
               || config.StartsWith("ss://", StringComparison.OrdinalIgnoreCase)
               || config.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase)
               || config.StartsWith("socks://", StringComparison.OrdinalIgnoreCase)
               || config.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Mirrors <see cref="HysteriaShareLink.TryCreateOutbound"/> eligibility without pulling that
    /// type into LatencyTests (which only links a small AppTunnel subset).
    /// </summary>
    private static bool IsBareHysteriaJson(string config)
    {
        try
        {
            using var doc = JsonDocument.Parse(config);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            if (root.TryGetProperty("inbounds", out var inbounds) &&
                inbounds.ValueKind == JsonValueKind.Array &&
                inbounds.GetArrayLength() > 0)
                return false;

            if (IsHysteriaType(TryGetString(root, "type")) &&
                !string.IsNullOrWhiteSpace(TryGetString(root, "server")))
                return true;

            if (!root.TryGetProperty("outbounds", out var outbounds) ||
                outbounds.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var item in outbounds.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                if (IsHysteriaType(TryGetString(item, "type")) &&
                    !string.IsNullOrWhiteSpace(TryGetString(item, "server")))
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsHysteriaType(string? type) =>
        type != null &&
        (type.Equals("hysteria", StringComparison.OrdinalIgnoreCase) ||
         type.Equals("hysteria2", StringComparison.OrdinalIgnoreCase));

    private static string? TryGetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
