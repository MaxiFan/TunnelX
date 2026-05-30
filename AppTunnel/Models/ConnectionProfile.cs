using System.Text.Json.Serialization;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using AppTunnel.Services;

namespace AppTunnel.Models;

public enum TunnelType
{
    L2tpIpsec,
    V2Ray,
    OpenVpn,
    SocksProxy,
    WireGuard
}

public enum ProxyProtocol
{
    Socks5,
    Http
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
    private string _openVpnConfig = "";
    private string _openVpnConfigPath = "";
    private string _openVpnUsername = "";
    private string _openVpnPassword = "";
    private string _openVpnPrivateKeyPassword = "";
    private string _wireGuardConfig = "";
    private string _wireGuardConfigPath = "";
    private ProxyProtocol _proxyProtocol = ProxyProtocol.Socks5;
    private string _proxyServerAddress = "";
    private int _proxyPort = 1080;
    private string _proxyUsername = "";
    private string _proxyPassword = "";
    private int _mixedProxyPort = 1080;
    private bool _autoTuneMtu = true;
    private bool _enableDnsOptimization = true;
    private bool _enableGameMode = false;
    private long? _lastLatencyMs;
    private string _lastLatencyLabel = "";
    private string _lastLatencyError = "";
    private bool _isLatencyTesting;
    private long? _lastServerLatencyMs;
    private string _lastServerLatencyLabel = "";
    private string _lastServerLatencyError = "";
    private bool _isServerPingTesting;

