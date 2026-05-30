using System.Collections.ObjectModel;
using System.Windows.Threading;
using AppTunnel.Models;
using AppTunnel.Services;
using Application = System.Windows.Application;
using DispatcherTimer = System.Windows.Threading.DispatcherTimer;

namespace AppTunnel.ViewModels;

public partial class MainViewModel
{
    private readonly Dictionary<string, ConnectionProgressStep> _connectionStepIndex = new(StringComparer.Ordinal);
    private string _connectionErrorDetailKey = "";
    private string _connectionErrorDetailRaw = "";
    private DispatcherTimer? _connectionElapsedTimer;
    private DateTime _connectionProgressStartedAt;
    private string _connectionElapsedText = "00:00";

    public ObservableCollection<ConnectionProgressStep> ConnectionSteps { get; } = new();

    public bool HasConnectionError =>
        !string.IsNullOrWhiteSpace(_connectionErrorDetailKey) ||
        !string.IsNullOrWhiteSpace(_connectionErrorDetailRaw);

    public string ConnectionErrorDetail
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_connectionErrorDetailKey))
                return _connectionErrorDetailRaw;

            if (_connectionErrorDetailKey.Contains("{0}", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(_connectionErrorDetailRaw))
                return LocalizationService.Instance.Format(_connectionErrorDetailKey, _connectionErrorDetailRaw);

            if (LocalizationService.Instance.IsPersianSourceKey(_connectionErrorDetailKey))
                return LocalizationService.Instance.T(_connectionErrorDetailKey);

            return _connectionErrorDetailKey;
        }
    }

    public bool ShowConnectionErrorPanel =>
        _connectionState == ConnectionState.Error && HasConnectionError;

    public string CancelConnectionButtonText => LocalizationService.Instance.T("لغو اتصال");

    public string CancelConnectionToolTipText => LocalizationService.Instance.T("لغو تلاش اتصال");

    public string ConnectionStagesHeaderText => LocalizationService.Instance.T("مراحل اتصال");

    public string ConnectionErrorHeaderText => LocalizationService.Instance.T("اتصال برقرار نشد");

    public string ConnectionErrorCloseButtonText => LocalizationService.Instance.T("متوجه شدم");

    public string StatusBarToolTipText =>
        HasConnectionError && !string.IsNullOrWhiteSpace(ConnectionErrorDetail)
            ? ConnectionErrorDetail
            : "";

    public string ConnectionElapsedLabelText => LocalizationService.Instance.T("زمان سپری‌شده");

    public string ConnectionElapsedText => _connectionElapsedText;

    public string ConnectionElapsedDisplayText =>
        LocalizationService.Instance.Format("زمان سپری‌شده: {0}", _connectionElapsedText);

    private void SubscribeConnectionProgress()
    {
        ConnectionProgressService.Changed += OnConnectionProgressChanged;
    }

    private void OnConnectionProgressChanged(ConnectionProgressEvent evt)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            ApplyConnectionProgress(evt);
        else
            dispatcher.BeginInvoke(() => ApplyConnectionProgress(evt), DispatcherPriority.Normal);
    }

    private void ApplyConnectionProgress(ConnectionProgressEvent evt)
    {
        if (!_connectionStepIndex.TryGetValue(evt.StepId, out var step))
            return;

        switch (evt.Phase)
        {
            case ConnectionProgressPhase.Begin:
            case ConnectionProgressPhase.Active:
                MarkStepActive(evt.StepId);
                if (!string.IsNullOrWhiteSpace(evt.Detail))
                    SetStepDetail(step, evt.Detail, evt.DetailFormatArg);
                break;
            case ConnectionProgressPhase.Complete:
                MarkStepCompleted(evt.StepId);
                if (!string.IsNullOrWhiteSpace(evt.Detail))
                    SetStepDetail(step, evt.Detail, evt.DetailFormatArg);
                break;
            case ConnectionProgressPhase.Fail:
                MarkStepFailed(evt.StepId, evt.Detail, evt.MessageKey, evt.DetailFormatArg);
                break;
            case ConnectionProgressPhase.Skip:
                step.Phase = ConnectionStepPhase.Skipped;
                break;
        }

        if (!string.IsNullOrWhiteSpace(evt.MessageKey))
        {
            StatusText = evt.Phase == ConnectionProgressPhase.Fail
                ? ResolveShortConnectionStatus(CurrentTunnelType, evt.MessageKey)
                : evt.MessageKey;
        }

        OnPropertyChanged(nameof(ConnectionSteps));
    }

    private static void SetStepDetail(ConnectionProgressStep step, string detailKey, string? formatArg)
    {
        if (IsLiveStepDetail(detailKey))
        {
            step.DetailKey = null;
            step.DetailFormatArg = null;
            step.Detail = detailKey;
            return;
        }

        step.DetailKey = detailKey;
        step.DetailFormatArg = formatArg;
        step.Detail = FormatStepDetail(detailKey, formatArg);
    }

    private static bool IsLiveStepDetail(string detailKey)
    {
        if (LocalizationService.Instance.IsKnownMessageKey(detailKey))
            return false;

        return detailKey.Contains('\n') || detailKey.Contains('\r');
    }

    private static string FormatStepDetail(string detailKey, string? formatArg)
    {
        if (string.IsNullOrWhiteSpace(formatArg))
            return LocalizationService.Instance.T(detailKey);

        return LocalizationService.Instance.Format(detailKey, formatArg);
    }

    private void BeginConnectionProgress(TunnelType tunnelType)
    {
        StartConnectionElapsedTimer();

        _connectionErrorDetailKey = "";
        _connectionErrorDetailRaw = "";
        OnPropertyChanged(nameof(HasConnectionError));
        OnPropertyChanged(nameof(ConnectionErrorDetail));
        OnPropertyChanged(nameof(StatusBarToolTipText));
        OnPropertyChanged(nameof(ShowConnectionErrorPanel));

        ConnectionSteps.Clear();
        _connectionStepIndex.Clear();

        foreach (var spec in GetStepSpecs(tunnelType))
        {
            var step = new ConnectionProgressStep
            {
                StepId = spec.Id,
                TitleKey = spec.TitleKey,
                Title = LocalizationService.Instance.T(spec.TitleKey),
                Phase = ConnectionStepPhase.Pending
            };
            _connectionStepIndex[spec.Id] = step;
            ConnectionSteps.Add(step);
        }

        if (ConnectionSteps.Count > 0)
            ConnectionSteps[0].Phase = ConnectionStepPhase.Active;

        OnPropertyChanged(nameof(ConnectionSteps));
    }

    private void MarkStepActive(string stepId)
    {
        foreach (var step in ConnectionSteps)
        {
            if (step.StepId == stepId)
            {
                step.Phase = ConnectionStepPhase.Active;
                continue;
            }

            if (step.Phase == ConnectionStepPhase.Active)
                step.Phase = ConnectionStepPhase.Completed;
        }
    }

    private void MarkStepCompleted(string stepId)
    {
        if (_connectionStepIndex.TryGetValue(stepId, out var step))
            step.Phase = ConnectionStepPhase.Completed;

        var idx = ConnectionSteps.ToList().FindIndex(s => s.StepId == stepId);
        if (idx >= 0 && idx + 1 < ConnectionSteps.Count)
        {
            var next = ConnectionSteps[idx + 1];
            if (next.Phase == ConnectionStepPhase.Pending)
                next.Phase = ConnectionStepPhase.Active;
        }
    }

    private void MarkStepFailed(string stepId, string? detailKey, string messageKey, string? detailFormatArg)
    {
        if (_connectionStepIndex.TryGetValue(stepId, out var step))
        {
            step.Phase = ConnectionStepPhase.Failed;
            if (!string.IsNullOrWhiteSpace(detailKey))
                SetStepDetail(step, detailKey, detailFormatArg);
            else
            {
                var (resolvedKey, resolvedArg) = LocalizationService.Instance.ResolveStatusStorage(messageKey);
                SetStepDetail(step, resolvedKey, resolvedArg);
            }
        }

        foreach (var pending in ConnectionSteps.Where(s => s.Phase is ConnectionStepPhase.Pending or ConnectionStepPhase.Active && s.StepId != stepId))
            pending.Phase = ConnectionStepPhase.Skipped;
    }

    private void CompleteConnectionProgress(bool success, string? errorDetailKey = null, string? errorDetailRaw = null)
    {
        StopConnectionElapsedTimer();

        if (success)
        {
            _connectionErrorDetailKey = "";
            _connectionErrorDetailRaw = "";
            foreach (var step in ConnectionSteps)
                step.Phase = ConnectionStepPhase.Completed;
            return;
        }

        if (!string.IsNullOrWhiteSpace(errorDetailKey))
        {
            _connectionErrorDetailKey = errorDetailKey;
            _connectionErrorDetailRaw = errorDetailRaw ?? "";
        }
        else if (!string.IsNullOrWhiteSpace(errorDetailRaw))
        {
            var (key, formatArg) = LocalizationService.Instance.ResolveStatusStorage(errorDetailRaw);
            _connectionErrorDetailKey = key;
            _connectionErrorDetailRaw = formatArg ?? "";
        }
        else
        {
            _connectionErrorDetailKey = "";
            _connectionErrorDetailRaw = "";
        }

        OnPropertyChanged(nameof(HasConnectionError));
        OnPropertyChanged(nameof(ConnectionErrorDetail));
        OnPropertyChanged(nameof(StatusBarToolTipText));
        OnPropertyChanged(nameof(ShowConnectionErrorPanel));
    }

    /// <summary>
    /// Body text for tray notification when <see cref="ConnectionState"/> is <see cref="ConnectionState.Error"/>:
    /// prefers staged connection error detail, then status line, plus a short user guidance paragraph.
    /// </summary>
    public string BuildConnectionFailureTrayMessage()
    {
        var loc = LocalizationService.Instance;
        const string guidanceKey = "راهنمای پس از خطای اتصال";

        var reason = HasConnectionError && !string.IsNullOrWhiteSpace(ConnectionErrorDetail)
            ? ConnectionErrorDetail.Trim()
            : StatusText?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(reason) ||
            reason == loc.T("خطا") ||
            reason.Equals("Error", StringComparison.OrdinalIgnoreCase))
            return $"{loc.T("جزئیات خطا را در پنجره برنامه یا لاگ‌ها بررسی کنید.")}{Environment.NewLine}{Environment.NewLine}{loc.T(guidanceKey)}";

        return $"{reason}{Environment.NewLine}{Environment.NewLine}{loc.T(guidanceKey)}";
    }

    private static IEnumerable<(string Id, string TitleKey)> GetStepSpecs(TunnelType tunnelType) => tunnelType switch
    {
        TunnelType.V2Ray or TunnelType.SocksProxy =>
        [
            ("validate", "بررسی کانفیگ و پورت‌ها"),
            ("cleanup", "پاکسازی processهای قبلی TunnelX"),
            ("tunnel_engine", "راه‌اندازی هسته تونل (Xray/V2Ray)"),
            ("tun_bridge", "راه‌اندازی پل TUN (sing-box)"),
            ("tun_interface", "شناسایی آداپتر مجازی"),
            ("split_router", "راه‌اندازی اسپلیت‌تانلینگ"),
            ("verify", "بررسی سلامت اتصال")
        ],
        TunnelType.OpenVpn =>
        [
            ("validate", "بررسی کانفیگ و پیش‌نیاز OpenVPN"),
            ("cleanup", "پاکسازی processهای قبلی TunnelX"),
            ("tunnel_engine", "راه‌اندازی OpenVPN"),
            ("tun_interface", "انتظار برای آداپتر VPN"),
            ("split_router", "راه‌اندازی اسپلیت‌تانلینگ"),
            ("verify", "بررسی سلامت اتصال")
        ],
        TunnelType.WireGuard =>
        [
            ("validate", "بررسی کانفیگ WireGuard"),
            ("cleanup", "پاکسازی سرویس WireGuard قبلی"),
            ("tunnel_engine", "راه‌اندازی سرویس WireGuard"),
            ("tun_interface", "انتظار برای آداپتر WireGuard"),
            ("split_router", "راه‌اندازی اسپلیت‌تانلینگ"),
            ("verify", "بررسی سلامت اتصال")
        ],
        _ =>
        [
            ("validate", "بررسی تنظیمات اتصال"),
            ("tunnel_engine", "برقراری اتصال L2TP/IPsec"),
            ("split_router", "راه‌اندازی اسپلیت‌تانلینگ"),
            ("verify", "بررسی سلامت اتصال")
        ]
    };

    private void StartConnectionElapsedTimer()
    {
        _connectionProgressStartedAt = DateTime.UtcNow;
        _connectionElapsedText = "00:00";
        OnPropertyChanged(nameof(ConnectionElapsedText));
        OnPropertyChanged(nameof(ConnectionElapsedDisplayText));

        _connectionElapsedTimer?.Stop();
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
            return;

        _connectionElapsedTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            var elapsed = DateTime.UtcNow - _connectionProgressStartedAt;
            if (elapsed < TimeSpan.Zero)
                elapsed = TimeSpan.Zero;

            var text = elapsed.TotalHours >= 1
                ? $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
                : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";

            if (text == _connectionElapsedText)
                return;

            _connectionElapsedText = text;
            OnPropertyChanged(nameof(ConnectionElapsedText));
            OnPropertyChanged(nameof(ConnectionElapsedDisplayText));
        }, dispatcher);
        _connectionElapsedTimer.Start();
    }

    private void StopConnectionElapsedTimer()
    {
        _connectionElapsedTimer?.Stop();
        _connectionElapsedTimer = null;
    }

    private void RefreshConnectionProgressLocalization()
    {
        NormalizeConnectionErrorStorage();

        OnPropertyChanged(nameof(ConnectionElapsedLabelText));
        OnPropertyChanged(nameof(ConnectionElapsedDisplayText));

        foreach (var step in ConnectionSteps)
        {
            step.Title = LocalizationService.Instance.T(step.TitleKey);
            if (!string.IsNullOrWhiteSpace(step.DetailKey))
                step.Detail = FormatStepDetail(step.DetailKey, step.DetailFormatArg);
        }

        OnPropertyChanged(nameof(ConnectionSteps));
        OnPropertyChanged(nameof(ConnectionErrorDetail));
        OnPropertyChanged(nameof(CancelConnectionButtonText));
        OnPropertyChanged(nameof(CancelConnectionToolTipText));
        OnPropertyChanged(nameof(ConnectionStagesHeaderText));
        OnPropertyChanged(nameof(ConnectionErrorHeaderText));
        OnPropertyChanged(nameof(ConnectionErrorCloseButtonText));
        OnPropertyChanged(nameof(StatusBarToolTipText));
    }

    private void QueueConnectionErrorDialog()
    {
        if (!HasConnectionError || _connectionState != ConnectionState.Error)
            return;

        var app = Application.Current;
        if (app?.Dispatcher == null)
            return;

        app.Dispatcher.BeginInvoke(() =>
        {
            if (!HasConnectionError || _connectionState != ConnectionState.Error)
                return;

            Views.ConnectionErrorDialog.Show(this, app.MainWindow);
        }, DispatcherPriority.Normal);
    }

    private void NormalizeConnectionErrorStorage()
    {
        if (!HasConnectionError)
            return;

        var snapshot = ConnectionErrorDetail;
        if (string.IsNullOrWhiteSpace(snapshot))
            return;

        var (key, formatArg) = LocalizationService.Instance.ResolveStatusStorage(snapshot);
        _connectionErrorDetailKey = key;
        _connectionErrorDetailRaw = formatArg ?? "";
    }
}
