using System.Windows.Input;
using AppTunnel.Models;
using AppTunnel.Services;

namespace AppTunnel.ViewModels;

public partial class MainViewModel
{
    private const int ConnectionTabIndex = 0;

    private bool _isImportingConfigs;
    private bool _isTestingProfileLatency;
    private bool _isTestingAllProfilesLatency;
    private bool _isTestingProfileServerPing;
    private string _profileQuickActionsStatusText = "";
    private int _selectedMainTabIndex;
    private CancellationTokenSource? _profileLatencyCts;

    public int SelectedMainTabIndex
    {
        get => _selectedMainTabIndex;
        set
        {
            if (_selectedMainTabIndex == value) return;
            _selectedMainTabIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsConnectionTabSelected));
            OnPropertyChanged(nameof(CanUseConnectionTabQuickActions));
            RefreshProfileQuickActionCommands();
        }
    }

    public bool IsConnectionTabSelected => SelectedMainTabIndex == ConnectionTabIndex;

    public bool CanUseConnectionTabQuickActions =>
        IsConnectionTabSelected && !IsConnected && !IsConnectionPending;

    public bool HasReadyProfilesForLatencyTest
    {
        get
        {
            foreach (var profile in Profiles)
            {
                if (profile.IsReady)
                    return true;
            }

            return false;
        }
    }

    public string ProfileQuickActionsStatusText
    {
        get => _profileQuickActionsStatusText;
        private set
        {
            if (_profileQuickActionsStatusText == value) return;
            _profileQuickActionsStatusText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasProfileQuickActionsStatusText));
        }
    }

    public bool HasProfileQuickActionsStatusText => !string.IsNullOrWhiteSpace(ProfileQuickActionsStatusText);

    public bool IsImportingConfigs
    {
        get => _isImportingConfigs;
        private set
        {
            if (_isImportingConfigs == value) return;
            _isImportingConfigs = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ImportConfigsButtonText));
            RefreshProfileQuickActionCommands();
        }
    }

    public bool IsTestingProfileLatency
    {
        get => _isTestingProfileLatency;
        private set
        {
            if (_isTestingProfileLatency == value) return;
            _isTestingProfileLatency = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TestProfileLatencyButtonText));
            OnPropertyChanged(nameof(SingleProfileLatencyButtonText));
            OnPropertyChanged(nameof(IsProfileLatencyTestRunning));
            RefreshProfileQuickActionCommands();
        }
    }

    public bool IsTestingProfileServerPing
    {
        get => _isTestingProfileServerPing;
        private set
        {
            if (_isTestingProfileServerPing == value) return;
            _isTestingProfileServerPing = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TestProfileServerPingButtonText));
            RefreshProfileQuickActionCommands();
        }
    }

    public bool IsTestingAllProfilesLatency
    {
        get => _isTestingAllProfilesLatency;
        private set
        {
            if (_isTestingAllProfilesLatency == value) return;
            _isTestingAllProfilesLatency = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TestAllProfilesLatencyButtonText));
            OnPropertyChanged(nameof(IsProfileLatencyTestRunning));
            RefreshProfileQuickActionCommands();
        }
    }

    public bool IsProfileLatencyTestRunning => IsTestingProfileLatency || IsTestingAllProfilesLatency;

    public string ImportConfigsButtonText => IsImportingConfigs
        ? LocalizationService.Instance.T("در حال افزودن...")
        : LocalizationService.Instance.T("چسباندن کانفیگ");

    public string TestAllProfilesLatencyButtonText => IsTestingAllProfilesLatency
        ? LocalizationService.Instance.T("در حال تست پینگ همه...")
        : LocalizationService.Instance.T("تست پینگ همه");

    public string TestProfileLatencyButtonText => IsTestingProfileLatency
        ? LocalizationService.Instance.T("در حال تست...")
        : LocalizationService.Instance.T("پینگ");

    public string SingleProfileLatencyButtonText => LocalizationService.Instance.T("پینگ");

    public string SingleProfileServerPingButtonText => LocalizationService.Instance.T("سرور");

    public string TestProfileServerPingButtonText => IsTestingProfileServerPing
        ? LocalizationService.Instance.T("در حال تست...")
        : LocalizationService.Instance.T("سرور");

    public string AddProfileManuallyButtonText => LocalizationService.Instance.T("افزودن دستی");

    public string ProfilesSectionTitleText => LocalizationService.Instance.T("کانفیگ‌ها و پروفایل‌ها");

    public string ProfileEditButtonText => LocalizationService.Instance.T("ویرایش");

    public string ProfileDeleteButtonText => LocalizationService.Instance.T("حذف");

    public string ImportConfigsToolTipText => LocalizationService.Instance.T(
        "کانفیگ V2Ray/Xray/OpenVPN/WireGuard را از کلیپ‌بورد می‌خواند و پروفایل می‌سازد (Ctrl+V در تب اتصال)");

    public string TestProfileLatencyToolTipText => LocalizationService.Instance.T(
        "پینگ اتصال: google (یا مقصد پینگ) از مسیر کامل کانفیگ — فقط sing-box share link");

    public string TestProfileServerPingToolTipText => LocalizationService.Instance.T(
        "پینگ سرور: رسیدن به IP/پورت سرور (TCP/TLS/ICMP) — برای همه کانفیگ‌ها");

    public string TestAllProfilesLatencyToolTipText => LocalizationService.Instance.T(
        "پینگ اتصال برای همه پروفایل‌های آماده — سریع‌ترین مسیر را پیدا کنید");

    public string ProfileLatencyResultToolTipText => LocalizationService.Instance.T(
        "نتیجه پینگ اتصال از مسیر کانفیگ");

    public string ProfileServerPingResultToolTipText => LocalizationService.Instance.T(
        "نتیجه پینگ سرور (بدون عبور از تونل)");

    public string CancelProfileLatencyTestButtonText => LocalizationService.Instance.T("توقف تست");

    public string ProfileQuickActionsHintText => LocalizationService.Instance.T(
        "پینگ = اتصال (sing-box share link) یا سرور (OpenVPN و بقیه). دکمه سرور فقط برای کانفیگ‌های دارای پینگ اتصال.");

    public bool TryImportConfigsFromClipboard()
    {
        if (!CanUseConnectionTabQuickActions || IsImportingConfigs)
            return false;

        ImportConfigsFromClipboard();
        return true;
    }

    private void ImportConfigsFromClipboard()
    {
        if (!CanUseConnectionTabQuickActions || IsImportingConfigs)
            return;

        try
        {
            if (!System.Windows.Clipboard.ContainsText())
            {
                ProfileQuickActionsStatusText = LocalizationService.Instance.T("کلیپ‌بورد خالی است");
                ShowImportToast(LocalizationService.Instance.T("کلیپ‌بورد خالی است"), warning: true);
                return;
            }

            ImportConfigsFromText(System.Windows.Clipboard.GetText());
        }
        catch (Exception ex)
        {
            ProfileQuickActionsStatusText = LocalizationService.Instance.Format("خواندن کلیپ‌بورد ناموفق بود: {0}", ex.Message);
            ShowImportToast(ProfileQuickActionsStatusText, warning: true);
        }
    }

    private void ImportConfigsFromText(string? rawText)
    {
        var drafts = ConfigImportService.ParseClipboard(rawText);
        if (drafts.Count == 0)
        {
            ProfileQuickActionsStatusText = LocalizationService.Instance.T("هیچ کانفیگ معتبری در کلیپ‌بورد پیدا نشد");
            ShowImportToast(ProfileQuickActionsStatusText, warning: true);
            return;
        }

        IsImportingConfigs = true;
        ProfileQuickActionsStatusText = LocalizationService.Instance.T("در حال افزودن کانفیگ‌ها...");
        try
        {
            SaveCurrentProfileState();

            var added = 0;
            var skipped = 0;
            ConnectionProfile? lastAdded = null;

            foreach (var draft in drafts)
            {
                if (!string.IsNullOrWhiteSpace(draft.SkipReason))
                {
                    skipped++;
                    continue;
                }

                var profile = ConfigImportService.CreateProfile(draft);
                if (ConfigImportService.IsDuplicateConfig(profile, Profiles))
                {
                    skipped++;
                    continue;
                }

                Profiles.Add(profile);
                lastAdded = profile;
                added++;
            }

            OnPropertyChanged(nameof(ProfileCountText));
            NotifyReadyProfilesForLatencyTestChanged();

            if (added == 0)
            {
                ProfileQuickActionsStatusText = skipped > 0
                    ? LocalizationService.Instance.T("همه کانفیگ‌ها تکراری یا نامعتبر بودند")
                    : LocalizationService.Instance.T("هیچ کانفیگ معتبری پیدا نشد");
                ShowImportToast(ProfileQuickActionsStatusText, warning: true);
                return;
            }

            if (lastAdded != null)
                SelectedProfile = lastAdded;

            SaveProfiles();

            var message = added == 1
                ? LocalizationService.Instance.T("۱ کانفیگ اضافه شد")
                : LocalizationService.Instance.Format("{0} کانفیگ اضافه شد", added);
            if (skipped > 0)
                message += LocalizationService.Instance.Format(" ({0} رد شد)", skipped);

            ProfileQuickActionsStatusText = message;
            ShowImportToast(message);
        }
        finally
        {
            IsImportingConfigs = false;
        }
    }

    private async Task TestProfileLatencyAsync(ConnectionProfile? profile)
    {
        if (profile == null || !profile.SupportsServerPing
            || !CanUseConnectionTabQuickActions || IsTestingProfileLatency || IsTestingAllProfilesLatency
            || IsTestingProfileServerPing)
            return;

        _profileLatencyCts?.Cancel();
        _profileLatencyCts = new CancellationTokenSource();
        var ct = _profileLatencyCts.Token;

        IsTestingProfileLatency = true;
        profile.IsLatencyTesting = true;
        profile.ResetLatencyResult();

        var isConnectionPing = profile.SupportsConnectionPing;
        ProfileQuickActionsStatusText = isConnectionPing
            ? LocalizationService.Instance.Format("در حال تست «{0}»...", profile.Name)
            : LocalizationService.Instance.Format("پینگ سرور «{0}»...", profile.Name);

        try
        {
            if (isConnectionPing)
            {
                await MeasureProfileLatencyAsync(profile, ct);
            }
            else
            {
                await MeasureProfileServerPingAsync(profile, ct);
                profile.LastLatencyMs = profile.LastServerLatencyMs;
                profile.LastLatencyLabel = profile.LastServerLatencyLabel;
                profile.LastLatencyError = profile.LastServerLatencyError;
                profile.ResetServerPingResult();
            }

            ProfileQuickActionsStatusText = profile.LastLatencyMs.HasValue
                ? LocalizationService.Instance.Format("«{0}»: {1} {2} ms", profile.Name, profile.LastLatencyLabel, profile.LastLatencyMs)
                : LocalizationService.Instance.Format("«{0}»: {1}", profile.Name, profile.LastLatencyError);
        }
        finally
        {
            profile.IsLatencyTesting = false;
            IsTestingProfileLatency = false;
        }
    }

    private async Task TestProfileServerPingAsync(ConnectionProfile? profile)
    {
        if (profile == null || !profile.ShowsServerPingButton
            || !CanUseConnectionTabQuickActions || IsTestingProfileServerPing || IsTestingProfileLatency)
            return;

        _profileLatencyCts?.Cancel();
        _profileLatencyCts = new CancellationTokenSource();
        var ct = _profileLatencyCts.Token;

        IsTestingProfileServerPing = true;
        profile.IsServerPingTesting = true;
        profile.ResetServerPingResult();
        ProfileQuickActionsStatusText = LocalizationService.Instance.Format("پینگ سرور «{0}»...", profile.Name);

        try
        {
            await MeasureProfileServerPingAsync(profile, ct);
            ProfileQuickActionsStatusText = profile.LastServerLatencyMs.HasValue
                ? LocalizationService.Instance.Format("«{0}» سرور: {1} {2} ms", profile.Name, profile.LastServerLatencyLabel, profile.LastServerLatencyMs)
                : LocalizationService.Instance.Format("«{0}» سرور: {1}", profile.Name, profile.LastServerLatencyError);
        }
        finally
        {
            profile.IsServerPingTesting = false;
            IsTestingProfileServerPing = false;
        }
    }

    private async Task TestAllProfilesLatencyAsync()
    {
        if (!CanUseConnectionTabQuickActions || IsTestingAllProfilesLatency)
            return;

        var candidates = Profiles.Where(p => p.IsReady).ToList();
        if (candidates.Count == 0)
        {
            ProfileQuickActionsStatusText = LocalizationService.Instance.T("پروفایل آماده‌ای برای تست وجود ندارد");
            ShowImportToast(ProfileQuickActionsStatusText, warning: true);
            return;
        }

        _profileLatencyCts?.Cancel();
        _profileLatencyCts = new CancellationTokenSource();
        var ct = _profileLatencyCts.Token;

        IsTestingAllProfilesLatency = true;
        foreach (var profile in candidates)
        {
            profile.ResetLatencyResult();
            profile.IsLatencyTesting = false;
        }

        var completed = 0;
        var success = 0;
        try
        {
            foreach (var profile in candidates)
            {
                ct.ThrowIfCancellationRequested();
                completed++;
                ProfileQuickActionsStatusText = LocalizationService.Instance.Format(
                    "در حال تست پینگ {0}/{1}: {2}",
                    completed,
                    candidates.Count,
                    profile.Name);
                profile.IsLatencyTesting = true;
                try
                {
                    await MeasureProfileLatencyAsync(profile, ct);
                    if (profile.LastLatencyMs.HasValue)
                        success++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (string.IsNullOrWhiteSpace(profile.LastLatencyError))
                        profile.LastLatencyError = ex.Message;
                }
                finally
                {
                    profile.IsLatencyTesting = false;
                }
            }

            var best = candidates
                .Where(p => p.LastLatencyMs.HasValue)
                .OrderBy(p => p.LastLatencyMs)
                .FirstOrDefault();

            ProfileQuickActionsStatusText = best != null
                ? LocalizationService.Instance.Format(
                    "تست {0}/{1} موفق — سریع‌ترین: «{2}» ({3} {4} ms)",
                    success,
                    candidates.Count,
                    best.Name,
                    best.LastLatencyLabel,
                    best.LastLatencyMs)
                : LocalizationService.Instance.Format("تست {0} پروفایل انجام شد — هیچ کانفیگ پاسخ نداد", candidates.Count);

            ShowImportToast(ProfileQuickActionsStatusText, warning: success == 0);

            if (best != null)
                SelectedProfile = best;
        }
        catch (OperationCanceledException)
        {
            ProfileQuickActionsStatusText = LocalizationService.Instance.T("تست پینگ متوقف شد");
            ShowImportToast(ProfileQuickActionsStatusText, warning: true);
        }
        finally
        {
            IsTestingAllProfilesLatency = false;
            foreach (var profile in candidates)
                profile.IsLatencyTesting = false;
        }
    }

    private void CancelProfileLatencyTest()
    {
        _profileLatencyCts?.Cancel();
    }

    private void NotifyReadyProfilesForLatencyTestChanged()
    {
        OnPropertyChanged(nameof(HasReadyProfilesForLatencyTest));
        RefreshProfileQuickActionCommands();
    }

    private static void RefreshProfileQuickActionCommands()
        => CommandManager.InvalidateRequerySuggested();

    private async Task MeasureProfileLatencyAsync(ConnectionProfile profile, CancellationToken ct)
    {
        try
        {
            if (profile.TunnelType != TunnelType.V2Ray || !ConnectionPingSupport.SupportsProfile(profile))
            {
                profile.LastLatencyError = LocalizationService.Instance.T("تست پینگ اتصال برای این کانفیگ پشتیبانی نمی‌شود");
                return;
            }

            await MeasureV2RayProfileLatencyAsync(profile, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            profile.LastLatencyError = LocalizationService.Instance.T("متوقف شد");
            throw;
        }
        catch (OperationCanceledException)
        {
            profile.LastLatencyError = LocalizationService.Instance.T("مهلت تست تمام شد");
        }
        catch (Exception ex)
        {
            profile.LastLatencyError = ex.Message;
        }
    }

    private async Task MeasureProfileServerPingAsync(ConnectionProfile profile, CancellationToken ct)
    {
        try
        {
            switch (profile.TunnelType)
            {
                case TunnelType.V2Ray:
                    await MeasureV2RayProfileServerPingAsync(profile, ct);
                    break;
                case TunnelType.OpenVpn:
                    await MeasureOpenVpnProfileServerPingAsync(profile, ct);
                    break;
                case TunnelType.WireGuard:
                    await MeasureWireGuardProfileServerPingAsync(profile, ct);
                    break;
                case TunnelType.SocksProxy:
                    await MeasureSocksProfileServerPingAsync(profile, ct);
                    break;
                case TunnelType.L2tpIpsec:
                    await MeasureL2tpProfileServerPingAsync(profile, ct);
                    break;
                default:
                    profile.LastServerLatencyError = LocalizationService.Instance.T("تست پشتیبانی نمی‌شود");
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            profile.LastServerLatencyError = LocalizationService.Instance.T("متوقف شد");
            throw;
        }
        catch (OperationCanceledException)
        {
            profile.LastServerLatencyError = LocalizationService.Instance.T("مهلت تست تمام شد");
        }
        catch (Exception ex)
        {
            profile.LastServerLatencyError = ex.Message;
        }
    }

    private async Task MeasureV2RayProfileLatencyAsync(ConnectionProfile profile, CancellationToken ct)
    {
        var config = profile.V2RayConfig.Trim();
        if (string.IsNullOrWhiteSpace(config))
        {
            profile.LastLatencyError = LocalizationService.Instance.T("کانفیگ خالی است");
            return;
        }

        if (TunnelProviderFactory.RequiresXray(config) || config.StartsWith('{'))
        {
            profile.LastLatencyError = LocalizationService.Instance.T("تست پینگ اتصال برای JSON/Xray پشتیبانی نمی‌شود");
            return;
        }

        var (probeHost, probePort) = ResolveConnectionPingProbeTarget();
        var provider = new V2RayTunnelProvider();
        try
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(TimeSpan.FromSeconds(25));
            profile.LastLatencyLabel = "";
            profile.LastLatencyMs = await provider.ProbeMixedProxyLatencyAsync(
                config, probeCts.Token, probeHost, probePort);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            profile.LastLatencyError = LocalizationService.Instance.T("مهلت تست تمام شد");
        }
        catch (Exception ex)
        {
            profile.LastLatencyError = ex.Message;
        }
    }

    /// <summary>
    /// Same target as the connected ping field (default www.google.com:443).
    /// </summary>
    private (string Host, int Port) ResolveConnectionPingProbeTarget()
    {
        var raw = PingTarget?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(raw))
            return (Socks5LatencyProbe.DefaultProbeHost, Socks5LatencyProbe.DefaultProbePort);

        var host = raw.Contains(':') ? raw.Split(':')[0] : raw;
        var port = Socks5LatencyProbe.DefaultProbePort;
        if (raw.Contains(':') && int.TryParse(raw.Split(':')[^1], out var parsedPort))
            port = parsedPort;

        return (host.Trim(), port);
    }

    private async Task MeasureV2RayProfileServerPingAsync(ConnectionProfile profile, CancellationToken ct)
    {
        var config = profile.V2RayConfig.Trim();
        if (string.IsNullOrWhiteSpace(config))
        {
            profile.LastServerLatencyError = LocalizationService.Instance.T("کانفیگ خالی است");
            return;
        }

        if (!TryExtractProxyEndpointDetails(config, out var endpoint, out var error))
        {
            profile.LastServerLatencyError = error;
            return;
        }

        using var tcpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        tcpCts.CancelAfter(TimeSpan.FromSeconds(6));
        profile.LastServerLatencyLabel = endpoint.UseTls ? "TLS" : "TCP";
        profile.LastServerLatencyMs = await MeasureEndpointLatencyAsync(endpoint, tcpCts.Token);
    }

    private async Task MeasureOpenVpnProfileServerPingAsync(ConnectionProfile profile, CancellationToken ct)
    {
        var endpoints = ExtractOpenVpnRemoteEndpoints(profile.OpenVpnConfig).ToList();
        var tcpEndpoints = endpoints
            .Where(e => !e.Protocol.Contains("udp", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (tcpEndpoints.Count == 0)
        {
            profile.LastServerLatencyError = LocalizationService.Instance.T("کانفیگ UDP است؛ پینگ TCP سرور ممکن نیست");
            return;
        }

        Exception? lastError = null;
        foreach (var endpoint in tcpEndpoints)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(6));
                profile.LastServerLatencyLabel = "TCP";
                profile.LastServerLatencyMs = await MeasureTcpConnectLatencyAsync(endpoint.Host, endpoint.Port, cts.Token);
                return;
            }
            catch (Exception ex) when (ex is OperationCanceledException or System.Net.Sockets.SocketException or TimeoutException)
            {
                lastError = ex;
            }
        }

        profile.LastServerLatencyError = lastError?.Message ?? LocalizationService.Instance.T("سرور در دسترس نیست");
    }

    private async Task MeasureWireGuardProfileServerPingAsync(ConnectionProfile profile, CancellationToken ct)
    {
        if (!WireGuardConfigParser.TryParse(profile.WireGuardConfig, out var wg, out var error))
        {
            profile.LastServerLatencyError = error;
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(4));
        profile.LastServerLatencyLabel = "ICMP";
        profile.LastServerLatencyMs = await MeasureIcmpLatencyAsync(wg.EndpointHost, cts.Token);
    }

    private async Task MeasureSocksProfileServerPingAsync(ConnectionProfile profile, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(profile.ProxyServerAddress) || profile.ProxyPort <= 0)
        {
            profile.LastServerLatencyError = LocalizationService.Instance.T("آدرس پراکسی نامعتبر است");
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(6));
        profile.LastServerLatencyLabel = "TCP";
        profile.LastServerLatencyMs = await MeasureTcpConnectLatencyAsync(profile.ProxyServerAddress.Trim(), profile.ProxyPort, cts.Token);
    }

    private async Task MeasureL2tpProfileServerPingAsync(ConnectionProfile profile, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(profile.ServerAddress))
        {
            profile.LastServerLatencyError = LocalizationService.Instance.T("آدرس سرور خالی است");
            return;
        }

        using var ping = new System.Net.NetworkInformation.Ping();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(4));
        var reply = await ping.SendPingAsync(profile.ServerAddress.Trim(), 3000).WaitAsync(cts.Token);
        if (reply.Status != System.Net.NetworkInformation.IPStatus.Success)
        {
            profile.LastServerLatencyError = reply.Status.ToString();
            return;
        }

        profile.LastServerLatencyLabel = "ICMP";
        profile.LastServerLatencyMs = reply.RoundtripTime;
    }

    private void ShowImportToast(string message, bool warning = false)
    {
        var kind = warning ? AppNotificationKind.Warning : AppNotificationKind.Success;
        if (System.Windows.Application.Current.MainWindow is MainWindow mainWindow)
            mainWindow.ShowAppToast(message, kind);
        else
            Helpers.DialogService.ShowToast(message, warning ? "⚠" : "✅");
    }
}
