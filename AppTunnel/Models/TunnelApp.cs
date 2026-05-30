using System.Windows.Media.Imaging;
using AppTunnel.Helpers;

namespace AppTunnel.Models;

/// <summary>
/// Represents an installed application that can be routed through the tunnel.
/// </summary>
public class TunnelApp
{
    public string DisplayName { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string ExecutableName { get; set; } = string.Empty;
    public BitmapSource? Icon { get; set; }
    public bool IsEnabled { get; set; }
    public long BytesSent { get; set; }
    public long BytesReceived { get; set; }

    public string TrafficDisplay => ByteSizeFormatter.Format(BytesSent + BytesReceived);
}
