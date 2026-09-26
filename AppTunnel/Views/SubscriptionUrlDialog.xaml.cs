using System.Windows;
using AppTunnel.Services;
using Application = System.Windows.Application;

namespace AppTunnel.Views;

public partial class SubscriptionUrlDialog : Window
{
    public SubscriptionUrlDialog(string? initialUrl)
    {
        InitializeComponent();
        UrlBox.Text = initialUrl ?? "";
        Loaded += (_, _) =>
        {
            LocalizationService.Instance.ApplyTo(this);
            UrlBox.Focus();
            UrlBox.SelectAll();
        };
    }

    public static string? Prompt(Window? owner, string? initialUrl)
    {
        var dialog = new SubscriptionUrlDialog(initialUrl)
        {
            Owner = owner ?? (Application.Current?.MainWindow is { IsVisible: true } main ? main : null)
        };

        return dialog.ShowDialog() == true ? dialog.UrlBox.Text.Trim() : null;
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
