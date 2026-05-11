using System.Windows;
using System.Windows.Input;
using AppTunnel.Services;
using AppTunnel.Helpers;
using Clipboard = System.Windows.Clipboard;

namespace AppTunnel;

public partial class LogWindow : Window
{
    public LogWindow()
    {
        InitializeComponent();
        LoadLogs();
        Logger.LogAdded += OnLogAdded;
        Closed += (_, _) => Logger.LogAdded -= OnLogAdded;
    }

    private void LoadLogs()
    {
        LogTextBox.Text = Logger.GetAllLogs();
        LogTextBox.ScrollToEnd();
    }

    private void OnLogAdded(string logEntry)
    {
        Dispatcher.BeginInvoke(() =>
        {
            LogTextBox.AppendText(logEntry + Environment.NewLine);
            LogTextBox.ScrollToEnd();
        });
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        Logger.Clear();
        LogTextBox.Clear();
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(LogTextBox.Text);
            DialogService.ShowCopied("لاگ‌ها");
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to copy logs: {ex}");
            DialogService.Error($"خطا در کپی کردن:\n{ex.Message}", "خطا");
        }
    }
}
