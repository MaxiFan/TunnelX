using System.Text.Json.Serialization;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AppTunnel.Models;

public enum TunnelType
{
    L2tpIpsec,
    V2Ray
}

/// <summary>
/// A named connection profile containing server settings and selected applications.
/// Multiple profiles can be saved and quickly switched between.
/// </summary>
public class ConnectionProfile : INotifyPropertyChanged
{
    private string _id = Guid.NewGuid().ToString("N")[..8];
    private string _name = "پروفایل جدید";
    private DateTime _createdAt = DateTime.Now;
    private DateTime _lastUsedAt = DateTime.Now;
    private string _serverAddress = string.Empty;
    private string _username = string.Empty;
    private string _password = string.Empty;
    private string _preSharedKey = string.Empty;
    private List<ProfileApp> _tunnelApps = new();
    private List<string> _excludedDestinations = new();
    private TunnelType _tunnelType = TunnelType.L2tpIpsec;
    private string _v2RayConfig = "";
    private int _socks5Port = 1080;
    private bool _autoTuneMtu = true;
    private bool _enableDnsOptimization = true;
    private bool _enableGameMode = false;

    public string Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public DateTime CreatedAt
    {
        get => _createdAt;
        set => SetField(ref _createdAt, value);
    }

    public DateTime LastUsedAt
    {
        get => _lastUsedAt;
        set => SetField(ref _lastUsedAt, value);
    }

    // Server configuration
    public string ServerAddress
    {
        get => _serverAddress;
        set => SetField(ref _serverAddress, value);
    }

    public string Username
    {
        get => _username;
        set => SetField(ref _username, value);
    }

    public string Password
    {
        get => _password;
        set => SetField(ref _password, value);
    }

    public string PreSharedKey
    {
        get => _preSharedKey;
        set => SetField(ref _preSharedKey, value);
    }

    // Selected apps for this profile
    public List<ProfileApp> TunnelApps
    {
        get => _tunnelApps;
        set => SetField(ref _tunnelApps, value);
    }

    /// <summary>
    /// Domains or IPs that should bypass the tunnel even for target apps.
    /// </summary>
    public List<string> ExcludedDestinations
    {
        get => _excludedDestinations;
        set => SetField(ref _excludedDestinations, value);
    }

    public TunnelType TunnelType
    {
        get => _tunnelType;
        set => SetField(ref _tunnelType, value);
    }

    public string V2RayConfig
    {
        get => _v2RayConfig;
        set => SetField(ref _v2RayConfig, value);
    }

    public int Socks5Port
    {
        get => _socks5Port;
        set => SetField(ref _socks5Port, value);
    }

    public bool AutoTuneMtu
    {
        get => _autoTuneMtu;
        set => SetField(ref _autoTuneMtu, value);
    }

    public bool EnableDnsOptimization
    {
        get => _enableDnsOptimization;
        set => SetField(ref _enableDnsOptimization, value);
    }

    public bool EnableGameMode
    {
        get => _enableGameMode;
        set => SetField(ref _enableGameMode, value);
    }

    [JsonIgnore]
    public string ConnectionName => $"TunnelX-{Id}";

    public ServerConfig ToServerConfig() => new()
    {
        ServerAddress = ServerAddress,
        Username = Username,
        Password = Password,
        PreSharedKey = PreSharedKey,
        ConnectionName = ConnectionName,
        TunnelType = TunnelType,
        V2RayConfig = V2RayConfig,
        AutoTuneMtu = AutoTuneMtu,
        EnableDnsOptimization = EnableDnsOptimization,
        EnableGameMode = EnableGameMode
    };

    public override string ToString() => Name;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

public class ProfileApp
{
    public string DisplayName { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string ExecutableName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
}
