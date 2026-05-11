using System.Collections.ObjectModel;
using System.Windows;
using Application = System.Windows.Application;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using AppTunnel.Models;
using AppTunnel.Services;

namespace AppTunnel.ViewModels;

public partial class MainViewModel
{
    #region App Management

    public void LoadInstalledApps()
    {
        Task.Run(() =>
        {
            var apps = AppDiscoveryService.GetInstalledApps();
            Application.Current?.Dispatcher.Invoke(() =>
            {
                AvailableApps.Clear();
                foreach (var app in apps)
                    AvailableApps.Add(new AppItemViewModel(app));
                RefreshAllFilters();
            });
        });
    }

    private void FilterAvailableApps()
    {
        var filtered = string.IsNullOrWhiteSpace(SearchText)
            ? AvailableApps
            : new ObservableCollection<AppItemViewModel>(
                AvailableApps.Where(a =>
                    a.DisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                    a.ExecutableName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                    a.ExecutablePath.Contains(SearchText, StringComparison.OrdinalIgnoreCase)));

        // Exclude apps already in tunnel list
        var tunnelExes = TunnelApps.Select(a => a.ExecutablePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        FilteredAvailableApps = new ObservableCollection<AppItemViewModel>(
            filtered.Where(a => !tunnelExes.Contains(a.ExecutablePath)));
    }

    private void FilterTunnelApps()
    {
        if (string.IsNullOrWhiteSpace(TunnelSearchText))
        {
            FilteredTunnelApps = new ObservableCollection<AppItemViewModel>(TunnelApps);
        }
        else
        {
            FilteredTunnelApps = new ObservableCollection<AppItemViewModel>(
                TunnelApps.Where(a =>
                    a.DisplayName.Contains(TunnelSearchText, StringComparison.OrdinalIgnoreCase) ||
                    a.ExecutableName.Contains(TunnelSearchText, StringComparison.OrdinalIgnoreCase) ||
                    a.ExecutablePath.Contains(TunnelSearchText, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private void RefreshAllFilters()
    {
        FilterAvailableApps();
        FilterTunnelApps();
    }

    private void AddCustomApp()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Applications (*.exe)|*.exe",
            Title = "انتخاب برنامه"
        };

        if (dialog.ShowDialog() == true)
        {
            var app = AppDiscoveryService.GetAppFromPath(dialog.FileName);
            if (app != null)
            {
                var vm = new AppItemViewModel(app) { IsEnabled = true };
                TunnelApps.Add(vm);

                if (IsConnected)
                    _trafficRouter.AddTargetApp(app.ExecutableName);

                RefreshAllFilters();
                SaveTunnelApps();
            }
        }
    }

    public void AddAppToTunnel(AppItemViewModel app)
    {
        if (TunnelApps.Any(a => a.ExecutablePath.Equals(app.ExecutablePath, StringComparison.OrdinalIgnoreCase)))
            return;

        var tunnelApp = new AppItemViewModel(new TunnelApp
        {
            DisplayName = app.DisplayName,
            ExecutablePath = app.ExecutablePath,
            ExecutableName = app.ExecutableName,
            Icon = app.Icon,
            IsEnabled = true
        }) { IsEnabled = true };

        TunnelApps.Add(tunnelApp);

        if (IsConnected)
            _trafficRouter.AddTargetApp(app.ExecutableName);

        RefreshAllFilters();
        OnPropertyChanged(nameof(EnabledAppsCount));
        SaveTunnelApps();
    }

    private void RemoveApp(object? param)
    {
        if (param is AppItemViewModel app)
        {
            TunnelApps.Remove(app);
            _trafficRouter.RemoveTargetApp(app.ExecutableName);
            RefreshAllFilters();
            OnPropertyChanged(nameof(EnabledAppsCount));
            SaveTunnelApps();
        }
    }

    private void ToggleApp(object? param)
    {
        if (param is AppItemViewModel app)
        {
            app.IsEnabled = !app.IsEnabled;
            Logger.Info($"[APP-SWITCH] '{app.ExecutableName}' => {(app.IsEnabled ? "ON" : "OFF")} (connected={IsConnected})");
            OnPropertyChanged(nameof(EnabledAppsCount));
            if (IsConnected)
            {
                if (app.IsEnabled)
                    _trafficRouter.AddTargetApp(app.ExecutableName);
                else
                    _trafficRouter.RemoveTargetApp(app.ExecutableName);
            }
            SaveTunnelApps();
        }
    }

    private void AddExcludedDestination()
    {
        var entry = ExcludeInput?.Trim();
        if (string.IsNullOrWhiteSpace(entry)) return;
        if (ExcludedDestinations.Contains(entry, StringComparer.OrdinalIgnoreCase)) return;

        ExcludedDestinations.Add(entry);
        ExcludeInput = "";

        if (IsConnected)
            _trafficRouter.AddExcludedDestination(entry);

        SaveExcludes();
    }

    private void RemoveExcludedDestination(object? param)
    {
        if (param is string entry)
        {
            ExcludedDestinations.Remove(entry);

            if (IsConnected)
                _trafficRouter.RemoveExcludedDestination(entry);

            SaveExcludes();
        }
    }

    private void AddIncludedDestination()
    {
        var entry = IncludeInput?.Trim();
        if (string.IsNullOrWhiteSpace(entry)) return;
        if (IncludedDestinations.Contains(entry, StringComparer.OrdinalIgnoreCase)) return;

        IncludedDestinations.Add(entry);
        IncludeInput = "";

        if (IsConnected)
            _trafficRouter.AddIncludedDestination(entry);

        SaveIncludes();
    }

    private void RemoveIncludedDestination(object? param)
    {
        if (param is string entry)
        {
            IncludedDestinations.Remove(entry);

            if (IsConnected)
                _trafficRouter.RemoveIncludedDestination(entry);

            SaveIncludes();
        }
    }

    #endregion
}
