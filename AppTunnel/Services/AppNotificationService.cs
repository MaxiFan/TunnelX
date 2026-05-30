using System.Windows;
using AppTunnel.Models;
using AppTunnel.Views;
using Application = System.Windows.Application;

namespace AppTunnel.Services;

/// <summary>
/// Branded in-app and tray notifications (Telegram-style cards) instead of Windows balloon tips.
/// Tray toasts pass Persian source keys so text follows the active UI language after toggles.
/// </summary>
public static class AppNotificationService
{
    private static MainWindow? _mainWindow;
    private static Func<bool>? _isInformationalEnabled;

    public static void Initialize(MainWindow mainWindow) => _mainWindow = mainWindow;

    /// <summary>Called from <see cref="ViewModels.MainViewModel"/> after app settings load.</summary>
    public static void Configure(Func<bool> isInformationalEnabled) =>
        _isInformationalEnabled = isInformationalEnabled;

    public static bool IsTunnelConnected =>
        _mainWindow?.DataContext is ViewModels.MainViewModel vm &&
        vm.ConnectionState == ConnectionState.Connected;

    private static bool ShouldShow(AppNotificationChannel channel) =>
        channel == AppNotificationChannel.Promotional ||
        (_isInformationalEnabled?.Invoke() ?? true);

    public static void ShowTrayPersistent(
        string titleKey,
        string messageKey,
        AppNotificationKind kind = AppNotificationKind.Info,
        AppNotificationChannel channel = AppNotificationChannel.Informational)
    {
        if (!ShouldShow(channel)) return;

        TrayToastWindow.ShowStickyFromKeys(
            titleKey,
            messageKey,
            kind,
            () => _mainWindow?.BringToForeground());
    }

    public static void ShowPromotionalTrayPersistent(
        string titleKey,
        string messageKey,
        AppNotificationKind kind = AppNotificationKind.Info) =>
        ShowTrayPersistent(titleKey, messageKey, kind, AppNotificationChannel.Promotional);

    private static TrayToastWindow? _activeUpdateToast;

    public static void DismissUpdateToast()
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            _activeUpdateToast?.DismissSilently();
            _activeUpdateToast = null;
        });
    }

    /// <summary>Promotional tray card when a newer GitHub release is available (always shown).</summary>
    public static void ShowUpdateAvailableTrayPersistent(Action onDownload, Action onShowChangelog)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            _activeUpdateToast?.DismissSilently();
            _activeUpdateToast = null;

            const string titleKey = "نسخه جدید آماده است";
            const string messageKey = "نسخه جدید TunnelX در GitHub منتشر شده است. دانلود کنید یا تغییرات این نسخه را بخوانید.";
            const string downloadKey = "دانلود";
            const string changelogKey = "تغییرات این نسخه";

            _activeUpdateToast = TrayToastWindow.ShowPersistentDualAction(
                titleKey,
                messageKey,
                downloadKey,
                changelogKey,
                AppNotificationKind.Info,
                onDownload,
                onShowChangelog,
                () => _activeUpdateToast = null);
        });
    }

    public static void ShowTray(
        string titleKey,
        string messageKey,
        AppNotificationKind kind = AppNotificationKind.Info,
        AppNotificationChannel channel = AppNotificationChannel.Informational)
    {
        if (!ShouldShow(channel)) return;

        TrayToastWindow.ShowFromKeys(
            titleKey,
            messageKey,
            kind,
            () => _mainWindow?.BringToForeground());
    }

    /// <summary>Always shown — e.g. when the app moves to the system tray so the user knows it was not closed.</summary>
    public static void ShowTrayForced(
        string titleKey,
        string messageKey,
        AppNotificationKind kind = AppNotificationKind.Info)
    {
        TrayToastWindow.ShowFromKeys(
            titleKey,
            messageKey,
            kind,
            () => _mainWindow?.BringToForeground());
    }

    public static void ShowTrayFormat(
        string titleKey,
        string messageFormatKey,
        AppNotificationKind kind,
        AppNotificationChannel channel = AppNotificationChannel.Informational,
        params object?[] formatArgs)
    {
        if (!ShouldShow(channel)) return;

        TrayToastWindow.ShowFromKeys(
            titleKey,
            messageFormatKey,
            kind,
            () => _mainWindow?.BringToForeground(),
            formatArgs);
    }

    /// <summary>Title is localized from <paramref name="titleKey"/>; body is shown as-is (e.g. dynamic status).</summary>
    public static void ShowTrayLiteralBody(
        string? titleKey,
        string messageLiteral,
        AppNotificationKind kind,
        AppNotificationChannel channel = AppNotificationChannel.Informational)
    {
        if (!ShouldShow(channel)) return;

        TrayToastWindow.ShowFromLiteral(
            titleKey,
            messageLiteral,
            kind,
            () => _mainWindow?.BringToForeground());
    }

    /// <summary>In-app toast when the window is visible; otherwise a short tray toast. <paramref name="messageKey"/> is a Persian source key.</summary>
    public static void ShowBrief(
        string messageKey,
        AppNotificationKind kind = AppNotificationKind.Success,
        AppNotificationChannel channel = AppNotificationChannel.Informational)
    {
        if (!ShouldShow(channel)) return;

        var text = LocalizationService.Instance.T(messageKey);
        if (_mainWindow != null && _mainWindow.IsVisible && _mainWindow.WindowState != WindowState.Minimized)
            _mainWindow.ShowAppToast(text, kind);
        else
            TrayToastWindow.ShowFromKeys(null, messageKey, kind, () => _mainWindow?.BringToForeground());
    }
}
