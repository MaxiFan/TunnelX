using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using Application = System.Windows.Application;
using System.Windows.Threading;
using AppTunnel.Models;
using AppTunnel.Services;

namespace AppTunnel.ViewModels;

public partial class MainViewModel : INotifyPropertyChanged
{
    private readonly VpnService _vpnService = new();
    private readonly TrafficRouterService _trafficRouter = new();
    private readonly ProfileService _profileService = new();
    private readonly HistoryService _historyService = new();
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _saveDebounceTimer;
    private CancellationTokenSource? _connectionCts;
    private DateTime _connectionStartTime;

    public MainViewModel()
    {
        ConnectCommand = new RelayCommand(_ =>
        {
            // Fire-and-forget safety: catch exceptions to avoid unobserved task exceptions
            _ = ToggleConnectionAsync().ContinueWith(t =>
            {
                if (t.IsFaulted)
                    Application.Current?.Dispatcher.Invoke(() =>
                        StatusText = $"خطا: {t.Exception?.InnerException?.Message}");
            }, TaskScheduler.Default);
        }, _ => !IsBusy);
        AddAppCommand = new RelayCommand(_ => AddCustomApp());
        RemoveAppCommand = new RelayCommand(RemoveApp);
        ToggleAppCommand = new RelayCommand(ToggleApp);
        RefreshAppsCommand = new RelayCommand(_ => LoadInstalledApps(), _ => !IsBusy);

        // Profile commands
        NewProfileCommand = new RelayCommand(_ => CreateNewProfile(), _ => !IsConnected);
        DeleteProfileCommand = new RelayCommand(_ => DeleteCurrentProfile(), _ => !IsConnected && Profiles.Count > 1);
        DuplicateProfileCommand = new RelayCommand(_ => DuplicateCurrentProfile(), _ => !IsConnected);

        // History command
        ClearHistoryCommand = new RelayCommand(_ => ClearHistory());

        // Exclude list commands
        AddExcludeCommand = new RelayCommand(_ => AddExcludedDestination());
        RemoveExcludeCommand = new RelayCommand(RemoveExcludedDestination);

        // Include list commands
        AddIncludeCommand = new RelayCommand(_ => AddIncludedDestination());
        RemoveIncludeCommand = new RelayCommand(RemoveIncludedDestination);

        // Ping command
        TogglePingCommand = new RelayCommand(_ => TogglePing(), _ => IsConnected);
        TestServerPingCommand = new RelayCommand(_ => _ = TestServerPingAsync(), _ => !IsConnected && !IsTestingServerPing);
        PasteConfigCommand = new RelayCommand(_ => PasteConfigFromClipboard(), _ => !IsConnected && CurrentTunnelType == TunnelType.V2Ray);
        ClearConfigCommand = new RelayCommand(_ => SelectedV2RayConfig = "", _ => !IsConnected && CurrentTunnelType == TunnelType.V2Ray);
        OpenGitHubCommand = new RelayCommand(_ => OpenExternalLink(AppInfo.GitHubUrl));
        OpenDonateCommand = new RelayCommand(_ => OpenExternalLink(AppInfo.PayPalDonateUrl));

        _trafficRouter.TrafficUpdated += OnTrafficUpdated;

        // Timer for updating UI (duration, traffic stats)
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateTimerTick();

        // Auto-save timer with debounce (save 1 second after last change)
        _saveDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _saveDebounceTimer.Tick += (_, _) =>
        {
            _saveDebounceTimer.Stop();
            SaveCurrentProfileState();
            SaveProfiles();
            SaveStatusText = "ذخیره شد";
        };

        // Load saved profiles, global tunnel apps, global excludes, global includes, and history on startup
        LoadProfiles();
        LoadTunnelApps();
        LoadExcludes();
        LoadIncludes();
        LoadHistory();
    }

    #region Properties

    private string _serverAddress = "";
    public string ServerAddress
    {
        get => _serverAddress;
        set { _serverAddress = value; OnPropertyChanged(); UpdateConfigDiagnostics(); SaveCurrentState(); }
    }

    private string _username = "";
    public string Username
    {
        get => _username;
        set { _username = value; OnPropertyChanged(); SaveCurrentState(); }
    }

