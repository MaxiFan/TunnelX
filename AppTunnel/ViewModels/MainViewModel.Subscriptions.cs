using System.Collections.ObjectModel;
using AppTunnel.Models;
using AppTunnel.Services;
using AppTunnel.Views;

namespace AppTunnel.ViewModels;

public partial class MainViewModel
{
    private readonly SubscriptionClient _subscriptionClient = new();
    private bool _isSubscriptionBusy;

    public ObservableCollection<SubscriptionLink> Subscriptions { get; } = new();

    public bool HasSubscriptions => Subscriptions.Count > 0;

    public bool IsSubscriptionBusy
    {
        get => _isSubscriptionBusy;
        private set
        {
            if (_isSubscriptionBusy == value)
                return;
            _isSubscriptionBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AddSubscriptionButtonText));
            RefreshProfileQuickActionCommands();
        }
    }

    public string AddSubscriptionButtonText => IsSubscriptionBusy
        ? LocalizationService.Instance.T("در حال دریافت...")
        : LocalizationService.Instance.T("افزودن اشتراک");

    public string RefreshSubscriptionButtonText => LocalizationService.Instance.T("به‌روزرسانی");

    public string DeleteSubscriptionButtonText => LocalizationService.Instance.T("حذف");

    public string AddSubscriptionToolTipText => LocalizationService.Instance.T(
        "لینک اشتراک (sub) را دریافت می‌کند و کانفیگ‌های آن را به پروفایل تبدیل می‌کند");

    public string RefreshSubscriptionToolTipText => LocalizationService.Instance.T("به‌روزرسانی این اشتراک");

    public string DeleteSubscriptionToolTipText => LocalizationService.Instance.T("حذف این اشتراک و کانفیگ‌های آن");

    private void LoadSubscriptions()
    {
        Subscriptions.Clear();
        foreach (var subscription in _profileService.LoadSubscriptions())
            Subscriptions.Add(subscription);
        OnPropertyChanged(nameof(HasSubscriptions));
    }

    private void SaveSubscriptions() => _profileService.SaveSubscriptions(Subscriptions);

    private bool CanMutateSubscriptions() =>
        CanUseConnectionTabQuickActions && !IsImportingConfigs && !IsSubscriptionBusy;

    private void PromptAddSubscription()
    {
        if (!CanMutateSubscriptions())
            return;

        var url = SubscriptionUrlDialog.Prompt(
            System.Windows.Application.Current?.MainWindow,
            TryReadClipboardUrl());
        if (string.IsNullOrWhiteSpace(url))
            return;

        _ = ImportSubscriptionUrlAsync(url);
    }

    private void RefreshSubscription(object? parameter)
    {
        if (parameter is not SubscriptionLink subscription || !CanMutateSubscriptions())
            return;

        _ = ImportSubscriptionUrlAsync(subscription.Url, subscription);
    }

    private async Task ImportSubscriptionUrlAsync(string url, SubscriptionLink? existing = null)
    {
        if (!CanMutateSubscriptions())
        {
            if (IsSubscriptionBusy)
            {
                ProfileQuickActionsStatusText = LocalizationService.Instance.T("دریافت اشتراک در حال انجام است");
                ShowImportToast(ProfileQuickActionsStatusText, warning: true);
            }
            return;
        }

        if (!SubscriptionUrl.TryNormalize(url, out var normalized, out var error))
        {
            ProfileQuickActionsStatusText = error;
            ShowImportToast(error, warning: true);
            return;
        }

        existing ??= Subscriptions.FirstOrDefault(s =>
            string.Equals(s.Url, normalized, StringComparison.OrdinalIgnoreCase));

        var subscription = existing ?? new SubscriptionLink
        {
            Name = SubscriptionUrl.SuggestName(normalized),
            Url = normalized
        };

        if (existing == null)
        {
            Subscriptions.Add(subscription);
            OnPropertyChanged(nameof(HasSubscriptions));
        }

        IsSubscriptionBusy = true;
        subscription.IsRefreshing = true;
        subscription.LastError = "";
        SaveSubscriptions();
        ProfileQuickActionsStatusText = LocalizationService.Instance.T("در حال دریافت اشتراک...");

        try
        {
            var fetched = await _subscriptionClient.FetchAsync(normalized);
            if (!fetched.Success)
            {
                subscription.LastError = fetched.ErrorMessage;
                ProfileQuickActionsStatusText = fetched.ErrorMessage;
                ShowImportToast(fetched.ErrorMessage, warning: true);
                return;
            }

            if (!string.IsNullOrWhiteSpace(fetched.ProfileTitle))
                subscription.Name = CleanSubscriptionTitle(fetched.ProfileTitle);

            var problem = ConfigImportService.DescribeSubscriptionProblem(fetched.Body);
            var drafts = ConfigImportService.ParseClipboard(fetched.Body);
            var usable = drafts.Count(d => string.IsNullOrWhiteSpace(d.SkipReason));
            if (problem != null || usable == 0)
            {
                subscription.LastError = problem ?? LocalizationService.Instance.T("هیچ کانفیگ معتبری در اشتراک پیدا نشد");
                ProfileQuickActionsStatusText = subscription.LastError;
                ShowImportToast(subscription.LastError, warning: true);
                return;
            }

            SaveCurrentProfileState();
            var selectedId = SelectedProfile?.Id;
            var result = SubscriptionSync.Apply(subscription.Id, drafts, Profiles);
            if (!result.Applied)
            {
                subscription.LastError = LocalizationService.Instance.T("هیچ کانفیگ معتبری در اشتراک پیدا نشد");
                ProfileQuickActionsStatusText = subscription.LastError;
                ShowImportToast(subscription.LastError, warning: true);
                return;
            }

            if (Profiles.Count == 0)
                Profiles.Add(new ConnectionProfile { Name = LocalizationService.Instance.T("پیش‌فرض") });

            subscription.LastError = "";
            subscription.LastFetchedAt = DateTime.Now;
            subscription.NodeCount = Profiles.Count(p => p.SubscriptionId == subscription.Id);
            subscription.ApplyTraffic(fetched.Traffic);

            if (SelectedProfile == null || !Profiles.Contains(SelectedProfile))
            {
                SelectedProfile = Profiles.FirstOrDefault(p => p.Id == selectedId)
                                  ?? result.FocusProfile
                                  ?? Profiles.FirstOrDefault();
            }
            else if (SelectedProfile.SubscriptionId == subscription.Id)
            {
                LoadProfileIntoUi(SelectedProfile);
            }

            OnPropertyChanged(nameof(ProfileCountText));
            NotifyReadyProfilesForLatencyTestChanged();
            SaveProfiles();

            var message = LocalizationService.Instance.Format(
                "اشتراک به‌روز شد: {0} جدید، {1} به‌روز، {2} حذف",
                result.Added,
                result.Updated,
                result.Removed);
            if (result.Skipped > 0)
                message += LocalizationService.Instance.Format(" ({0} رد شد)", result.Skipped);

            ProfileQuickActionsStatusText = message;
            ShowImportToast(message);
        }
        catch (Exception ex)
        {
            subscription.LastError = LocalizationService.Instance.Format("دریافت اشتراک ناموفق بود: {0}", ex.Message);
            ProfileQuickActionsStatusText = subscription.LastError;
            ShowImportToast(subscription.LastError, warning: true);
            Logger.Warning($"[SUB] Import failed: {ex.Message}");
        }
        finally
        {
            subscription.IsRefreshing = false;
            IsSubscriptionBusy = false;
            SaveSubscriptions();
        }
    }

    private void DeleteSubscription(object? parameter)
    {
        if (parameter is not SubscriptionLink subscription || !CanMutateSubscriptions())
            return;

        var confirm = Helpers.DialogService.Confirm(
            LocalizationService.Instance.Format(
                "اشتراک «{0}» حذف شود؟ کانفیگ‌های دریافت‌شده از این لینک هم حذف می‌شوند.",
                subscription.Name),
            "حذف اشتراک");
        if (!confirm)
            return;

        SaveCurrentProfileState();
        var owned = Profiles.Where(p => p.SubscriptionId == subscription.Id).ToList();
        foreach (var profile in owned)
            Profiles.Remove(profile);

        Subscriptions.Remove(subscription);
        OnPropertyChanged(nameof(HasSubscriptions));

        if (Profiles.Count == 0)
            Profiles.Add(new ConnectionProfile { Name = LocalizationService.Instance.T("پیش‌فرض") });

        if (SelectedProfile == null || !Profiles.Contains(SelectedProfile))
            SelectedProfile = Profiles[0];

        OnPropertyChanged(nameof(ProfileCountText));
        NotifyReadyProfilesForLatencyTestChanged();
        SaveProfiles();
        SaveSubscriptions();

        var message = LocalizationService.Instance.T("اشتراک حذف شد");
        ProfileQuickActionsStatusText = message;
        ShowImportToast(message);
    }

    private static string? TryReadClipboardUrl()
    {
        try
        {
            if (System.Windows.Clipboard.ContainsText() &&
                SubscriptionUrl.TryNormalize(System.Windows.Clipboard.GetText(), out var url, out _))
                return url;
        }
        catch
        {
            // Clipboard can be locked by another process.
        }

        return null;
    }

    private static string CleanSubscriptionTitle(string title)
    {
        title = title.Trim().ReplaceLineEndings(" ");
        if (title.Length > 48)
            title = title[..48];
        return string.IsNullOrWhiteSpace(title)
            ? LocalizationService.Instance.T("اشتراک")
            : title;
    }
}
