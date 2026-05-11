using System.Collections.ObjectModel;
using AppTunnel.Models;
using AppTunnel.Services;

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
            SaveCurrentProfileState();
            _selectedProfile = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedProfileName));
            if (value != null)
                LoadProfileIntoUi(value);
            SaveProfiles();
        }
    }

    public string SelectedProfileName => _selectedProfile?.Name ?? "";

    /// <summary>
    /// Event to notify code-behind to update PasswordBox controls.
    /// </summary>
    public event Action<string, string>? PasswordChanged;

    private void LoadProfiles()
    {
        var profiles = _profileService.LoadProfiles();
        Profiles.Clear();

        if (profiles.Count == 0)
            profiles.Add(new ConnectionProfile { Name = "پیش‌فرض" });

        foreach (var p in profiles.OrderByDescending(p => p.LastUsedAt))
            Profiles.Add(p);

        _selectedProfile = Profiles[0];
        OnPropertyChanged(nameof(SelectedProfile));
        OnPropertyChanged(nameof(SelectedProfileName));
        LoadProfileIntoUi(Profiles[0]);
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
        _selectedProfile.Socks5Port = Socks5Port;
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
        ServerAddress = profile.ServerAddress;
        Username = profile.Username;
        Password = profile.Password;
        PreSharedKey = profile.PreSharedKey;
        _socks5Port = profile.Socks5Port > 0 ? profile.Socks5Port : 1080;
        _trafficRouter.Socks5Port = _socks5Port;
        OnPropertyChanged(nameof(Socks5Port));
        OnPropertyChanged(nameof(Socks5PortText));
        OnPropertyChanged(nameof(Socks5Info));
        UpdateSocks5PortStatus();
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
        OnPropertyChanged(nameof(CurrentTunnelType));
        OnPropertyChanged(nameof(SelectedV2RayConfig));
        UpdateConfigDiagnostics();

        PasswordChanged?.Invoke(profile.Password, profile.PreSharedKey);
    }

    private void CreateNewProfile()
    {
        SaveCurrentProfileState();
        var profile = new ConnectionProfile { Name = $"پروفایل {Profiles.Count + 1}", Socks5Port = Socks5Port };
        profile.AutoTuneMtu = AutoTuneMtu;
        profile.EnableDnsOptimization = IsDnsOptimizationEnabled;
        profile.EnableGameMode = IsGameModeEnabled;
        Profiles.Add(profile);
        SelectedProfile = profile;
    }

    private void DuplicateCurrentProfile()
    {
        if (_selectedProfile == null) return;
        SaveCurrentProfileState();

        var clone = new ConnectionProfile
        {
            Name = $"{_selectedProfile.Name} (کپی)",
            ServerAddress = _selectedProfile.ServerAddress,
            Username = _selectedProfile.Username,
            Password = _selectedProfile.Password,
            PreSharedKey = _selectedProfile.PreSharedKey,
            TunnelType = _selectedProfile.TunnelType,
            V2RayConfig = _selectedProfile.V2RayConfig,
            Socks5Port = _selectedProfile.Socks5Port,
            AutoTuneMtu = _selectedProfile.AutoTuneMtu,
            EnableDnsOptimization = _selectedProfile.EnableDnsOptimization,
            EnableGameMode = _selectedProfile.EnableGameMode,
        };
        Profiles.Add(clone);
        SelectedProfile = clone;
    }

    private void DeleteCurrentProfile()
    {
        if (_selectedProfile == null || Profiles.Count <= 1) return;
        var toRemove = _selectedProfile;
        var idx = Profiles.IndexOf(toRemove);
        Profiles.Remove(toRemove);
        SelectedProfile = Profiles[Math.Min(idx, Profiles.Count - 1)];
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
            ProfileName = _selectedProfile?.Name ?? "پیش‌فرض",
            ServerAddress = ServerAddress,
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
