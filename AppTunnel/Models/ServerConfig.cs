namespace AppTunnel.Models;

/// <summary>
/// Server connection configuration (L2TP/IPsec, V2Ray, OpenVPN, Proxy, WireGuard).
/// </summary>
public class ServerConfig
{
    public string ServerAddress { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string PreSharedKey { get; set; } = string.Empty;
    public string ConnectionName { get; set; } = "TunnelX";
    public TunnelType TunnelType { get; set; } = TunnelType.L2tpIpsec;
    public string V2RayConfig { get; set; } = "";
    public string OpenVpnConfig { get; set; } = "";
    public string OpenVpnExePath { get; set; } = "";
    public string OpenVpnUsername { get; set; } = "";
    public string OpenVpnPassword { get; set; } = "";
    public string OpenVpnPrivateKeyPassword { get; set; } = "";
    public OpenVpnUpstreamProxyKind OpenVpnUpstreamProxyKind { get; set; } = OpenVpnUpstreamProxyKind.None;
    public string OpenVpnUpstreamProxyHost { get; set; } = "";
    public int OpenVpnUpstreamProxyPort { get; set; }
    public string OpenVpnUpstreamProxyUsername { get; set; } = "";
    public string OpenVpnUpstreamProxyPassword { get; set; } = "";
    public string WireGuardConfig { get; set; } = "";
    public string WireGuardConfigPath { get; set; } = "";
    public ProxyProtocol ProxyProtocol { get; set; } = ProxyProtocol.Socks5;
    public string ProxyServerAddress { get; set; } = "";
    public int ProxyPort { get; set; } = 1080;
    public string ProxyUsername { get; set; } = "";
    public string ProxyPassword { get; set; } = "";
    public bool AutoTuneMtu { get; set; } = true;
    public bool EnableDnsOptimization { get; set; } = true;
    public bool EnableGameMode { get; set; } = false;

    public string BuildProxyUri()
    {
        var scheme = ProxyProtocol == ProxyProtocol.Http ? "http" : "socks5";
        var port = ProxyPort > 0 ? ProxyPort : (ProxyProtocol == ProxyProtocol.Http ? 3128 : 1080);
        var auth = string.IsNullOrWhiteSpace(ProxyUsername)
            ? ""
            : $"{Uri.EscapeDataString(ProxyUsername)}:{Uri.EscapeDataString(ProxyPassword ?? "")}@";

        return $"{scheme}://{auth}{ProxyServerAddress.Trim()}:{port}#TunnelX-Proxy";
    }
}
