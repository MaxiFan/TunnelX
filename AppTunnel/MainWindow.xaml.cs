using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AppTunnel.Models;
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
    private bool _isInTray;
    private bool _isRealExit;
    private ConnectionState _lastNotifiedConnectionState = ConnectionState.Disconnected;
    private bool _updateNotificationShown;
    private readonly ConcurrentQueue<string> _pendingLogEntries = new();
    private int _logFlushScheduled;
    private const int WmSysCommand = 0x0112;
    private const int WmGetMinMaxInfo = 0x0024;
    private const int ScSize = 0xF000;
    private const int ScMove = 0xF010;
    private const int ScMaximize = 0xF030;
    private const int ScKeyMenu = 0xF100;
    private const int ScRestore = 0xF120;
    private const int ScMinimize = 0xF020;
    private const int SwRestore = 9;
    private const int SwShow = 5;
    private const int SwMinimize = 6;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        LocalizationService.Instance.ApplyTo(this);
        LocalizationService.Instance.LanguageChanged += (_, _) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                LocalizationService.Instance.ApplyTo(this);
                RefreshTrayText();
            });
        };

        AppNotificationService.Initialize(this);
        InitializeTrayIcon();
        PreviewKeyDown += OnWindowPreviewKeyDown;
        Loaded += OnLoaded;
        Closing += OnClosing;
        _viewModel.SelectedMainTabIndex = 0;
    }

    private void OnMainTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MainTabControl.SelectedIndex >= 0)
            _viewModel.SelectedMainTabIndex = MainTabControl.SelectedIndex;
    }

    private void OnWindowPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control)
            return;

        if (IsTextInputFocusedForQuickImport())
            return;

        if (!_viewModel.TryImportConfigsFromClipboard())
            return;

        e.Handled = true;
    }

    private static bool IsTextInputFocusedForQuickImport()
    {
        var focused = Keyboard.FocusedElement;
        return focused is System.Windows.Controls.TextBox
            or System.Windows.Controls.PasswordBox
            or System.Windows.Controls.Primitives.TextBoxBase
            or System.Windows.Controls.ComboBox;
    }

    private const double DesignWidth = 640;
    private const double DesignHeight = 800;
    private const double DesignMinWidth = 580;
    private const double DesignMinHeight = 680;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyAdaptiveWindowSize();
        MaxWidth = Width;
        MaxHeight = Height;

        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(WindowMessageHook);
    }

    private void ApplyAdaptiveWindowSize()
    {
        Width = DesignWidth;
        Height = DesignHeight;
        MinWidth = DesignMinWidth;
        MinHeight = DesignMinHeight;

        var workArea = SystemParameters.WorkArea;
        const double screenMargin = 24;

        Width = Math.Min(DesignWidth, Math.Max(DesignMinWidth, workArea.Width - screenMargin));
        Height = Math.Min(DesignHeight, Math.Max(DesignMinHeight, workArea.Height - screenMargin));

        Left = workArea.Left + Math.Max(0, (workArea.Width - Width) / 2);
        Top = workArea.Top + Math.Max(0, (workArea.Height - Height) / 2);
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmSysCommand)
        {
            var command = wParam.ToInt32() & 0xFFF0;
            if (command is ScMaximize or ScSize or ScKeyMenu)
            {
                handled = true;
                return IntPtr.Zero;
            }
        }
        else if (msg == WmGetMinMaxInfo)
        {
            MaxWidth = Width;
            MaxHeight = Height;
        }

        return IntPtr.Zero;
    }

    private void MinimizeToTaskbar()
    {
        _isInTray = false;
        if (_trayIcon != null)
            _trayIcon.Visible = false;

        ShowInTaskbar = true;
        WindowState = WindowState.Minimized;

        if (PresentationSource.FromVisual(this) is HwndSource source)
            ShowWindow(source.Handle, SwMinimize);
    }

    private void InitializeTrayIcon()
    {
        var iconStream = Application.GetResourceStream(
            new Uri("pack://application:,,,/app.ico"))?.Stream;
        
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = LocalizationService.Instance.T("TunnelX — Split Traffic Per App"),
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

        var statusItem = new System.Windows.Forms.ToolStripMenuItem($"{LocalizationService.Instance.T("وضعیت")}: {_viewModel.StatusText}");
        statusItem.Enabled = false;
        menu.Items.Add(statusItem);

        // Update status text dynamically
        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.StatusText))
            {
                Dispatcher.BeginInvoke(() =>
                {
                    statusItem.Text = $"{LocalizationService.Instance.T("وضعیت")}: {_viewModel.StatusText}";
                });
            }
            else if (args.PropertyName == nameof(MainViewModel.ConnectionState))
            {
                Dispatcher.BeginInvoke(NotifyConnectionStateChanged);
            }
            else if (args.PropertyName == nameof(MainViewModel.IsUpdateAvailable))
            {
                Dispatcher.BeginInvoke(NotifyUpdateAvailable);
            }
        };

        var updateItem = new System.Windows.Forms.ToolStripMenuItem("بررسی بروزرسانی");
        updateItem.Click += (_, _) =>
        {
            BringToForeground();
            if (_viewModel.CheckForUpdatesCommand.CanExecute(null))
                _viewModel.CheckForUpdatesCommand.Execute(null);
        };
        menu.Items.Add(updateItem);

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var exitItem = new System.Windows.Forms.ToolStripMenuItem("خروج از برنامه");
        exitItem.Click += (_, _) =>
        {
            Dispatcher.InvokeAsync(async () => await TryExitApplicationAsync());
        };
        menu.Items.Add(exitItem);

        _trayIcon.ContextMenuStrip = menu;
        RefreshTrayText();
    }

    private void RefreshTrayText()
    {
        if (_trayIcon?.ContextMenuStrip == null) return;

        _trayIcon.Text = LocalizationService.Instance.T("TunnelX — Split Traffic Per App");
        var items = _trayIcon.ContextMenuStrip.Items;
        if (items.Count > 0 && items[0] is System.Windows.Forms.ToolStripMenuItem showItem)
            showItem.Text = LocalizationService.Instance.T("نمایش TunnelX");
        if (items.Count > 2 && items[2] is System.Windows.Forms.ToolStripMenuItem statusItem)
            statusItem.Text = $"{LocalizationService.Instance.T("وضعیت")}: {_viewModel.StatusText}";
        if (items.Count > 3 && items[3] is System.Windows.Forms.ToolStripMenuItem updateItem)
            updateItem.Text = LocalizationService.Instance.T("بررسی بروزرسانی");
        if (items.Count > 5 && items[5] is System.Windows.Forms.ToolStripMenuItem exitItem)
            exitItem.Text = LocalizationService.Instance.T("خروج از برنامه");
    }

    private void ShowFromTray()
    {
        BringToForeground();
    }

    public void BringToForeground()
    {
        void ShowMainWindow()
        {
            _isInTray = false;
            if (_trayIcon != null)
                _trayIcon.Visible = false;

            Show();
            Visibility = Visibility.Visible;
            WindowState = WindowState.Normal;

            if (PresentationSource.FromVisual(this) is HwndSource source)
            {
                ShowWindow(source.Handle, SwRestore);
                ShowWindow(source.Handle, SwShow);
                SetForegroundWindow(source.Handle);
            }

            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        }

        if (Dispatcher.CheckAccess())
            ShowMainWindow();
        else
            Dispatcher.Invoke(ShowMainWindow);
    }

    /// <summary>
    /// Borderless windows can become Minimized without a tray hand-off: process stays alive,
    /// but there is no window, no tray icon, and no notification. Recover on startup or relaunch.
    /// </summary>
    internal void EnsureStartupVisible()
    {
        if (_isRealExit || _isInTray)
            return;

        if (!IsVisible || WindowState == WindowState.Minimized)
            BringToForeground();
    }

    private void MinimizeToTray()
    {
        if (_isInTray && !IsVisible)
            return;

        _isInTray = true;
        WindowState = WindowState.Normal;
        Hide();
        if (_trayIcon != null)
        {
            _trayIcon.Visible = true;
            AppNotificationService.ShowTrayForced(
                "TunnelX در System Tray است",
                "برنامه بسته نشده است. برای باز کردن، روی آیکن کنار ساعت دوبار کلیک کنید.",
                AppNotificationKind.Info);
        }
    }

    private void NotifyConnectionStateChanged()
    {
        if (_trayIcon == null) return;

        var state = _viewModel.ConnectionState;
        if (state == _lastNotifiedConnectionState) return;
        _lastNotifiedConnectionState = state;

        switch (state)
        {
            case ConnectionState.Connected:
                {
                    var profileName = _viewModel.SelectedProfileName;
                    if (!string.IsNullOrWhiteSpace(profileName))
                    {
                        AppNotificationService.ShowTrayFormat(
                            "تونل فعال شد",
                            "پروفایل «{0}» فعال است و ترافیک انتخاب‌شده از تونل عبور می‌کند.",
                            AppNotificationKind.Success,
                            AppNotificationChannel.Informational,
                            profileName);
                    }
                    else
                    {
                        AppNotificationService.ShowTray(
                            "تونل فعال شد",
                            "ترافیک انتخاب‌شده از TunnelX عبور می‌کند.",
                            AppNotificationKind.Success);
                    }

                    TelegramChannelPromoService.ScheduleAfterSuccessfulConnection();
                }
                break;
            case ConnectionState.Disconnected:
                _updateNotificationShown = false;
                UpdateCheckSchedulerService.OnDisconnected();
                AppNotificationService.DismissUpdateToast();
                TelegramChannelPromoService.OnDisconnected();
                AppNotificationService.ShowTray(
                    "تونل خاموش شد",
                    "ارتباط امن متوقف شده و ترافیک دیگر از TunnelX عبور نمی‌کند.",
                    AppNotificationKind.Info);
                break;
            case ConnectionState.Error:
                _updateNotificationShown = false;
                UpdateCheckSchedulerService.OnDisconnected();
                AppNotificationService.DismissUpdateToast();
                TelegramChannelPromoService.OnDisconnected();
                AppNotificationService.ShowTrayLiteralBody(
                    "اتصال برقرار نشد",
                    _viewModel.BuildConnectionFailureTrayMessage(),
                    AppNotificationKind.Warning);
                break;
        }
    }

    private void NotifyUpdateAvailable()
    {
        if (_trayIcon == null || _updateNotificationShown || !_viewModel.IsUpdateAvailable)
            return;

        _updateNotificationShown = true;
        AppNotificationService.ShowUpdateAvailableTrayPersistent(
            () =>
            {
                if (_viewModel.OpenLatestReleaseCommand.CanExecute(null))
                    _viewModel.OpenLatestReleaseCommand.Execute(null);
            },
            () => _viewModel.ShowLatestReleaseChangelog());
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isInTray = false;
        Visibility = Visibility.Visible;
        WindowState = WindowState.Normal;

        LocalizationService.Instance.ApplyTo(this);
        UpdateLogPanelCornerState(LogPanel.Visibility == Visibility.Visible);
        RefreshTrayText();

        // Force window to foreground — borderless/transparent windows sometimes
        // start behind other windows or appear unfocused on slower machines.
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();

        // Dismiss the startup overlay quickly; app discovery continues in the
        // background so the first screen becomes usable immediately.
        var ct = _loadCts.Token;
        LoadingStatusText.Text = LocalizationService.Instance.T("بارگذاری لیست برنامه‌های نصب‌شده...");
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
            _viewModel.ForceSave();
            _trayIcon?.Dispose();
            return;
        }

        e.Cancel = true;
        await TryExitApplicationAsync();
    }


    // Window control handlers
    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
        => MinimizeToTaskbar();

    private void OnSendToTrayClick(object sender, RoutedEventArgs e)
        => MinimizeToTray();

    private async void OnCloseClick(object sender, RoutedEventArgs e)
        => await TryExitApplicationAsync();

    private async void OnExitAppClick(object sender, RoutedEventArgs e)
        => await TryExitApplicationAsync();

    private async Task TryExitApplicationAsync()
    {
        string message;
        if (_viewModel.IsConnected)
            message = "اتصال VPN فعال است. با خروج، اتصال قطع خواهد شد.\nآیا مطمئن هستید؟";
        else if (_viewModel.IsConnectionPending || _viewModel.IsBusy)
            message = "اتصال در جریان است. با خروج، تلاش اتصال قطع می‌شود.\nآیا مطمئن هستید؟";
        else
            message = "آیا می‌خواهید از TunnelX خارج شوید؟";

        if (!DialogService.Confirm(message, "TunnelX — خروج", this))
            return;

        _isRealExit = true;
        _loadCts.Cancel();
        _trayIcon?.Dispose();
        _trayIcon = null;

        ShowShutdownOverlay();

        try { await _viewModel.PrepareForApplicationExitAsync(); }
        catch (Exception ex) { Logger.Warning($"[EXIT] Shutdown preparation failed: {ex.Message}"); }

        App.ForceTerminateApplication();
    }

    private void ShowShutdownOverlay()
    {
        ShutdownStatusText.Text = LocalizationService.Instance.T("در حال بستن پروسس‌ها و خروج...");
        ShutdownOverlay.Visibility = Visibility.Visible;
        ShutdownOverlay.IsHitTestVisible = true;
        IsEnabled = false;
        UpdateLayout();
    }

    private bool _logPanelLoaded;
    private bool _isLogPanelAnimating;
    private const double LogPanelWidth = 350;
    private static readonly Duration LogPanelAnimationDuration = TimeSpan.FromMilliseconds(220);
    private string _logFilter = "All";

    private void OnShowLogClick(object sender, RoutedEventArgs e)
    {
        if (_isLogPanelAnimating)
            return;

        if (!_logPanelLoaded)
        {
            _logPanelLoaded = true;
            LogTextBox.Text = ApplyLogFilter(Logger.GetAllLogs());
            LogTextBox.ScrollToEnd();
            Logger.LogAdded += OnLogAdded;
        }

        var shouldOpen = LogPanel.Visibility != Visibility.Visible;
        AnimateLogPanel(shouldOpen);
    }

    private void AnimateLogPanel(bool open)
    {
        _isLogPanelAnimating = true;
        UpdateLogPanelCornerState(open);

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var currentPanelWidth = LogPanel.Visibility == Visibility.Visible ? LogPanel.ActualWidth : 0;
        var maxWindowWidth = SystemParameters.WorkArea.Width - 24;
        var availablePanelWidth = Math.Max(0, maxWindowWidth - (Width - currentPanelWidth));
        var targetPanelWidth = open ? Math.Min(LogPanelWidth, availablePanelWidth) : 0;
        var targetWindowWidth = open
            ? Math.Min(maxWindowWidth, Width + (targetPanelWidth - currentPanelWidth))
            : Math.Max(MinWidth, Width - currentPanelWidth);

        if (open)
        {
            LogPanel.Visibility = Visibility.Visible;
            LogTextBox.ScrollToEnd();
        }

        var windowAnimation = new DoubleAnimation(targetWindowWidth, LogPanelAnimationDuration)
        {
            EasingFunction = easing
        };
        var panelAnimation = new DoubleAnimation(targetPanelWidth, LogPanelAnimationDuration)
        {
            EasingFunction = easing
        };
        var opacityAnimation = new DoubleAnimation(open ? 1 : 0, LogPanelAnimationDuration)
        {
            EasingFunction = easing
        };

        panelAnimation.Completed += (_, _) =>
        {
            BeginAnimation(WidthProperty, null);
            LogPanel.BeginAnimation(FrameworkElement.WidthProperty, null);
            LogPanel.BeginAnimation(OpacityProperty, null);

            Width = targetWindowWidth;
            LogPanel.Width = targetPanelWidth;
            LogPanel.Opacity = open ? 1 : 0;

            if (!open)
                LogPanel.Visibility = Visibility.Collapsed;

            _isLogPanelAnimating = false;
        };

        BeginAnimation(WidthProperty, windowAnimation);
        LogPanel.BeginAnimation(FrameworkElement.WidthProperty, panelAnimation);
        LogPanel.BeginAnimation(OpacityProperty, opacityAnimation);
    }

    private void UpdateLogPanelCornerState(bool logOpen)
    {
        if (logOpen)
        {
            MainTitleBar.CornerRadius = new CornerRadius(12, 0, 0, 0);
            MainFooterBar.CornerRadius = new CornerRadius(0, 0, 0, 12);
        }
        else
        {
            MainTitleBar.CornerRadius = new CornerRadius(12, 12, 0, 0);
            MainFooterBar.CornerRadius = new CornerRadius(0, 0, 12, 12);
        }
    }

    private void OnLogAdded(string logEntry)
    {
        _pendingLogEntries.Enqueue(logEntry);
        if (Interlocked.Exchange(ref _logFlushScheduled, 1) != 0)
            return;

        Dispatcher.BeginInvoke(
            new Action(FlushPendingLogEntries),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void FlushPendingLogEntries()
    {
        Interlocked.Exchange(ref _logFlushScheduled, 0);

        var appended = false;
        var batch = new System.Text.StringBuilder();
        var count = 0;
        while (count < 200 && _pendingLogEntries.TryDequeue(out var entry))
        {
            if (LogMatchesFilter(entry))
            {
                batch.AppendLine(entry);
                appended = true;
            }
            count++;
        }

        if (appended)
        {
            LogTextBox.AppendText(batch.ToString());
            LogTextBox.ScrollToEnd();
        }

        if (!_pendingLogEntries.IsEmpty &&
            Interlocked.Exchange(ref _logFlushScheduled, 1) == 0)
        {
            Dispatcher.BeginInvoke(
                new Action(FlushPendingLogEntries),
                System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private void OnLogClearClick(object sender, RoutedEventArgs e)
    {
        Logger.Clear();
        LogTextBox.Clear();
        ShowLogToast(LocalizationService.Instance.T("لاگ‌ها پاک شدند"), LogToastKind.Success);
    }

    private void OnLogCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(LogTextBox.Text))
            {
                ShowLogToast(LocalizationService.Instance.T("لاگی برای کپی وجود ندارد"), LogToastKind.Info);
                return;
            }

            System.Windows.Clipboard.SetText(LogTextBox.Text);
            ShowLogToast(LocalizationService.Instance.T("لاگ کپی شد"), LogToastKind.Success);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[UI] Copy logs failed: {ex.Message}");
            ShowLogToast(LocalizationService.Instance.T("کپی لاگ ناموفق بود"), LogToastKind.Warning);
        }
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
            if (string.IsNullOrWhiteSpace(line))
            {
                ShowLogToast(LocalizationService.Instance.T("آخرین خطا یا هشدار پیدا نشد"), LogToastKind.Info);
                return;
            }

            System.Windows.Clipboard.SetText(line);
            ShowLogToast(LocalizationService.Instance.T("آخرین خطا یا هشدار کپی شد"), LogToastKind.Success);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[UI] Copy last error/warning failed: {ex.Message}");
            ShowLogToast(LocalizationService.Instance.T("کپی لاگ ناموفق بود"), LogToastKind.Warning);
        }
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
            Owner = this,
            DataContext = _viewModel
        };
        LocalizationService.Instance.ApplyTo(helpWindow);
        helpWindow.ShowDialog();
    }

    private void OnDonateClick(object sender, RoutedEventArgs e)
    {
        var donationDialog = new AppTunnel.Views.DonationDialog
        {
            Owner = this
        };
        LocalizationService.Instance.ApplyTo(donationDialog);
        donationDialog.ShowDialog();
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
    private CancellationTokenSource? _logToastCts;

    private enum LogToastKind
    {
        Success,
        Info,
        Warning
    }

    private void ShowLogToast(string message, LogToastKind kind = LogToastKind.Success, int durationMs = 2800)
    {
        _logToastCts?.Cancel();
        _logToastCts = new CancellationTokenSource();
        var token = _logToastCts.Token;

        ApplyLogToastStyle(kind);
        LogToastIcon.Text = kind switch
        {
            LogToastKind.Success => "✓",
            LogToastKind.Info => "ℹ",
            LogToastKind.Warning => "⚠",
            _ => "•"
        };
        LogToastMessage.Text = message;
        LogToastPanel.Visibility = Visibility.Visible;
        LogToastPanel.Opacity = 1;
        LogToastPanel.BeginAnimation(OpacityProperty, null);

        Task.Delay(durationMs, token).ContinueWith(_ =>
        {
            if (token.IsCancellationRequested)
                return;

            Dispatcher.BeginInvoke(() =>
            {
                var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(280));
                fadeOut.Completed += (__, ___) => LogToastPanel.Visibility = Visibility.Collapsed;
                LogToastPanel.BeginAnimation(OpacityProperty, fadeOut);
            });
        }, TaskScheduler.Default);
    }

    private void ApplyLogToastStyle(LogToastKind kind)
    {
        LogToastPanel.Background = TryFindResource("CardBrush") as System.Windows.Media.Brush
            ?? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2D, 0x2D, 0x2D));
        LogToastMessage.Foreground = TryFindResource("TextPrimaryBrush") as System.Windows.Media.Brush
            ?? System.Windows.Media.Brushes.White;

        var accent = kind switch
        {
            LogToastKind.Info => TryFindResource("AccentBrush"),
            LogToastKind.Warning => TryFindResource("WarningBrush"),
            _ => TryFindResource("SuccessBrush")
        } as System.Windows.Media.Brush ?? TryFindResource("AccentBrush") as System.Windows.Media.Brush;

        LogToastPanel.BorderBrush = accent ?? System.Windows.Media.Brushes.Orange;
        LogToastIcon.Foreground = accent ?? LogToastMessage.Foreground;
    }

    public void ShowToast(string message, string icon = "✅", int durationMs = 3000)
    {
        var kind = icon switch
        {
            "⚠" or "⚠️" => AppNotificationKind.Warning,
            "❌" or "✖" => AppNotificationKind.Error,
            "ℹ" or "ℹ️" => AppNotificationKind.Info,
            _ => AppNotificationKind.Success
        };
        ShowAppToast(message, kind, durationMs);
    }

    public void ShowAppToast(string message, AppNotificationKind kind = AppNotificationKind.Success, int durationMs = 3000)
    {
        _toastCts?.Cancel();
        _toastCts = new CancellationTokenSource();
        var token = _toastCts.Token;

        ApplyAppToastStyle(kind);
        ToastIcon.Text = kind switch
        {
            AppNotificationKind.Warning => "⚠",
            AppNotificationKind.Error => "❌",
            AppNotificationKind.Info => "ℹ",
            _ => "✅"
        };
        ToastMessage.Text = message;
        ToastPanel.Visibility = Visibility.Visible;
        ToastPanel.Opacity = 1;
        ToastPanel.BeginAnimation(OpacityProperty, null);
        ToastSlideTransform.Y = 12;
        ToastSlideTransform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });

        Task.Delay(durationMs, token).ContinueWith(_ =>
        {
            if (token.IsCancellationRequested)
                return;

            Dispatcher.BeginInvoke(() =>
            {
                var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(280));
                fadeOut.Completed += (__, ___) => ToastPanel.Visibility = Visibility.Collapsed;
                ToastPanel.BeginAnimation(OpacityProperty, fadeOut);
            });
        }, TaskScheduler.Default);
    }

    private void ApplyAppToastStyle(AppNotificationKind kind)
    {
        ToastPanel.Background = TryFindResource("CardBrush") as System.Windows.Media.Brush
            ?? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2D, 0x2D, 0x2D));
        ToastMessage.Foreground = TryFindResource("TextPrimaryBrush") as System.Windows.Media.Brush
            ?? System.Windows.Media.Brushes.White;

        var accent = kind switch
        {
            AppNotificationKind.Info => TryFindResource("AccentBrush"),
            AppNotificationKind.Warning => TryFindResource("WarningBrush"),
            AppNotificationKind.Error => TryFindResource("ErrorBrush"),
            _ => TryFindResource("SuccessBrush")
        } as System.Windows.Media.Brush ?? TryFindResource("AccentBrush") as System.Windows.Media.Brush;

        ToastIcon.Foreground = accent ?? ToastMessage.Foreground;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
