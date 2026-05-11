using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media;
using AppTunnel.ViewModels;
using AppTunnel.Helpers;
using AppTunnel.Services;
using Application = System.Windows.Application;

namespace AppTunnel;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private CancellationTokenSource _loadCts = new();
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _isRealExit;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        InitializeTrayIcon();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void InitializeTrayIcon()
    {
        var iconStream = Application.GetResourceStream(
            new Uri("pack://application:,,,/app.ico"))?.Stream;
        
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "TunnelX — Split Traffic Per App",
            Visible = false
        };

        if (iconStream != null)
            _trayIcon.Icon = new System.Drawing.Icon(iconStream);

        // Double-click tray icon → show window
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();

        // Context menu
        var menu = new System.Windows.Forms.ContextMenuStrip();
        
        var showItem = new System.Windows.Forms.ToolStripMenuItem("نمایش TunnelX");
        showItem.Font = new System.Drawing.Font("Segoe UI", 9, System.Drawing.FontStyle.Bold);
        showItem.Click += (_, _) => ShowFromTray();
        menu.Items.Add(showItem);

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var statusItem = new System.Windows.Forms.ToolStripMenuItem("وضعیت: آماده");
        statusItem.Enabled = false;
        menu.Items.Add(statusItem);

        // Update status text dynamically
        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.StatusText))
            {
                Dispatcher.BeginInvoke(() =>
                {
                    statusItem.Text = $"وضعیت: {_viewModel.StatusText}";
                });
            }
        };

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var exitItem = new System.Windows.Forms.ToolStripMenuItem("خروج از برنامه");
        exitItem.Click += async (_, _) =>
        {
            if (_viewModel.IsConnected)
            {
                bool confirmed = false;
                Dispatcher.Invoke(() =>
                {
                    confirmed = DialogService.Confirm(
                        "اتصال VPN فعال است. با خروج، اتصال قطع خواهد شد.\nآیا مطمئن هستید؟",
                        "TunnelX — خروج");
                });

                if (!confirmed) return;

                try { await _viewModel.DisconnectAndCleanupAsync(); }
                catch { }
            }

            _isRealExit = true;
            _trayIcon?.Dispose();
            _trayIcon = null;
            Dispatcher.Invoke(() => Application.Current.Shutdown());
        };
        menu.Items.Add(exitItem);

        _trayIcon.ContextMenuStrip = menu;
    }

    private void ShowFromTray()
    {
        BringToForeground();
    }

    public void BringToForeground()
    {
        Dispatcher.Invoke(() =>
        {
            Show();
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
            if (_trayIcon != null) _trayIcon.Visible = false;
        });
    }

    private void MinimizeToTray()
    {
        Hide();
        if (_trayIcon != null)
        {
            _trayIcon.Visible = true;
            _trayIcon.ShowBalloonTip(
                2000,
                "TunnelX",
                "برنامه در System Tray فعال است. برای نمایش دوبار کلیک کنید.",
                System.Windows.Forms.ToolTipIcon.Info);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Force window to foreground — borderless/transparent windows sometimes
        // start behind other windows or appear unfocused on slower machines.
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();

        // Dismiss the startup overlay quickly; app discovery continues in the
        // background so the first screen becomes usable immediately.
        var ct = _loadCts.Token;
        LoadingStatusText.Text = "بارگذاری لیست برنامه‌های نصب‌شده...";
        _ = DismissLoadingOverlaySoonAsync(ct);
        Task.Run(() =>
        {
            var apps = Services.AppDiscoveryService.GetInstalledApps();
            if (!ct.IsCancellationRequested && !Dispatcher.HasShutdownStarted)
            {
                Dispatcher.Invoke(() =>
                {
                    foreach (var app in apps)
                        _viewModel.AvailableApps.Add(new AppItemViewModel(app));
                    _viewModel.SearchText = _viewModel.SearchText; // trigger filter
                });
            }
        }, ct).ContinueWith(t =>
        {
            // Safety: always dismiss overlay even if discovery throws
            if (t.IsFaulted)
                Dispatcher.Invoke(HideLoadingOverlay);
        }, TaskScheduler.Default);
    }

    private async Task DismissLoadingOverlaySoonAsync(CancellationToken ct)
    {
        try { await Task.Delay(550, ct); }
        catch (OperationCanceledException) { return; }
        if (!Dispatcher.HasShutdownStarted)
            _ = Dispatcher.BeginInvoke((Action)HideLoadingOverlay);
    }

    private void HideLoadingOverlay()
    {
        if (LoadingOverlay.Visibility != Visibility.Visible) return;

        var fadeOut = new DoubleAnimation(1.0, 0.0,
            new Duration(TimeSpan.FromMilliseconds(260)));
        fadeOut.Completed += (_, _) =>
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            LoadingOverlay.BeginAnimation(OpacityProperty, null);
        };
        LoadingOverlay.BeginAnimation(OpacityProperty, fadeOut);
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isRealExit)
        {
            _loadCts.Cancel();
            // Ensure VPN is disconnected even if shutdown is triggered externally
            if (_viewModel.IsConnected)
            {
                e.Cancel = true;
                try { await _viewModel.DisconnectAndCleanupAsync(); } catch { }
                _isRealExit = true;
                _trayIcon?.Dispose();
                Application.Current.Shutdown();
                return;
            }
            _viewModel.ForceSave();
            _trayIcon?.Dispose();
            return;
        }

        // X button → minimize to tray instead of closing
        e.Cancel = true;
        MinimizeToTray();
    }


    // Window control handlers
    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        // Minimize button → minimize to tray
        MinimizeToTray();
    }

    private async void OnCloseClick(object sender, RoutedEventArgs e)
    {
        // X button → show confirmation and exit
        string message = _viewModel.IsConnected 
            ? "اتصال VPN فعال است. با خروج، اتصال قطع خواهد شد.\nآیا مطمئن هستید؟"
            : "آیا می‌خواهید از TunnelX خارج شوید؟";

        if (DialogService.Confirm(message, "TunnelX — خروج", this))
        {
            if (_viewModel.IsConnected)
            {
                try { await _viewModel.DisconnectAndCleanupAsync(); }
                catch { }
            }

            _isRealExit = true;
            _trayIcon?.Dispose();
            _trayIcon = null;
            Application.Current.Shutdown();
        }
    }

    private async void OnExitAppClick(object sender, RoutedEventArgs e)
    {
        // Show confirmation dialog
        string message = _viewModel.IsConnected 
            ? "اتصال VPN فعال است. با خروج، اتصال قطع خواهد شد.\nآیا مطمئن هستید؟"
            : "آیا می‌خواهید از TunnelX خارج شوید؟";

        if (DialogService.Confirm(message, "TunnelX — خروج", this))
        {
            if (_viewModel.IsConnected)
            {
                try { await _viewModel.DisconnectAndCleanupAsync(); }
                catch { }
            }

            _isRealExit = true;
            _trayIcon?.Dispose();
            _trayIcon = null;
            Application.Current.Shutdown();
        }
    }

    private bool _logPanelLoaded;
    private const double LogPanelWidth = 350;
    private string _logFilter = "All";

    private void OnShowLogClick(object sender, RoutedEventArgs e)
    {
        if (LogPanel.Visibility == Visibility.Visible)
        {
            LogPanel.Visibility = Visibility.Collapsed;
            Width -= LogPanelWidth;
            return;
        }

        if (!_logPanelLoaded)
        {
            _logPanelLoaded = true;
            LogTextBox.Text = ApplyLogFilter(Logger.GetAllLogs());
            LogTextBox.ScrollToEnd();
            Logger.LogAdded += OnLogAdded;
        }

        Width += LogPanelWidth;
        LogPanel.Visibility = Visibility.Visible;
        LogTextBox.ScrollToEnd();
    }

    private void OnLogAdded(string logEntry)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!LogMatchesFilter(logEntry)) return;
            LogTextBox.AppendText(logEntry + Environment.NewLine);
            LogTextBox.ScrollToEnd();
        });
    }

    private void OnLogClearClick(object sender, RoutedEventArgs e)
    {
        Logger.Clear();
        LogTextBox.Clear();
    }

    private void OnLogCopyClick(object sender, RoutedEventArgs e)
    {
        try { System.Windows.Clipboard.SetText(LogTextBox.Text); }
        catch { }
    }

    private void OnLogCopyLastErrorClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var lines = Logger.GetAllLogs()
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            var line = lines.Reverse().FirstOrDefault(l =>
                l.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase)) ??
                       lines.Reverse().FirstOrDefault(l =>
                l.Contains("[WARN]", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(line))
                System.Windows.Clipboard.SetText(line);
        }
        catch { }
    }

    private void OnLogFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LogFilterCombo?.SelectedItem is ComboBoxItem item &&
            item.Tag is string tag)
            _logFilter = tag;

        if (_logPanelLoaded)
        {
            LogTextBox.Text = ApplyLogFilter(Logger.GetAllLogs());
            LogTextBox.ScrollToEnd();
        }
    }

    private string ApplyLogFilter(string logs)
    {
        if (_logFilter == "All") return logs;

        var lines = logs.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .Where(LogMatchesFilter);
        return string.Join(Environment.NewLine, lines);
    }

    private bool LogMatchesFilter(string line) => _logFilter switch
    {
        "Error" => line.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase),
        "Warn" => line.Contains("[WARN]", StringComparison.OrdinalIgnoreCase),
        "Dns" => line.Contains("[DNS", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("DNS-", StringComparison.OrdinalIgnoreCase),
        "Route" => line.Contains("[ROUTE", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("[FULL-ROUTE]", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("route.exe", StringComparison.OrdinalIgnoreCase),
        _ => true
    };

    private void OnShowHelpClick(object sender, RoutedEventArgs e)
    {
        var helpWindow = new AppTunnel.Views.HelpWindow
        {
            Owner = this
        };
        helpWindow.ShowDialog();
    }

    private void OnNestedScrollPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DependencyObject source) return;

        var parent = FindVisualParent<ScrollViewer>(source);
        if (parent == null) return;

        e.Handled = true;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source = sender
        });
    }

    private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        var parent = VisualTreeHelper.GetParent(child);
        while (parent != null)
        {
            if (parent is T typed)
                return typed;
            parent = VisualTreeHelper.GetParent(parent);
        }
        return null;
    }

    private void OnOuterBorderSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var border = (System.Windows.Controls.Border)sender;
        border.Clip = new System.Windows.Media.RectangleGeometry(
            new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 12, 12);
    }

    private CancellationTokenSource? _toastCts;

    public void ShowToast(string message, string icon = "✅", int durationMs = 3000)
    {
        _toastCts?.Cancel();
        _toastCts = new CancellationTokenSource();
        var token = _toastCts.Token;

        ToastIcon.Text = icon;
        ToastMessage.Text = message;
        ToastPanel.Visibility = Visibility.Visible;
        ToastPanel.Opacity = 1;

        Task.Delay(durationMs, token).ContinueWith(_ =>
        {
            if (!token.IsCancellationRequested)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300));
                    fadeOut.Completed += (__, ___) => ToastPanel.Visibility = Visibility.Collapsed;
                    ToastPanel.BeginAnimation(OpacityProperty, fadeOut);
                });
            }
        }, TaskScheduler.Default);
    }
}
