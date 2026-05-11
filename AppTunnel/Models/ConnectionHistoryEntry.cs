namespace AppTunnel.Models;

/// <summary>
/// Represents a single connection history entry.
/// </summary>
public class ConnectionHistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    
    /// <summary>Profile name at the time of connection.</summary>
    public string ProfileName { get; set; } = "";
    
    /// <summary>Server address.</summary>
    public string ServerAddress { get; set; } = "";
    
    /// <summary>When connection started.</summary>
    public DateTime ConnectedAt { get; set; }
    
    /// <summary>When connection ended.</summary>
    public DateTime DisconnectedAt { get; set; }
    
    /// <summary>Total duration of connection.</summary>
    public TimeSpan Duration => DisconnectedAt - ConnectedAt;
    
    /// <summary>Total bytes sent through tunnel.</summary>
    public long BytesSent { get; set; }
    
    /// <summary>Total bytes received through tunnel.</summary>
    public long BytesReceived { get; set; }
    
    /// <summary>Total data usage (sent + received).</summary>
    public long TotalBytes => BytesSent + BytesReceived;
    
    /// <summary>Formatted duration string.</summary>
    public string DurationText
    {
        get
        {
            var d = Duration;
            if (d.TotalHours >= 1)
                return $"{(int)d.TotalHours}:{d.Minutes:D2}:{d.Seconds:D2}";
            return $"{d.Minutes:D2}:{d.Seconds:D2}";
        }
    }
    
    /// <summary>Formatted date/time string (Persian-friendly).</summary>
    public string ConnectedAtText => ConnectedAt.ToString("yyyy/MM/dd HH:mm");
    
    /// <summary>Formatted total data usage.</summary>
    public string TotalDataText => FormatBytes(TotalBytes);
    
    /// <summary>Formatted sent data.</summary>
    public string SentText => FormatBytes(BytesSent);
    
    /// <summary>Formatted received data.</summary>
    public string ReceivedText => FormatBytes(BytesReceived);
    
    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F2} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }
}