    public ConnectionProfile()
    {
        LocalizationService.Instance.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(TunnelTypeDisplay));
            OnPropertyChanged(nameof(EndpointDisplay));
            OnPropertyChanged(nameof(ReadinessText));
            OnPropertyChanged(nameof(PingButtonToolTipText));
            OnPropertyChanged(nameof(PingResultToolTipText));
            OnPropertyChanged(nameof(ReadinessColor));
            OnPropertyChanged(nameof(LatencyDisplayText));
            OnPropertyChanged(nameof(LatencyColor));
            OnPropertyChanged(nameof(ServerLatencyDisplayText));
            OnPropertyChanged(nameof(ServerLatencyColor));
        };
    }

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

    public string OpenVpnConfig
    {
        get => _openVpnConfig;
        set => SetField(ref _openVpnConfig, value);
    }

    public string OpenVpnConfigPath
    {
        get => _openVpnConfigPath;
        set => SetField(ref _openVpnConfigPath, value);
    }

    public string OpenVpnUsername
    {
        get => _openVpnUsername;
        set => SetField(ref _openVpnUsername, value);
    }

    public string OpenVpnPassword
    {
        get => _openVpnPassword;
        set => SetField(ref _openVpnPassword, value);
    }

    /// <summary>Passphrase for encrypted private key in .ovpn (OpenVPN <c>askpass</c>).</summary>
    public string OpenVpnPrivateKeyPassword
    {
        get => _openVpnPrivateKeyPassword;
        set => SetField(ref _openVpnPrivateKeyPassword, value);
    }

    public string WireGuardConfig
    {
        get => _wireGuardConfig;
        set => SetField(ref _wireGuardConfig, value);
    }

    public string WireGuardConfigPath
    {
        get => _wireGuardConfigPath;
        set => SetField(ref _wireGuardConfigPath, value);
    }

    public ProxyProtocol ProxyProtocol
    {
        get => _proxyProtocol;
        set => SetField(ref _proxyProtocol, value);
    }

    public string ProxyServerAddress
    {
        get => _proxyServerAddress;
        set => SetField(ref _proxyServerAddress, value);
    }

    public int ProxyPort
    {
        get => _proxyPort;
        set => SetField(ref _proxyPort, value);
    }

    public string ProxyUsername
    {
        get => _proxyUsername;
        set => SetField(ref _proxyUsername, value);
    }

    public string ProxyPassword
    {
        get => _proxyPassword;
        set => SetField(ref _proxyPassword, value);
    }

    [JsonPropertyName("socks5Port")]
    public int MixedProxyPort
    {
        get => _mixedProxyPort;
        set => SetField(ref _mixedProxyPort, value);
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

    [JsonIgnore]
    public string TunnelTypeDisplay => TunnelType switch
    {
        TunnelType.L2tpIpsec => "L2TP/IPsec",
        TunnelType.V2Ray => "V2Ray / Xray",
        TunnelType.OpenVpn => "OpenVPN",
        TunnelType.SocksProxy => ProxyProtocol == ProxyProtocol.Http ? "HTTP Proxy" : "SOCKS5 Proxy",
        TunnelType.WireGuard => "WireGuard",
        _ => LocalizationService.Instance.T("نامشخص")
    };

    [JsonIgnore]
    public string EndpointDisplay => TunnelType switch
    {
        TunnelType.L2tpIpsec => string.IsNullOrWhiteSpace(ServerAddress) ? LocalizationService.Instance.T("آدرس سرور وارد نشده") : ServerAddress,
        TunnelType.V2Ray => string.IsNullOrWhiteSpace(V2RayConfig)
            ? LocalizationService.Instance.T("کانفیگ وارد نشده")
            : V2RayEndpointHelper.TryExtract(V2RayConfig, out var host, out var port) && !string.IsNullOrWhiteSpace(host)
                ? $"{host}:{port}"
                : LocalizationService.Instance.T("کانفیگ آماده"),
        TunnelType.OpenVpn => string.IsNullOrWhiteSpace(OpenVpnConfigPath) ? LocalizationService.Instance.T("فایل .ovpn انتخاب نشده") : Path.GetFileName(OpenVpnConfigPath),
        TunnelType.SocksProxy => string.IsNullOrWhiteSpace(ProxyServerAddress) ? LocalizationService.Instance.T("آدرس پراکسی وارد نشده") : $"{ProxyServerAddress}:{ProxyPort}",
        TunnelType.WireGuard => string.IsNullOrWhiteSpace(WireGuardConfigPath) ? (string.IsNullOrWhiteSpace(WireGuardConfig) ? LocalizationService.Instance.T("کانفیگ WireGuard وارد نشده") : LocalizationService.Instance.T("کانفیگ WireGuard آماده")) : Path.GetFileName(WireGuardConfigPath),
        _ => ""
    };

    [JsonIgnore]
    public bool SupportsConnectionPing => ConnectionPingSupport.SupportsProfile(this);

    [JsonIgnore]
    public bool SupportsServerPing => IsReady;

    [JsonIgnore]
    public bool ShowsServerPingButton => SupportsConnectionPing;

    [JsonIgnore]
    public string PingButtonToolTipText => SupportsConnectionPing
        ? LocalizationService.Instance.T("پینگ اتصال: google (یا مقصد پینگ) از مسیر کامل کانفیگ — فقط sing-box share link")
        : LocalizationService.Instance.T("پینگ سرور: رسیدن به IP/پورت سرور (TCP/TLS/ICMP)");

    [JsonIgnore]
    public string PingResultToolTipText => SupportsConnectionPing
        ? LocalizationService.Instance.T("نتیجه پینگ اتصال از مسیر کانفیگ")
        : LocalizationService.Instance.T("نتیجه پینگ سرور (بدون عبور از تونل)");

    [JsonIgnore]
    public bool IsReady => TunnelType switch
    {
        TunnelType.L2tpIpsec => !string.IsNullOrWhiteSpace(ServerAddress),
        TunnelType.V2Ray => !string.IsNullOrWhiteSpace(V2RayConfig),
        TunnelType.OpenVpn => OpenVpnProfileAnalyzer.IsProfileReady(
            OpenVpnConfig, OpenVpnUsername, OpenVpnPassword, OpenVpnPrivateKeyPassword),
        TunnelType.SocksProxy => !string.IsNullOrWhiteSpace(ProxyServerAddress) && ProxyPort is > 0 and <= 65535,
        TunnelType.WireGuard => !string.IsNullOrWhiteSpace(WireGuardConfig),
        _ => false
    };

    [JsonIgnore]
    public string ReadinessText => IsReady ? LocalizationService.Instance.T("آماده اتصال") : LocalizationService.Instance.T("نیاز به تکمیل");

    [JsonIgnore]
    public string ReadinessColor => IsReady ? "#6CCB5F" : "#E0A020";

    [JsonIgnore]
    public long? LastLatencyMs
    {
        get => _lastLatencyMs;
        set
        {
            if (_lastLatencyMs == value) return;
            _lastLatencyMs = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LatencyDisplayText));
            OnPropertyChanged(nameof(LatencyColor));
        }
    }

    [JsonIgnore]
    public string LastLatencyLabel
    {
        get => _lastLatencyLabel;
        set
        {
            if (_lastLatencyLabel == value) return;
            _lastLatencyLabel = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LatencyDisplayText));
        }
    }

    [JsonIgnore]
    public string LastLatencyError
    {
        get => _lastLatencyError;
        set
        {
            if (_lastLatencyError == value) return;
            _lastLatencyError = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LatencyDisplayText));
            OnPropertyChanged(nameof(LatencyColor));
        }
    }

    [JsonIgnore]
    public bool IsLatencyTesting
    {
        get => _isLatencyTesting;
        set
        {
            if (_isLatencyTesting == value) return;
            _isLatencyTesting = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LatencyDisplayText));
            OnPropertyChanged(nameof(LatencyColor));
        }
    }

    [JsonIgnore]
    public string LatencyDisplayText
    {
        get
        {
            if (IsLatencyTesting)
                return LocalizationService.Instance.T("در حال تست...");

            if (LastLatencyMs.HasValue)
            {
                var tech = string.IsNullOrWhiteSpace(LastLatencyLabel) ? "" : $"{LastLatencyLabel} ";
                if (SupportsConnectionPing)
                {
                    return LocalizationService.Instance.Format(
                        "{0} {1}{2} ms",
                        LocalizationService.Instance.T("اتصال:"),
                        tech,
                        LastLatencyMs);
                }

                return $"{tech}{LastLatencyMs} ms".Trim();
            }

            if (string.IsNullOrWhiteSpace(LastLatencyError))
                return "—";

            return SupportsConnectionPing
                ? LocalizationService.Instance.Format("{0} {1}", LocalizationService.Instance.T("اتصال:"), LastLatencyError)
                : LastLatencyError;
        }
    }

    [JsonIgnore]
    public string LatencyColor
    {
        get
        {
            if (IsLatencyTesting)
                return "#E0A020";
            if (!LastLatencyMs.HasValue)
                return string.IsNullOrWhiteSpace(LastLatencyError) ? "#888888" : "#E05252";
            return LastLatencyMs.Value switch
            {
                < 200 => "#6CCB5F",
                < 500 => "#E0A020",
                _ => "#E05252"
            };
        }
    }

    public void ResetLatencyResult()
    {
        LastLatencyMs = null;
        LastLatencyLabel = "";
        LastLatencyError = "";
    }

    [JsonIgnore]
    public long? LastServerLatencyMs
    {
        get => _lastServerLatencyMs;
        set
        {
            if (_lastServerLatencyMs == value) return;
            _lastServerLatencyMs = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ServerLatencyDisplayText));
            OnPropertyChanged(nameof(ServerLatencyColor));
        }
    }

    [JsonIgnore]
    public string LastServerLatencyLabel
    {
        get => _lastServerLatencyLabel;
        set
        {
            if (_lastServerLatencyLabel == value) return;
            _lastServerLatencyLabel = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ServerLatencyDisplayText));
        }
    }

    [JsonIgnore]
    public string LastServerLatencyError
    {
        get => _lastServerLatencyError;
        set
        {
            if (_lastServerLatencyError == value) return;
            _lastServerLatencyError = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ServerLatencyDisplayText));
            OnPropertyChanged(nameof(ServerLatencyColor));
        }
    }

    [JsonIgnore]
    public bool IsServerPingTesting
    {
        get => _isServerPingTesting;
        set
        {
            if (_isServerPingTesting == value) return;
            _isServerPingTesting = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ServerLatencyDisplayText));
            OnPropertyChanged(nameof(ServerLatencyColor));
        }
    }

    [JsonIgnore]
    public string ServerLatencyDisplayText
    {
        get
        {
            if (IsServerPingTesting)
                return LocalizationService.Instance.T("در حال تست...");

            if (LastServerLatencyMs.HasValue)
            {
                var tech = string.IsNullOrWhiteSpace(LastServerLatencyLabel) ? "" : $"{LastServerLatencyLabel} ";
                return LocalizationService.Instance.Format(
                    "{0} {1}{2} ms",
                    LocalizationService.Instance.T("سرور:"),
                    tech,
                    LastServerLatencyMs);
            }

            return string.IsNullOrWhiteSpace(LastServerLatencyError)
                ? "—"
                : LocalizationService.Instance.Format("{0} {1}", LocalizationService.Instance.T("سرور:"), LastServerLatencyError);
        }
    }

    [JsonIgnore]
    public string ServerLatencyColor
    {
        get
        {
            if (IsServerPingTesting)
                return "#E0A020";
            if (!LastServerLatencyMs.HasValue)
                return string.IsNullOrWhiteSpace(LastServerLatencyError) ? "#888888" : "#E05252";
            return LastServerLatencyMs.Value switch
            {
                < 200 => "#6CCB5F",
                < 500 => "#E0A020",
                _ => "#E05252"
            };
        }
    }

    public void ResetServerPingResult()
    {
        LastServerLatencyMs = null;
        LastServerLatencyLabel = "";
        LastServerLatencyError = "";
    }

    public ServerConfig ToServerConfig() => new()
    {
        ServerAddress = ServerAddress,
        Username = Username,
        Password = Password,
        PreSharedKey = PreSharedKey,
        ConnectionName = ConnectionName,
        TunnelType = TunnelType,
        V2RayConfig = V2RayConfig,
        OpenVpnConfig = OpenVpnConfig,
        OpenVpnUsername = OpenVpnUsername,
        OpenVpnPassword = OpenVpnPassword,
        OpenVpnPrivateKeyPassword = OpenVpnPrivateKeyPassword,
        WireGuardConfig = WireGuardConfig,
        WireGuardConfigPath = WireGuardConfigPath,
        ProxyProtocol = ProxyProtocol,
        ProxyServerAddress = ProxyServerAddress,
        ProxyPort = ProxyPort,
        ProxyUsername = ProxyUsername,
        ProxyPassword = ProxyPassword,
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
        OnPropertyChanged(nameof(TunnelTypeDisplay));
        OnPropertyChanged(nameof(EndpointDisplay));
        OnPropertyChanged(nameof(IsReady));
            OnPropertyChanged(nameof(SupportsConnectionPing));
            OnPropertyChanged(nameof(SupportsServerPing));
            OnPropertyChanged(nameof(ShowsServerPingButton));
            OnPropertyChanged(nameof(PingButtonToolTipText));
            OnPropertyChanged(nameof(PingResultToolTipText));
            OnPropertyChanged(nameof(ReadinessText));
        OnPropertyChanged(nameof(ReadinessColor));
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
