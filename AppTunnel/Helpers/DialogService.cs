using System.Windows;
using AppTunnel.Views;
using Application = System.Windows.Application;

namespace AppTunnel.Helpers;

/// <summary>
/// Service for showing modern styled dialogs matching the app's UI theme.
/// Replaces Windows MessageBox with Persian-friendly, styled dialogs.
/// </summary>
public static class DialogService
{
    /// <summary>
    /// Show a confirmation dialog and return user's choice
    /// </summary>
    /// <param name="message">پیام فارسی</param>
    /// <param name="title">عنوان (پیش‌فرض: "تاییدیه")</param>
    /// <param name="owner">پنجره والد (پیش‌فرض: MainWindow)</param>
    /// <returns>true اگر کاربر "بله" را انتخاب کند</returns>
    public static bool Confirm(string message, string title = "تاییدیه", Window? owner = null)
    {
        return ModernDialog.ShowConfirm(message, title, owner);
    }

    /// <summary>
    /// Show an information message
    /// </summary>
    public static void Info(string message, string title = "اطلاعات", Window? owner = null)
    {
        ModernDialog.ShowInfo(message, title, owner);
    }

    /// <summary>
    /// Show a success message
    /// </summary>
    public static void Success(string message, string title = "موفقیت", Window? owner = null)
    {
        ModernDialog.ShowSuccess(message, title, owner);
    }

    /// <summary>
    /// Show an error message
    /// </summary>
    public static void Error(string message, string title = "خطا", Window? owner = null)
    {
        ModernDialog.ShowError(message, title, owner);
    }

    /// <summary>
    /// Show a warning message
    /// </summary>
    public static void Warning(string message, string title = "هشدار", Window? owner = null)
    {
        ModernDialog.ShowWarning(message, title, owner);
    }

    /// <summary>
    /// Show a clipboard copy notification (toast, no dialog)
    /// </summary>
    public static void ShowCopied(string itemName = "متن")
    {
        ShowToast($"{itemName} کپی شد", "✅");
    }

    /// <summary>
    /// Show a small auto-dismiss toast notification for 3 seconds
    /// </summary>
    public static void ShowToast(string message, string icon = "✅")
    {
        if (Application.Current.MainWindow is MainWindow mainWindow)
        {
            mainWindow.ShowToast(message, icon);
        }
    }
}
