using System.Windows;
using AppTunnel.Helpers;
using AppTunnel.Services;
using AppTunnel.ViewModels;
using Application = System.Windows.Application;

namespace AppTunnel.Views;

public partial class ConnectionErrorDialog : Window
{
    private static bool _isShowing;

    public ConnectionErrorDialog()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
    }

    public static void Show(MainViewModel viewModel, Window? owner = null)
    {
        if (_isShowing || !viewModel.HasConnectionError)
            return;

        var app = Application.Current;
        if (app?.Dispatcher == null)
            return;

        void ShowCore()
        {
            if (_isShowing || !viewModel.HasConnectionError)
                return;

            _isShowing = true;
            try
            {
                var dialog = new ConnectionErrorDialog
                {
                    Owner = owner ?? app.MainWindow,
                    DataContext = viewModel
                };
                dialog.ShowDialog();
            }
            finally
            {
                _isShowing = false;
            }
        }

        if (app.Dispatcher.CheckAccess())
            ShowCore();
        else
            app.Dispatcher.Invoke(ShowCore);
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => ApplyDialogLayout();

    private void OnClosed(object? sender, EventArgs e)
        => LocalizationService.Instance.LanguageChanged -= OnLanguageChanged;

    private void OnLanguageChanged(object? sender, EventArgs e) => ApplyDialogLayout();

    private void ApplyDialogLayout()
    {
        Title = LocalizationService.Instance.T("اتصال برقرار نشد");
        var flow = LocalizationService.Instance.FlowDirection;
        FlowDirection = flow;

        if (DataContext is MainViewModel)
        {
            LocalizationLayoutHelper.ApplyTo(this);
            LocalizationLayoutHelper.RefreshLayoutBindings(this);
        }
        else
        {
            LocalizationService.Instance.ApplyTo(this);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
