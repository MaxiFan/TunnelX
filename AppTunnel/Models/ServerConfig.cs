namespace AppTunnel.Models;

/// <summary>
/// L2TP/IPsec server connection configuration.
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
    public bool AutoTuneMtu { get; set; } = true;
    public bool EnableDnsOptimization { get; set; } = true;
    public bool EnableGameMode { get; set; } = false;
}
