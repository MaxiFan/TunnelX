using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Application = System.Windows.Application;
using System.Windows.Threading;
using AppTunnel.Helpers;
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
    private ProfileService.AppSettings _appSettings = new();
    private bool _isLoadingProfile;
    private const string StartupRegistryRunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string StartupRegistryValueName = "TunnelX";

    public MainViewModel()
    {
        ConnectCommand = new RelayCommand(_ =>
        {
            // Fire-and-forget safety: catch exceptions to avoid unobserved task exceptions
            _ = ToggleConnectionAsync().ContinueWith(t =>
            {
                if (t.IsFaulted)
                    Application.Current?.Dispatcher.Invoke(() =>
                        StatusText = LocalizationService.Instance.Format("خطا: {0}", t.Exception?.InnerException?.Message));
            }, TaskScheduler.Default);
        }, _ => !IsBusy || ConnectionState == ConnectionState.Connecting);
        AddAppCommand = new RelayCommand(_ => AddCustomApp());
        RemoveAppCommand = new RelayCommand(RemoveApp);
        ToggleAppCommand = new RelayCommand(ToggleApp);
        RefreshAppsCommand = new RelayCommand(_ => LoadInstalledApps(), _ => !IsBusy);

        // Profile commands
        NewProfileCommand = new RelayCommand(_ => CreateNewProfile(), _ => !IsConnected);
        DeleteProfileCommand = new RelayCommand(DeleteCurrentProfile, _ => !IsConnected && Profiles.Count > 1);
        DuplicateProfileCommand = new RelayCommand(DuplicateCurrentProfile, _ => !IsConnected);
        EditProfileCommand = new RelayCommand(EditProfile, _ => !IsConnected);
        SelectProfileCommand = new RelayCommand(SelectProfile, _ => !IsConnected);
        ImportConfigsFromClipboardCommand = new RelayCommand(_ => ImportConfigsFromClipboard(), _ => CanUseConnectionTabQuickActions && !IsImportingConfigs);
        TestSelectedProfileLatencyCommand = new RelayCommand(
            _ => _ = TestProfileLatencyAsync(SelectedProfile),
            _ => CanUseConnectionTabQuickActions && !IsTestingProfileLatency && !IsTestingAllProfilesLatency && !IsTestingProfileServerPing
                 && SelectedProfile is { SupportsServerPing: true });
        TestProfileLatencyCommand = new RelayCommand(
            p => _ = TestProfileLatencyAsync(p as ConnectionProfile),
            p => CanUseConnectionTabQuickActions && !IsTestingProfileLatency && !IsTestingAllProfilesLatency && !IsTestingProfileServerPing
                 && p is ConnectionProfile profile && profile.SupportsServerPing);
        TestProfileServerPingCommand = new RelayCommand(
            p => _ = TestProfileServerPingAsync(p as ConnectionProfile),
            p => CanUseConnectionTabQuickActions && !IsTestingProfileServerPing && !IsTestingProfileLatency && !IsTestingAllProfilesLatency
                 && p is ConnectionProfile profile && profile.ShowsServerPingButton);
        TestAllProfilesLatencyCommand = new RelayCommand(_ => _ = TestAllProfilesLatencyAsync(), _ => CanUseConnectionTabQuickActions && !IsTestingAllProfilesLatency && !IsTestingProfileLatency && HasReadyProfilesForLatencyTest);
        CancelProfileLatencyTestCommand = new RelayCommand(_ => CancelProfileLatencyTest(), _ => IsTestingProfileLatency || IsTestingAllProfilesLatency);

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
        TestConnectedServerPingCommand = new RelayCommand(_ => _ = TestConnectedServerPingAsync(), _ => IsConnected && !IsTestingConnectedServerPing);
        TestServerPingCommand = new RelayCommand(_ => _ = TestServerPingAsync(), _ => !IsConnected && !IsTestingServerPing);
        PasteConfigCommand = new RelayCommand(_ => PasteConfigFromClipboard(), _ => !IsConnected && (CurrentTunnelType == TunnelType.V2Ray || CurrentTunnelType == TunnelType.OpenVpn || CurrentTunnelType == TunnelType.WireGuard));
        ClearConfigCommand = new RelayCommand(_ => ClearCurrentConfig(), _ => !IsConnected && (CurrentTunnelType == TunnelType.V2Ray || CurrentTunnelType == TunnelType.OpenVpn || CurrentTunnelType == TunnelType.WireGuard));
        BrowseOpenVpnConfigCommand = new RelayCommand(_ => BrowseForOpenVpnConfig(), _ => !IsConnected && CurrentTunnelType == TunnelType.OpenVpn);
        BrowseWireGuardConfigCommand = new RelayCommand(_ => BrowseForWireGuardConfig(), _ => !IsConnected && CurrentTunnelType == TunnelType.WireGuard);
        OpenOpenVpnCommunityDownloadCommand = new RelayCommand(_ => OpenExternalLink(OpenVpnCommunityDownloadUrl));
        OpenWireGuardDownloadCommand = new RelayCommand(_ => OpenExternalLink(WireGuardDownloadUrl));
        OpenGitHubCommand = new RelayCommand(_ => OpenExternalLink(AppInfo.GitHubUrl));
        OpenDonateCommand = new RelayCommand(_ => OpenExternalLink(AppInfo.PayPalDonateUrl));
        OpenAdRequestCommand = new RelayCommand(_ => OpenExternalLink(AppInfo.TelegramContactUrl));
        OpenTelegramChannelCommand = new RelayCommand(_ => OpenTelegramChannel());
        CopyDonationInfoCommand = new RelayCommand(_ => CopyDonationInfoToClipboard());
        CopyHelpCryptoAddressCommand = new RelayCommand(p => CopyHelpCryptoAddress(p as string));
        CheckForUpdatesCommand = new RelayCommand(
            _ => _ = CheckForUpdatesAsync(silent: false),
            _ => !IsCheckingForUpdates && IsConnected);
        OpenLatestReleaseCommand = new RelayCommand(_ => OpenExternalLink(LatestReleaseUrl), _ => !string.IsNullOrWhiteSpace(LatestReleaseUrl));
        ToggleLanguageCommand = new RelayCommand(_ => ToggleLanguage());

        CancelConnectionCommand = new RelayCommand(
            _ => _ = CancelConnectingAsync(),
            _ => _connectionState == ConnectionState.Connecting);

        SubscribeConnectionProgress();

        LocalizationService.Instance.LanguageChanged += (_, _) => OnLanguageChanged();

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
        LoadAppSettings();
        RefreshOpenVpnInstallStatus();
        RefreshWireGuardInstallStatus();

        RefreshHelpCryptoWalletRows();

        QueueAutoConnectToLastProfile();
    }

    #region Properties

    private string _serverAddress = "";
    public string ServerAddress
    {
        get => _serverAddress;
        set { _serverAddress = value; OnPropertyChanged(); UpdateConfigDiagnostics(); RaiseProfileCardChanged(); SaveCurrentState(); }
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

    private int _mixedProxyPort = 1080;
    public int MixedProxyPort
    {
        get => _mixedProxyPort;
        set
        {
            var normalized = value;
            if (_mixedProxyPort == normalized) return;
            _mixedProxyPort = normalized;
            _trafficRouter.Socks5Port = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MixedProxyPortText));
            OnPropertyChanged(nameof(MixedProxyInfo));
            UpdateMixedProxyPortStatus();
            SaveCurrentState();
        }
    }

    public string MixedProxyPortText
    {
        get => _mixedProxyPort.ToString();
        set
        {
            if (int.TryParse((value ?? "").Trim(), out var port))
            {
                if (_mixedProxyPort != port)
                {
                    _mixedProxyPort = port;
                    _trafficRouter.Socks5Port = port;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(MixedProxyPort));
                    OnPropertyChanged(nameof(MixedProxyInfo));
                    UpdateMixedProxyPortStatus();
                    SaveCurrentState();
                }
                return;
            }

            MixedProxyPortStatusText = string.IsNullOrWhiteSpace(value)
                ? "پورت SOCKS5 را وارد کنید"
                : "فقط عدد مجاز است";
        }
    }

    private string _mixedProxyPortStatusText = "";
    public string MixedProxyPortStatusText
    {
        get => LocalizationService.Instance.T(_mixedProxyPortStatusText);
        set { _mixedProxyPortStatusText = value; OnPropertyChanged(); }
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
        ? LocalizationService.Instance.T("Game Mode فعال است: Route نگهداری طولانی‌تر، DNS سریع‌تر و DSCP برای بسته‌های بازی اعمال می‌شود.")
        : LocalizationService.Instance.T("Game Mode غیرفعال است: حالت متعادل برای مصرف عمومی.");

    private bool _startWithWindows;
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (_startWithWindows == value) return;

            if (!TryUpdateStartupRegistry(value, out var error))
            {
                _startWithWindows = IsStartupRegistryEnabledForCurrentExecutable();
                OnPropertyChanged();
                StatusText = string.IsNullOrWhiteSpace(error)
                    ? "تغییر تنظیم اجرای خودکار ویندوز ناموفق بود"
                    : error;
                return;
            }

            _startWithWindows = value;
            OnPropertyChanged();
            _appSettings.StartWithWindows = value;
            _profileService.SaveAppSettings(_appSettings);

            if (value)
                Helpers.DialogService.Info(
                    "اجرای خودکار ویندوز فعال شد. اگر فایل TunnelX را جابه‌جا کردید، این گزینه را یک بار خاموش و روشن کنید.",
                    "TunnelX — استارت‌آپ");
        }
    }

    private bool _autoConnectOnStartup;
    public bool AutoConnectOnStartup
    {
        get => _autoConnectOnStartup;
        set
        {
            if (_autoConnectOnStartup == value) return;
            _autoConnectOnStartup = value;
            OnPropertyChanged();
            _appSettings.AutoConnectOnStartup = value;
            _profileService.SaveAppSettings(_appSettings);
        }
    }

    private bool _enableInformationalNotifications = true;
    public bool EnableInformationalNotifications
    {
        get => _enableInformationalNotifications;
        set
        {
            if (_enableInformationalNotifications == value) return;
            _enableInformationalNotifications = value;
            OnPropertyChanged();
            _appSettings.EnableInformationalNotifications = value;
            _profileService.SaveAppSettings(_appSettings);
        }
    }

    public string InformationalNotificationsSectionTitleText =>
        LocalizationService.Instance.T("🔔 اعلان‌ها");

    public string InformationalNotificationsTitleText =>
        LocalizationService.Instance.T("اعلان‌های وضعیت اتصال و برنامه");

    public string InformationalNotificationsDescriptionText =>
        LocalizationService.Instance.T("نمایش اعلان‌های وضعیت اتصال و برنامه. اعلان‌های تبلیغ/به‌روزرسانی با دکمه ✕ بسته می‌شوند.");

    public string HelpSettingsTabBodyText =>
        LocalizationService.Instance.T("پورت پراکسی محلی، MTU خودکار، DNS Optimization، Game Mode، اعلان‌های وضعیت، اجرای خودکار ویندوز و اتصال خودکار اینجاست.");

    public string? LastActiveProfileId
    {
        get => _appSettings.LastActiveProfileId;
        set
        {
            if (_appSettings.LastActiveProfileId == value) return;
            _appSettings.LastActiveProfileId = value;
            _profileService.SaveAppSettings(_appSettings);
        }
    }

    public string LanguageToggleText => LocalizationService.Instance.ToggleLanguageText;
    public bool AppIsRightToLeft => LocalizationService.Instance.IsRightToLeft;
    public string AppTitleText => LocalizationService.Instance.IsRightToLeft ? "تانلکس" : "TunnelX";
    public string AppTitleAccentText => LocalizationService.Instance.IsRightToLeft ? "س" : "X";
    public System.Windows.FlowDirection AppTitleFlowDirection => LocalizationService.Instance.FlowDirection;
    public System.Windows.HorizontalAlignment AppTitleAccentAlignment => LocalizationService.Instance.IsRightToLeft
        ? System.Windows.HorizontalAlignment.Left
        : System.Windows.HorizontalAlignment.Right;
    public System.Windows.FlowDirection AppFlowDirection => LocalizationService.Instance.FlowDirection;
    public System.Windows.TextAlignment AppTextAlignment => LocalizationService.Instance.TextAlignment;
    public System.Windows.HorizontalAlignment AppStartHorizontalAlignment => LocalizationService.Instance.StartHorizontalAlignment;
    public System.Windows.HorizontalAlignment AppEndHorizontalAlignment => LocalizationService.Instance.EndHorizontalAlignment;

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ConnectButtonText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private ConnectionState _connectionState = ConnectionState.Disconnected;
    public ConnectionState ConnectionState
    {
        get => _connectionState;
        set
        {
            var previous = _connectionState;
            _connectionState = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(ConnectButtonText));
            OnPropertyChanged(nameof(ConnectButtonToolTip));
            OnPropertyChanged(nameof(StatusColor));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(IsOpenVpnConnectionPending));
            OnPropertyChanged(nameof(IsConnectionPending));
            OnPropertyChanged(nameof(CanUseConnectionTabQuickActions));
            OnPropertyChanged(nameof(ConnectingTitleText));
            OnPropertyChanged(nameof(ConnectionTabTitleText));
            OnPropertyChanged(nameof(ConnectingHelpText));
            OnPropertyChanged(nameof(ShowConnectionErrorPanel));
            OnPropertyChanged(nameof(HasConnectionError));
            OnPropertyChanged(nameof(ConnectionErrorDetail));
            RaiseHealthStatusChanged();
            CommandManager.InvalidateRequerySuggested();

            if (value == ConnectionState.Error && previous != ConnectionState.Error)
                QueueConnectionErrorDialog();
        }
    }

    public bool IsConnected => _connectionState == ConnectionState.Connected;
    public bool IsOpenVpnConnectionPending =>
        _connectionState == ConnectionState.Connecting && CurrentTunnelType == TunnelType.OpenVpn;
    public bool IsConnectionPending => _connectionState == ConnectionState.Connecting;
    public const string OpenVpnCommunityDownloadUrl = "https://openvpn.net/community-downloads/";
    public const string WireGuardDownloadUrl = "https://www.wireguard.com/install/";

    private bool _isOpenVpnCommunityInstalled;
    public bool IsOpenVpnCommunityInstalled
    {
        get => _isOpenVpnCommunityInstalled;
        private set
        {
            if (_isOpenVpnCommunityInstalled == value) return;
            _isOpenVpnCommunityInstalled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OpenVpnPrerequisiteText));
            OnPropertyChanged(nameof(OpenVpnPrerequisiteColor));
        }
    }

    private string _openVpnDetectedPath = "";
    public string OpenVpnDetectedPath
    {
        get => _openVpnDetectedPath;
        private set
        {
            if (_openVpnDetectedPath == value) return;
            _openVpnDetectedPath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OpenVpnPrerequisiteText));
        }
    }

    public string OpenVpnPrerequisiteText => IsOpenVpnCommunityInstalled
        ? LocalizationService.Instance.Format("پیش‌نیاز آماده است: نسخه Community اوپن‌وی‌پی‌ان پیدا شد: {0}", OpenVpnDetectedPath)
        : LocalizationService.Instance.T("اخطار: نسخه Community اوپن‌وی‌پی‌ان نصب نیست. برای استفاده از اسپلیت‌تانلینگ با این نوع اتصال، ابتدا آن را از لینک رسمی نصب کنید.");

    public string OpenVpnPrerequisiteColor => IsOpenVpnCommunityInstalled ? "#6CCB5F" : "#E0A020";
    public string OpenVpnIntroText => LocalizationService.Instance.T("TunnelX فایل .ovpn را با OpenVPN Community اجرا می‌کند و مسیر/DNS پیش‌فرض OpenVPN را کنترل می‌کند تا فقط برنامه‌های انتخابی از تونل عبور کنند.");
    public string OpenVpnInstallGuideText => LocalizationService.Instance.T("می‌توانید فایل .ovpn را همین حالا اضافه کنید، اما اتصال OpenVPN فقط بعد از نصب OpenVPN Community انجام می‌شود. اگر نصب نیست، دکمه دانلود را بزنید، نصب را کامل کنید، سپس به TunnelX برگردید و اتصال را بزنید.");
    public string OpenVpnConnectWarningText => LocalizationService.Instance.T("OpenVPN Connect به‌تنهایی کافی نیست؛ اگر Community نصب نباشد، از دکمه دانلود پایین استفاده کنید.");
    public string DownloadOpenVpnText => LocalizationService.Instance.T("دانلود OpenVPN");

    private bool _isWireGuardInstalled;
    public bool IsWireGuardInstalled
    {
        get => _isWireGuardInstalled;
        private set
        {
            if (_isWireGuardInstalled == value) return;
            _isWireGuardInstalled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(WireGuardPrerequisiteText));
            OnPropertyChanged(nameof(WireGuardPrerequisiteColor));
        }
    }

    private string _wireGuardDetectedPath = "";
    public string WireGuardDetectedPath
    {
        get => _wireGuardDetectedPath;
        private set
        {
            if (_wireGuardDetectedPath == value) return;
            _wireGuardDetectedPath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(WireGuardPrerequisiteText));
        }
    }

    public string WireGuardPrerequisiteText => IsWireGuardInstalled
        ? LocalizationService.Instance.Format("پیش‌نیاز آماده است: WireGuard رسمی ویندوز پیدا شد: {0}", WireGuardDetectedPath)
        : LocalizationService.Instance.T("اخطار: WireGuard رسمی ویندوز نصب نیست. برای استفاده از اسپلیت‌تانلینگ WireGuard، ابتدا آن را از لینک رسمی نصب کنید.");

    public string WireGuardPrerequisiteColor => IsWireGuardInstalled ? "#6CCB5F" : "#E0A020";
    public string WireGuardIntroText => LocalizationService.Instance.T("TunnelX کانفیگ WireGuard را با WireGuard رسمی ویندوز به‌صورت adapter واقعی اجرا می‌کند و سپس اسپلیت‌تانلینگ برنامه‌ها را مثل OpenVPN/L2TP مدیریت می‌کند. نسخه فعلی فقط کانفیگ تک-peer را پشتیبانی می‌کند.");
    public string WireGuardInstallGuideText => LocalizationService.Instance.T("می‌توانید فایل .conf را همین حالا اضافه کنید، اما اتصال WireGuard فقط بعد از نصب WireGuard رسمی ویندوز انجام می‌شود. اگر نصب نیست، دکمه دانلود را بزنید، نصب را کامل کنید، سپس به TunnelX برگردید و اتصال را بزنید.");
    public string DownloadWireGuardText => LocalizationService.Instance.T("دانلود WireGuard");
    public string WireGuardFileLabelText => LocalizationService.Instance.T("فایل WireGuard (.conf)");
    public string WireGuardConfigLabelText => LocalizationService.Instance.T("کانفیگ WireGuard");
    public string ChooseFileText => LocalizationService.Instance.T("انتخاب فایل");
    public string RemoveFileText => LocalizationService.Instance.T("حذف فایل");

    /// <summary>App version read from a single app-wide source.</summary>
    public string AppVersion => AppInfo.VersionText;
    public string AppReleaseText => AppInfo.ReleaseText;
    public string AppCreatorText => AppInfo.CreatorText;
    public string AppGitHubUrl => AppInfo.GitHubUrl;
    public string AppLicenseText => AppInfo.LicenseName;
    public string AppLicenseDisplayText => LocalizationService.Instance.Format("لایسنس: {0}", AppInfo.LicenseName);
    public string AdPlaceholderTitleText => LocalizationService.Instance.T("محل تبلیغات شما");
    public string AdRequestButtonText => LocalizationService.Instance.T("درخواست تبلیغ");
    public string AdAudienceText => _githubInstallCount.HasValue
        ? LocalizationService.Instance.Format("تبلیغ شما می‌تواند در معرض دید کاربران TunnelX با بیش از {0} نصب از GitHub باشد.", GitHubInstallCountDisplay)
        : "";
    public string TelegramChannelJoinButtonText => LocalizationService.Instance.T(
        "📢 برای اطلاع‌رسانی، پشتیبانی و گزارش خطا در کانال تلگرام TunnelX عضو شوید");

    public string ConnectionTabTitleText => IsConnectionPending
        ? ConnectingTitleText
        : LocalizationService.Instance.T("اتصال VPN");

    public string SplitTunnelTipLine1Text => LocalizationService.Instance.T(
        "💡 در حالت انتخابی، فقط برنامه‌های فعال در تب «برنامه‌ها» از تونل عبور می‌کنند؛ بقیه مستقیم می‌مانند.");

    public string SplitTunnelTipLine2Text => LocalizationService.Instance.T(
        "📌 برای تلگرام، واتس‌اپ و برنامه‌های Store، Microsoft Edge WebView2 را هم به لیست تونل اضافه کنید.");
    public string TelegramChannelToolTipText => LocalizationService.Instance.Format(
        "کانال رسمی {0} — اخبار آپدیت، گزارش خطا و پشتیبانی. برای ارسال پیام به تیم، ابتدا در کانال عضو شوید.",
        AppInfo.TelegramChannelHandle);
    public string FooterTelegramButtonText => LocalizationService.Instance.T("📢 تلگرام");
    public string FooterTelegramToolTipText => TelegramChannelToolTipText;
    public string FooterMadeByText => LocalizationService.Instance.T("توسط Maxifan");
    public string LogClearButtonText => LocalizationService.Instance.T("پاک کردن");
    public string LogCopyErrorButtonText => LocalizationService.Instance.T("کپی خطا");
    public string LogCopyAllButtonText => LocalizationService.Instance.T("کپی همه");
    public string LogClearToolTipText => LocalizationService.Instance.T("پاک کردن همه لاگ‌ها");
    public string LogCopyErrorToolTipText => LocalizationService.Instance.T("کپی آخرین خطا یا هشدار");
    public string LogCopyAllToolTipText => LocalizationService.Instance.T("کپی کردن همه لاگ‌ها");
    public string DonatePayPalText => LocalizationService.Instance.IsRightToLeft
        ? $"پی‌پل: {AppInfo.PayPalEmail}"
        : $"PayPal: {AppInfo.PayPalEmail}";
    public string CryptoDonationText => LocalizationService.Instance.IsRightToLeft
        ? AppInfo.CryptoDonationText
        : AppInfo.CryptoDonationTextEn;

    public ObservableCollection<CryptoDonationAddress> HelpCryptoWalletRows { get; } = new();

    public string HelpProjectCardTitleText => LocalizationService.Instance.T("پروژه و بروزرسانی");
    public string HelpProjectMissionText => LocalizationService.Instance.T(
        "TunnelX اسپلیت‌تانلینگ برنامه‌ای را برای ویندوز فراهم می‌کند: فقط برنامه‌ها و مقصدهای انتخابی از تونل عبور می‌کنند و بقیه ترافیک مستقیم می‌ماند.");
    public string HelpSupportCardTitleText => LocalizationService.Instance.T("حمایت از پروژه");
    public string HelpSupportCtaText => LocalizationService.Instance.T(
        "با حمایت شما توسعه TunnelX ادامه می‌یابد؛ رفع باگ، پشتیبانی از پروتکل‌های جدید و قابلیت‌های بیشتر در راه است.");
    public string HelpGitHubButtonText => LocalizationService.Instance.T("GitHub پروژه TunnelX");
    public string HelpGitHubButtonToolTipText => LocalizationService.Instance.T("باز کردن صفحه GitHub پروژه TunnelX");
    public string HelpDonatePayPalButtonText => LocalizationService.Instance.T("حمایت با پی‌پل");
    public string HelpCopyCryptoAddressButtonText => LocalizationService.Instance.T("کپی");
    public string HelpCopyCryptoAddressToolTipText => LocalizationService.Instance.T("کپی آدرس کیف پول");

    private bool _isCheckingForUpdates;
    public bool IsCheckingForUpdates
    {
        get => _isCheckingForUpdates;
        set
        {
            if (_isCheckingForUpdates == value) return;
            _isCheckingForUpdates = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UpdateButtonText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private bool _isUpdateAvailable;
    public bool IsUpdateAvailable
    {
        get => _isUpdateAvailable;
        set
        {
            if (_isUpdateAvailable == value) return;
            _isUpdateAvailable = value;
            OnPropertyChanged();
        }
    }

    private string _updateStatusKey = "بررسی نسخه جدید پس از برقراری اتصال به‌صورت خودکار انجام می‌شود.";
    private object[] _updateStatusFormatArgs = Array.Empty<object>();

    public string UpdateStatusText =>
        _updateStatusFormatArgs.Length > 0
            ? LocalizationService.Instance.Format(_updateStatusKey, _updateStatusFormatArgs)
            : LocalizationService.Instance.T(_updateStatusKey);

    private void SetUpdateStatus(string persianKey, params object[] formatArgs)
    {
        _updateStatusKey = persianKey;
        _updateStatusFormatArgs = formatArgs ?? Array.Empty<object>();
        OnPropertyChanged(nameof(UpdateStatusText));
    }

    private string _latestReleaseUrl = AppInfo.LatestReleaseUrl;
    public string LatestReleaseUrl
    {
        get => _latestReleaseUrl;
        set
        {
            if (_latestReleaseUrl == value) return;
            _latestReleaseUrl = value;
            OnPropertyChanged();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private string _latestReleaseTag = "";
    public string LatestReleaseTag
    {
        get => _latestReleaseTag;
        private set
        {
            if (_latestReleaseTag == value) return;
            _latestReleaseTag = value;
            OnPropertyChanged();
        }
    }

    private string _latestReleaseNotes = "";
    public string LatestReleaseNotes
    {
        get => _latestReleaseNotes;
        private set
        {
            if (_latestReleaseNotes == value) return;
            _latestReleaseNotes = value;
            OnPropertyChanged();
        }
    }

    public void ShowLatestReleaseChangelog()
    {
        var loc = LocalizationService.Instance;
        var tag = (LatestReleaseTag ?? "").Trim();
        var title = string.IsNullOrWhiteSpace(tag)
            ? loc.T("تغییرات این نسخه")
            : loc.Format("تغییرات {0}", tag.TrimStart('v', 'V'));

        var notes = LatestReleaseNotes;
        if (string.IsNullOrWhiteSpace(notes) && !string.IsNullOrWhiteSpace(tag))
            notes = ChangelogService.TryGetSectionFromChangelogFile(tag) ?? "";

        Action? onDownload = !string.IsNullOrWhiteSpace(LatestReleaseUrl)
            ? () =>
            {
                if (OpenLatestReleaseCommand.CanExecute(null))
                    OpenLatestReleaseCommand.Execute(null);
            }
            : null;

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (Application.Current.MainWindow is { } main)
            {
                if (main.WindowState == WindowState.Minimized)
                    main.WindowState = WindowState.Normal;
                main.Activate();
            }

            Views.ReleaseNotesWindow.Show(Application.Current.MainWindow, title, notes, onDownload);
        });
    }

    private long? _githubInstallCount;
    private int _githubInstallCountRequestId;

    public bool HasGitHubInstallCount => _githubInstallCount.HasValue;

    public string GitHubInstallCountText => _githubInstallCount.HasValue
        ? LocalizationService.Instance.Format(
            "تعداد نصب این برنامه از گیت هاب: {0}",
            GitHubInstallCountDisplay)
        : "";

    private string GitHubInstallCountDisplay =>
        (_githubInstallCount ?? 0).ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    public string UpdateButtonText => IsCheckingForUpdates
        ? LocalizationService.Instance.T("در حال بررسی...")
        : LocalizationService.Instance.T("بررسی بروزرسانی");

    public string ConnectButtonText => _connectionState switch
    {
        ConnectionState.Disconnected => LocalizationService.Instance.T("🔌  اتصال"),
        ConnectionState.Connecting => LocalizationService.Instance.T("❌  لغو اتصال"),
        ConnectionState.Connected => LocalizationService.Instance.T("🔴  قطع اتصال"),
        ConnectionState.Disconnecting => LocalizationService.Instance.T("⏳  در حال قطع..."),
        ConnectionState.Error => LocalizationService.Instance.T("🔌  اتصال مجدد"),
        _ => LocalizationService.Instance.T("اتصال")
    };

    public string ConnectButtonToolTip => _connectionState switch
    {
        ConnectionState.Connecting => LocalizationService.Instance.T("لغو تلاش اتصال"),
        ConnectionState.Connected => LocalizationService.Instance.T("قطع اتصال فعلی"),
        ConnectionState.Disconnecting => LocalizationService.Instance.T("در حال قطع اتصال..."),
        ConnectionState.Error => LocalizationService.Instance.T("اتصال مجدد"),
        _ => LocalizationService.Instance.T("شروع اتصال با پروفایل انتخاب‌شده")
    };

    public string StatusColor => _connectionState switch
    {
        ConnectionState.Connected => "#4CAF50",
        ConnectionState.Connecting or ConnectionState.Disconnecting => "#E07820",
        ConnectionState.Error => "#E05252",
        _ => "#666666"
    };

    public string ConnectingTitleText => CurrentTunnelType switch
    {
        TunnelType.OpenVpn => LocalizationService.Instance.T("در حال اتصال OpenVPN"),
        TunnelType.WireGuard => LocalizationService.Instance.T("در حال اتصال WireGuard"),
        TunnelType.V2Ray => LocalizationService.Instance.T("در حال اتصال V2Ray/Xray"),
        TunnelType.SocksProxy => LocalizationService.Instance.T("در حال اتصال Proxy"),
        TunnelType.L2tpIpsec => LocalizationService.Instance.T("در حال اتصال L2TP/IPsec"),
        _ => LocalizationService.Instance.T("در حال اتصال")
    };

    public string ConnectingHelpText => CurrentTunnelType switch
    {
        TunnelType.OpenVpn => LocalizationService.Instance.T("تا قبل از بالا آمدن آداپتر، مسیرهای سیستم تغییر داده نمی‌شود. اگر اتصال طولانی شد، فایل .ovpn، نام کاربری/رمز یا نصب OpenVPN Community را بررسی کنید."),
        TunnelType.WireGuard => LocalizationService.Instance.T("TunnelX در حال آماده‌سازی سرویس WireGuard و آداپتر ویندوز است. می‌توانید با دکمه لغو اتصال تلاش فعلی را متوقف کنید."),
        _ => LocalizationService.Instance.T("TunnelX در حال راه‌اندازی اتصال و آماده‌سازی مسیرهای اسپلیت‌تانلینگ است. می‌توانید با دکمه لغو اتصال تلاش فعلی را متوقف کنید.")
    };

    private string _statusText = "آماده اتصال";
    private string? _statusFormatArg;

    public string StatusText
    {
        get => string.IsNullOrWhiteSpace(_statusFormatArg)
            ? LocalizationService.Instance.T(_statusText)
            : LocalizationService.Instance.Format(_statusText, _statusFormatArg);
        set
        {
            var (key, formatArg) = LocalizationService.Instance.ResolveStatusStorage(value);
            _statusText = key;
            _statusFormatArg = formatArg;
            OnPropertyChanged();
        }
    }

    private void NormalizeStatusTextStorage()
    {
        var snapshot = StatusText;
        var (key, formatArg) = LocalizationService.Instance.ResolveStatusStorage(snapshot);
        _statusText = key;
        _statusFormatArg = formatArg;
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
            OnPropertyChanged(nameof(IsOpenVpnConnectionPending));
            OnPropertyChanged(nameof(ConnectedBadgeText));
        OnPropertyChanged(nameof(ConnectedCardToolTipText));
            OnPropertyChanged(nameof(ConnectionIpLabel));
            OnPropertyChanged(nameof(ConnectedServerPingButtonText));
            OnPropertyChanged(nameof(ConnectingTitleText));
            OnPropertyChanged(nameof(ConnectingHelpText));
            if (_selectedProfile != null)
                _selectedProfile.TunnelType = value;
            if (value == TunnelType.OpenVpn)
                RefreshOpenVpnInstallStatus();
            if (value == TunnelType.WireGuard)
                RefreshWireGuardInstallStatus();
            UpdateConfigDiagnostics();
            RaiseProfileCardChanged();
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
            RaiseProfileCardChanged();
            RaiseHealthStatusChanged();
            SaveCurrentState();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private string _selectedOpenVpnConfig = "";
    public string SelectedOpenVpnConfig
    {
        get => _selectedOpenVpnConfig;
        set
        {
            if (_selectedOpenVpnConfig == value) return;
            _selectedOpenVpnConfig = value;
            if (_selectedProfile != null)
                _selectedProfile.OpenVpnConfig = value;
            OnPropertyChanged();
            UpdateConfigDiagnostics();
            RaiseProfileCardChanged();
            RaiseHealthStatusChanged();
            SaveCurrentState();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private string _selectedOpenVpnConfigPath = "";
    public string SelectedOpenVpnConfigPath
    {
        get => _selectedOpenVpnConfigPath;
        set
        {
            if (_selectedOpenVpnConfigPath == value) return;
            _selectedOpenVpnConfigPath = value;
            if (_selectedProfile != null)
                _selectedProfile.OpenVpnConfigPath = value;
            OnPropertyChanged();
            RaiseProfileCardChanged();
            SaveCurrentState();
        }
    }

    private string _selectedWireGuardConfig = "";
    public string SelectedWireGuardConfig
    {
        get => _selectedWireGuardConfig;
        set
        {
            if (_selectedWireGuardConfig == value) return;
            _selectedWireGuardConfig = value;
            if (_selectedProfile != null)
                _selectedProfile.WireGuardConfig = value;
            OnPropertyChanged();
            UpdateConfigDiagnostics();
            RaiseProfileCardChanged();
            RaiseHealthStatusChanged();
            SaveCurrentState();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private string _selectedWireGuardConfigPath = "";
    public string SelectedWireGuardConfigPath
    {
        get => _selectedWireGuardConfigPath;
        set
        {
            if (_selectedWireGuardConfigPath == value) return;
            _selectedWireGuardConfigPath = value;
            if (_selectedProfile != null)
                _selectedProfile.WireGuardConfigPath = value;
            OnPropertyChanged();
            RaiseProfileCardChanged();
            SaveCurrentState();
        }
    }

    private string _openVpnUsername = "";
    public string OpenVpnUsername
    {
        get => _openVpnUsername;
        set
        {
            if (_openVpnUsername == value) return;
            _openVpnUsername = value;
            if (_selectedProfile != null)
                _selectedProfile.OpenVpnUsername = value;
            OnPropertyChanged();
            UpdateConfigDiagnostics();
            NotifyOpenVpnProfileUi();
            SaveCurrentState();
        }
    }

    private string _openVpnPassword = "";
    public string OpenVpnPassword
    {
        get => _openVpnPassword;
        set
        {
            if (_openVpnPassword == value) return;
            _openVpnPassword = value;
            if (_selectedProfile != null)
                _selectedProfile.OpenVpnPassword = value;
            OnPropertyChanged();
            UpdateConfigDiagnostics();
            NotifyOpenVpnProfileUi();
            SaveCurrentState();
        }
    }

    private string _openVpnPrivateKeyPassword = "";
    public string OpenVpnPrivateKeyPassword
    {
        get => _openVpnPrivateKeyPassword;
        set
        {
            if (_openVpnPrivateKeyPassword == value) return;
            _openVpnPrivateKeyPassword = value;
            if (_selectedProfile != null)
                _selectedProfile.OpenVpnPrivateKeyPassword = value;
            OnPropertyChanged();
            UpdateConfigDiagnostics();
            NotifyOpenVpnProfileUi();
            SaveCurrentState();
        }
    }

    public string OpenVpnConfigIntroText =>
        LocalizationService.Instance.T("فایل .ovpn و اطلاعات احراز هویت OpenVPN را وارد کنید. TunnelX بر اساس محتوای فایل مشخص می‌کند کدام فیلدها اجباری است.");

    public string OpenVpnScenarioTitleText =>
        OpenVpnProfileAnalyzer.GetScenarioTitle(SelectedOpenVpnConfig);

    public string OpenVpnScenarioHintText =>
        OpenVpnProfileAnalyzer.GetScenarioHint(SelectedOpenVpnConfig);

    public string OpenVpnConfigFileLabelText =>
        LocalizationService.Instance.T("فایل OpenVPN (.ovpn)");

    public string OpenVpnUsernameFieldLabelText =>
        OpenVpnProfileAnalyzer.GetUsernameFieldLabel(SelectedOpenVpnConfig);

    public string OpenVpnPasswordFieldLabelText =>
        OpenVpnProfileAnalyzer.GetPasswordFieldLabel(SelectedOpenVpnConfig);

    public string OpenVpnSecretFieldLabelText =>
        OpenVpnProfileAnalyzer.GetSecretFieldLabel(SelectedOpenVpnConfig);

    private void NotifyOpenVpnProfileUi()
    {
        OnPropertyChanged(nameof(OpenVpnScenarioTitleText));
        OnPropertyChanged(nameof(OpenVpnScenarioHintText));
        OnPropertyChanged(nameof(OpenVpnUsernameFieldLabelText));
        OnPropertyChanged(nameof(OpenVpnPasswordFieldLabelText));
        OnPropertyChanged(nameof(OpenVpnSecretFieldLabelText));
    }

    private ProxyProtocol _proxyProtocol = ProxyProtocol.Socks5;
    public ProxyProtocol ProxyProtocol
    {
        get => _proxyProtocol;
        set
        {
            if (_proxyProtocol == value) return;
            _proxyProtocol = value;
            if (_selectedProfile != null)
                _selectedProfile.ProxyProtocol = value;
            OnPropertyChanged();
            UpdateConfigDiagnostics();
            RaiseProfileCardChanged();
            SaveCurrentState();
        }
    }

    private string _proxyServerAddress = "";
    public string ProxyServerAddress
    {
        get => _proxyServerAddress;
        set
        {
            if (_proxyServerAddress == value) return;
            _proxyServerAddress = value;
            if (_selectedProfile != null)
                _selectedProfile.ProxyServerAddress = value;
            OnPropertyChanged();
            UpdateConfigDiagnostics();
            RaiseProfileCardChanged();
            SaveCurrentState();
        }
    }

    private int _proxyPort = 1080;
    public int ProxyPort
    {
        get => _proxyPort;
        set
        {
            var normalized = value;
            if (_proxyPort == normalized) return;
            _proxyPort = normalized;
            if (_selectedProfile != null)
                _selectedProfile.ProxyPort = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProxyPortText));
            UpdateConfigDiagnostics();
            RaiseProfileCardChanged();
            SaveCurrentState();
        }
    }

    public string ProxyPortText
    {
        get => _proxyPort.ToString();
        set
        {
            if (int.TryParse((value ?? "").Trim(), out var port))
            {
                ProxyPort = port;
                return;
            }

            ConfigValidationText = string.IsNullOrWhiteSpace(value)
                ? "پورت پراکسی را وارد کنید"
                : "پورت پراکسی باید عدد باشد";
        }
    }

    private string _proxyUsername = "";
    public string ProxyUsername
    {
        get => _proxyUsername;
        set
        {
            if (_proxyUsername == value) return;
            _proxyUsername = value;
            if (_selectedProfile != null)
                _selectedProfile.ProxyUsername = value;
            OnPropertyChanged();
            UpdateConfigDiagnostics();
            RaiseProfileCardChanged();
            SaveCurrentState();
        }
    }

    private string _proxyPassword = "";
    public string ProxyPassword
    {
        get => _proxyPassword;
        set
        {
            if (_proxyPassword == value) return;
            _proxyPassword = value;
            if (_selectedProfile != null)
                _selectedProfile.ProxyPassword = value;
            OnPropertyChanged();
            SaveCurrentState();
        }
    }

    private string _configCoreHint = "";
    public string ConfigCoreHint
    {
        get => LocalizationService.Instance.T(_configCoreHint);
        set { _configCoreHint = value; OnPropertyChanged(); }
    }

    private string _configValidationText = "";
    public string ConfigValidationText
    {
        get => LocalizationService.Instance.T(_configValidationText);
        set { _configValidationText = value; OnPropertyChanged(); }
    }

    private string _saveStatusText = "";
    public string SaveStatusText
    {
        get => LocalizationService.Instance.T(_saveStatusText);
        set { _saveStatusText = value; OnPropertyChanged(); OnPropertyChanged(nameof(ProfileSaveHintText)); }
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

    private const string ConnectionIpFetchingKey = "در حال دریافت...";

    private string _connectionIpText = "-";
    public string ConnectionIpText
    {
        get => IsConnectionIpPlaceholder(_connectionIpText)
            ? LocalizationService.Instance.T(_connectionIpText)
            : _connectionIpText;
        set => SetConnectionIpDisplay(value);
    }

    /// <summary>RTL/LTR for exit-IP card text; real IPs stay LTR.</summary>
    public System.Windows.FlowDirection ConnectionIpTextFlowDirection =>
        IsConnectionIpPlaceholder(_connectionIpText)
            ? LocalizationService.Instance.FlowDirection
            : System.Windows.FlowDirection.LeftToRight;

    private static bool IsConnectionIpPlaceholder(string value) =>
        value is "-" or ConnectionIpFetchingKey;

    private void SetConnectionIpDisplay(string value)
    {
        void Apply()
        {
            if (_connectionIpText == value) return;
            _connectionIpText = value;
            OnPropertyChanged(nameof(ConnectionIpText));

            if (IsConnectionIpPlaceholder(value))
                ClearConnectionIpGeo();

            if (value == ConnectionIpFetchingKey)
                SetConnectionIpCountryFlagLoading(true);
            else if (value == "-")
                SetConnectionIpCountryFlagLoading(false);

            OnPropertyChanged(nameof(ConnectionIpTextFlowDirection));
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(Apply);
        else
            Apply();
    }

    private ImageSource? _connectionIpCountryFlag;
    private string _connectionIpCountryName = "";
    private string _connectionIpCountryCode = "";
    private int _connectionIpCountryFlagRequestId;
    private bool _isConnectionIpCountryFlagLoading;

    public ImageSource? ConnectionIpCountryFlag => _connectionIpCountryFlag;

    public bool ShowConnectionIpCountryFlag => _connectionIpCountryFlag != null;

    public bool IsConnectionIpCountryFlagLoading => _isConnectionIpCountryFlagLoading;

    public bool ShowConnectionIpDefaultIcon =>
        !_isConnectionIpCountryFlagLoading && _connectionIpCountryFlag == null;

    public string ConnectionIpCountryToolTip => _connectionIpCountryName;

    private void SetConnectionIpCountryFlagLoading(bool isLoading)
    {
        void Apply()
        {
            if (_isConnectionIpCountryFlagLoading == isLoading)
                return;

            _isConnectionIpCountryFlagLoading = isLoading;
            OnPropertyChanged(nameof(IsConnectionIpCountryFlagLoading));
            OnPropertyChanged(nameof(ShowConnectionIpDefaultIcon));
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(Apply);
        else
            Apply();
    }

    private void ClearConnectionIpCountryFlagLoadingIfCurrent(int requestId, string countryCode)
    {
        void Apply()
        {
            if (requestId != _connectionIpCountryFlagRequestId)
                return;

            if (!string.Equals(_connectionIpCountryCode, countryCode, StringComparison.OrdinalIgnoreCase))
                return;

            if (!_isConnectionIpCountryFlagLoading)
                return;

            _isConnectionIpCountryFlagLoading = false;
            OnPropertyChanged(nameof(IsConnectionIpCountryFlagLoading));
            OnPropertyChanged(nameof(ShowConnectionIpDefaultIcon));
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(Apply);
        else
            Apply();
    }

    private void SetConnectionIpGeo(IpGeoInfo info, int proxyPort)
    {
        var countryCode = info.CountryCode?.Trim().ToUpperInvariant() ?? "";
        var requestId = Interlocked.Increment(ref _connectionIpCountryFlagRequestId);
        SetConnectionIpCountryFlagLoading(true);

        void Apply()
        {
            _connectionIpCountryFlag = null;
            _connectionIpCountryName = info.CountryName;
            _connectionIpCountryCode = countryCode;
            OnPropertyChanged(nameof(ConnectionIpCountryFlag));
            OnPropertyChanged(nameof(ShowConnectionIpCountryFlag));
            OnPropertyChanged(nameof(ShowConnectionIpDefaultIcon));
            OnPropertyChanged(nameof(ConnectionIpCountryToolTip));
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(Apply);
        else
            Apply();

        if (!string.IsNullOrEmpty(countryCode) && proxyPort > 0)
            _ = RefreshConnectionIpCountryFlagAsync(proxyPort, countryCode, requestId);
        else
            SetConnectionIpCountryFlagLoading(false);
    }

    private void ClearConnectionIpGeo()
    {
        Interlocked.Increment(ref _connectionIpCountryFlagRequestId);

        void Apply()
        {
            if (_connectionIpCountryFlag == null &&
                string.IsNullOrEmpty(_connectionIpCountryName) &&
                string.IsNullOrEmpty(_connectionIpCountryCode))
                return;

            _connectionIpCountryFlag = null;
            _connectionIpCountryName = "";
            _connectionIpCountryCode = "";
            OnPropertyChanged(nameof(ConnectionIpCountryFlag));
            OnPropertyChanged(nameof(ShowConnectionIpCountryFlag));
            OnPropertyChanged(nameof(ShowConnectionIpDefaultIcon));
            OnPropertyChanged(nameof(ConnectionIpCountryToolTip));
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(Apply);
        else
            Apply();
    }

    private async Task RefreshConnectionIpCountryFlagAsync(int proxyPort, string countryCode, int requestId)
    {
        var probePorts = BuildExitIpFlagProbePorts(proxyPort);
        Exception? lastError = null;

        for (var attempt = 1; attempt <= 4 && _connectionState == ConnectionState.Connected; attempt++)
        {
            if (requestId != _connectionIpCountryFlagRequestId)
                return;

            if (!string.Equals(_connectionIpCountryCode, countryCode, StringComparison.OrdinalIgnoreCase))
                return;

            foreach (var port in probePorts)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var pngBytes = await IpGeoLookupService.DownloadFlagPngViaTunnelAsync(port, countryCode, cts.Token);
                    if (pngBytes == null || pngBytes.Length == 0)
                        continue;

                    BitmapImage image;
                    using (var ms = new MemoryStream(pngBytes))
                    {
                        image = new BitmapImage();
                        image.BeginInit();
                        image.CacheOption = BitmapCacheOption.OnLoad;
                        image.StreamSource = ms;
                        image.EndInit();
                        image.Freeze();
                    }

                    void Apply()
                    {
                        if (requestId != _connectionIpCountryFlagRequestId)
                            return;

                        if (!string.Equals(_connectionIpCountryCode, countryCode, StringComparison.OrdinalIgnoreCase))
                            return;

                        _connectionIpCountryFlag = image;
                        OnPropertyChanged(nameof(ConnectionIpCountryFlag));
                        OnPropertyChanged(nameof(ShowConnectionIpCountryFlag));
                        OnPropertyChanged(nameof(ShowConnectionIpDefaultIcon));
                        _isConnectionIpCountryFlagLoading = false;
                        OnPropertyChanged(nameof(IsConnectionIpCountryFlagLoading));
                    }

                    var dispatcher = Application.Current?.Dispatcher;
                    if (dispatcher != null && !dispatcher.CheckAccess())
                        _ = dispatcher.BeginInvoke(Apply);
                    else
                        Apply();

                    Logger.Info($"[EXIT-IP] Flag image for '{countryCode}' via 127.0.0.1:{port} (attempt {attempt})");
                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    Logger.Warning($"[EXIT-IP] Flag image via 127.0.0.1:{port} attempt {attempt}: {ex.Message}");
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(900 * attempt));
        }

        Logger.Warning($"[EXIT-IP] Flag image lookup via tunnel failed for '{countryCode}' after retries" +
                       (lastError != null ? $": {lastError.Message}" : ""));
        ClearConnectionIpCountryFlagLoadingIfCurrent(requestId, countryCode);
    }

    /// <summary>Proxy ports for flag/geo HTTP — prefers the port that resolved exit IP, then engine fallbacks.</summary>
    private List<int> BuildExitIpFlagProbePorts(int primaryPort)
    {
        var ports = GetExitIpProbePorts().ToList();
        if (primaryPort > 0)
        {
            ports.Remove(primaryPort);
            ports.Insert(0, primaryPort);
        }

        return ports;
    }

    public string ConnectionIpLabel => LocalizationService.Instance.T("IP خروجی");

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
        ? LocalizationService.Instance.T("Full Route فعال است؛ کل سیستم از تونل عبور می‌کند")
        : LocalizationService.Instance.T("Split فعال است؛ فقط برنامه‌ها و مقصدهای انتخابی از تونل عبور می‌کنند");

    public string RouteModeTitle => IsFullRouteEnabled
        ? LocalizationService.Instance.T("حالت کل سیستم")
        : LocalizationService.Instance.T("حالت انتخابی");
    public string RouteModeDescription => IsFullRouteEnabled
        ? LocalizationService.Instance.T("ترافیک کل سیستم از تونل عبور خواهد کرد؛ برای وقتی مناسب است که همه برنامه‌ها باید پشت تونل باشند.")
        : LocalizationService.Instance.T("فقط برنامه‌ها و مقصدهای انتخابی از تونل عبور می‌کنند؛ بقیه ترافیک مستقیم می‌ماند.");

    public string HeaderCoreText => $"Core: {ActiveCoreName}";
    public string HeaderRouteText => IsFullRouteEnabled ? "Mode: Full" : "Mode: Split";
    public string HeaderLeakText => IsConnected
        ? (_trafficRouter.LeakCount == 0
            ? (_trafficRouter.LeakBlockedCount == 0
                ? "Leak: OK"
                : $"Leak: Protected {_trafficRouter.LeakBlockedCount}")
            : $"Leak: {_trafficRouter.LeakCount}")
        : "Leak: -";
    public string HeaderLeakColor => !IsConnected
        ? "#6CCB5F"
        : _trafficRouter.LeakCount > 0
            ? "#E05252"
            : "#6CCB5F";

    public string HealthLeakText => IsConnected
        ? (_trafficRouter.LeakCount == 0
            ? (_trafficRouter.LeakBlockedCount == 0
                ? "0 leak"
                : $"0 leak / {_trafficRouter.LeakBlockedCount} protected")
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

    public string ConnectedBadgeText => CurrentTunnelType == TunnelType.SocksProxy
        ? LocalizationService.Instance.T("متصل به پراکسی")
        : LocalizationService.Instance.T("متصل به VPN");

    public string ConnectedDisconnectButtonText => LocalizationService.Instance.T("قطع اتصال");

    public string ConnectedCardToolTipText => LocalizationService.Instance.Format(
        "وضعیت اتصال: {0} — {1}",
        ConnectedBadgeText,
        ConnectedProfileName);

    public string ConnectedProfileName => string.IsNullOrWhiteSpace(SelectedProfileName)
        ? LocalizationService.Instance.T("پروفایل فعال")
        : SelectedProfileName;

    public string SelectedProfileSummaryText => LocalizationService.Instance.Format(
        "پروفایل فعال: {0}",
        ConnectedProfileName);

    private string ActiveCoreName => CurrentTunnelType switch
    {
        TunnelType.L2tpIpsec => "L2TP",
        TunnelType.V2Ray when TunnelProviderFactory.RequiresXray(SelectedV2RayConfig) => "Xray",
        TunnelType.V2Ray => "sing-box",
        TunnelType.OpenVpn => "OpenVPN",
        TunnelType.SocksProxy => ProxyProtocol == ProxyProtocol.Http ? "HTTP Proxy" : "SOCKS5",
        TunnelType.WireGuard => "WireGuard / Windows Adapter",
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

    private string _pingTarget = "www.google.com";
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

    public string PingButtonText => _isPinging
        ? LocalizationService.Instance.T("توقف")
        : LocalizationService.Instance.T("پینگ");

    private bool _isTestingConnectedServerPing;
    public bool IsTestingConnectedServerPing
    {
        get => _isTestingConnectedServerPing;
        set
        {
            _isTestingConnectedServerPing = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ConnectedServerPingButtonText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string ConnectedServerPingButtonText => IsTestingConnectedServerPing
        ? LocalizationService.Instance.T("در حال پینگ...")
        : LocalizationService.Instance.T("پینگ سرور");

    private string _pingResultKey = "";
    private object?[] _pingResultArgs = Array.Empty<object?>();
    private bool _pingShowDoneSuffix;

    public string PingResult => BuildLocalizedPingText(_pingResultKey, _pingResultArgs, _pingShowDoneSuffix);

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

    public string ServerPingButtonText => _isTestingServerPing
        ? LocalizationService.Instance.T("در حال تست...")
        : LocalizationService.Instance.T("تست سرور");

    private string _serverPingResultKey = "";
    private object?[] _serverPingResultArgs = Array.Empty<object?>();

    public string ServerPingResult => BuildLocalizedPingText(_serverPingResultKey, _serverPingResultArgs, false);

    public string ServerPingSectionTitleText => LocalizationService.Instance.T("🏓 سرور");
    public string ServerPingToolTipText => LocalizationService.Instance.T("قبل از اتصال، دسترسی و latency سرور را تست می‌کند");
    public string RouteTestSectionTitleText => LocalizationService.Instance.T("تست مسیر");
    public string FullRouteCardTitleText => LocalizationService.Instance.T("عبور کل سیستم");
    public string ManualProxyCardTitleText => LocalizationService.Instance.T("پروکسی دستی");
    public string ConnectionDurationLabelText => LocalizationService.Instance.T("مدت");
    public string TunnelTrafficLabelText => LocalizationService.Instance.T("تونل");
    public string DirectTrafficLabelText => LocalizationService.Instance.T("خارج تونل");
    public string RouteTestHelpText => LocalizationService.Instance.T("یک دامنه یا IP را از داخل تونل تست کنید.");
    public string ManualProxyCardToolTipText => LocalizationService.Instance.T("این آدرس داخلی را در برنامه‌هایی وارد کنید که تنظیم Proxy جداگانه دارند یا خودکار وارد تونل نمی‌شوند. پورت این پراکسی از تب تنظیمات قابل تغییر است.");
    public string PingTargetToolTipText => LocalizationService.Instance.T("IP یا دامنه مقصد برای تست از داخل تونل");
    public string PingTargetButtonToolTipText => LocalizationService.Instance.T("تست همین مقصد از داخل مسیر تونل");
    public string ConnectedServerPingToolTipText => LocalizationService.Instance.T("دسترسی به سرور همین اتصال را تست می‌کند");

    private static string BuildLocalizedPingText(string key, object?[] args, bool showDoneSuffix)
    {
        if (string.IsNullOrEmpty(key))
            return "";

        var loc = LocalizationService.Instance;
        var rtlArgs = RtlPingFormatArgs(args);
        var text = rtlArgs.Length > 0 ? loc.Format(key, rtlArgs) : loc.T(key);
        return showDoneSuffix ? text + loc.T("  [پایان]") : text;
    }

    private static object?[] RtlPingFormatArgs(object?[] args)
    {
        if (args.Length == 0 || LocalizationService.Instance.FlowDirection != System.Windows.FlowDirection.RightToLeft)
            return args;

        return args.Select(static arg =>
        {
            if (arg is not string s || string.IsNullOrWhiteSpace(s) || TextHelper.IsPersianText(s))
                return arg;
            return TextHelper.EmbedLtr(s);
        }).ToArray();
    }

    private void SetPingResult(string key, params object?[] args)
    {
        void Apply()
        {
            _pingResultKey = key;
            _pingResultArgs = args ?? Array.Empty<object?>();
            _pingShowDoneSuffix = false;
            OnPropertyChanged(nameof(PingResult));
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(Apply);
        else
            Apply();
    }

    private void MarkPingResultDone()
    {
        if (string.IsNullOrEmpty(_pingResultKey))
            return;

        _pingShowDoneSuffix = true;
        OnPropertyChanged(nameof(PingResult));
    }

    private void SetServerPingResult(string key, params object?[] args)
    {
        _serverPingResultKey = key;
        _serverPingResultArgs = args ?? Array.Empty<object?>();
        OnPropertyChanged(nameof(ServerPingResult));
    }

    public int EnabledAppsCount => TunnelApps.Count(a => a.IsEnabled);

    public string MixedProxyInfo => $"127.0.0.1:{_trafficRouter.Socks5Port}";

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

    public string RoutingRulesTabToolTipText =>
        LocalizationService.Instance.T("قوانین Include و Exclude مسیرها");

    public string RoutingRulesTitleText =>
        LocalizationService.Instance.T("قوانین مسیر");

    public string RoutingRulesSubtitleText =>
        LocalizationService.Instance.T("مقصدهای مستقیم و مقصدهای اجباری تونل را اینجا مدیریت کنید.");

    public string ExcludeStayDirectTitleText =>
        LocalizationService.Instance.T("🚫 مستقیم بماند");

    public string ExcludeStayDirectSummaryText =>
        LocalizationService.Instance.T("این مقصدها از تونل عبور نمی‌کنند.");

    public string ExcludeRulesGuideTitleText =>
        LocalizationService.Instance.T("راهنمای ورود آدرس");

    public string ExcludeRulesGuideBodyText =>
        LocalizationService.Instance.T("راهنمای Exclude — نحوه ورود:\n• آی‌پی: فقط IPv4 مثل 8.8.8.8 (IPv6 فعلاً پشتیبانی نمی‌شود).\n• دامنه: google.com کافی است؛ می‌توانید https://google.com/maps یا google.com:443 هم بزنید — فقط نام host استخراج می‌شود.\n• زیردامنه: با example.com خود دامنه و همه زیردامنه‌ها (مثل www.example.com و api.example.com) هم اعمال می‌شود؛ notexample.com یا example.com.evil شامل نمی‌شود.\n• Exclude یعنی ترافیک مستقیم است و از تونل عبور نمی‌کند.\n• پس از اتصال، IP دامنه resolve می‌شود و با تغییر DNS به‌روز می‌شود.\n\nمثال: 192.168.1.1 ، google.com ، cdn.example.com");

    public string ExcludeInputPlaceholderText =>
        LocalizationService.Instance.T("دامنه یا آی‌پی (مثلاً google.com یا 1.2.3.4)");

    public string ExcludeListHeaderText =>
        LocalizationService.Instance.T("آدرس‌های مستقیم");

    public string IncludeForceTunnelTitleText =>
        LocalizationService.Instance.T("✅ از تونل عبور کند");

    public string IncludeForceTunnelSummaryText =>
        LocalizationService.Instance.T("این مقصدها همیشه از تونل عبور می‌کنند.");

    public string IncludeRulesGuideTitleText =>
        LocalizationService.Instance.T("راهنمای ورود آدرس");

    public string IncludeRulesGuideBodyText =>
        LocalizationService.Instance.T("راهنمای Include — نحوه ورود:\n• آی‌پی: فقط IPv4 مثل 203.0.113.10 (IPv6 فعلاً پشتیبانی نمی‌شود).\n• دامنه: twitter.com کافی است؛ لینک کامل یا پورت در انتها هم قابل قبول است — فقط host خوانده می‌شود.\n• زیردامنه: با example.com همه زیردامنه‌ها (cdn.example.com و …) هم از تونل عبور می‌کنند.\n• Include یعنی این مقصد همیشه از تونل می‌رود، حتی اگر برنامه‌ای در لیست تونل انتخاب نشده باشد.\n• IPهای خصوصی/محلی (مثل 192.168.x) معمولاً مسیر تونل نمی‌گیرند.\n\nمثال: twitter.com ، api.service.com ، 203.0.113.10");

    public string IncludeInputPlaceholderText =>
        LocalizationService.Instance.T("دامنه یا آی‌پی (مثلاً example.com یا 1.2.3.4)");

    public string IncludeListHeaderText =>
        LocalizationService.Instance.T("آدرس‌های اجباری");

    public string AddRoutingRuleButtonText =>
        LocalizationService.Instance.T("افزودن");

    public string RemoveRoutingRuleButtonText =>
        LocalizationService.Instance.T("حذف");

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
    public ICommand CancelConnectionCommand { get; }
    public ICommand AddAppCommand { get; }
    public ICommand RemoveAppCommand { get; }
    public ICommand ToggleAppCommand { get; }
    public ICommand RefreshAppsCommand { get; }
    public ICommand NewProfileCommand { get; }
    public ICommand DeleteProfileCommand { get; }
    public ICommand DuplicateProfileCommand { get; }
    public ICommand EditProfileCommand { get; }
    public ICommand SelectProfileCommand { get; }
    public ICommand ImportConfigsFromClipboardCommand { get; }
    public ICommand TestSelectedProfileLatencyCommand { get; }
    public ICommand TestProfileLatencyCommand { get; }
    public ICommand TestProfileServerPingCommand { get; }
    public ICommand TestAllProfilesLatencyCommand { get; }
    public ICommand CancelProfileLatencyTestCommand { get; }
    public ICommand ClearHistoryCommand { get; }
    public ICommand AddExcludeCommand { get; }
    public ICommand RemoveExcludeCommand { get; }
    public ICommand AddIncludeCommand { get; }
    public ICommand RemoveIncludeCommand { get; }
    public ICommand TogglePingCommand { get; }
    public ICommand TestConnectedServerPingCommand { get; }
    public ICommand TestServerPingCommand { get; }
    public ICommand PasteConfigCommand { get; }
    public ICommand ClearConfigCommand { get; }
    public ICommand BrowseOpenVpnConfigCommand { get; }
    public ICommand BrowseWireGuardConfigCommand { get; }
    public ICommand OpenOpenVpnCommunityDownloadCommand { get; }
    public ICommand OpenWireGuardDownloadCommand { get; }
    public ICommand OpenGitHubCommand { get; }
    public ICommand OpenDonateCommand { get; }
    public ICommand OpenAdRequestCommand { get; }
    public ICommand OpenTelegramChannelCommand { get; }
    public ICommand CopyDonationInfoCommand { get; }
    public ICommand CopyHelpCryptoAddressCommand { get; }
    public ICommand CheckForUpdatesCommand { get; }
    public ICommand OpenLatestReleaseCommand { get; }
    public ICommand ToggleLanguageCommand { get; }

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

    private static void OpenTelegramChannel()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = AppInfo.TelegramChannelDeepLink,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Logger.Warning($"[UI] Telegram deep link failed, falling back to web URL: {ex.Message}");
            OpenExternalLink(AppInfo.TelegramChannelUrl);
        }
    }

    private void CopyDonationInfoToClipboard()
    {
        try
        {
            var text =
                $"{AppInfo.AppName} - {LocalizationService.Instance.T("حمایت از پروژه")}\n" +
                $"PayPal: {AppInfo.PayPalEmail}\n" +
                $"PayPal link: {AppInfo.PayPalDonateUrl}\n\n" +
                CryptoDonationText;
            System.Windows.Clipboard.SetText(text);
            Logger.Info("[UI] Donation info copied to clipboard");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[UI] Copy donation info failed: {ex.Message}");
        }
    }

    private void RefreshHelpCryptoWalletRows()
    {
        HelpCryptoWalletRows.Clear();
        foreach (var row in CryptoDonationAddressList.GetAll(LocalizationService.Instance))
            HelpCryptoWalletRows.Add(row);
    }

    private static void CopyHelpCryptoAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return;
        try
        {
            System.Windows.Clipboard.SetText(address.Trim());
            Logger.Info("[UI] Crypto wallet address copied from Help tab");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[UI] Copy wallet address failed: {ex.Message}");
        }
    }

    private int _updateCheckRequestId;

    private void CancelPendingUpdateCheck() => ++_updateCheckRequestId;

    private int ResolveTunnelProxyPort() =>
        _vpnService.Status.SingBoxMixedPort > 0
            ? _vpnService.Status.SingBoxMixedPort
            : _trafficRouter.Socks5Port;

    private async Task CheckForUpdatesAsync(bool silent, int proxyPort = 0)
    {
        if (IsCheckingForUpdates) return;

        if (!IsConnected)
        {
            if (!silent)
                SetUpdateStatus("برای بررسی نسخه جدید ابتدا اتصال برقرار کنید.");
            return;
        }

        var requestId = ++_updateCheckRequestId;
        var attempts = silent ? 3 : 1;

        try
        {
            IsCheckingForUpdates = true;
            if (!silent)
                SetUpdateStatus("در حال بررسی آخرین نسخه در GitHub از طریق تونل...");

            GitHubReleaseInfo? latest = null;
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                if (requestId != _updateCheckRequestId || !IsConnected)
                    return;

                var port = proxyPort > 0 ? proxyPort : ResolveTunnelProxyPort();
                if (port <= 0)
                {
                    if (!silent)
                        SetUpdateStatus("برای بررسی نسخه جدید ابتدا اتصال برقرار کنید.");
                    return;
                }

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                latest = await GitHubReleaseChecker.GetLatestReleaseAsync(cts.Token, port);
                if (latest != null)
                    break;

                if (attempt < attempts)
                {
                    Logger.Info($"[UPDATE] Tunnel GitHub check attempt {attempt}/{attempts} failed; retrying...");
                    await Task.Delay(TimeSpan.FromSeconds(4));
                }
            }

            if (requestId != _updateCheckRequestId || !IsConnected)
                return;

            if (latest == null)
            {
                if (!silent)
                    SetUpdateStatus("بررسی نسخه جدید از طریق تونل ناموفق بود. اتصال یا دسترسی به GitHub را بررسی کنید.");
                Logger.Warning("[UPDATE] Latest release check via tunnel failed after retries");
                return;
            }

            LatestReleaseUrl = latest.Url;
            LatestReleaseTag = latest.TagName;
            LatestReleaseNotes = ChangelogService.ResolveDisplayNotes(latest.ReleaseNotes, latest.TagName);
            var currentVersion = System.Reflection.Assembly.GetExecutingAssembly()
                .GetName().Version ?? new Version(0, 0, 0);
            var current = new Version(currentVersion.Major, currentVersion.Minor, currentVersion.Build);

            if (latest.Version > current)
            {
                IsUpdateAvailable = true;
                SetUpdateStatus("نسخه جدید آماده است: {0} - برای دانلود از GitHub باز کنید.", latest.TagName);
                Logger.Info($"[UPDATE] New version available: current={current} latest={latest.TagName}");
                return;
            }

            IsUpdateAvailable = false;
            SetUpdateStatus("TunnelX به‌روز است. نسخه فعلی: {0}", AppInfo.VersionText);
            Logger.Info($"[UPDATE] App is up to date: current={current} latest={latest.TagName}");
        }
        catch (OperationCanceledException)
        {
            if (requestId != _updateCheckRequestId || !IsConnected)
                return;

            if (!silent)
                SetUpdateStatus("بررسی بروزرسانی به زمان مجاز نرسید.");
            Logger.Warning("[UPDATE] Latest release check timed out");
        }
        catch (Exception ex)
        {
            if (requestId != _updateCheckRequestId || !IsConnected)
                return;

            if (!silent)
                SetUpdateStatus("بررسی بروزرسانی ناموفق بود: {0}", ex.Message);
            Logger.Warning($"[UPDATE] Latest release check failed: {ex.Message}");
        }
        finally
        {
            if (requestId == _updateCheckRequestId)
                IsCheckingForUpdates = false;
        }
    }

    private async Task RefreshGitHubInstallCountAsync(int proxyPort)
    {
        var requestId = ++_githubInstallCountRequestId;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var count = await GitHubReleaseChecker.GetAppDownloadCountAsync(cts.Token, proxyPort);
            if (requestId != _githubInstallCountRequestId || !IsConnected)
                return;

            if (count.HasValue)
            {
                SetGitHubInstallCount(count.Value, persist: true);
                Logger.Info($"[GITHUB-STATS] App downloads={count.Value}");
            }
            else
            {
                Logger.Warning("[GITHUB-STATS] App download count unavailable");
            }
        }
        catch (OperationCanceledException)
        {
            Logger.Warning("[GITHUB-STATS] App download count timed out");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[GITHUB-STATS] App download count failed: {ex.Message}");
        }
    }

    private void SetGitHubInstallCount(long? count, bool persist = false)
    {
        if (_githubInstallCount == count)
        {
            if (persist && _appSettings.GitHubAppDownloadCount != count)
            {
                _appSettings.GitHubAppDownloadCount = count;
                _profileService.SaveAppSettings(_appSettings);
            }
            return;
        }

        _githubInstallCount = count;
        if (persist)
        {
            _appSettings.GitHubAppDownloadCount = count;
            _profileService.SaveAppSettings(_appSettings);
        }

        OnPropertyChanged(nameof(HasGitHubInstallCount));
        OnPropertyChanged(nameof(GitHubInstallCountText));
        OnPropertyChanged(nameof(AdAudienceText));
    }

    private void PasteConfigFromClipboard()
    {
        try
        {
            if (System.Windows.Clipboard.ContainsText())
            {
                var text = System.Windows.Clipboard.GetText().Trim();
                if (CurrentTunnelType == TunnelType.OpenVpn)
                    SelectedOpenVpnConfig = text;
                else if (CurrentTunnelType == TunnelType.WireGuard)
                {
                    SelectedWireGuardConfig = text;
                    WarnIfWireGuardMissingAfterConfigAdded();
                }
                else
                    SelectedV2RayConfig = text;
            }
        }
        catch (Exception ex)
        {
            ConfigValidationText = LocalizationService.Instance.Format("خواندن کلیپ‌بورد ناموفق بود: {0}", ex.Message);
        }
    }

    private void ClearCurrentConfig()
    {
        if (CurrentTunnelType == TunnelType.OpenVpn)
        {
            SelectedOpenVpnConfig = "";
            SelectedOpenVpnConfigPath = "";
        }
        else if (CurrentTunnelType == TunnelType.WireGuard)
        {
            SelectedWireGuardConfig = "";
            SelectedWireGuardConfigPath = "";
        }
        else
            SelectedV2RayConfig = "";
    }

    private void BrowseForOpenVpnConfig()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = LocalizationService.Instance.T("انتخاب فایل OpenVPN"),
            Filter = LocalizationService.Instance.T("OpenVPN config (*.ovpn)|*.ovpn|All files (*.*)|*.*"),
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            SelectedOpenVpnConfigPath = dialog.FileName;
            SelectedOpenVpnConfig = File.ReadAllText(dialog.FileName);
            WarnIfOpenVpnMissingAfterConfigAdded();
        }
        catch (Exception ex)
        {
            ConfigValidationText = LocalizationService.Instance.Format("خواندن فایل OpenVPN ناموفق بود: {0}", ex.Message);
        }
    }

    private void WarnIfOpenVpnMissingAfterConfigAdded()
    {
        RefreshOpenVpnInstallStatus();
        if (IsOpenVpnCommunityInstalled)
            return;

        ConfigValidationText = LocalizationService.Instance.T("کانفیگ OpenVPN اضافه شد، اما OpenVPN Community نصب نیست. از دکمه دانلود OpenVPN در همین تب استفاده کنید و بعد از نصب دوباره اتصال را بزنید.");
        ShowOpenVpnInstallGuideDialog();
    }

    private void ShowOpenVpnInstallGuideDialog()
    {
        var openDownload = Helpers.DialogService.Action(
            LocalizationService.Instance.T(
                "TunnelX خودش openvpn.exe را اجرا می‌کند؛ لازم نیست برنامه OpenVPN Connect باز باشد.\n\nبرای Split Tunneling باید OpenVPN Community (openvpn.exe) نصب باشد، نه فقط OpenVPN Connect.\n\nدکمه دانلود را بزنید، نصب Community را کامل کنید، سپس دوباره اتصال را بزنید."),
            LocalizationService.Instance.T("راهنمای نصب OpenVPN"),
            LocalizationService.Instance.T("دانلود OpenVPN"),
            LocalizationService.Instance.T("بعداً نصب می‌کنم"));
        if (openDownload)
            OpenExternalLink(OpenVpnCommunityDownloadUrl);
    }

    private void BrowseForWireGuardConfig()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = LocalizationService.Instance.T("انتخاب فایل WireGuard"),
            Filter = LocalizationService.Instance.T("WireGuard config (*.conf)|*.conf|All files (*.*)|*.*"),
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            SelectedWireGuardConfigPath = dialog.FileName;
            SelectedWireGuardConfig = File.ReadAllText(dialog.FileName);
            WarnIfWireGuardMissingAfterConfigAdded();
        }
        catch (Exception ex)
        {
            ConfigValidationText = LocalizationService.Instance.Format("خواندن فایل WireGuard ناموفق بود: {0}", ex.Message);
        }
    }

    private void WarnIfWireGuardMissingAfterConfigAdded()
    {
        RefreshWireGuardInstallStatus();
        if (IsWireGuardInstalled)
            return;

        ConfigValidationText = LocalizationService.Instance.T("کانفیگ WireGuard اضافه شد، اما WireGuard رسمی ویندوز نصب نیست. از دکمه دانلود WireGuard در همین تب استفاده کنید و بعد از نصب دوباره اتصال را بزنید.");
        ShowWireGuardInstallGuideDialog();
    }

    private void ShowWireGuardInstallGuideDialog()
    {
        var openDownload = Helpers.DialogService.Action(
            "کانفیگ WireGuard اضافه شد، اما برای اتصال باید WireGuard رسمی ویندوز را نصب کنید.\nدکمه دانلود را بزنید، نصب را کامل کنید، سپس به TunnelX برگردید و اتصال را بزنید.",
            "راهنمای نصب WireGuard",
            "دانلود WireGuard",
            "بعداً نصب می‌کنم");
        if (openDownload)
            OpenExternalLink(WireGuardDownloadUrl);
    }

    private void RefreshOpenVpnInstallStatus()
    {
        var path = OpenVpnTunnelProvider.FindOpenVpnExecutable();
        IsOpenVpnCommunityInstalled = !string.IsNullOrWhiteSpace(path);
        OpenVpnDetectedPath = path ?? "";
        UpdateConfigDiagnostics();
    }

    private void RefreshWireGuardInstallStatus()
    {
        var path = WireGuardTunnelProvider.FindWireGuardExecutable();
        IsWireGuardInstalled = !string.IsNullOrWhiteSpace(path);
        WireGuardDetectedPath = path ?? "";
        UpdateConfigDiagnostics();
    }

    private void UpdateConfigDiagnostics()
    {
        if (CurrentTunnelType == TunnelType.L2tpIpsec)
        {
            ConfigCoreHint = "L2TP/IPsec";
            ConfigValidationText = string.IsNullOrWhiteSpace(ServerAddress)
                ? "آدرس سرور L2TP را وارد کنید"
                : "آماده تست و اتصال";
            return;
        }

        if (CurrentTunnelType == TunnelType.OpenVpn)
        {
            NotifyOpenVpnProfileUi();
            ConfigCoreHint = "OpenVPN";
            if (!IsOpenVpnCommunityInstalled)
            {
                ConfigValidationText = LocalizationService.Instance.T("OpenVPN Community نصب نیست؛ ابتدا آن را از لینک رسمی نصب کنید");
                return;
            }

            if (string.IsNullOrWhiteSpace(SelectedOpenVpnConfig))
            {
                ConfigValidationText = LocalizationService.Instance.T("فایل .ovpn را انتخاب کنید؛ TunnelX آن را در حالت split-compatible اجرا می‌کند");
                return;
            }

            if (OpenVpnProfileAnalyzer.TryGetProfileValidationError(
                    SelectedOpenVpnConfig,
                    OpenVpnUsername,
                    OpenVpnPassword,
                    OpenVpnPrivateKeyPassword,
                    out var openVpnValidationError))
            {
                ConfigValidationText = openVpnValidationError;
                return;
            }

            ConfigValidationText = OpenVpnProfileAnalyzer.IsProfileReady(
                SelectedOpenVpnConfig,
                OpenVpnUsername,
                OpenVpnPassword,
                OpenVpnPrivateKeyPassword)
                ? LocalizationService.Instance.T("کانفیگ OpenVPN آماده است")
                : OpenVpnProfileAnalyzer.GetScenarioHint(SelectedOpenVpnConfig);
            return;
        }

        if (CurrentTunnelType == TunnelType.SocksProxy)
        {
            ConfigCoreHint = ProxyProtocol == ProxyProtocol.Http ? "HTTP Proxy" : "SOCKS5 Proxy";
            ConfigValidationText = ValidateProxySettings(out var proxyMessage)
                ? BuildProxyValidationText()
                : proxyMessage;
            return;
        }

        if (CurrentTunnelType == TunnelType.WireGuard)
        {
            ConfigCoreHint = "WireGuard / Windows Adapter";
            ConfigValidationText = !IsWireGuardInstalled
                ? LocalizationService.Instance.T("WireGuard رسمی ویندوز نصب نیست؛ ابتدا آن را از لینک رسمی نصب کنید")
                : WireGuardConfigParser.TryParse(SelectedWireGuardConfig, out var profile, out var wireGuardError)
                ? LocalizationService.Instance.Format("Endpoint WireGuard: {0}:{1}", profile.EndpointHost, profile.EndpointPort)
                : wireGuardError;
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
            ? LocalizationService.Instance.Format("سرور: {0}:{1}", server, port)
            : error;
    }

    private string BuildProxyValidationText()
    {
        var endpoint = $"{ProxyServerAddress.Trim()}:{ProxyPort}";
        return IsLoopbackProxyServer()
            ? LocalizationService.Instance.Format("پراکسی آماده است: {0} — توجه: این پراکسی محلی است؛ برنامه‌هایی که خودشان مستقیم از همین پراکسی استفاده کنند خارج از لیست برنامه‌های TunnelX هم پروکسی می‌شوند.", endpoint)
            : LocalizationService.Instance.Format("پراکسی آماده است: {0}", endpoint);
    }

    private bool IsLoopbackProxyServer()
    {
        var host = ProxyServerAddress.Trim();
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
               host.StartsWith("127.", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("::1", StringComparison.OrdinalIgnoreCase);
    }

    private bool ValidateProxySettings(out string message)
    {
        if (string.IsNullOrWhiteSpace(ProxyServerAddress))
        {
            message = "آدرس IP یا دامنه سرور پراکسی را وارد کنید";
            return false;
        }

        if (ProxyPort <= 0 || ProxyPort > 65535)
        {
            message = "پورت پراکسی باید بین 1 تا 65535 باشد";
            return false;
        }

        message = "تنظیمات پراکسی آماده است";
        return true;
    }

    private bool ValidateMixedProxyPort(out string message)
    {
        var port = _mixedProxyPort;
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

    private void UpdateMixedProxyPortStatus()
    {
        ValidateMixedProxyPort(out var message);
        MixedProxyPortStatusText = message;
    }

    private void TryAutoNameProfileFromConfig(string config)
    {
        if (_selectedProfile == null || string.IsNullOrWhiteSpace(config))
            return;

        var currentName = _selectedProfile.Name?.Trim() ?? "";
        var canRename = string.IsNullOrWhiteSpace(currentName) ||
                        currentName.StartsWith("پروفایل ", StringComparison.OrdinalIgnoreCase) ||
                        currentName.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase) ||
                        currentName == "پروفایل جدید" ||
                        currentName == "پیش‌فرض" ||
                        currentName == "New Profile" ||
                        currentName == "Default";
        if (!canRename)
            return;

        var remark = ExtractConfigRemark(config);
        if (string.IsNullOrWhiteSpace(remark))
            return;

        _selectedProfile.Name = remark;
        OnPropertyChanged(nameof(SelectedProfileName));
        OnPropertyChanged(nameof(ConnectedProfileName));
        OnPropertyChanged(nameof(SelectedProfileSummaryText));
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

    private void QueueAutoConnectToLastProfile()
    {
        if (!_appSettings.AutoConnectOnStartup)
            return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            _ = AutoConnectToLastProfileAsync();
            return;
        }

        _ = dispatcher.BeginInvoke(
            new Action(() => _ = AutoConnectToLastProfileAsync()),
            DispatcherPriority.ApplicationIdle);
    }

    private ConnectionProfile? ResolveAutoConnectProfile()
    {
        if (!string.IsNullOrWhiteSpace(_appSettings.LastActiveProfileId))
        {
            var savedProfile = Profiles.FirstOrDefault(p => p.Id == _appSettings.LastActiveProfileId);
            if (savedProfile != null)
                return savedProfile;

            Logger.Warning($"[AUTO-CONNECT] Last profile id '{_appSettings.LastActiveProfileId}' was not found; falling back to most recently used profile.");
        }

        return Profiles
            .Where(p => p.LastUsedAt > DateTime.MinValue)
            .OrderByDescending(p => p.LastUsedAt)
            .FirstOrDefault();
    }

    private async Task AutoConnectToLastProfileAsync()
    {
        if (!_appSettings.AutoConnectOnStartup)
            return;

        if (_connectionState != ConnectionState.Disconnected || IsBusy)
        {
            Logger.Warning($"[AUTO-CONNECT] Skipped because connection state is {_connectionState} (busy={IsBusy}).");
            return;
        }

        var profile = ResolveAutoConnectProfile();
        if (profile == null)
        {
            StatusText = "اتصال خودکار فعال است، اما اتصال موفق قبلی پیدا نشد";
            Logger.Warning("[AUTO-CONNECT] Enabled but no previously connected profile was found.");
            return;
        }

        try
        {
            SelectedProfile = profile;
            StatusText = LocalizationService.Instance.Format("اتصال خودکار به «{0}»...", profile.Name);
            await ToggleConnectionAsync();
        }
        catch (Exception ex)
        {
            Logger.Error("[AUTO-CONNECT] Failed", ex);
            StatusText = LocalizationService.Instance.Format("خطای اتصال خودکار: {0}", ex.Message);
        }
    }

    private void LoadAppSettings()
    {
        _appSettings = _profileService.LoadAppSettings();
        LocalizationService.Instance.Initialize(_appSettings.Language);
        SyncStartupRegistryFromSettings();
        _autoConnectOnStartup = _appSettings.AutoConnectOnStartup;
        _enableInformationalNotifications = _appSettings.EnableInformationalNotifications;
        _githubInstallCount = _appSettings.GitHubAppDownloadCount;
        AppNotificationService.Configure(() => _enableInformationalNotifications);
        OnPropertyChanged(nameof(StartWithWindows));
        OnPropertyChanged(nameof(AutoConnectOnStartup));
        OnPropertyChanged(nameof(EnableInformationalNotifications));
        OnPropertyChanged(nameof(InformationalNotificationsSectionTitleText));
        OnPropertyChanged(nameof(InformationalNotificationsTitleText));
        OnPropertyChanged(nameof(InformationalNotificationsDescriptionText));
        OnPropertyChanged(nameof(HelpSettingsTabBodyText));
        OnPropertyChanged(nameof(HasGitHubInstallCount));
        OnPropertyChanged(nameof(GitHubInstallCountText));
        OnPropertyChanged(nameof(AdAudienceText));
        OnPropertyChanged(nameof(LanguageToggleText));
        OnPropertyChanged(nameof(AppIsRightToLeft));
        OnPropertyChanged(nameof(AppFlowDirection));
        OnPropertyChanged(nameof(AppTextAlignment));
        OnPropertyChanged(nameof(AppStartHorizontalAlignment));
        OnPropertyChanged(nameof(AppEndHorizontalAlignment));
    }

    private void SyncStartupRegistryFromSettings()
    {
        if (_appSettings.StartWithWindows)
        {
            if (!TryUpdateStartupRegistry(true, out var error))
                Logger.Warning($"[STARTUP] Failed to repair startup registry value: {error}");
        }
        else if (IsStartupRegistryEnabledForCurrentExecutable())
        {
            if (!TryUpdateStartupRegistry(false, out var error))
                Logger.Warning($"[STARTUP] Failed to remove disabled startup registry value: {error}");
        }

        _startWithWindows = IsStartupRegistryEnabledForCurrentExecutable();
        if (_appSettings.StartWithWindows != _startWithWindows)
        {
            _appSettings.StartWithWindows = _startWithWindows;
            _profileService.SaveAppSettings(_appSettings);
        }
    }

    private void ToggleLanguage()
    {
        LocalizationService.Instance.ToggleLanguage();
        _appSettings.Language = LocalizationService.Instance.EffectiveLanguage;
        _profileService.SaveAppSettings(_appSettings);
    }

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(LanguageToggleText));
        OnPropertyChanged(nameof(AppIsRightToLeft));
        OnPropertyChanged(nameof(AppTitleText));
        OnPropertyChanged(nameof(AppTitleAccentText));
        OnPropertyChanged(nameof(AppTitleFlowDirection));
        OnPropertyChanged(nameof(AppTitleAccentAlignment));
        OnPropertyChanged(nameof(AppFlowDirection));
        OnPropertyChanged(nameof(AppTextAlignment));
        OnPropertyChanged(nameof(AppStartHorizontalAlignment));
        OnPropertyChanged(nameof(AppEndHorizontalAlignment));
        NormalizeStatusTextStorage();
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ConfigValidationText));
        OnPropertyChanged(nameof(GameModeStatusText));
        OnPropertyChanged(nameof(InformationalNotificationsSectionTitleText));
        OnPropertyChanged(nameof(InformationalNotificationsTitleText));
        OnPropertyChanged(nameof(InformationalNotificationsDescriptionText));
        OnPropertyChanged(nameof(HelpSettingsTabBodyText));
        OnPropertyChanged(nameof(OpenVpnPrerequisiteText));
        OnPropertyChanged(nameof(OpenVpnIntroText));
        OnPropertyChanged(nameof(OpenVpnInstallGuideText));
        OnPropertyChanged(nameof(OpenVpnConnectWarningText));
        OnPropertyChanged(nameof(DownloadOpenVpnText));
        OnPropertyChanged(nameof(WireGuardPrerequisiteText));
        OnPropertyChanged(nameof(WireGuardIntroText));
        OnPropertyChanged(nameof(WireGuardInstallGuideText));
        OnPropertyChanged(nameof(DownloadWireGuardText));
        OnPropertyChanged(nameof(WireGuardFileLabelText));
        OnPropertyChanged(nameof(WireGuardConfigLabelText));
        OnPropertyChanged(nameof(ChooseFileText));
        OnPropertyChanged(nameof(RemoveFileText));
        OnPropertyChanged(nameof(DonatePayPalText));
        OnPropertyChanged(nameof(CryptoDonationText));
        RefreshHelpCryptoWalletRows();
        OnPropertyChanged(nameof(HelpProjectCardTitleText));
        OnPropertyChanged(nameof(HelpProjectMissionText));
        OnPropertyChanged(nameof(HelpSupportCardTitleText));
        OnPropertyChanged(nameof(HelpSupportCtaText));
        OnPropertyChanged(nameof(HelpGitHubButtonText));
        OnPropertyChanged(nameof(HelpGitHubButtonToolTipText));
        OnPropertyChanged(nameof(HelpDonatePayPalButtonText));
        OnPropertyChanged(nameof(HelpCopyCryptoAddressButtonText));
        OnPropertyChanged(nameof(HelpCopyCryptoAddressToolTipText));
        OnPropertyChanged(nameof(AppCreatorText));
        OnPropertyChanged(nameof(AdPlaceholderTitleText));
        OnPropertyChanged(nameof(AdRequestButtonText));
        OnPropertyChanged(nameof(AdAudienceText));
        OnPropertyChanged(nameof(TelegramChannelJoinButtonText));
        OnPropertyChanged(nameof(ConnectionTabTitleText));
        OnPropertyChanged(nameof(SplitTunnelTipLine1Text));
        OnPropertyChanged(nameof(SplitTunnelTipLine2Text));
        OnPropertyChanged(nameof(TelegramChannelToolTipText));
        OnPropertyChanged(nameof(FooterTelegramButtonText));
        OnPropertyChanged(nameof(FooterTelegramToolTipText));
        OnPropertyChanged(nameof(FooterMadeByText));
        OnPropertyChanged(nameof(LogClearButtonText));
        OnPropertyChanged(nameof(LogCopyErrorButtonText));
        OnPropertyChanged(nameof(LogCopyAllButtonText));
        OnPropertyChanged(nameof(LogClearToolTipText));
        OnPropertyChanged(nameof(LogCopyErrorToolTipText));
        OnPropertyChanged(nameof(LogCopyAllToolTipText));
        OnPropertyChanged(nameof(UpdateButtonText));
        OnPropertyChanged(nameof(UpdateStatusText));
        OnPropertyChanged(nameof(GitHubInstallCountText));
        OnPropertyChanged(nameof(ConnectButtonText));
        OnPropertyChanged(nameof(ConnectButtonToolTip));
        OnPropertyChanged(nameof(ConnectingTitleText));
        OnPropertyChanged(nameof(ConnectingHelpText));
        OnPropertyChanged(nameof(CancelConnectionButtonText));
        OnPropertyChanged(nameof(CancelConnectionToolTipText));
        OnPropertyChanged(nameof(ConnectionStagesHeaderText));
        OnPropertyChanged(nameof(ConnectionErrorHeaderText));
        OnPropertyChanged(nameof(ConnectionErrorCloseButtonText));
        RefreshConnectionProgressLocalization();
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(MixedProxyPortStatusText));
        OnPropertyChanged(nameof(AppLicenseDisplayText));
        OnPropertyChanged(nameof(ConfigCoreHint));
        OnPropertyChanged(nameof(ConfigValidationText));
        OnPropertyChanged(nameof(SaveStatusText));
        OnPropertyChanged(nameof(ConnectionIpText));
        OnPropertyChanged(nameof(ConnectionIpTextFlowDirection));
        OnPropertyChanged(nameof(ConnectionIpLabel));
        OnPropertyChanged(nameof(ConnectionIpCountryFlag));
        OnPropertyChanged(nameof(ShowConnectionIpCountryFlag));
        OnPropertyChanged(nameof(IsConnectionIpCountryFlagLoading));
        OnPropertyChanged(nameof(ConnectionIpCountryToolTip));
        if (_exitIpProxyPort > 0 && IPAddress.TryParse(_connectionIpText, out _))
            _ = RefreshExitIpCountryAsync(_exitIpProxyPort, _connectionIpText);
        OnPropertyChanged(nameof(FullRouteStatusText));
        OnPropertyChanged(nameof(RouteModeTitle));
        OnPropertyChanged(nameof(RouteModeDescription));
        OnPropertyChanged(nameof(HeaderCoreText));
        OnPropertyChanged(nameof(HeaderRouteText));
        OnPropertyChanged(nameof(HeaderLeakText));
        OnPropertyChanged(nameof(HealthLeakText));
        OnPropertyChanged(nameof(HealthDnsText));
        OnPropertyChanged(nameof(HealthIpv6Text));
        OnPropertyChanged(nameof(HealthRoutesText));
        OnPropertyChanged(nameof(ConnectedBadgeText));
        OnPropertyChanged(nameof(ConnectedCardToolTipText));
        OnPropertyChanged(nameof(ConnectedDisconnectButtonText));
        OnPropertyChanged(nameof(ConnectedProfileName));
        OnPropertyChanged(nameof(SelectedProfileSummaryText));
        OnPropertyChanged(nameof(PingButtonText));
        OnPropertyChanged(nameof(ConnectedServerPingButtonText));
        OnPropertyChanged(nameof(ServerPingButtonText));
        OnPropertyChanged(nameof(PingResult));
        OnPropertyChanged(nameof(ServerPingResult));
        OnPropertyChanged(nameof(ServerPingSectionTitleText));
        OnPropertyChanged(nameof(ServerPingToolTipText));
        OnPropertyChanged(nameof(RouteTestSectionTitleText));
        OnPropertyChanged(nameof(FullRouteCardTitleText));
        OnPropertyChanged(nameof(ManualProxyCardTitleText));
        OnPropertyChanged(nameof(ConnectionDurationLabelText));
        OnPropertyChanged(nameof(TunnelTrafficLabelText));
        OnPropertyChanged(nameof(DirectTrafficLabelText));
        OnPropertyChanged(nameof(RouteTestHelpText));
        OnPropertyChanged(nameof(ManualProxyCardToolTipText));
        OnPropertyChanged(nameof(PingTargetToolTipText));
        OnPropertyChanged(nameof(PingTargetButtonToolTipText));
        OnPropertyChanged(nameof(ConnectedServerPingToolTipText));
        OnPropertyChanged(nameof(ImportConfigsButtonText));
        OnPropertyChanged(nameof(ImportConfigsToolTipText));
        OnPropertyChanged(nameof(TestProfileLatencyButtonText));
        OnPropertyChanged(nameof(TestAllProfilesLatencyButtonText));
        OnPropertyChanged(nameof(TestProfileLatencyToolTipText));
        OnPropertyChanged(nameof(TestProfileServerPingToolTipText));
        OnPropertyChanged(nameof(SingleProfileServerPingButtonText));
        OnPropertyChanged(nameof(TestProfileServerPingButtonText));
        OnPropertyChanged(nameof(ProfileServerPingResultToolTipText));
        OnPropertyChanged(nameof(TestAllProfilesLatencyToolTipText));
        OnPropertyChanged(nameof(ProfileLatencyResultToolTipText));
        OnPropertyChanged(nameof(ProfileQuickActionsHintText));
        OnPropertyChanged(nameof(ProfileQuickActionsStatusText));
        OnPropertyChanged(nameof(HasProfileQuickActionsStatusText));
        OnPropertyChanged(nameof(AddProfileManuallyButtonText));
        OnPropertyChanged(nameof(ProfilesSectionTitleText));
        OnPropertyChanged(nameof(ProfileEditButtonText));
        OnPropertyChanged(nameof(ProfileDeleteButtonText));
        OnPropertyChanged(nameof(SingleProfileLatencyButtonText));
        OnPropertyChanged(nameof(CancelProfileLatencyTestButtonText));
        OnPropertyChanged(nameof(CanUseConnectionTabQuickActions));
        OnPropertyChanged(nameof(IsConnectionTabSelected));
        OnPropertyChanged(nameof(ProfileCountText));
        OnPropertyChanged(nameof(RoutingRulesTabToolTipText));
        OnPropertyChanged(nameof(RoutingRulesTitleText));
        OnPropertyChanged(nameof(RoutingRulesSubtitleText));
        OnPropertyChanged(nameof(ExcludeStayDirectTitleText));
        OnPropertyChanged(nameof(ExcludeStayDirectSummaryText));
        OnPropertyChanged(nameof(ExcludeRulesGuideTitleText));
        OnPropertyChanged(nameof(ExcludeRulesGuideBodyText));
        OnPropertyChanged(nameof(ExcludeInputPlaceholderText));
        OnPropertyChanged(nameof(ExcludeListHeaderText));
        OnPropertyChanged(nameof(IncludeForceTunnelTitleText));
        OnPropertyChanged(nameof(IncludeForceTunnelSummaryText));
        OnPropertyChanged(nameof(IncludeRulesGuideTitleText));
        OnPropertyChanged(nameof(IncludeRulesGuideBodyText));
        OnPropertyChanged(nameof(IncludeInputPlaceholderText));
        OnPropertyChanged(nameof(IncludeListHeaderText));
        OnPropertyChanged(nameof(AddRoutingRuleButtonText));
        OnPropertyChanged(nameof(RemoveRoutingRuleButtonText));
        OnPropertyChanged(nameof(OpenVpnConfigIntroText));
        OnPropertyChanged(nameof(OpenVpnScenarioTitleText));
        OnPropertyChanged(nameof(OpenVpnScenarioHintText));
        OnPropertyChanged(nameof(OpenVpnConfigFileLabelText));
        OnPropertyChanged(nameof(OpenVpnUsernameFieldLabelText));
        OnPropertyChanged(nameof(OpenVpnPasswordFieldLabelText));
        OnPropertyChanged(nameof(OpenVpnSecretFieldLabelText));
        OnPropertyChanged(nameof(ActiveProfileTypeText));
        OnPropertyChanged(nameof(ActiveProfileEndpointText));
        OnPropertyChanged(nameof(ProfileSaveHintText));
        RaiseProfileCardChanged();
        OnPropertyChanged(nameof(Profiles));
    }

    private static bool TryUpdateStartupRegistry(bool enable, out string? error)
    {
        error = null;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(StartupRegistryRunKey, writable: true);
            if (key == null)
            {
                error = "دسترسی به تنظیمات اجرای خودکار ویندوز ممکن نیست";
                return false;
            }

            if (enable)
            {
                var exePath = GetCurrentExecutablePath();
                if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                {
                    error = "مسیر فایل اجرایی TunnelX پیدا نشد";
                    return false;
                }

                key.SetValue(StartupRegistryValueName, $"\"{exePath}\"", RegistryValueKind.String);
            }
            else if (key.GetValue(StartupRegistryValueName) != null)
            {
                key.DeleteValue(StartupRegistryValueName);
            }

            return true;
        }
        catch (Exception ex)
        {
            Logger.Warning($"[STARTUP] Registry update failed: {ex.Message}");
            error = LocalizationService.Instance.Format("تغییر تنظیم اجرای خودکار ویندوز ناموفق بود: {0}", ex.Message);
            return false;
        }
    }

    private static bool IsStartupRegistryEnabledForCurrentExecutable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryRunKey, writable: false);
            var value = key?.GetValue(StartupRegistryValueName)?.ToString();
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var registeredPath = ExtractExecutablePath(value);
            var currentPath = GetCurrentExecutablePath();
            return !string.IsNullOrWhiteSpace(currentPath) &&
                   string.Equals(registeredPath, currentPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[STARTUP] Registry read failed: {ex.Message}");
            return false;
        }
    }

    private static string GetCurrentExecutablePath()
        => Environment.ProcessPath ??
           Process.GetCurrentProcess().MainModule?.FileName ??
           Path.Combine(AppContext.BaseDirectory, "TunnelX.exe");

    private static string ExtractExecutablePath(string registryValue)
    {
        var value = registryValue.Trim();
        if (value.Length == 0)
            return "";

        if (value[0] == '"')
        {
            var closingQuote = value.IndexOf('"', 1);
            return closingQuote > 1 ? value[1..closingQuote] : value.Trim('"');
        }

        var exeIndex = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exeIndex >= 0 ? value[..(exeIndex + 4)] : value;
    }

    #endregion

    #region INotifyPropertyChanged

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    #endregion
}