    private string _password = "";
    public string Password
    {
        get => _password;
        set { _password = value; OnPropertyChanged(); }
    }

    private string _preSharedKey = "";
    public string PreSharedKey
    {
        get => _preSharedKey;
        set { _preSharedKey = value; OnPropertyChanged(); }
    }

    private int _socks5Port = 1080;
    public int Socks5Port
    {
        get => _socks5Port;
        set
        {
            var normalized = value;
            if (_socks5Port == normalized) return;
            _socks5Port = normalized;
            _trafficRouter.Socks5Port = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Socks5PortText));
            OnPropertyChanged(nameof(Socks5Info));
            UpdateSocks5PortStatus();
            SaveCurrentState();
        }
    }

    public string Socks5PortText
    {
        get => _socks5Port.ToString();
        set
        {
            if (int.TryParse((value ?? "").Trim(), out var port))
            {
                if (_socks5Port != port)
                {
                    _socks5Port = port;
                    _trafficRouter.Socks5Port = port;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(Socks5Port));
                    OnPropertyChanged(nameof(Socks5Info));
                    UpdateSocks5PortStatus();
                    SaveCurrentState();
                }
                return;
            }

            Socks5PortStatusText = string.IsNullOrWhiteSpace(value)
                ? "پورت SOCKS5 را وارد کنید"
                : "فقط عدد مجاز است";
        }
    }

    private string _socks5PortStatusText = "";
    public string Socks5PortStatusText
    {
        get => _socks5PortStatusText;
        set { _socks5PortStatusText = value; OnPropertyChanged(); }
    }

    private bool _autoTuneMtu = true;
    public bool AutoTuneMtu
    {
        get => _autoTuneMtu;
        set
        {
            if (_autoTuneMtu == value) return;
            _autoTuneMtu = value;
            OnPropertyChanged();
            SaveCurrentState();
        }
    }

    private bool _isDnsOptimizationEnabled = true;
    public bool IsDnsOptimizationEnabled
    {
        get => _isDnsOptimizationEnabled;
        set
        {
            if (_isDnsOptimizationEnabled == value) return;
            _isDnsOptimizationEnabled = value;
            _trafficRouter.EnableDnsOptimization = value;
            OnPropertyChanged();
            SaveCurrentState();
        }
    }

    private bool _isGameModeEnabled;
    public bool IsGameModeEnabled
    {
        get => _isGameModeEnabled;
        set
        {
            if (_isGameModeEnabled == value) return;
            _isGameModeEnabled = value;
            _trafficRouter.EnableGameMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GameModeStatusText));
            SaveCurrentState();
        }
    }

    public string GameModeStatusText => IsGameModeEnabled
        ? "Game Mode فعال است: Route نگهداری طولانی‌تر، DNS سریع‌تر و DSCP برای بسته‌های بازی اعمال می‌شود."
        : "Game Mode غیرفعال است: حالت متعادل برای مصرف عمومی.";

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { _isBusy = value; OnPropertyChanged(); OnPropertyChanged(nameof(ConnectButtonText)); }
    }

    private ConnectionState _connectionState = ConnectionState.Disconnected;
    public ConnectionState ConnectionState
    {
        get => _connectionState;
        set
        {
            _connectionState = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(ConnectButtonText));
            OnPropertyChanged(nameof(StatusColor));
            OnPropertyChanged(nameof(StatusText));
            RaiseHealthStatusChanged();
        }
    }

    public bool IsConnected => _connectionState == ConnectionState.Connected;

    /// <summary>App version read from a single app-wide source.</summary>
    public string AppVersion => AppInfo.VersionText;
    public string AppReleaseText => AppInfo.ReleaseText;
    public string AppCreatorText => AppInfo.CreatorText;
    public string AppGitHubUrl => AppInfo.GitHubUrl;
    public string AppLicenseText => AppInfo.LicenseName;
    public string DonatePayPalText => $"PayPal: {AppInfo.PayPalEmail}";
    public string CryptoDonationText => AppInfo.CryptoDonationText;

    public string ConnectButtonText => _connectionState switch
    {
        ConnectionState.Disconnected => "🔌  اتصال",
        ConnectionState.Connecting => "❌  لغو اتصال",
        ConnectionState.Connected => "🔴  قطع اتصال",
        ConnectionState.Disconnecting => "⏳  در حال قطع...",
        ConnectionState.Error => "🔌  اتصال مجدد",
        _ => "اتصال"
    };

    public string StatusColor => _connectionState switch
    {
        ConnectionState.Connected => "#4CAF50",
        ConnectionState.Connecting or ConnectionState.Disconnecting => "#E07820",
        ConnectionState.Error => "#E05252",
        _ => "#666666"
    };

    private string _statusText = "آماده اتصال";
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Mirrors SelectedProfile.TunnelType as a direct ViewModel property so XAML
    /// visibility bindings update reliably when the selected profile changes.
    /// Setter also writes back to the active profile.
    /// </summary>
    private TunnelType _currentTunnelType = TunnelType.L2tpIpsec;
    public TunnelType CurrentTunnelType
    {
        get => _currentTunnelType;
        set
        {
            if (_currentTunnelType == value) return;
            _currentTunnelType = value;
            OnPropertyChanged();
            if (_selectedProfile != null)
                _selectedProfile.TunnelType = value;
            UpdateConfigDiagnostics();
            RaiseHealthStatusChanged();
            SaveCurrentState();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private string _selectedV2RayConfig = "";
    public string SelectedV2RayConfig
    {
        get => _selectedV2RayConfig;
        set
        {
            if (_selectedV2RayConfig == value) return;
            _selectedV2RayConfig = value;
            if (_selectedProfile != null)
                _selectedProfile.V2RayConfig = value;
            OnPropertyChanged();
            TryAutoNameProfileFromConfig(value);
            UpdateConfigDiagnostics();
            RaiseHealthStatusChanged();
            SaveCurrentState();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private string _configCoreHint = "";
    public string ConfigCoreHint
    {
        get => _configCoreHint;
        set { _configCoreHint = value; OnPropertyChanged(); }
    }

    private string _configValidationText = "";
    public string ConfigValidationText
    {
        get => _configValidationText;
        set { _configValidationText = value; OnPropertyChanged(); }
    }

    private string _saveStatusText = "";
    public string SaveStatusText
    {
        get => _saveStatusText;
        set { _saveStatusText = value; OnPropertyChanged(); }
    }

    private string _connectionDuration = "--:--:--";
    public string ConnectionDuration
    {
        get => _connectionDuration;
        set { _connectionDuration = value; OnPropertyChanged(); }
    }

    private string _vpnIp = "";
    public string VpnIp
    {
        get => _vpnIp;
        set { _vpnIp = value; OnPropertyChanged(); }
    }

    private string _vpnAdapterName = "";
    public string VpnAdapterName
    {
        get => _vpnAdapterName;
        set { _vpnAdapterName = value; OnPropertyChanged(); }
    }

    private bool _isFullRouteEnabled;
    public bool IsFullRouteEnabled
    {
        get => _isFullRouteEnabled;
        set
        {
            if (_isFullRouteEnabled == value) return;

            if (IsConnected)
            {
                var ok = _trafficRouter.SetFullRouteEnabled(value);
                if (!ok)
                {
                    OnPropertyChanged();
                    StatusText = "تغییر حالت Full Route ناموفق بود";
                    return;
                }
            }

            _isFullRouteEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FullRouteStatusText));
            OnPropertyChanged(nameof(RouteModeTitle));
            OnPropertyChanged(nameof(RouteModeDescription));
            RaiseHealthStatusChanged();
        }
    }

    public string FullRouteStatusText => _isFullRouteEnabled
        ? "Full Route فعال است؛ کل سیستم از تونل عبور می‌کند"
        : "Split فعال است؛ فقط برنامه‌ها و مقصدهای انتخابی از تونل عبور می‌کنند";

    public string RouteModeTitle => IsFullRouteEnabled ? "حالت کل سیستم" : "حالت انتخابی";
    public string RouteModeDescription => IsFullRouteEnabled
        ? "همه برنامه‌ها از تونل عبور می‌کنند. برای تست یا وقتی می‌خواهید کل ویندوز پشت VPN باشد مناسب است."
        : "فقط برنامه‌های فعال در تب برنامه‌ها و مقصدهای لزومی از تونل عبور می‌کنند. حالت پیشنهادی برای مصرف کمتر و کنترل بهتر.";

    public string HeaderCoreText => $"Core: {ActiveCoreName}";
    public string HeaderRouteText => IsFullRouteEnabled ? "Mode: Full" : "Mode: Split";
    public string HeaderLeakText => IsConnected
        ? (_trafficRouter.LeakCount == 0
            ? (_trafficRouter.LeakBlockedCount == 0
                ? "Leak: OK"
                : $"Leak: OK (blocked {_trafficRouter.LeakBlockedCount})")
            : $"Leak: {_trafficRouter.LeakCount}")
        : "Leak: -";
    public string HeaderLeakColor => !IsConnected
        ? "#6CCB5F"
        : _trafficRouter.LeakCount > 0
            ? "#E05252"
            : (_trafficRouter.LeakBlockedCount > 0 ? "#E07820" : "#6CCB5F");

    public string HealthLeakText => IsConnected
        ? (_trafficRouter.LeakCount == 0
            ? (_trafficRouter.LeakBlockedCount == 0
                ? "0 leak"
                : $"0 leak / {_trafficRouter.LeakBlockedCount} blocked")
            : $"{_trafficRouter.LeakCount} leak")
        : "-";
    public string HealthDnsText => IsConnected
        ? (_trafficRouter.DnsRedirectCount > 0 ? $"DNS tunnel {_trafficRouter.DnsRedirectCount}" : "DNS ready")
        : "-";
    public string HealthIpv6Text => IsConnected
        ? $"IPv6 blocked {_trafficRouter.Ipv6BlockedCount}"
        : "-";
    public string HealthRoutesText => IsConnected
        ? $"routes {_trafficRouter.ActiveRouteCount}/{_trafficRouter.RouteFailureCount} fail"
        : "-";

    private string ActiveCoreName => CurrentTunnelType switch
    {
        TunnelType.L2tpIpsec => "L2TP",
        TunnelType.V2Ray when TunnelProviderFactory.RequiresXray(SelectedV2RayConfig) => "Xray",
        TunnelType.V2Ray => "sing-box",
        _ => "-"
    };

    private string _totalTraffic = "0 B";
    public string TotalTraffic
    {
        get => _totalTraffic;
        set { _totalTraffic = value; OnPropertyChanged(); }
    }

    private string _appTrafficTotal = "0 B";
    public string AppTrafficTotal
    {
        get => _appTrafficTotal;
        set { _appTrafficTotal = value; OnPropertyChanged(); }
    }

    private string _otherTunnelTraffic = "0 B";
    public string OtherTunnelTraffic
    {
        get => _otherTunnelTraffic;
        set { _otherTunnelTraffic = value; OnPropertyChanged(); }
    }

    private string _directTraffic = "0 B";
    public string DirectTraffic
    {
        get => _directTraffic;
        set { _directTraffic = value; OnPropertyChanged(); }
    }

    private string _pingTarget = "8.8.8.8";
    public string PingTarget
    {
        get => _pingTarget;
        set { _pingTarget = value; OnPropertyChanged(); }
    }

    private bool _isPinging;
    public bool IsPinging
    {
        get => _isPinging;
        set { _isPinging = value; OnPropertyChanged(); OnPropertyChanged(nameof(PingButtonText)); }
    }

    public string PingButtonText => _isPinging ? "⏹ توقف" : "▶ شروع";

    private string _pingResult = "";
    public string PingResult
    {
        get => _pingResult;
        set { _pingResult = value; OnPropertyChanged(); }
    }

    private bool _isTestingServerPing;
    public bool IsTestingServerPing
    {
        get => _isTestingServerPing;
        set
        {
            _isTestingServerPing = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ServerPingButtonText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string ServerPingButtonText => _isTestingServerPing ? "در حال تست..." : "تست سرور";

    private string _serverPingResult = "";
    public string ServerPingResult
    {
        get => _serverPingResult;
        set { _serverPingResult = value; OnPropertyChanged(); }
    }

    public int EnabledAppsCount => TunnelApps.Count(a => a.IsEnabled);

    public string Socks5Info => $"127.0.0.1:{_trafficRouter.Socks5Port}";

    // Exclude list
    public ObservableCollection<string> ExcludedDestinations { get; } = new();

    private string _excludeInput = "";
    public string ExcludeInput
    {
        get => _excludeInput;
        set { _excludeInput = value; OnPropertyChanged(); }
    }

    // Include list
    public ObservableCollection<string> IncludedDestinations { get; } = new();

    private string _includeInput = "";
    public string IncludeInput
    {
        get => _includeInput;
        set { _includeInput = value; OnPropertyChanged(); }
    }

    public ObservableCollection<AppItemViewModel> TunnelApps { get; } = new();

    public ObservableCollection<AppItemViewModel> AvailableApps { get; } = new();

    private AppItemViewModel? _selectedApp;
    public AppItemViewModel? SelectedApp
    {
        get => _selectedApp;
        set { _selectedApp = value; OnPropertyChanged(); }
    }

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set
        {
            _searchText = value;
            OnPropertyChanged();
            FilterAvailableApps();
        }
    }

    private string _tunnelSearchText = "";
    public string TunnelSearchText
    {
        get => _tunnelSearchText;
        set
        {
            _tunnelSearchText = value;
            OnPropertyChanged();
            FilterTunnelApps();
        }
    }

    private ObservableCollection<AppItemViewModel> _filteredAvailableApps = new();
    public ObservableCollection<AppItemViewModel> FilteredAvailableApps
    {
        get => _filteredAvailableApps;
        set { _filteredAvailableApps = value; OnPropertyChanged(); }
    }

    private ObservableCollection<AppItemViewModel> _filteredTunnelApps = new();
    public ObservableCollection<AppItemViewModel> FilteredTunnelApps
    {
        get => _filteredTunnelApps;
        set { _filteredTunnelApps = value; OnPropertyChanged(); }
    }

    // Connection History
    public ObservableCollection<ConnectionHistoryEntry> ConnectionHistory { get; } = new();

    public string TotalHistoryData
    {
        get
        {
            long total = ConnectionHistory.Sum(e => e.BytesSent + e.BytesReceived);
            return FormatBytes(total);
        }
    }

    #endregion

    #region Commands

    public ICommand ConnectCommand { get; }
    public ICommand AddAppCommand { get; }
    public ICommand RemoveAppCommand { get; }
    public ICommand ToggleAppCommand { get; }
    public ICommand RefreshAppsCommand { get; }
    public ICommand NewProfileCommand { get; }
    public ICommand DeleteProfileCommand { get; }
    public ICommand DuplicateProfileCommand { get; }
    public ICommand ClearHistoryCommand { get; }
    public ICommand AddExcludeCommand { get; }
    public ICommand RemoveExcludeCommand { get; }
    public ICommand AddIncludeCommand { get; }
    public ICommand RemoveIncludeCommand { get; }
    public ICommand TogglePingCommand { get; }
    public ICommand TestServerPingCommand { get; }
    public ICommand PasteConfigCommand { get; }
    public ICommand ClearConfigCommand { get; }
    public ICommand OpenGitHubCommand { get; }
    public ICommand OpenDonateCommand { get; }

    #endregion

    #region Config UX

    private static void OpenExternalLink(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Logger.Warning($"[UI] Open link failed: {url} — {ex.Message}");
        }
    }

    private void PasteConfigFromClipboard()
    {
        try
        {
            if (System.Windows.Clipboard.ContainsText())
                SelectedV2RayConfig = System.Windows.Clipboard.GetText().Trim();
        }
        catch (Exception ex)
        {
            ConfigValidationText = $"خواندن کلیپ‌بورد ناموفق بود: {ex.Message}";
        }
    }

    private void UpdateConfigDiagnostics()
    {
        if (CurrentTunnelType != TunnelType.V2Ray)
        {
            ConfigCoreHint = "L2TP/IPsec";
            ConfigValidationText = string.IsNullOrWhiteSpace(ServerAddress)
                ? "آدرس سرور L2TP را وارد کنید"
                : "آماده تست و اتصال";
            return;
        }

        var config = SelectedV2RayConfig.Trim();
        if (string.IsNullOrWhiteSpace(config))
        {
            ConfigCoreHint = "منتظر کانفیگ";
            ConfigValidationText = "کانفیگ V2Ray/Xray را وارد یا پیست کنید";
            return;
        }

        ConfigCoreHint = TunnelProviderFactory.RequiresXray(config)
            ? "هسته: Xray-core"
            : "هسته: sing-box";

        ConfigValidationText = TryExtractProxyEndpoint(config, out var server, out var port, out var error)
            ? $"سرور: {server}:{port}"
            : error;
    }

    private bool ValidateSocks5Port(out string message)
    {
        var port = _socks5Port;
        if (port < 1024 || port > 65535)
        {
            message = "پورت باید بین 1024 تا 65535 باشد";
            return false;
        }

        var blocked = new HashSet<int>
        {
            1433, 1521, 1723, 1900, 2049, 2080, 2375, 2376,
            3000, 3306, 3389, 5000, 5432, 5353, 5355, 5900,
            6379, 8000, 8080, 8443, 8888, 9000, 9090, 27017
        };

        if (blocked.Contains(port))
        {
            message = "این پورت رایج/حساس است؛ یک پورت آزاد مثل 1080، 1081 یا 18080 انتخاب کنید";
            return false;
        }

        try
        {
            var used = IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Any(p => p.Port == port);
            if (used && !IsConnected)
            {
                message = "این پورت همین حالا توسط برنامه دیگری استفاده می‌شود";
                return false;
            }
        }
        catch { }

        message = "پورت SOCKS5 داخلی آماده است";
        return true;
    }

    private void UpdateSocks5PortStatus()
    {
        ValidateSocks5Port(out var message);
        Socks5PortStatusText = message;
    }

    private void TryAutoNameProfileFromConfig(string config)
    {
        if (_selectedProfile == null || string.IsNullOrWhiteSpace(config))
            return;

        var currentName = _selectedProfile.Name?.Trim() ?? "";
        var canRename = string.IsNullOrWhiteSpace(currentName) ||
                        currentName.StartsWith("پروفایل ", StringComparison.OrdinalIgnoreCase) ||
                        currentName == "پروفایل جدید" ||
                        currentName == "پیش‌فرض";
        if (!canRename)
            return;

        var remark = ExtractConfigRemark(config);
        if (string.IsNullOrWhiteSpace(remark))
            return;

        _selectedProfile.Name = remark;
        OnPropertyChanged(nameof(SelectedProfileName));
    }

    private static string ExtractConfigRemark(string config)
    {
        try
        {
            if (config.Contains('#'))
                return Uri.UnescapeDataString(config[(config.IndexOf('#') + 1)..]).Trim();

            if (config.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
            {
                var json = TryBase64DecodeConfig(config["vmess://".Length..]);
                return JsonNode.Parse(json)?["ps"]?.GetValue<string>()?.Trim() ?? "";
            }

            if (config.StartsWith("{"))
            {
                var root = JsonNode.Parse(config);
                return root?["remarks"]?.GetValue<string>()?.Trim() ??
                       root?["name"]?.GetValue<string>()?.Trim() ??
                       root?["outbounds"]?[0]?["tag"]?.GetValue<string>()?.Trim() ?? "";
            }
        }
        catch { }

        return "";
    }

    private void RaiseHealthStatusChanged()
    {
        OnPropertyChanged(nameof(HeaderCoreText));
        OnPropertyChanged(nameof(HeaderRouteText));
        OnPropertyChanged(nameof(HeaderLeakText));
        OnPropertyChanged(nameof(HeaderLeakColor));
        OnPropertyChanged(nameof(RouteModeTitle));
        OnPropertyChanged(nameof(RouteModeDescription));
        OnPropertyChanged(nameof(HealthLeakText));
        OnPropertyChanged(nameof(HealthDnsText));
        OnPropertyChanged(nameof(HealthIpv6Text));
        OnPropertyChanged(nameof(HealthRoutesText));
    }

    #endregion

    #region INotifyPropertyChanged

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    #endregion
}
