using System.Windows;
using Application = System.Windows.Application;

namespace AppTunnel.Views;

/// <summary>
/// Modern styled dialog window matching the app's UI theme.
/// Replaces standard Windows MessageBox with Persian-friendly, styled dialogs.
/// </summary>
public partial class ModernDialog : Window
{
    public bool Result { get; private set; }

    public ModernDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Show a confirmation dialog (Yes/No)
    /// </summary>
    public static bool ShowConfirm(string message, string title = "تاییدیه", Window? owner = null)
    {
        var dialog = new ModernDialog
        {
            Owner = owner ?? Application.Current.MainWindow,
            TitleText = { Text = title },
            MessageText = { Text = message },
            IconText = { Text = "⚠️" },
            PrimaryButton = { Content = "بله" },
            SecondaryButton = { Content = "خیر" }
        };
        
        dialog.ShowDialog();
        return dialog.Result;
    }

    /// <summary>
    /// Show an information dialog (OK only)
    /// </summary>
    public static void ShowInfo(string message, string title = "اطلاعات", Window? owner = null)
    {
        var dialog = new ModernDialog
        {
            Owner = owner ?? Application.Current.MainWindow,
            TitleText = { Text = title },
            MessageText = { Text = message },
            IconText = { Text = "ℹ️" },
            PrimaryButton = { Content = "متوجه شدم", Visibility = Visibility.Visible },
            SecondaryButton = { Visibility = Visibility.Collapsed }
        };
        
        dialog.ShowDialog();
    }

    /// <summary>
    /// Show a success dialog (OK only)
    /// </summary>
    public static void ShowSuccess(string message, string title = "موفقیت", Window? owner = null)
    {
        var dialog = new ModernDialog
        {
            Owner = owner ?? Application.Current.MainWindow,
            TitleText = { Text = title },
            MessageText = { Text = message },
            IconText = { Text = "✅" },
            PrimaryButton = { Content = "عالی", Visibility = Visibility.Visible },
            SecondaryButton = { Visibility = Visibility.Collapsed }
        };
        
        dialog.ShowDialog();
    }

    /// <summary>
    /// Show an error dialog (OK only)
    /// </summary>
    public static void ShowError(string message, string title = "خطا", Window? owner = null)
    {
        var dialog = new ModernDialog
        {
            Owner = owner ?? Application.Current.MainWindow,
            TitleText = { Text = title },
            MessageText = { Text = message },
            IconText = { Text = "❌" },
            PrimaryButton = { Content = "متوجه شدم", Visibility = Visibility.Visible },
            SecondaryButton = { Visibility = Visibility.Collapsed }
        };
        
        dialog.ShowDialog();
    }

    /// <summary>
    /// Show a warning dialog (OK only)
    /// </summary>
    public static void ShowWarning(string message, string title = "هشدار", Window? owner = null)
    {
        var dialog = new ModernDialog
        {
            Owner = owner ?? Application.Current.MainWindow,
            TitleText = { Text = title },
            MessageText = { Text = message },
            IconText = { Text = "⚠️" },
            PrimaryButton = { Content = "متوجه شدم", Visibility = Visibility.Visible },
            SecondaryButton = { Visibility = Visibility.Collapsed }
        };
        
        dialog.ShowDialog();
    }

    private void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        Result = true;
        Close();
    }

    private void OnSecondaryClick(object sender, RoutedEventArgs e)
    {
        Result = false;
        Close();
    }
}
