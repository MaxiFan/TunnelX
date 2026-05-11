namespace AppTunnel.Models;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Disconnecting,
    Error
}

public class ConnectionStatus
{
    public ConnectionState State { get; set; } = ConnectionState.Disconnected;
    public string Message { get; set; } = "آماده اتصال";
    public DateTime? ConnectedSince { get; set; }
    public string VpnLocalIp { get; set; } = string.Empty;
    public string VpnServerIp { get; set; } = string.Empty;
    public int VpnInterfaceIndex { get; set; } = -1;

    /// <summary>
    /// Port of the sing-box mixed (SOCKS5/HTTP) inbound, used for accurate
    /// end-to-end ping measurement in V2Ray mode.
    /// 0 means not applicable (L2TP mode).
    /// </summary>
    public int SingBoxMixedPort { get; set; } = 0;

    public string Duration
    {
        get
        {
            if (ConnectedSince == null) return "--:--:--";
            var elapsed = DateTime.Now - ConnectedSince.Value;
            return $"{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
        }
    }
}
