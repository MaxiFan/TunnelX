using System.Collections.ObjectModel;
using System.IO;
using AppTunnel.Models;
using AppTunnel.Services;
using AppTunnel.Views;

namespace AppTunnel.ViewModels;

public partial class MainViewModel
{
    #region Profile Management

    public ObservableCollection<ConnectionProfile> Profiles { get; } = new();

    private ConnectionProfile? _selectedProfile;
    public ConnectionProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (_selectedProfile == value) return;
            _saveDebounceTimer.Stop();
            SaveCurrentProfileState();
            _selectedProfile = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedProfileName));
            OnPropertyChanged(nameof(ConnectedProfileName));
            OnPropertyChanged(nameof(SelectedProfileSummaryText));
            RaiseProfileCardChanged();
            if (value != null)
                LoadProfileIntoUi(value);
            SaveProfiles();
        }
    }

    public string SelectedProfileName => _selectedProfile?.Name ?? "";
    public string ProfileCountText => Profiles.Count == 1
        ? LocalizationService.Instance.T("۱ پروفایل ذخیره‌شده")
        : LocalizationService.Instance.Format("{0} پروفایل ذخیره‌شده", Profiles.Count);

    public string ActiveProfileTypeText => CurrentTunnelType switch
    {
        TunnelType.L2tpIpsec => "L2TP/IPsec",
        TunnelType.V2Ray => TunnelProviderFactory.RequiresXray(SelectedV2RayConfig) ? "V2Ray / Xray" : "V2Ray / sing-box",
        TunnelType.OpenVpn => "OpenVPN",
        TunnelType.SocksProxy => ProxyProtocol == ProxyProtocol.Http ? "HTTP Proxy" : "SOCKS5 Proxy",
        TunnelType.WireGuard => "WireGuard / Windows Adapter",
        _ => LocalizationService.Instance.T("نوع اتصال نامشخص")
    };

    public string ActiveProfileEndpointText => CurrentTunnelType switch
    {
        TunnelType.L2tpIpsec => string.IsNullOrWhiteSpace(ServerAddress) ? LocalizationService.Instance.T("آدرس سرور هنوز وارد نشده") : ServerAddress.Trim(),
        TunnelType.V2Ray => TryExtractProxyEndpoint(SelectedV2RayConfig.Trim(), out var server, out var port, out _)
            ? $"{server}:{port}"
            : LocalizationService.Instance.T("کانفیگ V2Ray/Xray آماده نمایش نیست"),
        TunnelType.OpenVpn => string.IsNullOrWhiteSpace(SelectedOpenVpnConfigPath)
            ? LocalizationService.Instance.T("فایل OpenVPN انتخاب نشده")
            : Path.GetFileName(SelectedOpenVpnConfigPath),
        TunnelType.SocksProxy => string.IsNullOrWhiteSpace(ProxyServerAddress)
            ? LocalizationService.Instance.T("آدرس پراکسی هنوز وارد نشده")
            : $"{ProxyServerAddress.Trim()}:{ProxyPort}",
        TunnelType.WireGuard => WireGuardConfigParser.TryParse(SelectedWireGuardConfig, out var wg, out _)
            ? $"{wg.EndpointHost}:{wg.EndpointPort}"
            : LocalizationService.Instance.T("کانفیگ WireGuard آماده نمایش نیست"),
        _ => ""
    };

    public string ProfileSaveHintText => string.IsNullOrWhiteSpace(SaveStatusText)
        ? LocalizationService.Instance.T("تغییرات این پروفایل به‌صورت خودکار ذخیره می‌شود")
        : SaveStatusText;

    /// <summary>
    /// Event to notify code-behind to update PasswordBox controls.
    /// </summary>
    public event Action<string, string>? PasswordChanged;
    public event Action<string>? OpenVpnPasswordChanged;
    public event Action<string>? OpenVpnPrivateKeyPasswordChanged;
    public event Action<string>? ProxyPasswordChanged;

    private void LoadProfiles()
    {
        var profiles = _profileService.LoadProfiles();
        Profiles.Clear();

        if (profiles.Count == 0)
            profiles.Add(new ConnectionProfile { Name = LocalizationService.Instance.T("پیش‌فرض") });

        foreach (var p in profiles.OrderByDescending(p => p.LastUsedAt))
            Profiles.Add(p);
        OnPropertyChanged(nameof(ProfileCountText));

        _selectedProfile = Profiles[0];
        OnPropertyChanged(nameof(SelectedProfile));
        OnPropertyChanged(nameof(SelectedProfileName));
        OnPropertyChanged(nameof(ConnectedProfileName));
        OnPropertyChanged(nameof(SelectedProfileSummaryText));
        LoadProfileIntoUi(Profiles[0]);
        NotifyReadyProfilesForLatencyTestChanged();
    }

    private void SaveProfiles() => _profileService.SaveProfiles(Profiles);

    private void LoadExcludes()
    {
        var excludes = _profileService.LoadExcludes();
        ExcludedDestinations.Clear();
        foreach (var e in excludes)
            ExcludedDestinations.Add(e);
    }

    private void SaveExcludes() => _profileService.SaveExcludes(ExcludedDestinations);

    private void LoadIncludes()
    {
        var includes = _profileService.LoadIncludes();
        IncludedDestinations.Clear();
        foreach (var i in includes)
            IncludedDestinations.Add(i);
    }

    private void SaveIncludes() => _profileService.SaveIncludes(IncludedDestinations);

    private void LoadTunnelApps()
    {
        var apps = _profileService.LoadTunnelApps();
        TunnelApps.Clear();
        foreach (var app in apps)
        {
            var icon = AppDiscoveryService.ExtractIcon(app.ExecutablePath);
            TunnelApps.Add(new AppItemViewModel(new TunnelApp
            {
                DisplayName = app.DisplayName,
                ExecutablePath = app.ExecutablePath,
                ExecutableName = app.ExecutableName,
                Icon = icon,
                IsEnabled = app.IsEnabled
            }) { IsEnabled = app.IsEnabled });
        }
        RefreshAllFilters();
        OnPropertyChanged(nameof(EnabledAppsCount));
    }

    private void SaveTunnelApps() => _profileService.SaveTunnelApps(
        TunnelApps.Select(a => new ProfileApp
        {
            DisplayName = a.DisplayName,
            ExecutablePath = a.ExecutablePath,
            ExecutableName = a.ExecutableName,
            IsEnabled = a.IsEnabled
        }));

    private void SaveCurrentProfileState()
    {
        if (_selectedProfile == null) return;
        _selectedProfile.ServerAddress = ServerAddress;
        _selectedProfile.Username = Username;
        _selectedProfile.Password = Password;
        _selectedProfile.PreSharedKey = PreSharedKey;
        _selectedProfile.TunnelType = _currentTunnelType;
        _selectedProfile.V2RayConfig = SelectedV2RayConfig;
        _selectedProfile.OpenVpnConfig = SelectedOpenVpnConfig;
        _selectedProfile.OpenVpnConfigPath = SelectedOpenVpnConfigPath;
        _selectedProfile.OpenVpnUsername = OpenVpnUsername;
        _selectedProfile.OpenVpnPassword = OpenVpnPassword;
        _selectedProfile.OpenVpnPrivateKeyPassword = OpenVpnPrivateKeyPassword;
        _selectedProfile.WireGuardConfig = SelectedWireGuardConfig;
        _selectedProfile.WireGuardConfigPath = SelectedWireGuardConfigPath;
        _selectedProfile.ProxyProtocol = ProxyProtocol;
        _selectedProfile.ProxyServerAddress = ProxyServerAddress;
        _selectedProfile.ProxyPort = ProxyPort;
        _selectedProfile.ProxyUsername = ProxyUsername;
        _selectedProfile.ProxyPassword = ProxyPassword;
        _selectedProfile.MixedProxyPort = MixedProxyPort;
        _selectedProfile.AutoTuneMtu = AutoTuneMtu;
        _selectedProfile.EnableDnsOptimization = IsDnsOptimizationEnabled;
        _selectedProfile.EnableGameMode = IsGameModeEnabled;
    }

    /// <summary>
    /// Called from code-behind after PasswordBox changes to persist state.
    /// Uses debounce to avoid saving on every keystroke.
    /// </summary>
    public void SaveCurrentState()
    {
        if (_isLoadingProfile) return;
        SaveStatusText = "در حال ذخیره...";
        _saveDebounceTimer.Stop();
        _saveDebounceTimer.Start(); // Restart timer - will save after 1 second of no changes
    }

    /// <summary>
    /// Force immediate save without debounce (for app shutdown).
    /// </summary>
    public void ForceSave()
    {
        _saveDebounceTimer.Stop();
        SaveCurrentProfileState();
        SaveProfiles();
        SaveStatusText = "ذخیره شد";
    }

    private void LoadProfileIntoUi(ConnectionProfile profile)
    {
        _isLoadingProfile = true;
        try
        {
            ServerAddress = profile.ServerAddress;
            Username = profile.Username;
            Password = profile.Password;
            PreSharedKey = profile.PreSharedKey;
            _mixedProxyPort = profile.MixedProxyPort > 0 ? profile.MixedProxyPort : 1080;
            _trafficRouter.Socks5Port = _mixedProxyPort;
            OnPropertyChanged(nameof(MixedProxyPort));
            OnPropertyChanged(nameof(MixedProxyPortText));
            OnPropertyChanged(nameof(MixedProxyInfo));
            UpdateMixedProxyPortStatus();
            _autoTuneMtu = profile.AutoTuneMtu;
            _isDnsOptimizationEnabled = profile.EnableDnsOptimization;
            _isGameModeEnabled = profile.EnableGameMode;
            _trafficRouter.EnableDnsOptimization = _isDnsOptimizationEnabled;
            _trafficRouter.EnableGameMode = _isGameModeEnabled;
            OnPropertyChanged(nameof(AutoTuneMtu));
            OnPropertyChanged(nameof(IsDnsOptimizationEnabled));
            OnPropertyChanged(nameof(IsGameModeEnabled));
            OnPropertyChanged(nameof(GameModeStatusText));
            // Use the field directly to avoid writing back to the old profile
            // while the new profile is being loaded.
            _currentTunnelType = profile.TunnelType;
            _selectedV2RayConfig = profile.V2RayConfig;
            _selectedOpenVpnConfig = profile.OpenVpnConfig;
            _selectedOpenVpnConfigPath = profile.OpenVpnConfigPath;
            _selectedWireGuardConfig = profile.WireGuardConfig;
            _selectedWireGuardConfigPath = profile.WireGuardConfigPath;
            _openVpnUsername = profile.OpenVpnUsername;
            _openVpnPassword = profile.OpenVpnPassword;
            _openVpnPrivateKeyPassword = profile.OpenVpnPrivateKeyPassword;
            _proxyProtocol = profile.ProxyProtocol;
            _proxyServerAddress = profile.ProxyServerAddress;
            _proxyPort = profile.ProxyPort > 0 ? profile.ProxyPort : 1080;
            _proxyUsername = profile.ProxyUsername;
            _proxyPassword = profile.ProxyPassword;
            OnPropertyChanged(nameof(CurrentTunnelType));
            OnPropertyChanged(nameof(ConnectedBadgeText));
            OnPropertyChanged(nameof(SelectedV2RayConfig));
            OnPropertyChanged(nameof(SelectedOpenVpnConfig));
            OnPropertyChanged(nameof(SelectedOpenVpnConfigPath));
            OnPropertyChanged(nameof(SelectedWireGuardConfig));
            OnPropertyChanged(nameof(SelectedWireGuardConfigPath));
            OnPropertyChanged(nameof(OpenVpnUsername));
            OnPropertyChanged(nameof(OpenVpnPrivateKeyPassword));
            OnPropertyChanged(nameof(ProxyProtocol));
            OnPropertyChanged(nameof(ProxyServerAddress));
            OnPropertyChanged(nameof(ProxyPort));
            OnPropertyChanged(nameof(ProxyPortText));
            OnPropertyChanged(nameof(ProxyUsername));
            UpdateConfigDiagnostics();
            RaiseProfileCardChanged();

            PasswordChanged?.Invoke(profile.Password, profile.PreSharedKey);
            OpenVpnPasswordChanged?.Invoke(profile.OpenVpnPassword);
            OpenVpnPrivateKeyPasswordChanged?.Invoke(profile.OpenVpnPrivateKeyPassword);
            ProxyPasswordChanged?.Invoke(profile.ProxyPassword);
        }
        finally
        {
            _isLoadingProfile = false;
        }
    }

    private void CreateNewProfile()
    {
        SaveCurrentProfileState();
        var profile = new ConnectionProfile
        {
            Name = "",
            MixedProxyPort = MixedProxyPort,
            AutoTuneMtu = AutoTuneMtu,
            EnableDnsOptimization = IsDnsOptimizationEnabled,
            EnableGameMode = IsGameModeEnabled
        };

        if (ProfileEditorDialog.Show(profile, "افزودن کانفیگ جدید", System.Windows.Application.Current.MainWindow) != true)
            return;

        Profiles.Add(profile);
        OnPropertyChanged(nameof(ProfileCountText));
        SelectedProfile = profile;
        SaveProfiles();
        NotifyReadyProfilesForLatencyTestChanged();
    }

    private void DuplicateCurrentProfile(object? parameter = null)
    {
        var source = parameter as ConnectionProfile ?? _selectedProfile;
        if (source == null) return;
        SaveCurrentProfileState();

        var clone = CloneProfile(source);
        clone.Name = LocalizationService.Instance.Format("{0} (کپی)", source.Name);

        if (ProfileEditorDialog.Show(clone, "کپی پروفایل", System.Windows.Application.Current.MainWindow) != true)
            return;

        Profiles.Add(clone);
        OnPropertyChanged(nameof(ProfileCountText));
        SelectedProfile = clone;
        SaveProfiles();
        NotifyReadyProfilesForLatencyTestChanged();
    }

    private void EditProfile(object? parameter)
    {
        var profile = parameter as ConnectionProfile ?? _selectedProfile;
        if (profile == null) return;

        SaveCurrentProfileState();
        var editable = CloneProfile(profile);
        editable.Id = profile.Id;
        editable.CreatedAt = profile.CreatedAt;
        editable.LastUsedAt = profile.LastUsedAt;

        if (ProfileEditorDialog.Show(editable, "ویرایش پروفایل", System.Windows.Application.Current.MainWindow) != true)
            return;

        ApplyProfileValues(profile, editable);
        if (_selectedProfile == profile)
            LoadProfileIntoUi(profile);
        SaveProfiles();
        RaiseProfileCardChanged();
        NotifyReadyProfilesForLatencyTestChanged();
    }

    private void SelectProfile(object? parameter)
    {
        if (parameter is ConnectionProfile profile)
            SelectedProfile = profile;
    }

    private void DeleteCurrentProfile(object? parameter = null)
    {
        var toRemove = parameter as ConnectionProfile ?? _selectedProfile;
        if (toRemove == null || Profiles.Count <= 1) return;
        if (!Helpers.DialogService.Confirm(LocalizationService.Instance.Format("پروفایل «{0}» حذف شود؟", toRemove.Name), "حذف پروفایل"))
            return;

        var idx = Profiles.IndexOf(toRemove);
        Profiles.Remove(toRemove);
        OnPropertyChanged(nameof(ProfileCountText));
        SelectedProfile = Profiles[Math.Min(idx, Profiles.Count - 1)];
        SaveProfiles();
        NotifyReadyProfilesForLatencyTestChanged();
    }

    private static ConnectionProfile CloneProfile(ConnectionProfile source) => new()
    {
        Name = source.Name,
        ServerAddress = source.ServerAddress,
        Username = source.Username,
        Password = source.Password,
        PreSharedKey = source.PreSharedKey,
        TunnelType = source.TunnelType,
        V2RayConfig = source.V2RayConfig,
        OpenVpnConfig = source.OpenVpnConfig,
        OpenVpnConfigPath = source.OpenVpnConfigPath,
        OpenVpnUsername = source.OpenVpnUsername,
        OpenVpnPassword = source.OpenVpnPassword,
        OpenVpnPrivateKeyPassword = source.OpenVpnPrivateKeyPassword,
        WireGuardConfig = source.WireGuardConfig,
        WireGuardConfigPath = source.WireGuardConfigPath,
        ProxyProtocol = source.ProxyProtocol,
        ProxyServerAddress = source.ProxyServerAddress,
        ProxyPort = source.ProxyPort,
        ProxyUsername = source.ProxyUsername,
        ProxyPassword = source.ProxyPassword,
        MixedProxyPort = source.MixedProxyPort,
        AutoTuneMtu = source.AutoTuneMtu,
        EnableDnsOptimization = source.EnableDnsOptimization,
        EnableGameMode = source.EnableGameMode
    };

    private static void ApplyProfileValues(ConnectionProfile target, ConnectionProfile source)
    {
        target.Name = source.Name;
        target.ServerAddress = source.ServerAddress;
        target.Username = source.Username;
        target.Password = source.Password;
        target.PreSharedKey = source.PreSharedKey;
        target.TunnelType = source.TunnelType;
        target.V2RayConfig = source.V2RayConfig;
        target.OpenVpnConfig = source.OpenVpnConfig;
        target.OpenVpnConfigPath = source.OpenVpnConfigPath;
        target.OpenVpnUsername = source.OpenVpnUsername;
        target.OpenVpnPassword = source.OpenVpnPassword;
        target.OpenVpnPrivateKeyPassword = source.OpenVpnPrivateKeyPassword;
        target.WireGuardConfig = source.WireGuardConfig;
        target.WireGuardConfigPath = source.WireGuardConfigPath;
        target.ProxyProtocol = source.ProxyProtocol;
        target.ProxyServerAddress = source.ProxyServerAddress;
        target.ProxyPort = source.ProxyPort;
        target.ProxyUsername = source.ProxyUsername;
        target.ProxyPassword = source.ProxyPassword;
        target.MixedProxyPort = source.MixedProxyPort;
        target.AutoTuneMtu = source.AutoTuneMtu;
        target.EnableDnsOptimization = source.EnableDnsOptimization;
        target.EnableGameMode = source.EnableGameMode;
    }

    private void RaiseProfileCardChanged()
    {
        OnPropertyChanged(nameof(ProfileCountText));
        OnPropertyChanged(nameof(ConnectedProfileName));
        OnPropertyChanged(nameof(SelectedProfileSummaryText));
        OnPropertyChanged(nameof(ActiveProfileTypeText));
        OnPropertyChanged(nameof(ActiveProfileEndpointText));
        OnPropertyChanged(nameof(ProfileSaveHintText));
    }

    #endregion

    #region History

    private void LoadHistory()
    {
        var entries = _historyService.LoadHistory();
        ConnectionHistory.Clear();
        foreach (var entry in entries)
            ConnectionHistory.Add(entry);
        OnPropertyChanged(nameof(TotalHistoryData));
    }

    private void ClearHistory()
    {
        _historyService.ClearHistory();
        ConnectionHistory.Clear();
        OnPropertyChanged(nameof(TotalHistoryData));
    }

    private void SaveConnectionToHistory()
    {
        if (_connectionStartTime == default) return;

        // Use authoritative VPN-interface counters for the total,
        // not the sum of per-app counters (which may miss tail packets
        // and non-attributed traffic like VPN keepalives).
        var (totalSent, totalReceived) = _trafficRouter.GetTotalVpnTraffic();

        var entry = new ConnectionHistoryEntry
        {
            ProfileName = _selectedProfile?.Name ?? LocalizationService.Instance.T("پیش‌فرض"),
            ServerAddress = CurrentTunnelType == TunnelType.SocksProxy
                ? $"{ProxyServerAddress}:{ProxyPort}"
                : ServerAddress,
            ConnectedAt = _connectionStartTime,
            DisconnectedAt = DateTime.Now,
            BytesSent = totalSent,
            BytesReceived = totalReceived
        };

        _historyService.AddEntry(entry);
        ConnectionHistory.Insert(0, entry);
        _connectionStartTime = default;
        OnPropertyChanged(nameof(TotalHistoryData));
    }

    #endregion
}
