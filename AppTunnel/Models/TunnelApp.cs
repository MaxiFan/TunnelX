using System.Windows.Media.Imaging;

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

    public string TrafficDisplay
    {
        get
        {
            var total = BytesSent + BytesReceived;
            return total switch
            {
                < 1024 => $"{total} B",
                < 1024 * 1024 => $"{total / 1024.0:F1} KB",
                < 1024 * 1024 * 1024 => $"{total / (1024.0 * 1024):F1} MB",
                _ => $"{total / (1024.0 * 1024 * 1024):F2} GB"
            };
        }
    }
}
